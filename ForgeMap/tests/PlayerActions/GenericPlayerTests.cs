using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeMap.Tests.PlayerActionFacts;
using ForgeRuntime.Framework;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeMap.Tests.GenericPlayerFacts;

/// <summary>One spawned player and the native halves the generic-player rows write through.</summary>
internal sealed record GenericFixture(SNet_Player Player, PlayerAgent Agent, EntityReference Reference);

/// <summary>One case's world for the generic-player batch: a kernel whose one registration is exactly the
/// declaration this batch adds — its capability rows, binding rows, support rows, handler shapes and evaluators —
/// plus the identity surface the handlers resolve recipients through. The handlers are the production static entry
/// points, so a case drives them directly and reads what they did through the game doubles.</summary>
internal sealed class GenericPlayerWorld : IDisposable
{
    private static long _world;
    private readonly RuntimeModuleHandle _registration;

    internal RuntimeKernel Kernel { get; }
    internal PlayerIdentityModule Identity { get; private set; } = null!;

    internal GenericPlayerWorld()
    {
        PlayerManager.Reset();
        PlayerAgent.ResetStatics();
        ScreenLiquidManager.Reset();
        SNet.IsMaster = true;
        // The game's clock is a process-wide static, and the movement-state read measures against it.
        Time.time = 500f;
        Kernel = new RuntimeKernel(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        Kernel.BeginWorld(++_world);
        _registration = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
            GenericPlayerActions.Handlers(), GenericPlayerActions.Support(),
            new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = reference => PlayerIdentityModule.Current is { } half && half.IsCurrent(reference)
            })
        {
            Shapes = GenericPlayerActions.Shapes(),
            Evaluators = GenericPlayerActions.Evaluators(),
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = instance => PlayerIdentityModule.Current?.ResolveInstance(instance)
            },
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = reference => PlayerIdentityModule.Current?.Observe(reference)
            },
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = () => PlayerIdentityModule.Current is { } half && half.IsRegistered
                    ? half.CurrentPlayers() : throw new RuntimeContractException("player-module-unavailable", "no identity")
            }
        }, RuntimeLogLevel.Off);
        Identity = new PlayerIdentityModule(_registration, Kernel, () => true, _ => { }, _ => { });
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, true);
    }

    /// <summary>The provider declaration this batch's rows are registered under, built the way the real Map
    /// registration builds it: the rows and bindings come from the contracts themselves, so a row whose graph or
    /// binding is malformed fails here exactly as it would at startup.</summary>
    private static string Registry() => RuntimeJson.From(new
    {
        providers = new[] { new { id = ModuleDefinition.ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Capabilities,
        bindings = Bindings
    }).GetRawText();

    private static readonly object[] Capabilities = new object[] { CombatImpulseContract.Row() }
        .Concat(new object[] { PlayerStaminaContract.Row() })
        .Concat(new object[] { PlayerMovementStateContract.CapabilityRow() })
        .Concat(PresentationActionContract.CapabilityRows())
        .ToArray();

    private static readonly object[] Bindings = new object[] { CombatImpulseContract.BindingRow() }
        .Concat(new object[] { PlayerStaminaContract.BindingRow() })
        .Concat(new object[] { PlayerMovementStateContract.BindingRow() })
        .Concat(PresentationActionContract.BindingRows())
        .ToArray();

    /// <summary>One spawned player with a locomotion machine, a stamina value and a camera — the three halves the
    /// batch writes through — read back through the identity module's own reconcile.</summary>
    internal GenericFixture Spawn(ulong lookup, PlayerLocomotion.PLOC_State state = PlayerLocomotion.PLOC_State.Stand,
        float stamina = 1f, float stateEnteredAt = 490f, Vector3 position = default)
    {
        int slot = PlayerManager.PlayerAgentsInLevel.Count;
        var player = new SNet_Player { Lookup = lookup, SlotIndex = slot };
        var agent = new PlayerAgent { Owner = player, PlayerSlotIndex = slot };
        agent.Position = position;
        agent.Forward = new Vector3(0f, 0f, 1f);
        agent.Locomotion = new PlayerLocomotion { m_currentStateEnum = state, m_changeStateTime = stateEnteredAt };
        agent.Stamina = new PlayerStamina();
        agent.Stamina.Stamina = stamina;
        agent.Stamina.Writes.Clear();
        agent.FPSCamera = new FPSCamera();
        player.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
        PlayerManager.PlayerAgentsInLevel.Add(agent);
        Identity.Reconcile();
        return new GenericFixture(player, agent, Kernel.ResolveEntityInstance(PlayerIdentityModule.EntityKind, player)!);
    }

    public void Dispose()
    {
        Identity.Dispose();
        _registration.Dispose();
    }
}

public sealed class GenericPlayerTests
{
    private static JsonElement[] Rows(CommandResult result) => result.Outputs.GetProperty("results").EnumerateArray().ToArray();

    private static string RowCode(CommandResult result) => Rows(result)[0].GetProperty("code").GetString()!;

    // ---------------------------------------------------------------- registration

    [Fact]
    public void The_batch_bodies_are_declared_by_the_map_registration()
    {
        foreach (var handler in new[]
                 {
                     CombatImpulseContract.HandlerName, PlayerStaminaContract.HandlerName,
                     PresentationActionContract.CameraShakeHandlerName, PresentationActionContract.ScreenLiquidHandlerName
                 })
            Assert.Contains(handler, ModuleRegistration.CommandHandlers);
        Assert.Contains(PlayerMovementStateContract.HandlerName, ModuleRegistration.EvaluatorHandlers);
        // The movement-state row is an observe row, so it is answered by an evaluator and never by a command body.
        Assert.DoesNotContain(PlayerMovementStateContract.HandlerName, ModuleRegistration.CommandHandlers);
    }

    [Fact]
    public void The_batch_rows_are_composed_into_the_map_declaration()
    {
        var capabilityIds = RuntimeJson.From(ModuleRegistration.Capabilities).EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()).ToArray();
        foreach (var id in new[]
                 {
                     CombatImpulseContract.CapabilityId, PlayerStaminaContract.CapabilityId,
                     PlayerMovementStateContract.CapabilityId, PresentationActionContract.CameraShakeCapabilityId,
                     PresentationActionContract.ScreenLiquidCapabilityId
                 })
            Assert.Contains(id, capabilityIds);
        var bindingIds = RuntimeJson.From(ModuleRegistration.Bindings).EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()).ToArray();
        foreach (var id in new[]
                 {
                     CombatImpulseContract.BindingId, PlayerStaminaContract.BindingId,
                     PlayerMovementStateContract.BindingId, PresentationActionContract.CameraShakeBindingId,
                     PresentationActionContract.ScreenLiquidBindingId
                 })
            Assert.Contains(id, bindingIds);
        // Every declared row registers against its own graph and binding: the world's kernel would refuse the
        // registration otherwise, so building one is the check.
        using var world = new GenericPlayerWorld();
        Assert.Equal(4, GenericPlayerActions.Handlers().Count);
        Assert.NotNull(world.Kernel);
    }

    // ---------------------------------------------------------------- impulse

    [Fact]
    public void Impulse_pushes_a_player_along_the_authored_direction()
    {
        using var world = new GenericPlayerWorld();
        var target = world.Spawn(1UL, position: new Vector3(1f, 0f, 3f));
        var result = GenericPlayerActions.Impulse(
            PlayerActionWorld.Parameters(new { horizontal = 5.0, vertical = 2.0 }),
            PlayerActionWorld.Frame(("targets", new[] { target.Reference }), ("direction", new[] { 1.0, 0.0, 0.0 })));

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        var force = Assert.Single(target.Agent.Locomotion!.Pushes);
        Assert.Equal(5f, force.x, 3);
        Assert.Equal(2f, force.y, 3);
        Assert.Equal(0f, force.z, 3);
        var row = Rows(result)[0];
        Assert.Equal(new[] { "target", "status", "committed", "code", "strength", "target_count" },
            row.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("succeeded", row.GetProperty("status").GetString());
        Assert.Equal(GenericPlayerActions.ImpulseAppliedCode, row.GetProperty("code").GetString());
    }

    [Fact]
    public void Impulse_derives_the_direction_from_the_source_and_excludes_it()
    {
        using var world = new GenericPlayerWorld();
        var source = world.Spawn(1UL, position: new Vector3(0f, 0f, 0f));
        var target = world.Spawn(2UL, position: new Vector3(0f, 0f, 4f));
        var result = GenericPlayerActions.Impulse(
            PlayerActionWorld.Parameters(new { horizontal = 3.0 }),
            PlayerActionWorld.Frame(("targets", new[] { target.Reference, source.Reference }), ("source", source.Reference)));

        Assert.Equal(CommandStatuses.Partial, result.Status);
        var force = Assert.Single(target.Agent.Locomotion!.Pushes);
        Assert.Equal(3f, force.z, 3);
        Assert.Empty(source.Agent.Locomotion!.Pushes);
        Assert.Equal(GenericPlayerActions.ImpulseSourceExcludedCode, Rows(result)[1].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(0.0, 0.0, GenericPlayerActions.ImpulseStrengthRequiredCode)]
    [InlineData(500.0, 0.0, GenericPlayerActions.ImpulseStrengthRangeCode)]
    public void Impulse_refuses_a_strength_it_cannot_apply(double horizontal, double vertical, string code)
    {
        using var world = new GenericPlayerWorld();
        var target = world.Spawn(1UL);
        var result = GenericPlayerActions.Impulse(
            PlayerActionWorld.Parameters(new { horizontal, vertical }),
            PlayerActionWorld.Frame(("targets", new[] { target.Reference }), ("direction", new[] { 1.0, 0.0, 0.0 })));
        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public void Impulse_refuses_a_request_with_no_direction_and_no_source()
    {
        using var world = new GenericPlayerWorld();
        var target = world.Spawn(1UL);
        var result = GenericPlayerActions.Impulse(
            PlayerActionWorld.Parameters(new { horizontal = 1.0 }),
            PlayerActionWorld.Frame(("targets", new[] { target.Reference })));
        Assert.Equal(GenericPlayerActions.ImpulseDirectionCode, result.Code);
    }

    [Fact]
    public void Impulse_refuses_the_falloff_switches_it_cannot_honour()
    {
        using var world = new GenericPlayerWorld();
        var target = world.Spawn(1UL);
        var result = GenericPlayerActions.Impulse(
            PlayerActionWorld.Parameters(new { horizontal = 1.0, falloff_distance = true }),
            PlayerActionWorld.Frame(("targets", new[] { target.Reference }), ("direction", new[] { 1.0, 0.0, 0.0 })));
        Assert.Equal(GenericPlayerActions.ImpulseFalloffCode, result.Code);
    }

    [Fact]
    public void Impulse_refuses_an_enemy_recipient_it_cannot_resolve_and_another_kind_by_name()
    {
        using var world = new GenericPlayerWorld();
        var enemy = world.Kernel is not null
            ? new EntityReference("gtfo.enemy:7", world.Kernel.WorldEpoch, 1)
            : throw new InvalidOperationException();
        var door = new EntityReference("gtfo.map_object:door-1", world.Kernel.WorldEpoch, 1);
        var result = GenericPlayerActions.Impulse(
            PlayerActionWorld.Parameters(new { horizontal = 1.0 }),
            PlayerActionWorld.Frame(("targets", new[] { enemy, door }), ("direction", new[] { 1.0, 0.0, 0.0 })));
        Assert.Equal(GenericPlayerActions.ImpulseRecipientCode, Rows(result)[0].GetProperty("code").GetString());
        Assert.Equal(GenericPlayerActions.ImpulseTargetKindCode, Rows(result)[1].GetProperty("code").GetString());
    }

    [Fact]
    public void Impulse_refuses_a_source_of_a_kind_it_cannot_resolve()
    {
        using var world = new GenericPlayerWorld();
        var target = world.Spawn(1UL);
        var enemy = new EntityReference("gtfo.enemy:7", world.Kernel.WorldEpoch, 1);
        var result = GenericPlayerActions.Impulse(
            PlayerActionWorld.Parameters(new { horizontal = 1.0 }),
            PlayerActionWorld.Frame(("targets", new[] { target.Reference }), ("source", enemy)));
        Assert.Equal(GenericPlayerActions.ImpulseSourceCode, result.Code);
    }

    // ---------------------------------------------------------------- stamina

    [Fact]
    public void Stamina_set_writes_the_value_and_reports_the_change()
    {
        using var world = new GenericPlayerWorld();
        var target = world.Spawn(1UL, stamina: 1f);
        var result = GenericPlayerActions.StaminaChange(
            PlayerActionWorld.Parameters(new { operation = "set" }),
            PlayerActionWorld.Frame(("targets", new[] { target.Reference }), ("amount", 0.25)));

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(0.25f, Assert.Single(target.Agent.Stamina!.Writes), 4);
        Assert.Equal(-0.75, Rows(result)[0].GetProperty("amount").GetDouble(), 4);
    }

    [Theory]
    [InlineData("add", 0.5, 1.0, 0.2)]
    [InlineData("subtract", 0.5, 0.0, -0.1)]
    public void Stamina_add_and_subtract_clamp_at_the_native_range(string operation, double amount, float expected, double delta)
    {
        using var world = new GenericPlayerWorld();
        var target = world.Spawn(1UL, stamina: operation == "add" ? 0.8f : 0.1f);
        var result = GenericPlayerActions.StaminaChange(
            PlayerActionWorld.Parameters(new { operation }),
            PlayerActionWorld.Frame(("targets", new[] { target.Reference }), ("amount", amount)));

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(expected, target.Agent.Stamina!.Stamina, 4);
        Assert.Equal(delta, Rows(result)[0].GetProperty("amount").GetDouble(), 4);
    }

    [Theory]
    [InlineData("multiply", 0.2, GenericPlayerActions.OperationCode)]
    [InlineData("set", 5.0, GenericPlayerActions.AmountRangeCode)]
    public void Stamina_refuses_an_operation_or_an_amount_it_cannot_carry(string operation, double amount, string code)
    {
        using var world = new GenericPlayerWorld();
        var target = world.Spawn(1UL);
        var result = GenericPlayerActions.StaminaChange(
            PlayerActionWorld.Parameters(new { operation }),
            PlayerActionWorld.Frame(("targets", new[] { target.Reference }), ("amount", amount)));
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public void Stamina_refuses_a_recipient_that_is_not_a_player()
    {
        using var world = new GenericPlayerWorld();
        var door = new EntityReference("gtfo.map_object:door-1", world.Kernel.WorldEpoch, 1);
        var result = GenericPlayerActions.StaminaChange(
            PlayerActionWorld.Parameters(new { operation = "set" }),
            PlayerActionWorld.Frame(("targets", new[] { door }), ("amount", 0.5)));
        Assert.Equal(GenericPlayerActions.StaminaTargetKindCode, result.Code);
    }

    // ---------------------------------------------------------------- camera shake

    [Fact]
    public void Camera_shake_falls_off_with_the_distance_from_the_centre()
    {
        using var world = new GenericPlayerWorld();
        var near = world.Spawn(1UL, position: new Vector3(0f, 0f, 0f));
        var far = world.Spawn(2UL, position: new Vector3(0f, 0f, 8f));
        var result = GenericPlayerActions.CameraShake(
            // `duration` is ticks: 24 ticks is the 0.4 s the native entry is asked for.
            PlayerActionWorld.Parameters(new { duration = 24, amplitude = 2.0, frequency = 20.0, inner_radius = 0.0, radius = 10.0, direction = new[] { 0.0, 0.0, 1.0 } }),
            PlayerActionWorld.Frame(("viewers", new[] { near.Reference, far.Reference }), ("center", new[] { 0.0, 0.0, 0.0 })));

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        var nearCall = Assert.Single(near.Agent.FPSCamera!.Shakes);
        var farCall = Assert.Single(far.Agent.FPSCamera!.Shakes);
        Assert.Equal(2f, nearCall.Amplitude, 3);
        Assert.Equal(0.4f, farCall.Amplitude, 3);
        Assert.Equal(0.4f, nearCall.Duration, 3);
        Assert.Equal(20f, nearCall.Frequency, 3);
    }

    [Fact]
    public void Camera_shake_refuses_a_viewer_outside_the_outer_radius()
    {
        using var world = new GenericPlayerWorld();
        var viewer = world.Spawn(1UL, position: new Vector3(0f, 0f, 40f));
        var result = GenericPlayerActions.CameraShake(
            PlayerActionWorld.Parameters(new { duration = 24, amplitude = 2.0, inner_radius = 1.0, radius = 10.0 }),
            PlayerActionWorld.Frame(("viewers", new[] { viewer.Reference }), ("center", new[] { 0.0, 0.0, 0.0 })));

        Assert.Empty(viewer.Agent.FPSCamera!.Shakes);
        Assert.Equal(GenericPlayerActions.CameraOutOfRangeCode, RowCode(result));
    }

    [Theory]
    [InlineData(0.0, 2.0, GenericPlayerActions.CameraDurationRequiredCode)]
    [InlineData(30.0, 0.0, GenericPlayerActions.CameraAmplitudeRequiredCode)]
    [InlineData(30.0, 20.0, GenericPlayerActions.CameraAmplitudeRangeCode)]
    public void Camera_shake_refuses_parameters_outside_its_bounds(double durationTicks, double amplitude, string code)
    {
        using var world = new GenericPlayerWorld();
        var viewer = world.Spawn(1UL);
        var result = GenericPlayerActions.CameraShake(
            PlayerActionWorld.Parameters(new { duration = durationTicks, amplitude }),
            PlayerActionWorld.Frame(("viewers", new[] { viewer.Reference })));
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public void Camera_shake_refuses_a_step_that_names_no_viewer_or_a_non_player()
    {
        using var world = new GenericPlayerWorld();
        var parameters = PlayerActionWorld.Parameters(new { duration = 30, amplitude = 1.0 });
        Assert.Equal(GenericPlayerActions.ViewersRequiredCode,
            GenericPlayerActions.CameraShake(parameters, PlayerActionWorld.Frame(("viewers", Array.Empty<object>()))).Code);
        var door = new EntityReference("gtfo.map_object:door-1", world.Kernel.WorldEpoch, 1);
        Assert.Equal(GenericPlayerActions.ViewerCode,
            GenericPlayerActions.CameraShake(parameters, PlayerActionWorld.Frame(("viewers", new[] { door }))).Code);
    }

    // ---------------------------------------------------------------- screen liquid

    [Fact]
    public void Screen_liquid_applies_the_named_preset_to_the_local_viewport()
    {
        using var world = new GenericPlayerWorld();
        var viewer = world.Spawn(1UL, position: new Vector3(2f, 1f, 0f));
        var result = GenericPlayerActions.ScreenLiquid(
            PlayerActionWorld.Parameters(new { preset = "player_blood" }),
            PlayerActionWorld.Frame(("viewers", new[] { viewer.Reference })));

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        var call = Assert.Single(ScreenLiquidManager.Calls);
        Assert.Equal(ScreenLiquidSettingName.playerBlood, call.Setting);
        Assert.Equal(2f, call.Position.x, 3);
        Assert.Equal("player_blood", Rows(result)[0].GetProperty("preset").GetString());
    }

    [Theory]
    [InlineData("not_a_preset", GenericPlayerActions.LiquidPresetUnknownCode)]
    [InlineData(null, GenericPlayerActions.LiquidPresetRequiredCode)]
    public void Screen_liquid_refuses_a_preset_it_does_not_know(string? preset, string code)
    {
        using var world = new GenericPlayerWorld();
        var viewer = world.Spawn(1UL);
        var result = GenericPlayerActions.ScreenLiquid(
            PlayerActionWorld.Parameters(new { preset }),
            PlayerActionWorld.Frame(("viewers", new[] { viewer.Reference })));
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public void Screen_liquid_refuses_a_viewport_of_another_machine_and_a_declined_call()
    {
        using var world = new GenericPlayerWorld();
        var remote = world.Spawn(1UL);
        remote.Agent.IsLocallyOwned = false;
        var foreign = GenericPlayerActions.ScreenLiquid(
            PlayerActionWorld.Parameters(new { preset = "player_blood" }),
            PlayerActionWorld.Frame(("viewers", new[] { remote.Reference })));
        Assert.Empty(ScreenLiquidManager.Calls);
        Assert.Equal(GenericPlayerActions.LiquidViewerCode, RowCode(foreign));

        var local = world.Spawn(2UL);
        ScreenLiquidManager.Result = false;
        var declined = GenericPlayerActions.ScreenLiquid(
            PlayerActionWorld.Parameters(new { preset = "player_blood" }),
            PlayerActionWorld.Frame(("viewers", new[] { local.Reference })));
        Assert.Equal(GenericPlayerActions.LiquidNotAppliedCode, RowCode(declined));
    }

    // ---------------------------------------------------------------- movement state

    [Fact]
    public void Movement_state_reads_the_native_member_and_the_seconds_it_has_been_held()
    {
        using var world = new GenericPlayerWorld();
        var player = world.Spawn(1UL, PlayerLocomotion.PLOC_State.Jump, stateEnteredAt: 490f);
        var sample = GenericPlayerActions.ReadMovement(player.Reference);
        Assert.NotNull(sample);
        Assert.Equal("jump", sample!.Value.State);
        Assert.Equal(3, sample.Value.Index);
        Assert.Equal(10.0, sample.Value.SinceSeconds, 3);
        Assert.Null(GenericPlayerActions.ReadMovement(new EntityReference("gtfo.player:99", world.Kernel.WorldEpoch, 9)));
    }

    [Fact]
    public void Movement_state_answers_the_state_and_refuses_a_foreign_kind()
    {
        using var world = new GenericPlayerWorld();
        var player = world.Spawn(1UL, PlayerLocomotion.PLOC_State.ClimbLadder);
        var readers = new PlayerMovementStateContract.MovementReaders(GenericPlayerActions.ReadMovement);
        var evaluator = PlayerMovementStateContract.Evaluator(readers);

        // The row answers the state and how long it has been held; the set question and its `in_set` output the
        // row used to carry are deleted, so no port and no parameter names a member set any more.
        var answer = evaluator(Context(new { player = player.Reference }, new { }));
        Assert.Equal("climb_ladder", answer.GetProperty("state").GetString());
        Assert.Equal(10.0, answer.GetProperty("since").GetDouble(), 3);
        Assert.False(answer.TryGetProperty("in_set", out _));

        var door = new EntityReference("gtfo.map_object:door-1", world.Kernel.WorldEpoch, 1);
        var foreign = Assert.Throws<RuntimeContractException>(
            () => { _ = evaluator(Context(new { player = door }, new { })); });
        Assert.Equal(PlayerMovementStateContract.InputKindCode, foreign.Code);

        var unknown = new EntityReference("gtfo.player:404", world.Kernel.WorldEpoch, 4);
        var stale = Assert.Throws<RuntimeContractException>(
            () => { _ = evaluator(Context(new { player = unknown }, new { })); });
        Assert.Equal(PlayerMovementStateContract.InputStaleCode, stale.Code);
    }

    // ---------------------------------------------------------------- evaluation plumbing

    private static readonly ConstructorInfo EvaluationConstructor = typeof(EvaluationContext)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(constructor => constructor.GetParameters().Length == 6);

    /// <summary>One read-only frame as the kernel hands it to an evaluator. A case's read uses no world session and
    /// no trigger context, so the three reference halves are null: a row that read them would fail here.</summary>
    private static EvaluationContext Context(object inputs, object parameters)
        => (EvaluationContext)EvaluationConstructor.Invoke(new object?[]
        {
            "movement-state-case", RuntimeJson.From(parameters), RuntimeJson.From(inputs), null, null, null
        });
}
