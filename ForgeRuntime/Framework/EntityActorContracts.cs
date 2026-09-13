using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>Explicit actor roles supplied by the caller. No role is inferred from another.</summary>
public sealed class RuntimeActorContext
{
    private readonly IReadOnlyDictionary<string, EntityReference> actors;
    public RuntimeActorContext(IReadOnlyDictionary<string, EntityReference> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        RuntimeJson.Require(values.Count <= 5, "actor-budget", "Only the five explicit roles are supported.");
        var copy = new Dictionary<string, EntityReference>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            ValidateRole(pair.Key); copy.Add(pair.Key, RuntimeEntityReferences.Validate(pair.Value));
        }
        actors = new ReadOnlyDictionary<string, EntityReference>(copy);
    }
    internal static void ValidateRole(string role)
        => RuntimeJson.Require(role is "self" or "source" or "owner" or "instigator" or "event-target",
            "actor-role", "Unknown actor role.");
    public EntityReference? Get(string role)
    { ValidateRole(role); return actors.TryGetValue(role, out var value) ? value : null; }
    public IReadOnlyDictionary<string, EntityReference> Actors => actors;
    public static RuntimeActorContext FromJson(JsonElement input)
    {
        var value = RuntimeJson.Parse(input.GetRawText());
        RuntimeJson.Shape(value, "", "self source owner instigator event-target");
        return new(value.EnumerateObject().ToDictionary(p => p.Name,
            p => RuntimeJson.Entity(p.Value), StringComparer.Ordinal));
    }
}

public sealed record RuntimeFactionRelation(string From, string To, string Relation);

/// <summary>Bounded directed snapshot of explicit relations, not a global faction service.</summary>
public sealed class RuntimeFactionRelations
{
    public const int MaximumRelations = 4096;
    private readonly Dictionary<(string From, string To), string> relations = new();
    public RuntimeFactionRelations(IReadOnlyList<RuntimeFactionRelation> rules)
    {
        RuntimeJson.Require(rules != null && rules.Count <= MaximumRelations,
            "relation-budget", "Faction relation budget exceeded.");
        for (int i = 0; i < rules!.Count; i++)
        {
            var rule = rules[i]; ArgumentNullException.ThrowIfNull(rule);
            var from = RuntimeJson.Text(rule.From); var to = RuntimeJson.Text(rule.To);
            RuntimeJson.Require(rule.Relation is "ally" or "hostile" or "neutral",
                "relation-value", "Only explicit ally, hostile or neutral rules are accepted.");
            RuntimeJson.Require(relations.TryAdd((from, to), rule.Relation),
                "relation-conflict", "Duplicate directed faction rule.");
        }
    }
    public string Resolve(RuntimeEntitySnapshot anchor, RuntimeEntitySnapshot recipient)
    {
        ArgumentNullException.ThrowIfNull(anchor); ArgumentNullException.ThrowIfNull(recipient);
        RuntimeJson.Require(anchor.Ref.WorldEpoch == recipient.Ref.WorldEpoch,
            "stale-world", "Relationship snapshots belong to different worlds.");
        if (anchor.Ref == recipient.Ref) return "self";
        if (anchor.Faction == null || recipient.Faction == null) return "unknown";
        return relations.TryGetValue((anchor.Faction, recipient.Faction), out var relation) ? relation : "unknown";
    }
    public static RuntimeFactionRelations FromJson(JsonElement input)
    {
        var value = RuntimeJson.Parse(input.GetRawText());
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= MaximumRelations,
            "relation-budget", "Expected bounded faction rule array.");
        var rules = new List<RuntimeFactionRelation>();
        foreach (var rule in value.EnumerateArray())
        {
            RuntimeJson.Shape(rule, "from to relation");
            rules.Add(new(RuntimeJson.Text(rule, "from"), RuntimeJson.Text(rule, "to"), RuntimeJson.Text(rule, "relation")));
        }
        return new(rules);
    }
}
