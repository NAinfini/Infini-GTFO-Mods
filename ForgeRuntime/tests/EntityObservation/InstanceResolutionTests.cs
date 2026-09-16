using ForgeRuntime.Framework;

// Native-instance lookup through the owning provider. The instances are managed stand-ins; no GTFO object is used.
static class InstanceResolutionTests
{
    private const string Secret = "76561198000000000";
    private sealed class Native { public override string ToString() => "native-" + Secret; }

    private static RuntimeKernel Kernel() => new(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "synthetic"));
    private static string Registry(string provider)
        => "{\"providers\":[{\"id\":\"" + provider + "\",\"kind\":\"extension\",\"version\":\"1.0.0\",\"dependencies\":[]}],\"capabilities\":[],\"bindings\":[]}";
    private static RuntimeModule Module(string provider, Dictionary<string, Func<EntityReference, bool>>? resolvers,
        Dictionary<string, Func<object, EntityReference?>>? instances)
        => new(RuntimeKernel.ApiVersion, Registry(provider), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(), resolvers)
        { EntityInstanceResolvers = instances };
    private static string? Code(Action action)
    {
        try { action(); return null; }
        catch (RuntimeContractException error) { return error.Code; }
    }

    private sealed class World
    {
        public readonly RuntimeKernel Kernel = InstanceResolutionTests.Kernel();
        public readonly Native Instance = new();
        public readonly HashSet<EntityReference> Live = new();
        public Func<object, EntityReference?> Answer;
        public Func<EntityReference, bool>? Owner;
        public int Calls;
        public RuntimeModuleHandle Handle = null!;
        public World()
        {
            var reference = new EntityReference("test.entity:1", 1, 1);
            Live.Add(reference);
            Answer = value => ReferenceEquals(value, Instance) ? reference : null;
        }
        public World Register(bool start = true, bool begin = true)
        {
            if (begin) Kernel.BeginWorld(1);
            Handle = Kernel.RegisterModule(Module("test.entities",
                new() { ["test.entity"] = r => Owner?.Invoke(r) ?? Live.Contains(r) },
                new() { ["test.entity"] = value => { Calls++; return Answer(value); } }), RuntimeLogLevel.Off);
            if (start) Kernel.StartRuntime(() => { });
            return this;
        }
    }

    public static void Run()
    {
        Registration(); Readiness(); Failures(); Answers(); Mutation(); Currency(); Manifest();
    }

    private static void Registration()
    {
        var kernel = Kernel(); Func<object, EntityReference?> any = _ => null;
        Check.Reject(() => kernel.RegisterModule(Module("test.a", null, new() { ["test.entity"] = any }), RuntimeLogLevel.Off),
            "instance resolver without any resolver", "entity-instance-resolver-owner");
        Check.Reject(() => kernel.RegisterModule(Module("test.a", new() { ["test.entity"] = _ => true }, new() { ["test.other"] = any }), RuntimeLogLevel.Off),
            "instance resolver for a namespace this provider does not resolve", "entity-instance-resolver-owner");
        Check.Reject(() => kernel.RegisterModule(Module("test.a", new() { ["test.entity"] = _ => true }, new() { ["Bad"] = any }), RuntimeLogLevel.Off),
            "invalid instance resolver namespace", "entity-instance-resolver");
        Check.Reject(() => kernel.RegisterModule(Module("test.a", new() { ["test.entity"] = _ => true }, new() { ["test.entity"] = null! }), RuntimeLogLevel.Off),
            "null instance resolver", "entity-instance-resolver");
        var owner = kernel.RegisterModule(Module("test.a", new() { ["test.entity"] = _ => true }, new() { ["test.entity"] = any }), RuntimeLogLevel.Off);
        Check.That(owner.IsRegistered, "failed registrations were atomic; the provider id is still free");
        Check.Reject(() => kernel.RegisterModule(Module("test.b", null, new() { ["test.entity"] = any }), RuntimeLogLevel.Off),
            "another provider cannot attach an instance resolver to an owned namespace", "entity-instance-resolver-owner");
        Check.Reject(() => kernel.RegisterModule(Module("test.b", new() { ["test.entity"] = _ => true }, new() { ["test.entity"] = any }), RuntimeLogLevel.Off),
            "another provider cannot take over the namespace with its own resolver", "entity-namespace-conflict");
        var instance = new Native(); int ownerCalls = 0, otherCalls = 0;
        var probe = Kernel(); probe.BeginWorld(1);
        probe.RegisterModule(Module("test.a", new() { ["test.a_entity"] = _ => true },
            new() { ["test.a_entity"] = _ => { ownerCalls++; return null; } }), RuntimeLogLevel.Off);
        probe.RegisterModule(Module("test.b", new() { ["test.b_entity"] = _ => true },
            new() { ["test.b_entity"] = _ => { otherCalls++; return new EntityReference("test.b_entity:1", 1, 1); } }), RuntimeLogLevel.Off);
        probe.StartRuntime(() => { });
        Check.That(probe.ResolveEntityInstance("test.a_entity", instance) == null && ownerCalls == 1 && otherCalls == 0,
            "only the owning provider is asked; a null answer never falls back to another provider");

        var world = new World().Register();
        Check.That(world.Kernel.ResolveEntityInstance("test.entity", world.Instance) == new EntityReference("test.entity:1", 1, 1),
            "registered instance resolver answers before unregistration");
        world.Handle.Dispose();
        Check.That(world.Kernel.ResolveEntityInstance("test.entity", world.Instance) == null,
            "unregistering the provider removes its instance resolver");
    }

    private static void Readiness()
    {
        var registering = new World().Register(start: false);
        Check.That(registering.Kernel.ResolveEntityInstance("test.entity", registering.Instance) == null && registering.Calls == 0,
            "registering runtime returns null without asking the provider");
        var noWorld = new World().Register(begin: false);
        Check.That(noWorld.Kernel.ResolveEntityInstance("test.entity", noWorld.Instance) == null && noWorld.Calls == 0,
            "ready runtime without a world returns null without asking the provider");

        var failed = Kernel();
        failed.RegisterModule(Module("test.entities", new() { ["test.entity"] = _ => true }, new() { ["test.entity"] = _ => null }), RuntimeLogLevel.Off);
        try { failed.StartRuntime(() => throw new InvalidOperationException("startup")); } catch (InvalidOperationException) { }
        Check.Reject(() => failed.ResolveEntityInstance("test.entity", new Native()), "failed runtime rejects instance lookup", "runtime-not-ready");
        Check.Reject(() => failed.IsEntityCurrent(new EntityReference("test.entity:1", 0, 1)), "failed runtime rejects currency check", "runtime-not-ready");
        var stopped = new World().Register(); stopped.Kernel.StopRuntime();
        Check.Reject(() => stopped.Kernel.ResolveEntityInstance("test.entity", stopped.Instance), "stopped runtime rejects instance lookup", "runtime-not-ready");
        Check.Reject(() => stopped.Kernel.IsEntityCurrent(new EntityReference("test.entity:1", 1, 1)), "stopped runtime rejects currency check", "runtime-not-ready");

        var world = new World().Register();
        string? lookupCode = null, currentCode = null;
        var thread = new Thread(() =>
        {
            lookupCode = Code(() => world.Kernel.ResolveEntityInstance("test.entity", world.Instance));
            currentCode = Code(() => world.Kernel.IsEntityCurrent(new EntityReference("test.entity:1", 1, 1)));
        });
        thread.Start(); thread.Join();
        Check.That(lookupCode == "wrong-thread" && currentCode == "wrong-thread" && world.Calls == 0, "foreign thread rejected before the provider runs");
        Check.Reject(() => world.Kernel.ResolveEntityInstance(null!, world.Instance), "null kind");
        Check.Reject(() => world.Kernel.ResolveEntityInstance("test.entity", null!), "null instance");
        Check.Reject(() => world.Kernel.IsEntityCurrent(null!), "null reference");
    }

    private static void Failures()
    {
        var world = new World().Register();
        // Ruling 53: an optional domain package that is not installed leaves the kind unresolved. Only a kind name
        // no provider could ever register is a contract violation, and it is refused before the registry is read.
        Check.That(world.Kernel.ResolveEntityInstance("test.unknown", world.Instance) == null,
            "unknown kind has no instance resolver, which is unresolved rather than refused");
        foreach (var malformed in new[] { "test", "Test.entity", "test..entity", "test.entity." })
            Check.Reject(() => world.Kernel.ResolveEntityInstance(malformed, world.Instance),
                "malformed kind name " + malformed, "entity-resolver");
        var observed = Kernel(); observed.BeginWorld(1);
        observed.RegisterModule(Module("test.entities", new() { ["test.entity"] = _ => true }, null), RuntimeLogLevel.Off);
        observed.StartRuntime(() => { });
        Check.That(observed.ResolveEntityInstance("test.entity", new Native()) == null,
            "resolver-only namespace does not enumerate or guess an instance");

        world.Answer = _ => throw new InvalidOperationException("account " + Secret);
        RuntimeContractException? error = null;
        try { world.Kernel.ResolveEntityInstance("test.entity", world.Instance); }
        catch (RuntimeContractException caught) { error = caught; }
        Check.That(error?.Code == "entity-resolver-failed", "throwing instance resolver is a contract failure");
        Check.That(error != null && !error.ToString().Contains(Secret, StringComparison.Ordinal) && error.InnerException == null,
            "neither the resolver exception nor the instance reaches the error");
    }

    private static void Answers()
    {
        var routed = Kernel(); routed.BeginWorld(1);
        var other = new EntityReference("test.other:1", 1, 1);
        routed.RegisterModule(Module("test.entities", new() { ["test.entity"] = _ => true }, new() { ["test.entity"] = _ => other }), RuntimeLogLevel.Off);
        routed.RegisterModule(Module("test.others", new() { ["test.other"] = r => r == other }, null), RuntimeLogLevel.Off);
        routed.StartRuntime(() => { });
        Check.That(routed.IsEntityCurrent(other) && routed.ResolveEntityInstance("test.entity", new Native()) == null,
            "a current reference from another provider's namespace is still returned as null");

        var world = new World().Register();
        Check.That(world.Kernel.ResolveEntityInstance("test.entity", new Native()) == null, "resolver null is returned as null");
        foreach (var (id, name) in new[] { ("test.entity.sub:1", "longer namespace sharing the prefix"), ("test.entity", "missing separator") })
        {
            var foreign = new EntityReference(id, 1, 1); world.Live.Add(foreign);
            world.Answer = _ => foreign;
            Check.That(world.Kernel.ResolveEntityInstance("test.entity", world.Instance) == null, "wrong prefix returns null: " + name);
        }
        var stale = new EntityReference("test.entity:1", 0, 1); world.Live.Add(stale);
        world.Answer = _ => stale;
        Check.That(world.Kernel.ResolveEntityInstance("test.entity", world.Instance) == null, "reference from another world returns null");
        var dead = new EntityReference("test.entity:2", 1, 1);
        world.Answer = _ => dead;
        Check.That(world.Kernel.ResolveEntityInstance("test.entity", world.Instance) == null, "owner resolver rejecting the answer returns null");
        world.Answer = _ => new EntityReference("test.entity:1", 1, 1);
        world.Owner = _ => throw new InvalidOperationException(Secret);
        Check.That(world.Kernel.ResolveEntityInstance("test.entity", world.Instance) == null, "owner resolver failing on the answer returns null");
        world.Owner = null;
        Check.That(world.Kernel.ResolveEntityInstance("test.entity", world.Instance) == new EntityReference("test.entity:1", 1, 1),
            "verified answer is returned unchanged");
    }

    private static void Mutation()
    {
        var world = new World().Register();
        string?[] codes = new string?[3];
        world.Answer = _ =>
        {
            codes[0] = Code(() => world.Kernel.BeginWorld(9));
            codes[1] = Code(() => world.Kernel.ResolveEntityInstance("test.entity", world.Instance));
            codes[2] = Code(() => world.Kernel.IsEntityCurrent(new EntityReference("test.entity:1", 1, 1)));
            return new EntityReference("test.entity:1", 1, 1);
        };
        Check.That(world.Kernel.ResolveEntityInstance("test.entity", world.Instance) == new EntityReference("test.entity:1", 1, 1),
            "resolver that attempted kernel calls still gets its verified answer checked");
        Check.That(codes.All(code => code == "entity-observer-mutation") && world.Kernel.WorldEpoch == 1,
            "mutation, recursive lookup and currency checks inside the resolver are rejected");
        world.Owner = reference => { codes[0] = Code(() => world.Kernel.BeginWorld(9)); return world.Live.Contains(reference); };
        world.Answer = _ => new EntityReference("test.entity:1", 1, 1);
        codes[0] = null;
        Check.That(world.Kernel.ResolveEntityInstance("test.entity", world.Instance) != null && codes[0] == "entity-observer-mutation",
            "owner verification of the answer runs under the same guard");
        world.Owner = null;
        world.Answer = _ => throw new InvalidOperationException("fault");
        Code(() => world.Kernel.ResolveEntityInstance("test.entity", world.Instance));
        Check.That(Code(() => world.Kernel.BeginWorld(2)) == null && world.Kernel.WorldEpoch == 2, "guard is released after a failing resolver");
    }

    private static void Currency()
    {
        var world = new World().Register();
        var live = new EntityReference("test.entity:1", 1, 1);
        Check.That(world.Kernel.IsEntityCurrent(live), "current reference is current");
        Check.That(!world.Kernel.IsEntityCurrent(new EntityReference("test.entity:1", 1, 2)), "old life is not current");
        Check.That(!world.Kernel.IsEntityCurrent(new EntityReference("test.entity:1", 0, 1)), "other world is not current");
        Check.That(!world.Kernel.IsEntityCurrent(new EntityReference("test.unknown:1", 1, 1)), "unrouted namespace is not current");
        Check.That(!world.Kernel.IsEntityCurrent(new EntityReference(" bad", 1, 1)), "malformed reference is not current");
        world.Owner = _ => throw new InvalidOperationException(Secret);
        Check.That(!world.Kernel.IsEntityCurrent(live), "failing owner resolver is not current");
    }

    private static void Manifest()
    {
        var with = new World().Register(start: false);
        var without = Kernel(); without.BeginWorld(1);
        without.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry("test.entities"), new Dictionary<string, CommandHandler>(),
            Array.Empty<BindingSupport>(), new Dictionary<string, Func<EntityReference, bool>> { ["test.entity"] = _ => true }), RuntimeLogLevel.Off);
        Check.That(with.Kernel.ExportManifest() == without.ExportManifest(), "manifest does not expose instance resolvers");
    }
}
