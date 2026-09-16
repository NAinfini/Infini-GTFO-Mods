using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

/// <summary>One partition row's answer: the candidates whose field equalled the plan's key, and the ones whose did
/// not. The two sides are disjoint and together are exactly the input, in the identity order every other collection
/// row answers with.</summary>
public sealed class ObservedPartition
{
    internal ObservedPartition(EntityReference[] matched, EntityReference[] rest)
    {
        Matched = Array.AsReadOnly(matched); Rest = Array.AsReadOnly(rest);
    }
    public IReadOnlyList<EntityReference> Matched { get; }
    public IReadOnlyList<EntityReference> Rest { get; }
}

/// <summary>The space and ranking selection over the references a step already holds, and the registration tables
/// of the rows that publish it. The declaration is <see cref="ObservedSpaceDeclarations"/>'s; this class owns the
/// selection itself — the volume query, the two distance rankings, the chain, the weighted draw, the relation
/// filter and the partition — so a handler, a consumer and a test all reach one implementation.
///
/// Every read goes through the caller's own <see cref="RuntimeQuerySession"/>: one budgeted call for the whole
/// explicit candidate set, and a set the kernel could not observe completely is refused rather than ranked, so a
/// selector can never answer with the best of the half it happened to see.</summary>
public static class ObservedSpaceNodes
{
    // ---- the family's registration tables ---------------------------------------------------------------------
    // The batch that declares these rows owns them; the module reads them here because this is the family's public
    // entry point, exactly as it reads the two tables of the query module.

    public static IReadOnlyList<ObservedNode> Nodes => ObservedSpaceDeclarations.Family.Nodes;
    public static JsonElement[] Capabilities => ObservedSpaceDeclarations.Family.Capabilities;
    public static JsonElement[] Bindings => ObservedSpaceDeclarations.Family.Bindings;
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators => ObservedSpaceDeclarations.Family.Evaluators;
    public static IReadOnlyDictionary<string, HandlerShape> Shapes => ObservedSpaceDeclarations.Family.Shapes;
    public static IReadOnlyList<BindingSupport> Support => ObservedSpaceDeclarations.Family.Support;

    /// <summary>An explicit candidate set's current positions, in the order the kernel answered them. One budgeted
    /// call, never a batch of single reads around the per-query or per-tick budget, and an incomplete answer — a
    /// spent budget, a stale identity, an observer that refused — is refused with the kernel's own code rather than
    /// answered as a shorter set.</summary>
    public static IReadOnlyList<RuntimeEntitySnapshot> Observe(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        // The read's own bound is decided before the read: one past it is refused rather than attempted and answered
        // with the kernel's normalized budget code, so a caller can tell "too many to ask about" from "the read ran
        // out of budget".
        if (candidates.Count > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Explicit candidate query exceeds the Runtime limit.");
        if (!session.TrySnapshots(candidates, out var result, out var code))
            throw new RuntimeContractException(code, "Entity observation failed: " + code);
        // A partial answer is the framework's own refusal, thrown with the framework's own code: a selector that
        // ranked the half it happened to observe would silently drop the targets it never saw.
        return result.RequireComplete();
    }

    /// <summary>The catalog's identity order over a set the kernel already observed.</summary>
    public static IReadOnlyList<EntityReference> IdentityOrder(IEnumerable<EntityReference> candidates)
        => Array.AsReadOnly(candidates.OrderBy(ReferenceCollections.OrderKey, StringComparer.Ordinal).ToArray());

    /// <summary>Minima and maxima of a distance ranking, in the order the plan asks for.</summary>
    public static IReadOnlyList<EntityReference> Nearest(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        IReadOnlyList<double> anchor, int count)
        => ObservedSpatialNodes.Nearest(session, candidates, anchor, count).Selected;
    public static IReadOnlyList<EntityReference> Farthest(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        IReadOnlyList<double> anchor, int count)
        => ObservedSpatialNodes.Farthest(session, candidates, anchor, count).Selected;

    /// <summary>One hop chain from the origin. The hop budget and radius are the existing chain's own bounds.</summary>
    public static IReadOnlyList<EntityReference> Chain(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference start, int hops, double radius)
        => ObservedSpatialNodes.Chain(session, candidates, start, hops, radius).Selected;

    /// <summary>The zone filter: the candidates the provider that owns each of their kinds places in the same zone
    /// the plan named. Zone membership is read from the same session as every other observation, one read per
    /// candidate, because a zone is a fact about an entity rather than a value any snapshot carries. The answer is
    /// in identity order, exactly like every other collection row of this family.
    ///
    /// An entity no provider can place is refused, never treated as "outside": a filter that dropped it would
    /// answer a shorter set than its own input, which is the one thing a collection row may not do silently.</summary>
    public static IReadOnlyList<EntityReference> ZoneMembers(RuntimeQuerySession session,
        IReadOnlyList<EntityReference> candidates, EntityReference zone)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        RuntimeEntityReferences.Validate(zone);
        if (candidates.Count > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Explicit candidate query exceeds the Runtime limit.");
        var matched = new List<EntityReference>();
        foreach (var candidate in candidates)
        {
            if (!session.TryZone(candidate, out var current, out var code))
                throw new RuntimeContractException(code, "Zone of " + candidate.Id + " could not be read: " + code);
            if (current! == zone) matched.Add(candidate);
        }
        return IdentityOrder(matched);
    }

    /// <summary>The relation filter measured against the row's explicit anchor, never against a role looked up
    /// again: the anchor is one more reference read in the same budgeted call as the candidates.</summary>
    public static IReadOnlyList<EntityReference> Filter(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference anchor, RuntimeFactionRelations relations, RecipientFilterRequest request)
        => ObservedRecipientFilter.Select(session, candidates, anchor, relations, request).Selected;

    /// <summary>The explicit weighted draw. Every input is a plan value, so this half reads no world at all.</summary>
    public static IReadOnlyList<EntityReference> Weighted(IReadOnlyList<EntityReference> candidates, IReadOnlyList<double> weights,
        int count, long seed)
    {
        if (weights.Count != candidates.Count)
            throw new RuntimeContractException("weighted-weight", "One weight is required per candidate.");
        var weighted = candidates.Select((target, index) => new WeightedCandidate(target, weights[index])).ToArray();
        return WeightedSampling.Sample(weighted, count, seed, WeightedSamplingMode.WithoutReplacement).Selected;
    }

    /// <summary>Partition splits an observed candidate set on one named field into the rows whose field carries the
    /// plan's `key` and the rows whose field does not. The two sides are disjoint and together are exactly the
    /// input, so nothing is dropped; a field the catalog does not declare is refused instead of answering every
    /// target as "rest". A field every candidate has (`identity`, `kind`, `life_state`) and a key that is never
    /// found therefore answer every candidate as `rest` — a real split, not an error.</summary>
    public static ObservedPartition PartitionOn(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        string fieldName, string key)
    {
        var values = PartitionField(fieldName);
        var matched = new List<EntityReference>(); var rest = new List<EntityReference>();
        foreach (var snapshot in Observe(session, candidates))
            (values(snapshot).Contains(key, StringComparer.Ordinal) ? matched : rest).Add(snapshot.Ref);
        return new ObservedPartition(IdentityOrder(matched).ToArray(), IdentityOrder(rest).ToArray());
    }

    private static Func<RuntimeEntitySnapshot, IReadOnlyList<string>> PartitionField(string name) => name switch
    {
        "identity" => snapshot => new[] { snapshot.Ref.Id },
        "kind" => snapshot => new[] { snapshot.Kind },
        "faction" => snapshot => snapshot.Faction == null ? Array.Empty<string>() : new[] { snapshot.Faction },
        "life_state" => snapshot => new[] { snapshot.LifeState },
        "tag" => snapshot => snapshot.Tags,
        "receiver" => snapshot => snapshot.Receives,
        _ => throw new RuntimeContractException("partition-key", "Unknown partition field: " + name)
    };

    /// <summary>The catalog's structural empty policy, read from one row's resolved parameter frame. `fail` refuses
    /// an empty answer, `skip` only tells a downstream scheduler to skip work, so the selected value itself stays
    /// empty for both of them.</summary>
    public static EmptySelectionPolicy EmptyPolicy(JsonElement parameters) => ObservedEvaluation.EmptyPolicy(parameters);
}
