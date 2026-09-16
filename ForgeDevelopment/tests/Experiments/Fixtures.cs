using System.Text.Json;
using ForgeDevelopment.Native;

internal static class Json
{
    internal static JsonElement Element(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    internal static ExperimentCommand Parse(string text, out IReadOnlyList<string> problems)
    {
        var commands = ExperimentParser.Parse(text, "test.json", out var error);
        if (error.Length != 0)
        {
            problems = new[] { error };
            return null!;
        }
        problems = commands.Count == 0 ? new[] { "no command" } : commands[0].Problems;
        return commands.Count == 0 ? null! : commands[0];
    }

    internal static bool Valid(string text) => Parse(text, out var problems) is { } command && problems.Count == 0 && command.Valid;

    internal static string ProblemOf(string text)
    {
        Parse(text, out var problems);
        return string.Join(" | ", problems);
    }
}