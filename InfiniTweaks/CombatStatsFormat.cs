using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InfiniTweaks;

// Dinorush-style slot / Shot-Group-Full / Hit-Crit-Fired tokens, not executable expressions.
internal static class CombatStatsFormat
{
    internal const string Hud = "<#A0A0A0>{Hit/Fired}</color>/<#FFFF00>{Crit/Hit}</color> (<#A0A0A0>{Damage}</color>)";
    internal const string Results = "<#A0A0A0>{Hit/Fired}</color>/<#FFFF00>{Crit/Hit}</color>  {Damage} (<#FFFF00>{DamageCrit}</color>)";
    private readonly record struct Term(StatSlot Slots, int Shot, int Value, bool Damage);
    private readonly record struct Segment(string? Literal, Term Numerator, Term? Denominator, string Format);
    private static readonly Dictionary<string, Segment[]> Templates = new();
    internal static void ClearCache() => Templates.Clear();
    private static bool Parse(string text, out Term term)
    {
        string value = text.Trim().ToUpperInvariant();
        StatSlot slots = StatSlot.None;
        bool Take(string word) { if (!value.Contains(word)) return false; value = value.Replace(word, ""); return true; }
        if (Take("PRIMARY") | Take("MAIN")) slots |= StatSlot.Main;
        if (Take("SECONDARY") | Take("SPECIAL")) slots |= StatSlot.Special;
        if (Take("TOOL") | Take("CLASS")) slots |= StatSlot.Tool;
        if (Take("MELEE")) slots |= StatSlot.Melee;
        if (Take("OTHER")) slots |= StatSlot.Other;
        if (Take("ALL")) slots = StatSlot.All;
        if (slots == StatSlot.None) slots = StatSlot.All;
        bool damage = Take("DAMAGE"), full = Take("FULL"), group = Take("GROUP");
        bool shot = Take("SHOT");
        bool fired = Take("FIRED"), crit = Take("CRIT"), hit = Take("HIT"), any = Take("ANY");
        term = new Term(slots, full ? 2 : group ? 1 : 0, fired ? 0 : crit ? 2 : 1, damage);
        return value.Length == 0 && text.Trim().Length > 0 && (full ? 1 : 0) + (group ? 1 : 0) + (shot ? 1 : 0) <= 1 &&
            (fired ? 1 : 0) + (crit ? 1 : 0) + (hit ? 1 : 0) + (any ? 1 : 0) <= 1 &&
            (!damage || !fired && !full && !group && !shot) && (damage || !any);
    }
    private static bool Token(string value, out Term numerator, out Term? denominator, out string format)
    {
        var parts = value.Split(':'); var terms = parts[0].Split('/');
        format = parts.Length == 2 ? parts[1] : "0"; denominator = null;
        if (!Parse(terms[0], out numerator) || parts.Length > 2 || terms.Length > 2 || format is not ("0" or "0.0" or "0.00")) return false;
        if (terms.Length == 2) { if (!Parse(terms[1], out var parsed)) return false; denominator = parsed; }
        return true;
    }
    internal static bool Valid(string text)
    {
        if (text == null || text.Length > 1024) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '}') return false;
            if (text[i] != '{') continue;
            int end = text.IndexOf('}', i + 1);
            if (end < 0 || !Token(text[(i + 1)..end], out _, out _, out _)) return false;
            i = end;
        }
        return true;
    }
    internal static string Render(string template, CombatStatsSync data, ulong player, StatSlot restrict = StatSlot.All)
    {
        if (!Templates.TryGetValue(template, out var segments))
        {
            if (!Valid(template)) throw new ArgumentException("Invalid statistics template.");
            var parts = new List<Segment>();
            int start = 0;
            while (start < template.Length)
            {
                int open = template.IndexOf('{', start);
                if (open < 0) { parts.Add(new(template[start..], default, null, "")); break; }
                if (open > start) parts.Add(new(template[start..open], default, null, ""));
                int end = template.IndexOf('}', open + 1);
                Token(template[(open + 1)..end], out var numerator, out var denominator, out var format);
                parts.Add(new(null, numerator, denominator, format));
                start = end + 1;
            }
            Templates[template] = segments = parts.ToArray();
        }
        double? Number(Term term)
        {
            var counts = data.Total(player, term.Slots & restrict, out bool known);
            if (term.Damage) return term.Value == 2 ? counts.CritDamage : counts.Damage;
            if (!known) return null;
            return (term.Shot, term.Value) switch
            {
                (0, 0) => counts.Fired, (0, 1) => counts.Hit, (0, 2) => counts.Crit,
                (1, 0) => counts.GroupFired, (1, 1) => counts.GroupHit, (1, 2) => counts.GroupCrit,
                (2, 0) => counts.FullFired, (2, 1) => counts.FullHit, _ => counts.FullCrit
            };
        }
        var output = new StringBuilder(template.Length + 32);
        foreach (var segment in segments)
        {
            if (segment.Literal != null) { output.Append(segment.Literal); continue; }
            double? value = Number(segment.Numerator);
            if (segment.Denominator is { } divisor)
            { double? amount = Number(divisor); value = amount > 0 ? value / amount * 100 : null; }
            else if (value.HasValue && segment.Format == "0") value = Math.Floor(value.Value);
            output.Append(value?.ToString(segment.Format, CultureInfo.InvariantCulture) ?? "—");
            if (value.HasValue && segment.Denominator != null) output.Append('%');
        }
        return output.ToString();
    }
}
