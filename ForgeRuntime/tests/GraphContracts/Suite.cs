using System.Text.Json;
using ForgeRuntime.Framework;

internal static class Suite
{
    internal static int Passed, Failed;
    private static readonly List<string> uncovered = new();
    /// <summary>The website catalog rows this runtime does not register: a definition its own registry refuses is a
    /// capability outside the shapes this SDK knows, so the row is counted rather than failed. Counted rows are
    /// printed with the total, which is what keeps them from being silently dropped.</summary>
    internal static int Unregistered => uncovered.Count;
    internal static void Test(string name, Action action)
    {
        try { action(); }
        catch (Exception error) { Failed++; Console.WriteLine($"FAIL {name}: {error}"); }
    }
    internal static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Passed++;
    }
    internal static string Reject(Action action, string? expectedCode = null)
    {
        try { action(); }
        catch (RuntimeContractException error)
        {
            Check(expectedCode == null || error.Code == expectedCode,
                $"Expected {expectedCode}, got {error.Code}.");
            return error.Code;
        }
        throw new InvalidOperationException("Expected contract rejection.");
    }
    /// <summary>One catalog row outside this runtime's registry, recorded under its vector name.</summary>
    internal static void Uncover(string name) => uncovered.Add(name);
    /// <summary>True while this runtime's own registry accepts the website definition a vector carries. The check
    /// runs in a kernel of its own, so asking does not touch the case that follows.</summary>
    internal static bool Registrable(JsonElement seed)
    {
        try { Kernel().RegisterModule(Module(seed), RuntimeLogLevel.Off); return true; }
        catch (RuntimeContractException) { return false; }
    }
    internal static void ReportCoverage()
    {
        Console.WriteLine($"unregistered: {Unregistered}");
        foreach (var name in uncovered) Console.WriteLine("  " + name);
    }
    internal static RuntimeKernel Kernel() => new(new RuntimeIdentity(
        "forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
    internal static RuntimeModule Module(JsonElement seed) => new(RuntimeKernel.ApiVersion, seed.GetRawText(),
        new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
    internal static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count()
                && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var v) && Equal(p.Value, v)),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength()
                && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Equal(p.First, p.Second)),
            JsonValueKind.Number => a.GetDouble() == b.GetDouble(),
            JsonValueKind.String => a.GetString() == b.GetString(),
            _ => a.GetRawText() == b.GetRawText()
        };
    }
}
