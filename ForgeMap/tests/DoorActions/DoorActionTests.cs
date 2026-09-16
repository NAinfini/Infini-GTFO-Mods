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
    public void the_three_rows_register_and_resolve_their_shapes()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var manifest = world.Manifest();
        var registry = manifest.GetProperty("registry");

        var capabilities = Ids(registry.GetProperty("capabilities"));
        Assert.Equal(new[]
        {
            DoorActionContract.AlarmCapability, DoorActionContract.CloseCapability, DoorActionContract.OpenCapability
        }, capabilities);
        // The three rows no native path can keep are not declared at all: a plan pinning one resolves no binding.
        Assert.DoesNotContain("forge.action.map.door_lock", capabilities);
        Assert.DoesNotContain("forge.action.map.door_unlock", capabilities);
        Assert.DoesNotContain("forge.action.map.door_damage", capabilities);

        Assert.Equal(new[]
        {
            DoorActionContract.Binding(DoorActionContract.AlarmCapability),
            DoorActionContract.Binding(DoorActionContract.CloseCapability),
            DoorActionContract.Binding(DoorActionContract.OpenCapability)
        }, Ids(registry.GetProperty("bindings")));

        var support = manifest.GetProperty("bindingSupport").EnumerateArray()
            .Select(row => row.GetProperty("bindingId").GetString()).ToArray();
        Assert.Equal(new[]
        {
            DoorActionContract.Binding(DoorActionContract.AlarmCapability),
            DoorActionContract.Binding(DoorActionContract.CloseCapability),
            DoorActionContract.Binding(DoorActionContract.OpenCapability)
        }, support);
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
        Assert.Equal(new[] { "in", "doors", "duration" },
            graph.GetProperty("inputs").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        var duration = graph.GetProperty("inputs").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "duration");
        Assert.True(duration.GetProperty("optional").GetBoolean(),
            "The duration port must stay optional: the native entry takes none.");
        Assert.Equal("many", graph.GetProperty("inputs").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "doors")
            .GetProperty("cardinality").GetString());
        Assert.Equal("doors", graph.GetProperty("recipients").GetProperty("input").GetString());
        Assert.Equal(new[] { "door.control" },
            graph.GetProperty("recipients").GetProperty("requires").EnumerateArray().Select(p => p.GetString()).ToArray());
        Assert.Equal(new[] { DoorActionContract.OpenPermission },
            world.Manifest().GetProperty("bindingSupport").EnumerateArray()
                .Single(row => row.GetProperty("bindingId").GetString() == DoorActionContract.Binding(DoorActionContract.OpenCapability))
                .GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ToArray());
    }

    [Fact]
    public void the_alarm_row_declares_the_two_ports_the_native_path_cannot_carry_as_optional()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var graph = world.Manifest().GetProperty("registry").GetProperty("capabilities").EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == DoorActionContract.AlarmCapability)
            .GetProperty("graph");

        var alarm = graph.GetProperty("inputs").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "alarm");
        Assert.Equal("resource", alarm.GetProperty("type").GetString());
        Assert.True(alarm.GetProperty("optional").GetBoolean(),
            "The alarm resource has no provider: a required port would make every alarm step unloadable.");
        var handle = graph.GetProperty("outputs").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "alarm_handle");
        Assert.Equal("handle", handle.GetProperty("type").GetString());
        Assert.True(handle.GetProperty("optional").GetBoolean(),
            "No provider handle can be minted for a command yet, so the port is the absence it is.");
        Assert.Equal(new[] { "start", "stop" },
            graph.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "mode")
                .GetProperty("values").EnumerateArray().Select(p => p.GetString()).ToArray());
    }

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
            new { doors = new[] { world.ReferenceOf(open), world.ReferenceOf(shut) } },
            new { occupancy_policy = "block", force_policy = "normal" });

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

    [Fact]
    public void alarm_start_activates_the_puzzle_the_door_itself_holds()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1, eDoorStatus.Closed_LockedWithChainedPuzzle_Alarm);
        var locks = DoorActionWorld.Locks(door);
        locks.m_hasAlarm = true;
        var puzzle = new ChainedPuzzles.ChainedPuzzleInstance();
        locks.ChainedPuzzleToSolve = puzzle;

        var result = world.Run(world.Commands.HandleAlarm,
            new { doors = new[] { world.ReferenceOf(door) }, source = world.Player("1") }, new { mode = "start" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(1, puzzle.ActivateCalls);
        Assert.Equal(0, puzzle.DeactivateCalls);
        Assert.Equal("door-alarm-started", DoorActionWorld.Field(result, "code"));
    }

    [Fact]
    public void alarm_stop_deactivates_an_active_puzzle()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1, eDoorStatus.ChainedPuzzleActivated);
        var locks = DoorActionWorld.Locks(door);
        locks.m_hasAlarm = true;
        var puzzle = new ChainedPuzzles.ChainedPuzzleInstance { IsActive = true };
        locks.ChainedPuzzleToSolve = puzzle;

        var result = world.Run(world.Commands.HandleAlarm,
            new { doors = new[] { world.ReferenceOf(door) } }, new { mode = "stop" });

        Assert.Equal(1, puzzle.DeactivateCalls);
        Assert.Equal(0, puzzle.ActivateCalls);
        Assert.Equal("door-alarm-stopped", DoorActionWorld.Field(result, "code"));
    }

    [Fact]
    public void alarm_reports_the_state_it_is_already_in_without_writing()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1, eDoorStatus.ChainedPuzzleActivated);
        var locks = DoorActionWorld.Locks(door);
        locks.m_hasAlarm = true;
        var puzzle = new ChainedPuzzles.ChainedPuzzleInstance { IsActive = true };
        locks.ChainedPuzzleToSolve = puzzle;

        var started = world.Run(world.Commands.HandleAlarm,
            new { doors = new[] { world.ReferenceOf(door) } }, new { mode = "start" });

        Assert.Equal(0, puzzle.ActivateCalls);
        Assert.Equal(CommandStatuses.Succeeded, started.Status);
        Assert.Equal(CommitStates.Confirmed, started.CommitState);
        Assert.Equal("door-alarm-already-active", DoorActionWorld.Field(started, "code"));

        puzzle.IsActive = false;
        var stopped = world.Run(world.Commands.HandleAlarm,
            new { doors = new[] { world.ReferenceOf(door) } }, new { mode = "stop" });

        Assert.Equal(0, puzzle.DeactivateCalls);
        Assert.Equal("door-alarm-not-active", DoorActionWorld.Field(stopped, "code"));
    }

    // ---- the host gate -------------------------------------------------------------------------------

    [Fact]
    public void a_command_dispatch_that_is_not_the_host_is_refused_before_any_door_is_looked_up()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);

        foreach (var handler in new CommandHandler[] { world.Commands.HandleOpen, world.Commands.HandleClose, world.Commands.HandleAlarm })
        {
            var result = world.Run(handler, new { doors = new[] { world.ReferenceOf(door) } },
                new { bypass_policy = "respect", occupancy_policy = "block", force_policy = "normal", mode = "start" }, isHost: false);

            Assert.Equal(CommandStatuses.Rejected, result.Status);
            Assert.Equal(CommitStates.None, result.CommitState);
            Assert.Equal(DoorActionCommands.AuthorityCode, result.Code);
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
            new { doors = new[] { world.Terminal(1) } }, new { occupancy_policy = "block", force_policy = "normal" });
        Assert.Equal(DoorActionCommands.KindCode, DoorActionWorld.Field(terminal, "code"));

        var player = world.Run(world.Commands.HandleClose,
            new { doors = new[] { world.Player("1") } }, new { occupancy_policy = "block", force_policy = "normal" });
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
    public void crush_and_force_closes_are_refused_by_name()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1, eDoorStatus.Open);
        var request = new { doors = new[] { world.ReferenceOf(door) } };

        var crush = world.Run(world.Commands.HandleClose, request,
            new { occupancy_policy = "crush", force_policy = "normal" });
        var force = world.Run(world.Commands.HandleClose, request,
            new { occupancy_policy = "block", force_policy = "force" });

        Assert.Equal(CommandStatuses.Rejected, crush.Status);
        Assert.Equal(ForgeMap.Native.DoorActions.CrushUnsupported, DoorActionWorld.Field(crush, "code"));
        Assert.Equal(ForgeMap.Native.DoorActions.ForceCloseUnsupported, DoorActionWorld.Field(force, "code"));
        Assert.Equal(0, door.OpenCloseCalls);
    }

    [Fact]
    public void a_door_without_a_lock_component_or_an_alarm_is_refused_by_the_alarm_row()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var bare = world.Door(1, withLocks: false);
        var scan = world.Door(2, eDoorStatus.Closed_LockedWithChainedPuzzle);
        var alarmer = world.Door(3, eDoorStatus.Closed_LockedWithChainedPuzzle_Alarm);
        DoorActionWorld.Locks(alarmer).m_hasAlarm = true;

        var result = world.Run(world.Commands.HandleAlarm, new
        {
            doors = new[] { world.ReferenceOf(bare), world.ReferenceOf(scan), world.ReferenceOf(alarmer) }
        }, new { mode = "start" });

        var rows = DoorActionWorld.Rows(result);
        Assert.Equal(ForgeMap.Native.DoorActions.NoLockComponent, rows[0].GetProperty("code").GetString());
        Assert.Equal(ForgeMap.Native.DoorActions.NotAnAlarm, rows[1].GetProperty("code").GetString());
        Assert.Equal(ForgeMap.Native.DoorActions.NoAlarmPuzzle, rows[2].GetProperty("code").GetString());
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
        Assert.Equal(new[] { "target", "status", "committed", "code", "duration", "target_count" },
            row.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("duration").ValueKind);

        // The alarm row's own door needs an alarm to exist before its result row can be read.
        var locks = DoorActionWorld.Locks(door);
        locks.m_hasAlarm = true;
        locks.ChainedPuzzleToSolve = new ChainedPuzzles.ChainedPuzzleInstance();
        var close = world.Run(world.Commands.HandleClose,
            new { doors = new[] { world.ReferenceOf(door) } }, new { occupancy_policy = "block", force_policy = "normal" });
        Assert.Equal(new[] { "target", "status", "committed", "code", "target_count" },
            DoorActionWorld.Row(close).EnumerateObject().Select(p => p.Name).ToArray());

        var alarm = world.Run(world.Commands.HandleAlarm,
            new { doors = new[] { world.ReferenceOf(door) } }, new { mode = "start" });
        Assert.Equal(new[] { "target", "status", "committed", "code", "target_count" },
            DoorActionWorld.Row(alarm).EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void a_requested_duration_is_refused_by_name()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);

        var result = world.Run(world.Commands.HandleOpen,
            new { doors = new[] { world.ReferenceOf(door) }, duration = 120 }, new { bypass_policy = "respect" });

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(DoorActionCommands.DurationCode, result.Code);
        Assert.Empty(world.LookedUp);
        Assert.Equal(0, door.OpenCloseCalls);
    }

    [Fact]
    public void an_author_named_alarm_resource_is_refused_by_name()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1, eDoorStatus.Closed_LockedWithChainedPuzzle_Alarm);
        var locks = DoorActionWorld.Locks(door);
        locks.m_hasAlarm = true;
        var puzzle = new ChainedPuzzles.ChainedPuzzleInstance();
        locks.ChainedPuzzleToSolve = puzzle;

        var result = world.Run(world.Commands.HandleAlarm, new
        {
            doors = new[] { world.ReferenceOf(door) },
            alarm = new { resourceKind = "encounter", resourceId = "alarm-3" }
        }, new { mode = "start" });

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(DoorActionCommands.AlarmResourceCode, result.Code);
        Assert.Equal(0, puzzle.ActivateCalls);
    }

    [Fact]
    public void an_unknown_policy_member_is_refused_by_name()
    {
        using var world = new DoorActionWorld();
        world.Start();
        var door = world.Door(1);
        var request = new { doors = new[] { world.ReferenceOf(door) } };

        Assert.Equal(DoorActionCommands.PolicyCode,
            world.Run(world.Commands.HandleOpen, request, new { bypass_policy = "glue" }).Code);
        Assert.Equal(DoorActionCommands.PolicyCode,
            world.Run(world.Commands.HandleClose, request, new { occupancy_policy = "block", force_policy = "shove" }).Code);
        Assert.Equal(DoorActionCommands.PolicyCode,
            world.Run(world.Commands.HandleAlarm, request, new { mode = "toggle" }).Code);
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
}
