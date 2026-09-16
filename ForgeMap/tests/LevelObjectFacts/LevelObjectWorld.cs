using System.Text.Json;
using ForgeMap;
using ForgeMap.Tests.Support;
using ForgeRuntime.Framework;

/// <summary>
/// The fixture every case in this project runs against: one kernel in a live world, one Map registration whose
/// registry seed is the level-object contract, and the publication half attached to that registration exactly
/// as the session attaches it.
///
/// The registration is built from the contract file alone — the same <see cref="LevelObjectContract.BindingRows"/>
/// and <see cref="LevelObjectContract.Support"/> the integration batch inserts into the shared declaration — so
/// a case that passes here passes against the rows the runtime will really resolve. The capability rows are the
/// contract's own JSON, parsed by the same registry the host uses, which is what makes a declared port and a
/// published port agree by construction rather than by inspection.
/// </summary>
internal sealed class LevelObjectWorld : IDisposable
{
    private readonly List<string> _reports = new();
    private readonly List<RuntimeEvent> _facts;

    internal RuntimeKernel Kernel { get; }
    internal RuntimeModuleHandle Registration { get; }
    internal LevelObjectModule Module { get; }
    internal IReadOnlyList<string> Reports => _reports;

    /// <summary>Every fact the module handed to the kernel, in order. The observation point is the module's
    /// own test-only one, so a case asserts the exact event the host would dispatch without standing a plan up
    /// behind every row.</summary>
    internal IReadOnlyList<RuntimeEvent> Facts => _facts;
    internal RuntimeEvent Last => _facts[^1];

    private LevelObjectWorld(RuntimeKernel kernel, RuntimeModuleHandle registration, LevelObjectModule module,
        List<RuntimeEvent> facts)
    {
        Kernel = kernel;
        Registration = registration;
        Module = module;
        _facts = facts;
    }

    /// <summary>One live world at epoch 1 with the level-object rows registered and the module attached.</summary>
    internal static LevelObjectWorld Start()
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.test.levelobject", "0.1.0", RuntimeKernel.ApiVersion, "20403457"));
        // The control vocabulary is the kernel's own module, the way the host registers it: the one step of the
        // plan this fixture mounts below is the kernel's own `branch`.
        kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        kernel.BeginWorld(1);
        var facts = new List<RuntimeEvent>();
        var module = BuildRegistration(out var reports);
        var registration = kernel.RegisterModule(module, RuntimeLogLevel.Off);
        var levelObjects = new LevelObjectModule(kernel, registration, reports.Add) { FactObserver = facts.Add };
        // The game-bound half of the one value row this module owns, in the order `MapPluginSession` hands it
        // over: the module answers the shape and the table, the session supplies the read that can see a native
        // subject.
        levelObjects.ScanStateReader = ScanNativeState;
        registration.ObserveLifecycle(value =>
        {
            if (value.Kind == RuntimeLifecycleKind.WorldChanged) levelObjects.ClearWorld();
        });
        // The observation point these cases assert on sits in the module, before the kernel is reached, so the
        // module needs a subscriber for every row it publishes under or it builds no event at all.
        SubscriptionGateFixture.Open(kernel, registration);
        kernel.StartRuntime(() => { });
        return new LevelObjectWorld(kernel, registration, levelObjects, facts);
    }

    /// <summary>The scan a case observed, keyed by uid, as the game-bound reader's own table. Production keeps
    /// this table in <c>LevelObjectObservation</c>; the fixture keeps the same one row per uid so a read through
    /// the value row answers the status the fact carried.</summary>
    private static readonly Dictionary<string, (string State, double Progress)> ScanReadings = new(StringComparer.Ordinal);

    /// <summary>The reader of one scan's state and progress, shaped exactly as
    /// <c>LevelObjectObservation.ReadScanState</c> is: the uid names the instance and the module hands over the
    /// fraction it last published. A progress reading at or below zero is recorded by the module but never
    /// published, so the reader answers zero for a uid it has no fraction for rather than refusing — the
    /// instance exists, its completion is simply nothing yet.</summary>
    private static JsonElement ScanReading(EvaluationContext context)
    {
        var id = context.Inputs.TryGetProperty("scan", out var input) && input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("resourceId", out var named) && named.ValueKind == JsonValueKind.String
            ? named.GetString()! : "";
        if (id.Length == 0) throw new RuntimeContractException("scan-resource-missing", "The scan input names no resource.");
        var reading = ScanReadings.TryGetValue(id, out var recorded) ? recorded : ("disabled", 0d);
        return RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["state"] = RuntimeJson.From(reading.Item1),
            ["progress"] = RuntimeJson.From(reading.Item2)
        });
    }

    /// <summary>The same read for the module's own reader slot, which is what the evaluator calls. It is the
    /// production signature (`Func&lt;string, double, JsonElement&gt;`) rather than the contract delegate's: the
    /// module hands the fraction it holds to the reader, exactly as the session's reader receives it.</summary>
    private static JsonElement ScanNativeState(string uid, double progress)
    {
        var reading = ScanReadings.TryGetValue(uid, out var recorded) ? recorded : ("disabled", progress);
        return RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["state"] = RuntimeJson.From(reading.Item1),
            ["progress"] = RuntimeJson.From(reading.Item2)
        });
    }

    private static RuntimeModule BuildRegistration(out List<string> reports)
    {
        // The contract's own capability array, read back through the registry's parser: every declared row the
        // module publishes under has to exist here, or the registration itself is refused.
        var capabilities = JsonDocument.Parse(LevelObjectContract.CapabilitiesJson).RootElement;
        var registry = RuntimeJson.From(new
        {
            providers = new[] { new { id = LevelObjectContract.ProviderId, kind = "native", version = "0.1.0", dependencies = Array.Empty<string>() } },
            capabilities,
            bindings = LevelObjectContract.BindingRows()
        });
        reports = new List<string>();
        // The one game-bound reader, shaped exactly as `MapPluginSession` hands the production one in: the scan
        // reader answers the native status name and the fraction for one uid. What a case reads back through the
        // value row is therefore the same reading the fact carried, with no stub in between.
        var readers = new LevelObjectContract.LevelObjectReaders(context => ScanReading(context));
        return new RuntimeModule(RuntimeKernel.ApiVersion, registry.GetRawText(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal), LevelObjectContract.Support())
        {
            Evaluators = LevelObjectContract.Evaluators(readers),
            Shapes = LevelObjectContract.Shapes(),
            // The session registers this half's entity kind; a fixture without one would have every entity port
            // refused as `entity-resolver` rather than exercised.
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [LevelObjectModule.EntityKind] = reference => reference.Id.StartsWith(LevelObjectModule.EntityKind + ":", StringComparison.Ordinal)
            }
        };
    }

    /// <summary>The world this fixture builds anew: the same kernel, a new epoch, and every per-world table of
    /// the module dropped through the lifecycle subscription the registration carries. The reader's own scan
    /// table is dropped here too, because it names instances of the level that no longer exists.</summary>
    internal void NextWorld()
    {
        ScanReadings.Clear();
        Kernel.BeginWorld(Kernel.WorldEpoch + 1);
    }

    internal static EntityReference Player()
        => new("gtfo.player:test", 1, 1);

    /// <summary>One scan status change, recorded in the reader's table and handed to the module, which is the
    /// order the native hook runs in: `LevelObjectObservation` records the instance it read and then publishes
    /// through the module.</summary>
    internal void ObserveScanState(string uid, bool active, bool solved)
    {
        if (uid.Length != 0)
        {
            var progress = ScanReadings.TryGetValue(uid, out var previous) ? previous.Progress : 0d;
            ScanReadings[uid] = (solved ? "solved" : active ? "active" : "disabled", progress);
        }
        Module.ScanStateChanged(uid, active, solved);
    }

    /// <summary>One scan progress reading, recorded and published the same way.</summary>
    internal void ObserveScanProgress(string uid, double progress, IReadOnlyList<EntityReference> players)
    {
        if (uid.Length != 0)
        {
            var state = ScanReadings.TryGetValue(uid, out var previous) ? previous.State : "disabled";
            ScanReadings[uid] = (state, progress);
        }
        Module.ScanProgress(uid, progress, players);
    }

    internal double ScanProgress(string uid)
    {
        using var document = JsonDocument.Parse(Module.ReadScanState(ScanInput(uid)).GetRawText());
        return document.RootElement.GetProperty("progress").GetDouble();
    }

    internal JsonElement ScanState(string uid) => Module.ReadScanState(ScanInput(uid));

    internal JsonElement ScanState(string uid, string state) => Module.ReadScanState(ScanInput(uid, state));

    private static EvaluationContext ScanInput(string uid, string state = "active") => Input("scan",
        LevelObjectContract.ChainedPuzzleKind, uid, LevelObjectContract.ScanStateHandler,
        RuntimeJson.From(new { state }));

    /// <summary>One evaluation context the way the kernel builds it: the node's own handler name, the row's
    /// structural parameters and the resource input the row declares. The context's constructor is internal to
    /// the SDK, so it is reached through its non-public signature, exactly as the query tests of the framework
    /// do.</summary>
    private static EvaluationContext Input(string port, string kind, string id, string node, JsonElement? parameters = null)
    {
        var query = default(RuntimeQuerySession);
        var actors = new RuntimeActorContext(new Dictionary<string, EntityReference>(StringComparer.Ordinal));
        var relations = new RuntimeFactionRelations(Array.Empty<RuntimeFactionRelation>());
        var context = (EvaluationContext)Activator.CreateInstance(typeof(EvaluationContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object?[]
            {
                node, parameters ?? RuntimeJson.EmptyObject,
                RuntimeJson.From(new Dictionary<string, object> { [port] = new { resourceKind = kind, resourceId = id } }),
                query, actors, relations
            }, null)!;
        return context;
    }

    public void Dispose() => Registration.Dispose();
}
