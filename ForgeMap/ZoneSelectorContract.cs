using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `forge.selector.target.zone` declaration: the capability's identity, the binding that implements
/// it, the port shape its handler is resolved against, and the evaluator that answers through the readers the
/// game-bound registration supplies. It sits in the game-independent Map assembly because the runtime module, the
/// manifest and the website read it there; the native half reads the level's zone table and none of that crosses
/// this boundary.
///
/// The row is the catalog's own: one `anchor` entity in, one nullable `zone` resource out, no parameters. It is
/// the half of `q-zone` that names a place: a zone is the level's own, identified by the three coordinates its
/// doors and terminals are addressed by, so the text this row answers with is the text every other reader of that
/// zone already spells.</summary>
public static class ZoneSelectorContract
{
    public const string CapabilityId = "forge.selector.target.zone";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.target.zone";
    public const string HandlerName = "gtfo.map.zone";
    /// <summary>The entity kind a zone is read as, and the namespace its resource ids are written in. It is the
    /// framework's own spelling, because the selector that filters by zone compares the reference this row answers
    /// with the one a provider answers for an entity of its own kind — two packages, one text.</summary>
    public const string EntityKind = RuntimeZones.EntityKind;
    /// <summary>The one shape of this handler: the entity it is asked about, and the zone resource it answers.</summary>
    public static readonly HandlerShape Shape = new HandlerShape().Inputs("anchor").Outputs("zone");

    /// <summary>The capability row a registration declares, spelled exactly as the authoring catalog row
    /// `forge.selector.target.zone`. The output is nullable because an entity standing in no zone of this level is
    /// a real answer; the handler answers it by refusing, never by naming a zone the level does not have.</summary>
    public const string CapabilityRowJson = """
    {
      "id": "forge.selector.target.zone",
      "owner": "forge.module.gtfo.map",
      "kind": "selector",
      "label": "选择关卡 Zone",
      "version": "1.0.0",
      "parameters": { "description": "选中所在的关卡 Zone。" },
      "graph": {
        "domains": ["map", "room", "enemy", "weapon", "tool", "consumable", "player", "logic"],
        "execution": "query",
        "inputs": [
          { "id": "anchor", "type": "entity" }
        ],
        "outputs": [
          { "id": "zone", "type": "resource", "resourceKind": "zone", "schema": "forge.resource.zone", "nullable": true }
        ],
        "parameters": []
      }
    }
    """;

    /// <summary>The binding row the same registration declares: an on-demand `query` binding, which is the
    /// `observe` role in this runtime.</summary>
    public const string BindingRowJson = """
    {
      "id": "forge.module.gtfo.map.binding.target.zone",
      "capabilityId": "forge.selector.target.zone",
      "providerId": "forge.module.gtfo.map",
      "handler": "gtfo.map.zone",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;

    /// <summary>The refusal an anchor this provider does not track gets. It is the ownership rule every provider
    /// read follows: a kind is answered by the package that owns it, and this row owns the player kind.</summary>
    public const string AnchorKindCode = "zone-anchor-kind";

    /// <summary>The refusal a read that could not be made gets because the world holds no level zone table: the
    /// anchor may well stand in a zone, and this process cannot name a single one of them.</summary>
    public const string AnchorUnavailableCode = "zone-anchor-unavailable";

    /// <summary>The refusal a reference this provider is not holding as a current life gets. A plan may name a
    /// player entity this session no longer tracks, and where that entity stands is a question this provider
    /// cannot answer.</summary>
    public const string AnchorUntrackedCode = "zone-anchor-untracked";

    /// <summary>The refusal a life whose own course node cannot be read gets. The course node is the one thing
    /// that places a player, so a life without a readable one has an unknown placement and not a known absence
    /// from every zone.</summary>
    public const string AnchorNodeMissingCode = "zone-anchor-node-missing";

    /// <summary>The refusal a tracked player whose own placement names no zone of this level gets, reported by
    /// name rather than answered as a zone the level does not have. It is a refusal and not a null answer: this
    /// row is asked for the zone of a player it holds, and a player standing in no zone of the level is not an
    /// answer the `zone` resource kind can carry.</summary>
    public const string AnchorUnplacedCode = "zone-anchor-unplaced";

    /// <summary>The one read this row needs from the world: which zone an anchor of this provider's own kind
    /// stands in, as the level's own zone reference. Null is the answer that the anchor's own placement names no
    /// zone of this level; a read that could not be made is refused with one of the codes above and is never
    /// collapsed into that answer. This assembly holds no game type, so the game-bound registration is the only
    /// one that can supply the read; there is no static slot here, because two registrations in one process would
    /// otherwise answer each other's reads.</summary>
    public sealed record ZoneReaders(Func<EntityReference, EntityReference?> ZoneOfAnchor);

    /// <summary>The one evaluator, keyed by handler name: what a registration adds to its evaluator table beside
    /// the player selector's. Every refusal is raised with the code this contract names, so a caller can tell an
    /// anchor of another kind from one the level has not placed.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(ZoneReaders readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        return new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [HandlerName] = context => Evaluate(context, readers)
        };
    }

    private static JsonElement Evaluate(EvaluationContext context, ZoneReaders readers)
    {
        var anchor = RuntimeJson.Entity(Input(context, "anchor"));
        if (RuntimeJson.KindOf(anchor.Id) != "gtfo.player")
            throw new RuntimeContractException(AnchorKindCode, "This provider reads the zone of a player, not of " + anchor.Id);
        var zone = readers.ZoneOfAnchor(anchor);
        if (zone == null || RuntimeJson.KindOf(zone.Id) != EntityKind)
            throw new RuntimeContractException(AnchorUnplacedCode, "The anchor stands in no zone of this level.");
        var reference = new ResourceRef(RuntimeZones.ResourceKind, zone.Id);
        return RuntimeJson.From(new { zone = reference.ToJson() });
    }

    /// <summary>One required input port of the resolved frame, refused by name when the plan left it out: an
    /// absent anchor answered as "no zone" would read exactly like a player the level had not placed.</summary>
    private static JsonElement Input(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new RuntimeContractException("missing-field", port);
}
