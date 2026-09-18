using System.Text.Json;
using ForgeRuntime.Framework;

internal static class ObservationDataTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("FAIL: " + name); }
        void Reject(string code, Action action, string name)
        {
            checks++;
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} [{error.Code} expected {code}]");
                return;
            }
            throw new Exception("FAIL: accepted " + name);
        }

        var player = new EntityReference("gtfo.player:1", 1, 1);
        var enemy = new EntityReference("gtfo.enemy:2", 1, 1);
        var noHealth = new EntityReference("gtfo.player:3", 1, 1);
        var equipment = new EntityReference("gtfo.equipment:4", 1, 1);
        using var world = new World(new[]
        {
            Snapshot(player, "gtfo.player", 42, 100),
            Snapshot(enemy, "gtfo.enemy", 30, 120),
            Snapshot(noHealth, "gtfo.player", null, null),
            Snapshot(equipment, "gtfo.equipment", 10, 10)
        });

        var playerValue = ObservationContracts.EntityHealth(world.Context(player));
        Check(Math.Abs(playerValue.GetProperty("value").GetDouble() - 42) < 0.0001
            && Math.Abs(playerValue.GetProperty("maximum").GetDouble() - 100) < 0.0001
            && Math.Abs(playerValue.GetProperty("fraction").GetDouble() - 0.42) < 0.0001,
            "generic health reads one player snapshot and derives its fraction");

        var enemyValue = ObservationContracts.EntityHealth(world.Context(enemy));
        Check(Math.Abs(enemyValue.GetProperty("value").GetDouble() - 30) < 0.0001
            && Math.Abs(enemyValue.GetProperty("maximum").GetDouble() - 120) < 0.0001
            && Math.Abs(enemyValue.GetProperty("fraction").GetDouble() - 0.25) < 0.0001,
            "generic health reads one enemy snapshot and derives its fraction");

        Reject(ObservationContracts.HealthUnavailableCode,
            () => ObservationContracts.EntityHealth(world.Context(noHealth)),
            "health without a published reading refuses");
        Reject(ObservationContracts.HealthKindUnsupportedCode,
            () => ObservationContracts.EntityHealth(world.Context(equipment)),
            "health is not silently widened to every entity kind");

        var module = ObservationContracts.Module();
        var registry = RuntimeJson.Parse(module.RegistryJson);
        var healthRow = RuntimeJson.Rows(registry, "capabilities")
            .Single(row => RuntimeJson.Text(row, "id") == ObservationContracts.EntityHealthCapability);
        Check(JsonSerializer.Serialize(healthRow.GetProperty("graph"))
                == JsonSerializer.Serialize(BehaviorOperatorGraphSource.Get(ObservationContracts.EntityHealthCapability)),
            "runtime health row consumes the generated Behavior Operator graph");
        Check(!module.RegistryJson.Contains("forge.query.player.health", StringComparison.Ordinal)
            && !module.RegistryJson.Contains("forge.query.enemy.health", StringComparison.Ordinal),
            "runtime observation registry contains no old domain health query ids");
        Check(module.Evaluators.Keys.OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(new[] { ObservationContracts.EntityHealthHandler,
                    ObservationContracts.EntityPositionHandler }.OrderBy(x => x, StringComparer.Ordinal)),
            "observation provider owns exactly position and health evaluators");

        return checks;
    }

    private static RuntimeEntitySnapshot Snapshot(EntityReference reference, string kind,
        double? health, double? maximum) => new(reference, kind, null, "alive",
        Array.Empty<string>(), Array.Empty<string>(), new[] { 1d, 2d, 3d }, health, maximum);
    private sealed class World : IDisposable
    {
        private readonly RuntimeModuleHandle handle;
        internal RuntimeKernel Kernel { get; }
        private readonly Dictionary<EntityReference, RuntimeEntitySnapshot> table;

        internal World(IEnumerable<RuntimeEntitySnapshot> snapshots)
        {
            table = snapshots.ToDictionary(x => x.Ref, x => x);
            Kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0",
                RuntimeKernel.ApiVersion, "test-health"));
            var kinds = new[] { "gtfo.player", "gtfo.enemy", "gtfo.equipment" };
            handle = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion,
                RuntimeJson.From(new
                {
                    providers = new[] { new { id = "test.health.entities", kind = "extension",
                        version = "1.0.0", dependencies = Array.Empty<string>() } },
                    capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
                }).GetRawText(),
                new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
                kinds.ToDictionary(k => k, k => new Func<EntityReference, bool>(
                    reference => reference.Id.StartsWith(k + ":", StringComparison.Ordinal) && table.ContainsKey(reference))))
            {
                EntityObservers = kinds.ToDictionary(k => k,
                    k => new Func<EntityReference, RuntimeEntitySnapshot?>(
                        reference => reference.Id.StartsWith(k + ":", StringComparison.Ordinal)
                            && table.TryGetValue(reference, out var snapshot) ? snapshot : null))
            }, RuntimeLogLevel.Off);
            Kernel.RegisterBuiltinModule(ObservationContracts.Module());
            Kernel.StartRuntime(() => Kernel.BeginWorld(1));
            Kernel.Advance(0, true);
        }

        internal EvaluationContext Context(EntityReference reference)
            => new("Health", RuntimeJson.EmptyObject, RuntimeJson.From(new { entity = reference }),
                new RuntimeQuerySession(Kernel, "test.observation.health", true),
                new RuntimeActorContext(new Dictionary<string, EntityReference>()),
                new RuntimeFactionRelations(Array.Empty<RuntimeFactionRelation>()), 0, _ => null);

        public void Dispose()
        {
            Kernel.StopRuntime();
            handle.Dispose();
        }
    }
}
