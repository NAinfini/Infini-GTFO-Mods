using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using GameData;
using LevelGeneration;
using Localization;

namespace ForgeMap.Tests.MapNativeAdapter;

// The native execution layer for the door and terminal actions, compiled against the doubles the same way the
// production native sources are. Every native entry point these cases observe is a managed double: what they
// prove is that the action asks the member it says it asks, with the arguments it resolved, and that a request
// no native entry can carry is refused by name instead of being run in its plain form. No GTFO assembly is
// loaded and no instance here is game-verified.
public sealed class DoorTerminalActionTests
{
    // The address table is keyed by the world epoch, which is null without a session: a case that built a
    // different level would otherwise read the previous case's zones.
    public DoorTerminalActionTests() => Reset();

    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    private static void Reset()
    {
        LG_LevelBuilder.Current = null;
        ZoneIndex.Reset();
        LG_ComputerTerminalManager.Reset();
    }

    private static SyntheticLevel Level()
        => new(new RuntimeKernel(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457")));

    private static MapObjectReference AddressOf(LG_SecurityDoor door)
        => DoorObservation.Decide(door).Address ?? throw new Exception("The door carried no address.");

    private static MapObjectReference AddressOf(LG_ComputerTerminal terminal)
        => TerminalObservation.Address(terminal) ?? throw new Exception("The terminal carried no address.");

    private static LG_ComputerTerminal TerminalWith(SyntheticLevel world, LG_Zone zone, uint syncId, params TERM_Command[] commands)
    {
        var terminal = world.Terminal(zone, TERM_State.PlayerInteracting);
        terminal.SyncID = syncId;
        terminal.m_command = new LG_ComputerTerminalCommandInterpreter();
        foreach (var command in commands) terminal.m_command.Commands[command.ToString()] = command;
        return terminal;
    }

    [Fact]
    public void open_respect_policy_asks_the_doors_own_interaction_entry()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Closed);
        var outcome = DoorActions.Open(door, AddressOf(door), DoorBypassPolicy.Respect);
        Require(outcome.Commit == MapActionCommit.Issued && outcome.Code == DoorActions.Opened,
            "A closed, unlocked door was not asked to open: " + outcome.Code);
        Require(door.OpenCloseCalls == 1 && door.LastOnlyUnlock == false && door.ForceOpenCalls == 0,
            "The plain interaction entry was not the one entry asked, or its onlyUnlock flag was not the plain one.");
    }

    [Fact]
    public void open_on_a_door_that_is_already_open_writes_nothing()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var open = world.Entrance(zone, eDoorStatus.Open);
        var opening = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_2), eDoorStatus.Opening);
        foreach (var door in new[] { open, opening })
        {
            var outcome = DoorActions.Open(door, AddressOf(door), DoorBypassPolicy.Respect);
            Require(outcome.Commit == MapActionCommit.AlreadyInState && outcome.Code == DoorActions.AlreadyOpen,
                "An open door was not left alone: " + outcome.Code);
            Require(door.OpenCloseCalls == 0 && door.ForceOpenCalls == 0,
                "The interaction entry toggles, so asking an open door to open would have closed it.");
        }
    }

    [Fact]
    public void open_respect_policy_refuses_a_lock_the_door_still_reads()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var locked = world.Entrance(zone, eDoorStatus.Closed_LockedWithKeyItem);
        var noKey = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_2), eDoorStatus.Closed);
        noKey.m_locks!.TryCast<LG_SecurityDoor_Locks>()!.m_lockedWithNoKey = true;
        foreach (var door in new[] { locked, noKey })
        {
            var outcome = DoorActions.Open(door, AddressOf(door), DoorBypassPolicy.Respect);
            Require(outcome.Commit == MapActionCommit.Refused && outcome.Code == DoorActions.Locked,
                "A door whose own lock still holds was asked to open: " + outcome.Code);
            Require(door.OpenCloseCalls == 0, "A locked door was asked through the plain interaction entry.");
        }
    }

    [Fact]
    public void open_respect_policy_refuses_a_door_that_is_not_interactable()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Unlocked);
        door.InteractionAllowed = false;
        var outcome = DoorActions.Open(door, AddressOf(door), DoorBypassPolicy.Respect);
        Require(outcome.Commit == MapActionCommit.Refused && outcome.Code == DoorActions.InteractionNotAllowed,
            "The door's own interaction gate did not refuse the request: " + outcome.Code);
        Require(door.OpenCloseCalls == 0, "A door that reports itself un-interactable was asked anyway.");
    }

    [Fact]
    public void open_force_policy_forces_a_locked_door_and_no_other_entry_is_asked()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1),
            eDoorStatus.Closed_LockedWithChainedPuzzle_Alarm);
        var outcome = DoorActions.Open(door, AddressOf(door), DoorBypassPolicy.Force);
        Require(outcome.Commit == MapActionCommit.Issued && outcome.Code == DoorActions.Opened,
            "The force policy did not force a locked door open: " + outcome.Code);
        Require(door.ForceOpenCalls == 1 && door.OpenCloseCalls == 0,
            "The force-open entry was not the entry asked, or the plain interaction entry was asked as well.");
    }

    [Fact]
    public void open_refuses_a_door_the_doors_own_status_has_removed()
    {
        var world = Level();
        var destroyed = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Destroyed);
        var broken = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_2),
            eDoorStatus.Closed_BrokenCantOpen);
        var removed = DoorActions.Open(destroyed, AddressOf(destroyed), DoorBypassPolicy.Force);
        Require(removed.Commit == MapActionCommit.Refused && removed.Code == DoorActions.Destroyed,
            "A destroyed door was asked to open: " + removed.Code);
        var held = DoorActions.Open(broken, AddressOf(broken), DoorBypassPolicy.Respect);
        Require(held.Commit == MapActionCommit.Refused && held.Code == DoorActions.ClosedBroken,
            "A door the level built as unable to open was asked to open: " + held.Code);
        Require(destroyed.ForceOpenCalls == 0 && broken.OpenCloseCalls == 0, "A removed or broken door was written to.");
    }

    [Fact]
    public void open_refuses_a_door_that_no_longer_reads_as_the_address_it_was_resolved_through()
    {
        var world = Level();
        var first = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Closed);
        var second = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_2), eDoorStatus.Closed);
        var stale = DoorActions.Open(second, AddressOf(first), DoorBypassPolicy.Respect);
        Require(stale.Commit == MapActionCommit.Refused && stale.Code == DoorActions.Stale,
            "A door that is not the addressed one was written to: " + stale.Code);
        Require(second.OpenCloseCalls == 0, "The wrong door was asked to open.");
    }

    [Fact]
    public void open_refuses_an_instance_that_does_not_read()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Closed);
        var address = AddressOf(door);
        var absent = DoorActions.Open(null!, address, DoorBypassPolicy.Respect);
        Require(absent.Commit == MapActionCommit.Refused && absent.Code == DoorActions.Unavailable,
            "A missing door was not refused as unreadable: " + absent.Code);
        door.Destroyed = true;
        var collected = DoorActions.Open(door, address, DoorBypassPolicy.Respect);
        Require(collected.Commit == MapActionCommit.Refused && collected.Code == DoorActions.Unavailable,
            "A collected door was not refused as unreadable: " + collected.Code);
        Require(door.OpenCloseCalls == 0 && door.ForceOpenCalls == 0, "A collected door was written to.");
    }

    [Fact]
    public void close_leaves_a_door_that_is_not_open_alone_and_closes_one_that_is()
    {
        var world = Level();
        // Both doors are built before the first read: the level's table is rebuilt per world epoch, so a zone
        // added after a read belongs to no table this case would still be reading.
        var shut = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Closed);
        var open = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_2), eDoorStatus.Open);
        var shutOutcome = DoorActions.Close(shut, AddressOf(shut), DoorOccupancyPolicy.Block, DoorForcePolicy.Normal);
        Require(shutOutcome.Commit == MapActionCommit.AlreadyInState && shutOutcome.Code == DoorActions.AlreadyClosed,
            "A closed door was not left alone: " + shutOutcome.Code);
        Require(shut.OpenCloseCalls == 0, "The interaction entry toggles, so asking a closed door to close would have opened it.");
        var openOutcome = DoorActions.Close(open, AddressOf(open), DoorOccupancyPolicy.Block, DoorForcePolicy.Normal);
        Require(openOutcome.Commit == MapActionCommit.Issued && openOutcome.Code == DoorActions.Closed,
            "An open door was not asked to close: " + openOutcome.Code);
        Require(open.OpenCloseCalls == 1 && open.LastOnlyUnlock == false,
            "The plain interaction entry was not the one entry asked to close.");
    }

    [Fact]
    public void close_refuses_the_two_policies_no_native_entry_carries()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var door = world.Entrance(zone, eDoorStatus.Open);
        var crush = DoorActions.Close(door, AddressOf(door), DoorOccupancyPolicy.Crush, DoorForcePolicy.Normal);
        Require(crush.Commit == MapActionCommit.Refused && crush.Code == DoorActions.CrushUnsupported,
            "A crush request was not refused by name: " + crush.Code);
        var force = DoorActions.Close(door, AddressOf(door), DoorOccupancyPolicy.Block, DoorForcePolicy.Force);
        Require(force.Commit == MapActionCommit.Refused && force.Code == DoorActions.ForceCloseUnsupported,
            "A forced close was not refused by name: " + force.Code);
        Require(door.OpenCloseCalls == 0, "A request no native entry can carry was run in its plain form.");
    }

    [Fact]
    public void alarm_wave_arms_and_disarms_the_doors_own_lock_component()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Open);
        var locks = door.m_locks!.TryCast<LG_SecurityDoor_Locks>()!;
        // The door's own alarm: the lock component reports that it carries one, and the puzzle it was set up
        // with is the instance the two entries act on.
        locks.m_hasAlarm = true;
        var puzzle = new ChainedPuzzles.ChainedPuzzleInstance();
        locks.ChainedPuzzleToSolve = puzzle;
        var armed = DoorActions.SetAlarm(door, AddressOf(door), DoorAlarmMode.Start);
        Require(armed.Commit == MapActionCommit.Issued && armed.Code == DoorActions.AlarmStarted,
            "The door's own alarm wave was not armed: " + armed.Code);
        var disarmed = DoorActions.SetAlarm(door, AddressOf(door), DoorAlarmMode.Stop);
        Require(disarmed.Commit == MapActionCommit.Issued && disarmed.Code == DoorActions.AlarmStopped,
            "The door's own alarm wave was not disarmed: " + disarmed.Code);
        Require(puzzle.ActivateCalls == 1 && puzzle.DeactivateCalls == 1 && !puzzle.IsActive,
            "The alarm's own master entries were not the members written.");
    }

    [Fact]
    public void alarm_wave_refuses_a_door_that_holds_no_lock_component()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Open);
        door.m_locks = null;
        var outcome = DoorActions.SetAlarm(door, AddressOf(door), DoorAlarmMode.Start);
        Require(outcome.Commit == MapActionCommit.Refused && outcome.Code == DoorActions.NoLockComponent,
            "A door with no lock component was written to: " + outcome.Code);
    }

    [Fact]
    public void lock_with_a_key_item_uses_the_lock_components_own_key_setup()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Closed);
        var locks = door.m_locks!.TryCast<LG_SecurityDoor_Locks>()!;
        var key = new GateKeyItem { DataBlockID = 77, PublicName = "KEY_77" };
        var outcome = DoorActions.LockWithKeyItem(door, AddressOf(door), key);
        Require(outcome.Commit == MapActionCommit.Issued && outcome.Code == DoorActions.LockedWithKey,
            "The key-item lock was not set up: " + outcome.Code);
        Require(locks.KeyItemLockCalls == 1 && ReferenceEquals(locks.LastKeyItem, key),
            "The lock component's own key setup did not receive the resolved key item.");
        var unresolved = DoorActions.LockWithKeyItem(door, AddressOf(door), null!);
        Require(unresolved.Commit == MapActionCommit.Refused && unresolved.Code == DoorActions.KeyItemUnusable,
            "An unresolved key item was not refused: " + unresolved.Code);
        Require(locks.KeyItemLockCalls == 1, "A lock was set up with a key item that does not read.");
    }

    [Fact]
    public void lock_without_a_key_refuses_an_empty_prompt_and_uses_the_lock_components_own_setup()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Closed);
        var locks = door.m_locks!.TryCast<LG_SecurityDoor_Locks>()!;
        var empty = DoorActions.LockWithoutKey(door, AddressOf(door), default);
        Require(empty.Commit == MapActionCommit.Refused && empty.Code == DoorActions.LockTextUnusable,
            "A no-key lock with no interaction text was not refused: " + empty.Code);
        Require(locks.NoKeyLockCalls == 0, "A no-key lock was set up with an empty prompt.");
        var outcome = DoorActions.LockWithoutKey(door, AddressOf(door), new LocalizedText("LOCKED_BY_FORGE"));
        Require(outcome.Commit == MapActionCommit.Issued && outcome.Code == DoorActions.LockedWithoutKey,
            "The no-key lock was not set up: " + outcome.Code);
        Require(locks.NoKeyLockCalls == 1 && locks.LastNoKeyText.UntranslatedText == "LOCKED_BY_FORGE",
            "The lock component's own no-key setup did not receive the resolved prompt.");
    }

    [Fact]
    public void damage_refuses_the_native_damage_type_and_source_arguments_it_cannot_carry()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Closed);
        var address = AddressOf(door);
        var origin = new UnityEngine.Vector3(1f, 2f, 3f);
        var unknown = DoorActions.Damage(door, address, (eDoorDamageType)9, origin, null);
        Require(unknown.Commit == MapActionCommit.Refused && unknown.Code == DoorActions.DamageTypeUnknown,
            "A damage type outside the native vocabulary was not refused: " + unknown.Code);
        var unreadable = DoorActions.Damage(door, address, eDoorDamageType.Explosion,
            new UnityEngine.Vector3(float.NaN, 0f, 0f), null);
        Require(unreadable.Commit == MapActionCommit.Refused && unreadable.Code == DoorActions.SourcePositionUnusable,
            "A source position that does not read was not refused: " + unreadable.Code);
        Require(door.DamageCalls == 0, "A door was damaged through an argument this layer had already refused.");
    }

    [Fact]
    public void damage_refuses_an_addressed_door_because_its_own_entry_carries_nothing()
    {
        var world = Level();
        var door = world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Closed);
        var agent = new Player.PlayerAgent();
        var outcome = DoorActions.Damage(door, AddressOf(door), eDoorDamageType.MeleeWeaponHeavy,
            new UnityEngine.Vector3(4f, 5f, 6f), agent);
        Require(outcome.Commit == MapActionCommit.Refused && outcome.Code == DoorActions.NotDamageable,
            "The addressed door kind was damaged through an entry that carries nothing: " + outcome.Code);
        Require(door.DamageCalls == 0 && door.LastStatus == eDoorStatus.Closed,
            "The security door's own empty damage override was reached, or the refusal changed the door.");
    }

    [Fact]
    public void terminal_command_parses_through_the_terminals_own_interpreter_and_sends_what_it_answered()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var terminal = TerminalWith(world, zone, 7001, TERM_Command.Open);
        LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager();
        var outcome = TerminalActions.Command(terminal, AddressOf(terminal), "open", null);
        Require(outcome.Commit == MapActionCommit.Issued && outcome.Code == TerminalActions.CommandSent,
            "A whitelisted command was not sent: " + outcome.Code);
        Require(terminal.m_command!.ParseCalls == 1 && terminal.m_command.LastInput == "open",
            "The command was not parsed by the terminal's own interpreter.");
        Require(LG_ComputerTerminalManager.Sent.Count == 1, "The command entry was not asked exactly once.");
        var sent = LG_ComputerTerminalManager.Sent[0];
        Require(sent.TerminalId == 7001 && sent.Command == TERM_Command.Open && sent.Input == "open"
            && sent.Param1 == "" && sent.Param2 == "",
            "The command entry did not receive the terminal's own id, the parsed command and the parsed parameters.");
    }

    [Fact]
    public void terminal_command_carries_the_arguments_into_the_line_the_interpreter_receives()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var terminal = TerminalWith(world, zone, 7002, TERM_Command.Ping);
        LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager();
        var outcome = TerminalActions.Command(terminal, AddressOf(terminal), "ping", "door_1");
        Require(outcome.Commit == MapActionCommit.Issued, "A whitelisted command with arguments was not sent: " + outcome.Code);
        Require(terminal.m_command!.LastInput == "ping door_1", "The arguments did not reach the interpreter in the input line.");
        var sent = LG_ComputerTerminalManager.Sent[0];
        Require(sent.Input == "ping door_1" && sent.Param1 == "door_1",
            "The command entry did not carry the input line and the parameter the interpreter produced.");
    }

    [Fact]
    public void terminal_command_refuses_a_line_the_terminal_does_not_recognise()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var terminal = TerminalWith(world, zone, 7003, TERM_Command.Open);
        LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager();
        var outcome = TerminalActions.Command(terminal, AddressOf(terminal), "shutdown", null);
        Require(outcome.Commit == MapActionCommit.Refused && outcome.Code == TerminalActions.CommandUnknown,
            "A line the terminal does not know became a command: " + outcome.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "An unrecognised line was sent to the command entry.");
    }

    [Fact]
    public void terminal_command_refuses_a_command_outside_the_whitelist()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var terminal = TerminalWith(world, zone, 7004, TERM_Command.Exit, TERM_Command.Help);
        LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager();
        foreach (var name in new[] { "exit", "help" })
        {
            var outcome = TerminalActions.Command(terminal, AddressOf(terminal), name, null);
            Require(outcome.Commit == MapActionCommit.Refused && outcome.Code == TerminalActions.CommandNotAllowed,
                "A terminal session command was run as a map action: " + outcome.Code);
        }
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A command outside the whitelist was sent.");
        var empty = TerminalActions.Command(terminal, AddressOf(terminal), "  ", null);
        Require(empty.Commit == MapActionCommit.Refused && empty.Code == TerminalActions.CommandEmpty,
            "An empty command was not refused: " + empty.Code);
    }

    [Fact]
    public void terminal_command_refuses_without_a_manager_and_the_member_it_cannot_reach()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var terminal = TerminalWith(world, zone, 7005, TERM_Command.Open);
        var address = AddressOf(terminal);
        LG_ComputerTerminalManager.Current = null;
        var noManager = TerminalActions.Command(terminal, address, "open", null);
        Require(noManager.Commit == MapActionCommit.Refused && noManager.Code == TerminalActions.ManagerUnavailable,
            "A command was sent with no manager alive: " + noManager.Code);
        LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager();
        terminal.m_command = null;
        var noInterpreter = TerminalActions.Command(terminal, address, "open", null);
        Require(noInterpreter.Commit == MapActionCommit.Refused && noInterpreter.Code == TerminalActions.InterpreterUnavailable,
            "A command was sent through a terminal with no interpreter: " + noInterpreter.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A command was sent without the member it needs.");
    }

    [Fact]
    public void terminal_actions_refuse_a_terminal_that_is_unreadable_stale_or_not_the_addressed_one()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var first = TerminalWith(world, zone, 7006, TERM_Command.Open);
        var second = world.Terminal(zone, TERM_State.Awake);
        LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager();
        var absent = TerminalActions.Command(null!, AddressOf(first), "open", null);
        Require(absent.Commit == MapActionCommit.Refused && absent.Code == TerminalActions.Unavailable,
            "A missing terminal was not refused as unreadable: " + absent.Code);
        first.Destroyed = true;
        var collected = TerminalActions.Command(first, AddressOf(second), "open", null);
        Require(collected.Commit == MapActionCommit.Refused && collected.Code == TerminalActions.Unavailable,
            "A collected terminal was not refused as unreadable: " + collected.Code);
        var live = TerminalWith(world, zone, 7007, TERM_Command.Open);
        // The level grew a terminal after the table was read, so the table is rebuilt as a new world would
        // rebuild it; the refusal below is then about the address, not about a table that never saw the terminal.
        ZoneIndex.Reset();
        var moved = TerminalActions.Command(live, AddressOf(second), "open", null);
        Require(moved.Commit == MapActionCommit.Refused && moved.Code == TerminalActions.Stale,
            "A terminal that is not the addressed one was written to: " + moved.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A refused terminal still reached the command entry.");
    }

    [Fact]
    public void terminal_output_adds_one_local_line_in_the_severitys_own_kind()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var terminal = TerminalWith(world, zone, 7008);
        LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager();
        var kinds = new[] { TerminalLineSeverity.Info, TerminalLineSeverity.Warning, TerminalLineSeverity.Error };
        var expected = new[] { TerminalLineType.Normal, TerminalLineType.Warning, TerminalLineType.Fail };
        for (int i = 0; i != kinds.Length; i++)
        {
            var outcome = TerminalActions.Print(terminal, AddressOf(terminal), kinds[i], "line " + i);
            Require(outcome.Commit == MapActionCommit.Issued && outcome.Code == TerminalActions.LineAdded,
                "A printed line was not added: " + outcome.Code);
            Require(terminal.Lines[i].Type == expected[i] && terminal.Lines[i].Text == "line " + i
                && terminal.Lines[i].Time == 0f,
                "The line did not carry the severity's own line kind, its text and the native member's own time.");
        }
        // The line buffer is local to the terminal: the command channel is not the path a printed line takes, so
        // nothing about it can be read as a replicated line.
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A printed line was sent through the command channel.");
    }

    [Fact]
    public void terminal_output_refuses_an_empty_text()
    {
        var world = Level();
        var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
        var terminal = TerminalWith(world, zone, 7009);
        var outcome = TerminalActions.Print(terminal, AddressOf(terminal), TerminalLineSeverity.Info, "");
        Require(outcome.Commit == MapActionCommit.Refused && outcome.Code == TerminalActions.TextEmpty,
            "An empty text was printed: " + outcome.Code);
        Require(terminal.Lines.Count == 0, "An empty text reached the terminal's line buffer.");
    }
}
