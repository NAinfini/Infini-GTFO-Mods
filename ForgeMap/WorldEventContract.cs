using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The world-event rows of the level: the two mount points a level's own world-event objects offer — the
/// interact trigger and the look-at trigger — and the one action that sets a world event condition, which is what
/// those mount points' authored condition lists are checked against.
///
/// The three rows are one family because they meet at one native type. A `LG_WorldEventObject` carries a stable
/// `WorldEventObjectKey`, and a `WardenObjectiveEventData` names its target through the very same text
/// (`WorldEventObjectFilter`); the two trigger rows publish that key as their subject, and the action writes the
/// `SetWorldEventCondition` member of the struct whose executor — `WorldEventManager.ExecuteEvent` — is the entry
/// every vanilla mount point already uses. Nothing here writes the state an event owns.
///
/// The capability ids the two trigger rows carry are **new** canonical ids: the catalog's `forge.trigger.map.*`
/// family has no world-event row (its members are generator_cell, generator_cluster, item_pickup, zone_entered and
/// portal_warped), and the two rows are distinguishable native types rather than one row with a `kind` parameter,
/// because a plan reads a different port set from each. This is the same rule the door and terminal interaction
/// rows already follow (`DoorTerminalEventContract` declares `forge.trigger.interaction.door_scan` and its two
/// siblings for the same reason).
///
/// The action row keeps an id of its own rather than folding into `forge.action.map.objective_state`: a world
/// event condition is not objective state — it is the flag a world event object's own condition list reads — and
/// the native entry is a different member of the same executor.
///
/// Every row is `host` execution. The two trigger rows publish only on the authoritative peer, because a world
/// event object's activation is replicated state and the host is the peer that decides it; the action writes
/// through the executor, which replicates the struct's fields the way a vanilla event list does.</summary>
public static class WorldEventContract
{
    /// <summary>The provider this family's rows belong to, spelled once here: the declaration and every binding id
    /// below are built from it, and it is the same constant `LevelEventContract`, `DoorActionContract` and every
    /// other family of this package build theirs from. A focused case asserts the two agree.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    // ---- triggers -----------------------------------------------------------------------------------

    /// <summary>The checklist's "world event object interacted": the level's own `LG_InteractWorldEventTrigger`.
    /// Its `Trigger(SNet_Player, Item)` is the game's own activation entry — the same body the sync replicator's
    /// master side calls — so the row's fact is the activation the game performed and not a guess this provider
    /// makes about it.</summary>
    public const string WorldEventInteractCapability = "forge.trigger.interaction.world_event_interact";
    /// <summary>The checklist's "world event object looked at": `LG_LookatWorldEventTrigger`, the second of the two
    /// trigger components a world event object can carry. It is declared as its own row because the two native
    /// types carry different fields (the look-at trigger's own `m_lookatMaxDistance` and its world position are what
    /// an author filters on) and because a plan that mounts on one of them must not also receive the other's
    /// facts.</summary>
    public const string WorldEventLookatCapability = "forge.trigger.interaction.world_event_lookat";

    /// <summary>The two fact kinds these rows carry, in the order the rows are declared. A binding or a fact key
    /// names one of these, never the capability again, so the two cannot drift apart.</summary>
    public const string WorldEventInteractFact = "world_event_interact";
    public const string WorldEventLookatFact = "world_event_lookat";

    /// <summary>The two moments of one trigger row, in the order the native entry reaches them: `triggered` is the
    /// activation and `reset` is the trigger's own reusable reset, which a level's own `TriggerEventsOnReset` flag
    /// decides whether to run the authored list again — a plan sees the reset either way, because the fact is the
    /// trigger's state and not the list's decision. They are this contract's own two moments and travel as the
    /// native boolean `pSyncTriggerState.HasBeenTriggered` really is (`true` when triggered, `false` when reset),
    /// so the port needs no vocabulary the catalog would have to carry a new enum set for.</summary>
    internal const string TriggeredMoment = "triggered";
    internal const string ResetMoment = "reset";

    // ---- action -------------------------------------------------------------------------------------

    /// <summary>The checklist's "set a world event condition": `eWardenObjectiveEventType.SetWorldEventCondition`
    /// (19) through `WorldEventManager.ExecuteEvent`. A condition index is one slot of the machine's own
    /// `pWorldEventManagerState.WorldEventConditions`, and that state declares `WORLD_EVENT_CONDITION_COUNT` = 16
    /// slots, which is where the index bound below comes from.</summary>
    public const string WorldEventConditionCapability = "forge.action.map.world_event_condition";

    public const string WorldEventConditionHandlerName = "gtfo.map.world_event_condition";

    /// <summary>The one domain list every row here names. A world event object is a level object a room, a logic
    /// step and a tool all act on — the same set the package's other map-object rows carry.</summary>
    internal static readonly string[] Domains = { "map", "room", "tool", "consumable" };

    /// <summary>The provider-side binding id of one capability: this provider's own namespace plus the
    /// capability's own suffix, which is the rule `LevelEventContract` and `DoorTerminalEventContract` already
    /// spell, so a binding id that disagrees with the capability it serves fails registration.</summary>
    public static string Binding(string capabilityId)
        => capabilityId.StartsWith("forge.", StringComparison.Ordinal)
            ? ProviderId + ".binding." + capabilityId["forge.".Length..].Replace('.', '_')
            : throw new RuntimeContractException("world-event-capability", capabilityId);

    // ---- permissions --------------------------------------------------------------------------------

    /// <summary>The permission the two trigger rows read under: what they publish is a level object's own
    /// activation, so it is the map-object namespace's read permission, spelled as the constant value the
    /// map-object rows declare rather than by reading their contract — this file declares its permissions on its
    /// own so it compiles without a second family's contract beside it, and a case asserts the two spellings
    /// agree.</summary>
    public const string MapObjectReadPermission = "gtfo.map_object.read";
    /// <summary>The permission the condition action writes under. It sets the flag a world event object's own
    /// condition list reads, which is objective machine state and not a map object's, so the namespace is the
    /// objective one the level-event actions already write under.</summary>
    public const string WorldEventWritePermission = "objective.condition";

    // ---- parameters ---------------------------------------------------------------------------------

    // ---- vocabularies -------------------------------------------------------------------------------

    /// <summary>One published moment as the boolean the native trigger state is: an activation is the trigger
    /// having been triggered and a reset is it having been cleared. A state key of one moment is therefore the
    /// boolean itself, which is what makes a repeated activation of an already-triggered object not news.</summary>
    internal static bool IsTriggered(string moment) => moment == TriggeredMoment;

    // ---- rows ---------------------------------------------------------------------------------------

    /// <summary>The two trigger rows, paired with the fact each one carries, in declaration order.</summary>
    internal static readonly (string Fact, string Capability)[] TriggerTable =
    {
        (WorldEventInteractFact, WorldEventInteractCapability),
        (WorldEventLookatFact, WorldEventLookatCapability)
    };

    public static string TriggerCapability(string fact) => fact switch
    {
        WorldEventInteractFact => WorldEventInteractCapability,
        WorldEventLookatFact => WorldEventLookatCapability,
        _ => throw new RuntimeContractException("world-event-trigger-fact", "Unknown world event trigger fact.")
    };

    internal static object Port(string id, string type) => new { id, type };
    internal static object Optional(string id, string type) => new { id, type, optional = true };
    internal static object ZonePort(string id)
        => new { id, type = "resource", resourceKind = "zone", schema = "forge.resource.zone" };
    internal static object PositionPort(string id) => new { id, type = "vector3", unit = "m" };

    /// <summary>One trigger row: the catalog's own shape — id, owner, kind, label, version, description and a
    /// graph block with the domains, the host execution and the ports the row really publishes. `key` is the
    /// world event object's own `WorldEventObjectKey` text, carried as a property rather than as a resource id
    /// because it is the same text the action's target filter is authored with.</summary>
    private static object TriggerRow(string capability, string label, string description, object[] outputs)
        => new
        {
            id = capability, owner = ProviderId, kind = "trigger", label, version = "1.0.0",
            parameters = new { description },
            graph = new
            {
                domains = Domains, execution = "host", inputs = Array.Empty<object>(), outputs,
                parameters = Array.Empty<object>()
            }
        };

    /// <summary>The interact row: the object's own key, the trigger state, the player whose interaction did it, the
    /// zone the object stands in and its world position. The zone and the position are what an author filters on —
    /// a world event object has no map address of its own, so the key is the identity a plan wires on.</summary>
    public static object InteractRow() => TriggerRow(WorldEventInteractCapability, "世界事件物被交互",
        "交互世界事件物（按下交互键）。", new object[]
        {
            Port("next", "execution"),
            Port("key", "string"),
            Port("triggered", "boolean"),
            Port("source", "entity"),
            ZonePort("zone"),
            PositionPort("position")
        });

    /// <summary>The look-at row: the same facts as the interact row plus the trigger's own look-at distance,
    /// which is the field this component kind exists for. A row that omitted it would make the two trigger kinds
    /// indistinguishable in a plan.</summary>
    public static object LookatRow() => TriggerRow(WorldEventLookatCapability, "世界事件物被注视",
        "注视世界事件物到足够近。", new object[]
        {
            Port("next", "execution"),
            Port("key", "string"),
            Port("triggered", "boolean"),
            Port("source", "entity"),
            ZonePort("zone"),
            PositionPort("position"),
            Optional("lookat_distance", "number")
        });

    /// <summary>The two trigger rows in <see cref="TriggerTable"/> order.</summary>
    public static object[] TriggerRows() => new object[] { InteractRow(), LookatRow() };

    /// <summary>The one action row: the condition slot and the value to write, with the request's own target
    /// reference refused by the handler — the native event names its condition by index and its target by the
    /// object filter, so a plan's target set is not a shape this row can honor.</summary>
    public static object ActionRow() => new
    {
        id = WorldEventConditionCapability, owner = ProviderId, kind = "action",
        label = "设置世界事件条件", version = "1.0.0",
        parameters = new { description = "把一个世界事件条件的槽位设为真，或者清除它。" },
        graph = new
        {
            domains = Domains, execution = "host",
            inputs = new object[] { Port("in", "execution"), Port("targets", "entity") },
            outputs = new object[]
            {
                Port("next", "execution"),
                Result()
            },
            parameters = new object[]
            {
                Integer("condition", required: true),
                new { id = "solved", type = "boolean", role = "structural", required = true }
            },
            recipients = new
            {
                input = "targets", target = "entity", cardinality = "one",
                requires = new[] { WorldEventWritePermission }, result = "result"
            }
        }
    };

    /// <summary>The result row every action of this package carries: the four shared columns, this row's own extra
    /// fields and the request's own target count.</summary>
    private static object Result()
    {
        var fields = new List<object>
        {
            new { id = "target", type = "entity" },
            new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" },
            new { id = "code", type = "string" },
            new { id = "condition", type = "integer" },
            new { id = "solved", type = "boolean" },
            new { id = "target_count", type = "integer" }
        };
        return new { id = "result", type = "result", schema = "forge.result.map.world_event_condition", fields };
    }

    /// <summary>Every capability row this file declares: the two trigger rows and the one action row.</summary>
    public static object[] CapabilityRows() => new object[] { InteractRow(), LookatRow(), ActionRow() };

    private static object Integer(string id, bool required = false)
        => new { id, type = "integer", role = "structural", required };

    // ---- bindings -----------------------------------------------------------------------------------

    /// <summary>One observe binding row: this provider's own id, the capability, the fact kind the native callback
    /// names and `observe` as the role — neither trigger row has a handler, because both publish state the game
    /// already replicated instead of acting on it.</summary>
    public static object TriggerBinding(string fact) => new
    {
        id = Binding(TriggerCapability(fact)), capabilityId = TriggerCapability(fact), providerId = ProviderId,
        handler = ProviderId + ".observe." + fact, role = "observe", status = "implemented",
        dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    /// <summary>The one execute binding row: the condition action's handler, which is the name
    /// `MapPluginSession`'s own handler table answers with `WorldEventActions.Condition`.</summary>
    public static object ActionBinding() => new
    {
        id = Binding(WorldEventConditionCapability), capabilityId = WorldEventConditionCapability,
        providerId = ProviderId, handler = WorldEventConditionHandlerName, role = "execute", status = "implemented",
        dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    public static object[] BindingRows() => new object[]
    {
        TriggerBinding(WorldEventInteractFact), TriggerBinding(WorldEventLookatFact), ActionBinding()
    };

    /// <summary>One binding's registration support: reading a world event object at all is the map-object
    /// namespace's own read permission, and writing a condition is the objective machine's own namespace.</summary>
    public static BindingSupport TriggerSupport(string fact)
        => new(Binding(TriggerCapability(fact)), "implementation-only", new[] { MapObjectReadPermission });

    public static BindingSupport ActionSupport()
        => new(Binding(WorldEventConditionCapability), "implementation-only", new[] { WorldEventWritePermission });

    public static BindingSupport[] Supports() => new[]
    {
        TriggerSupport(WorldEventInteractFact), TriggerSupport(WorldEventLookatFact), ActionSupport()
    };

    // ---- the handler's shape and its one decision ----------------------------------------------------

    /// <summary>The condition action's port layout: the one port the native event carries a value in — the target
    /// entity the event is executed for — and the two parameters its own pair is built from. `condition` and
    /// `solved` are parameters rather than inputs because the row declares them as such: the plan compiles them as
    /// constants or promotes them, and either way the handler reads them by their declared parameter index.</summary>
    public static readonly HandlerShape ConditionShape = new HandlerShape()
        .Inputs("targets").Outputs("result").Parameters("condition", "solved");

    /// <summary>The shape table of this family: the one action handler. An observation row carries no handler
    /// table entry, so it declares no shape.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes()
        => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [WorldEventConditionHandlerName] = ConditionShape
        };

    // ---- the condition slot -------------------------------------------------------------------------

    /// <summary>The number of condition slots the machine's own state declares
    /// (`pWorldEventManagerState.WORLD_EVENT_CONDITION_COUNT`, read from the build 20403457). An index outside
    /// `[0, 16)` is one the replicated state has no slot for, so it is refused rather than truncated.</summary>
    public const int ConditionSlots = 16;

    /// <summary>Every code the condition action refuses with, so a plan sees a stable spelling for each reason and
    /// a case can assert one without restating it.</summary>
    public const string AuthorityCode = "authority-or-phase";
    public const string TargetCode = "world-event-target-unsupported";
    public const string ConditionCode = "world-event-condition-invalid";
    public const string SolvedCode = "world-event-solved-invalid";

    /// <summary>One condition request, already validated: the slot it names and the value it writes. It is the
    /// only shape the native executor accepts, so a value that reached the executor is known to be legal.</summary>
    public readonly struct ConditionRequest
    {
        internal ConditionRequest(int index, bool solved) { Index = index; Solved = solved; }
        public int Index { get; }
        public bool Solved { get; }
    }

    /// <summary>The one decision the condition action makes, taken before the native call and in the
    /// game-independent assembly so a case can drive every refusal without a game. A refusal is one of the codes
    /// above; anything else is a request the native executor may run.</summary>
    public static bool TryCondition(CommandContext context, out ConditionRequest request, out string? code)
    {
        ArgumentNullException.ThrowIfNull(context);
        request = default;
        code = null;
        if (!context.IsHost) { code = AuthorityCode; return false; }
        if (Present(context.Inputs, "targets")) { code = TargetCode; return false; }
        if (!context.Parameters.TryGetProperty("condition", out var condition) || !condition.TryGetInt32(out int index)
            || index < 0 || index >= ConditionSlots)
        {
            code = ConditionCode;
            return false;
        }
        if (!TryBoolean(context.Parameters, "solved", out bool solved))
        {
            code = SolvedCode;
            return false;
        }
        request = new ConditionRequest(index, solved);
        return true;
    }

    /// <summary>The refusal a caller answers with, in the framework's own vocabulary: a request this row cannot
    /// honor wrote nothing, so the commit state is `none`.</summary>
    public static CommandResult Refused(string code) => CommandResult.Rejected(code);

    /// <summary>The outcome of a native call that threw. The executor may already have written the replicated
    /// condition before it threw, so the commit state is `unknown`: claiming nothing happened is a claim this
    /// layer cannot make.</summary>
    public static CommandResult Failed(string code) => CommandResult.FailedUnknown(code);

    /// <summary>Whether one input port is present at all. An input the plan did not wire is absent rather than
    /// null, which is the framework's own way to spell "not supplied".</summary>
    private static bool Present(JsonElement inputs, string port)
        => inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(port, out _);

    /// <summary>One structural boolean, in either spelling the boundary can hand a handler: the JSON boolean a plan
    /// writes, or the `true`/`false` member name a structural enum is resolved to. Anything else is a value the row
    /// cannot honor rather than one that folds into the nearest.</summary>
    private static bool TryBoolean(JsonElement parameter, string id, out bool value)
    {
        value = false;
        if (!parameter.TryGetProperty(id, out var member)) return false;
        if (member.ValueKind is JsonValueKind.True) { value = true; return true; }
        if (member.ValueKind is JsonValueKind.False) return true;
        if (member.ValueKind is JsonValueKind.String)
            return bool.TryParse(member.GetString(), out value);
        return false;
    }

    /// <summary>The two ways a boolean is spelled in a result row, so a reader of the result reads the same member
    /// name the request wrote.</summary>
    internal static string Spelling(bool value) => value ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant();

    /// <summary>The slot and value of one condition request as the result's own two fields, so a plan that set a
    /// condition can read back what it set without a second query.</summary>
    public static JsonElement Outputs(ConditionRequest request) => RuntimeJson.From(new
    {
        condition = request.Index,
        solved = request.Solved
    });
}
