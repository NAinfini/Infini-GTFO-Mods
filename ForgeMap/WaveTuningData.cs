using System;
using System.Text.Json;

namespace ForgeMap;

/// <summary>
/// The wave tuning one level carries, as the map data document's own field: the `waveTuning` member of one
/// expedition's map project in `plugins/&lt;package&gt;/projects/rundown.json`, the document a package ships its
/// level data in. It is a **field of the level**, not a file of its own and not an action: the game picks a wave's
/// next type by cooling every type down, drawing by weight and heating the type it drew, and every aggressive
/// enemy also spends from a per-dimension cap. Those numbers live on components the level carries and no data
/// block carries them, so the value is applied once when the level is there and stays applied — an author who
/// wants them changed mid-level is asking for a different feature, not for a second verb on this one. The rows
/// that used to exist (`forge.action.map.wave_tuning` and the per-package `wave-tuning.json` document) are gone
/// with their binding and permission.
///
/// There are no `fixes` here. The source mod's cooldown-speed and per-level heat-reset switches and its boss-limit
/// raise change what the game itself does rather than what an author asked for; the rulings classify those as
/// system fixes, so they are not author data and are not read from this field.
///
/// Every field is optional: a level that tunes one number keeps the game's own values for the rest, which is the
/// only reading under which a partial tuning means what it says. Each table is exactly `eEnemyType`'s own length
/// in its own member order (`dump.cs:591652`: Weakling, Standard, Special, MiniBoss, Boss), because that is the
/// order the two components index their arrays by; padding or truncating one would pick enemies the author never
/// named. Reading is strict and all-or-nothing: a field with an unknown member, a wrong length or a number that is
/// not a finite non-negative value names no tuning at all.
/// </summary>
public sealed record WaveTuningData(
    float? AllowedTotalCost,
    float? HeatCooldownSpeed,
    float? MaxHeat,
    float[]? BaseWeights,
    float[]? HeatAtStart,
    float[]? TypeCostTowardsCap)
{
    /// <summary>The members of `eEnemyType`, which is the length every table here has to be.</summary>
    public const int TypeCount = 5;

    /// <summary>The field's own name inside one expedition's map project, spelled the way the map document spells
    /// its own fields: camelCase beside `native`, `balance` and the other level-level members.</summary>
    public const string FieldName = "waveTuning";

    /// <summary>Whether the field asks for any change at all. A field that names nothing is refused rather than
    /// applied as a successful write of nothing.</summary>
    public bool IsEmpty => AllowedTotalCost is null && HeatCooldownSpeed is null && MaxHeat is null
        && BaseWeights is null && HeatAtStart is null && TypeCostTowardsCap is null;

    /// <summary>Reads the level's own field. Every refusal is one code and one sentence naming the first thing
    /// that could not be read.</summary>
    public static bool TryRead(JsonElement field, out WaveTuningData? data, out string? code, out string? reason)
    {
        data = null;
        code = null;
        reason = null;
        if (field.ValueKind != JsonValueKind.Object)
            return Refuse(out code, out reason, "wave-tuning-json", "The level's `waveTuning` field is not an object.");
        if (Unknown(field, out var unknown, "allowedTotalCost", "heatCooldownSpeed", "maxHeat",
                "baseWeights", "heatAtStart", "typeCostTowardsCap"))
            return Refuse(out code, out reason, "wave-tuning-field", "Unknown field `" + unknown + "`.");
        if (Number(field, "allowedTotalCost", out var allowed) is { } allowedCode)
            return Refuse(out code, out reason, allowedCode, "`allowedTotalCost` is not a finite non-negative number.");
        if (Number(field, "heatCooldownSpeed", out var speed) is { } speedCode)
            return Refuse(out code, out reason, speedCode, "`heatCooldownSpeed` is not a finite non-negative number.");
        if (Number(field, "maxHeat", out var maxHeat) is { } heatCode)
            return Refuse(out code, out reason, heatCode, "`maxHeat` is not a finite non-negative number.");
        if (Table(field, "baseWeights", out var weights) is { } weightsCode)
            return Refuse(out code, out reason, weightsCode, TableReason("baseWeights"));
        if (Table(field, "heatAtStart", out var heat) is { } heatTableCode)
            return Refuse(out code, out reason, heatTableCode, TableReason("heatAtStart"));
        if (Table(field, "typeCostTowardsCap", out var costs) is { } costsCode)
            return Refuse(out code, out reason, costsCode, TableReason("typeCostTowardsCap"));
        var result = new WaveTuningData(allowed, speed, maxHeat, weights, heat, costs);
        if (result.IsEmpty) return Refuse(out code, out reason, "wave-tuning-empty", "The level tunes nothing.");
        data = result;
        return true;
    }

    /// <summary>One optional finite non-negative number.</summary>
    private static string? Number(JsonElement root, string field, out float? value)
    {
        value = null;
        if (!root.TryGetProperty(field, out var member)) return null;
        if (member.ValueKind != JsonValueKind.Number || !IsValue(member)) return "wave-tuning-value";
        value = member.GetSingle();
        return null;
    }

    /// <summary>One optional table: exactly <see cref="TypeCount"/> finite non-negative numbers.</summary>
    private static string? Table(JsonElement root, string field, out float[]? table)
    {
        table = null;
        if (!root.TryGetProperty(field, out var member)) return null;
        if (member.ValueKind != JsonValueKind.Array || member.GetArrayLength() != TypeCount)
            return "wave-tuning-table";
        var values = new float[TypeCount];
        var index = 0;
        foreach (var item in member.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !IsValue(item)) return "wave-tuning-value";
            values[index++] = item.GetSingle();
        }
        table = values;
        return null;
    }

    private static string TableReason(string field)
        => "`" + field + "` is not " + TypeCount + " finite non-negative numbers.";

    /// <summary>The first member the level's field carries that this shape does not name, or null when every
    /// member is known. A field with a member nothing reads would otherwise be applied as if the member were not
    /// there.</summary>
    private static bool Unknown(JsonElement root, out string? unknown, params string[] known)
    {
        unknown = null;
        foreach (var property in root.EnumerateObject())
        {
            var found = false;
            foreach (var name in known) if (string.Equals(name, property.Name, StringComparison.Ordinal)) { found = true; break; }
            if (!found) { unknown = property.Name; return true; }
        }
        return false;
    }

    private static bool Refuse(out string? code, out string? reason, string refused, string why)
    {
        code = refused;
        reason = why;
        return false;
    }

    /// <summary>A finite, non-negative number: the tables are weights, heats and costs, and a negative one would
    /// make the game's own weighted draw pick a type it never should.</summary>
    private static bool IsValue(JsonElement element)
        => element.TryGetSingle(out float number) && !float.IsNaN(number) && !float.IsInfinity(number) && number >= 0f;
}
