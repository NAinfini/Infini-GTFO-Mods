using System;

namespace InfiniTweaks;

internal enum MarkerCategory
{
    Health, Ammo, Tool, Disinfection, Consumable, CarryItem, Objective,
    Terminal, Generator, DisinfectionStation, HSU, BulkheadController, Other, HSUActivator
}

internal static class MarkerRules
{
    internal static string CleanConfig(string text)
    {
        // Only retired marker options are removed; unrelated sections retain their values.
        return System.Text.RegularExpressions.Regex.Replace(text,
            @"(?m)^\[(?<section>[^\]\r\n]+)\][^\r\n]*\r?\n(?<body>(?:(?!^\[)[\s\S])*)", match =>
            {
                string section = match.Groups["section"].Value;
                if (section == "Markers - DoorLock" || section.StartsWith("Marker Item - ", StringComparison.Ordinal)) return "";
                string keys = section == "Item Markers"
                    ? "ExplicitPingDistanceMeters|HideMarkersWhileAiming|MarkerOpacity|MarkerScale|DetailDistanceMeters"
                    : section.StartsWith("Markers - ", StringComparison.Ordinal) ? "Color" : "";
                if (keys.Length == 0) return match.Value;
                return System.Text.RegularExpressions.Regex.Replace(match.Value,
                    @"(?m)^(?:[ \t]*#[^\r\n]*\r?\n)*[ \t]*(?:" + keys + @")[ \t]*=[^\r\n]*(?:\r?\n|$)", "");
            });
    }

    // Match terminal item types, not arbitrary substrings (e.g. a cargo terminal).
    internal static MarkerCategory? CarryLandmark(string key) => key.Split('_')[0].ToUpperInvariant() switch
    {
        "CEL" or "CELL" => MarkerCategory.Generator,
        "CRYO" => MarkerCategory.HSU,
        "CARGO" => MarkerCategory.CarryItem,
        _ => null
    };
    internal static string SafeName(string name)
    {
        var text = new System.Text.StringBuilder();
        foreach (char c in name)
        {
            if (char.IsControl(c) || c is '<' or '>') continue;
            if (text.Length == 16) break;
            text.Append(c);
        }
        return text.ToString();
    }
    internal static string ResourceColor(float amount, bool infection = false, bool infinite = false)
    {
        if (!float.IsFinite(amount) && !infinite) return "C8D2DE";
        float value = infinite ? 1 : Math.Clamp(infection ? 1 - amount : amount, 0, 1);
        var low = value < 0.5f ? (255, 48, 48) : (255, 255, 0);
        var high = value < 0.5f ? (255, 255, 0) : (48, 255, 48);
        float t = Math.Clamp(value < 0.5f ? (value - 0.25f) * 4 : (value - 0.5f) * 4, 0, 1);
        int Mix(int a, int b) => (int)MathF.Round(a + (b - a) * t);
        return $"{Mix(low.Item1, high.Item1):X2}{Mix(low.Item2, high.Item2):X2}{Mix(low.Item3, high.Item3):X2}";
    }
    // ResourceHelper: nearby titles, otherwise the range-dependent focus cone.
    internal static bool Details(float distance, float angle) => distance < 4f || angle < 20f - distance * .3f;
    internal static float MarkerOpacity(float elapsed, float aimOpacity) =>
        (1f - MathF.Cos(Math.Clamp(elapsed / .1f, 0, 1) * MathF.PI / 2)) * aimOpacity;
    // Doors never become persistent item markers, including locked security doors.
    internal static bool TerminalAllowed(bool storage, bool door) => !storage && !door;
    internal static bool CountConsumable(bool infinite, bool showTotal, float max) => !infinite && showTotal && max > 1;
    internal static bool InRange(float distanceSquared, float range) =>
        range > 0 && float.IsFinite(distanceSquared) && distanceSquared >= 0 && distanceSquared <= range * range;
    internal static bool Depleted(bool counted, float amount) => counted && (!float.IsFinite(amount) || amount <= 0);
    internal static bool Discoverable(float distanceSquared, bool sameDimension, bool blocked, bool hitOwnSurface) =>
        float.IsFinite(distanceSquared) && distanceSquared >= 0 && distanceSquared <= 16f && sameDimension && (!blocked || hitOwnSurface);
    internal static (float Distance, string Color) Defaults(MarkerCategory category) => category switch
    {
        MarkerCategory.Health => (40, "#F25B5B"),
        MarkerCategory.Ammo => (40, "#79E68C"),
        MarkerCategory.Tool => (40, "#63B8FF"),
        MarkerCategory.Disinfection => (40, "#D8ACFF"),
        MarkerCategory.Consumable => (10, "#B8C6D9"),
        MarkerCategory.CarryItem or MarkerCategory.Objective => (45, "#FFB477"),
        MarkerCategory.Terminal => (40, "#77E8DD"),
        MarkerCategory.Generator => (30, "#FFD66B"),
        MarkerCategory.DisinfectionStation => (30, "#D8ACFF"),
        MarkerCategory.HSU or MarkerCategory.HSUActivator => (40, "#FFB477"),
        MarkerCategory.BulkheadController => (40, "#FFAA88"),
        _ => (20, "#FFFFFF")
    };

    internal static float Range(float categoryDistance, float forcedUntil, float now) =>
        categoryDistance <= 0 ? 0 : Math.Min(60f, now < forcedUntil ? 60f : categoryDistance);

    internal static float HudOpacity(float angleDegrees, bool aiming, float aimOpacity, bool dynamicOpacity) =>
        aiming ? aimOpacity : dynamicOpacity ? Math.Max(0.85f, 8f / Math.Clamp(angleDegrees, 8f, 40f)) : 1f;

    // Multiplication sign is language-neutral and avoids a misleading ammo percentage.
    internal static string Count(float uses) => float.IsFinite(uses) && uses > 0 ? $" ×{uses:0.#}" : "";
}
