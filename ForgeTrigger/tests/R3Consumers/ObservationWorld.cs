using System.Text.Json;
using ForgeRuntime.Framework;

// Synthetic test provider only. Every observation uses the actual public registration path.
internal sealed class ObservationWorld : IDisposable
{
    internal RuntimeKernel Kernel { get; } = new(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "synthetic-r3-spatial"));
    internal Dictionary<string, RuntimeEntitySnapshot> Entities { get; } = new(StringComparer.Ordinal);
    internal RuntimeModuleHandle Handle { get; }
    internal Func<EntityReference, RuntimeEntitySnapshot?>? OnObserve { get; set; }
    internal Func<EntityReference, bool>? OnResolve { get; set; }
    internal int Observations { get; private set; }
    internal ObservationWorld(IEnumerable<RuntimeEntitySnapshot> entities, bool start = true)
    {
        foreach (var entity in entities) Entities.Add(entity.Ref.Id, entity);
        var prefixes = Entities.Keys.Select(id => id[..id.IndexOf(':')]).Distinct().ToArray();
        var module = Definition("test.trigger.spatial",
            prefixes.ToDictionary(p => p, p => (Func<EntityReference, bool>)Resolve),
            prefixes.ToDictionary(p => p, p => (Func<EntityReference, RuntimeEntitySnapshot?>)Observe));
        Handle = Kernel.RegisterModule(module);
        if (start) { Kernel.StartRuntime(() => Kernel.BeginWorld(1)); Kernel.Advance(0, true); }
    }
    private bool Resolve(EntityReference reference) => OnResolve?.Invoke(reference)
        ?? (Entities.TryGetValue(reference.Id, out var entity) && entity.Ref == reference);
    private RuntimeEntitySnapshot? Observe(EntityReference reference)
    {
        Observations++;
        return OnObserve is null ? Entities.GetValueOrDefault(reference.Id) : OnObserve(reference);
    }
    internal static RuntimeModule Definition(string provider,
        IReadOnlyDictionary<string, Func<EntityReference, bool>> resolvers,
        IReadOnlyDictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>? observers)
        => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(), resolvers)
        { EntityObservers = observers };
    internal static RuntimeEntitySnapshot At(string id, double x = 0, double y = 0, double z = 0, long life = 1)
        => new(new EntityReference(id, 1, life), "enemy", null, "alive", Array.Empty<string>(),
            new[] { "test.receiver.health" }, new[] { x, y, z });
    internal static ObservationWorld FromJson(JsonElement world)
        => new(world.GetProperty("entities").EnumerateArray().Select(RuntimeEntitySnapshot.FromJson));
    public void Dispose() { Kernel.StopRuntime(); Handle.Dispose(); }
}
