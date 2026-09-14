using SNetwork;

internal static class Audit
{
    internal static string Prefix = "";
    internal static readonly List<AuditRow> Rows = new();
    internal static readonly List<(string Id, string Reason)> Blocked = new();
    internal static void Case(string name, Action test)
    {
        try { test(); Rows.Add(new(Prefix + name, true, "passed")); }
        catch (CaseBlocked reason) { Blocked.Add((Prefix + name, reason.Message)); }
        catch (Exception error) { Rows.Add(new(Prefix + name, false, error.ToString())); }
        finally { SNet.IsMaster = true; SFloat16.Preview = (value, _) => value; }
    }
    internal static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
internal sealed record AuditRow(string Id, bool Passed, string Detail);
