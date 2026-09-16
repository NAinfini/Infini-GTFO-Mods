using System.Security.Cryptography;
using System.Text.Json;
using ForgeRuntime.Framework;

// `T` is the suite's own check list (Scene.cs); the cases are the files next to this one.

if (args.Length != 1) { Console.Error.WriteLine("Usage: EnemyBehavior <report.json>"); return 2; }
LedgerCases.Run(); ActionCases.Run();
int failed = T.Rows.Count(x => !x.Passed), passed = T.Rows.Count - failed;
var report = new
{
    schemaVersion = 1,
    verification = "production-provider-handlers-with-compiled-sdk-and-game-doubles",
    gameExecuted = false, multiplayerExecuted = false, utc = DateTimeOffset.UtcNow,
    sdkSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RuntimeKernel).Assembly.Location))),
    passed, failed, checks = T.Rows
};
string reportPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {passed}/{T.Rows.Count} enemy behaviour cases; failed {failed}; no GTFO execution.");
return failed == 0 ? 0 : 1;
