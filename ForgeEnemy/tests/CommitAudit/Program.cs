using System.Security.Cryptography;
using System.Text.Json;
using ForgeRuntime.Framework;

if (args.Length != 2) { Console.Error.WriteLine("Usage: CommitAudit <runtime fixture directory> <report.json>"); return 2; }
AuditScene.Fixtures = Path.GetFullPath(args[0]);
Audit.Prefix = "existing-path.";
CommitCases.Run(); IdentityCases.Run(); DamageCases.Run();
int existingCount = Audit.Rows.Count, existingFailed = Audit.Rows.Count(r => !r.Passed);
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
    schemaVersion = 1, verification = "existing-EnemyModule-source-and-compiled-SDK-with-test-doubles",
    nativeGameExecuted = false, multiplayerExecuted = false, utc = DateTimeOffset.UtcNow,
    enemyModuleSourceSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "EnemyModule.source.txt")),
    gameDoublesSourceSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "GameDoubles.source.txt")),
    frameworkAssemblySha256 = Hash(typeof(RuntimeKernel).Assembly.Location),
    fixtureSha256 = Hash(Path.Combine(AuditScene.Fixtures, "native-heal.plan.json")),
    migrationHealthSourceSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "EnemyHealthCommit.source.txt")),
    existingPath = new { passed = existingCount - existingFailed, failed = existingFailed },
    migrationBoundary = new { passed = Audit.Rows.Count - existingCount - (failed - existingFailed), failed = failed - existingFailed },
    passed = Audit.Rows.Count - failed, failed, checks = Audit.Rows
};
string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var row in Audit.Rows.Where(row => !row.Passed)) Console.Error.WriteLine("FAIL " + row.Id + ": " + row.Detail.Split('\n')[0]);
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {Audit.Rows.Count - failed}/{Audit.Rows.Count} independent Enemy commit/identity/damage cases. Not executed in GTFO.");
return failed == 0 ? 0 : 1;
