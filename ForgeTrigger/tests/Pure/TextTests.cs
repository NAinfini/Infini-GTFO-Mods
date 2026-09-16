using ForgeTrigger.Pure;

/// <summary>The one text row: a template sentence with one value placed in it, and the three formats the authoring
/// node names — integer, a fixed decimal count, percent — read from the template's own format item. The website has
/// no preview for this row yet, so its coverage is here: the same public helper the registered handler calls, plus
/// the refusals a plan could otherwise carry into a string port.</summary>
internal static class TextTests
{
    internal static void Run(Action<bool, string> check, Action<string, string, Action> reject)
    {
        check(TextNodes.Compose("剩余 {0:0} 个电池", 3) == "剩余 3 个电池", "the template places the value where its format item is");
        check(TextNodes.Compose("{0:0}-{0:0}", 4) == "4-4", "a format item may be placed more than once");
        check(TextNodes.Compose("{{剩余}} {0:0}", 2) == "{剩余} 2", "a doubled brace stays a literal brace");
        check(TextNodes.Compose("{0}", 2.5) == "2.5", "a format item without a format writes the invariant number");

        check(TextNodes.Compose("{0:0}", 3.4) == "3" && TextNodes.Compose("{0:0}", 3.6) == "4",
            "integer format writes no decimal places");
        check(TextNodes.Compose("{0:0.00}", 2.5) == "2.50" && TextNodes.Compose("{0:0.000}", -0.125) == "-0.125",
            "the format item's decimal count is the one that is written");
        check(TextNodes.Compose("{0:0%}", 0.3) == "30%" && TextNodes.Compose("{0:0.0%}", 0.306) == "30.6%",
            "percent format scales by one hundred and names the unit");
        check(TextNodes.Compose("{0:0.00}", -0d) == "0.00", "negative zero has one text form");

        reject("a template with no format item", "pure-text-template", () => TextNodes.Compose("剩余 3 个电池", 3));
        reject("a template that is only escaped braces", "pure-text-template", () => TextNodes.Compose("{{0}}", 3));
        reject("a malformed format item", "pure-text-template", () => TextNodes.Compose("剩余 {0:0 个电池", 3));
        reject("a format item past the argument list", "pure-text-template", () => TextNodes.Compose("剩余 {1:0}", 3));
        reject("a result longer than a string port carries", "pure-text-range", () => TextNodes.Compose("{0:0}", 1e300));
        reject("a result with a control character", "pure-text-encoding", () => TextNodes.Compose("剩余 {0:0}\n", 3));
        reject("a non-finite value", "pure-invalid-number", () => TextNodes.Compose("{0:0}", double.NaN));
    }
}
