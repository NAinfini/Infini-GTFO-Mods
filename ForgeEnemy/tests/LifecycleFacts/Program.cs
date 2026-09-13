using System.Security.Cryptography;
using System.Text.Json;
using ForgeRuntime.Framework;
using SNetwork;

if (args.Length != 2) { Console.Error.WriteLine("Usage: LifecycleFacts <runtime fixtures> <report.json>"); return 2; }
Scene.Fixtures = Path.GetFullPath(args[0]);
T.OutputDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
DeathCases.Run(); LimbCases.Run(); IntegrationCases.Run();
var report = new { schemaVersion = 1, verification = "production-provider-hooks-and-compiled-sdk-with-game-doubles",
    gameExecuted = false, multiplayerExecuted = false, utc = DateTimeOffset.UtcNow,
    sdkSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RuntimeKernel).Assembly.Location))),
    passed = T.Rows.Count(x => x.Passed), failed = T.Rows.Count(x => !x.Passed), checks = T.Rows };
File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(report.failed == 0 ? "PASS" : "FAIL")} {report.passed}/{T.Rows.Count} lifecycle fact cases; no GTFO execution.");
return report.failed == 0 ? 0 : 1;

internal static class T
{
    internal sealed record Row(string Id, bool Passed, string Detail);
    internal static readonly List<Row> Rows = new();
    internal static string OutputDirectory = "";
    internal static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    internal static void Case(string id, Action test)
    {
        try { test(); Rows.Add(new(id, true, "passed")); }
        catch (Exception e) { Rows.Add(new(id, false, e.ToString())); Console.Error.WriteLine("FAIL " + id + ": " + e.Message); }
        finally { SNet.IsMaster = true; }
    }
}
