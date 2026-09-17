using System.Security.Cryptography;
using System.Text.Json;
using ForgeRuntime.Framework;

// `T` is the suite's own check list (Scene.cs); the cases are the two files next to this one. The documents are
// real files under a temporary BepInEx root, read by the production discovery and applied from the production
// spawn path; the native calls land on the shared game doubles and no GTFO process is started.

if (args.Length != 1) { Console.Error.WriteLine("Usage: EnemyProfile <report.json>"); return 2; }
DocumentCases.Run(); ApplyCases.Run();
int failed = T.Rows.Count(x => !x.Passed), passed = T.Rows.Count - failed;
var report = new
{
    schemaVersion = 3,
    verification = "production-profile-reader-discovery-and-spawn-application-with-game-doubles",
    gameExecuted = false, multiplayerExecuted = false, utc = DateTimeOffset.UtcNow,
    sdkSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RuntimeKernel).Assembly.Location))),
    passed, failed, checks = T.Rows
};
string reportPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {passed}/{T.Rows.Count} enemy profile cases; failed {failed}; no GTFO execution.");
return failed == 0 ? 0 : 1;
