using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: NativeEvidence <BepInEx> <GTFO game root> <frozen spec.json> <report.json>");
    return 2;
}
var checks = new List<EvidenceCheck>();
void Check(string id, bool passed, string detail) => checks.Add(new(id, passed, detail));
string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); }
IEnumerable<TypeDefinition> Walk(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Walk));
var assemblies = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
try
{
    using var document = JsonDocument.Parse(File.ReadAllText(args[2]));
    var spec = document.RootElement;
    if (spec.GetProperty("schemaVersion").GetInt32() != 1 || spec.GetProperty("methods").GetArrayLength() == 0)
        throw new InvalidDataException("Unsupported or empty frozen evidence specification.");
    var appPath = Path.GetFullPath(Path.Combine(args[1], "..", "..", "appmanifest_493520.acf"));
    var app = File.ReadAllText(appPath);
    string Field(string key) => Regex.Match(app, "\"" + key + "\"\\s+\"([^\"]+)\"").Groups[1].Value;
    Check("steam.app", Field("appid") == "493520", "Steam app ID must be 493520.");
    Check("steam.build", Field("buildid") == spec.GetProperty("buildId").GetString(), "Observed build: " + Field("buildid"));
    var nativePath = Path.Combine(args[1], "GameAssembly.dll");
    var nativeHash = Hash(nativePath);
    Check("native.hash", nativeHash == spec.GetProperty("gameAssemblySha256").GetString(), nativeHash);
    foreach (var expected in spec.GetProperty("assemblies").EnumerateArray())
    {
        string file = expected.GetProperty("file").GetString()!;
        if (Path.GetFileName(file) != file || !file.EndsWith(".dll", StringComparison.Ordinal))
            throw new InvalidDataException("Assembly entries must be plain DLL filenames.");
        string path = Path.Combine(args[0], "interop", file);
        Check("hash." + file, Hash(path) == expected.GetProperty("sha256").GetString(), Hash(path));
        var assembly = AssemblyDefinition.ReadAssembly(path);
        assemblies.Add(file, assembly);
        Check("mvid." + file, assembly.MainModule.Mvid.ToString() == expected.GetProperty("mvid").GetString(), assembly.MainModule.Mvid.ToString());
    }
    foreach (var expected in spec.GetProperty("methods").EnumerateArray())
    {
        string signature = expected.GetProperty("signature").GetString()!;
        var assembly = assemblies[expected.GetProperty("assembly").GetString()!];
        var type = assembly.MainModule.Types.SelectMany(Walk).SingleOrDefault(t => t.FullName == expected.GetProperty("type").GetString());
        var matches = type?.Methods.Where(m => m.FullName == signature).ToArray() ?? Array.Empty<MethodDefinition>();
        bool passed = matches.Length == 1 && matches[0].IsStatic == expected.GetProperty("isStatic").GetBoolean()
            && matches[0].IsVirtual == expected.GetProperty("isVirtual").GetBoolean()
            && matches[0].IsPublic == expected.GetProperty("isPublic").GetBoolean();
        Check(expected.GetProperty("id").GetString()!, passed, signature);
    }
    foreach (var expected in spec.GetProperty("absentDeclaredMethods").EnumerateArray())
    {
        var assembly = assemblies[expected.GetProperty("assembly").GetString()!];
        var type = assembly.MainModule.Types.SelectMany(Walk).Single(t => t.FullName == expected.GetProperty("type").GetString());
        Check(expected.GetProperty("id").GetString()!, !type.Methods.Any(m => m.Name == expected.GetProperty("name").GetString()),
            "No declaration on this type; inherited method existence does not prove an implemented receiver.");
    }
    NativeHealth.Verify(nativePath, nativeHash, Check);
}
catch (Exception error) { Check("audit.error", false, error.GetType().Name + ": " + error.Message); }
finally { foreach (var assembly in assemblies.Values) assembly.Dispose(); }
int failed = checks.Count(c => !c.Passed);
var report = new { schemaVersion = 1, verification = "metadata-and-static-native-only", gameExecuted = false,
    utc = DateTimeOffset.UtcNow, passed = checks.Count - failed, failed, checks };
string reportPath = Path.GetFullPath(args[3]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Detail);
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {checks.Count - failed}/{checks.Count} static evidence checks; game execution and multiplayer NOT tested.");
return failed == 0 ? 0 : 1;
internal sealed record EvidenceCheck(string Id, bool Passed, string Detail);
