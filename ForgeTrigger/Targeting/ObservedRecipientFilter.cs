using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

public sealed record RecipientFilterExclusion(EntityReference Target, string Code, string? Detail);

/// <summary>Read-only selection evidence, not permission, cost reservation or a committed effect.</summary>
public sealed class RecipientFilterSelection
{
    internal RecipientFilterSelection(EntityReference[] selected, RecipientFilterExclusion[] excluded,
        int requested, int distinct, RuntimeLifecycleSnapshot context)
    {
        Selected = Array.AsReadOnly(selected); Excluded = Array.AsReadOnly(excluded);
        Requested = requested; Distinct = distinct; Context = context;
    }
    public IReadOnlyList<EntityReference> Selected { get; }
    public IReadOnlyList<RecipientFilterExclusion> Excluded { get; }
    public int Requested { get; }
    public int Distinct { get; }
    public int Matched => Selected.Count;
    public RuntimeLifecycleSnapshot Context { get; }
}

/// <summary>Filter explicit current references using the existing RecipientPolicy and public R3 observations.</summary>
public static class ObservedRecipientFilter
{
    public static RecipientFilterSelection Select(RuntimeKernel runtime, IReadOnlyList<EntityReference> candidates,
        RuntimeActorContext actors, RuntimeFactionRelations relations, JsonElement policyInput,
        IReadOnlyList<string> requiredReceivers)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(relations);
        var policy = RecipientFilterPolicy.Read(policyInput);
        var receivers = RecipientFilterPolicy.ReceiverLabels(requiredReceivers);
        if (candidates.Count > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Candidate input exceeds the public query limit.");
        var captured = candidates.Select(RuntimeEntityReferences.Validate).ToArray();
        var anchor = actors.Get(policy.Anchor);
        if (anchor is null)
            throw new RuntimeContractException("actor-missing", "The explicit relation anchor is missing.");
        var requested = captured.ToList();
        if (!requested.Contains(anchor)) requested.Add(anchor);
        if (requested.Count > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Candidates and anchor exceed the public query limit.");
        var query = runtime.InspectEntities(requested);
        var observed = query.RequireComplete();
        var anchorSnapshot = observed.Single(row => row.Ref == anchor);
        var candidateSet = new HashSet<EntityReference>(captured);
        var matched = new List<RuntimeEntitySnapshot>();
        var excluded = new List<RecipientFilterExclusion>();
        foreach (var row in observed.Where(row => candidateSet.Contains(row.Ref)))
        {
            void Exclude(string code, string? detail = null) => excluded.Add(new(row.Ref, code, detail));
            if (policy.Kinds != null && !policy.Kinds.Contains(row.Kind, StringComparer.Ordinal))
            { Exclude("kind-filter"); continue; }
            var relation = relations.Resolve(anchorSnapshot, row);
            if (!policy.Relations.Contains(relation, StringComparer.Ordinal))
            { Exclude("relation-filter", relation); continue; }
            if (!policy.LifeStates.Contains(row.LifeState, StringComparer.Ordinal))
            { Exclude("life-filter", row.LifeState); continue; }
            var missingTags = policy.RequireTags.Except(row.Tags, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missingTags.Length > 0)
            { Exclude("required-tag", string.Join(", ", missingTags)); continue; }
            var excludedTags = policy.ExcludeTags.Intersect(row.Tags, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (excludedTags.Length > 0)
            { Exclude("excluded-tag", string.Join(", ", excludedTags)); continue; }
            var missingReceivers = receivers.Except(row.Receives, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missingReceivers.Length > 0)
            { Exclude("receiver-unsupported", string.Join(", ", missingReceivers)); continue; }
            matched.Add(row);
        }
        var ranked = matched.Select(row => (Row: row, Distance: policy.Sort == "stable-id" ? 0d
            : ObservedSpatialNodes.Distance(row.Position, anchorSnapshot.Position))).ToArray();
        var ordered = policy.Sort == "farthest" ? ranked.OrderByDescending(row => row.Distance)
            : ranked.OrderBy(row => row.Distance);
        // This policy's stable order is the current entity ID, as in the existing website targeting function.
        var selected = ordered.ThenBy(row => row.Row.Ref.Id, StringComparer.Ordinal).Select(row => row.Row.Ref).ToArray();
        if (selected.Length > policy.Maximum)
            throw new RuntimeContractException("recipient-target-limit", "Matched targets exceed the explicit limit; no partial selection is returned.");
        var rejected = excluded.OrderBy(row => ReferenceCollections.OrderKey(row.Target), StringComparer.Ordinal)
            .ThenBy(row => row.Code, StringComparer.Ordinal).ToArray();
        return new RecipientFilterSelection(selected, rejected, captured.Length, candidateSet.Count, query.Context);
    }
}
