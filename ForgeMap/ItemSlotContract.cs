using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The two interaction rows this provider implements for a carried item and the host it is put into:
/// `forge.trigger.interaction.item_placed` and `forge.trigger.interaction.item_removed`. Both are the same event
/// from the two sides of one native pair — the game's own insertion entry and its own removal entry — so they
/// share this one contract, one address grammar and one publisher; only their capability id, their fact kind and
/// their binding id differ.
///
/// The capability list, its domain list and its output ports are the catalog entry for the same id, unchanged:
/// the catalog is the authority for the shape and this package only implements it. Every port here observes
/// native state, so each binding is registered with role `observe`, carries no handler and no result row, and is
/// published by the host only.
///
/// An item and a host are addressed as map objects and not as a second namespace: `MapObjectReference` is the
/// one address record this package writes, so the plan `attachments[]` `map-object` reference, the website and
/// both facts spell a subject the same way, and an item placed by a player can be mounted on exactly like the
/// zone it stands in. The item is a `CarryItemPickup_Core` the level placed, addressed by the course node its
/// spawn node names and its own serial number; a host is the insertion target itself, addressed by the course
/// node it stands in and the kind of socket it is (`eCarryItemInsertTargetType`, as the game spells that value).
/// A subject the native side cannot read those keys off has no address at all, and its port stays absent rather
/// than being published under a coordinate nothing was read for.</summary>
public static class ItemSlotContract
{
    public const string PlacedCapability = "forge.trigger.interaction.item_placed";
    public const string RemovedCapability = "forge.trigger.interaction.item_removed";

    /// <summary>The provider every row of this family belongs to, written out rather than read from
    /// `ModuleDefinition` so the contract and the publisher behind it compile on their own. The integration batch
    /// wires the rows into that definition and is what keeps the two spellings one: the rows below are the ones
    /// it appends there.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    /// <summary>The one entity namespace both subjects are addressed in; the same string the map-object half
    /// registers. Written out here for the same reason the provider id is.</summary>
    public const string EntityKind = "gtfo.map_object";

    /// <summary>The map-object category of an item the level placed: the carried object the two facts are about.
    /// It is addressed in the one `gtfo.map_object` namespace, so the category is a segment of the address and
    /// never a second entity kind.</summary>
    public const string ItemCategory = "item";

    /// <summary>The map-object category of the host an item is put into: the socket, crate or power-cell
    /// receptacle the game's own insertion target is.</summary>
    public const string HostCategory = "host";

    /// <summary>The catalog's domain list for both rows, unchanged.</summary>
    internal static readonly string[] Domains = { "map", "room", "tool", "consumable" };

    /// <summary>The binding a native insertion or removal reading publishes through: the capability's own suffix
    /// under the Map provider, so either side names the counterpart of a row.</summary>
    public static string Binding(string capabilityId)
        => ProviderId + ".binding." + capabilityId["forge.trigger.".Length..];

    /// <summary>The event id prefix of a fact: the map-object namespace, the fact kind and the world epoch. The
    /// address and the subject's own transition number complete it, so a repeated sync of one state produces
    /// neither a new transition nor a new id.</summary>
    public static string EventId(string fact, long worldEpoch, string address, long transition)
        => EntityKind + "." + fact + ":" + Number(worldEpoch) + ":" + address + ":" + Number(transition);

    /// <summary>One observe binding row: this provider's own id, the canonical capability it implements and the
    /// handler name the native half supplies. The handler is not declared as a shape because an observe binding
    /// carries no command: its ports are the capability's own, and the module publishes values into them.</summary>
    public static object BindingRow(string capabilityId, string fact)
        => new
        {
            id = Binding(capabilityId),
            capabilityId,
            providerId = ProviderId,
            handler = EntityKind + "." + fact,
            role = "observe",
            status = "implemented",
            dependencies = Array.Empty<string>(),
            requires = Array.Empty<string>()
        };

    /// <summary>Every row of this family in the order the module declares them, paired with the fact each one
    /// carries and the class name the native half supplies for it.</summary>
    public static readonly IReadOnlyList<(string Fact, string Capability)> Rows = Array.AsReadOnly(new[]
    {
        (ItemSlotFacts.PlacedFact, PlacedCapability),
        (ItemSlotFacts.RemovedFact, RemovedCapability)
    });

    /// <summary>The two rows the item-slot family binds, one binding each.</summary>
    public static IEnumerable<object> Bindings()
    {
        foreach (var (fact, capability) in Rows) yield return BindingRow(capability, fact);
    }

    /// <summary>The registration support of the family. Neither binding carries a permission: both read state a
    /// plan cannot own — a carried item and the socket it is put in — and neither names an object the plan would
    /// have to declare.</summary>
    public static IReadOnlyList<BindingSupport> Support()
        => Array.AsReadOnly(new[]
        {
            new BindingSupport(Binding(PlacedCapability), "implementation-only", Array.Empty<string>()),
            new BindingSupport(Binding(RemovedCapability), "implementation-only", Array.Empty<string>())
        });

    /// <summary>The two catalog rows this family registers, field for field as `catalog/capability-catalog.json`
    /// carries them, with the two ports this provider cannot always read marked optional. `TriggerContracts` does
    /// not declare them yet, so the integration batch appends exactly these objects there and deletes this
    /// method; until then the rows travel with the package that implements them, which is what keeps a declared
    /// port a port something publishes.
    ///
    /// Why the two flags: the kernel refuses a fact whose payload leaves out a port its capability declares as
    /// required, so a row that declared `item` and `actor` required would make every fact that could not read one
    /// of them unpublishable. A carried object the level never placed has no `item` address, and a callback that
    /// cannot name its actor has no `actor` — the catalog's own two rows say nothing about either case, and the
    /// catalog is changed by the website's own batch, not from here. Until it is, the local declaration is the
    /// honest one: a port this provider publishes when it reads it and leaves absent when it does not.</summary>
    public static IReadOnlyList<string> CapabilityRows() => Array.AsReadOnly(new[]
    {
        Row(PlacedCapability, "指定物品放入宿主", "指定物品被放进了某个宿主。", "Item placed",
            "Fires when the required item is placed into its host.", ItemPorts()),
        Row(RemovedCapability, "指定物品从宿主移出", "指定物品被从宿主里取走。", "Item removed",
            "Fires when the required item is taken out of its host.", ItemPorts())
    });

    /// <summary>The output ports both item rows carry: the execution exit, the host and the item as entities and
    /// the acting player. `host` is required — a fact is only published for a host that addresses — while `item`
    /// and `actor` are optional, because either can be unreadable in a delivery that is still a real fact.</summary>
    private static string ItemPorts()
        => Ports(("next", "execution", false), ("host", "entity", false),
            ("item", "entity", true), ("actor", "entity", true));

    /// <summary>One capability row of this family: the catalog's own id, kind, label, description, domains,
    /// execution tier and port list.</summary>
    private static string Row(string id, string labelZh, string descriptionZh, string labelEn, string descriptionEn,
        string ports)
        => "{\n"
            + "          \"id\": \"" + id + "\",\n"
            + "          \"owner\": \"forge.module.gtfo.map\",\n"
            + "          \"kind\": \"trigger\",\n"
            + "          \"label\": \"" + labelZh + "\",\n"
            + "          \"version\": \"1.0.0\",\n"
            + "          \"parameters\": {\n"
            + "            \"description\": \"" + descriptionZh + "\",\n"
            + "            \"summary\": \"" + descriptionZh + "\",\n"
            + "            \"summaryEn\": \"" + descriptionEn + "\",\n"
            + "            \"labelEn\": \"" + labelEn + "\",\n"
            + "            \"support\": \"authoring-contract-only\"\n"
            + "          },\n"
            + "          \"graph\": {\n"
            + "            \"domains\": [ \"map\", \"room\", \"tool\", \"consumable\" ],\n"
            + "            \"execution\": \"host\",\n"
            + "            \"inputs\": [],\n"
            + "            \"outputs\": [ " + ports + " ],\n"
            + "            \"parameters\": []\n"
            + "          }\n"
            + "        }";

    /// <summary>The catalog's port rows for one capability, in the catalog's own order. A port this provider
    /// publishes only when it can read it carries the optional flag, which is the same flag the catalog uses for
    /// the noise row's emitter.</summary>
    private static string Ports(params (string Id, string Type, bool Optional)[] ports)
    {
        var rows = new List<string>(ports.Length);
        foreach (var (id, type, optional) in ports)
            rows.Add("{ \"id\": \"" + id + "\", \"type\": \"" + type + "\"" + (optional ? ", \"optional\": true" : "") + " }");
        return string.Join(", ", rows);
    }

    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
