using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The player state this provider observes and the read-only value rows it answers: the "after the fact"
/// damage event, the low-health and infection events, and the values a plan reads out of one player's own state.
///
/// Three things about this family are deliberate:
/// <list type="bullet">
/// <item>The damage row is the canonical `forge.trigger.combat.damage_applied` — one capability, published by
/// whoever can observe an application — because a second "player damage" capability would be a second name for
/// one fact. This provider adds a binding for `gtfo.player`, never a capability: the runtime's trigger contract
/// owns that row's shape, the enemy half already binds the same id for its own subject, and the catalog is the
/// authority for the ports. One port this provider publishes has no home in the catalog's row yet and is
/// requested by the integration fragment: `friendly_fire`, optional and appended, because the game reports a
/// friendly-fire event of its own and the player row is the only place it can be read.</item>
/// <item>`forge.trigger.player.low_health` and `forge.trigger.player.infection_changed` are declared rows of the
/// trigger contract (the text below is what the integration batch moves there) and observed by this provider. The
/// game has a low-health event of its own; the infection row carries the value it changed to and the delta, both
/// read from the receiver rather than derived from a threshold crossing.</item>
/// <item>The value rows are this provider's own capabilities, so they are declared here with the full graph —
/// unlike a trigger, whose shape belongs to the runtime's trigger contract. Each one reads native state on the
/// host through the kernel's budgeted query session and writes nothing; the read layer is
/// `Native/PlayerValueReads.cs` and the shapes below are the ports it answers.</item>
/// </list>
///
/// The player entity kind is the one identity this package already owns (`gtfo.player`); nothing here mints an
/// entity of its own, and no port is filled with a guess. A port the native side could not read is left out of a
/// payload (the framework's "not observable"), while a nullable port the game genuinely answered with nobody is
/// written as JSON null.</summary>
public static class PlayerStateContract
{
    /// <summary>The Map package's provider id. Written here rather than read from
    /// <see cref="ModuleDefinition"/> because this file is also compiled by the focused test project, which must
    /// not drag the whole provider declaration in to build one binding id.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    // ---------------------------------------------------------------- capabilities this provider observes

    /// <summary>The canonical damage row (owner `forge.contract.trigger`): this provider binds it for a player
    /// subject and publishes it from the player receiver's own accept path.</summary>
    public const string DamageAppliedCapability = "forge.trigger.combat.damage_applied";
    /// <summary>Declared by the trigger contract, observed here from the game's own low-health event.</summary>
    public const string LowHealthCapability = "forge.trigger.player.low_health";
    /// <summary>Declared by the trigger contract, observed here from the player receiver's infection write.</summary>
    public const string InfectionChangedCapability = "forge.trigger.player.infection_changed";

    // ---------------------------------------------------------------- capabilities this provider owns

    public const string HealthValueCapability = "forge.query.player.health";
    public const string InfectionValueCapability = "forge.query.player.infection";
    public const string DownedValueCapability = "forge.query.player.downed";
    public const string PositionValueCapability = "forge.query.player.position";
    public const string WieldedGearValueCapability = "forge.query.player.wielded_gear";
    public const string AmmoValueCapability = "forge.query.player.ammo";
    public const string CarriedItemValueCapability = "forge.query.player.carried_item";
    /// <summary>`v-p-tool`: the energy the held item's own class-ammunition pool holds and the consumables the
    /// backpack carries. One row, because the two answers are the two halves of the same question — "how much of
    /// my gear is left" — and the game reads both from the same two objects the row already resolves.</summary>
    public const string ToolValueCapability = "forge.query.player.tool";

    /// <summary>The value handler names: the evaluator this provider registers for each read-only row. The name
    /// is also the shape key, so the registration cannot supply an evaluator whose ports were never declared.</summary>
    public const string HealthValueHandler = "gtfo.player.value.health";
    public const string InfectionValueHandler = "gtfo.player.value.infection";
    public const string DownedValueHandler = "gtfo.player.value.downed";
    public const string PositionValueHandler = "gtfo.player.value.position";
    public const string WieldedGearValueHandler = "gtfo.player.value.wielded_gear";
    public const string AmmoValueHandler = "gtfo.player.value.ammo";
    public const string CarriedItemValueHandler = "gtfo.player.value.carried_item";
    public const string ToolValueHandler = "gtfo.player.value.tool";

    /// <summary>The one entity kind every row here is about. The Map provider owns it; a fact or a value about
    /// another kind is not this family's.</summary>
    public const string EntityKind = "gtfo.player";

    /// <summary>Every observed row this family binds, in registration order: the wire name of the fact and the
    /// capability it is the observation of. One fact has exactly one capability.</summary>
    internal static readonly IReadOnlyList<(string Fact, string Capability)> Facts = Array.AsReadOnly(new[]
    {
        ("damage_applied", DamageAppliedCapability),
        ("low_health", LowHealthCapability),
        ("infection_changed", InfectionChangedCapability)
    });

    internal static string CapabilityOf(string fact)
    {
        foreach (var (name, capability) in Facts) if (string.Equals(name, fact, StringComparison.Ordinal)) return capability;
        throw new RuntimeContractException("player-state-fact", "Unknown player-state fact: " + fact);
    }

    /// <summary>The binding a native transition publishes through: the capability's own suffix under the Map
    /// provider, so either side names the counterpart of a row.</summary>
    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.".Length..];

    internal static string BindingOf(string fact) => Binding(CapabilityOf(fact));

    /// <summary>The handler name that travels with an observe binding. The observation is native and no handler is
    /// dispatched to, so the text is the row's own identity: provider, domain and fact.</summary>
    internal static string HandlerOf(string fact) => "gtfo.map.player." + fact;

    /// <summary>One observe binding row: this provider's own id, the canonical capability it observes and the
    /// handler text that names the observation. It requires no other binding, so the closure of a plan that pins
    /// it is the row itself.</summary>
    public static object Row(string fact) => new
    {
        id = BindingOf(fact),
        capabilityId = CapabilityOf(fact),
        providerId = ProviderId,
        handler = HandlerOf(fact),
        role = "observe",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    /// <summary>Every observe binding row in <see cref="Facts"/> order.</summary>
    public static IReadOnlyList<object> Rows()
    {
        var rows = new object[Facts.Count];
        for (int i = 0; i < Facts.Count; i++) rows[i] = Row(Facts[i].Fact);
        return Array.AsReadOnly(rows);
    }

    /// <summary>The registration support of every observe row: a transition of the state this provider already
    /// tracks reads that state and writes nothing, so no row declares a permission and no row depends on another
    /// binding.</summary>
    public static IReadOnlyList<BindingSupport> Support()
    {
        var support = new BindingSupport[Facts.Count];
        for (int i = 0; i < Facts.Count; i++) support[i] = new BindingSupport(BindingOf(Facts[i].Fact), "implementation-only", Array.Empty<string>());
        return Array.AsReadOnly(support);
    }

    /// <summary>The binding id of every observe row, in declaration order.</summary>
    public static IReadOnlyList<string> BindingIds()
    {
        var ids = new string[Facts.Count];
        for (int i = 0; i < Facts.Count; i++) ids[i] = BindingOf(Facts[i].Fact);
        return Array.AsReadOnly(ids);
    }

    // ---------------------------------------------------------------- payloads

    /// <summary>`damage_applied` for a player: the canonical row's ports in its own order, plus the one optional
    /// port this provider asks to add. `source` is nullable because a damage application whose attacker this
    /// process cannot name is still an application; `damage_kind` is left out when the receive entry that carried
    /// the application is not one of the kinds this half names; `limb` is never carried by this half, because a
    /// player's damage entries hand it no limb index it reads.
    ///
    /// An enum port's wire value is the member's index in the declared set and never its name — the same rule the
    /// enemy package's own `damage_kind` publication follows — so the kind travels as an index and the member
    /// name is what the journal and the evidence carry.</summary>
    public static JsonElement DamageAppliedPayload(EntityReference target, EntityReference? source, double amount,
        int? damageKind, bool? friendlyFire)
        => Payload(
            ("source", source is { } attacker ? Entity(attacker) : Null),
            ("target", Entity(target)),
            ("amount", RuntimeJson.From(amount)),
            ("damage_kind", damageKind == null ? null : RuntimeJson.From(damageKind.Value)),
            ("friendly_fire", friendlyFire == null ? null : RuntimeJson.From(friendlyFire.Value)));

    /// <summary>The `damage_kind` set's declaration order, which is what an enum port carries. Only the members
    /// this half can name are pinned; a receive entry whose kind this half cannot name publishes no port.</summary>
    public const int DamageKindDirect = 0, DamageKindMelee = 1, DamageKindExplosion = 2, DamageKindDot = 3,
        DamageKindCollision = 5, DamageKindFall = 6;

    /// <summary>The member name one declared index spells, for the journal and the evidence. An index outside this
    /// package's own list is a programming error here, not a native condition.</summary>
    public static string DamageKindName(int index) => index switch
    {
        DamageKindDirect => "direct",
        DamageKindMelee => "melee",
        DamageKindExplosion => "explosion",
        DamageKindDot => "dot",
        DamageKindCollision => "collision",
        DamageKindFall => "fall",
        _ => throw new RuntimeContractException("player-state-kind", "Not a named damage kind index: " + index)
    };

    /// <summary>`low_health`: the player the game's own low-health event named.</summary>
    public static JsonElement LowHealthPayload(EntityReference player) => Payload(("player", Entity(player)));

    /// <summary>`infection_changed`: the player, the value the receiver holds now and the signed change. Both
    /// numbers are the receiver's own unit; the game publishes no unit of its own and this row claims none.</summary>
    public static JsonElement InfectionChangedPayload(EntityReference player, double value, double delta)
        => Payload(("player", Entity(player)), ("value", RuntimeJson.From(value)), ("delta", RuntimeJson.From(delta)));

    // ---------------------------------------------------------------- value answers

    /// <summary>`forge.query.player.health`: the receiver's current and maximum health and their ratio. Every
    /// number is read from the same sample, so the ratio can never describe a different read than the two
    /// absolute values.</summary>
    public static JsonElement HealthAnswer(double value, double maximum)
        => RuntimeJson.From(new { value, maximum, fraction = maximum > 0 ? value / maximum : 0 });

    /// <summary>`forge.query.player.infection`: the receiver's infection value.</summary>
    public static JsonElement InfectionAnswer(double value) => RuntimeJson.From(new { value });

    /// <summary>`forge.query.player.downed`: the life's downed and alive flags, read from the same sample.</summary>
    public static JsonElement DownedAnswer(bool downed, bool alive) => RuntimeJson.From(new { value = downed, alive });

    /// <summary>`forge.query.player.position`: the life's position in metres.</summary>
    public static JsonElement PositionAnswer(double[] position) => RuntimeJson.From(new { position });

    /// <summary>`forge.query.player.wielded_gear`: the equipment entity of the item the player is holding, or
    /// null when the player holds no item this process can name.</summary>
    public static JsonElement WieldedGearAnswer(EntityReference? equipment)
        => RuntimeJson.From(new { equipment });

    /// <summary>`forge.query.player.ammo`: the wielded weapon's clip and its reserve, in rounds.</summary>
    public static JsonElement AmmoAnswer(int clip, int clipMaximum, int reserve)
        => RuntimeJson.From(new { clip, clip_max = clipMaximum, reserve });

    /// <summary>`forge.query.player.carried_item`: the expedition item this player carries, or null when nobody
    /// in the backpack holds one. A read that happened and found nothing is an answer, not a missing port.</summary>
    public static JsonElement CarriedItemAnswer(EntityReference? item) => RuntimeJson.From(new { item });

    /// <summary>`forge.query.player.tool`: the held item the two ammunition numbers belong to, the class-ammunition
    /// pool (`GetClassAmmoInPackAbs` / `GetClassAmmoMaxCap` — the tool energy the game itself spends), and the
    /// backpack's own consumable stacks: `count` is how many pocket items it holds in total and `stacks` how many
    /// separate stacks they are grouped into. The item is nullable for the same reason `wielded_gear` is: a player
    /// holding nothing has no pool, which this row refuses rather than answers with a zero.</summary>
    public static JsonElement ToolAnswer(EntityReference? item, int ammo, int ammoMaximum, int count, int stacks)
        => RuntimeJson.From(new { item, ammo, ammo_max = ammoMaximum, count, stacks });

    // ---------------------------------------------------------------- value rows

    /// <summary>The eight read-only rows this provider declares. Each one takes the player it is about and
    /// answers with values only: no `result` row, no handle, no write.
    ///
    /// Their capability kind is `state` — a read-only row answers a state, and it is the kind the framework
    /// evaluates for an `observe` binding — and their execution tier is `query`, the tier a read-only step runs
    /// in. The id says `query` because the plan node an author places is a query step; the two names are not the
    /// same field and neither is derived from the other.</summary>
    public static IReadOnlyList<object> ValueRows() => Array.AsReadOnly(new[]
    {
        ValueRow(HealthValueCapability, "玩家生命值", "读一个玩家当前与最大的生命值。",
            "Reads one player's current and maximum health.",
            new object[]
            {
                new { id = "value", type = "number", unit = "hp" },
                new { id = "maximum", type = "number", unit = "hp" },
                new { id = "fraction", type = "number" }
            }),
        ValueRow(InfectionValueCapability, "玩家感染值", "读一个玩家当前的感染值。",
            "Reads one player's current infection value.",
            new object[] { new { id = "value", type = "number" } }),
        ValueRow(DownedValueCapability, "玩家是否倒地", "读一个玩家是否倒地、是否还活着。",
            "Reads whether one player is downed and whether the life is still alive.",
            new object[] { new { id = "value", type = "boolean" }, new { id = "alive", type = "boolean" } }),
        ValueRow(PositionValueCapability, "玩家位置", "读一个玩家当前的位置。",
            "Reads one player's current position.",
            new object[] { new { id = "position", type = "vector3", unit = "m" } }),
        ValueRow(WieldedGearValueCapability, "玩家手持装备", "读一个玩家当前拿在手上的装备。",
            "Reads the equipment one player is currently holding.",
            new object[] { new { id = "equipment", type = "entity", nullable = true, entityKinds = new[] { "gtfo.equipment" } } }),
        ValueRow(AmmoValueCapability, "玩家弹药", "读一个玩家手上武器的弹匣与备用弹药。",
            "Reads the clip and reserve ammunition of the weapon one player is holding.",
            new object[]
            {
                new { id = "clip", type = "integer" },
                new { id = "clip_max", type = "integer" },
                new { id = "reserve", type = "integer" }
            }),
        ValueRow(CarriedItemValueCapability, "玩家背负的大件物品", "读一个玩家背包里背着的大件物品。",
            "Reads the expedition item one player carries in the backpack.",
            new object[] { new { id = "item", type = "entity", nullable = true } }),
        ValueRow(ToolValueCapability, "玩家工具能源与消耗品数量",
            "读一个玩家手上物品的职业弹药池（工具能源）和背包里的消耗品堆叠。",
            "Reads the class-ammunition pool of the item one player is holding, and the consumable stacks in the backpack.",
            new object[]
            {
                new { id = "item", type = "entity", nullable = true },
                new { id = "ammo", type = "integer" },
                new { id = "ammo_max", type = "integer" },
                new { id = "count", type = "integer" },
                new { id = "stacks", type = "integer" }
            })
    });

    private static object ValueRow(string id, string label, string description, string descriptionEn, object[] outputs) => new
    {
        id,
        owner = ProviderId,
        kind = "state",
        label,
        version = "1.0.0",
        parameters = new { description, descriptionEn },
        graph = new
        {
            domains = new[] { "map", "player", "logic" },
            execution = "query",
            inputs = new object[] { new { id = "player", type = "entity", entityKinds = new[] { "gtfo.player" } } },
            outputs,
            parameters = Array.Empty<object>()
        }
    };

    /// <summary>The eight query bindings, one per value row, paired with <see cref="ValueRows"/> by position.</summary>
    public static IReadOnlyList<object> ValueBindings() => Array.AsReadOnly(new object[]
    {
        ValueBinding(HealthValueCapability, HealthValueHandler),
        ValueBinding(InfectionValueCapability, InfectionValueHandler),
        ValueBinding(DownedValueCapability, DownedValueHandler),
        ValueBinding(PositionValueCapability, PositionValueHandler),
        ValueBinding(WieldedGearValueCapability, WieldedGearValueHandler),
        ValueBinding(AmmoValueCapability, AmmoValueHandler),
        ValueBinding(CarriedItemValueCapability, CarriedItemValueHandler),
        ValueBinding(ToolValueCapability, ToolValueHandler)
    });

    /// <summary>The eight registration support rows, paired with <see cref="ValueBindings"/> by position. A value
    /// read owns no object and writes nothing, so no row carries a permission.</summary>
    public static IReadOnlyList<BindingSupport> ValueSupport() => Array.AsReadOnly(new[]
    {
        ValueSupport(HealthValueCapability), ValueSupport(InfectionValueCapability), ValueSupport(DownedValueCapability),
        ValueSupport(PositionValueCapability), ValueSupport(WieldedGearValueCapability), ValueSupport(AmmoValueCapability),
        ValueSupport(CarriedItemValueCapability), ValueSupport(ToolValueCapability)
    });

    /// <summary>The one shape of each value handler: the player it reads and the ports it answers with. Declared
    /// here so the native read layer cannot describe a second layout; the registration composes this dictionary
    /// into its own shape table.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> ValueShapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [HealthValueHandler] = new HandlerShape().Inputs("player").Outputs("value", "maximum", "fraction"),
        [InfectionValueHandler] = new HandlerShape().Inputs("player").Outputs("value"),
        [DownedValueHandler] = new HandlerShape().Inputs("player").Outputs("value", "alive"),
        [PositionValueHandler] = new HandlerShape().Inputs("player").Outputs("position"),
        [WieldedGearValueHandler] = new HandlerShape().Inputs("player").Outputs("equipment"),
        [AmmoValueHandler] = new HandlerShape().Inputs("player").Outputs("clip", "clip_max", "reserve"),
        [CarriedItemValueHandler] = new HandlerShape().Inputs("player").Outputs("item"),
        [ToolValueHandler] = new HandlerShape().Inputs("player").Outputs("item", "ammo", "ammo_max", "count", "stacks")
    };

    private static object ValueBinding(string capabilityId, string handler) => new
    {
        id = Binding(capabilityId),
        capabilityId,
        providerId = ProviderId,
        handler,
        role = "observe",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    private static BindingSupport ValueSupport(string capabilityId)
        => new(Binding(capabilityId), "implementation-only", Array.Empty<string>());

    // ---------------------------------------------------------------- payload plumbing

    private static JsonElement Entity(EntityReference reference) => RuntimeJson.From(reference);

    private static JsonElement Null { get; } = RuntimeJson.From((object?)null);

    /// <summary>One payload object, port by port, in the order the caller wrote them. A port whose value is
    /// <c>null</c> is left out of the object entirely, which is the framework's own "not observable" and never a
    /// claim that the port was written with nothing; a port the native side genuinely answered with nobody is
    /// passed as <see cref="Null"/> and lands in the payload as JSON null. The distinction is the whole reason
    /// these builders take a nullable element instead of a sentinel.</summary>
    private static JsonElement Payload(params (string Id, JsonElement? Value)[] ports)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in ports) if (value is { } present) payload[id] = present;
        return RuntimeJson.From(payload);
    }
}
