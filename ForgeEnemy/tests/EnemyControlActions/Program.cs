using System.Security.Cryptography;
using System.Text.Json;
using ForgeRuntime.Framework;
using static Probe;

if (args.Length != 1) { Console.Error.WriteLine("Usage: EnemyControlActions <report.json>"); return 2; }
EnemyControlFacts.Run();

int failed = Rows.Count(x => !x.Passed), passed = Rows.Count - failed;
var report = new
{
    schemaVersion = 3,
    verification = "production-provider-handler-with-compiled-sdk-and-game-doubles",
    gameExecuted = false,
    multiplayerExecuted = false,
    utc = DateTimeOffset.UtcNow,
    sdkSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RuntimeKernel).Assembly.Location))),
    passed,
    failed,
    checks = Rows
};
string reportPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {passed}/{Rows.Count} enemy control action cases; failed {failed}; no GTFO execution.");
return failed == 0 ? 0 : 1;
