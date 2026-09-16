using System;
using System.Globalization;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>The `g-text` row's implementation: one template sentence and one value joined into a line of text, with
/// the number written the way the template asks for it.
///
/// The template is a composite format string — `剩余 {0:0} 个电池`, `{0:0.00}`, `{0:0%}` — so the three formats the
/// authoring node names are the template's own format item rather than three more ports: `{0:0}` writes an integer,
/// `{0:0.00}` a fixed decimal count, and `{0:0%}` the value times one hundred with the percent sign. Formatting is
/// invariant-culture, and a template the formatter cannot read, a template that never places the value, and a text a
/// string port cannot carry are all refused here in this family's own terms instead of arriving at the far side of
/// the plan as a port error.</summary>
public static class TextNodes
{
    /// <summary>The longest text a kernel string port can carry; the same bound the framework enforces on every
    /// string it validates, applied here so the reason is this family's own code.</summary>
    public const int MaximumCharacters = 256;

    /// <summary>One line of text: the template with its format item filled from the value.</summary>
    public static string Compose(string template, double value)
    {
        if (template is null)
            throw new RuntimeContractException("pure-text-template", "A text template must not be null.");
        PureNumbers.Input(value);
        // A value that is negative zero is written as zero: the same normalization the arithmetic rows apply, so
        // one value has one text form.
        var number = value == 0d ? 0d : value;
        // Every text this row answers is the template with the value in it: a template that never places the value
        // would silently drop a wired input, which is a wiring error rather than an answer.
        if (!PlacesValue(template))
            throw new RuntimeContractException("pure-text-template", "The template does not place the value.");
        string text;
        try { text = string.Format(CultureInfo.InvariantCulture, template, number); }
        catch (FormatException error)
        {
            throw new RuntimeContractException("pure-text-template", "The template is not a composite format string: " + error.Message);
        }
        return Carry(text);
    }

    /// <summary>Whether the template has a format item, read with the composite format's own escaping rule: `{{`
    /// is a literal brace, so the first `{` that is not doubled is where the value goes.</summary>
    private static bool PlacesValue(string template)
    {
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '{') continue;
            if (index + 1 < template.Length && template[index + 1] == '{') { index++; continue; }
            return true;
        }
        return false;
    }

    /// <summary>Whether one text is what a string port carries, answered by the same three rules the framework
    /// validates strings with: no control characters, a length of 1..256, and no surrounding whitespace.</summary>
    private static string Carry(string text)
    {
        foreach (var character in text)
            if (character < 32 || character == 127)
                throw new RuntimeContractException("pure-text-encoding", "Text must not contain control characters.");
        if (text.Length is 0 or > MaximumCharacters || text.Trim() != text)
            throw new RuntimeContractException("pure-text-range", "Text must be 1.." + MaximumCharacters + " trimmed characters.");
        return text;
    }
}
