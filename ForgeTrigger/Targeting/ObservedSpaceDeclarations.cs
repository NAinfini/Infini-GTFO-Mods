using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

/// <summary>The space and ranking selectors: the rows that read the world to select from the explicit candidates a
/// plan handed them — the volume query, the two distance rankings, the chain, the weighted draw, the relation
/// filter and the partition. Every one of them takes its candidates from its own catalog port; none of them
/// enumerates a world of its own, so an absent or empty candidate set is never answered as if it were the query
/// world.
///
/// The declaration is this file's; the selection itself is <see cref="ObservedSpaceNodes"/>, which owns the
/// algorithms and the public selection entry points the handlers below call — one read, one ranking, one refusal
/// path.</summary>
public static class ObservedSpaceDeclarations
{
    /// <summary>The observation fields the partition row's `field` parameter can name, in the catalog's own order.
    /// Four of them are label lists the snapshot already carries — a `tag` or `receiver` key matches any one of a
    /// target's own labels, which is what "group by tag" means — and `identity` is the reference's own id, so a key
    /// can also name one exact target. `kind`, `faction` and `life_state` are single-valued in the snapshot and
    /// answer as one-label lists, so every field is asked the same question: does this target carry the key?</summary>
    private static readonly string[] PartitionFields = { "identity", "kind", "faction", "life_state", "tag", "receiver" };

    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Node("forge.selector.target.shape_overlap", "query", "范围内的玩家 / 敌人", "用球、锥、盒、柱或胶囊圈一片范围找东西。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("candidates"), ObservedDeclaration.Vector("center", "m"),
                ObservedDeclaration.Number("radius", "m"), ObservedDeclaration.Number("angle", "deg"),
                ObservedDeclaration.Number("height", "m"), ObservedDeclaration.Vector("extents", "m")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")),
            ObservedDeclaration.Parameters(ObservedDeclaration.StructuralEnum("shape", "query_shape"), ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("candidates", "center", "radius", "angle", "height", "extents").Outputs("targets")
                .Parameters("shape", "empty"), ShapeOverlap),
        ObservedDeclaration.Node("forge.selector.target.nearest", "query", "按距离取最近若干", "按距离取最近的几个。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Entity("anchor"), ObservedDeclaration.Many("candidates"), ObservedDeclaration.Integer("max_targets")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("anchor", "candidates", "max_targets").Outputs("targets").Parameters("empty"), NearestHandler),
        ObservedDeclaration.Node("forge.selector.target.farthest", "query", "按距离取最远若干", "按距离取最远的几个。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Entity("anchor"), ObservedDeclaration.Many("candidates"), ObservedDeclaration.Integer("max_targets")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("anchor", "candidates", "max_targets").Outputs("targets").Parameters("empty"), FarthestHandler),
        ObservedDeclaration.Node("forge.selector.target.chain", "query", "有次数与去重规则的连锁目标", "像连锁闪电一样一跳一跳往下选。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Entity("origin"), ObservedDeclaration.Many("candidates"), ObservedDeclaration.Integer("hops"),
                ObservedDeclaration.Number("radius", "m")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("origin", "candidates", "hops", "radius").Outputs("targets").Parameters("empty"), ChainHandler),
        ObservedDeclaration.Node("forge.selector.target.weighted", "query", "按显式权重选目标", "按你给的权重挑。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("candidates"), ObservedDeclaration.ManyNumber("weights"),
                ObservedDeclaration.Integer("max_targets"), ObservedDeclaration.Integer("seed")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("candidates", "weights", "max_targets", "seed").Outputs("targets").Parameters("empty"), WeightedHandler),
        ObservedDeclaration.Node("forge.selector.target.filter", "query", "某区域里的玩家 / 敌人", "用一套目标规则筛掉不要的。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Entity("anchor"), ObservedDeclaration.Many("candidates")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")),
            ObservedDeclaration.Parameters(ObservedDeclaration.StructuralEnum("relation", "recipient_relation"), ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("anchor", "candidates").Outputs("targets").Parameters("relation", "empty"), FilterHandler),
        ObservedDeclaration.Node("forge.selector.target.partition", "query", "按队伍、部位或标签分组", "按一个键把目标分成两堆。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("candidates"), ObservedDeclaration.Text("key")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("matched"), ObservedDeclaration.Many("rest")),
            ObservedDeclaration.Parameters(ObservedDeclaration.StructuralEnumValues("field", PartitionFields), ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("candidates", "key").Outputs("matched", "rest").Parameters("field", "empty"), PartitionHandler),
        ObservedDeclaration.Node("forge.selector.target.zone_members", "query", "某区域里的实体", "从候选里挑出位于指定区域内的实体。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("candidates"),
                ObservedDeclaration.Resource("zone", RuntimeZones.ResourceKind, "forge.resource.zone")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("candidates", "zone").Outputs("targets").Parameters("empty"), ZoneMembersHandler));

    /// <summary>The zone filter keeps the candidates whose own zone is the one the plan named, and it reads that
    /// zone through the same session as the candidates: the plan hands a zone resource, the row asks the kernel for
    /// the zone entity that resource names, and every candidate's zone comes from the provider that owns its kind.
    /// A kind no provider can place, an entity the level has not placed, and a zone that is not this level's are all
    /// refusals — a filter that answered an unknown zone as "outside" would silently drop the target it could not
    /// read. The zone is compared by identity, which is the level's own coordinates, so two references to one zone
    /// answer the same.</summary>
    private static JsonElement ZoneMembersHandler(EvaluationContext context)
    {
        // The row's own port is read first, so a plan naming a zone this level does not have is refused before any
        // candidate is read, and the entity the answer carries is the kernel's own reference rather than one this
        // half would have to spell the world epoch of.
        var zone = ZoneReference(context);
        return ObservedEvaluation.Targets(context,
            ObservedSpaceNodes.ZoneMembers(context.Query, ObservedEvaluation.Candidates(context, "candidates"), zone));
    }

    /// <summary>The zone the row's own resource port names, as the kernel's own entity for it. The port carries
    /// what the plan compiled — a reference written in the document's `{id, revision}` form — and a value an
    /// upstream step produced, which is the frame's own two-field form; the id is the zone's coordinates either
    /// way, so both spellings are read here and the kernel's one zone read decides whether the level has it.</summary>
    private static EntityReference ZoneReference(EvaluationContext context)
    {
        var value = ObservedEvaluation.Required(context, "zone");
        string id;
        if (value.TryGetProperty("resourceKind", out var kind))
        {
            var resource = RuntimeJson.ResourceRefOf(value);
            if (resource.ResourceKind != RuntimeZones.ResourceKind)
                throw new RuntimeContractException(RuntimeAbiCodes.ResourceKind, resource.ResourceKind);
            id = resource.ResourceId;
        }
        else if (!value.TryGetProperty("id", out var compiled) || compiled.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("missing-field", "zone");
        else id = compiled.GetString()!;
        // The zone is read in the world this step runs in: a reference of another world is the kernel's own
        // `stale-world` refusal, not an id this half may re-stamp.
        var reference = new EntityReference(id, context.Query.WorldEpoch, 0);
        if (!context.Query.TryZone(reference, out var zone, out var code))
            throw new RuntimeContractException(code, "The named zone could not be read: " + code);
        return zone!;
    }

    // ---- handlers ---------------------------------------------------------------------------------------------

    /// <summary>The volume selects from the row's own `candidates` port — this row never enumerates a world, so a
    /// candidate port that is not there is refused instead of passing as "everything nearby". Only the measurements
    /// the chosen shape defines are used: a sphere and a cylinder are `radius` and `height`, a capsule the same two,
    /// and a box is the plan's own `extents`, which are half sizes along the world axes. `angle` is the cone's and
    /// no shape with a cone has an implementation, so it is never read rather than approximated.</summary>
    private static JsonElement ShapeOverlap(EvaluationContext context)
    {
        var shape = ObservedEvaluation.ParameterText(context, "shape") switch
        {
            "sphere" => ObservedVolumeShape.Sphere,
            "cylinder" => ObservedVolumeShape.Cylinder,
            "capsule" => ObservedVolumeShape.Capsule,
            "box" => ObservedVolumeShape.Box,
            var other => throw new RuntimeContractException("spatial-shape", "Unsupported query shape: " + other)
        };
        var candidates = ObservedEvaluation.Candidates(context, "candidates");
        var radius = ObservedEvaluation.Required(context, "radius").GetDouble();
        var height = ObservedEvaluation.Required(context, "height").GetDouble();
        if (shape == ObservedVolumeShape.Box)
        {
            var extents = Vector(context, "extents");
            radius = extents[0]; height = extents[1] * 2d;
        }
        return ObservedEvaluation.Targets(context, ObservedSpatialNodes.Overlap(context.Query, candidates,
            Vector(context, "center"), shape, radius, height));
    }

    /// <summary>The anchor entity is resolved through the same session read as the candidates, so the ranking never
    /// mixes a stale position with a current one.</summary>
    private static JsonElement NearestHandler(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ObservedSpaceNodes.Nearest(context.Query,
            ObservedEvaluation.Candidates(context, "candidates"), AnchorPosition(context),
            ObservedEvaluation.RequestedCount(ObservedEvaluation.Required(context, "max_targets"), 1, ReferenceCollections.MaximumSelection)));
    private static JsonElement FarthestHandler(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ObservedSpaceNodes.Farthest(context.Query,
            ObservedEvaluation.Candidates(context, "candidates"), AnchorPosition(context),
            ObservedEvaluation.RequestedCount(ObservedEvaluation.Required(context, "max_targets"), 1, ReferenceCollections.MaximumSelection)));

    private static JsonElement WeightedHandler(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ObservedSpaceNodes.Weighted(ObservedEvaluation.Candidates(context, "candidates"),
            Numbers(context, "weights"),
            ObservedEvaluation.RequestedCount(ObservedEvaluation.Required(context, "max_targets"), 1, WeightedSampling.MaximumSelections),
            ObservedEvaluation.Required(context, "seed").GetInt64()));

    private static JsonElement ChainHandler(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ObservedSpaceNodes.Chain(context.Query,
            ObservedEvaluation.Candidates(context, "candidates"), ObservedEvaluation.Entity(context, "origin"),
            ObservedEvaluation.RequestedCount(ObservedEvaluation.Required(context, "hops"), 1, 64),
            ObservedEvaluation.Required(context, "radius").GetDouble()));

    /// <summary>The relation is measured against the row's explicit `anchor` input, which is what a deployable
    /// wires its `owner` into; nothing here reads a role from the event.</summary>
    private static JsonElement FilterHandler(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ObservedSpaceNodes.Filter(context.Query,
            ObservedEvaluation.Candidates(context, "candidates"), ObservedEvaluation.Entity(context, "anchor"),
            context.Relations, RecipientFilterRequest.Read(ObservedEvaluation.ParameterText(context, "relation"))));

    /// <summary>This row has two outputs, so `empty` is applied to the pair once: `fail` refuses when either side
    /// would be empty, and `skip` answers both sides unchanged.</summary>
    private static JsonElement PartitionHandler(EvaluationContext context)
    {
        var partition = ObservedSpaceNodes.PartitionOn(context.Query, ObservedEvaluation.Candidates(context, "candidates"),
            ObservedEvaluation.ParameterText(context, "field"), ObservedEvaluation.Required(context, "key").GetString()!);
        var policy = ObservedEvaluation.EmptyPolicy(context.Parameters);
        if (policy == EmptySelectionPolicy.Fail && (partition.Matched.Count == 0 || partition.Rest.Count == 0))
            throw new RuntimeContractException("pure-empty-selection", "Empty selection rejected by its empty policy.");
        return RuntimeJson.From(new
        {
            matched = ReferenceCollections.ApplyEmptyPolicy(partition.Matched, policy),
            rest = ReferenceCollections.ApplyEmptyPolicy(partition.Rest, policy)
        });
    }

    /// <summary>The anchor entity's current position, read through the session. An anchor the kernel could not
    /// observe is the session's own refusal, never a zero position.</summary>
    private static double[] AnchorPosition(EvaluationContext context)
    {
        if (!context.Query.TrySnapshot(ObservedEvaluation.Entity(context, "anchor"), out var snapshot, out var code))
            throw new RuntimeContractException(code, "Anchor observation failed: " + code);
        return snapshot!.Position.ToArray();
    }

    private static double[] Vector(EvaluationContext context, string port)
        => ObservedEvaluation.Required(context, port).EnumerateArray().Select(value => value.GetDouble()).ToArray();
    private static IReadOnlyList<double> Numbers(EvaluationContext context, string port)
        => Array.AsReadOnly(ObservedEvaluation.Required(context, port).EnumerateArray().Select(value => value.GetDouble()).ToArray());
}
