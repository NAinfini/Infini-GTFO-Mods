using System;
using System.Linq;
using System.Text.Json;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMap.Tests.DoorActions;

/// <summary>
/// The focused suite for the three registered door action rows. Every case runs the production handler the way
/// the kernel runs it and asserts two things: the result row the plan would read, and whether the native entry
/// the row claims was really invoked. Native state is the doubles in `GameDoubles.cs` and NOT game-verified.
/// </summary>
public sealed class DoorActionTests
{
    // ---- the declaration the registry accepts ---------------------------------------------------------

    [Fact]
    public void the_two_door_rows_and_the_map_state_row_all_register()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var manifest = world.Manifest();
        var registry = manifest.GetProperty("registry");

        var capabilities = Ids(registry.GetProperty("capabilities"));
        // The manifest lists the declared rows in its own order, so the case compares them as a set.
        Assert.Equal(new[]
        {
            DoorActionContract.CloseCapability, DoorActionContract.OpenCapability,
            MapStateContract.InteractionCapabilityId
        }.OrderBy(id => id, StringComparer.Ordinal), capabilities.OrderBy(id => id, StringComparer.Ordinal));
        // The three rows no native path can keep are not declared at all: a plan pinning one resolves no binding.
        Assert.DoesNotContain("forge.action.map.door_lock", capabilities);
        Assert.DoesNotContain("forge.action.map.door_unlock", capabilities);
        Assert.DoesNotContain("forge.action.map.door_damage", capabilities);
        // The alarm action is gone: one chained-puzzle write keeps one card, and that card is the scan row's.
        Assert.DoesNotContain("forge.action.map.door_alarm", capabilities);
        Assert.DoesNotContain("forge.action.map.alarm", capabilities);

        Assert.Equal(new[]
        {
            DoorActionContract.Binding(DoorActionContract.CloseCapability),
            DoorActionContract.Binding(DoorActionContract.OpenCapability),
            MapStateContract.InteractionBindingId
        }.OrderBy(id => id, StringComparer.Ordinal),
            Ids(registry.GetProperty("bindings")).OrderBy(id => id, StringComparer.Ordinal));

        var support = manifest.GetProperty("bindingSupport").EnumerateArray()
            .Select(row => row.GetProperty("bindingId").GetString()).ToArray();
        Assert.Equal(new[]
        {
            DoorActionContract.Binding(DoorActionContract.CloseCapability),
            DoorActionContract.Binding(DoorActionContract.OpenCapability),
            MapStateContract.InteractionBindingId
        }.OrderBy(id => id, StringComparer.Ordinal), support.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void the_open_row_carries_the_catalog_ports_and_its_recipient_contract()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var graph = world.Manifest().GetProperty("registry").GetProperty("capabilities").EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == DoorActionContract.OpenCapability)
            .GetProperty("graph");

        Assert.Equal("host", graph.GetProperty("execution").GetString());
        Assert.Equal(new[] { "in", "doors" },
            graph.GetProperty("inputs").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        Assert.Equal("many", graph.GetProperty("inputs").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "doors")
            .GetProperty("cardinality").GetString());
        Assert.Equal(new[] { "gtfo.map_object" }, graph.GetProperty("inputs").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "doors")
            .GetProperty("entityKinds").EnumerateArray().Select(k => k.GetString()).ToArray());
        Assert.Equal("doors", graph.GetProperty("recipients").GetProperty("input").GetString());
        Assert.Equal(new[] { "door.control" },
            graph.GetProperty("recipients").GetProperty("requires").EnumerateArray().Select(p => p.GetString()).ToArray());
        Assert.Equal(new[] { DoorActionContract.OpenPermission },
            world.Manifest().GetProperty("bindingSupport").EnumerateArray()
                .Single(row => row.GetProperty("bindingId").GetString() == DoorActionContract.Binding(DoorActionContract.OpenCapability))
                .GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ToArray());
    }

    [Fact]
    public void the_interaction_row_declares_one_structural_setting_and_its_recipient_contract()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var graph = RowGraph(world, MapStateContract.InteractionCapabilityId);

        Assert.Equal("host", graph.GetProperty("execution").GetString());
        Assert.Equal(new[] { "in", "targets" },
            graph.GetProperty("inputs").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        Assert.Equal(new[] { "enable", "disable" },
            graph.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "operation")
                .GetProperty("values").EnumerateArray().Select(p => p.GetString()).ToArray());
        Assert.Equal(new[] { MapStateContract.InteractionPermission },
            graph.GetProperty("recipients").GetProperty("requires").EnumerateArray().Select(p => p.GetString()).ToArray());
        Assert.Equal(new[] { MapStateContract.InteractionPermission },
            world.Manifest().GetProperty("bindingSupport").EnumerateArray()
                .Single(row => row.GetProperty("bindingId").GetString() == MapStateContract.InteractionBindingId)
                .GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ToArray());
    }

    private static JsonElement RowGraph(DoorActionWorld world, string capability)
        => world.Manifest().GetProperty("registry").GetProperty("capabilities").EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == capability)
            .GetProperty("graph");

    private static string?[] Ids(JsonElement rows)
        => rows.EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToArray();

    // ---- host execution ------------------------------------------------------------------------------

    [Fact]
    public void open_asks_the_doors_own_interaction_entry()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(door) } }, new { bypass_policy = "respect" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(1, door.OpenCloseCalls);
        Assert.False(door.LastOnlyUnlock);
        Assert.Equal(0, door.ForceOpenCalls);
        Assert.Equal("succeeded", DoorActionWorld.Field(result, "status"));
        Assert.Equal(CommitStates.Confirmed, DoorActionWorld.Field(result, "committed"));
        Assert.Equal("door-open-issued", DoorActionWorld.Field(result, "code"));
    }

    [Fact]
    public void open_force_asks_the_doors_own_force_entry()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1, eDoorStatus.Closed_LockedWithKeyItem);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(door) } }, new { bypass_policy = "force" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(1, door.ForceOpenCalls);
        Assert.Equal(0, door.OpenCloseCalls);
    }

    [Fact]
    public void open_leaves_a_door_that_already_reads_open_alone()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1, eDoorStatus.Open);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(door) } }, new { bypass_policy = "respect" });

        // The interaction entry toggles, so a second open must not become a close.
        Assert.Equal(0, door.OpenCloseCalls);
        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal("door-already-open", DoorActionWorld.Field(result, "code"));
    }

    [Fact]
    public void close_asks_the_same_entry_only_for_a_door_that_reads_open()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var open = world.Door(1, eDoorStatus.Open);
        var shut = world.Door(2);

        var closed = world.Run(world.Commands.HandleClose,
            new { doors = new[] { world.ReferenceOf(open), world.ReferenceOf(shut) } }, null);

        // A door that already reads closed is the state the request asked for: a committed row that wrote
        // nothing, beside the door whose own entry was really asked.
        Assert.Equal(CommandStatuses.Succeeded, closed.Status);
        Assert.Equal(CommitStates.Confirmed, closed.CommitState);
        Assert.Equal(1, open.OpenCloseCalls);
        Assert.Equal(0, shut.OpenCloseCalls);
        var rows = DoorActionWorld.Rows(closed);
        Assert.Equal("door-close-issued", rows[0].GetProperty("code").GetString());
        Assert.Equal("door-already-closed", rows[1].GetProperty("code").GetString());
    }

    // ---- the host gate -------------------------------------------------------------------------------

    [Fact]
    public void a_command_dispatch_that_is_not_the_host_is_refused_before_any_door_is_looked_up()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);

        // Each row reads its own recipient port: the two door rows name `doors`, the state row `targets`.
        foreach (var handler in new CommandHandler[] { world.Commands.HandleOpen, world.Commands.HandleClose })
        {
            var refused = world.Run(handler, new { doors = new[] { world.ReferenceOf(door) } },
                new { bypass_policy = "respect" }, isHost: false);

            Assert.Equal(CommandStatuses.Rejected, refused.Status);
            Assert.Equal(CommitStates.None, refused.CommitState);
            Assert.Equal(DoorActionCommands.AuthorityCode, refused.Code);
        }

        Assert.Empty(world.LookedUp);
        Assert.Equal(0, door.OpenCloseCalls);
    }

    [Fact]
    public void a_session_that_is_not_the_master_and_one_that_is_not_ready_are_refused()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);
        var request = new { doors = new[] { world.ReferenceOf(door) } };
        var parameters = new { bypass_policy = "respect" };

        world.Master = false;
        Assert.Equal(DoorActionCommands.AuthorityCode,
            world.Run(world.Commands.HandleOpen, request, parameters).Code);

        world.Master = true;
        world.Ready = false;
        Assert.Equal(DoorActionCommands.AuthorityCode,
            world.Run(world.Commands.HandleOpen, request, parameters).Code);
        Assert.Equal(0, door.OpenCloseCalls);
    }

    // ---- stale and foreign recipients ------------------------------------------------------------------

    [Fact]
    public void a_reference_minted_in_another_world_is_refused()
    {
        using var world = new DoorActionWorld();
        world.Start();

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.OtherWorld(1) } }, new { bypass_policy = "respect" });

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(DoorActionCommands.StaleCode, DoorActionWorld.Field(result, "code"));
        Assert.Empty(world.LookedUp);
    }

    [Fact]
    public void a_door_this_world_no_longer_answers_for_is_refused()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);
        var reference = world.ReferenceOf(door);
        world.Retire(reference);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { reference } }, new { bypass_policy = "respect" });

        Assert.Equal(DoorActionCommands.StaleCode, DoorActionWorld.Field(result, "code"));
        Assert.Equal(0, door.OpenCloseCalls);
    }

    [Fact]
    public void a_door_that_no_longer_reads_as_its_address_is_refused()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);
        // The same reference, but the instance now reads as another zone's entrance.
        door.AddressNow = MapObjectDoorAddress.Create(0, 0, 9);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(door) } }, new { bypass_policy = "respect" });

        Assert.Equal(DoorActionCommands.StaleCode, DoorActionWorld.Field(result, "code"));
        Assert.Equal(0, door.OpenCloseCalls);
    }

    [Fact]
    public void an_instance_the_provider_no_longer_maps_back_is_refused()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var asked = world.Door(1);
        var other = world.Door(2);

        // The lookup answers with a door of this world, but the provider's own table maps that instance back to
        // a different reference than the one the plan named.
        world.Substitute = other;
        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(asked) } }, new { bypass_policy = "respect" });

        Assert.Equal(DoorActionCommands.MismatchCode, DoorActionWorld.Field(result, "code"));
        Assert.Equal(0, other.OpenCloseCalls);
        Assert.Equal(0, asked.OpenCloseCalls);
    }

    [Fact]
    public void a_reference_of_another_category_or_kind_is_refused_by_name()
    {
        using var world = new DoorActionWorld();
        world.Start();

        var terminal = world.Run(world.Commands.HandleClose,
            new { doors = new[] { world.OtherCategory(1) } }, null);
        Assert.Equal(DoorActionCommands.KindCode, DoorActionWorld.Field(terminal, "code"));

        var player = world.Run(world.Commands.HandleClose,
            new { doors = new[] { world.Player("1") } }, null);
        Assert.Equal(DoorActionCommands.KindCode, DoorActionWorld.Field(player, "code"));
    }

    [Fact]
    public void a_door_lookup_that_throws_is_reported_as_stale_not_as_an_unknown_commit()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);
        world.LookupThrows = true;

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(door) } }, new { bypass_policy = "respect" });

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal(DoorActionCommands.StaleCode, result.Code);
        Assert.Contains(world.Reports, report => report.StartsWith("map.door-resolve-exception:", StringComparison.Ordinal));
    }

    // ---- the native refusals ---------------------------------------------------------------------------

    [Fact]
    public void a_locked_or_broken_door_refuses_an_open_before_anything_is_written()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var locked = world.Door(1, eDoorStatus.Closed_LockedWithKeyItem);
        var broken = world.Door(2, eDoorStatus.Closed_BrokenCantOpen);
        var destroyed = world.Door(3, eDoorStatus.Destroyed);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(locked), world.ReferenceOf(broken), world.ReferenceOf(destroyed) } },
            new { bypass_policy = "respect" });

        var rows = DoorActionWorld.Rows(result);
        Assert.Equal("door-locked", rows[0].GetProperty("code").GetString());
        Assert.Equal("door-closed-broken", rows[1].GetProperty("code").GetString());
        Assert.Equal("door-destroyed", rows[2].GetProperty("code").GetString());
        Assert.Equal(0, locked.OpenCloseCalls);
        Assert.Equal(0, broken.OpenCloseCalls);
        Assert.Equal(0, destroyed.OpenCloseCalls);
        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal("door-open-all-rejected", result.Code);
    }

    [Fact]
    public void an_interaction_gate_that_refuses_is_its_own_code()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);
        door.InteractionAllowed = false;

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(door) } }, new { bypass_policy = "respect" });

        Assert.Equal("door-interaction-not-allowed", DoorActionWorld.Field(result, "code"));
        Assert.Equal(0, door.OpenCloseCalls);
    }

    [Fact]
    public void a_close_carries_no_structural_parameter_and_writes_through_the_doors_own_entry()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1, eDoorStatus.Open);

        var result = world.Run(world.Commands.HandleClose,
            new { doors = new[] { world.ReferenceOf(door) } }, null);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(1, door.OpenCloseCalls);
    }

    [Fact]
    public void a_native_commit_that_throws_is_an_unknown_commit_and_stops_the_rest()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var throwing = world.Door(1);
        var after = world.Door(2);
        throwing.StatusThrows = true;

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(throwing), world.ReferenceOf(after) } },
            new { bypass_policy = "respect" });

        var rows = DoorActionWorld.Rows(result);
        Assert.Equal("failed", rows[0].GetProperty("status").GetString());
        Assert.Equal(CommitStates.Unknown, rows[0].GetProperty("committed").GetString());
        Assert.Equal(DoorActionCommands.CommitExceptionCode, rows[0].GetProperty("code").GetString());
        Assert.Equal("not-attempted-after-unknown-commit", rows[1].GetProperty("code").GetString());
        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal("door-open-all-unknown", result.Code);
        Assert.Equal(0, after.OpenCloseCalls);
        Assert.Contains(world.Reports, report => report.StartsWith("map.door-commit-exception:", StringComparison.Ordinal));
    }

    // ---- the request frame and the result rows ---------------------------------------------------------

    [Fact]
    public void an_open_row_carries_the_catalog_columns_in_their_order()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);
        var second = world.Door(2);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(door), world.ReferenceOf(second) } }, new { bypass_policy = "respect" });

        var rows = DoorActionWorld.Rows(result);
        Assert.Equal(2, rows.Length);
        var row = rows[0];
        Assert.Equal(2, row.GetProperty("target_count").GetInt32());
        Assert.Equal(new[] { "target", "status", "committed", "code", "target_count" },
            row.EnumerateObject().Select(p => p.Name).ToArray());

        // The close row carries the same five columns: the open row's own `duration` column is gone with the
        // port the native entry never took.
        var close = world.Run(world.Commands.HandleClose,
            new { doors = new[] { world.ReferenceOf(door) } }, null);
        Assert.Equal(new[] { "target", "status", "committed", "code", "target_count" },
            DoorActionWorld.Row(close).EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void an_unknown_policy_member_is_refused_by_name()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);
        var doorRequest = new { doors = new[] { world.ReferenceOf(door) } };

        Assert.Equal(DoorActionCommands.PolicyCode,
            world.Run(world.Commands.HandleOpen, doorRequest, new { bypass_policy = "glue" }).Code);
        Assert.Empty(world.LookedUp);
    }

    [Fact]
    public void a_request_that_names_no_door_or_too_many_is_refused_before_any_write()
    {
        using var world = new DoorActionWorld();
        world.Start();

        var empty = world.Run(world.Commands.HandleOpen,
            new { doors = Array.Empty<string>() }, new { bypass_policy = "respect" });
        Assert.Equal(CommandStatuses.Rejected, empty.Status);
        Assert.Equal(DoorActionCommands.NoTargetsCode, empty.Code);

        var many = Enumerable.Range(1, CommandResult.MaximumFacts + 1).Select(world.Unknown).Cast<object>().ToArray();
        var oversize = world.Run(world.Commands.HandleOpen,
            new { doors = many }, new { bypass_policy = "respect" });
        Assert.Equal(CommandStatuses.Rejected, oversize.Status);
        Assert.Equal(DoorActionCommands.TargetsCode, oversize.Code);
        Assert.Empty(world.LookedUp);
    }

    [Fact]
    public void a_committed_door_beside_a_refused_one_is_a_partial_command()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var open = world.Door(1);
        var destroyed = world.Door(2, eDoorStatus.Destroyed);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(open), world.ReferenceOf(destroyed) } },
            new { bypass_policy = "respect" });

        Assert.Equal(CommandStatuses.Partial, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(1, open.OpenCloseCalls);
        var rows = DoorActionWorld.Rows(result);
        Assert.Equal("door-open-issued", rows[0].GetProperty("code").GetString());
        Assert.Equal("door-destroyed", rows[1].GetProperty("code").GetString());
    }

    [Fact]
    public void a_door_that_already_holds_the_requested_state_is_a_confirmed_no_op_row()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1, eDoorStatus.Open);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(door) } }, new { bypass_policy = "respect" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(1, world.Commands.Settled);
        Assert.Equal(0, world.Commands.Submitted);
        Assert.Equal(0, world.Commands.Refused);
        Assert.Equal(0, world.Commands.Unknown);
    }

    // ---- forge.action.map.interaction_state ----------------------------------------------------------

    [Fact]
    public void interaction_disable_puts_the_terminals_own_state_to_sleep()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var terminal = world.Terminal(1, TERM_State.Awake);

        var result = world.Run(world.MapState.HandleInteraction,
            new { targets = new[] { world.ReferenceOf(terminal) } }, new { operation = "disable" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(TERM_State.Sleeping, terminal.CurrentStateName);
        Assert.Equal(MapStateActions.TerminalSleepingCode, DoorActionWorld.Field(result, "code"));
    }

    [Fact]
    public void interaction_enable_wakes_a_sleeping_terminal()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var terminal = world.Terminal(1);

        var result = world.Run(world.MapState.HandleInteraction,
            new { targets = new[] { world.ReferenceOf(terminal) } }, new { operation = "enable" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(TERM_State.Awake, terminal.CurrentStateName);
        Assert.Equal(MapStateActions.TerminalAwakeCode, DoorActionWorld.Field(result, "code"));
    }

    [Fact]
    public void interaction_reports_the_state_the_terminal_already_holds_without_writing()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var terminal = world.Terminal(1, TERM_State.Sleeping);
        var other = world.Terminal(2, TERM_State.Awake);

        var result = world.Run(world.MapState.HandleInteraction,
            new { targets = new[] { world.ReferenceOf(terminal), world.ReferenceOf(other) } },
            new { operation = "disable" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(TERM_State.Sleeping, terminal.CurrentStateName);
        Assert.Equal(TERM_State.Sleeping, other.CurrentStateName);
    }

    [Fact]
    public void interaction_refuses_a_door_by_name_because_this_build_has_no_write_for_it()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);
        var terminal = world.Terminal(1);

        var result = world.Run(world.MapState.HandleInteraction,
            new { targets = new[] { world.ReferenceOf(door), world.ReferenceOf(terminal) } },
            new { operation = "disable" });

        Assert.Equal(CommandStatuses.Partial, result.Status);
        Assert.Equal(MapStateActions.DoorInteractionCode, DoorActionWorld.Rows(result)[0].GetProperty("code").GetString());
        Assert.Equal(TERM_State.Sleeping, terminal.CurrentStateName);
    }

    [Fact]
    public void interaction_refuses_an_unknown_operation_a_foreign_category_and_no_targets()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var terminal = world.Terminal(1, TERM_State.Awake);

        var unknown = world.Run(world.MapState.HandleInteraction,
            new { targets = new[] { world.ReferenceOf(terminal) } }, new { operation = "toggle" });
        Assert.Equal(CommandStatuses.Rejected, unknown.Status);
        Assert.Equal(MapStateActions.OperationCode, unknown.Code);
        Assert.Equal(TERM_State.Awake, terminal.CurrentStateName);

        var foreign = world.Run(world.MapState.HandleInteraction,
            new { targets = new[] { world.OtherCategory(2) } }, new { operation = "disable" });
        Assert.Equal(CommandStatuses.Rejected, foreign.Status);
        Assert.Equal(MapStateActions.KindCode, foreign.Code);

        var none = world.Run(world.MapState.HandleInteraction,
            new { targets = Array.Empty<string>() }, new { operation = "disable" });
        Assert.Equal(CommandStatuses.Rejected, none.Status);
        Assert.Equal(MapStateActions.NoTargetsCode, none.Code);
    }

    [Fact]
    public void interaction_refuses_a_peer_that_is_not_the_host_before_looking_anything_up()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var terminal = world.Terminal(1, TERM_State.Awake);

        var result = world.Run(world.MapState.HandleInteraction,
            new { targets = new[] { world.ReferenceOf(terminal) } }, new { operation = "disable" }, isHost: false);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(MapStateActions.AuthorityCode, result.Code);
        Assert.Equal(TERM_State.Awake, terminal.CurrentStateName);
    }

    [Fact]
    public void interaction_reports_a_stale_reference_and_a_failed_terminal_read()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var terminal = world.Terminal(1, TERM_State.Awake);
        var reference = world.ReferenceOf(terminal);
        world.Retire(reference);

        var stale = world.Run(world.MapState.HandleInteraction,
            new { targets = new[] { reference } }, new { operation = "disable" });
        Assert.Equal(MapStateActions.StaleCode, stale.Code);

        var live = world.Terminal(2, TERM_State.Awake);
        live.StateThrows = true;
        var threw = world.Run(world.MapState.HandleInteraction,
            new { targets = new[] { world.ReferenceOf(live) } }, new { operation = "disable" });

        Assert.Equal(CommandStatuses.Failed, threw.Status);
        Assert.Equal(CommitStates.Unknown, threw.CommitState);
        Assert.Equal(MapStateActions.CommitExceptionCode, threw.Code);
        Assert.Contains(world.Reports, sentence => sentence.Contains("map.state-commit-exception"));
    }
}
