/// <summary>
/// Cases that cannot run against the current contracts. A blocked case is reported by id and reason, never
/// counted as passed; unblock conditions are recorded in ForgeEnemy/VALIDATION.md.
/// </summary>
internal static class Blockers
{
    /// <summary>heal takes many-valued targets; no event output can feed them until single-to-many wiring and literal inputs ship.</summary>
    internal const string Heal = "J-003: no legal heal plan exists before literal inputs and single-to-many wiring (FORGE-FRAMEWORK D-006 1-2)";

    /// <summary>I-PACK D-009 removed LoadPlan's permissions override; a plan's permissions now come only from its own
    /// JSON, so a load-time grant narrower than that cannot be expressed and this case tests a retired mechanism.</summary>
    internal const string PermissionDenied = "I-PACK D-009 removed grantedPermissions/permission-denied; this case tests a retired mechanism";

    /// <summary>Blocked cases are not passes: a run with blocks but no failures is incomplete, not green.</summary>
    internal static string Verdict(int failed, int blocked) => failed > 0 ? "FAIL" : blocked > 0 ? "INCOMPLETE" : "PASS";

    internal static void Print(IEnumerable<(string Id, string Reason)> rows)
    {
        foreach (var group in rows.GroupBy(row => row.Reason, StringComparer.Ordinal))
        {
            Console.WriteLine($"BLOCKED {group.Count()} ({group.Key})");
            foreach (var row in group) Console.WriteLine("  " + row.Id);
        }
    }
}

internal sealed class CaseBlocked : Exception
{
    internal CaseBlocked(string reason) : base(reason) { }
}
