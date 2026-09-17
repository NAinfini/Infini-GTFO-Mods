using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

// Synthetic test provider only. Every observation uses the actual public registration path.
internal sealed class ObservationWorld : IDisposable
{
    internal RuntimeKernel Kernel { get; } = new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-r3-spatial"));
    internal Dictionary<string, RuntimeEntitySnapshot> Entities { get; } = new(StringComparer.Ordinal);
    internal RuntimeModuleHandle Handle { get; }
    internal Func<EntityReference, RuntimeEntitySnapshot?>? OnObserve { get; set; }
    internal Func<EntityReference, bool>? OnResolve { get; set; }
    internal int Observations { get; private set; }
    /// <summary>One world over the entities a case declares. <paramref name="zones"/> false stands up a provider
    /// that tracks entities without answering where they stand, which is the world a zone filter must refuse
    /// rather than answer as "outside every zone".</summary>
    internal ObservationWorld(IEnumerable<RuntimeEntitySnapshot> entities, bool start = true, bool zones = true)
    {
        foreach (var entity in entities) Entities.Add(entity.Ref.Id, entity);
        var prefixes = Entities.Keys.Select(id => id[..id.IndexOf(':')]).Distinct().ToArray();
        var module = Definition("test.trigger.spatial",
            prefixes.ToDictionary(p => p, p => (Func<EntityReference, bool>)Resolve),
            prefixes.ToDictionary(p => p, p => (Func<EntityReference, RuntimeEntitySnapshot?>)Observe),
            // Which zone an entity stands in is answered by the provider that owns its kind, exactly as its
            // observation is: this world answers for every kind it holds entities of.
            zones ? prefixes.ToDictionary(p => p, p => (Func<EntityReference, EntityReference?>)Zone) : null);
        Handle = Kernel.RegisterModule(module, RuntimeLogLevel.Off);
        if (start) { Kernel.StartRuntime(() => Kernel.BeginWorld(1)); Kernel.Advance(0, true); }
    }
    private bool Resolve(EntityReference reference) => OnResolve?.Invoke(reference)
        ?? (Entities.TryGetValue(reference.Id, out var entity) && entity.Ref == reference);
    internal RuntimeEntitySnapshot? Observe(EntityReference reference)
    {
        Observations++;
        return OnObserve is null ? Entities.GetValueOrDefault(reference.Id) : OnObserve(reference);
    }
    /// <summary>Where each entity of this world stands, by reference id, as the zone reference the provider that
    /// owns its kind answers with. An entity that is not in the table is one this provider cannot place, which the
    /// responder says by refusing it by name: a null answer is the world's own "this entity stands in no zone",
    /// and this fixture has no such entity.</summary>
    internal Dictionary<string, EntityReference?> Zones { get; } = new(StringComparer.Ordinal);
    /// <summary>Every zone read this world answered, so a case can check that a row reads no more than it needs.</summary>
    internal int ZoneReads { get; private set; }
    private EntityReference? Zone(EntityReference reference)
    {
        ZoneReads++;
        return Zones.TryGetValue(reference.Id, out var zone)
            ? zone
            : throw new RuntimeContractException("entity-zone-unknown", "This provider cannot place " + reference.Id + ".");
    }
    internal static RuntimeModule Definition(string provider,
        IReadOnlyDictionary<string, Func<EntityReference, bool>> resolvers,
        IReadOnlyDictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>? observers,
        IReadOnlyDictionary<string, Func<EntityReference, EntityReference?>>? zones = null)
        => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(), resolvers)
        { EntityObservers = observers, EntityZones = zones };
    internal static RuntimeEntitySnapshot At(string id, double x = 0, double y = 0, double z = 0, long life = 1)
        => new(new EntityReference(id, 1, life), "enemy", null, "alive", Array.Empty<string>(),
            new[] { "test.receiver.health" }, new[] { x, y, z });
    internal static ObservationWorld FromJson(JsonElement world)
        => new(world.GetProperty("entities").EnumerateArray().Select(RuntimeEntitySnapshot.FromJson));
    /// <summary>The budgeted read a `query` step is handed is the kernel's own to build, and this assembly is not a
    /// friend of the framework, so the one constructor that takes it is reached by reflection here rather than by
    /// widening the framework's public surface for a test. Every production call still goes through the session.</summary>
    internal RuntimeQuerySession Session(string nodeId = "test.node")
        => (RuntimeQuerySession)SessionConstructor.Invoke(new object[] { Kernel, nodeId, true });
    private static readonly ConstructorInfo SessionConstructor = typeof(RuntimeQuerySession)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(RuntimeKernel), typeof(string), typeof(bool) }, null)
        ?? throw new InvalidOperationException("The framework no longer exposes the query session constructor this test needs.");
    public void Dispose() { Kernel.StopRuntime(); Handle.Dispose(); }
}
