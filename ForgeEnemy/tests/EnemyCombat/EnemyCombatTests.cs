using System.Text.Json;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Tests.EnemyCombat;

/// <summary>Cases for the `C-enemy-combat` rows over doubles. Every case drives the production handlers directly
/// and reads what they did through the native state machine they write to; no GTFO assembly is loaded and no hook
/// is installed, so these cases prove the handlers' decisions, not the game's own attack path.
///
/// The two registered rows are dispatched through the provider the kernel knows; the third method, `Impulse`, is
/// called directly because its row is deliberately unregistered — the audit judged the effect unreachable in game,
/// which the cases about it record alongside its own semantics.</summary>
[Trait("Category", "EnemyCombat")]
public sealed class EnemyCombatTests
{
    private static void ResetGame() => SNetwork.SNet.IsMaster = true;

    // ---------------------------------------------------------------- registration and shape

    /// <summary>The canonical rows are asserted from the contract text, because the kernel lets a suite register
    /// only its own capability ids: the registered tables are the same rows under the suite provider's own names,
    /// and the canonical names are what the production provider and the website carry. Both halves are checked, so
    /// a row cannot drift in either direction without a case failing.</summary>
    [Fact]
    public void the_registered_rows_carry_the_contracts_own_graphs()
    {
        ResetGame();
        using var world = new Scene();
        var registry = RuntimeJson.Parse(world.Kernel.ExportManifest()).GetProperty("registry");

        foreach (var (canonical, owned, schema) in new[]
        {
            (EnemyCombatContract.StaggerCapability, EnemyCombatRegistration.TestStaggerCapability, "forge.result.combat.stagger"),
            (EnemyCombatContract.AttackInterruptCapability, EnemyCombatRegistration.TestAttackInterruptCapability,
                "forge.result.combat.attack_interrupt")
        })
        {
            var declared = DeclaredCapability(canonical);
            var registered = registry.GetProperty("capabilities").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == owned);
            Assert.Equal("action", registered.GetProperty("kind").GetString());
            // The row is re-filed under the suite's provider because a module declares only its own capabilities;
            // `declared.GetProperty("owner")` below is the production provider the row really belongs to.
            Assert.Equal(EnemyCombatRegistration.TestProviderId, registered.GetProperty("owner").GetString());
            // The whole graph travels unchanged; only the id it is filed under differs, which is what the
            // canonical row below proves is the one the website and the production provider carry. The registered
            // value comes back re-emitted by the kernel's manifest, so the two are compared as values: whitespace
            // is the one difference the round trip is allowed to introduce.
            AssertSameJson(declared.GetProperty("graph"), registered.GetProperty("graph"));
            var result = registered.GetProperty("graph").GetProperty("outputs").EnumerateArray()
                .Single(o => o.GetProperty("id").GetString() == "result");
            Assert.Equal(schema, result.GetProperty("schema").GetString());
            Assert.Equal(new[] { "target", "status", "committed", "code" },
                result.GetProperty("fields").EnumerateArray().Take(4).Select(f => f.GetProperty("id").GetString()));
            Assert.Contains(registry.GetProperty("bindings").EnumerateArray(),
                b => b.GetProperty("capabilityId").GetString() == owned && b.GetProperty("role").GetString() == "execute");
        }

        // The stagger shape: the reaction strength is a structural parameter and `immunity_policy` keeps the
        // catalog's two members, because the native entry point takes an `ES_HitreactType` that cannot be omitted.
        var stagger = DeclaredCapability(EnemyCombatContract.StaggerCapability);
        Assert.Equal(EnemyCombatContract.ProviderId, stagger.GetProperty("owner").GetString());
        Assert.Equal(new[] { "reaction", "immunity_policy" },
            stagger.GetProperty("graph").GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(new[] { "micro", "light", "heavy" }, Values(stagger, "reaction"));
        Assert.Equal(new[] { "respect", "ignore" }, Values(stagger, "immunity_policy"));
        Assert.Equal(new[] { "in", "targets", "source" }, Ports(stagger, "inputs"));
        Assert.Equal("targets", stagger.GetProperty("graph").GetProperty("recipients").GetProperty("input").GetString());

        // The reshaped interrupt row: the enemies themselves are the input and there is no handle anywhere, because
        // no provider in this tree casts an attack-instance transaction handle. The row carries no structural
        // parameter — the refund policy went with the transaction it belonged to.
        var interrupt = DeclaredCapability(EnemyCombatContract.AttackInterruptCapability);
        Assert.Equal(new[] { "in", "enemies", "source" }, Ports(interrupt, "inputs"));
        Assert.Equal("many", interrupt.GetProperty("graph").GetProperty("inputs").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "enemies").GetProperty("cardinality").GetString());
        Assert.Empty(interrupt.GetProperty("graph").GetProperty("parameters").EnumerateArray());
        Assert.DoesNotContain("handle", interrupt.GetProperty("graph").GetRawText(), StringComparison.Ordinal);
        var recipients = interrupt.GetProperty("graph").GetProperty("recipients");
        Assert.Equal("enemies", recipients.GetProperty("input").GetString());
        Assert.Equal("entity", recipients.GetProperty("target").GetString());
        Assert.Equal("many", recipients.GetProperty("cardinality").GetString());
        Assert.Equal(new[] { "attack.interrupt" }, recipients.GetProperty("requires").EnumerateArray()
            .Select(r => r.GetString()));
        Assert.Contains("enemy-not-attacking", Codes(registry, EnemyCombatRegistration.TestAttackInterruptCapability));
    }

    [Fact]
    public void the_impulse_row_is_implemented_but_not_registered()
    {
        ResetGame();
        using var world = new Scene();
        var registry = RuntimeJson.Parse(world.Kernel.ExportManifest()).GetProperty("registry");

        Assert.DoesNotContain(registry.GetProperty("capabilities").EnumerateArray(),
            c => c.GetProperty("id").GetString()!.Contains("impulse", StringComparison.Ordinal));
        Assert.DoesNotContain(registry.GetProperty("bindings").EnumerateArray(),
            b => b.GetProperty("id").GetString() == EnemyCombatContract.ImpulseBinding);
        // The row is still spelled out in full, so registering it after review is a one-line wiring job.
        var row = RuntimeJson.Parse(EnemyCombatContract.ImpulseCapabilityRow);
        Assert.Equal(new[] { "in", "targets", "source", "force", "magnitude", "duration", "limb" }, Ports(row, "inputs"));
        Assert.Equal(new[] { "scaled", "absolute" }, Values(row, "mass_policy"));
        Assert.Equal("targets", row.GetProperty("graph").GetProperty("recipients").GetProperty("input").GetString());
        Assert.Single(EnemyCombatContract.Unregistered,
            entry => entry.Capability == EnemyCombatContract.ImpulseCapability);
    }

    [Fact]
    public void every_declared_result_code_is_one_the_handlers_actually_answer_with()
    {
        ResetGame();
        using var world = new Scene();
        var registry = RuntimeJson.Parse(world.Kernel.ExportManifest()).GetProperty("registry");

        Assert.Equal(StaggerCodes, Codes(registry, EnemyCombatRegistration.TestStaggerCapability));
        Assert.Equal(AttackInterruptCodes, Codes(registry, EnemyCombatRegistration.TestAttackInterruptCapability));
    }

    // ---------------------------------------------------------------- stagger

    [Fact]
    public void stagger_commits_through_the_native_state_machine_and_reads_the_reaction_back()
    {
        ResetGame();
        using var world = new Scene();

        var result = world.DispatchStagger(reaction: 1, immunity: 0, world.Reference);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Same(world.Enemy, world.Locomotion.m_agent);
        var activation = Assert.Single(world.Hitreact.Activations);
        Assert.Equal(ES_HitreactType.Light, activation.Reaction);
        // No direction, no attacker and no noise are supplied: this shape has no port for any of them, and the
        // neutral values are the only ones this provider may submit.
        Assert.Equal(ImpactDirection.Unspecified, activation.Direction);
        Assert.False(activation.AttackerIsPlayer);
        Assert.Null(activation.Attacker);
        Assert.Equal(DamageNoiseLevel.Normal, activation.Noise);
        Assert.Equal(UnityEngine.Vector3.zero, activation.Position);
        // `respect` asks the game's own gate exactly once and never overrides its timer.
        Assert.Equal(new[] { ES_HitreactType.Light }, world.Hitreact.GateAsks);
        Assert.False(world.Hitreact.LastOverrideTimer);

        var row = Row(result, 0);
        Assert.Equal(world.Reference, RuntimeJson.Entity(row.GetProperty("target")));
        Assert.Equal("committed", row.GetProperty("status").GetString());
        Assert.Equal(CommitStates.Confirmed, row.GetProperty("committed").GetString());
        Assert.Equal("committed", row.GetProperty("code").GetString());
        Assert.Equal("light", row.GetProperty("reaction").GetString());
        Assert.Equal(1, row.GetProperty("target_count").GetInt32());
    }

    [Fact]
    public void stagger_ignore_clears_the_forbidden_flag_and_overrides_the_retrigger_timer()
    {
        ResetGame();
        using var world = new Scene();
        world.Hitreact.HitreactForbidden = true;

        var result = world.DispatchStagger(reaction: 2, immunity: 1, world.Reference);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.False(world.Hitreact.HitreactForbidden);
        Assert.True(world.Hitreact.LastOverrideTimer);
        Assert.Equal(ES_HitreactType.Heavy, Assert.Single(world.Hitreact.Activations).Reaction);
    }

    [Fact]
    public void stagger_respect_refuses_what_the_game_gate_refuses_with_no_native_write()
    {
        ResetGame();
        using var world = new Scene();
        world.Hitreact.Gate = false;

        var result = world.DispatchStagger(reaction: 0, immunity: 0, world.Reference);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal("stagger-immune", result.Code);
        Assert.Empty(world.Hitreact.Activations);
        // The game's own gate was the only thing asked: the refusal is its answer, not a write this side undid.
        Assert.Equal(new[] { ES_HitreactType.Micro }, world.Hitreact.GateAsks);
    }

    [Fact]
    public void stagger_a_write_the_state_machine_does_not_read_back_is_unknown()
    {
        ResetGame();
        using var world = new Scene();
        // The machine accepts the call and keeps another reaction: the submission is real, the effect is not
        // observable, and neither may be claimed.
        world.Hitreact.OnActivate = () => world.Hitreact.ForceReaction(ES_HitreactType.None);

        var result = world.DispatchStagger(reaction: 1, immunity: 0, world.Reference);

        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal("unexpected-reaction-readback", result.Code);
        Assert.Single(world.Hitreact.Activations);
    }

    [Fact]
    public void stagger_refuses_a_non_host_and_a_closed_caller_gate_before_any_native_call()
    {
        ResetGame();
        using var world = new Scene();
        SNetwork.SNet.IsMaster = false;

        var client = world.DispatchStagger(reaction: 1, immunity: 0, world.Reference);
        Assert.Equal(CommandStatuses.Rejected, client.Status);
        Assert.Equal("authority-or-phase", client.Code);
        Assert.Empty(world.Hitreact.Activations);

        SNetwork.SNet.IsMaster = true;
        world.Allowed = false;
        var gated = world.DispatchStagger(reaction: 1, immunity: 0, world.Reference);
        Assert.Equal("authority-or-phase", gated.Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    [Fact]
    public void stagger_refuses_a_member_the_declared_reaction_set_does_not_carry()
    {
        ResetGame();
        using var world = new Scene();

        var badReaction = world.DispatchStagger(reaction: 3, immunity: 0, world.Reference);
        Assert.Equal(CommandStatuses.Rejected, badReaction.Status);
        Assert.Equal("reaction-unsupported", badReaction.Code);

        var badPolicy = world.DispatchStagger(reaction: 1, immunity: 2, world.Reference);
        Assert.Equal("immunity-policy-unsupported", badPolicy.Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    [Fact]
    public void stagger_refuses_a_stale_life_a_dead_target_and_a_missing_hitreact_entry()
    {
        ResetGame();
        using var world = new Scene();
        var foreign = new EntityReference(world.Reference.Id, world.Reference.WorldEpoch + 1, world.Reference.LifeEpoch);
        var stale = world.DispatchStagger(reaction: 1, immunity: 0, foreign);
        Assert.Equal("stale-or-unsupported-recipient", stale.Code);

        world.Enemy.Alive = false;
        var dead = world.DispatchStagger(reaction: 1, immunity: 0, world.Reference);
        Assert.Equal("not-alive", dead.Code);

        world.Enemy.Alive = true;
        world.Locomotion.Hitreact = null!;
        var noEntry = world.DispatchStagger(reaction: 1, immunity: 0, world.Reference);
        Assert.Equal("no-hitreact-entry", noEntry.Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    [Fact]
    public void stagger_writes_one_row_per_recipient_in_the_plans_order()
    {
        ResetGame();
        using var world = new Scene();
        var second = Scene.NewEnemy(world.Locomotion, world.Applicator, id: 8, pointer: 40);
        var secondReference = world.Module.TrackSpawn(second);

        var result = world.DispatchStagger(reaction: 1, immunity: 0, world.Reference, secondReference, world.Reference);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        var rows = result.Outputs.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(new[] { world.Reference, secondReference, world.Reference },
            rows.Select(r => RuntimeJson.Entity(r.GetProperty("target"))));
        Assert.All(rows, r => Assert.Equal(3, r.GetProperty("target_count").GetInt32()));
        // Two distinct lives were written; the repeated reference is two submissions, not one.
        Assert.Equal(3, world.Hitreact.Activations.Count);
    }

    // ---------------------------------------------------------------- impulse

    [Fact]
    public void impulse_scales_the_authored_force_and_submits_it_as_an_unknown_commit()
    {
        ResetGame();
        using var world = new Scene();

        var result = world.DispatchImpulse(massPolicy: 0, force: new[] { 1.0, 2.0, 3.0 }, magnitude: 5, duration: 0.25,
            targets: world.Reference);

        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        var force = Assert.Single(world.Applicator.Forces);
        Assert.Equal(5f, force.Force.x, 3);
        Assert.Equal(10f, force.Force.y, 3);
        Assert.Equal(15f, force.Force.z, 3);
        Assert.Equal(0.25f, force.Duration, 3);

        // The applicator returns void and integrates later, so a submitted shove is an unknown commit, never a
        // confirmed displacement.
        var row = Row(result, 0);
        Assert.Equal(world.Reference, RuntimeJson.Entity(row.GetProperty("target")));
        Assert.Equal("unknown", row.GetProperty("status").GetString());
        Assert.Equal(CommitStates.Unknown, row.GetProperty("committed").GetString());
        Assert.Equal("submitted-unverified", row.GetProperty("code").GetString());
    }

    [Fact]
    public void impulse_absolute_submits_the_authored_vector_unchanged()
    {
        ResetGame();
        using var world = new Scene();

        var result = world.DispatchImpulse(massPolicy: 1, force: new[] { 2.0, 0.0, 0.0 }, magnitude: 5, duration: 1,
            targets: world.Reference);

        Assert.Equal("submitted-unverified", result.Code);
        var force = Assert.Single(world.Applicator.Forces);
        Assert.Equal(2f, force.Force.x, 3);
        Assert.Equal(1f, force.Duration, 3);
    }

    [Fact]
    public void impulse_without_a_limb_uses_the_receivers_first_limb_applicator()
    {
        ResetGame();
        using var world = new Scene();

        var result = world.DispatchImpulse(massPolicy: 0, force: new[] { 1.0, 0.0, 0.0 }, magnitude: 1, duration: 0.5,
            targets: world.Reference);

        Assert.Equal("submitted-unverified", result.Code);
        Assert.Single(world.Applicator.Forces);
        // The second limb's applicator is a different object and was not touched.
        Assert.Empty(world.Enemy.Damage.DamageLimbs[1].ForceApplicator.Forces);
    }

    [Fact]
    public void impulse_names_a_limb_by_its_declared_id_and_refuses_one_no_limb_carries()
    {
        ResetGame();
        using var world = new Scene();

        var named = world.DispatchImpulse(massPolicy: 0, force: new[] { 1.0, 0.0, 0.0 }, magnitude: 1, duration: 0.5,
            limb: 1, targets: world.Reference);
        Assert.Equal("submitted-unverified", named.Code);
        Assert.Empty(world.Applicator.Forces);
        Assert.Single(world.Enemy.Damage.DamageLimbs[1].ForceApplicator.Forces);

        var missing = world.DispatchImpulse(massPolicy: 0, force: new[] { 1.0, 0.0, 0.0 }, magnitude: 1, duration: 0.5,
            limb: 9, targets: world.Reference);
        Assert.Equal(CommandStatuses.Rejected, missing.Status);
        Assert.Equal("invalid-limb", missing.Code);
    }

    [Fact]
    public void impulse_refuses_a_limb_whose_applicator_is_gone()
    {
        ResetGame();
        using var world = new Scene();
        world.Enemy.Damage.DamageLimbs[0].ForceApplicator = null!;

        var result = world.DispatchImpulse(massPolicy: 0, force: new[] { 1.0, 0.0, 0.0 }, magnitude: 1, duration: 0.5,
            targets: world.Reference);

        Assert.Equal("no-force-applicator", result.Code);
        Assert.Empty(world.Applicator.Forces);
    }

    [Fact]
    public void impulse_refuses_a_force_magnitude_duration_or_policy_outside_the_declared_domain()
    {
        ResetGame();
        using var world = new Scene();

        Assert.Equal("mass-policy-unsupported", world.DispatchImpulse(2, new[] { 1.0, 0.0, 0.0 }, 1, 1, null, world.Reference).Code);
        // A force vector is three numbers or it is nothing: a shorter array is refused, never padded.
        Assert.Equal("invalid-force", world.DispatchImpulse(0, new[] { 1.0, 0.0 }, 1, 1, null, world.Reference).Code);
        Assert.Equal("magnitude-out-of-range", world.DispatchImpulse(0, new[] { 1.0, 0.0, 0.0 }, 0, 1, null, world.Reference).Code);
        Assert.Equal("duration-out-of-range", world.DispatchImpulse(0, new[] { 1.0, 0.0, 0.0 }, 1, 0, null, world.Reference).Code);
        Assert.Equal("invalid-limb", world.DispatchImpulse(0, new[] { 1.0, 0.0, 0.0 }, 1, 1, -2, world.Reference).Code);
        Assert.Empty(world.Applicator.Forces);
    }

    [Fact]
    public void impulse_native_failure_is_an_unknown_commit()
    {
        ResetGame();
        using var world = new Scene();
        world.Applicator.ThrowOnAddForce = true;

        var result = world.DispatchImpulse(massPolicy: 0, force: new[] { 1.0, 0.0, 0.0 }, magnitude: 1, duration: 1,
            targets: world.Reference);

        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal("native-commit-exception", result.Code);
        Assert.Empty(world.Applicator.Forces);
    }

    [Fact]
    public void every_row_refuses_more_targets_than_the_result_budget_allows()
    {
        ResetGame();
        using var world = new Scene();
        var tooMany = Enumerable.Range(0, CommandResult.MaximumFacts + 1).Select(_ => world.Reference).ToArray();

        Assert.Equal("too-many-targets", world.DispatchStagger(1, 0, tooMany).Code);
        Assert.Equal("too-many-targets",
            world.DispatchImpulse(0, new[] { 1.0, 0.0, 0.0 }, 1, 1, null, tooMany).Code);
        Assert.Equal("too-many-targets", world.DispatchAttackInterrupt(tooMany).Code);
        Assert.Empty(world.Hitreact.Activations);
        Assert.Empty(world.Applicator.Forces);
    }

    // ---------------------------------------------------------------- attack interrupt

    [Fact]
    public void attack_interrupt_writes_the_state_machine_for_an_attack_still_in_flight()
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, "striker", performing: true);

        var result = world.DispatchAttackInterrupt(world.Reference);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        // The interruption is the game's own hitreact state machine, asked through the game's own gate.
        var activation = Assert.Single(world.Hitreact.Activations);
        Assert.Equal(ES_HitreactType.Light, activation.Reaction);
        Assert.Equal(ImpactDirection.Unspecified, activation.Direction);
        Assert.False(activation.AttackerIsPlayer);
        Assert.Null(activation.Attacker);
        Assert.Equal(new[] { ES_HitreactType.Light }, world.Hitreact.GateAsks);
        Assert.False(world.Hitreact.LastOverrideTimer);

        var row = Row(result, 0);
        Assert.Equal(world.Reference, RuntimeJson.Entity(row.GetProperty("target")));
        Assert.Equal("committed", row.GetProperty("status").GetString());
        Assert.Equal(CommitStates.Confirmed, row.GetProperty("committed").GetString());
        Assert.Equal("committed", row.GetProperty("code").GetString());
        Assert.Equal(1, row.GetProperty("target_count").GetInt32());
    }

    /// <summary>An enemy's attack may stand in any of the four fields `EnemyLocomotion` declares, and both
    /// questions `ES_EnemyAttackBase` answers count as in flight: a hit being performed and one still charging.</summary>
    [Theory]
    [InlineData("striker", true, false)]
    [InlineData("striker", false, true)]
    [InlineData("tank", true, false)]
    [InlineData("tank_multi", false, true)]
    [InlineData("shooter", true, false)]
    public void attack_interrupt_reads_every_attack_field_the_locomotion_declares(string field, bool performing, bool charging)
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, field, performing, charging);

        var result = world.DispatchAttackInterrupt(world.Reference);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Single(world.Hitreact.Activations);
    }

    [Fact]
    public void attack_interrupt_refuses_an_enemy_that_is_not_attacking_and_commits_nothing()
    {
        ResetGame();
        using var world = new Scene();
        // An attack state that exists but is neither performing nor charging is not an attack in flight.
        Scene.SetAttack(world.Locomotion, "shooter");

        var result = world.DispatchAttackInterrupt(world.Reference);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal("enemy-not-attacking", result.Code);
        Assert.Empty(world.Hitreact.Activations);
        Assert.Empty(world.Hitreact.GateAsks);
        var row = Row(result, 0);
        Assert.Equal("rejected", row.GetProperty("status").GetString());
        Assert.Equal(CommitStates.None, row.GetProperty("committed").GetString());
        Assert.Equal("enemy-not-attacking", row.GetProperty("code").GetString());
    }

    [Fact]
    public void attack_interrupt_refuses_when_the_games_own_gate_forbids_the_reaction()
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, "tank", performing: true);
        world.Hitreact.Gate = false;

        var result = world.DispatchAttackInterrupt(world.Reference);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal("attack-interrupt-forbidden", result.Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    [Fact]
    public void attack_interrupt_a_gate_that_throws_is_a_refusal_not_an_unknown_commit()
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, "striker", performing: true);
        world.Hitreact.OnGate = () => throw new InvalidOperationException("fixture native failure");

        var result = world.DispatchAttackInterrupt(world.Reference);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal("hitreact-gate-exception", result.Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    [Fact]
    public void attack_interrupt_refuses_an_enemy_whose_attack_state_cannot_be_read()
    {
        ResetGame();
        using var world = new Scene();
        var attack = Scene.SetAttack(world.Locomotion, "striker", performing: true);
        attack.OnStateRead = () => throw new InvalidOperationException("fixture native failure");

        var result = world.DispatchAttackInterrupt(world.Reference);

        // A read that throws proves nothing about the attack, so nothing is written and nothing is claimed.
        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal("attack-state-unreadable", result.Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    [Fact]
    public void attack_interrupt_refuses_an_enemy_with_no_hitreact_entry()
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, "striker", performing: true);
        world.Locomotion.Hitreact = null!;

        var result = world.DispatchAttackInterrupt(world.Reference);

        Assert.Equal("no-hitreact-entry", result.Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    [Fact]
    public void attack_interrupt_writes_one_row_per_enemy_in_the_plans_order()
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, "striker", performing: true);
        // A second enemy with its own locomotion: only the first one is attacking, so the answer is one committed
        // row and one refusal, in the order the plan named them.
        var otherLocomotion = Scene.NewLocomotion(26);
        var second = Scene.NewEnemy(otherLocomotion, new LimbForceApplicator { Pointer = new(320) }, id: 8, pointer: 40);
        var secondReference = world.Module.TrackSpawn(second);

        var result = world.DispatchAttackInterrupt(secondReference, world.Reference, secondReference);

        Assert.Equal(CommandStatuses.Partial, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        var rows = result.Outputs.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(new[] { secondReference, world.Reference, secondReference },
            rows.Select(r => RuntimeJson.Entity(r.GetProperty("target"))));
        Assert.Equal(new[] { "enemy-not-attacking", "committed", "enemy-not-attacking" },
            rows.Select(r => r.GetProperty("code").GetString()));
        Assert.All(rows, r => Assert.Equal(3, r.GetProperty("target_count").GetInt32()));
        Assert.Single(world.Hitreact.Activations);
        Assert.Empty(otherLocomotion.Hitreact.Activations);
    }

    [Fact]
    public void attack_interrupt_refuses_a_non_host_and_a_closed_caller_gate_before_any_native_call()
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, "striker", performing: true);
        SNetwork.SNet.IsMaster = false;

        var client = world.DispatchAttackInterrupt(world.Reference);
        Assert.Equal(CommandStatuses.Rejected, client.Status);
        Assert.Equal("authority-or-phase", client.Code);
        Assert.Empty(world.Hitreact.Activations);

        SNetwork.SNet.IsMaster = true;
        world.Allowed = false;
        var gated = world.DispatchAttackInterrupt(world.Reference);
        Assert.Equal("authority-or-phase", gated.Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    [Fact]
    public void attack_interrupt_refuses_a_stale_life_a_dead_target_and_a_missing_locomotion()
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, "striker", performing: true);
        var foreign = new EntityReference(world.Reference.Id, world.Reference.WorldEpoch + 1, world.Reference.LifeEpoch);

        Assert.Equal("stale-or-unsupported-recipient", world.DispatchAttackInterrupt(foreign).Code);

        world.Enemy.Alive = false;
        Assert.Equal("not-alive", world.DispatchAttackInterrupt(world.Reference).Code);

        world.Enemy.Alive = true;
        world.Enemy.Locomotion = null!;
        Assert.Equal("missing-locomotion", world.DispatchAttackInterrupt(world.Reference).Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    [Fact]
    public void attack_interrupt_a_write_the_state_machine_does_not_read_back_is_unknown()
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, "striker", performing: true);
        world.Hitreact.OnActivate = () => world.Hitreact.ForceReaction(ES_HitreactType.None);

        var result = world.DispatchAttackInterrupt(world.Reference);

        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal("unexpected-reaction-readback", result.Code);
        Assert.Single(world.Hitreact.Activations);
    }

    [Fact]
    public void attack_interrupt_native_failure_is_an_unknown_commit()
    {
        ResetGame();
        using var world = new Scene();
        Scene.SetAttack(world.Locomotion, "striker", performing: true);
        world.Hitreact.ThrowOnActivate = true;

        var result = world.DispatchAttackInterrupt(world.Reference);

        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal("native-commit-exception", result.Code);
        Assert.Empty(world.Hitreact.Activations);
    }

    private static JsonElement Row(CommandResult result, int index)
        => result.Outputs.GetProperty("results").EnumerateArray().ElementAt(index);

    /// <summary>Value equality of two JSON values: the same members with the same values, arrays in order. The
    /// registry the kernel hands back has been re-emitted with its members sorted, so member order and whitespace
    /// are the two differences a round trip is allowed to introduce; everything the registry declares is compared.
    /// </summary>
    private static void AssertSameJson(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedFields = expected.EnumerateObject().ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);
                var actualFields = actual.EnumerateObject().ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);
                Assert.Equal(expectedFields.Keys.OrderBy(name => name, StringComparer.Ordinal),
                    actualFields.Keys.OrderBy(name => name, StringComparer.Ordinal));
                foreach (var name in expectedFields.Keys) AssertSameJson(expectedFields[name], actualFields[name]);
                break;
            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                Assert.Equal(expectedItems.Length, actualItems.Length);
                for (var index = 0; index < expectedItems.Length; index++)
                    AssertSameJson(expectedItems[index], actualItems[index]);
                break;
            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }

    /// <summary>One canonical capability row, parsed out of the contract text the production provider and the
    /// website carry.</summary>
    private static JsonElement DeclaredCapability(string capabilityId)
        => RuntimeJson.Parse("[" + EnemyCombatContract.CapabilityRows + "]").EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == capabilityId);

    private static string[] Ports(JsonElement capability, string list)
        => capability.GetProperty("graph").GetProperty(list).EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()!).ToArray();

    private static string[] Values(JsonElement capability, string parameter)
        => capability.GetProperty("graph").GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == parameter).GetProperty("values").EnumerateArray()
            .Select(v => v.GetString()!).ToArray();

    private static string[] Codes(JsonElement registry, string capabilityId)
        => registry.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == capabilityId)
            .GetProperty("graph").GetProperty("outputs").EnumerateArray()
            .Single(o => o.GetProperty("id").GetString() == "result")
            .GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToArray();

    private static readonly string[] StaggerCodes =
    {
        "reaction-unsupported", "immunity-policy-unsupported", "too-many-targets", "authority-or-phase",
        "stale-or-unsupported-recipient", "missing-health-receiver", "health-receiver-owner-mismatch", "not-alive",
        "missing-locomotion", "no-hitreact-entry", "hitreact-gate-exception", "stagger-immune",
        "native-commit-exception", "receiver-changed-during-commit", "unexpected-reaction-readback",
        "readback-exception", "stagger-all-rejected", "stagger-all-unknown"
    };

    private static readonly string[] AttackInterruptCodes =
    {
        "too-many-targets", "authority-or-phase", "stale-or-unsupported-recipient", "missing-health-receiver",
        "health-receiver-owner-mismatch", "not-alive", "missing-locomotion", "attack-state-unreadable",
        "enemy-not-attacking", "no-hitreact-entry", "hitreact-gate-exception", "attack-interrupt-forbidden",
        "native-commit-exception", "receiver-changed-during-commit", "unexpected-reaction-readback",
        "readback-exception", "attack-interrupt-all-rejected", "attack-interrupt-all-unknown"
    };
}
