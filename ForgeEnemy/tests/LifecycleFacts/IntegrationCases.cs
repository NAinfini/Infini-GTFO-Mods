using System.Reflection;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using static T;

internal static class IntegrationCases
{
    internal static void Run()
    {
        Case("integration.manifest-is-implemented-not-game-verified", () => {
            using var s = new Scene(); var m = RuntimeJson.Parse(s.Kernel.ExportManifest());
            var bindings = m.GetProperty("registry").GetProperty("bindings").EnumerateArray()
                .Where(b => b.GetProperty("providerId").GetString() == EnemyModule.ProviderId).ToArray();
            Check(bindings.Length == 5, "Provider has missing or duplicate bindings.");
            Check(!bindings.Any(b => b.GetProperty("capabilityId").GetString() == "forge.trigger.combat.killed"), "Death implies kill.");
            Check(m.GetProperty("bindingSupport").EnumerateArray().All(x => x.GetProperty("verification").GetString()
                == "implementation-only"), "Unverified native binding was promoted.");
        });
        foreach (string suffix in new[] { "death_started", "limb_broken" }) Case("integration.permission-" + suffix, () => {
            using var s = new Scene(load: false);
            // The plan's own permissions omit the native read permission the binding closure requires (only the
            // recorder's permission is declared); permission-lock demands an exact match, so this can never load.
            var plan = Scene.FactPlan(s.Kernel, suffix).WithPermissions(new[] { LocalPlan.RecordPermission });
            bool rejected = false;
            try { LocalPlan.Load(s.Kernel, plan); }
            catch (RuntimeContractException e) { rejected = e.Code == "permission-lock"; }
            Check(rejected && s.Kernel.LoadedPlans == 0, "Native read permission was bypassed.");
        });
        Case("integration.causality-remains-runtime-owned", () => {
            using var s = new Scene(); s.OnRecord = _ => { if (s.Records.Count == 1) s.Break(); };
            s.Die(); s.Tick(); Check(s.Records.Count == 2 && s.Records[1].CauseId == s.Records[0].CommandId
                && s.Records[1].RootEventId == s.Records[0].RootEventId, "Canonical cause/root propagation was lost.");
        });
        Case("integration.queue-rejection-does-not-reopen-fact", () => {
            using var s = new Scene(load: false); Enemies.EnemyAgent? last = null;
            // The per-plan budget, not the kernel ceiling, bounds this plan: 16 queued, at most 4 dispatched per tick.
            s.Load("death_started", budget: new RuntimeLimits { MaxEventsPerTick = 4, MaxCommandsPerTick = 8, MaxQueuedEvents = 16, MaxCausalDepth = 2 });
            for (int i = 0; i < 17; i++) { last = Scene.NewEnemy((ushort)(100 + i), 1000 + i);
                s.Module.TrackSpawn(last); s.Die(last); }
            for (int i = 1; i <= 8; i++) s.Tick(i);
            s.Die(last!); s.Tick(9);
            Check(s.Records.Count == 16 && s.Messages.Any(x => x.Contains("rejected")), "Dropped/rejected fact retried or hidden.");
        });
        Case("integration.queued-death-cannot-target-respawn", () => {
            using var s = new Scene(); s.Die(); s.Module.TrackDespawn(s.Enemy); s.Enemy.Alive = true;
            s.Module.TrackSpawn(s.Enemy); s.Tick(); Check(s.Records.Count == 0, "Queued old-life fact targeted respawn.");
        });
        // Fact -> heal against the real receiver through the real kernel: the fact's single subject is wrapped into heal's
        // many-valued targets, amount is the literal 5 and overheal_policy the clamp index.
        Case("integration.real-heal-limb_broken", () => {
            using var s = new Scene(load: false); LocalPlan.Load(s.Kernel, Scene.HealPlan(s.Kernel, "limb_broken"));
            s.Break(); var tick = s.Tick(); var damage = s.Enemy.Damage;
            Check(tick.Commands.Count == 1, $"Expected one heal command, got {tick.Commands.Count}.");
            var result = tick.Commands[0].Result; var row = result.Outputs.GetProperty("results").EnumerateArray().Single();
            Check(result.Status == "succeeded" && result.CommitState == CommitStates.Confirmed
                && row.GetProperty("actualAmount").GetDouble() == 5 && damage.Health == 55 && damage.Sends == 1,
                $"Limb fact did not heal exactly +5 HP once: status={result.Status}; commit={result.CommitState}; row={row}; health={damage.Health}; sends={damage.Sends}");
            var later = s.Tick(2);
            Check(later.Commands.Count == 0 && damage.Sends == 1 && damage.Health == 55, "A consumed limb fact healed again on a later tick.");
        });
        Case("integration.real-heal-death_started", () => {
            using var s = new Scene(load: false); LocalPlan.Load(s.Kernel, Scene.HealPlan(s.Kernel, "death_started"));
            s.Die(); var tick = s.Tick(); var damage = s.Enemy.Damage;
            Check(tick.Commands.Count == 1, $"Expected one heal command, got {tick.Commands.Count}.");
            var result = tick.Commands[0].Result;
            Check(result.Status == "rejected" && result.CommitState == CommitStates.None && result.Code == "not-alive"
                && damage.Sends == 0 && damage.Health == 50,
                $"Death fact healing revived or wrote: status={result.Status}; commit={result.CommitState}; code={result.Code}; health={damage.Health}; sends={damage.Sends}");
        });
        Case("integration.native-hooks-delegate-exactly-once", Hooks);
    }

    private static void Hooks()
    {
        var k = new RuntimeKernel(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
        k.BeginWorld(1); k.RegisterModule(CombatContracts.Module());
        using var session = EnemyPluginSession.Start(k, () => true, _ => { }, () => { }, () => { });
        var rows = new List<CommandContext>();
        using var sink = k.RegisterModule(LocalPlan.Recorder(rows.Add));
        var actor = Scene.NewEnemy(); session.Module.TrackSpawn(actor);
        foreach (var suffix in new[] { "death_started", "limb_broken" }) LocalPlan.Load(k, Scene.FactPlan(k, suffix));
        k.StartRuntime(() => { });
        var sessionProperty = typeof(ForgeEnemy.Native.Plugin).GetProperty("Session", BindingFlags.Static | BindingFlags.NonPublic)!;
        sessionProperty.SetValue(null, session);
        try
        {
            void Invoke(Type type, object instance, Action original)
            {
                var args = new object?[] { instance, null };
                type.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
                original();
                var post = type.GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)!;
                post.Invoke(null, args); post.Invoke(null, args);
            }
            Invoke(typeof(EnemyDeathStarted), actor, actor.OnDead);
            Invoke(typeof(EnemyLimbBroken), actor.Damage.DamageLimbs[0], actor.Damage.DamageLimbs[0].DestroyLimb);
            k.Advance(1, true);
            Check(rows.Count == 2 && !session.Faulted && rows[1].Inputs.GetProperty("limb_id").GetInt32() == 0,
                "Native hook adapters did not publish the real provider facts exactly once.");
        }
        finally { sessionProperty.SetValue(null, null); k.StopRuntime(); }
    }
}
