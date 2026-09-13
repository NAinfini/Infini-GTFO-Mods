using ForgeRuntime.Framework;
using static Checks;

internal static class ObserverRegistrationCases
{
    private static readonly EntityReference Ref = new("test.entity:1", 1, 1);
    private static RuntimeKernel Kernel()
    {
        var k = new RuntimeKernel(new("forge.test", "1.0.0", "1.0.0", "synthetic"));
        k.BeginWorld(1); return k;
    }
    private static RuntimeEntitySnapshot Snapshot(string kind)
        => new(Ref, kind, null, "alive", Array.Empty<string>(), Array.Empty<string>(), new double[] { 0, 0, 0 });
    private static RuntimeModule Module(string id, string[] names, bool resolvers = true)
        => new("1.0.0", RuntimeJson.From(new
        {
            providers = new[] { new { id, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            resolvers ? names.ToDictionary(n => n, _ => (Func<EntityReference, bool>)(r => r == Ref)) : null)
        {
            EntityObservers = names.ToDictionary(n => n,
                _ => (Func<EntityReference, RuntimeEntitySnapshot?>)(_ => Snapshot("test")))
        };
    private static void RejectAtomic(RuntimeKernel k, RuntimeModule module, string code)
    {
        string before = k.ExportManifest(); Code(() => k.RegisterModule(module), code);
        Require(k.ExportManifest() == before, "Rejected observer registration mutated the registry.");
    }
    internal static void Run()
    {
        Case("registration.requires-owned-resolver", () =>
            RejectAtomic(Kernel(), Module("test.owner", new[] { "test.entity" }, false), "entity-observer-owner"));
        Case("registration.foreign-resolver", () =>
        {
            var k = Kernel(); using var a = k.RegisterModule(Module("test.first", new[] { "test.entity" }));
            RejectAtomic(k, Module("test.second", new[] { "test.entity" }, false), "entity-observer-owner");
        });
        Case("registration.null-observer", () =>
        {
            var m = Module("test.owner", new[] { "test.entity" }) with
            { EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { ["test.entity"] = null! } };
            RejectAtomic(Kernel(), m, "entity-observer");
        });
        Case("registration.invalid-namespace", () =>
        {
            var m = Module("test.owner", new[] { "test.entity" }) with
            { EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { ["../escape"] = _ => null } };
            RejectAtomic(Kernel(), m, "entity-observer");
        });
        Case("registration.module-budget", () =>
        {
            var names = Enumerable.Range(0, RuntimeKernel.MaximumEntityObservers + 1).Select(i => "test.item" + i).ToArray();
            RejectAtomic(Kernel(), Module("test.owner", names), "entity-observer-budget");
        });
        Case("registration.global-budget", () =>
        {
            var k = Kernel();
            using var a = k.RegisterModule(Module("test.first", Enumerable.Range(0, 128).Select(i => "test.first" + i).ToArray()));
            using var b = k.RegisterModule(Module("test.second", Enumerable.Range(0, 128).Select(i => "test.second" + i).ToArray()));
            RejectAtomic(k, Module("test.third", new[] { "test.extra" }), "entity-observer-budget");
        });
        Case("registration.dispose-cleans-observer", () =>
        {
            var k = Kernel(); int oldCalls = 0, newCalls = 0;
            var a = Module("test.old", new[] { "test.entity" }) with
            { EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                { ["test.entity"] = _ => { oldCalls++; return Snapshot("old"); } } };
            var old = k.RegisterModule(a); old.Dispose(); old.Dispose();
            var b = Module("test.new", new[] { "test.entity" }) with
            { EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                { ["test.entity"] = _ => { newCalls++; return Snapshot("new"); } } };
            using var fresh = k.RegisterModule(b);
            k.StartRuntime(() => { });
            Require(k.InspectEntities(new[] { Ref }).RequireComplete().Single().Kind == "new"
                && oldCalls == 0 && newCalls == 1, "Old observer survived unregister or shadowed its replacement.");
        });
        Case("registration.entity-callback-cannot-dispose-lifecycle", () =>
        {
            var k = Kernel(); RuntimeLifecycleSubscription? subscription = null;
            var m = Module("test.owner", new[] { "test.entity" }) with
            { EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                { ["test.entity"] = _ => { Code(() => subscription!.Dispose(), "entity-observer-mutation"); return Snapshot("test"); } } };
            using var owner = k.RegisterModule(m);
            subscription = owner.ObserveLifecycle(_ => { }); k.StartRuntime(() => { });
            Require(k.InspectEntities(new[] { Ref }).IsComplete && subscription.IsActive,
                "A read-only entity callback cancelled the lifecycle subscription.");
            subscription.Dispose(); Require(!subscription.IsActive, "Ordinary cleanup was blocked.");
        });
        Case("registration.lifecycle-self-disposal-remains-legal", () =>
        {
            var k = Kernel(); using var owner = k.RegisterModule(Module("test.owner", new[] { "test.entity" }));
            RuntimeLifecycleSubscription? subscription = null;
            subscription = owner.ObserveLifecycle(e =>
            { if (e.Kind == RuntimeLifecycleKind.TickAdvanced) subscription!.Dispose(); }, false);
            k.StartRuntime(() => { }); k.Advance(1, true);
            Require(!subscription.IsActive && k.LifecycleFaultCount == 0, "Lifecycle self-disposal regressed.");
        });
        Case("registration.failure-does-not-reserve-namespace", () =>
        {
            var k = Kernel();
            RejectAtomic(k, Module("test.owner", new[] { "test.entity" }, false), "entity-observer-owner");
            using var valid = k.RegisterModule(Module("test.owner", new[] { "test.entity" }));
            k.StartRuntime(() => { });
            Require(k.InspectEntities(new[] { Ref }).IsComplete, "Failed registration left a partial reservation.");
        });
    }
}
