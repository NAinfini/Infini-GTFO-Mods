using ForgeRuntime.Framework;
using ForgeTrigger.Targeting;

internal static class ObserverContractTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(string name, string code, Action action)
        {
            try { action(); check(false, name + " accepted"); }
            catch (RuntimeContractException error) { check(error.Code == code, name + ": " + error.Code); }
        }
        var entity = ObservationWorld.At("test.observer:one"); var reference = entity.Ref;
        using (var world = new ObservationWorld(new[] { entity }))
        {
            var k = world.Kernel;
            check(ObservedEntityNodes.Exists(k, reference), "exists current entity");
            check(!ObservedEntityNodes.Exists(k, reference with { LifeEpoch = 2 }), "exists does not rebind wrong life");
            check(!ObservedEntityNodes.Exists(k, reference with { Id = "test.observer:missing" }), "exists known namespace missing entity");
            world.OnObserve = _ => null;
            check(k.InspectEntities(new[] { reference }).Items.Single().Code == "entity-observation-unavailable", "observer null remains unknown");
            Reject("exists unknown is not false", "entity-query-incomplete", () => ObservedEntityNodes.Exists(k, reference));
            world.OnObserve = _ => throw new InvalidOperationException("synthetic failure");
            check(k.InspectEntities(new[] { reference }).Items.Single().Code == "entity-observer-failed", "observer exception remains unknown");
            world.OnObserve = _ => ObservationWorld.At(reference.Id, life: 3);
            check(k.InspectEntities(new[] { reference }).Items.Single().Code == "entity-observation-mismatch", "observer cannot substitute a new life");
            world.OnObserve = _ => { world.Entities[reference.Id] = ObservationWorld.At(reference.Id, life: 2); return entity; };
            check(k.InspectEntities(new[] { reference }).Items.Single().Code == "stale-entity", "post-observation resolver is rechecked");
            world.OnObserve = null;
            check(ObservedEntityNodes.Exists(k, reference with { LifeEpoch = 2 }), "failed callback does not poison later queries");
            world.Handle.Dispose();
            Reject("unregistered namespace unknown", "entity-query-incomplete", () => ObservedEntityNodes.Exists(k, reference));
        }
        using (var world = new ObservationWorld(new[] { entity }))
        {
            var k = world.Kernel; var refs = Enumerable.Repeat(reference, 256).ToArray();
            for (var i = 0; i < 4; i++) check(k.InspectEntities(refs).IsComplete, "raw reference budget batch " + i);
            check(world.Observations == 4, "each query deduplicates observers but not raw input cost");
            Reject("shared reference budget", "entity-query-tick-budget", () => ObservedEntityNodes.Exists(k, reference));
            k.Advance(0, true);
            Reject("repeated tick keeps reference budget", "entity-query-tick-budget", () => ObservedEntityNodes.Exists(k, reference));
        }
        Registration(check);
        CallbackGuard(check);
    }
    private static void Registration(Action<bool, string> check)
    {
        var k = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "registration-tests"));
        var entity = ObservationWorld.At("test.owner:one");
        var resolvers = new Dictionary<string, Func<EntityReference, bool>> { ["test.owner"] = r => r == entity.Ref };
        var observers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { ["test.owner"] = _ => entity };
        void RejectAtomic(string name, RuntimeModule module)
        {
            var before = k.ExportManifest();
            try { k.RegisterModule(module, RuntimeLogLevel.Off); check(false, name + " accepted"); }
            catch (RuntimeContractException) { check(k.ExportManifest() == before, name + " is atomic"); }
        }
        RejectAtomic("observer without resolver", ObservationWorld.Definition("test.orphan", new Dictionary<string, Func<EntityReference, bool>>(), observers));
        var nullObserver = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { ["test.owner"] = null! };
        RejectAtomic("null observer", ObservationWorld.Definition("test.null", resolvers, nullObserver));
        var handle = k.RegisterModule(ObservationWorld.Definition("test.owner.module", resolvers, observers), RuntimeLogLevel.Off);
        RejectAtomic("observer stealing resolver", ObservationWorld.Definition("test.thief", new Dictionary<string, Func<EntityReference, bool>>(), observers));
        handle.Dispose();
        var replacements = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { ["test.owner"] = _ => entity };
        using var replacement = k.RegisterModule(ObservationWorld.Definition("test.replacement", resolvers, replacements), RuntimeLogLevel.Off);
        // Mutating supplied dictionaries after registration must not change the selected callbacks.
        replacements["test.owner"] = _ => null;
        k.StartRuntime(() => k.BeginWorld(1)); k.Advance(0, true);
        check(k.InspectEntities(new[] { entity.Ref }).RequireComplete().Single().Ref == entity.Ref, "observer copy and namespace reuse after unload");
        k.StopRuntime();
        var budgetKernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "observer-budget"));
        var manyResolvers = Enumerable.Range(0, 257).ToDictionary(i => "test.n" + i, i => (Func<EntityReference, bool>)(_ => true));
        var manyObservers = manyResolvers.Keys.ToDictionary(id => id, id => (Func<EntityReference, RuntimeEntitySnapshot?>)(_ => null));
        var baseline = budgetKernel.ExportManifest();
        try { budgetKernel.RegisterModule(ObservationWorld.Definition("test.large", manyResolvers, manyObservers), RuntimeLogLevel.Off); check(false, "observer capacity ignored"); }
        catch (RuntimeContractException error) { check(error.Code == "entity-observer-budget" && baseline == budgetKernel.ExportManifest(), "observer capacity rejects atomically"); }
        budgetKernel.StopRuntime();
    }
    private static void CallbackGuard(Action<bool, string> check)
    {
        var entity = ObservationWorld.At("test.guard:one");
        using var world = new ObservationWorld(new[] { entity });
        var k = world.Kernel; var nested = false;
        var actions = new (string Name, Action Invoke)[]
        {
            ("recursive query", () => k.InspectEntities(new[] { entity.Ref })),
            ("world replacement", () => k.BeginWorld(2)),
            ("module disposal", () => world.Handle.Dispose()),
            ("runtime stop", () => k.StopRuntime()),
            ("advance", () => k.Advance(1, true)),
            ("scope cancellation", () => world.Handle.CancelScope("guard-scope"))
        };
        foreach (var action in actions)
        {
            var invoked = false; var blocked = false;
            world.OnObserve = _ =>
            {
                if (nested) return entity; // Deliberately bounded even when the candidate guard is broken.
                invoked = true; nested = true;
                try { action.Invoke(); }
                catch (RuntimeContractException error) { blocked = error.Code == "entity-observer-mutation"; }
                finally { nested = false; }
                check(k.ExportManifest().Length > 0, "read-only manifest remains allowed");
                return entity;
            };
            var query = k.InspectEntities(new[] { entity.Ref });
            check(invoked && blocked && query.IsComplete, "observer blocks " + action.Name);
        }
        world.OnObserve = null;
        using var subscription = world.Handle.ObserveLifecycle(_ => { });
        world.OnObserve = _ =>
        {
            try { subscription.Dispose(); check(false, "entity observer disposed lifecycle subscription"); }
            catch (RuntimeContractException error) { check(error.Code == "entity-observer-mutation", "lifecycle subscription mutation rejected"); }
            return entity;
        };
        check(k.InspectEntities(new[] { entity.Ref }).IsComplete && subscription.IsActive, "subscription remains intact");
        world.OnObserve = null;
        var resolverBlocked = false;
        world.OnResolve = _ =>
        {
            try { world.Handle.CancelScope("resolver-scope"); }
            catch (RuntimeContractException error) { resolverBlocked = error.Code == "entity-observer-mutation"; }
            return true;
        };
        check(k.InspectEntities(new[] { entity.Ref }).IsComplete && resolverBlocked, "resolvers share callback mutation protection");
        world.OnResolve = null;
        check(k.WorldEpoch == 1 && k.CurrentTick == 0 && k.QueuedEvents == 0, "observations did not alter world or queued work");
    }
}
