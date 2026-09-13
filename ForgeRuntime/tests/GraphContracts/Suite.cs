using System.Text.Json;
using ForgeRuntime.Framework;

internal static class Suite
{
    internal static int Passed, Failed;
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
    internal static RuntimeKernel Kernel() => new(new RuntimeIdentity(
        "forge.runtime", "1.2.0", "1.0.0", "synthetic-no-game"));
    internal static RuntimeModule Module(JsonElement seed) => new("1.0.0", seed.GetRawText(),
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
