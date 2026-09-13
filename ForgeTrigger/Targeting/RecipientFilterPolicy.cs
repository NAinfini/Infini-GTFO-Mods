using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Targeting;

// A transient reader for the existing RecipientPolicy v1 parameter, not another registry or wire format.
internal sealed record RecipientFilterPolicy(string Anchor, string[]? Kinds, string[] Relations,
    string[] LifeStates, string[] RequireTags, string[] ExcludeTags, string Sort, int Maximum)
{
    internal static RecipientFilterPolicy Read(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) Fail("Expected the existing recipient-policy object.");
        var value = RuntimeJson.Parse(input.GetRawText());
        var fields = new[] { "schemaVersion", "anchor", "kinds", "relations", "lifeStates",
            "requireTags", "excludeTags", "sort", "maxTargets" };
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal)) Fail("Unknown policy field: " + property.Name);
        foreach (var field in fields)
            if (!value.TryGetProperty(field, out _)) Fail("Missing policy field: " + field);
        if (Integer(value.GetProperty("schemaVersion"), 1, 1) != 1) Fail("Unsupported policy version.");
        var anchor = Text(value.GetProperty("anchor"));
        if (anchor is not ("self" or "source" or "owner" or "instigator" or "event-target")) Fail("Unknown relation anchor.");
        var rawKinds = value.GetProperty("kinds");
        var kinds = rawKinds.ValueKind == JsonValueKind.String && rawKinds.GetString() == "any"
            ? null : Labels(rawKinds, nonempty: true);
        var relations = Labels(value.GetProperty("relations"), nonempty: true);
        if (relations.Any(x => x is not ("self" or "ally" or "hostile" or "neutral" or "unknown"))) Fail("Unknown relation.");
        var lives = Labels(value.GetProperty("lifeStates"), nonempty: true);
        if (lives.Any(x => x is not ("alive" or "downed" or "dead"))) Fail("Unknown life state.");
        var required = Labels(value.GetProperty("requireTags"));
        var excluded = Labels(value.GetProperty("excludeTags"));
        if (required.Intersect(excluded, StringComparer.Ordinal).Any()) Fail("Contradictory tags.");
        var sort = Text(value.GetProperty("sort"));
        if (sort is not ("stable-id" or "nearest" or "farthest")) Fail("Unknown sorting policy.");
        return new(anchor, kinds, relations, lives, required, excluded, sort,
            Integer(value.GetProperty("maxTargets"), 1, RuntimeKernel.MaximumEntityReferencesPerQuery));
    }

    internal static string[] ReceiverLabels(IReadOnlyList<string> values)
    {
        if (values is null || values.Count > 128) Fail("Receiver requirement budget exceeded or missing.");
        var copy = new string[values!.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = TextValue(values[i]);
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length) Fail("Duplicate receiver requirement.");
        return copy;
    }

    private static string[] Labels(JsonElement value, bool nonempty = false)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 128
            || nonempty && value.GetArrayLength() == 0) Fail("Expected a bounded label array.");
        var result = value.EnumerateArray().Select(Text).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) Fail("Duplicate policy label.");
        return result;
    }

    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) Fail("Expected a text policy value.");
        return TextValue(value.GetString());
    }

    private static string TextValue(string? input)
    {
        if (input is null) Fail("Policy text is missing.");
        var text = input!;
        // Match the Runtime's supported text subset; never trim or normalize identity/tag data.
        if (text.Length is < 1 or > 256 || text.Trim() != text || text.Any(c => c <= 31 || c == 127))
            Fail("Policy text is not valid Runtime text.");
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) Fail("Policy text contains an unpaired surrogate.");
            }
            else if (char.IsLowSurrogate(text[i])) Fail("Policy text contains an unpaired surrogate.");
        }
        return text;
    }

    private static int Integer(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number) Fail("Expected an integer policy value.");
        var number = value.GetDouble();
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < minimum || number > maximum)
            Fail("Policy integer is outside its allowed range.");
        return (int)number;
    }

    private static void Fail(string detail) => throw new RuntimeContractException("recipient-policy", detail);
}
