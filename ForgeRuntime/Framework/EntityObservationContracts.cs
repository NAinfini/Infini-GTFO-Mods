using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>Uses the existing Runtime entity wire contract; never normalizes an ID.</summary>
public static class RuntimeEntityReferences
{
    public static EntityReference Validate(EntityReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RuntimeJson.Text(reference.Id);
        RuntimeJson.Integer(reference.WorldEpoch); RuntimeJson.Integer(reference.LifeEpoch);
        return reference;
    }
}

/// <summary>Frozen targeting observation, not authority, world coverage or a native handle.</summary>
public sealed class RuntimeEntitySnapshot
{
    public EntityReference Ref { get; }
    public string Kind { get; }
    public string? Faction { get; }
    public string LifeState { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<string> Receives { get; }
    public IReadOnlyList<double> Position { get; }

    public RuntimeEntitySnapshot(EntityReference reference, string kind, string? faction,
        string lifeState, IReadOnlyList<string> tags, IReadOnlyList<string> receives,
        IReadOnlyList<double> position)
    {
        Ref = RuntimeEntityReferences.Validate(reference); Kind = RuntimeJson.Text(kind);
        Faction = faction == null ? null : RuntimeJson.Text(faction);
        RuntimeJson.Require(lifeState is "alive" or "downed" or "dead", "entity-life-state", "Unknown life state.");
        LifeState = lifeState; Tags = CopyLabels(tags); Receives = CopyLabels(receives);
        RuntimeJson.Require(position != null && position.Count == 3, "entity-position", "Position requires three finite coordinates.");
        var coordinates = new double[3];
        for (int i = 0; i < 3; i++)
        {
            coordinates[i] = position![i];
            RuntimeJson.Require(double.IsFinite(coordinates[i]), "entity-position", "Non-finite coordinate.");
        }
        Position = Array.AsReadOnly(coordinates);
    }
    internal static IReadOnlyList<string> CopyLabels(IReadOnlyList<string> labels)
    {
        RuntimeJson.Require(labels != null && labels.Count <= 128, "entity-label-budget", "At most 128 labels are allowed.");
        var copy = new string[labels!.Count]; var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = RuntimeJson.Text(labels[i]);
            RuntimeJson.Require(seen.Add(copy[i]), "duplicate-value", "Duplicate observation label.");
        }
        return Array.AsReadOnly(copy);
    }

    public static RuntimeEntitySnapshot FromJson(JsonElement input)
    {
        var value = RuntimeJson.Parse(input.GetRawText());
        RuntimeJson.Shape(value, "ref kind faction lifeState tags receives position");
        var position = value.GetProperty("position");
        RuntimeJson.Require(position.ValueKind == JsonValueKind.Array && position.GetArrayLength() == 3,
            "entity-position", "Position requires three finite coordinates.");
        var coordinates = new double[3];
        for (int i = 0; i < 3; i++)
            RuntimeJson.Require(position[i].ValueKind == JsonValueKind.Number && position[i].TryGetDouble(out coordinates[i])
                && double.IsFinite(coordinates[i]), "entity-position", "Non-finite coordinate.");
        return new(RuntimeJson.Entity(value.GetProperty("ref")), RuntimeJson.Text(value, "kind"),
            value.GetProperty("faction").ValueKind == JsonValueKind.Null ? null : RuntimeJson.Text(value, "faction"),
            RuntimeJson.Text(value, "lifeState"), RuntimeJson.Strings(value.GetProperty("tags")),
            RuntimeJson.Strings(value.GetProperty("receives")), coordinates);
    }
}

public sealed record RuntimeEntityInspection(EntityReference Reference,
    RuntimeEntitySnapshot? Snapshot, string Code);

/// <summary>Complete covers only requested references, never all entities in a world or area.</summary>
public sealed class RuntimeEntityQueryResult
{
    public string Status { get; }
    public string Code { get; }
    public RuntimeLifecycleSnapshot Context { get; }
    public int Requested { get; }
    public int Distinct { get; }
    public IReadOnlyList<RuntimeEntityInspection> Items { get; }
    public bool IsComplete => Status == "complete";
    internal RuntimeEntityQueryResult(string status, string code, RuntimeLifecycleSnapshot context,
        int requested, int distinct, IEnumerable<RuntimeEntityInspection> items)
    {
        Status = status; Code = code; Context = context; Requested = requested; Distinct = distinct;
        Items = Array.AsReadOnly(items.ToArray());
    }
    public IReadOnlyList<RuntimeEntitySnapshot> RequireComplete()
    {
        RuntimeJson.Require(IsComplete, Code, "Entity observation is not complete; no implicit partial selection is allowed.");
        return Array.AsReadOnly(Items.Select(item => item.Snapshot!).ToArray());
    }
}
