using ForgeRuntime.Framework;
using ForgeTrigger.Targeting;

internal static class R3ConsumerTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(string name, string code, Action action)
        {
            try { action(); check(false, name + " accepted"); }
            catch (RuntimeContractException error) { check(error.Code == code, name + ": " + error.Code); }
        }
        using (var world = new World())
        {
            var k = world.Kernel; var roles = world.Actors;
            foreach (var role in new[] { "self", "source", "owner", "instigator", "event-target" })
            {
                var observation = k.InspectActor(roles, role);
                if (!observation.IsComplete)
                    throw new RuntimeContractException(observation.Code, "R3 registration: " + string.Join(", ", observation.Items.Select(row => row.Code)));
                check(ObservedEntityNodes.Actor(k, roles, role).Ref == roles.Get(role), "R3 explicit role " + role);
            }
            var target = roles.Get("event-target")!; var owner = roles.Get("owner")!;
            var sourceOnly = new RuntimeActorContext(new Dictionary<string, EntityReference> { ["source"] = roles.Get("source")! });
            Reject("R3 no owner fallback", "actor-missing", () => ObservedEntityNodes.Actor(k, sourceOnly, "owner"));
            Reject("R3 invalid role", "actor-role", () => ObservedEntityNodes.Actor(k, roles, "attacker"));
            check(ObservedEntityNodes.IsKind(k, target, "player"), "R3 observed kind");
            check(!ObservedEntityNodes.IsKind(k, target, "enemy"), "R3 kind comparison");
            check(ObservedEntityNodes.HasTag(k, target, "test-tag"), "R3 observed tag");
            check(!ObservedEntityNodes.HasTag(k, target, "TEST-TAG"), "R3 exact tag");
            check(ObservedEntityNodes.Relation(k, roles, "owner", target, world.Relations) == "hostile", "R3 directional hostility");
            check(ObservedEntityNodes.Relation(k, roles, "event-target", owner, world.Relations) == "ally", "R3 asymmetric reverse relation");
            check(ObservedEntityNodes.Relation(k, roles, "instigator", target, world.Relations) == "unknown", "R3 no inferred relation");
            check(ObservedEntityNodes.Relation(k, roles, "owner", owner, world.Relations) == "self", "R3 exact self identity");
            Reject("R3 missing relation anchor", "actor-missing", () => ObservedEntityNodes.Relation(k, sourceOnly, "owner", target, world.Relations));
            check(ObservedEntityNodes.RequireReceivers(k, new[] { owner, target }, "test.receiver.health").Count == 2,
                "R3 health receivers independent of kind and relationship");
            Reject("R3 unsupported receiver", "receiver-unsupported", () => ObservedEntityNodes.RequireReceivers(k, new[] { target }, "test.receiver.energy"));
            check(ObservedEntityNodes.RequireReceivers(k, Array.Empty<EntityReference>(), "test.receiver.health").Count == 0, "R3 empty is not world enumeration");
            Reject("R3 partial query cannot select", "entity-query-incomplete", () => ObservedEntityNodes.RequireReceivers(k,
                new[] { target, new EntityReference("test.entity:missing", 1, 1) }, "test.receiver.health"));
            Reject("R3 budget before dedup", "entity-query-budget", () => ObservedEntityNodes.RequireReceivers(k,
                Enumerable.Repeat(target, 257).ToArray(), "test.receiver.health"));
            var before = ObservedEntityNodes.Entity(k, target);
            world.Entities[target.Id] = world.Snapshot(target with { LifeEpoch = 2 }, "player", "red");
            Reject("R3 stale predicate is not false", "entity-query-incomplete", () => ObservedEntityNodes.HasTag(k, target, "test-tag"));
            Reject("R3 stale actor is not rebound", "entity-query-incomplete", () => ObservedEntityNodes.Actor(k, roles, "event-target"));
            check(before.Ref.LifeEpoch == 1 && ObservedEntityNodes.Entity(k, target with { LifeEpoch = 2 }).Ref.LifeEpoch == 2,
                "R3 fresh observation never modifies previous snapshot");
            check(k.QueuedEvents == 0 && k.LoadedPlans == 0, "R3 observations produce no gameplay work");
            k.BeginWorld(2); k.Advance(0, true);
            Reject("R3 stale world", "entity-query-incomplete", () => ObservedEntityNodes.Entity(k, owner));
        }
        using (var world = new World())
        {
            var k = world.Kernel; var target = world.Actors.Get("owner")!;
            for (var i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick; i++)
                ObservedEntityNodes.Entity(k, target);
            Reject("R3 shared query budget", "entity-query-tick-budget", () => ObservedEntityNodes.Entity(k, target));
            k.Advance(0, true);
            Reject("R3 same tick keeps budget", "entity-query-tick-budget", () => ObservedEntityNodes.Entity(k, target));
            k.Advance(1, true);
            check(ObservedEntityNodes.Entity(k, target).Ref == target, "R3 next tick resumes queries");
            world.Handle.Dispose();
            Reject("R3 unregistered observer", "entity-query-incomplete", () => ObservedEntityNodes.Entity(k, target));
        }
        using (var world = new World(false))
        {
            var k = world.Kernel;
            check(ObservedEntityNodes.Actor(k, world.Actors, "owner").Ref == world.Actors.Get("owner"), "R3 client observation is read-only");
            check(k.QueuedEvents == 0, "R3 client observation is not an action grant");
            k.StopRuntime();
            Reject("R3 stopped runtime", "runtime-not-ready", () => ObservedEntityNodes.Actor(k, world.Actors, "owner"));
        }
    }

    // Synthetic observation provider; only test code owns this dictionary.
    private sealed class World : IDisposable
    {
        internal readonly Dictionary<string, RuntimeEntitySnapshot> Entities = new();
        internal RuntimeKernel Kernel { get; }
        internal RuntimeModuleHandle Handle { get; }
        internal RuntimeActorContext Actors { get; }
        internal RuntimeFactionRelations Relations { get; } = new(new RuntimeFactionRelation[]
        {
            new("blue", "red", "hostile"), new("red", "blue", "ally"), new("blue", "blue", "ally")
        });
        internal RuntimeEntitySnapshot Snapshot(EntityReference reference, string kind, string faction)
            => new(reference, kind, faction, "alive", new[] { "test-tag" }, new[] { "test.receiver.health" }, new[] { 0d, 0d, 0d });
        internal World(bool host = true)
        {
            var actors = new Dictionary<string, EntityReference>();
            foreach (var role in new[] { "self", "source", "owner", "instigator", "event-target" })
            {
                var reference = new EntityReference("test.entity:" + role, 1, 1); actors[role] = reference;
                Entities[reference.Id] = Snapshot(reference, role == "owner" ? "enemy" : "player",
                    role == "event-target" ? "red" : role == "instigator" ? "green" : "blue");
            }
            Actors = new RuntimeActorContext(actors);
            Kernel = new(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "test-only-r3"));
            var module = new RuntimeModule(RuntimeKernel.ApiVersion,
                RuntimeJson.From(new { providers = new[] { new { id = "test.trigger.observations", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
                    capabilities = Array.Empty<object>(), bindings = Array.Empty<object>() }).GetRawText(),
                new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
                new Dictionary<string, Func<EntityReference, bool>> { ["test.entity"] = r => Entities.TryGetValue(r.Id, out var e) && e.Ref == r })
            {
                EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                { ["test.entity"] = r => Entities.TryGetValue(r.Id, out var e) ? e : null }
            };
            Handle = Kernel.RegisterModule(module);
            Kernel.StartRuntime(() => Kernel.BeginWorld(1));
            Kernel.Advance(0, host);
        }
        public void Dispose() { Kernel.StopRuntime(); Handle.Dispose(); }
    }
}
