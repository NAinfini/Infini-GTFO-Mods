using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>
/// The level-object rows this slice owns, game-independent: the three scan facts, the container row and the level
/// item row, plus the read-only value row the same scan answers (<c>v-scan</c> of the node list). A wave row is
/// deliberately absent — the wave facts, the wave actions and the wave resource kind all belong to ForgeEnemy,
/// and a second declaration of the same binding here is how two descriptions of one row drift apart. The
/// generator rows that used to live here are the map-object generator category's own now
/// (<see cref="GeneratorContract"/>, ruling 148.4): a generator is an object the level places, so it is addressed
/// and published like a door and a terminal instead of through a level-object key.
///
/// Every shape below is complete, not a sketch: ports, parameter roles, enum members, handle kinds and
/// lifetimes are written out field for field, because this file is what the game-independent registry, the
/// export manifest and the website catalog are compared against.
///
/// A scan fact is read from native state and published by the host only, so each is registered with role
/// `observe` and carries no result row:
/// <list type="bullet">
/// <item><description>A scan is a `ChainedPuzzles.ChainedPuzzleInstance` — the same instance kind the alarm
/// is — and its own `Master_OnPlayerScanChanged(float scanProgress, List&lt;PlayerAgent&gt; playersInScan, int
/// inScanMax, bool[] reqObjsInScan)` callback is the one place the host learns the current progress. The
/// callback is called <b>before</b> the instance's own body, which is the only point at which the progress
/// belongs to the pulse being reported rather than to the state the body is about to write.</description></item>
/// </list>
/// </summary>
public static class LevelObjectContract
{
    /// <summary>The Map provider these rows belong to. The spelling is the one `ModuleDefinition.ProviderId`
    /// carries, restated here so this file compiles against the framework alone — which is what lets the
    /// focused test project exercise it without the game-independent assembly's other halves — and the
    /// integration batch's row insertion is what keeps the two spellings equal.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    // ---- capability ids ------------------------------------------------------------------------------
    /// <summary>The scan's three states, catalog rows unchanged: the pair this provider publishes is exactly
    /// the pair the website already carries for `e-scan`.</summary>
    public const string ScanStartedCapability = "forge.trigger.objective.scan_started";
    public const string ScanCompletedCapability = "forge.trigger.objective.scan_completed";

    /// <summary>The progress row `e-scan-progress` needs, on the website catalog's own id
    /// `forge.trigger.objective.scan_membership`. One `ChainedPuzzleInstance.Master_OnPlayerScanChanged`
    /// callback carries both the `scanProgress` the master computed and the players standing in the scan, so
    /// the two facts are one row and the catalog row carries this registration's ports (181.3).</summary>
    public const string ScanProgressCapability = "forge.trigger.objective.scan_membership";

    /// <summary>The container row `e-container` needs and the level item row `e-pickup` needs. The container row
    /// is the catalog's `forge.trigger.interaction.container_state`: the publish is this provider's, so the
    /// catalog row carries this registration's ports, status set and copy, and the row id follows the catalog's
    /// namespace (181.3/177.2). `forge.trigger.equipment.picked_up`/`dropped` are the equipment half's rows about
    /// a player's inventory slots, not about a world item changing hands, so the level item row stays here.</summary>
    /// <summary>The kind every row's structural `resource` parameter carries, so the row names what an author
    /// points it at without a second table. The three scan rows read the chained puzzle the value row's input
    /// already declares; the container row and the level item row both name the one `item` resource kind this
    /// framework has for a thing the level placed. The kind is what a plan's constant frame is indexed by, so a
    /// row that named none could not be compiled at all.</summary>
    public const string ContainerStateCapability = "forge.trigger.interaction.container_state";
    public const string ItemPickupCapability = "forge.trigger.map.item_pickup";

    /// <summary>The container statuses this provider publishes, as the game's own `eResourceContainerStatus`
    /// readings: `NotSetup`, `Locked`, `Closed`, `Open`, `PlayerClose`, `PlayerFar`. A status outside the set is
    /// not published at all, so a future enum member cannot be reported as one of these.</summary>
    public static readonly string[] ContainerStateNames =
    {
        "not-setup", "locked", "closed", "open", "player-close", "player-far"
    };

    /// <summary>The scan's category inside the level-object namespace: a scan is addressed by the chained
    /// puzzle's own `m_puzzleUID`, which the instance assigns itself and which the alarm and scan actions
    /// already use.</summary>
    public const string ScanCategory = "scan";

    /// <summary>The value rows of the same subjects, on the two catalog ids the node list's `v-scan` and
    /// `v-scan` already has (`forge.condition.predicate.scan`). A value row is
    /// a `query`: it reads native state on the host, changes nothing, and produces no command. It is reshaped
    /// from the catalog's predicate-only form to the list row's readings — the catalog's own `value` port stays
    /// as the predicate answer and the extra ports are the state and the progress the list row
    /// asks for; the complete shape travels in the JSON below.</summary>
    public const string ScanStateCapability = "forge.condition.predicate.scan";

    /// <summary>The scan states the value row tests, as the game's own `eChainedPuzzleStatus` readings. The
    /// catalog's fourth member (`timed-out`) names no native status of a chained puzzle, so it is not declared:
    /// an author value this provider cannot read is refused at registration rather than answered with a guess.
    /// </summary>
    public static readonly string[] ScanStateNames = { "disabled", "active", "solved" };

    // ---- handler and binding names -------------------------------------------------------------------
    /// <summary>The handler names the native half supplies, one per row. A fact arrives as one whole frame, so
    /// an `observe` binding resolves no shape; the names are the rows' own discriminators and are what the
    /// native publisher spells when it reports.</summary>
    public const string ScanStartedHandler = "gtfo.map.scan_started";
    public const string ScanProgressHandler = "gtfo.map.scan_progress";
    public const string ScanCompletedHandler = "gtfo.map.scan_completed";
    public const string ContainerStateHandler = "gtfo.map.container_state";
    public const string ItemPickupHandler = "gtfo.map.item_pickup";

    /// <summary>The evaluator name the value row carries. A `query` row is dispatched to an evaluator,
    /// which is handed the budgeted query session and answers with the row's output ports.</summary>
    public const string ScanStateHandler = "gtfo.map.scan_state_query";

    /// <summary>The permission every row here reads through: a plan that wants to know what a scan is doing
    /// reads the map object state and nothing else. The control side stays with the action rows, which declare
    /// their own `scan.control`.</summary>
    public const string ReadPermission = "gtfo.map_object.read";

    /// <summary>The binding id of one row: the capability's own suffix under the Map provider, so either side
    /// of a row names the counterpart of the other without a second table. The suffix is everything after the
    /// capability's kind segment (`forge.&lt;kind&gt;.`), which is what lets a trigger and a condition of this
    /// family produce their two distinctive binding ids from one rule.</summary>
    public static string Binding(string capabilityId)
    {
        int kind = capabilityId.IndexOf('.', "forge.".Length);
        if (kind < 0) throw new ArgumentException("capability id has no kind segment", nameof(capabilityId));
        return ProviderId + ".binding." + capabilityId[(kind + 1)..];
    }

    /// <summary>The binding ids the native half publishes under, in the order the rows are declared.</summary>
    public static readonly string ScanStartedBinding = Binding(ScanStartedCapability);
    public static readonly string ScanProgressBinding = Binding(ScanProgressCapability);
    public static readonly string ScanCompletedBinding = Binding(ScanCompletedCapability);
    public static readonly string ContainerStateBinding = Binding(ContainerStateCapability);
    public static readonly string ItemPickupBinding = Binding(ItemPickupCapability);
    /// <summary>The value row's own binding: a `condition` row is answered by an evaluator, so its binding
    /// carries role `evaluate` and names the reader the registration must hold.</summary>
    public static readonly string ScanStateBinding = Binding(ScanStateCapability);

    /// <summary>The resource kind the scan actions and the scan resource table carry. A scan is addressed by the
    /// chained puzzle's own `m_puzzleUID`, which the instance assigns itself while it is set up and which the
    /// alarm actions already use.</summary>
    public const string ChainedPuzzleKind = "chained-puzzle";

    // ---- registration rows ---------------------------------------------------------------------------

    /// <summary>Every binding this family declares, in the order the registry rows list them.</summary>
    public static readonly string[] Bindings =
    {
        ScanStartedBinding, ScanProgressBinding, ScanCompletedBinding,
        ContainerStateBinding, ItemPickupBinding, ScanStateBinding
    };

    /// <summary>Every binding's registration row. The five facts are observations of host-side native state,
    /// the value row is evaluated on demand; all six read the same map-object surface, so the family
    /// carries one permission.</summary>
    public static IReadOnlyList<BindingSupport> Support() => Array.AsReadOnly(new[]
    {
        new BindingSupport(ScanStartedBinding, "implementation-only", new[] { ReadPermission }),
        new BindingSupport(ScanProgressBinding, "implementation-only", new[] { ReadPermission }),
        new BindingSupport(ScanCompletedBinding, "implementation-only", new[] { ReadPermission }),
        new BindingSupport(ContainerStateBinding, "implementation-only", new[] { ReadPermission }),
        new BindingSupport(ItemPickupBinding, "implementation-only", new[] { ReadPermission }),
        new BindingSupport(ScanStateBinding, "implementation-only", new[] { ReadPermission })
    });

    /// <summary>The three scan rows, verbatim: the ports, enums, handle kinds and lifetimes the website
    /// publishes for these ids. Each row is its own constant so the integration batch can add them one at a
    /// time to the shared declaration's capability array.</summary>
    public const string ScanStartedCapabilityJson = """
    {
      "id": "forge.trigger.objective.scan_started",
      "owner": "forge.module.gtfo.map",
      "kind": "trigger",
      "label": "扫描开始",
      "version": "1.0.0",
      "parameters": {
        "description": "扫描开始了。",
        "summary": "扫描开始了。",
        "summaryEn": "Fires when a scan starts.",
        "labelEn": "Scan started",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "scan", "type": "handle", "handleKind": "effect", "lifetime": "encounter", "optional": true }
        ],
        "parameters": [
          { "id": "resource", "type": "resource", "role": "structural", "required": true, "resourceKind": "chained-puzzle" }
        ]
      }
    }
    """;

    public const string ScanProgressCapabilityJson = """
    {
      "id": "forge.trigger.objective.scan_membership",
      "owner": "forge.module.gtfo.map",
      "kind": "trigger",
      "label": "扫描进度变化",
      "version": "1.0.0",
      "parameters": {
        "description": "扫描的完成度变了，站进扫描圈的人也一并报出。",
        "summary": "扫描的完成度变了，站进扫描圈的人也一并报出。",
        "summaryEn": "Fires when a scan's completion fraction changes, together with who is standing in the scan.",
        "labelEn": "Scan progress",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "scan", "type": "handle", "handleKind": "effect", "lifetime": "encounter", "optional": true },
          { "id": "progress", "type": "number", "unit": "ratio" },
          { "entityKinds": ["gtfo.player"], "id": "participants", "type": "entity", "cardinality": "many" },
          { "id": "count", "type": "integer" }
        ],
        "parameters": [
          { "id": "resource", "type": "resource", "role": "structural", "required": true, "resourceKind": "chained-puzzle" }
        ]
      }
    }
    """;

    public const string ScanCompletedCapabilityJson = """
    {
      "id": "forge.trigger.objective.scan_completed",
      "owner": "forge.module.gtfo.map",
      "kind": "trigger",
      "label": "扫描完成",
      "version": "1.0.0",
      "parameters": {
        "description": "扫描完成。",
        "summary": "扫描完成。",
        "summaryEn": "Fires when a scan completes.",
        "labelEn": "Scan completed",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "scan", "type": "handle", "handleKind": "effect", "lifetime": "encounter", "optional": true }
        ],
        "parameters": [
          { "id": "resource", "type": "resource", "role": "structural", "required": true, "resourceKind": "chained-puzzle" }
        ]
      }
    }
    """;

    /// <summary>The two item rows. Both carry the object and the reading that changed, and the container row
    /// carries the game's own status enum rather than a state name this provider invented.</summary>
    public const string ContainerStateCapabilityJson = """
    {
      "id": "forge.trigger.interaction.container_state",
      "owner": "forge.module.gtfo.map",
      "kind": "trigger",
      "label": "储物柜 / 资源箱状态变化",
      "version": "1.0.0",
      "parameters": {
        "description": "储物柜或资源箱被上锁、打开、关上，或者有人靠近、离开。",
        "summary": "储物柜或资源箱被上锁、打开、关上，或者有人靠近、离开。",
        "summaryEn": "Fires when a locker or resource box is locked, opened, closed, or when a player comes close to it or leaves it.",
        "labelEn": "Container state changed",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [ "map", "room", "tool", "consumable" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.map_object"], "id": "container", "type": "entity" },
          { "id": "state", "type": "enum", "schema": "container_state" }
        ],
        "parameters": [
          { "id": "resource", "type": "resource", "role": "structural", "required": true, "resourceKind": "item" }
        ]
      }
    }
    """;

    public const string ItemPickupCapabilityJson = """
    {
      "id": "forge.trigger.map.item_pickup",
      "owner": "forge.module.gtfo.map",
      "kind": "trigger",
      "label": "物品被捡起 / 放下",
      "version": "1.0.0",
      "parameters": {
        "description": "关卡里的物品被谁捡起来，或者又被放回地上。",
        "summary": "关卡里的物品被谁捡起来，或者又被放回地上。",
        "summaryEn": "Fires when a level item is picked up by a player or put back down.",
        "labelEn": "Item picked up or placed",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "item", "type": "entity" },
          { "id": "picked_up", "type": "boolean" },
          { "entityKinds": ["gtfo.player"], "id": "actor", "type": "entity", "optional": true }
        ],
        "parameters": [
          { "id": "resource", "type": "resource", "role": "structural", "required": true, "resourceKind": "item" }
        ]
      }
    }
    """;

    /// <summary>The two value rows. A value row has no execution input and no `next`: it is dispatched by the
    /// plan as a query, answers with its ports and nothing else. Both keep the catalog's own predicate port
    /// (`value`) as the answer to the state the row names and add the readings the list rows ask for
    /// (`v-scan`: the state and the fraction; `v-gen`: whether the generator is powered and how many of its
    /// group are). The catalog's `scan` predicate declared a fourth state member, `timed-out`, which no native
    /// chained-puzzle status has; it is dropped here rather than answered with a value the game never produces.
    /// </summary>
    public const string ScanStateCapabilityJson = """
    {
      "id": "forge.condition.predicate.scan",
      "owner": "forge.module.gtfo.map",
      "kind": "condition",
      "label": "扫描状态与进度",
      "version": "1.0.0",
      "parameters": {
        "description": "判断扫描的状态和站进去的人数。",
        "summary": "读一台扫描现在的状态和完成度。",
        "summaryEn": "Reads one scan's current state and completion fraction.",
        "labelEn": "Scan state",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "query",
        "inputs": [
          { "id": "scan", "type": "resource", "resourceKind": "chained-puzzle", "schema": "forge.resource.chained-puzzle" }
        ],
        "outputs": [
          { "id": "value", "type": "boolean" },
          { "id": "state", "type": "enum", "schema": "scan_state" },
          { "id": "progress", "type": "number", "unit": "ratio" }
        ],
        "parameters": [
          { "id": "state", "type": "enum", "role": "structural", "required": true,
            "values": [ "disabled", "active", "solved" ] }
        ]
      }
    }
    """;

    /// <summary>The three scan rows in catalog order, as the array the shared declaration's capability
    /// section carries.</summary>
    public const string TriggerCapabilitiesJson = "[\n" + ScanStartedCapabilityJson + ",\n" + ScanProgressCapabilityJson
        + ",\n" + ScanCompletedCapabilityJson
        + ",\n" + ContainerStateCapabilityJson + ",\n" + ItemPickupCapabilityJson + "\n]";

    /// <summary>The one value row.</summary>
    public const string ValueCapabilitiesJson = "[\n" + ScanStateCapabilityJson + "\n]";

    /// <summary>Every capability row this file owns, as the one array an integration insertion adds.</summary>
    public static readonly string CapabilitiesJson = "[\n" + ScanStartedCapabilityJson + ",\n" + ScanProgressCapabilityJson
        + ",\n" + ScanCompletedCapabilityJson
        + ",\n" + ContainerStateCapabilityJson + ",\n" + ItemPickupCapabilityJson
        + ",\n" + ScanStateCapabilityJson + "\n]";

    /// <summary>The six binding rows, as the array the shared declaration's binding section concatenates: the
    /// five observations of native state and the one evaluated value row, in the order the capabilities are
    /// declared.</summary>
    public static object[] BindingRows() => new object[]
    {
        Row(ScanStartedCapability, ScanStartedHandler, "observe"),
        Row(ScanProgressCapability, ScanProgressHandler, "observe"),
        Row(ScanCompletedCapability, ScanCompletedHandler, "observe"),
        Row(ContainerStateCapability, ContainerStateHandler, "observe"),
        Row(ItemPickupCapability, ItemPickupHandler, "observe"),
        Row(ScanStateCapability, ScanStateHandler, "evaluate")
    };

    /// <summary>The value row's port shape: a value row has no execution ports at all — its input is the
    /// resource it reads and its outputs are its readings.</summary>
    public static readonly HandlerShape ScanStateShape = new HandlerShape()
        .Inputs("scan").Outputs("value", "state", "progress");

    /// <summary>The one shape, keyed by handler name: what the integration batch adds to the registration's
    /// shape table beside the native handlers' own.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [ScanStateHandler] = ScanStateShape
    };

    private static object Row(string capability, string handler, string role) => new
    {
        id = Binding(capability),
        capabilityId = capability,
        providerId = ProviderId,
        handler,
        role,
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    /// <summary>The reader of the value row, as the native half hands it over. This assembly declares the row
    /// and holds no game type, so the evaluator is built from this delegate and the game-bound registration is
    /// the only caller that supplies it. There is no static slot here: two registrations in one process would
    /// otherwise answer each other's reads.
    /// </summary>
    public sealed record LevelObjectReaders(Func<EvaluationContext, System.Text.Json.JsonElement> ScanState);

    /// <summary>The one evaluator, keyed by handler name: what the integration batch adds to the
    /// registration's evaluator table beside the player selector's.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(LevelObjectReaders readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        return new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [ScanStateHandler] = context => readers.ScanState(context)
        };
    }
}
