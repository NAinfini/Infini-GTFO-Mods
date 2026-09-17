using System;
using System.Linq;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>The two behaviour actions' cases: what each row's ports accept and refuse, what the bridge is asked
/// for, and which result row the caller reads back. The rules are the production ones; the bridge is
/// `FakePorts`, whose answers stand in for the game's.</summary>
internal static class ActionCases
{
    private const byte Melee = 1, Ranged = 2, Alarm = 3, Detection = 7, SpawnChildren = 9;

    internal static void Run()
    {
        AbilityCases();
        NoiseCases();
        RowCases();
    }

    private static void AbilityCases()
    {
        T.Case("ability.commits-the-native-trigger", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Ranged);
            FakePorts.Register(enemy, Ranged, 2);
            var ledger = new EnemyBehaviorLedger();
            var reference = Scene.Reference();
            var result = Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("ranged")), ports, ledger);
            T.Equal(CommandStatuses.Succeeded, result.Status, "one committed target is a success");
            T.Equal(CommitStates.Confirmed, T.Committed(result, 0), "the commit is confirmed");
            T.Equal(1, enemy.UseAbilityCalls, "the native trigger was called exactly once");
            T.Equal(Ranged, enemy.LastAbility, "the requested kind was submitted");
            T.Equal(2, enemy.LastIndex, "the index is the component's own position");
            T.Equal(1, T.Row(result, 0).GetProperty("target_count").GetInt32(), "one target is reported");
            var running = ledger.Find(reference.Id, Scene.World);
            T.Check(running != null, "the running ability is remembered");
            T.Equal(new IntPtr(4242), running!.Component, "the row names the component that was submitted to");
            T.Equal(Ranged, running.Ability, "the row names the requested ability");
        });

        T.Case("ability.finished-ability-leaves-no-row", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Alarm);
            enemy.Done.Add(Alarm);
            var ledger = new EnemyBehaviorLedger();
            var reference = Scene.Reference();
            var result = Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("alarm")), ports, ledger);
            T.Equal(CommitStates.Confirmed, T.Committed(result, 0), "the trigger answered true, so it is committed");
            T.Check(ledger.Find(reference.Id, Scene.World) == null,
                "an ability the machine already reports done is not in flight");
        });

        T.Case("ability.a-replaced-row-is-reported-as-superseded", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Melee, Ranged);
            var ledger = new EnemyBehaviorLedger();
            var reference = Scene.Reference();
            Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("melee")), ports, ledger);
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { reference }, Scene.Ability("ranged")), ports, ledger);
            T.Equal(CommitStates.Confirmed, T.Committed(result, 0), "the replacing trigger still commits");
            var dropped = ports.Dropped.Single();
            T.Equal("gtfo.enemy:7", dropped.Key, "the dropped row names the enemy life it belonged to");
            T.Equal(Melee, dropped.Ability, "the dropped row names the ability the ledger held");
            T.Check(!dropped.Finished, "the machine's own answer for the replaced ability was read back unfinished");
            T.Equal(ForgeEnemy.EnemyAbilityInterruptedContract.ReasonSuperseded, dropped.Reason,
                "the end path names the replacing trigger");
            T.Equal(Ranged, ledger.Find(reference.Id, Scene.World)!.Ability, "the new row is the one in flight");
        });

        T.Case("ability.a-finished-replaced-row-carries-the-finished-answer", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Melee, Ranged);
            var ledger = new EnemyBehaviorLedger();
            var reference = Scene.Reference();
            Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("melee")), ports, ledger);
            enemy.Done.Add(Melee);
            Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("ranged")), ports, ledger);
            // The decision half reports the drop with the machine's own answer; whether that answer is published
            // as an interruption is the module's filter (`ReportAbilityDropped`), not this half's.
            var dropped = ports.Dropped.Single();
            T.Check(dropped.Finished, "the machine's own answer says the replaced ability had finished");
            T.Equal(ForgeEnemy.EnemyAbilityInterruptedContract.ReasonSuperseded, dropped.Reason,
                "the end path is still the replacing trigger");
            T.Equal(Ranged, ledger.Find(reference.Id, Scene.World)!.Ability, "the new row is still in flight");
        });

        T.Case("ability.not-registered-is-refused", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Melee);
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("ranged")), ports, ledger);
            T.Equal(CommandStatuses.Rejected, result.Status, "a kind this enemy does not have is a rejection");
            T.Equal("ability-not-registered", T.Code(result, 0), "the refusal names the missing ability");
            T.Equal(0, ports.Enemies["gtfo.enemy:7"].UseAbilityCalls, "nothing was submitted");
        });

        T.Case("ability.unknown-resource-is-refused", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Melee);
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("teleportation")), ports, ledger);
            T.Equal("ability-kind-unsupported", result.Code, "a resource this provider does not own is refused");
            T.Equal(0, ports.Enemies["gtfo.enemy:7"].UseAbilityCalls, "nothing was submitted");
        });

        T.Case("ability.missing-resource-is-refused", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Melee);
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(Scene.AbilityContext(new[] { Scene.Reference() }, null), ports, ledger);
            T.Equal("ability-missing", result.Code, "an absent ability port is refused");
        });

        T.Case("ability.native-false-is-a-refusal", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Melee);
            enemy.Triggered = false;
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("melee")), ports, ledger);
            T.Equal(CommandStatuses.Rejected, result.Status, "the game's own false is a rejection");
            T.Equal("ability-refused", T.Code(result, 0), "the refusal is the machine's own answer");
            T.Check(ledger.Find("gtfo.enemy:7", Scene.World) == null, "a refused ability is not in flight");
        });

        T.Case("ability.unavailable-receiver-is-refused", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Melee);
            enemy.SubmissionUnavailable = true;
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("melee")), ports, ledger);
            T.Equal("receiver-unavailable", T.Code(result, 0), "an attempt that could not be made is a refusal");
        });

        T.Case("ability.nearest-policy-is-refused", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Melee);
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("melee"),
                    targetPolicy: AbilityDecision.TargetNearest), ports, ledger);
            T.Equal("target-policy-unsupported", result.Code, "a policy with no native member is refused");
            T.Equal(0, ports.Enemies["gtfo.enemy:7"].UseAbilityCalls, "nothing was submitted");
        });

        T.Case("ability.policy-outside-the-set-is-refused", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Melee);
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("melee"), targetPolicy: 7), ports, ledger);
            T.Equal("target-policy-unsupported", result.Code, "an index outside the declared set is refused");
            T.Equal(0, ports.Enemies["gtfo.enemy:7"].UseAbilityCalls, "nothing was submitted");
        });

        T.Case("ability.cooldown-scope-outside-the-set-is-refused", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Melee);
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("melee"), cooldownScope: 6), ports, ledger);
            T.Equal("cooldown-scope-unsupported", result.Code, "a member outside the declared set is refused");
            T.Equal(0, ports.Enemies["gtfo.enemy:7"].UseAbilityCalls, "nothing was submitted");
        });

        T.Case("ability.disabled-machine-is-refused", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Melee);
            enemy.CanTrigger = false;
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("melee")), ports, ledger);
            T.Equal("abilities-disabled", T.Code(result, 0), "a machine that cannot trigger is refused");
            T.Equal(0, enemy.UseAbilityCalls, "nothing was submitted");
        });

        T.Case("ability.dead-enemy-is-refused", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Melee);
            enemy.Alive = false;
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("melee")), ports, ledger);
            T.Equal("not-alive", T.Code(result, 0), "a dead enemy is refused");
            T.Equal(0, enemy.UseAbilityCalls, "nothing was submitted");
        });

        T.Case("ability.unknown-recipient-is-refused", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Melee);
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference("gtfo.enemy:999") }, Scene.Ability("melee")), ports, ledger);
            T.Equal("stale-or-unsupported-recipient", T.Code(result, 0), "a life the bridge does not know is refused");
        });

        T.Case("ability.native-exception-is-unknown-not-committed", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Melee);
            enemy.ThrowOnUse = new InvalidOperationException("native");
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("melee")), ports, ledger);
            T.Equal(CommandStatuses.Failed, result.Status, "an exception across the native boundary is a failure");
            T.Equal(CommitStates.Unknown, T.Committed(result, 0), "the commit is unknown, never claimed");
            T.Equal("native-commit-exception", T.Code(result, 0), "the code names the boundary");
        });

        T.Case("ability.stops-committing-after-an-unknown-commit", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Melee);
            enemy.ThrowOnUse = new InvalidOperationException("native");
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(Scene.AbilityContext(
                new[] { Scene.Reference(), Scene.Reference() }, Scene.Ability("melee")), ports, ledger);
            T.Equal(1, enemy.UseAbilityCalls, "the second target is not submitted after an unknown commit");
            T.Equal("not-attempted-after-unknown-commit", T.Code(result, 1), "the second row says why it was skipped");
        });

        T.Case("ability.component-gone-is-unknown", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Detection);
            enemy.ComponentGone = true;
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("detection")), ports, ledger);
            T.Equal(CommitStates.Unknown, T.Committed(result, 0), "an unreadable component is an unknown commit");
            T.Equal("ability-disappeared", T.Code(result, 0), "the code names the missing component");
        });

        T.Case("ability.host-only", () =>
        {
            var ports = new FakePorts { CanExecuteAll = false };
            var enemy = ports.Track("gtfo.enemy:7", Melee);
            var ledger = new EnemyBehaviorLedger();
            var result = Scene.DispatchAbility(
                Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("melee")), ports, ledger);
            T.Equal("authority-or-phase", result.Code, "a client does not submit an ability");
            T.Equal(0, enemy.UseAbilityCalls, "nothing was submitted");
        });

        T.Case("ability.every-ability-kind-resolves", () =>
        {
            foreach (var id in new[] { "melee", "ranged", "alarm", "defensive", "healing", "group_enhance",
                "detection", "door_breaker", "spawn_children" })
                T.Check(EnemyAbilityResources.Resolve(id) != null, id + " resolves to a resource");
            T.Check(EnemyAbilityResources.Resolve("none") == null, "the machine's own None is not a resource");
            T.Check(EnemyAbilityResources.Enumerate().Count == 9, "nine ability kinds are owned");
            T.Check(EnemyAbilityResources.Enumerate().All(x => x.ResourceKind == "ability"), "every kind is `ability`");
        });
    }

    private static void NoiseCases()
    {
        T.Case("noise.submits-the-position-and-radius", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", SpawnChildren);
            var result = NoiseDecision.Run(Scene.NoiseContext(Scene.Reference(), 12.5, 4, 5, 6), ports);
            T.Equal(CommandStatuses.Succeeded, result.Status, "a submitted noise is a success");
            T.Equal(1, ports.NoiseCalls, "the game's own write point was reached once");
            T.Equal(4d, ports.NoisePosition.X, "the caller's position reached the native struct");
            T.Equal(6d, ports.NoisePosition.Z, "the caller's position reached the native struct");
            T.Equal(12.5d, ports.NoiseRadius, "the declared radius reached the native struct");
            T.Equal(CommitStates.Confirmed, T.Committed(result, 0), "the commit is confirmed");
            T.Equal(12.5d, T.Row(result, 0).GetProperty("radius").GetDouble(), "the row reports the requested radius");
        });

        T.Case("noise.result-carries-the-source", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", SpawnChildren);
            var result = NoiseDecision.Run(Scene.NoiseContext(Scene.Reference(), 3.5), ports);
            T.Equal("gtfo.enemy:7", T.Row(result, 0).GetProperty("target").GetProperty("id").GetString()!,
                "the row's target is the source the caller named");
        });

        T.Case("noise.position-without-a-node-is-refused", () =>
        {
            var ports = new FakePorts { NoiseNodeFound = false };
            ports.Track("gtfo.enemy:7", SpawnChildren);
            var result = NoiseDecision.Run(Scene.NoiseContext(Scene.Reference(), 3.5), ports);
            T.Equal("noise-node-unavailable", result.Code, "a position no node covers is refused");
        });

        T.Case("noise.radius-out-of-range-is-refused", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", SpawnChildren);
            T.Equal("radius-out-of-range", NoiseDecision.Run(Scene.NoiseContext(Scene.Reference(), 0), ports).Code,
                "a zero radius is refused");
            T.Equal("radius-out-of-range", NoiseDecision.Run(Scene.NoiseContext(Scene.Reference(), 5000), ports).Code,
                "an unbounded radius is refused");
            T.Equal(0, ports.NoiseCalls, "nothing was submitted");
        });

        T.Case("noise.position-out-of-range-is-refused", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", SpawnChildren);
            var result = NoiseDecision.Run(Scene.NoiseContext(Scene.Reference(), 4, x: 1e9), ports);
            T.Equal("invalid-position", result.Code, "a component outside the world bounds is refused");
            T.Equal(0, ports.NoiseCalls, "nothing was submitted");
        });

        T.Case("noise.native-exception-is-unknown", () =>
        {
            var ports = new FakePorts { ThrowOnNoise = new InvalidOperationException("native") };
            ports.Track("gtfo.enemy:7", SpawnChildren);
            var result = NoiseDecision.Run(Scene.NoiseContext(Scene.Reference(), 4), ports);
            T.Equal(CommandStatuses.Failed, result.Status, "an exception across the native boundary is a failure");
            T.Equal(CommitStates.Unknown, result.CommitState, "the commit is unknown, never claimed");
            T.Equal("native-commit-exception", result.Code, "the code names the boundary");
        });

        T.Case("noise.unknown-source-is-refused", () =>
        {
            var ports = new FakePorts();
            var result = NoiseDecision.Run(Scene.NoiseContext(Scene.Reference("gtfo.enemy:999"), 4), ports);
            T.Equal("noise-node-unavailable", result.Code, "a source the bridge does not know cannot emit");
            T.Equal(0, ports.NoiseCalls, "nothing was submitted");
        });

        T.Case("noise.host-only", () =>
        {
            var ports = new FakePorts { CanExecuteAll = false };
            ports.Track("gtfo.enemy:7", SpawnChildren);
            var result = NoiseDecision.Run(Scene.NoiseContext(Scene.Reference(), 4), ports);
            T.Equal("authority-or-phase", result.Code, "a client does not emit a world noise");
            T.Equal(0, ports.NoiseCalls, "nothing was submitted");
        });
    }

    /// <summary>The declared rows against the handlers' own ports, checked by the kernel's real resolver: a row
    /// whose ports and a handler whose shape disagree is refused at registration, so `RowKernel` registering at
    /// all is that check, and the ids and shapes are read from the declaration itself.</summary>
    private static void RowCases()
    {
        T.Case("rows.register-and-declare-their-rows", () =>
        {
            var kernel = Scene.RowKernel();
            T.Check(kernel.StartupState == RuntimeStartupState.Ready, "the two rows register against their shapes");
            T.Check(EnemyBehaviorContract.CapabilityIds.SequenceEqual(
                new[] { EnemyBehaviorContract.AbilityCapability, EnemyBehaviorContract.NoiseEmitCapability }),
                "the declared rows are the two implemented ones, in catalog order");
            T.Check(EnemyBehaviorContract.HandlerNames.SequenceEqual(
                new[] { EnemyBehaviorContract.AbilityHandler, EnemyBehaviorContract.NoiseEmitHandler }),
                "the declared handlers are the two the contract hands over");
            T.Check(EnemyBehaviorContract.Shapes().Keys.OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(new[] { EnemyBehaviorContract.AbilityHandler, EnemyBehaviorContract.NoiseEmitHandler }),
                "the shapes are the two handlers' own");
            T.Check(EnemyBehaviorContract.BindingRows.Contains("\"gtfo.enemy.ability\"", StringComparison.Ordinal)
                && EnemyBehaviorContract.BindingRows.Contains("\"gtfo.enemy.noise_emit\"", StringComparison.Ordinal),
                "each binding row names the handler its shape belongs to");
        });

        T.Case("rows.declared-shapes-are-what-the-decisions-read", () =>
        {
            // The two shapes are the port sets the decisions actually read: a port the decision asks for and the
            // shape omits (or the other way round) is a row the kernel would resolve a different frame against.
            T.Equal("enemies ability", string.Join(' ', EnemyBehaviorContract.AbilityPorts.InputPorts),
                "the ability row's inputs");
            T.Equal("result", string.Join(' ', EnemyBehaviorContract.AbilityPorts.OutputPorts),
                "the ability row's outputs");
            T.Equal("target_policy cooldown_scope", string.Join(' ', EnemyBehaviorContract.AbilityPorts.ParameterIds),
                "the ability row's structural parameters");
            T.Equal("source position radius", string.Join(' ', EnemyBehaviorContract.NoiseEmitPorts.InputPorts),
                "the noise row's inputs");
            T.Equal("result", string.Join(' ', EnemyBehaviorContract.NoiseEmitPorts.OutputPorts),
                "the noise row's outputs");
            T.Equal("", string.Join(' ', EnemyBehaviorContract.NoiseEmitPorts.ParameterIds),
                "the noise row has no parameters");
            T.Check(AbilityDecision.TargetPolicyMembers.SequenceEqual(new[] { "current", "nearest" }),
                "the policy member table is the catalog's own order");
            T.Equal(0, AbilityDecision.TargetCurrent, "the served policy is the catalog's first member");
        });

        T.Case("rows.deleted-rows-are-not-declared", () =>
        {
            var rows = EnemyBehaviorContract.CapabilityRows;
            foreach (var deleted in new[] { "forge.action.enemy.patrol", "forge.action.enemy.flee",
                "forge.action.enemy.threat_change", "forge.action.enemy.investigate" })
                T.Check(!rows.Contains(deleted, StringComparison.Ordinal), deleted + " is not declared");
            T.Check(!rows.Contains("heard_by", StringComparison.Ordinal), "the deleted heard_by port is not declared");
            T.Check(!rows.Contains("propagation_mask", StringComparison.Ordinal),
                "the deleted propagation_mask parameter is not declared");
        });
    }
}
