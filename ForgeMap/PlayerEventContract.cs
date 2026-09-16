using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The player's own input and acquisition events: using a supply, picking an item up, and the local
/// ping. All three are things the game reports about the player who did them, which is why they are one family —
/// each is one game event with a player and a small vocabulary, not a state of an item or of a life.
///
/// Two rules from the framework are what shape these rows:
/// <list type="bullet">
/// <item>The game announces a pickup and a supply use on the machine that performed it. The host is the only
/// publisher, so the row declares `execution: host` like every other trigger and the observation half publishes
/// only where it is authoritative; a client's local event is not a second publisher of a host fact. What the host
/// genuinely lacks (a client's own input) is recorded in `evidence/player-event-facts.json` and in the report as
/// the one open channel, rather than being faked with a derived host fact.</item>
/// <item>`supply_kind` and `pickup_kind` are `enum` ports whose members are the two vocabularies below, spelled
/// in the order the framework's shared enum-set table declares: an enum port's wire value is the member's index
/// and never its name, which is the same rule the player-state family's `damage_kind` publication follows. The
/// member name is what the provider validates and what the journal carries — a kind outside the list is refused
/// by name, never passed through — and the index is what the payload writes. Every other port is a member of a
/// closed vocabulary read from the game's own event, so no port is invented.</item>
/// </list>
///
/// The three rows are trigger-contract rows (the text below is what the integration batch moves into
/// `TriggerContracts`); this provider binds them to its own observation. `e-p-equip` needs nothing here: the
/// canonical `forge.trigger.input.equipped` / `unequipped` rows already exist and are already bound, so the node
/// list row lands on them unchanged.</summary>
public static class PlayerEventContract
{
    public const string ProviderId = "forge.module.gtfo.map";

    public const string SupplyUsedCapability = "forge.trigger.player.supply_used";
    public const string ItemPickedUpCapability = "forge.trigger.player.item_picked_up";
    public const string PingCapability = "forge.trigger.input.ping";

    /// <summary>Every observed row this family binds, in registration order.</summary>
    internal static readonly IReadOnlyList<(string Fact, string Capability)> Facts = Array.AsReadOnly(new[]
    {
        ("supply_used", SupplyUsedCapability),
        ("item_picked_up", ItemPickedUpCapability),
        ("ping", PingCapability)
    });

    /// <summary>The `supply_used.supply_kind` vocabulary, in the declaration order of the framework's
    /// `supply_kind` enum set: the game posts one event per supply it knows, these four are the supplies a player
    /// can apply, and a payload carries a member's position in this list. The member name is what the journal and
    /// the evidence carry.</summary>
    public static readonly IReadOnlyList<string> SupplyKinds = Array.AsReadOnly(new[]
    {
        "medikit", "ammokit", "disinfection", "tool_refill"
    });

    /// <summary>The `item_picked_up.pickup_kind` vocabulary, in the declaration order of the framework's
    /// `pickup_kind` enum set: every pickup event the game posts, with the game's own distinction between a
    /// commodity's three sizes kept because it is a distinction the game makes.</summary>
    public static readonly IReadOnlyList<string> PickupKinds = Array.AsReadOnly(new[]
    {
        "medikit", "ammokit", "tool_refill", "artifact", "commodity_small", "commodity_medium", "commodity_large",
        "consumable", "keycard"
    });

    internal static string CapabilityOf(string fact)
    {
        foreach (var (name, capability) in Facts) if (string.Equals(name, fact, StringComparison.Ordinal)) return capability;
        throw new RuntimeContractException("player-event-fact", "Unknown player-event fact: " + fact);
    }

    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.".Length..];

    internal static string BindingOf(string fact) => Binding(CapabilityOf(fact));

    internal static string HandlerOf(string fact) => "gtfo.map.player." + fact;

    /// <summary>One observe binding row, exactly as <see cref="PlayerStateContract.Row"/> builds them: this
    /// provider's own id, the canonical capability and a handler text that names the observation.</summary>
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

    public static IReadOnlyList<object> Rows()
    {
        var rows = new object[Facts.Count];
        for (int i = 0; i < Facts.Count; i++) rows[i] = Row(Facts[i].Fact);
        return Array.AsReadOnly(rows);
    }

    public static IReadOnlyList<BindingSupport> Support()
    {
        var support = new BindingSupport[Facts.Count];
        for (int i = 0; i < Facts.Count; i++) support[i] = new BindingSupport(BindingOf(Facts[i].Fact), "implementation-only", Array.Empty<string>());
        return Array.AsReadOnly(support);
    }

    /// <summary>One fact's kind word as the port carries it: the caller passes the game's own event member and
    /// gets the catalog's vocabulary member, or a refusal by name when the event is not one of this family's. A
    /// member outside the list is a programming error, never a silent pass-through of a game enum name.</summary>
    public static string SupplyKind(string member) => Member(SupplyKinds, member, "supply_kind");

    public static string PickupKind(string member) => Member(PickupKinds, member, "pickup_kind");

    private static string Member(IReadOnlyList<string> vocabulary, string member, string set)
    {
        foreach (var candidate in vocabulary) if (string.Equals(candidate, member, StringComparison.Ordinal)) return candidate;
        throw new RuntimeContractException("player-event-kind", "Not a " + set + " member: " + member);
    }

    // ---------------------------------------------------------------- payloads

    /// <summary>The position one member of a kind list holds, which is the value its enum port carries. A member
    /// outside the list is a programming error here and not a native condition: the caller resolved the name
    /// through <see cref="SupplyKind"/> or <see cref="PickupKind"/> first.</summary>
    private static int Position(IReadOnlyList<string> members, string kind, string set)
    {
        for (var position = 0; position < members.Count; position++)
            if (string.Equals(members[position], kind, StringComparison.Ordinal)) return position;
        throw new RuntimeContractException("player-event-kind", "Not a " + set + " member: " + kind);
    }

    /// <summary>`supply_used`: the player who applied a supply and which supply it was, on the shipped row's own
    /// `supply_kind` port. The port carries the member's index in the set the framework declares and never its
    /// name, which is why the list above is spelled in that set's order.</summary>
    public static JsonElement SupplyUsedPayload(EntityReference player, string kind)
        => Payload(("player", Entity(player)), ("supply_kind", RuntimeJson.From(Position(SupplyKinds, kind, "supply_kind"))));

    /// <summary>`item_picked_up`: the player who picked something up and which kind of item it was, on the shipped
    /// row's own `pickup_kind` port and with the index that set declares.</summary>
    public static JsonElement ItemPickedUpPayload(EntityReference player, string kind)
        => Payload(("player", Entity(player)), ("pickup_kind", RuntimeJson.From(Position(PickupKinds, kind, "pickup_kind"))));

    /// <summary>`ping`: the player who pinged and where, in metres. `target` is the object the ping named and is
    /// left out of the payload when this process cannot name one: a ping at empty world space is a real ping with
    /// no target, and "no target" must not read the same as "target unreadable".</summary>
    public static JsonElement PingPayload(EntityReference player, double[] position, EntityReference? target)
        => Payload(("player", Entity(player)), ("position", RuntimeJson.From(position)),
            ("target", target is { } named ? Entity(named) : null));

    private static JsonElement Entity(EntityReference reference) => RuntimeJson.From(reference);

    /// <summary>One payload object, port by port: a port whose value is <c>null</c> is left out entirely, so "not
    /// observable" never reads as "observed as nobody".</summary>
    private static JsonElement Payload(params (string Id, JsonElement? Value)[] ports)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in ports) if (value is { } present) payload[id] = present;
        return RuntimeJson.From(payload);
    }
}
