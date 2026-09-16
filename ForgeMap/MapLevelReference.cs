using System;
using System.Globalization;

namespace ForgeMap;

/// <summary>
/// The identity of one level inside one rundown package, written the way a `level` mount target spells it:
/// `&lt;rundown block id&gt;:&lt;tier A-E&gt;:&lt;tier index&gt;`, every number decimal without leading zeros —
/// the same spelling the authoring side already uses for its native level presets. Both halves of the
/// comparison come from the game itself: the rundown block the process loaded, and the active expedition's own
/// tier and zero-based index, so no name has to be invented on either side.
///
/// The parser is strict on purpose. A reference that is not this shape names no level, and the matcher that
/// reads one answers false with a single bounded diagnostic rather than reading "I cannot tell" as "this must
/// be my level".
/// </summary>
public readonly record struct MapLevelReference(uint RundownId, char Tier, int Index)
{
    /// <summary>The one reference grammar: three colon-separated segments, the middle one a single tier letter
    /// from A to E. A null, a missing segment, a lowercase or out-of-range tier letter and a number with a
    /// leading zero, a sign or any other character all leave the text unparsed.</summary>
    public static MapLevelReference? TryParse(string? reference)
    {
        if (reference == null) return null;
        var parts = reference.Split(':');
        if (parts.Length != 3 || parts[1].Length != 1) return null;
        var tier = parts[1][0];
        if (tier is < 'A' or > 'E') return null;
        if (!Decimal(parts[0], out var rundownId) || rundownId > uint.MaxValue) return null;
        if (!Decimal(parts[2], out var index) || index > int.MaxValue) return null;
        return new MapLevelReference((uint)rundownId, tier, (int)index);
    }

    /// <summary>The identity the game reports for its active expedition: the rundown block it loaded, the tier
    /// as the game's own `eRundownTier` (A is 1) and the zero-based index inside that tier. A tier outside the
    /// five letters, or a negative index, names no level this vocabulary can spell, so it answers null instead
    /// of a reference that would not round-trip.</summary>
    public static MapLevelReference? FromNative(uint rundownId, int tier, int index)
        => tier is >= 1 and <= 5 && index >= 0 ? new MapLevelReference(rundownId, (char)('A' + tier - 1), index) : null;

    /// <summary>The reference text this identity writes, so a diagnostic and the value it was compared against
    /// cannot spell the same level two ways.</summary>
    public override string ToString()
        => RundownId.ToString(CultureInfo.InvariantCulture) + ":" + Tier + ":" + Index.ToString(CultureInfo.InvariantCulture);

    /// <summary>A decimal written without a leading zero. Zero itself is one digit and is legal — the first
    /// tier's index is 0 — while an empty text, a leading zero on a longer text and any non-digit leave the
    /// text unparsed. The accumulator is bounded before it can overflow, so an over-long number is refused
    /// rather than wrapped.</summary>
    private static bool Decimal(string text, out long value)
    {
        value = 0;
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0')) return false;
        foreach (var digit in text)
        {
            if (digit is < '0' or > '9') return false;
            value = value * 10 + (digit - '0');
            if (value > uint.MaxValue) return false;
        }
        return true;
    }
}
