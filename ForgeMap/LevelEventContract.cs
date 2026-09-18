using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The level-lifecycle, objective-transition and dimension rows of the checklist: the elevator landing that
/// starts an expedition, the objective chain step a reactor wave is, the HSU's own "the sample is in" transition, the
/// checkpoint reload, the zone a player walks into, the dimension a portal sends them to — and the two actions that
/// ride the same native entry: the objective's own countdown, and the team's dimension moves.
///
/// One runtime entry carries all of them. `WorldEventManager.ExecuteEvent(WardenObjectiveEventData)` is the
/// game's own level-event executor — the same call every vanilla mount point uses — so an action this file
/// declares builds the event struct with its own type member and hands it over, instead of writing the state the
/// event owns. That is what keeps a client's world consistent: the call runs on the host and the struct's own
/// replicated fields do the rest.
///
/// The event types this file names are `GameData.eWardenObjectiveEventType` members, read from the build
/// (20403457): `AddToTimer` 24, `ResetTimer` 25, `WinOnDeath` 26, `ForceInstantWin` 27, `DimensionFlashTeam` 7,
/// `DimensionWarpTeam` 8, `ClearDimension` 30.
///
/// Every capability row this family observes is the runtime's own: the six `forge.trigger.*` ids below are
/// declared by the trigger contract (`forge.contract.trigger`, `ForgeRuntime/Framework/TriggerContracts.cs`,
/// ruling 148.3), because the catalog row for these transitions carries no port this row cannot be bound to and
/// an id has exactly one owner. This file therefore declares no trigger capability row at all — it names the
/// canonical id, its own binding, its registration row and the fact that travels through them. The three
/// `forge.action.*` rows have no canonical owner and stay declared here.</summary>
public static class LevelEventContract
{
    /// <summary>The provider this family's rows belong to, spelled once here: the declaration and every binding id
    /// below are built from it, and the provider declaration's own `ProviderId` is the same constant — a focused
    /// case asserts the two agree, so this contract compiles without the declaration beside it.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    // ---- triggers -----------------------------------------------------------------------------------

    /// <summary>The checklist's "level started (elevator landed)". The catalog declares this id with a `next` and
    /// a `map` resource output; the runtime's own trigger contract leaves it out, so this provider declares it and
    /// publishes the level it stands in as the reference the `level` attachment matcher already compares.</summary>
    public const string ExpeditionStartedCapability = "forge.trigger.session.expedition_started";
    /// <summary>The reactor objective's own chain step. A reactor's waves are the objective's event chain, so what
    /// makes a new wave is the chain index the objective machine already reports; there is no author-defined wave
    /// instance to name and publishing one would invent an identity the game does not have.</summary>
    public const string ReactorWaveCapability = "forge.trigger.objective.reactor_wave";
    /// <summary>The HSU's own activation: the objective item has been taken out of the activator. The mount point
    /// is the HSU objective's `OnLocalPlayerSolvedObjectiveItem`, which is the transition the vanilla
    /// `ActivateHSU_Events` list is attached to.</summary>
    public const string HsuSampledCapability = "forge.trigger.objective.hsu_sampled";
    /// <summary>The session's checkpoint reload, fired once the recall has finished.</summary>
    public const string CheckpointRestoredCapability = "forge.trigger.session.checkpoint_restored";
    /// <summary>A player entering a zone. The mount point is the game's own local-player zone callback, which
    /// carries the player and the zone; the row reports the zone reference the level's own zone table resolves, so
    /// the three-layer address is the same one every other level-scope row uses.</summary>
    public const string ZoneEnteredCapability = "forge.trigger.map.zone_entered";
    /// <summary>A dimension portal sending a player through. The mount point is the portal's own state change;
    /// the row reports the portal and the transition it made, which is the fact the vanilla
    /// `EventsOnPortalWarp` list is attached to.</summary>
    public const string PortalWarpedCapability = "forge.trigger.map.portal_warped";

    public const string ExpeditionStartedFact = "session.expedition_started";
    public const string ReactorWaveFact = "objective.reactor_wave";
    public const string HsuSampledFact = "objective.hsu_sampled";
    public const string CheckpointRestoredFact = "session.checkpoint_restored";
    public const string ZoneEnteredFact = "map.zone_entered";
    public const string PortalWarpedFact = "map.portal_warped";

    // ---- actions ------------------------------------------------------------------------------------

    /// <summary>The checklist's "objective countdown: add time or reset". An id of its own rather than the
    /// generic `objective_state` row's `SetExtraTime` member: `reset` puts the countdown back to the value the
    /// objective's own data block set, which is a different native event (`ResetTimer`) from adding time.</summary>
    public const string ObjectiveTimerCapability = "forge.action.map.objective_timer";
    /// <summary>The checklist's "flash / warp the team into a dimension, or clear a dimension": the three members
    /// of one native event family, which is why they are one row with one structural parameter.</summary>
    public const string DimensionCapability = "forge.action.player.dimension";
    /// <summary>The checklist's "win immediately / a wipe counts as a win": two native events that say how the
    /// expedition ends, which is why they are one row with one structural parameter.</summary>
    public const string ExpeditionEndCapability = "forge.action.map.expedition_end";

    public const string ObjectiveTimerHandlerName = "gtfo.map.objective_timer";
    public const string DimensionHandlerName = "gtfo.map.player_dimension";
    public const string ExpeditionEndHandlerName = "gtfo.map.expedition_end";

    public static string Binding(string capabilityId) => ProviderId + ".binding." + Suffix(capabilityId);
    public static string BindingId(string capabilityId) => Binding(capabilityId);

    // ---- permissions --------------------------------------------------------------------------------

    /// <summary>The permission the trigger rows read under: what they publish is the objective machine's and the
    /// level session's own decision, so the permission names the domain rather than an instance.</summary>
    public const string ObjectiveReadPermission = "objective.read";
    /// <summary>The permission the session rows read under — the checkpoint and the expedition start.</summary>
    public const string SessionReadPermission = "session.read";
    /// <summary>The permission the zone and portal rows read under: both are level objects the map owns, so the
    /// permission is the map-object namespace's read permission, spelled as the constant value the map-object
    /// rows declare rather than by reading their contract — this contract declares its rows' permissions on its
    /// own so it compiles without a second family's contract beside it, and a case asserts the two spellings
    /// agree.</summary>
    public const string MapObjectReadPermission = "gtfo.map_object.read";
    /// <summary>The permission the countdown action writes under. It moves the state of an objective this map's
    /// own data blocks declare, so it is the same namespace the objective action rows already write under.</summary>
    public const string ObjectiveWritePermission = "objective.timer";
    /// <summary>The permission the dimension action writes under: it moves the players the same way
    /// `forge.action.player.teleport` does, so it names the same namespace.</summary>
    public const string PlayerWarpPermission = "player.warp";
    /// <summary>The permission the expedition-end action writes under.</summary>
    public const string ExpeditionEndPermission = "expedition.end";

    // ---- vocabularies -------------------------------------------------------------------------------

    /// <summary>`LG_LayerType`'s members as the rows index them, in the enum's own order.</summary>
    public static readonly string[] Layers = { "main", "secondary", "third" };
    /// <summary>The two things the countdown row can do, which are the two native events it can reach.</summary>
    public static readonly string[] TimerOperations = { "add", "reset" };
    /// <summary>The two things the dimension row can do with an index, which are the two native team events it can
    /// reach besides `clear`.</summary>
    public static readonly string[] DimensionModes = { "flash", "warp", "clear" };
    /// <summary>The two ways the expedition-end row can end it: `instant_win` ends it now, `win_on_death` makes
    /// the next wipe the win the objective's own completion check looks for.</summary>
    public static readonly string[] ExpeditionOutcomes = { "instant_win", "win_on_death" };

    // ---- handler shapes -----------------------------------------------------------------------------

    /// <summary>No trigger row carries an input: every one of them is a native callback's own report.</summary>
    public static readonly HandlerShape TriggerShape = new HandlerShape();
    /// <summary>The countdown command: the seconds it adds and the operation it performs. The catalog's own
    /// `objectives` resource input is declared on the row because the framework requires an action's recipient
    /// port, and the handler refuses a plan that supplies one: no provider in this runtime answers a resource
    /// reference the way an action target would have to be resolved, so serving it would be a promise nothing
    /// keeps.</summary>
    public static readonly HandlerShape TimerShape = new HandlerShape()
        .Inputs("seconds").Outputs("result").Parameters("operation");
    /// <summary>The dimension command: the mode, the destination index and the authored destination table.
    /// `players` is a real target when the request carries a table — that is the EOS dimension-warp half folded in
    /// here, and each named player lands on its own entry — and is refused when it does not, because the three
    /// native team events move the whole team and a plan's own player set is then not a target this row can honor.
    ///
    /// The table is the row's `positions` and `look_dirs` collections, zipped by index, because this runtime has no
    /// list parameter type: a `vector3` port with `cardinality = "many"` is the one collection form a plan can
    /// author (`forge.control.flow.for_each_position` carries its own `positions` that way), and a plan's own
    /// collection literal is what the frame builder folds into its constant pool. `positions` is metres and
    /// `look_dirs` carries no unit at all: a facing direction is not a length.</summary>
    public static readonly HandlerShape DimensionShape = new HandlerShape()
        .Inputs("players", "dimension", "positions", "look_dirs").Outputs("result").Parameters("mode");
    /// <summary>The expedition-end command: the outcome. `participants` is declared and refused for the same
    /// reason: the expedition ends for everyone in it, not for a subset a plan names.</summary>
    public static readonly HandlerShape ExpeditionEndShape = new HandlerShape()
        .Outputs("result").Parameters("ending");

    // ---- rows ---------------------------------------------------------------------------------------

    /// <summary>Every trigger row this file declares, in declaration order, as (fact, capability). The binding is
    /// the capability's own suffix under this provider, so a hook names the fact and the registration names the
    /// binding without a third spelling in between.</summary>
    public static readonly IReadOnlyList<(string Fact, string Capability)> Triggers = Array.AsReadOnly(new[]
    {
        (ExpeditionStartedFact, ExpeditionStartedCapability),
        (ReactorWaveFact, ReactorWaveCapability),
        (HsuSampledFact, HsuSampledCapability),
        (CheckpointRestoredFact, CheckpointRestoredCapability),
        (ZoneEnteredFact, ZoneEnteredCapability),
        (PortalWarpedFact, PortalWarpedCapability)
    });

    /// <summary>Every action row, in declaration order, as (capability, handler).</summary>
    public static readonly IReadOnlyList<(string Capability, string Handler)> Actions = Array.AsReadOnly(new[]
    {
        (ObjectiveTimerCapability, ObjectiveTimerHandlerName),
        (DimensionCapability, DimensionHandlerName),
        (ExpeditionEndCapability, ExpeditionEndHandlerName)
    });

    public static string Suffix(string capabilityId)
        => capabilityId.StartsWith("forge.", StringComparison.Ordinal)
            ? capabilityId["forge.".Length..].Replace('.', '_')
            : throw new RuntimeContractException("level-event-capability", capabilityId);

    /// <summary>One output port of an action row. A trigger row's ports are not built here any more: the
    /// six trigger capability rows are the runtime's trigger contract's own text (ruling 148.3), so this file
    /// carries no second spelling of them.</summary>
    public static object Port(string id, string type) => new { id, type };
    public static object Port(string id, string type, string[] entityKinds) => new { id, type, entityKinds };
    /// <summary>An entity port the recipient contract declares as a collection: the framework requires the port's
    /// own cardinality to be the one the contract names, and a per-recipient row addresses many players.</summary>
    public static object ManyPort(string id, string type, string[] entityKinds)
        => new { id, type, cardinality = "many", entityKinds };
    /// <summary>One resource input port: the kind and the schema name the provider answers for, so a plan that
    /// wires a reference of another kind fails the port rather than being read as the nearest one.</summary>
    public static object ResourcePort(string id, string kind, string schema)
        => new { id, type = "resource", resourceKind = kind, schema };
    /// <summary>One integer port of an action row.</summary>
    public static object Integer(string id) => new { id, type = "integer" };
    /// <summary>One number port carrying the runtime's own tick unit.</summary>
    public static object Number(string id) => new { id, type = "number", unit = "tick" };
    /// <summary>One collection port of `vector3` values, optional because a row accepts it only in the shape that
    /// carries an authored table. It is the one collection form this runtime has for authored positions. A unit is
    /// carried only when the caller names one: a position is metres, a facing direction is not a length.</summary>
    public static object VectorList(string id, string? unit)
        => unit == null
            ? new { id, type = "vector3", cardinality = "many", optional = true }
            : new { id, type = "vector3", cardinality = "many", unit, optional = true };
    /// <summary>One integer port a row reads only in one of its shapes, so its absence is a legal request rather
    /// than a missing input.</summary>
    public static object OptionalInteger(string id) => new { id, type = "integer", optional = true };

    /// <summary>The level reference an expedition-start fact carries: the same identity string the `level`
    /// attachment matcher compares, read from the live expedition and never invented.</summary>
    public static object MapReference(string levelReference)
        => new { resourceKind = "map", resourceId = levelReference };

    /// <summary>The objective reference a per-layer row carries. An objective has no per-instance native identity
    /// — the machine keeps three fixed layer slots — so the id is the layer's own name, which is what the plan's
    /// structural `layer` parameter already spells.</summary>
    public static object ObjectiveReference(string layer)
        => new { resourceKind = "objective", resourceId = "layer:" + layer };

    /// <summary>One binding row: this provider's own id under its own namespace, the capability it implements and
    /// the fact or handler the native half answers with. A trigger binding is an observation, so it declares no
    /// handler name beyond the fact it publishes and no result row.</summary>
    public static object TriggerBinding(string capability)
        => new
        {
            id = Binding(capability), capabilityId = capability, providerId = ProviderId,
            handler = "gtfo.map." + Suffix(capability), role = "observe", status = "implemented",
            dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
        };

    /// <summary>One execute binding row for an action.</summary>
    public static object ActionBinding(string capability, string handler)
        => new
        {
            id = Binding(capability), capabilityId = capability, providerId = ProviderId,
            handler, role = "execute", status = "implemented",
            dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
        };

    public static BindingSupport TriggerSupport(string capability)
        => new(Binding(capability), "implementation-only", new[] { ReadPermission(capability) });

    public static BindingSupport ActionSupport(string capability)
        => new(Binding(capability), "implementation-only", new[] { WritePermission(capability) });

    private static string ReadPermission(string capability) => capability switch
    {
        ZoneEnteredCapability or PortalWarpedCapability => MapObjectReadPermission,
        CheckpointRestoredCapability or ExpeditionStartedCapability => SessionReadPermission,
        _ => ObjectiveReadPermission
    };

    private static string WritePermission(string capability) => capability switch
    {
        DimensionCapability => PlayerWarpPermission,
        ExpeditionEndCapability => ExpeditionEndPermission,
        _ => ObjectiveWritePermission
    };

    /// <summary>The six trigger binding rows, in the same order.</summary>
    public static object[] TriggerBindings()
    {
        var rows = new object[Triggers.Count];
        for (int index = 0; index < Triggers.Count; index++) rows[index] = TriggerBinding(Triggers[index].Capability);
        return rows;
    }

    /// <summary>The six registration support rows, in the same order.</summary>
    public static BindingSupport[] TriggerSupports()
    {
        var rows = new BindingSupport[Triggers.Count];
        for (int index = 0; index < Triggers.Count; index++) rows[index] = TriggerSupport(Triggers[index].Capability);
        return rows;
    }

    /// <summary>The three action capability rows, in <see cref="Actions"/> order, spelled the way the website's
    /// catalog spells them: the same ports, the same structural parameters and the same result schemas. The one
    /// difference is the resource input the catalog names on the timer row: no provider in this runtime answers a
    /// resource reference, so the row carries the fields without it and the handler refuses a plan that supplies
    /// one, exactly as the objective action rows already do.</summary>
    public static object[] ActionRows() => new object[]
    {
        PrimitiveActionRow(ObjectiveTimerCapability, "目标倒计时加时或重置",
            "给目标倒计时加时间，或者把它重置回起点。"),
        PrimitiveActionRow(DimensionCapability, "全队闪入、传送进维度或清空维度",
            "把整队闪一下、按名单逐个传送进另一个维度，或者清空一个维度。"),
        ActionRow(ExpeditionEndCapability, "立即通关 / 全队倒地也算通关",
            "以明确的结果结束远征。",
            new object[] { Port("in", "execution"), Port("participants", "entity", new[] { "gtfo.player" }) },
            new object[] { Enum("ending", ExpeditionOutcomes, required: true) },
            "forge.result.map.expedition_end", Array.Empty<(string, string)>(),
            new { input = "participants", target = "entity", cardinality = "one", requires = new[] { "expedition.end" }, result = "result" })
    };

    private static object PrimitiveActionRow(string capability, string label, string description) => new
    {
        id = capability, owner = ProviderId, kind = "action", label, version = "1.0.0",
        parameters = new { description },
        graph = PrimitiveGraphSource.Get(capability)
    };

    /// <summary>The three action binding rows, in the same order.</summary>
    public static object[] ActionBindings()
    {
        var rows = new object[Actions.Count];
        for (int index = 0; index < Actions.Count; index++)
            rows[index] = ActionBinding(Actions[index].Capability, Actions[index].Handler);
        return rows;
    }

    /// <summary>The three action registration support rows, in the same order.</summary>
    public static BindingSupport[] ActionSupports()
    {
        var rows = new BindingSupport[Actions.Count];
        for (int index = 0; index < Actions.Count; index++) rows[index] = ActionSupport(Actions[index].Capability);
        return rows;
    }

    /// <summary>Every capability row this file declares, which is the three action rows: the six trigger rows
    /// are the trigger contract's own declaration and this file adds no second copy of them (ruling 148.3).</summary>
    public static object[] CapabilityRows() => ActionRows();

    /// <summary>Every binding row, in the same order as <see cref="CapabilityRows"/>.</summary>
    public static object[] BindingRows()
    {
        var rows = new object[Triggers.Count + Actions.Count];
        TriggerBindings().CopyTo(rows, 0);
        ActionBindings().CopyTo(rows, Triggers.Count);
        return rows;
    }

    /// <summary>Every registration support row, in the same order.</summary>
    public static BindingSupport[] Supports()
    {
        var rows = new BindingSupport[Triggers.Count + Actions.Count];
        TriggerSupports().CopyTo(rows, 0);
        ActionSupports().CopyTo(rows, Triggers.Count);
        return rows;
    }

    /// <summary>The shape table of this family: the three action handlers and nothing else. An observation binding
    /// carries no handler table entry, so it declares no shape — a table entry the registration does not have a
    /// handler for is refused, and the six trigger rows add none.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes()
        => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [ObjectiveTimerHandlerName] = TimerShape,
            [DimensionHandlerName] = DimensionShape,
            [ExpeditionEndHandlerName] = ExpeditionEndShape
        };

    /// <summary>The handler name a trigger's binding publishes under, so a hook and the registration spell the
    /// same name.</summary>
    public static string HandlerName(string capabilityId) => "gtfo.map." + Suffix(capabilityId);

    private static object ActionRow(string capability, string label, string description, object[] inputs,
        object[] parameters, string resultSchema, (string Id, string Type)[] resultFields, object recipients)
        => new
        {
            id = capability, owner = ProviderId, kind = "action", label, version = "1.0.0",
            parameters = new { description },
            graph = new
            {
                domains = TriggerDomains, execution = "host", inputs,
                outputs = new object[] { Port("next", "execution"), Result(resultSchema, resultFields) },
                parameters,
                recipients
            }
        };

    /// <summary>The result row the catalog declares: the four shared columns, this row's own extra fields and the
    /// request's own target count. A field carries a unit only when it is a number, because the runtime refuses a
    /// unit on any other type.</summary>
    private static object Result(string schema, (string Id, string Type)[] extra)
    {
        var fields = new List<object>
        {
            new { id = "target", type = "entity" },
            new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" },
            new { id = "code", type = "string" }
        };
        foreach (var (id, type) in extra)
        {
            if (type == "number") fields.Add(new { id, type, unit = "tick" });
            else fields.Add(new { id, type });
        }
        fields.Add(new { id = "target_count", type = "integer" });
        return new { id = "result", type = "result", schema, fields };
    }

    private static object Enum(string id, string[] values, bool required = false)
        => new { id, type = "enum", role = "structural", required, values };

    private static readonly string[] TriggerDomains = { "map", "room", "logic" };
}
