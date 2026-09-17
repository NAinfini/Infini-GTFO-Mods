using ForgeRuntime.Framework;

// Object-to-entity resolution: the one entry point a caller that holds a native object rather than a kind uses.
// Each package answers for the native types it knows and returns null for everything else, so the packages never
// have to know about each other. The objects here are managed stand-ins; no GTFO type is used.
static class ObjectEntityResolutionTests
{
    private sealed class Native
    {
        public Native(string name) => Name = name;
        public string Name { get; }
        public override string ToString() => "native-" + Name;
    }

    private static RuntimeKernel Kernel() => new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "synthetic"));

    private static string Registry(string provider)
        => "{\"providers\":[{\"id\":\"" + provider + "\",\"kind\":\"extension\",\"version\":\"1.0.0\",\"dependencies\":[]}],\"capabilities\":[],\"bindings\":[]}";

    /// <summary>One package that owns one entity kind and answers for one of the two native objects it is asked
    /// about: the shape every domain package has when it registers its own object resolver.</summary>
    private static RuntimeModule Module(string provider, string kind, Func<object, EntityReference?>? resolve,
        Func<EntityReference, bool>? owns = null, int[]? calls = null)
        => new(RuntimeKernel.ApiVersion, Registry(provider), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>> { [kind] = owns ?? (_ => true) })
        {
            ObjectEntityResolvers = resolve == null ? null : new[]
            {
                ObjectEntityResolver.Of(value => { if (calls != null) calls[0]++; return resolve(value); })
            }
        };

    private static string? Code(Action action)
    {
        try { action(); return null; }
        catch (RuntimeContractException error) { return error.Code; }
    }

    public static void Run()
    {
        Order();
        Routing();
        Readiness();
        Failure();
    }

    /// <summary>Registration order is the whole contract: the first package that recognizes the object answers for
    /// it, and a package that does not recognize it is asked and passes.</summary>
    private static void Order()
    {
        var kernel = Kernel(); kernel.BeginWorld(1);
        var enemy = new Native("enemy"); var door = new Native("door");
        var enemyCalls = new int[1]; var doorCalls = new int[1];
        // The door package registers first, exactly like ForgeMap's own package would: it answers for doors only.
        kernel.RegisterModule(Module("gtfo.map", "gtfo.map_object",
            value => ReferenceEquals(value, door) ? new EntityReference("gtfo.map_object:7", 1, 1) : null, calls: doorCalls), RuntimeLogLevel.Off);
        kernel.RegisterModule(Module("gtfo.enemy", "gtfo.enemy",
            value => ReferenceEquals(value, enemy) ? new EntityReference("gtfo.enemy:3", 1, 1) : null, calls: enemyCalls), RuntimeLogLevel.Off);
        kernel.StartRuntime(() => { });
        Check.That(kernel.ResolveEntity(enemy) == new EntityReference("gtfo.enemy:3", 1, 1),
            "the package that recognizes the object answers, whichever order it registered in");
        Check.That(kernel.ResolveEntity(door) == new EntityReference("gtfo.map_object:7", 1, 1),
            "the other package answers for its own native type");
        Check.That(enemyCalls[0] == 1 && doorCalls[0] == 2,
            $"each object is answered by its own package without asking the others twice: {doorCalls[0]}/{enemyCalls[0]}");
        Check.That(kernel.ResolveEntity(new Native("neither")) == null, "an object no package recognizes is unanswered");
        Check.That(kernel.ResolveEntity(new object()) == null, "an object with no native type at all is unanswered");

        // One object both packages could name: the first registration wins, and the second is never asked.
        var shared = new Native("shared");
        var firstAsked = new int[1]; var secondAsked = new int[1];
        var ordered = Kernel(); ordered.BeginWorld(1);
        ordered.RegisterModule(Module("gtfo.map", "gtfo.map_object",
            _ => new EntityReference("gtfo.map_object:1", 1, 1), calls: firstAsked), RuntimeLogLevel.Off);
        ordered.RegisterModule(Module("gtfo.enemy", "gtfo.enemy",
            _ => new EntityReference("gtfo.enemy:1", 1, 1), calls: secondAsked), RuntimeLogLevel.Off);
        ordered.StartRuntime(() => { });
        Check.That(ordered.ResolveEntity(shared) == new EntityReference("gtfo.map_object:1", 1, 1) && secondAsked[0] == 0,
            "the first registration wins and the later package is not asked");
    }

    /// <summary>An answer is a reference like any other: the owning package's resolver decides whether it is
    /// current, and a package that is not installed at all leaves the object unanswered.</summary>
    private static void Routing()
    {
        var kernel = Kernel(); kernel.BeginWorld(1);
        var native = new Native("enemy");
        var owns = true;
        kernel.RegisterModule(Module("gtfo.enemy", "gtfo.enemy", _ => new EntityReference("gtfo.enemy:3", 1, 1), _ => owns), RuntimeLogLevel.Off);
        kernel.StartRuntime(() => { });
        Check.That(kernel.ResolveEntity(native) != null, "an answer the owning resolver accepts is returned");
        owns = false;
        Check.That(kernel.ResolveEntity(native) == null, "an answer the owning resolver rejects is unanswered");
        owns = true;
        // A package that is not installed is not a contract violation: the port that would have carried the
        // reference is simply left missing, which is what hit_candidate.target relies on.
        var absent = Kernel(); absent.BeginWorld(1);
        absent.RegisterModule(Module("gtfo.map", "gtfo.map_object", _ => new EntityReference("gtfo.enemy:3", 1, 1)), RuntimeLogLevel.Off);
        absent.StartRuntime(() => { });
        Check.That(absent.ResolveEntity(new Native("enemy")) == null,
            "an answer for a kind no provider registered is unanswered rather than refused");
        // A reference from another world is not this world's object, and a malformed one is not a reference.
        var otherWorld = Kernel(); otherWorld.BeginWorld(1);
        otherWorld.RegisterModule(Module("gtfo.enemy", "gtfo.enemy", _ => new EntityReference("gtfo.enemy:3", 0, 1)), RuntimeLogLevel.Off);
        otherWorld.StartRuntime(() => { });
        Check.That(otherWorld.ResolveEntity(new Native("enemy")) == null, "a reference from another world is unanswered");
        foreach (var malformed in new[] { "", "gtfo.enemy", " bad:1" })
        {
            var broken = Kernel(); broken.BeginWorld(1);
            broken.RegisterModule(Module("gtfo.enemy", "gtfo.enemy", _ => new EntityReference(malformed, 1, 1)), RuntimeLogLevel.Off);
            broken.StartRuntime(() => { });
            Check.That(broken.ResolveEntity(new Native("enemy")) == null, "a malformed reference is unanswered: [" + malformed + "]");
        }
    }

    /// <summary>A runtime that never started, a world that never began and a failed startup each leave the object
    /// unanswered without asking any package, and only a null object is refused as the caller's own error.</summary>
    private static void Readiness()
    {
        var registering = Kernel(); registering.BeginWorld(1);
        var calls = new int[1];
        registering.RegisterModule(Module("gtfo.enemy", "gtfo.enemy", _ => new EntityReference("gtfo.enemy:3", 1, 1), calls: calls), RuntimeLogLevel.Off);
        Check.That(registering.ResolveEntity(new Native("enemy")) == null && calls[0] == 0,
            "a registering runtime answers nothing and asks nobody");
        var noWorld = Kernel();
        noWorld.RegisterModule(Module("gtfo.enemy", "gtfo.enemy", _ => new EntityReference("gtfo.enemy:3", 1, 1), calls: calls), RuntimeLogLevel.Off);
        noWorld.StartRuntime(() => { });
        Check.That(noWorld.ResolveEntity(new Native("enemy")) == null && calls[0] == 0,
            "a ready runtime without a world answers nothing and asks nobody");
        var failed = Kernel(); failed.BeginWorld(1);
        failed.RegisterModule(Module("gtfo.enemy", "gtfo.enemy", _ => new EntityReference("gtfo.enemy:3", 1, 1), calls: calls), RuntimeLogLevel.Off);
        try { failed.StartRuntime(() => throw new InvalidOperationException("startup fixture")); } catch (InvalidOperationException) { }
        Check.Reject(() => failed.ResolveEntity(new Native("enemy")), "a failed runtime rejects object resolution", "runtime-not-ready");
        Check.That(calls[0] == 0, "a failed runtime asked no package");
        Check.Reject(() => registering.ResolveEntity(null!), "a null object is rejected");
        // A provider that unregisters takes its object resolver with it: an object it used to know is unanswered,
        // never answered by a package that is gone.
        var leaving = Kernel(); leaving.BeginWorld(1);
        var handle = leaving.RegisterModule(Module("gtfo.enemy", "gtfo.enemy", _ => new EntityReference("gtfo.enemy:3", 1, 1)), RuntimeLogLevel.Off);
        leaving.StartRuntime(() => { });
        Check.That(leaving.ResolveEntity(new Native("enemy")) != null, "a registered package answers before it unregisters");
        handle.Dispose();
        Check.That(leaving.ResolveEntity(new Native("enemy")) == null, "an unregistered package stops answering for its own objects");
    }

    /// <summary>A package's own lookup is untrusted input like every other table: one that throws is skipped and
    /// the objects it never recognized stay unanswered — never a failed resolution of the whole call.</summary>
    private static void Failure()
    {
        var kernel = Kernel(); kernel.BeginWorld(1);
        var door = new Native("door");
        kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry("gtfo.broken"),
            new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>> { ["gtfo.broken"] = _ => true })
        {
            ObjectEntityResolvers = new[] { ObjectEntityResolver.Of(_ => throw new InvalidOperationException("native table is gone")) }
        }, RuntimeLogLevel.Off);
        kernel.RegisterModule(Module("gtfo.map", "gtfo.map_object",
            value => ReferenceEquals(value, door) ? new EntityReference("gtfo.map_object:1", 1, 1) : null), RuntimeLogLevel.Off);
        kernel.StartRuntime(() => { });
        Check.That(kernel.ResolveEntity(door) == new EntityReference("gtfo.map_object:1", 1, 1),
            "a throwing package does not stop the packages after it");
        Check.That(kernel.ResolveEntity(new Native("unknown")) == null, "an object only the throwing package might know is unanswered");
        Check.That(Code(() => kernel.ResolveEntity(new object())) == null, "a throwing package never turns resolution into a failure");
    }
}
