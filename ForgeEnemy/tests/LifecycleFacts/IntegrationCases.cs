using System.Reflection;
using System.Text.Json;
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
            using var s = new Scene(load: false); bool rejected = false;
            try { s.Load(suffix, new[] { "test.record" }); }
            catch (RuntimeContractException e) { rejected = e.Code.Contains("permission"); }
            Check(rejected && s.Kernel.LoadedPlans == 0, "Native read permission was bypassed.");
        });
        Case("integration.causality-remains-runtime-owned", () => {
            using var s = new Scene(); s.OnRecord = _ => { if (s.Records.Count == 1) s.Break(); };
            s.Die(); s.Tick(); Check(s.Records.Count == 2 && s.Records[1].CauseId == s.Records[0].CommandId
                && s.Records[1].RootEventId == s.Records[0].RootEventId, "Canonical cause/root propagation was lost.");
        });
        Case("integration.queue-rejection-does-not-reopen-fact", () => {
            using var s = new Scene(); Enemies.EnemyAgent? last = null;
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
        foreach (string suffix in new[] { "death_started", "limb_broken" }) Case("integration.real-heal-" + suffix, () => {
            using var s = new Scene(load: false);
            var plan = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Scene.Fixtures, "native-heal.plan.json")))!;
            string id = suffix == "death_started" ? EnemyModule.DeathStartedBinding : EnemyModule.LimbBrokenBinding;
            string cap = suffix == "death_started" ? "forge.trigger.enemy.death_started" : "forge.trigger.combat.limb_broken";
            plan["bindings"]![0]!["bindingId"] = id; plan["bindings"]![0]!["capabilityId"] = cap;
            plan["bindings"]![0]!["handler"] = "gtfo.enemy." + suffix;
            plan["entrypoints"]![0]!["bindingId"] = id;
            plan["planId"] = "forge.example.enemy_" + suffix + "_heal";
            plan["resource"]!["id"] = "forge.example.enemy_" + suffix + "_heal";
            string read = suffix == "death_started" ? "gtfo.enemy.lifecycle.read" : "gtfo.enemy.limbs.read";
            plan["permissions"] = JsonSerializer.SerializeToNode(new[] { read, "gtfo.enemy.health.write" }.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            plan["bindings"] = new System.Text.Json.Nodes.JsonArray(plan["bindings"]!.AsArray()
                .Select(x => System.Text.Json.Nodes.JsonNode.Parse(x!.ToJsonString()))
                .OrderBy(x => x!["bindingId"]!.GetValue<string>(), StringComparer.Ordinal).ToArray());
            s.Kernel.LoadPlan(plan.ToJsonString(), new[] { read, "gtfo.enemy.health.write" });
            if (suffix == "death_started") s.Die(); else s.Break();
            var result = s.Tick().Commands.Single().Result;
            Check(suffix == "death_started" ? result.Code == "gtfo.enemy.not_alive" && s.Enemy.Damage.Sends == 0
                : result.Status == "succeeded" && result.Outputs.GetProperty("actualAmount").GetDouble() == 5
                    && s.Enemy.Damage.Health == 55 && s.Enemy.Damage.Sends == 1,
                "Event did not reach the real receiver or implicitly revived a dead enemy.");
            if (suffix == "limb_broken") File.WriteAllText(Path.Combine(T.OutputDirectory, "limb-broken-heal.plan.json"),
                plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        });
        Case("integration.native-hooks-delegate-exactly-once", Hooks);
    }

    private static void Hooks()
    {
        var k = new RuntimeKernel(new("forge.runtime", "1.2.0", "1.0.0", "20403457"));
        k.BeginWorld(1); k.RegisterModule(CombatContracts.Module());
        using var session = EnemyPluginSession.Start(k, () => true, _ => { }, () => { }, () => { });
        var rows = new List<CommandContext>();
        using var sink = k.RegisterModule(new("1.0.0", Fixture.SinkRegistry,
            new Dictionary<string, CommandHandler> { ["test.record"] = c =>
                { rows.Add(c); return CommandResult.Succeeded(RuntimeJson.EmptyObject); } },
            new[] { new BindingSupport("test.lifecycle.binding.record", "implementation-only", new[] { "test.record" }) }));
        var actor = Scene.NewEnemy(); session.Module.TrackSpawn(actor);
        foreach (var suffix in new[] { "death_started", "limb_broken" })
            k.LoadPlan(Fixture.Plan(Scene.Fixtures, suffix), new[] { "gtfo.enemy.lifecycle.read", "gtfo.enemy.limbs.read", "test.record" });
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
