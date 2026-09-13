using ForgeRuntime.Framework;
using SNetwork;

internal static class Checks
{
    internal static readonly List<Row> Rows = new();
    internal static void Case(string name, Action test)
    {
        try { test(); Rows.Add(new(name, true, "passed")); }
        catch (Exception error)
        {
            Rows.Add(new(name, false, error.ToString()));
            Console.Error.WriteLine("FAIL " + name + ": " + error.Message);
        }
        finally { SNet.IsMaster = true; }
    }
    internal static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    internal static void Code(Action action, string code)
    {
        try { action(); }
        catch (RuntimeContractException error) when (error.Code == code) { return; }
        throw new Exception("Expected contract rejection: " + code);
    }
    internal sealed record Row(string Id, bool Passed, string Detail);
}
