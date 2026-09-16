using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The player-life trigger bindings this provider implements: the six catalog rows that describe what
/// happens to one player's life — downed, revive started, revive cancelled, revived, died and teleported — plus
/// `respawned`, whose fact is published by the replication spawn path rather than by a life transition. Every
/// one of them is an observation of native state, so each is registered with role `observe`, declares no
/// handler and no result row, and publishes the fact instead of acting on it.
///
/// The capability ids, their output ports and their `host` execution tier belong to the runtime's
/// `TriggerContracts` provider, which declares the catalog rows field for field; this provider declares only
/// the rows a plan binds to, in <see cref="Bindings"/>, and the one fact each of them carries. The row for a
/// capability this file binds is never restated here, so the shape and the implementation cannot drift into
/// two descriptions of one trigger.
///
/// The player entity kind these facts carry is the one identity the Map provider already owns (`gtfo.player`,
/// declared and resolved by `Native/PlayerIdentityModule`); nothing here mints an entity of its own.</summary>
public static class PlayerLifeContract
{
    /// <summary>The Map package's provider id. It is written here rather than read from
    /// <see cref="ModuleDefinition"/> because this file is also compiled by the focused test project, which must
    /// not drag the whole provider declaration in to build one binding id. The identity table's own contract test
    /// asserts the two agree, so the literal cannot drift unnoticed.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    public const string DownedCapability = "forge.trigger.player.downed";
    public const string ReviveStartedCapability = "forge.trigger.player.revive_started";
    public const string ReviveCancelledCapability = "forge.trigger.player.revive_cancelled";
    public const string RevivedCapability = "forge.trigger.player.revived";
    public const string DiedCapability = "forge.trigger.player.died";
    public const string TeleportedCapability = "forge.trigger.player.teleported";
    public const string RespawnedCapability = "forge.trigger.player.respawned";

    /// <summary>Every row this provider implements, in the order the registration declares them: the wire name
    /// of the fact and the capability it is the observation of. One fact has exactly one capability, and a
    /// native hook names the fact rather than an index into this list.</summary>
    internal static readonly IReadOnlyList<(string Fact, string Capability)> Facts = Array.AsReadOnly(new[]
    {
        ("downed", DownedCapability),
        ("revive_started", ReviveStartedCapability),
        ("revive_cancelled", ReviveCancelledCapability),
        ("revived", RevivedCapability),
        ("died", DiedCapability),
        ("teleported", TeleportedCapability),
        ("respawned", RespawnedCapability)
    });

    /// <summary>The capability one fact is published through. A fact name outside this set is a programming
    /// error in this package, not a native condition, so it is refused by name instead of publishing nothing.</summary>
    internal static string CapabilityOf(string fact)
    {
        foreach (var (name, capability) in Facts) if (string.Equals(name, fact, StringComparison.Ordinal)) return capability;
        throw new RuntimeContractException("player-life-fact", "Unknown player-life fact: " + fact);
    }

    /// <summary>The binding a native life transition publishes through: the capability's own suffix under the
    /// Map provider, so either side names the counterpart of a row.</summary>
    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.trigger.".Length..];

    /// <summary>The binding of one fact, for the hooks and the module that publish it.</summary>
    internal static string BindingOf(string fact) => Binding(CapabilityOf(fact));

    /// <summary>The handler name that travels with a binding row. The observation is native and has no handler
    /// the runtime dispatches to, so the name is the row's own identity text: provider, domain and fact.</summary>
    internal static string HandlerOf(string fact) => "gtfo.map.player." + fact;

    /// <summary>One binding row: this provider's own id under the capability's own suffix, the canonical
    /// capability it observes, and the handler text that names the observation. It requires no other binding, so
    /// the closure of a plan that pins it is the row itself.</summary>
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

    /// <summary>Every binding row in <see cref="Facts"/> order, for the one registration call that adds them to
    /// the Map provider definition.</summary>
    public static IReadOnlyList<object> Rows()
    {
        var rows = new object[Facts.Count];
        for (int i = 0; i < Facts.Count; i++) rows[i] = Row(Facts[i].Fact);
        return Array.AsReadOnly(rows);
    }

    /// <summary>The registration support of every row: a life transition reads the life the provider already
    /// tracks and writes nothing, so no row declares a permission and no row depends on another binding. The
    /// support carries the binding id alone — which is what the runtime matches a declared binding against.</summary>
    public static IReadOnlyList<BindingSupport> Support()
    {
        var support = new BindingSupport[Facts.Count];
        for (int i = 0; i < Facts.Count; i++) support[i] = new BindingSupport(BindingOf(Facts[i].Fact), "implementation-only", Array.Empty<string>());
        return Array.AsReadOnly(support);
    }

    /// <summary>The binding id of every row, in declaration order. A registration built from this file supplies
    /// one native publisher per id.</summary>
    public static IReadOnlyList<string> BindingIds()
    {
        var ids = new string[Facts.Count];
        for (int i = 0; i < Facts.Count; i++) ids[i] = BindingOf(Facts[i].Fact);
        return Array.AsReadOnly(ids);
    }

    /// <summary>The `revive_cancelled` reason vocabulary, as the wire string the port carries. The game reports
    /// no reason of its own: the abort callback the revive interaction's own timer raises is the one native
    /// entry, so a cancellation is either that abort or the reviver leaving the interaction. No other reason is
    /// invented, and the port is required, so a cancellation always carries one of these two.</summary>
    public const string AbortedReason = "aborted", LeftReason = "left";

    // ------------------------------------------------------------------ payloads
    //
    // One builder per fact. Each writes exactly the ports the catalog declares for its row, in the catalog's
    // order, and nothing else: a port the native side could not read has no builder argument and is left out of
    // the object, while a nullable port the game genuinely answered with nobody is passed as null and is
    // written as JSON null. The distinction is the whole reason these are built here rather than at each hook:
    // "not observable" and "observably nobody" are different facts and a reader must be able to tell them apart.
    //
    // They live beside the row declarations because the ports and the payload are two halves of one shape, and
    // because the package's own tests can then read the payload the kernel would dispatch without driving a
    // whole plan.

    /// <summary>`downed`: the player, and the nullable source. The game reports no downs source of its own — the
    /// attacker list is a record of everyone who ever hit the player, not a cause — so a downed fact always
    /// carries null there.</summary>
    public static JsonElement DownedPayload(EntityReference player)
        => Payload(("player", Entity(player)), ("source", Null));

    /// <summary>`revive_started`: the downed player and the rescuer who is reviving them. The rescuer port is
    /// required, so a rescue whose actor cannot be named has no payload at all and is not published.</summary>
    public static JsonElement ReviveStartedPayload(EntityReference player, EntityReference rescuer)
        => Payload(("player", Entity(player)), ("rescuer", Entity(rescuer)));

    /// <summary>`revive_cancelled`: the downed player, the nullable rescuer whose rescue ended, and the reason
    /// word from this file's own vocabulary.</summary>
    public static JsonElement ReviveCancelledPayload(EntityReference player, EntityReference? rescuer, string reason)
        => Payload(("player", Entity(player)), ("rescuer", rescuer is { } named ? Entity(named) : Null),
            ("reason", RuntimeJson.From(reason)));

    /// <summary>`revived`: the player who came back and the nullable actor of the revive. A revive can run with
    /// no interaction actor this process can name, so the rescuer is allowed to be nobody.</summary>
    public static JsonElement RevivedPayload(EntityReference player, EntityReference? rescuer)
        => Payload(("player", Entity(player)), ("rescuer", rescuer is { } named ? Entity(named) : Null));

    /// <summary>`died`: the player who died and the nullable source. No native read names the kill that ended a
    /// life, so a death fact always carries null there rather than a guess.</summary>
    public static JsonElement DiedPayload(EntityReference player)
        => Payload(("player", Entity(player)), ("source", Null));

    /// <summary>`teleported`: the player and both ends of the move, in metres. Both positions are required, so a
    /// move whose origin or destination could not be read is not published.</summary>
    public static JsonElement TeleportedPayload(EntityReference player, double[] from, double[] to)
        => Payload(("player", Entity(player)), ("from", RuntimeJson.From(from)), ("to", RuntimeJson.From(to)));

    /// <summary>`respawned`: the player and the position the game spawned them at, in metres.</summary>
    public static JsonElement RespawnedPayload(EntityReference player, double[] position)
        => Payload(("player", Entity(player)), ("position", RuntimeJson.From(position)));

    private static JsonElement Entity(EntityReference reference) => RuntimeJson.From(reference);

    private static JsonElement Null { get; } = RuntimeJson.From((object?)null);

    /// <summary>One payload object, port by port, in the order the caller wrote them. The dictionary is ordinal
    /// keyed and the serializer's own ordering is what the kernel reads back by name, so the order here is the
    /// row's own and never a second, implicit one.</summary>
    private static JsonElement Payload(params (string Id, JsonElement Value)[] ports)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in ports) payload[id] = value;
        return RuntimeJson.From(payload);
    }
}
