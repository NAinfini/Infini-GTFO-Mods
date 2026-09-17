using System;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Targeting;

/// <summary>The two entity-state reads: the kind one reference was observed as, and the zone it currently stands
/// in. Both are `state` rows in the catalog's `query` namespace, and both answer about an entity the plan named —
/// the kind comes from the provider that owns the kind, and the zone from the kernel's own zone resolution, so
/// neither row invents a second answer for a fact the world already publishes.
///
/// A zone the kernel cannot answer is refused with the kernel's own code. The row never answers "no zone": an
/// entity that stands nowhere and an entity whose placement could not be read would then be the same answer, and a
/// consumer could not tell a level without zones from an unreadable read.</summary>
public static class ObservedEntityStateDeclarations
{
    /// <summary>The declared rows in registration order, in the one table this family owns. The catalog declares
    /// both under kind `state` while their ids say `query`, so the kind is stated here rather than read off the id.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Node("forge.query.entity.kind", "query", "实体种类", "读取任意实体所属的本机种类。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Entity("entity")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Text("kind")), ObservedDeclaration.NoParameters,
            new HandlerShape().Inputs("entity").Outputs("kind"), KindHandler, new[] { "world" }, kind: "state"),
        ObservedDeclaration.Node("forge.query.entity.zone", "query", "实体所在区域", "读取实体当前所在的区域；读不到时明确失败。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Entity("entity")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Resource("zone", RuntimeZones.ResourceKind, "forge.resource.zone")),
            ObservedDeclaration.NoParameters,
            new HandlerShape().Inputs("entity").Outputs("zone"), ZoneHandler, new[] { "world" }, kind: "state"));

    /// <summary>One reference's observed kind, read through the step's own session. The kind is what the provider
    /// that owns the entity observed — never a kind spelled from the reference's id.</summary>
    public static string Kind(RuntimeQuerySession session, EntityReference entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return ObservedSpaceNodes.Observe(session, new[] { entity })[0].Kind;
    }

    /// <summary>The zone one reference currently stands in, as the kernel's own zone entity for it. The provider
    /// that owns the entity's kind answers the placement; a kind no provider can place and an entity the level has
    /// not placed are both refusals, carried here with the kernel's own code.</summary>
    public static EntityReference ZoneOf(RuntimeQuerySession session, EntityReference entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!session.TryZone(entity, out var zone, out var code))
            throw new RuntimeContractException(code, "The entity's zone could not be read: " + code);
        return zone!;
    }

    // ---- handlers ------------------------------------------------------------------------------------------

    private static JsonElement KindHandler(EvaluationContext context)
        => RuntimeJson.From(new { kind = Kind(context.Query, ObservedEvaluation.Entity(context, "entity")) });

    /// <summary>The zone the row answers is the resource the catalog's port carries: the zone's own reference,
    /// wrapped in the resource kind the port declares, so a consumer reads one shape whether the zone came from a
    /// step or from the document.</summary>
    private static JsonElement ZoneHandler(EvaluationContext context)
        => RuntimeJson.From(new
        {
            zone = new ResourceRef(RuntimeZones.ResourceKind,
                ZoneOf(context.Query, ObservedEvaluation.Entity(context, "entity")).Id).ToJson()
        });
}
