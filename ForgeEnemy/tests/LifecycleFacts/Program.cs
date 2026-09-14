using System.Security.Cryptography;
using System.Text.Json;
using ForgeRuntime.Framework;
using SNetwork;

if (args.Length != 1) { Console.Error.WriteLine("Usage: LifecycleFacts <report.json>"); return 2; }
DeathCases.Run(); LimbCases.Run(); IntegrationCases.Run();
int failed = T.Rows.Count(x => !x.Passed), passed = T.Rows.Count - failed;
var report = new { schemaVersion = 3, verification = "production-provider-hooks-and-compiled-sdk-with-game-doubles",
    gameExecuted = false, multiplayerExecuted = false, utc = DateTimeOffset.UtcNow,
    sdkSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RuntimeKernel).Assembly.Location))),
    passed, failed, checks = T.Rows };
string reportPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {passed}/{T.Rows.Count} lifecycle fact cases; failed {failed}; no GTFO execution.");
return failed == 0 ? 0 : 1;

internal static class T
{
    internal sealed record Row(string Id, bool Passed, string Detail);
    internal static readonly List<Row> Rows = new();
    internal static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    internal static void Case(string id, Action test)
    {
        try { test(); Rows.Add(new(id, true, "passed")); }
        catch (Exception e) { Rows.Add(new(id, false, e.ToString())); Console.Error.WriteLine("FAIL " + id + ": " + e.Message); }
        finally { SNet.IsMaster = true; }
    }
}
