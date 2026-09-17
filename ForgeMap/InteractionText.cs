using System;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ForgeMap;

/// <summary>The four edits one interaction-prompt request may ask for, plus the structural mode that puts the
/// game's own prompt back. The names are the wire spelling the row's `mode` parameter carries.</summary>
public enum InteractionTextMode { Replace, Prefix, Suffix, Glitch, Clear }

/// <summary>The two glitch presentations, in the generic spelling of what they draw: hexadecimal noise, and a
/// decryption reading that never settles. The EOS feature's `Style1`/`Style2` are these two.</summary>
public enum InteractionTextStyle { Hex, Decrypt }

/// <summary>
/// One interaction-prompt rule: which edit, which glitch presentation, the author's text and how fast the glitch
/// re-rolls. The mode and the text are one fact — a `replace` with no text and a `prefix` with no text are both
/// orders that would write nothing — which is why this is one record and not three optional members.
/// </summary>
public sealed record InteractionTextRule(InteractionTextMode Mode, InteractionTextStyle Style, string Text,
    double RefreshSeconds)
{
    /// <summary>The rule that leaves the prompt exactly as the game wrote it.</summary>
    public static readonly InteractionTextRule None =
        new(InteractionTextMode.Clear, InteractionTextStyle.Hex, "", InteractionText.HexRefreshSeconds);

    public bool IsClear => Mode == InteractionTextMode.Clear;

    /// <summary>What the player reads. The glitch is applied last on purpose — a broken prompt breaks all of it,
    /// including the author's own words. A cleared rule returns the game's own text, which is the whole point of
    /// the mode.</summary>
    public string Render(string original, double elapsedSeconds) => Mode switch
    {
        InteractionTextMode.Clear => original,
        InteractionTextMode.Replace => Text,
        InteractionTextMode.Prefix => Text + original,
        InteractionTextMode.Suffix => original + Text,
        _ => InteractionText.Glitch(Style, original, RefreshSeconds, elapsedSeconds)
    };
}

/// <summary>
/// The text half of the interaction-prompt row, as a pure function of the rule, the game's own prompt and the
/// clock. Nothing here touches the game, so the transform the player sees is the one this file can be tested on.
///
/// The glitch is a function of a time bucket rather than of a frame count: the feature this replaces refreshes its
/// hexadecimal noise about every 0.05 s and its decryption reading about every 0.075 s, and the prompt is rebuilt
/// by the game's own interaction layer. Reading the clock keeps the animation reproducible from a test without
/// this layer owning an update loop — the one thing it does not do is repaint the prompt by itself, which is
/// listed for in-game verification.
///
/// Reading is strict and whole-request: a rule this half cannot carry out exactly is refused with one code rather
/// than applied with the parts that happened to parse, because "the prompt says something else than what I wrote"
/// is a different request from the one the author made.</summary>
public static class InteractionText
{
    /// <summary>A rule, or one of its texts, past the length the prompt buffers are read at.</summary>
    public const string TooLongCode = "interaction-text-too-long";
    /// <summary>The `mode` member is not one of the five the row carries.</summary>
    public const string ModeUnknownCode = "interaction-text-mode-unknown";
    /// <summary>The `style` member is not one of the two the row carries.</summary>
    public const string StyleUnknownCode = "interaction-text-style-unknown";
    /// <summary>A mode that writes text was asked for without any text to write.</summary>
    public const string MissingTextCode = "interaction-text-missing-text";
    /// <summary>The refresh interval is not a positive, finite number of ticks.</summary>
    public const string RefreshCode = "interaction-text-refresh-invalid";

    public const int MaximumTextLength = 256;

    /// <summary>The two cadences the feature this row replaces used, kept as the defaults a request that names no
    /// interval gets. The row's `refresh_interval` is declared in ticks and these are the seconds the glitch
    /// arithmetic below runs in, so the request is converted once at the read.</summary>
    public const double HexRefreshSeconds = 0.05;
    public const double DecryptRefreshSeconds = 0.075;

    /// <summary>The seconds one tick of plan time is.</summary>
    private const double SecondsPerTick = 1.0 / 60.0;

    /// <summary>The rule a request asks for, or the one code that names why it was refused. The text is the row's
    /// own `text` input; the mode, the style and the interval are its structural parameters.</summary>
    public static bool TryRead(JsonElement parameters, JsonElement text, out InteractionTextRule rule, out string code)
    {
        rule = InteractionTextRule.None;
        code = "";
        if (!TryReadMode(parameters, out var mode)) { code = ModeUnknownCode; return false; }
        if (!TryReadStyle(parameters, out var style)) { code = StyleUnknownCode; return false; }
        if (!TryReadRefresh(parameters, style, out var refresh)) { code = RefreshCode; return false; }
        var written = ReadText(text, out var tooLong);
        if (tooLong) { code = TooLongCode; return false; }
        if (mode != InteractionTextMode.Clear && mode != InteractionTextMode.Glitch && written.Length == 0)
        {
            code = MissingTextCode;
            return false;
        }
        rule = new InteractionTextRule(mode, style, written, refresh);
        return true;
    }

    /// <summary>The mode member. A missing member is a refusal rather than a default: the row declares it
    /// required, and a request that names no edit is not one this layer can guess at.</summary>
    public static bool TryReadMode(JsonElement parameters, out InteractionTextMode mode)
    {
        mode = InteractionTextMode.Clear;
        switch (Member(parameters, "mode"))
        {
            case "replace": mode = InteractionTextMode.Replace; return true;
            case "prefix": mode = InteractionTextMode.Prefix; return true;
            case "suffix": mode = InteractionTextMode.Suffix; return true;
            case "glitch": mode = InteractionTextMode.Glitch; return true;
            case "clear": mode = InteractionTextMode.Clear; return true;
            default: return false;
        }
    }

    /// <summary>The glitch presentation. A missing member reads as the hexadecimal noise, which is the one the
    /// feature this row replaces showed by default.</summary>
    public static bool TryReadStyle(JsonElement parameters, out InteractionTextStyle style)
    {
        style = InteractionTextStyle.Hex;
        switch (Member(parameters, "style"))
        {
            case null:
            case "hex": style = InteractionTextStyle.Hex; return true;
            case "decrypt": style = InteractionTextStyle.Decrypt; return true;
            default: return false;
        }
    }

    /// <summary>The refresh cadence: a positive finite number of ticks, or the style's own default when the
    /// request names none. A request may name one for any mode; only the glitch reads it. The value is converted
    /// to the seconds the glitch arithmetic runs in before the rule is built.</summary>
    public static bool TryReadRefresh(JsonElement parameters, InteractionTextStyle style, out double seconds)
    {
        seconds = style == InteractionTextStyle.Decrypt ? DecryptRefreshSeconds : HexRefreshSeconds;
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("refresh_interval", out var member)
            || member.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        if (member.ValueKind != JsonValueKind.Number || !member.TryGetDouble(out var value)) return false;
        if (!double.IsFinite(value) || value <= 0) return false;
        seconds = value * SecondsPerTick;
        return true;
    }

    /// <summary>One bucket of a glitch presentation. `elapsedSeconds` is the game clock the prompt is drawn
    /// against; a negative or non-finite reading is treated as the first bucket rather than refusing to draw.</summary>
    public static string Glitch(InteractionTextStyle style, string text, double refreshSeconds, double elapsedSeconds)
    {
        if (text.Length == 0) return text;
        var seconds = double.IsFinite(elapsedSeconds) && elapsedSeconds > 0 ? elapsedSeconds : 0;
        var cadence = double.IsFinite(refreshSeconds) && refreshSeconds > 0 ? refreshSeconds : HexRefreshSeconds;
        var bucket = (long)Math.Floor(seconds / cadence);
        return style == InteractionTextStyle.Hex ? HexNoise(text, bucket) : DecryptingError(bucket);
    }

    /// <summary>The hexadecimal noise: every visible character is replaced by a hexadecimal digit the bucket
    /// picks, and the prompt keeps its length so the row it is drawn in does not move.</summary>
    public static string HexNoise(string text, long bucket)
    {
        const string digits = "0123456789ABCDEF";
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character)) { builder.Append(character); continue; }
            builder.Append(digits[(int)(Mix(bucket, index) % (ulong)digits.Length)]);
        }
        return builder.ToString();
    }

    /// <summary>The decryption error: a progress reading that never settles, and a plain `UNKNOWN` on the buckets
    /// where the read fails outright. Both are presentation strings, not localized copy — the object's own prompt
    /// is the only text an author writes.</summary>
    public static string DecryptingError(long bucket)
    {
        var roll = Mix(bucket, 0);
        if (roll % 7 == 0) return "UNKNOWN";
        return "DECRYPTING " + (roll % 100).ToString(CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>The bucket mix: a stable function of the bucket and the character index, used instead of a
    /// random draw so the same bucket always paints the same prompt on every machine and a test can assert the
    /// exact string.</summary>
    private static ulong Mix(long bucket, int index)
    {
        unchecked
        {
            var value = (ulong)bucket * 0x9E3779B97F4A7C15UL + (ulong)index * 0xBF58476D1CE4E5B9UL;
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return value;
        }
    }

    /// <summary>The row's `text` input: absent or null is the empty string, a non-string is a refusal, and a text
    /// past the buffer limit is one too.</summary>
    private static string ReadText(JsonElement text, out bool tooLong)
    {
        tooLong = false;
        if (text.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        if (text.ValueKind != JsonValueKind.String) { tooLong = true; return ""; }
        var value = text.GetString() ?? "";
        if (value.Length > MaximumTextLength) { tooLong = true; return ""; }
        return value;
    }

    private static string? Member(JsonElement parameters, string id)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(id, out var member)
           && member.ValueKind == JsonValueKind.String ? member.GetString() : null;
}
