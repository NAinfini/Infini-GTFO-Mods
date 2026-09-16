using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

/// <summary>One relation selection over explicit current references. This is a read-only answer, not permission,
/// cost reservation or a committed effect: the references it returns are re-checked by the kernel before a
/// consumer sees them, exactly like the candidates it was handed.</summary>
public sealed class RecipientFilterSelection
{
    internal RecipientFilterSelection(EntityReference[] selected, int requested, int distinct)
    {
        Selected = Array.AsReadOnly(selected); Requested = requested; Distinct = distinct;
    }
    public IReadOnlyList<EntityReference> Selected { get; }
    public int Requested { get; }
    public int Distinct { get; }
    public int Matched => Selected.Count;
}

/// <summary>One filter row's structural parameters: the relation the candidates must have to the row's own
/// `anchor` input, which is the whole of what the catalog's `forge.selector.target.filter` declares beyond the
/// anchor and the candidates. There is no policy object here — the shape a plan compiles is the shape the
/// capability declares, and a field no row declares cannot be read by one.</summary>
public sealed record RecipientFilterRequest(string Relation)
{
    /// <summary>Every member of the catalog's `recipient_relation` set, in its declared order.</summary>
    public static readonly string[] Relations = { "self", "ally", "hostile", "neutral", "unknown" };

    /// <summary>One relation read from a row's resolved parameter frame, which spells an enum as its member name.
    /// A name outside the set is refused rather than answered with an empty selection, because "no candidate has a
    /// relation nobody declared" would look exactly like a world in which none of them matched.</summary>
    public static RecipientFilterRequest Read(string relation)
    {
        if (relation is null || !Relations.Contains(relation, StringComparer.Ordinal))
            throw new RuntimeContractException("recipient-relation", "Unknown relation: " + relation);
        return new RecipientFilterRequest(relation);
    }
}

/// <summary>Keeps the candidates whose current relation to the row's own anchor is the requested one, using the
/// world's own faction relations and the step's own observation session.
///
/// The anchor is the explicit `anchor` input of the filter row, never a role looked up again: a deployable whose
/// owner is the thing its targets must relate to wires `owner` into that port, and the row measures the relation
/// against exactly what it was handed. A pair the world says nothing about resolves to `unknown`, which is a
/// relation like any other and therefore matches only when `unknown` was asked for; it is never treated as "no
/// match" or as a guess at neutrality. The answer is ordered by stable identity, so the same world and the same
/// candidates always produce the same set.</summary>
public static class ObservedRecipientFilter
{
    public static RecipientFilterSelection Select(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference anchor, RuntimeFactionRelations relations, RecipientFilterRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(request);
        // A request the row cannot answer is refused before the world is read: a relation the catalog does not
        // declare and a candidate list past the read budget are decidable from the step's own inputs, and a step
        // that could never have answered leaves no observation behind.
        var captured = candidates.ToArray();
        RuntimeEntityReferences.Validate(anchor);
        if (captured.Length > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Candidate input exceeds the public query limit.");
        if (!RecipientFilterRequest.Relations.Contains(request.Relation, StringComparer.Ordinal))
            throw new RuntimeContractException("recipient-relation", "Unknown relation: " + request.Relation);
        var requested = captured.Contains(anchor) ? captured : captured.Append(anchor).ToArray();
        // One read for the candidates and their anchor together: the anchor's own current faction is part of the
        // answer, so observing it separately would be a second chance at a different world.
        var observed = ObservedSpaceNodes.Observe(session, requested);
        var anchorSnapshot = observed.Single(row => row.Ref == anchor);
        var candidatesSet = new HashSet<EntityReference>(captured);
        var matched = observed
            .Where(row => candidatesSet.Contains(row.Ref)
                && string.Equals(relations.Resolve(anchorSnapshot, row), request.Relation, StringComparison.Ordinal))
            .Select(row => row.Ref)
            .OrderBy(row => ReferenceCollections.OrderKey(row), StringComparer.Ordinal)
            .ToArray();
        return new RecipientFilterSelection(matched, captured.Length, candidatesSet.Count);
    }
}
