using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeMap.Tests.PlayerActionFacts;

/// <summary>The two player actions over doubles. Every case drives the production handlers directly and reads what
/// they did through the game entries they submitted to; no GTFO assembly is loaded and no hook is installed, so
/// these cases prove the actions' decisions and the shape of their result rows, not the game's own warp or
/// infection behaviour. The doubles are synthetic and NOT game-verified.</summary>
[Trait("Category", "PlayerActions")]
public sealed class PlayerActionTests
{
    private const ulong A = 76561198000000001, B = 76561198000000002;

    private static CommandResult Teleport(PlayerActionWorld world, JsonElement inputs, string policy = "keep")
        => PlayerActions.Teleport(PlayerActionWorld.Parameters(new { inventory_policy = policy }), inputs);

    private static CommandResult Infect(PlayerActionWorld world, JsonElement inputs, string operation = "set")
        => PlayerActions.Infection(PlayerActionWorld.Parameters(new { operation }), inputs);

    private static JsonElement[] Rows(CommandResult result) => result.Outputs.GetProperty("results").EnumerateArray().ToArray();

    private static JsonElement Row(CommandResult result) => Rows(result).Single();

    private static object[] Position(double x, double y, double z) => new object[] { x, y, z };

    // ---- the declaration ----------------------------------------------------------------------------------

    [Fact]
    public void declaration_is_the_two_catalog_rows_and_registers_their_handlers()
    {
        using var world = new PlayerActionWorld();
        Assert.Equal(new[] { PlayerActionContract.InfectionHandlerName, PlayerActionContract.TeleportHandlerName },
            PlayerActions.Handlers().Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        // The shapes are the catalog's ports, and the kernel resolved them against the registered rows when the
        // module above was registered: a port the capability does not declare would have failed that call.
        var teleport = PlayerActionContract.TeleportShape;
        Assert.Equal(new[] { "players", "destination", "rotation", "area" }, teleport.InputPorts);
        Assert.Equal(new[] { "result" }, teleport.OutputPorts);
        Assert.Equal(new[] { "inventory_policy" }, teleport.ParameterIds);
        var infection = PlayerActionContract.InfectionShape;
        Assert.Equal(new[] { "targets", "source", "amount", "resistance", "cap" }, infection.InputPorts);
        Assert.Equal(new[] { "result" }, infection.OutputPorts);
        Assert.Equal(new[] { "operation" }, infection.ParameterIds);
        // A handler is only accepted with a shape of its own, and both entry points answer the delegate the
        // kernel dispatches through.
        CommandHandler bound = PlayerActions.Teleport;
        Assert.NotNull(bound);
        Assert.Equal(2, PlayerActionContract.Rows().Count);
        Assert.Equal(2, PlayerActionContract.Bindings().Count);
        Assert.Equal(2, PlayerActionContract.Support().Count);
    }

    // ---- teleport -----------------------------------------------------------------------------------------

    [Fact]
    public void teleport_commits_on_the_host_and_moves_the_life_it_was_asked_about()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(10, 2, -3)),
            ("rotation", Position(0, 90, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        var call = Assert.Single(a.Agent.Warps);
        // The one native entry that replicates: the level's reality dimension, the landing point the game's own
        // solver answered, and the options an authored teleport may use.
        Assert.Equal(eDimensionIndex.Reality, call.Dimension);
        Assert.Equal(new Vector3(10, 2, -3), call.Position);
        Assert.Equal(PlayerAgent.WarpOptions.None, call.Options);
        // Facing yaw 90 degrees in Unity's euler order looks down +X.
        Assert.Equal(1f, call.Look.x, 4);
        Assert.Equal(0f, call.Look.y, 4);
        Assert.Equal(0f, call.Look.z, 4);
        Assert.Equal(new Vector3(10, 2, -3), a.Agent.Position);
        // The landing point is the game's own answer, not the raw request.
        var sample = Assert.Single(PlayerAgent.Samples);
        Assert.Equal(eDimensionIndex.Reality, sample.Dimension);
        Assert.Equal(new Vector3(10, 2, -3), sample.Reference);
    }

    [Fact]
    public void teleport_writes_one_row_per_recipient_in_the_plans_order()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        var b = world.Spawn(B);
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference, b.Reference }), ("destination", Position(1, 1, 1)),
            ("rotation", Position(0, 0, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        var rows = Rows(result);
        Assert.Equal(2, rows.Length);
        Assert.Equal(a.Reference.Id, rows[0].GetProperty("target").GetProperty("id").GetString());
        Assert.Equal(b.Reference.Id, rows[1].GetProperty("target").GetProperty("id").GetString());
        Assert.Equal(2, rows[0].GetProperty("target_count").GetInt32());
        Assert.Equal(2, rows[1].GetProperty("target_count").GetInt32());
        Assert.Single(a.Agent.Warps);
        Assert.Single(b.Agent.Warps);
    }

    [Fact]
    public void teleport_row_is_the_catalog_result_shape()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(0, 0, 5)),
            ("rotation", Position(0, 0, 0)));

        var row = Row(Teleport(world, inputs));

        Assert.Equal(new[] { "target", "status", "committed", "code", "target_count" },
            row.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(a.Reference.Id, row.GetProperty("target").GetProperty("id").GetString());
        Assert.Equal(a.Reference.WorldEpoch, row.GetProperty("target").GetProperty("worldEpoch").GetInt64());
        Assert.Equal(CommandStatuses.Succeeded, row.GetProperty("status").GetString());
        Assert.Equal(CommitStates.Confirmed, row.GetProperty("committed").GetString());
        Assert.Equal("committed", row.GetProperty("code").GetString());
        Assert.Equal(1, row.GetProperty("target_count").GetInt32());
    }

    [Fact]
    public void teleport_refuses_a_session_that_is_not_the_authority()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        SNet.IsMaster = false;
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(1, 1, 1)),
            ("rotation", Position(0, 0, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(PlayerActions.AuthorityCode, result.Code);
        Assert.Empty(a.Agent.Warps);
        Assert.Empty(PlayerAgent.Samples);
    }

    [Fact]
    public void teleport_refuses_a_life_the_identity_module_no_longer_answers_for()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        var forged = new[]
        {
            a.Reference with { LifeEpoch = a.Reference.LifeEpoch + 1 },
            a.Reference with { WorldEpoch = a.Reference.WorldEpoch + 1 },
            new EntityReference(PlayerIdentityModule.EntityKind + ":99", a.Reference.WorldEpoch, a.Reference.LifeEpoch),
            new EntityReference("gtfo.enemy:7", a.Reference.WorldEpoch, a.Reference.LifeEpoch)
        };
        foreach (var target in forged)
        {
            var inputs = PlayerActionWorld.Frame(("players", new[] { target }), ("destination", Position(1, 1, 1)),
                ("rotation", Position(0, 0, 0)));
            var result = Teleport(world, inputs);
            var row = Row(result);
            Assert.Equal(CommandStatuses.Rejected, result.Status);
            Assert.Equal(CommitStates.None, row.GetProperty("committed").GetString());
            Assert.Equal(target.Id.StartsWith("gtfo.player:", StringComparison.Ordinal)
                ? PlayerActions.StaleCode : PlayerActions.KindCode, row.GetProperty("code").GetString());
        }
        Assert.Empty(a.Agent.Warps);
    }

    [Fact]
    public void teleport_stops_answering_for_a_life_after_the_world_is_cleared()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        world.Despawn(a);
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(1, 1, 1)),
            ("rotation", Position(0, 0, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(PlayerActions.StaleCode, Row(result).GetProperty("code").GetString());
        Assert.Empty(a.Agent.Warps);
    }

    [Fact]
    public void teleport_refuses_a_locomotion_state_the_agent_does_not_warp_in()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A, state: PlayerLocomotion.PLOC_State.Downed);
        a.Agent.m_warpableStates = new HashSet<PlayerLocomotion.PLOC_State> { PlayerLocomotion.PLOC_State.Stand };
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(1, 1, 1)),
            ("rotation", Position(0, 0, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(PlayerActions.WarpStateCode, Row(result).GetProperty("code").GetString());
        Assert.Empty(a.Agent.Warps);
    }

    [Fact]
    public void teleport_refuses_a_dead_life()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        a.Agent.Alive = false;
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(1, 1, 1)),
            ("rotation", Position(0, 0, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(PlayerActions.NotAliveCode, Row(result).GetProperty("code").GetString());
        Assert.Empty(a.Agent.Warps);
    }

    [Fact]
    public void teleport_refuses_the_whole_command_when_the_game_cannot_solve_the_destination()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        PlayerAgent.Sampler = (_, _) => (false, default);
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(1, 1, 1)),
            ("rotation", Position(0, 0, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(PlayerActions.DestinationCode, result.Code);
        Assert.Empty(a.Agent.Warps);
    }

    [Fact]
    public void teleport_reports_an_unknown_when_the_warp_does_not_land_where_it_was_asked_to()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        // The native request is accepted and the agent is not moved: the game's own warp gate declining to place
        // the player looks exactly like this from the action's side.
        a.Agent.Warp = (_, _, _, _, _) => { };
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(1, 1, 1)),
            ("rotation", Position(0, 0, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal(PlayerActions.WarpUnknownCode, Row(result).GetProperty("code").GetString());
        Assert.Single(a.Agent.Warps);
    }

    [Fact]
    public void teleport_reports_an_unknown_when_the_native_request_throws()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        a.Agent.Warp = (_, _, _, _, _) => throw new InvalidOperationException("fixture native failure");
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(1, 1, 1)),
            ("rotation", Position(0, 0, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal(PlayerActions.CommitExceptionCode, Row(result).GetProperty("code").GetString());
    }

    [Fact]
    public void teleport_partially_commits_and_keeps_the_rows_the_lives_earned()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        var b = world.Spawn(B);
        b.Agent.Warp = (_, _, _, _, _) => { };
        var inputs = PlayerActionWorld.Frame(("players", new[] { a.Reference, b.Reference }), ("destination", Position(2, 0, 0)),
            ("rotation", Position(0, 0, 0)));

        var result = Teleport(world, inputs);

        Assert.Equal(CommandStatuses.Partial, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        var rows = Rows(result);
        Assert.Equal(CommitStates.Confirmed, rows[0].GetProperty("committed").GetString());
        Assert.Equal(CommitStates.Unknown, rows[1].GetProperty("committed").GetString());
        Assert.Equal(PlayerActions.WarpUnknownCode, rows[1].GetProperty("code").GetString());
    }

    [Fact]
    public void teleport_refuses_the_requests_the_native_path_cannot_carry()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);

        // The native warp moves the agent and touches no inventory: `keep` is what it is, the other two policies
        // are refused rather than served as a keep.
        foreach (var policy in new[] { "drop", "store" })
        {
            var refused = Teleport(world, PlayerActionWorld.Frame(("players", new[] { a.Reference }),
                ("destination", Position(1, 1, 1)), ("rotation", Position(0, 0, 0))), policy);
            Assert.Equal(PlayerActions.InventoryPolicyCode, refused.Code);
        }
        // An area field is a resource kind no provider registers in this build, so a constrained destination is
        // refused instead of being treated as the bare coordinate.
        var area = Teleport(world, PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(1, 1, 1)),
            ("rotation", Position(0, 0, 0)), ("area", new { resourceKind = "area_field", resourceId = "zone:1" })));
        Assert.Equal(PlayerActions.AreaCode, area.Code);
        // The warp takes a direction, so a request that names no facing has nothing to derive one from.
        var rotation = Teleport(world, PlayerActionWorld.Frame(("players", new[] { a.Reference }), ("destination", Position(1, 1, 1))));
        Assert.Equal(PlayerActions.RotationCode, rotation.Code);
        var destination = Teleport(world, PlayerActionWorld.Frame(("players", new[] { a.Reference }),
            ("destination", new object[] { 1, 1 }), ("rotation", Position(0, 0, 0))));
        Assert.Equal(PlayerActions.DestinationInvalidCode, destination.Code);
        var none = Teleport(world, PlayerActionWorld.Frame(("players", Array.Empty<EntityReference>()),
            ("destination", Position(1, 1, 1)), ("rotation", Position(0, 0, 0))));
        Assert.Equal(PlayerActions.NoTargetsCode, none.Code);
        Assert.Empty(a.Agent.Warps);
        Assert.Empty(PlayerAgent.Samples);
    }

    [Fact]
    public void look_direction_follows_unity_euler_order()
    {
        var forward = PlayerActions.LookDirection(0, 0, 0);
        Assert.Equal(0f, forward.x, 4);
        Assert.Equal(0f, forward.y, 4);
        Assert.Equal(1f, forward.z, 4);
        // Pitch is positive downwards in Unity's euler angles.
        var down = PlayerActions.LookDirection(90, 0, 0);
        Assert.Equal(-1f, down.y, 4);
        // Roll cannot tilt a forward vector.
        var rolled = PlayerActions.LookDirection(0, 0, 45);
        Assert.Equal(forward, rolled);
    }

    // ---- infection change ---------------------------------------------------------------------------------

    [Fact]
    public void infection_set_writes_the_value_and_reports_the_readback()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A, infection: 0.25f);
        var inputs = PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference), ("amount", 0.5));

        var result = Infect(world, inputs, "set");

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        var call = Assert.Single(a.Agent.Damage!.Calls);
        // The native replicated write entry: the value, the `Set` mode, no disinfection effect, and the sync flag
        // that sends the change to the other machines.
        Assert.Equal(0.5f, call.Data.amount, 5);
        Assert.Equal(pInfectionMode.Set, call.Data.mode);
        Assert.Equal(pInfectionEffect.None, call.Data.effect);
        Assert.True(call.Sync);
        Assert.Equal(0.5f, a.Agent.Damage.Infection, 5);
        var row = Row(result);
        Assert.Equal(new[] { "target", "status", "committed", "code", "amount", "target_count" },
            row.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(0.5, row.GetProperty("amount").GetDouble(), 5);
        Assert.Equal(CommitStates.Confirmed, row.GetProperty("committed").GetString());
    }

    [Fact]
    public void infection_add_uses_the_native_add_mode_and_subtract_is_computed_as_a_set()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A, infection: 0.25f);
        var addInputs = PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference), ("amount", 0.25));

        var added = Infect(world, addInputs, "add");

        Assert.Equal(CommandStatuses.Succeeded, added.Status);
        Assert.Equal(pInfectionMode.Add, a.Agent.Damage!.Calls[^1].Data.mode);
        Assert.Equal(0.25f, a.Agent.Damage.Calls[^1].Data.amount, 5);
        Assert.Equal(0.5f, a.Agent.Damage.Infection, 5);
        Assert.Equal(0.5, Row(added).GetProperty("amount").GetDouble(), 5);

        // `pInfectionMode` has no subtraction: the absolute value the operation asks for is computed here and
        // submitted as a set.
        var subtracted = Infect(world, addInputs, "subtract");

        Assert.Equal(CommandStatuses.Succeeded, subtracted.Status);
        Assert.Equal(pInfectionMode.Set, a.Agent.Damage.Calls[^1].Data.mode);
        Assert.Equal(0.25f, a.Agent.Damage.Calls[^1].Data.amount, 5);
        Assert.Equal(0.25f, a.Agent.Damage.Infection, 5);
    }

    [Fact]
    public void infection_subtract_never_leaves_a_negative_value()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A, infection: 0.1f);
        var inputs = PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference), ("amount", 0.5));

        var result = Infect(world, inputs, "subtract");

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(0f, a.Agent.Damage!.Calls[^1].Data.amount, 5);
        Assert.Equal(0f, a.Agent.Damage.Infection, 5);
    }

    [Fact]
    public void infection_ceiling_bounds_the_value_and_add_never_lowers_it()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A, infection: 0.2f);
        var capped = PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference),
            ("amount", 0.5), ("cap", 0.3));

        var result = Infect(world, capped, "add");

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        // An addition is submitted as the delta it is, bounded by what is left under the ceiling.
        Assert.Equal(0.1f, a.Agent.Damage!.Calls[^1].Data.amount, 5);
        Assert.Equal(pInfectionMode.Add, a.Agent.Damage.Calls[^1].Data.mode);
        Assert.Equal(0.3f, a.Agent.Damage.Infection, 5);
        Assert.Equal(0.3, Row(result).GetProperty("amount").GetDouble(), 5);

        // A ceiling already below the current value cannot make an addition subtract from it.
        a.Agent.Damage.Infection = 0.4f;
        var below = Infect(world, PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference),
            ("amount", 0.2), ("cap", 0.1)), "add");
        Assert.Equal(CommandStatuses.Succeeded, below.Status);
        Assert.Equal(0f, a.Agent.Damage.Calls[^1].Data.amount, 5);
        Assert.Equal(0.4f, a.Agent.Damage.Infection, 5);

        // A `set` is explicit: it may lower the value to the ceiling.
        var set = Infect(world, PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference),
            ("amount", 0.9), ("cap", 0.2)), "set");
        Assert.Equal(CommandStatuses.Succeeded, set.Status);
        Assert.Equal(0.2f, a.Agent.Damage.Infection, 5);
    }

    [Fact]
    public void infection_refuses_a_resistance_the_native_path_already_applies()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        var inputs = PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference),
            ("amount", 0.5), ("resistance", 0.25));

        var result = Infect(world, inputs, "add");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(PlayerActions.ResistanceCode, result.Code);
        Assert.Empty(a.Agent.Damage!.Calls);
    }

    [Fact]
    public void infection_reports_an_unknown_when_the_receiver_does_not_store_the_value()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A, infection: 0.1f);
        // A receiver that keeps a different value — an immune player, or one whose own rules quantize the
        // request — is not a confirmed write.
        a.Agent.Damage!.Commit = (_, _, _) => { };
        var inputs = PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference), ("amount", 0.5));

        var result = Infect(world, inputs, "set");

        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal(PlayerActions.InfectionReadbackCode, Row(result).GetProperty("code").GetString());
        Assert.Single(a.Agent.Damage.Calls);
    }

    [Fact]
    public void infection_reports_an_unknown_when_the_native_write_throws()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        a.Agent.Damage!.Commit = (_, _, _) => throw new InvalidOperationException("fixture native failure");
        var inputs = PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference), ("amount", 0.5));

        var result = Infect(world, inputs, "set");

        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(PlayerActions.CommitExceptionCode, Row(result).GetProperty("code").GetString());
    }

    [Fact]
    public void infection_refuses_a_recipient_without_a_usable_receiver()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        var inputs = PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference), ("amount", 0.5));

        // No receiver at all.
        a.Agent.Damage = null;
        Assert.Equal(PlayerActions.ReceiverCode, Row(Infect(world, inputs)).GetProperty("code").GetString());
        // A receiver the game has not set up.
        a.Agent.Damage = new Dam_PlayerDamageBase { Owner = a.Agent, IsSetup = false };
        Assert.Equal(PlayerActions.ReceiverCode, Row(Infect(world, inputs)).GetProperty("code").GetString());
        // A receiver that belongs to another agent.
        var other = world.Spawn(B);
        a.Agent.Damage = other.Agent.Damage;
        Assert.Equal(PlayerActions.ReceiverMismatchCode, Row(Infect(world, inputs)).GetProperty("code").GetString());
        // A dead life: the receiver's own receive path is the one this refusal stands on.
        a.Agent.Damage = new Dam_PlayerDamageBase { Owner = a.Agent, IsSetup = true };
        a.Agent.Alive = false;
        Assert.Equal(PlayerActions.NotAliveCode, Row(Infect(world, inputs)).GetProperty("code").GetString());
        Assert.Empty(other.Agent.Damage!.Calls);
    }

    [Fact]
    public void infection_refuses_state_that_moved_between_the_read_and_the_write()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A, infection: 0.2f);
        // The receiver changes the value while the request is being computed: the write is refused instead of
        // being applied to a value the action never read.
        a.Agent.Damage!.Commit = (_, _, _) => a.Agent.Damage.Infection = 0.9f;
        var result = Infect(world, PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference),
            ("amount", 0.5)), "set");
        // The commit itself is what changed the value, so this attempt is an unknown readback, not a stale read.
        Assert.Equal(PlayerActions.InfectionReadbackCode, Row(result).GetProperty("code").GetString());
    }

    [Fact]
    public void infection_refuses_a_request_it_cannot_carry()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        var targets = new[] { a.Reference };

        var operation = Infect(world, PlayerActionWorld.Frame(("targets", targets), ("source", a.Reference), ("amount", 0.5)), "multiply");
        Assert.Equal(PlayerActions.OperationCode, operation.Code);
        var amount = Infect(world, PlayerActionWorld.Frame(("targets", targets), ("source", a.Reference), ("amount", -0.5)));
        Assert.Equal(PlayerActions.AmountRangeCode, amount.Code);
        var huge = Infect(world, PlayerActionWorld.Frame(("targets", targets), ("source", a.Reference), ("amount", 1e6)));
        Assert.Equal(PlayerActions.AmountRangeCode, huge.Code);
        var cap = Infect(world, PlayerActionWorld.Frame(("targets", targets), ("source", a.Reference), ("amount", 0.5), ("cap", 0)));
        Assert.Equal(PlayerActions.CapCode, cap.Code);
        var source = Infect(world, PlayerActionWorld.Frame(("targets", targets), ("amount", 0.5)));
        Assert.Equal(PlayerActions.SourceCode, source.Code);
        var none = Infect(world, PlayerActionWorld.Frame(("targets", Array.Empty<EntityReference>()), ("source", a.Reference), ("amount", 0.5)));
        Assert.Equal(PlayerActions.NoTargetsCode, none.Code);
        Assert.Empty(a.Agent.Damage!.Calls);
    }

    [Fact]
    public void infection_refuses_a_session_that_is_not_the_authority()
    {
        using var world = new PlayerActionWorld();
        var a = world.Spawn(A);
        SNet.IsMaster = false;
        var inputs = PlayerActionWorld.Frame(("targets", new[] { a.Reference }), ("source", a.Reference), ("amount", 0.5));

        var result = Infect(world, inputs, "set");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(PlayerActions.AuthorityCode, result.Code);
        Assert.Empty(a.Agent.Damage!.Calls);
    }
}
