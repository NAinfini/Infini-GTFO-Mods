using System.Text.Json;
using ForgeDevelopment.Native;
using ForgeRuntime.Framework;

namespace ForgeDevelopment.Tests.DevModule;

/// <summary>
/// One case's world: a kernel with a fixture entity kind that has a resolver, a candidate source and an observer,
/// plus the production diagnostic provider registered over it. There is no game assembly and no BepInEx here —
/// the three sources under test name no game type — so every read the diagnostic rows make is answered by the
/// kernel's own entity table and nothing else.
/// </summary>
internal sealed class ModuleWorld : IDisposable
{
    internal const string ActorKind = "fixture.actor";
    internal const long WorldEpoch = 11;

    private readonly RuntimeModuleHandle _actors;
    private readonly Dictionary<string, RuntimeEntitySnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly List<EntityReference> _candidates = new();

    internal RuntimeKernel Kernel { get; }
    internal List<DiagnosticRecord> Records { get; } = new();

    internal ModuleWorld(bool enabled = true)
    {
        Kernel = new RuntimeKernel(new("fixture.development.module", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(WorldEpoch);
        var kind = ActorKind;
        _actors = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry("fixture.actors"),
            new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>> { [kind] = reference => _snapshots.ContainsKey(reference.Id) })
        {
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
            {
                [kind] = reference => _snapshots.TryGetValue(reference.Id, out var snapshot) ? snapshot : null
            },
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>> { [kind] = () => _candidates }
        }, RuntimeLogLevel.Off);
        Registered = DevelopmentModule.Register(Kernel, out var code, enabled, Records.Add);
        RegistrationCode = code;
        // The kernel answers no entity read before its runtime is ready, so the fixture starts it exactly as the
        // host does: every module is registered first, then registration freezes and one advance settles the world.
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, true);
        // The refused registration is a case of its own: it is the one where no session exists, so the fixture
        // must not open one for it.
        if (Registered) DevelopmentModule.Sessions!.TryBegin(1, out _, out _);
    }

    internal bool Registered { get; }
    internal string RegistrationCode { get; }

    private static string Registry(string provider) => RuntimeJson.From(new
    {
        providers = new[] { new { id = provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText();

    /// <summary>The session handle a plan wires into a row's `session` input, as the module minted it. It is the
    /// handle value itself, so the frame this fixture builds carries the same object the kernel would.</summary>
    internal JsonElement SessionHandle => DevelopmentModule.Sessions!.Handle;

    internal (EntityReference Reference, RuntimeEntitySnapshot Snapshot) Actor(string id, string lifeState = "alive",
        string? faction = "hostile", double x = 1, double y = 2, double z = 3)
    {
        var reference = new EntityReference(ActorKind + ":" + id, WorldEpoch, 1);
        var snapshot = new RuntimeEntitySnapshot(reference, ActorKind, faction, lifeState,
            new[] { "heavy" }, new[] { "direct" }, new[] { x, y, z });
        _snapshots[reference.Id] = snapshot;
        if (!_candidates.Contains(reference)) _candidates.Add(reference);
        return (reference, snapshot);
    }

    internal void Drop(string id) => _snapshots.Remove(ActorKind + ":" + id);

    /// <summary>One request frame as the dispatcher would hand it to a handler, plus the parameter bag a node
    /// compiled. A port a case does not name is absent from the frame, which is what a plan that wired nothing
    /// into it produces.</summary>
    internal static CommandContext Context(object inputs, object? parameters = null, string planId = "fixture.development.plan",
        string nodeId = "fixture.development.node")
    {
        var origin = new RuntimeEvent("fixture.development.event", DevelopmentModule.Binding(DevelopmentModule.TraceCapability),
            WorldEpoch, 3, "fixture.development.scope", RuntimeJson.EmptyObject);
        return (CommandContext)Activator.CreateInstance(typeof(CommandContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object?[]
            {
                origin, 3L, "fixture.development.command", planId, "fixture.development.resource", "1", nodeId,
                RuntimeJson.From(parameters ?? new { }), RuntimeJson.From(inputs), false,
                (Func<EntityReference, object?>)(_ => null)
            }, null)!;
    }

    /// <summary>The one row of a result, and one column of it. The column order is the schema's own, so a case
    /// asserts it rather than trusting the serializer's member order.</summary>
    internal static JsonElement Row(CommandResult result)
        => result.Outputs.GetProperty("results").EnumerateArray().Single();

    internal static string Field(CommandResult result, string name)
    {
        var value = Row(result).GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? "" : value.ToString();
    }

    internal static string[] Columns(CommandResult result)
        => Row(result).EnumerateObject().Select(property => property.Name).ToArray();

    internal static JsonElement Entity(EntityReference reference)
    {
        var actor = new { id = reference.Id, worldEpoch = reference.WorldEpoch, lifeEpoch = reference.LifeEpoch };
        return RuntimeJson.From(actor);
    }

    public void Dispose()
    {
        DevelopmentModule.Forget();
        _actors.Dispose();
    }
}
