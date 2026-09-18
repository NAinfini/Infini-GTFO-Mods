using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The three action rows this slice owns: the scan and wave actions of the `forge.action.map` family.
/// The catalog is the authority for every port, parameter and enum these rows carry, so each capability is
/// declared here field for field and never restated in the native half; this file is the game-independent side
/// the runtime registry, the manifest and the website compare against.
///
/// Every one of them is an `execute` row: the handler writes through a native entry the game itself runs, and the
/// result row reports whether that write was issued, was already in the state the request asked for, or was
/// refused by name. The rows are declared here rather than in `ModuleDefinition` because they share their
/// subjects — the chained puzzle instance the scan row activates, and the Mastermind event that is the wave — and
/// a second declaration of the same ports is how two descriptions of one binding drift apart.
///
/// The alarm is that same chained puzzle: the game has one instance kind, the catalog expresses starting it with
/// `scan_start`, and the puzzle's own data block (`TriggerAlarmOnActivate`) is what makes the activation raise an
/// alarm. The two alarm rows the rulings deleted are therefore not declared here, and nothing takes their place —
/// a level's alarm is started by the scan row that names the same instance.</summary>
public static class AlarmWaveContract
{
    /// <summary>The provider every row of this family belongs to, written out rather than read from
    /// `ModuleDefinition` so the contract and the native half behind it compile on their own — which is what lets
    /// the focused test project exercise the rows without the game-independent assembly's other halves. The
    /// integration batch wires the rows into that definition and is what keeps the two spellings one.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    /// <summary>The port the wave start row publishes its handle on, and the lifetime every handle this family
    /// mints carries: a wave lives for the encounter. The scan row publishes no handle — nothing read one, so the
    /// rulings had it deleted rather than kept as a port no plan can use.</summary>
    public const string WaveHandlePort = "wave_handle";
    public const string HandleLifetime = "encounter";

    public const string ScanStateCapability = "forge.action.map.scan_state";
    public const string WaveStartCapability = "forge.action.map.wave_start";
    public const string WaveStopCapability = "forge.action.map.wave_stop";

    /// <summary>The three dynamic scan-state changes: start the existing chained puzzle, force it solved, or
    /// deactivate/reset it. Placement, participants and quorum stay in scan/objective Data rather than this Outcome.</summary>
    public static readonly string[] ScanOperations = { "start", "force_complete", "reset" };

    /// <summary>The handler names the native half supplies, one per row. They are this provider's own vocabulary:
    /// a binding names the handler the registration must carry, and the runtime refuses an implemented binding
    /// whose handler is missing rather than dispatching into nothing.</summary>
    public const string ScanStateHandler = "gtfo.map.scan_state";
    public const string WaveStartHandler = "gtfo.map.wave_start";
    public const string WaveStopHandler = "gtfo.map.wave_stop";

    /// <summary>The permission a plan needs to drive a scan, and the one it needs to drive waves. Both are the
    /// resources' own control permission, spelled the way the catalog's `recipients.requires` spells it, so a plan
    /// that pins one of these bindings declares exactly what the row says it must.</summary>
    public const string ScanControlPermission = "scan.control";
    public const string WaveControlPermission = "wave.control";

    /// <summary>One handler's own port set, resolved at registration against the canonical capability. The port
    /// lists are the catalog's, in the catalog's order: an execute binding ends in `result`, and the wave start
    /// row additionally declares the handle output its row publishes — the handle a later stop row reads
    /// back.
    ///
    /// The scan row carries exactly the resource it writes and the setting that says how: `anchor`,
    /// `participants` and `quorum` were declared but never read — the required number of players in a scan
    /// belongs to the puzzle's own data block and no native entry takes it per request — and a parameter or port
    /// the game ignores is a choice an author would keep making, so the rulings had them deleted.</summary>
    public static readonly HandlerShape ScanStateShape = new HandlerShape()
        .Inputs("scan").Outputs("result")
        .Parameters("mode");
    public static readonly HandlerShape WaveStartShape = new HandlerShape()
        .Inputs("wave", "budget", "count", "seed", "interval").Outputs("result", "wave_handle");
    public static readonly HandlerShape WaveStopShape = new HandlerShape()
        .Inputs("waves", "reason").Outputs("result").Parameters("pending_spawns_policy");

    /// <summary>Every shape this slice owns, keyed by handler name: what the integration batch adds to the
    /// registration's shape table beside the player selector's and the heal handler's.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [ScanStateHandler] = ScanStateShape,
        [WaveStartHandler] = WaveStartShape,
        [WaveStopHandler] = WaveStopShape
    };

    /// <summary>The three binding rows: this provider's own id under its own namespace, the canonical capability
    /// each implements and the handler the native half supplies. The rows are `execute` bindings, so the runtime
    /// resolves their shapes against these capabilities and refuses a registration that supplies no handler for
    /// one of them. `requires` is empty on every row: it names other bindings a row's closure needs, not the
    /// permissions its recipients declare, and those travel on the support rows below where the kernel reads
    /// them.</summary>
    public static object[] Bindings() => new object[]
    {
        Row(ScanStateCapability, ScanStateHandler),
        Row(WaveStartCapability, WaveStartHandler),
        Row(WaveStopCapability, WaveStopHandler)
    };

    /// <summary>The three registration rows, in the same order as <see cref="Bindings"/>: one per binding, each
    /// naming the permission that binding's recipients require. `implementation-only` is the verification the
    /// provider can stand behind for all three — a native write path exists for each, and its own evidence file
    /// names the entry point and the denial.</summary>
    public static BindingSupport[] Support() => new[]
    {
        new BindingSupport(Binding(ScanStateCapability), "implementation-only", new[] { ScanControlPermission }),
        new BindingSupport(Binding(WaveStartCapability), "implementation-only", new[] { WaveControlPermission }),
        new BindingSupport(Binding(WaveStopCapability), "implementation-only", new[] { WaveControlPermission })
    };

    /// <summary>The binding id of one capability: the capability's own suffix under the Map provider, so either
    /// side of a row names the counterpart of the other without a second table.</summary>
    public static string Binding(string capabilityId)
        => ProviderId + ".binding." + capabilityId["forge.action.map.".Length..];

    private static object Row(string capability, string handler) => new
    {
        id = Binding(capability),
        capabilityId = capability,
        providerId = ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    /// <summary>The three catalog rows, verbatim: the ports, resource kinds, handle kinds, lifetimes, structural
    /// parameters and recipient declarations the website publishes for these ids. They are declared here rather
    /// than copied into `ModuleDefinition` because the registry resolves each binding's shape against the
    /// capability it names, and a capability that describes a different port set than the website's own row would
    /// make the same plan compile on one side and be refused on the other.
    ///
    /// `graph.execution` is `host` for all three: each one writes through a host-side entry point the game
    /// replicates (the puzzle's own state replicator, the Mastermind's event list), so the row is never a
    /// local-only presentation.
    ///
    /// Each row is its own constant so the registration can take them one at a time — the Map declaration's
    /// capability array is a shared file and one insertion per batch is how the integration adds them — and
    /// <see cref="CapabilitiesJson"/> is the same three in catalog order for a reader that wants the whole set.</summary>
    public static string ScanStateCapabilityJson => CapabilityJson(
        ScanStateCapability, "启动、强制完成或重置扫描",
        "对已绑定的扫描执行启动、强制完成或重置；扫描与警报共用同一种链式谜题实例。");

    public static string WaveStartCapabilityJson => CapabilityJson(
        WaveStartCapability, "开始具名波次", "开始一波具名的敌人。");

    public static string WaveStopCapabilityJson => CapabilityJson(
        WaveStopCapability, "停止可定位的具名波次", "停掉一波敌人，并决定未刷的怎么办。");

    private static string CapabilityJson(string id, string label, string description) => RuntimeJson.From(new
    {
        id,
        owner = ProviderId,
        kind = "action",
        label,
        version = "1.0.0",
        parameters = new { description },
        graph = PrimitiveGraphSource.Get(id)
    }).GetRawText();

    /// <summary>The three rows in catalog order, as the array a registry's capability section carries. The order
    /// is the catalog's own, so a diff of this array against the website's rows is positional.</summary>
    public static readonly string CapabilitiesJson = "[\n" + ScanStateCapabilityJson
        + ",\n" + WaveStartCapabilityJson + ",\n" + WaveStopCapabilityJson + "\n]";

    /// <summary>The chained-puzzle resource kind's own name in the shared resource-kind table. A chained puzzle
    /// is both the alarm and the scan: the game has one instance kind, its data block's `TriggerAlarmOnActivate`
    /// is what makes an activation an alarm, and the one scan row is therefore how a level's alarm is started.</summary>
    public const string ChainedPuzzleKind = "chained-puzzle";
    /// <summary>The wave resource kind's own name. A wave resource is the author's pair of data blocks — the
    /// settings block and the population block — not a running wave; the running wave is the Mastermind event the
    /// native start entry answers with.</summary>
    public const string WaveKind = "wave";

    /// <summary>The codes the shared result vocabulary already carries for a resource the provider cannot answer
    /// for, and the read a provider performs when it is asked about one. They live here so the native half, the
    /// evidence file and the integration note spell them the same way.</summary>
    public const string ResourceUnavailableCode = "resource-unavailable";

    /// <summary>The provider that owns the two resource kinds above, for the registration the integration batch
    /// wires into `MapPluginSession.Definition()`. Enumeration and resolution are the native world's own table:
    /// the provider answers what the level currently holds and never invents an instance for an id it does not
    /// have, which is what makes a plan naming a chained puzzle that this level never built fail as
    /// `stale-resource` instead of silently starting nothing.
    ///
    /// Both providers are handed in by the native half, because only it can read the level: this assembly is the
    /// declaration side and holds no game type.</summary>
    public static IReadOnlyDictionary<string, RuntimeResourceProvider> ResourceProviders(
        Func<IReadOnlyList<ResourceRef>> chainedPuzzles, Func<string, ResourceRef?> resolveChainedPuzzle,
        Func<IReadOnlyList<ResourceRef>> waves, Func<string, ResourceRef?> resolveWave)
        => new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
        {
            [ChainedPuzzleKind] = RuntimeResourceProvider.Of(chainedPuzzles, resolveChainedPuzzle),
            [WaveKind] = RuntimeResourceProvider.Of(waves, resolveWave)
        };
}
