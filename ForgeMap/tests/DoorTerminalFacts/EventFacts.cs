using System;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using LevelGeneration;

namespace ForgeMapTests.DoorTerminalFacts;

/// <summary>The five event rows: what each one publishes, which ports it carries, the three facts read from the
/// terminal's one command entry, the two stages of a weak door, the absence semantics, the authority rule and
/// the cleanup on a world transition. Every case drives the production `DoorTerminalFacts` the Harmony patches
/// call, so a rule that only lived in a patch would not be reached here.</summary>
public sealed class EventFacts
{
    [Fact]
    public void ApproachPublishesTheDoorAndNoActor()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        world.Facts.DoorApproached(door);

        var published = world.Facts.Last(DoorTerminalEventContract.DoorApproachFact, World.AddressOf(door));
        Assert.NotNull(published);
        Assert.Equal(DoorTerminalEventContract.Binding(DoorTerminalEventContract.DoorApproachFact), published!.Value.Binding);
        Assert.Equal(1, world.Facts.Published);
        Assert.Equal(world.ReferenceOf(door).Id, Port(published.Value, "door").GetProperty("id").GetString());
        // The approach callback carries no player and the door's replicated state carries none either, so the
        // actor port is absent rather than written as a player that was never observed.
        Assert.False(Has(published.Value, "actor"));
    }

    [Fact]
    public void RepeatedApproachOfOneDoorPublishesOnce()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        world.Facts.DoorApproached(door);
        world.Facts.DoorApproached(door);

        Assert.Equal(1, world.Facts.Published);
        Assert.Equal(1, world.Facts.Tracked);
    }

    [Fact]
    public void ScanStartedAndCompletedCarryTheirOwnPhases()
    {
        using var world = new World();
        world.Start();
        var door = world.Door(status: eDoorStatus.ChainedPuzzleActivated);
        var locks = World.Locks(door);

        world.Facts.ScanTransition(locks, DoorScanStage.Activated);
        door.LastStatus = eDoorStatus.Unlocked;
        // The puzzle's own reading is the authority on which phase the callback was: the solved notification
        // runs while the puzzle reads solved.
        locks.ChainedPuzzleToSolve = new ChainedPuzzles.ChainedPuzzleInstance { IsSolved = true };
        world.Facts.ScanTransition(locks, DoorScanStage.Solved);

        var published = world.Facts.Last(DoorTerminalEventContract.DoorScanFact, World.AddressOf(door));
        Assert.NotNull(published);
        // The state key is the phase and the status, so the completion is a second fact about the same door.
        Assert.Equal(2, world.Facts.Published);
        Assert.Equal(MapObjectPhases.Completed, Port(published!.Value, "phase").GetInt32());
        // The status is re-read from the door after the callback, never taken from the callback's argument.
        Assert.Equal("unlocked", Port(published.Value, "status").GetString());
    }

    [Fact]
    public void ASolvedNotificationOnAnUnsolvedPuzzleIsTheActivationItReallyIs()
    {
        using var world = new World();
        world.Start();
        var door = world.Door(status: eDoorStatus.Closed_LockedWithChainedPuzzle);
        var locks = World.Locks(door);
        // The puzzle reads unsolved, so the callback that was named "solved" is published as the activation the
        // puzzle's own reading really saw rather than as a completion nothing confirmed.
        locks.ChainedPuzzleToSolve = new ChainedPuzzles.ChainedPuzzleInstance { IsSolved = false };

        world.Facts.ScanTransition(locks, DoorScanStage.Solved);

        var published = world.Facts.Last(DoorTerminalEventContract.DoorScanFact, World.AddressOf(door));
        Assert.NotNull(published);
        Assert.Equal(MapObjectPhases.Started, Port(published!.Value, "phase").GetInt32());
    }

    [Fact]
    public void ScanStartedReadsTheDoorRatherThanTheCallback()
    {
        using var world = new World();
        world.Start();
        var door = world.Door(status: eDoorStatus.Closed_LockedWithChainedPuzzle);

        world.Facts.ScanTransition(World.Locks(door), DoorScanStage.Activated);

        var published = world.Facts.Last(DoorTerminalEventContract.DoorScanFact, World.AddressOf(door));
        Assert.NotNull(published);
        Assert.Equal(MapObjectPhases.Started, Port(published!.Value, "phase").GetInt32());
        Assert.Equal("closed_locked_with_chained_puzzle", Port(published.Value, "status").GetString());
    }

    [Fact]
    public void AWeakDoorPublishesItsTwoStagesWithZonePositionAndAttacker()
    {
        using var world = new World();
        world.Start();
        var door = world.WeakDoor(zone: 4, x: 1.5f, y: -2f, z: 7f);
        var player = world.Player("steam-1");

        world.Facts.WeakDoorAttacked(door, player);
        var attacked = world.Facts.LastWeak(DoorTerminalEventContract.DoorBrokenFact, world.ReferenceOf(door));
        Assert.NotNull(attacked);
        Assert.Equal(DoorTerminalEventContract.DoorPhaseIndex(DoorTerminalEventContract.AttackedPhase),
            Port(attacked!.Value, "phase").GetInt32());
        Assert.Equal("gtfo.zone:0:0:4", Port(attacked.Value, "zone").GetProperty("resourceId").GetString());
        Assert.Equal(1.5, Port(attacked.Value, "position")[0].GetDouble(), 3);
        Assert.Equal(world.PlayerReference(player).Id, Port(attacked.Value, "attacker").GetProperty("id").GetString());

        // The door broke, which is the second stage of the same author node and a second fact about one door.
        world.Facts.WeakDoorBroken(door, null);
        var broken = world.Facts.LastWeak(DoorTerminalEventContract.DoorBrokenFact, world.ReferenceOf(door));
        Assert.NotNull(broken);
        Assert.Equal(DoorTerminalEventContract.DoorPhaseIndex(DoorTerminalEventContract.BrokenPhase),
            Port(broken!.Value, "phase").GetInt32());
        Assert.False(Has(broken.Value, "attacker"));
        Assert.Equal(2, world.Facts.Published);
    }

    [Fact]
    public void AWeakDoorTheLevelCannotPlacePublishesNothing()
    {
        using var world = new World();
        world.Start();
        var door = world.WeakDoor();
        // A gate with no course node names no zone, so there is no zone for the row to report and no id for the
        // door to be named by: the fact is not published rather than published without the zone an author needs.
        door.Gate!.m_nodes.Clear();

        world.Facts.WeakDoorAttacked(door, null);

        Assert.Equal(0, world.Facts.Published);
    }

    [Fact]
    public void AWeakDoorReferenceStopsBeingCurrentAfterAWorldTransition()
    {
        using var world = new World();
        world.Start();
        var door = world.WeakDoor();
        world.Facts.WeakDoorAttacked(door, null);
        var reference = world.ReferenceOf(door);
        Assert.True(world.Facts.IsCurrentWeakDoor(reference));

        world.Facts.BeginWorld();

        Assert.False(world.Facts.IsCurrentWeakDoor(reference));
    }

    [Fact]
    public void RepeatedAttackOfOneDoorPublishesOnce()
    {
        using var world = new World();
        world.Start();
        var door = world.WeakDoor();

        world.Facts.WeakDoorAttacked(door, null);
        world.Facts.WeakDoorAttacked(door, null);

        Assert.Equal(1, world.Facts.Published);
    }

    [Fact]
    public void CommandsArePublishedWithTheirOwnSlotAndALogReadPublishesBothFacts()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(4242);

        world.Facts.TerminalCommandEntry(4242, (int)TERM_Command.UniqueCommand3, "boom now", null);
        world.Facts.TerminalCommandEntry(4242, (int)TERM_Command.ReadLog, "read_log FILE_1", "FILE_1");

        var command = world.Facts.Last(DoorTerminalPublisher.CommandFact, World.AddressOf(terminal));
        Assert.NotNull(command);
        // The command fact is keyed by the command, so the last one is the second: the read.
        Assert.Equal("read_log", Port(command!.Value, "command").GetString());
        Assert.False(Has(command.Value, "slot"));

        var log = world.Facts.Last(DoorTerminalEventContract.TerminalLogFact, World.AddressOf(terminal));
        Assert.NotNull(log);
        Assert.Equal("FILE_1", Port(log!.Value, "log").GetString());
        Assert.Equal("read_log FILE_1", Port(log.Value, "line").GetString());
    }

    [Fact]
    public void OneCommandFactPerCommandValue()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(4242);

        world.Facts.TerminalCommandEntry(4242, (int)TERM_Command.UniqueCommand3, "boom now", null);
        var slot = world.Facts.Last(DoorTerminalPublisher.CommandFact, World.AddressOf(terminal));
        Assert.NotNull(slot);
        Assert.Equal("unique_command_3", Port(slot!.Value, "command").GetString());
        // The five slots are the game's own enum members, published one-based.
        Assert.Equal(3, Port(slot.Value, "slot").GetInt32());
        Assert.Equal("boom now", Port(slot.Value, "input").GetString());

        // The same command twice in one world is one fact: the ledger keys it by the command value, so the
        // second entry is remembered under the same key and never reaches the kernel.
        long queued = world.Facts.Published;
        world.Facts.TerminalCommandEntry(4242, (int)TERM_Command.UniqueCommand3, "boom now", null);
        Assert.Equal(queued, world.Facts.Published);
    }

    [Fact]
    public void ACommandThatIsNotOneOfTheFiveSlotsCarriesNoSlot()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(7);

        world.Facts.TerminalCommandEntry(7, (int)TERM_Command.DisableAlarm, "disable_alarm", null);

        var command = world.Facts.Last(DoorTerminalPublisher.CommandFact, World.AddressOf(terminal));
        Assert.NotNull(command);
        Assert.Equal("disable_alarm", Port(command!.Value, "command").GetString());
        Assert.False(Has(command.Value, "slot"));
    }

    [Fact]
    public void ALogReadWithoutANamePublishesNoLogFact()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(9);

        world.Facts.TerminalCommandEntry(9, (int)TERM_Command.ReadLog, "read_log", "   ");

        // The command fact is published — the terminal did accept the read — but a log with no name is absence,
        // so no log fact is published rather than one carrying an empty name.
        Assert.NotNull(world.Facts.Last(DoorTerminalPublisher.CommandFact, World.AddressOf(terminal)));
        Assert.Null(world.Facts.Last(DoorTerminalEventContract.TerminalLogFact, World.AddressOf(terminal)));
    }

    [Fact]
    public void UnknownTerminalSyncIdPublishesNothing()
    {
        using var world = new World();
        world.Start();
        world.Terminal(1);

        world.Facts.TerminalCommandEntry(2, (int)TERM_Command.Help, "help", null);

        Assert.Equal(0, world.Facts.Published);
    }

    [Fact]
    public void ASmashedAndAHackedLockCarryTheirOwnCause()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        world.Facts.WeakLockChanged(World.WeakLock(door, eWeakLockType.Melee, eWeakLockStatus.Unlocked));
        var smashed = world.Facts.Last(DoorTerminalEventContract.LockBrokenFact, World.AddressOf(door));
        Assert.NotNull(smashed);
        Assert.Equal(DoorTerminalEventContract.LockCauseIndex("smashed"), Port(smashed!.Value, "cause").GetInt32());
        Assert.Equal("melee", Port(smashed.Value, "lock_kind").GetString());

        world.Facts.WeakLockChanged(World.WeakLock(door, eWeakLockType.Hackable, eWeakLockStatus.Unlocked));
        var hacked = world.Facts.Last(DoorTerminalEventContract.LockBrokenFact, World.AddressOf(door));
        Assert.NotNull(hacked);
        Assert.Equal(DoorTerminalEventContract.LockCauseIndex("hacked"), Port(hacked!.Value, "cause").GetInt32());
        Assert.Equal("hackable", Port(hacked.Value, "lock_kind").GetString());
        Assert.Equal(2, world.Facts.Published);
    }

    [Fact]
    public void ALockThatIsNotUnlockedPublishesNothing()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        world.Facts.WeakLockChanged(World.WeakLock(door, eWeakLockType.Melee, eWeakLockStatus.LockedMelee));

        Assert.Equal(0, world.Facts.Published);
    }

    [Fact]
    public void ADoorWithoutAnAddressPublishesNothing()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();
        // A weak, node or decorative door is not a zone's entrance gate, so it has no address in this provider's
        // grammar: no fact is published for an address that does not exist.
        door.AddressNow = null;

        world.Facts.DoorApproached(door);
        world.Facts.ScanTransition(World.Locks(door), DoorScanStage.Activated);
        world.Facts.WeakLockChanged(World.WeakLock(door, eWeakLockType.Melee, eWeakLockStatus.Unlocked));

        Assert.Equal(0, world.Facts.Published);
    }

    [Fact]
    public void AClientPublishesNothing()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();
        world.LoseAuthority();

        world.Facts.DoorApproached(door);

        Assert.Equal(0, world.Facts.Published);
        Assert.Contains(world.Reports, report => report.Contains("non-authoritative", StringComparison.Ordinal));
    }

    [Fact]
    public void AWorldTransitionReleasesEveryKey()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();
        world.Facts.DoorApproached(door);
        Assert.Equal(1, world.Facts.Tracked);

        world.Facts.BeginWorld();

        Assert.Equal(0, world.Facts.Tracked);
        // The same fact is new again in the next world, because no address of the old one survives.
        world.Facts.DoorApproached(door);
        Assert.Equal(2, world.Facts.Published);
    }

    [Fact]
    public void AnEventIdNamesTheFactTheWorldTheAddressAndTheTransition()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        world.Facts.DoorApproached(door);

        var eventId = world.Facts.Last(DoorTerminalEventContract.DoorApproachFact, World.AddressOf(door))!.Value.EventId;
        Assert.StartsWith(World.Kind + ".door_approach:" + World.WorldEpoch + ":", eventId, StringComparison.Ordinal);
        Assert.Contains(World.AddressOf(door).ToString(), eventId, StringComparison.Ordinal);
        Assert.EndsWith(":1", eventId, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedCallbackDisablesTheProducerOnce()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        world.Facts.Guard(_ => throw new InvalidOperationException("fixture callback failure"));
        Assert.True(world.Facts.Faulted);
        Assert.Contains(world.Reports, report => report.Contains("disabled until restart", StringComparison.Ordinal));

        // Every later callback is a no-op rather than a second failure or a half-published fact.
        world.Facts.Guard(_ => world.Facts.DoorApproached(door));
        Assert.Equal(0, world.Facts.Published);
    }

    private static JsonElement Port((string Binding, string EventId, string StateKey, string Outputs) published, string port)
    {
        using var document = JsonDocument.Parse(published.Outputs);
        return document.RootElement.GetProperty(port).Clone();
    }

    private static bool Has((string Binding, string EventId, string StateKey, string Outputs) published, string port)
    {
        using var document = JsonDocument.Parse(published.Outputs);
        return document.RootElement.TryGetProperty(port, out _);
    }
}
