using System.Security.Cryptography;
using System.Text.Json;
using ForgeRuntime.Framework;

if (args.Length != 1) { Console.Error.WriteLine("Usage: CommitAudit <report.json>"); return 2; }
Audit.Prefix = "existing-path.";
CommitCases.Run(); IdentityCases.Run(); DamageCases.Run();
int existingCount = Audit.Rows.Count, existingFailed = Audit.Rows.Count(r => !r.Passed), existingBlocked = Audit.Blocked.Count;
Audit.Prefix = "migration-boundary."; AuditScene.UseManagedBoundary = true;
CommitCases.Run();
string Hash(string path)
{
    using var stream = File.OpenRead(path); using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(stream));
}
int failed = Audit.Rows.Count(row => !row.Passed);
var report = new
{
    schemaVersion = 2, verification = "existing-EnemyModule-source-and-compiled-SDK-with-test-doubles",
    nativeGameExecuted = false, multiplayerExecuted = false, utc = DateTimeOffset.UtcNow,
    enemyModuleSourceSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "EnemyModule.source.txt")),
    gameDoublesSourceSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "GameDoubles.source.txt")),
    frameworkAssemblySha256 = Hash(typeof(RuntimeKernel).Assembly.Location),
    migrationHealthSourceSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "EnemyHealthCommit.source.txt")),
    existingPath = new { passed = existingCount - existingFailed, failed = existingFailed, blocked = existingBlocked },
    migrationBoundary = new { passed = Audit.Rows.Count - existingCount - (failed - existingFailed), failed = failed - existingFailed,
        blocked = Audit.Blocked.Count - existingBlocked },
    passed = Audit.Rows.Count - failed, failed, blocked = Audit.Blocked.Select(b => new { b.Id, b.Reason }).ToArray(), checks = Audit.Rows
};
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var row in Audit.Rows.Where(row => !row.Passed)) Console.Error.WriteLine("FAIL " + row.Id + ": " + row.Detail.Split('\n')[0]);
Blockers.Print(Audit.Blocked);
Console.WriteLine($"{Blockers.Verdict(failed, Audit.Blocked.Count)} {Audit.Rows.Count - failed}/{Audit.Rows.Count + Audit.Blocked.Count} independent Enemy commit/identity/damage cases; failed {failed}; BLOCKED {Audit.Blocked.Count}. Not executed in GTFO.");
return failed == 0 ? 0 : 1;
