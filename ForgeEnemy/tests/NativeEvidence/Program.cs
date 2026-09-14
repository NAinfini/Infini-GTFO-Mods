using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;

if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: NativeEvidence <BepInEx> <GTFO game root> <ForgeEnemy.Native.dll> <frozen spec.json> <report.json>");
    return 2;
}
var checks = new List<EvidenceCheck>();
void Check(string id, bool passed, string detail) => checks.Add(new(id, passed, detail));
string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); }
IEnumerable<TypeDefinition> Walk(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Walk));
void Shape(JsonElement element, string[] required, params string[] optional)
{
    if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Specification entries must be objects.");
    var names = element.EnumerateObject().Select(p => p.Name).ToArray();
    var unknown = names.Except(required).Except(optional).ToArray();
    var missing = required.Except(names).ToArray();
    if (unknown.Length > 0 || missing.Length > 0)
        throw new InvalidDataException($"Specification shape mismatch: unknown [{string.Join(",", unknown)}], missing [{string.Join(",", missing)}].");
}
string[] Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!).ToArray();
// Compiler-generated lambdas, local functions and state machines are attributed to the source method that owns them.
string Caller(MethodDefinition method)
{
    var type = method.DeclaringType; string name = method.Name;
    while (type.Name.StartsWith('<') && type.DeclaringType != null)
    {
        var generated = Regex.Match(type.Name, "^<([^>]+)>");
        if (generated.Success && !name.StartsWith('<')) name = generated.Groups[1].Value;
        type = type.DeclaringType;
    }
    var owner = Regex.Match(name, "^<([^>]+)>");
    return type.FullName + "::" + (owner.Success ? owner.Groups[1].Value : name);
}
static bool Resolves(JsonElement current, string[] segments)
{
    if (segments.Length == 0) return true;
    bool each = segments[0].EndsWith("[*]", StringComparison.Ordinal);
    string name = each ? segments[0][..^3] : segments[0];
    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next)) return false;
    if (!each) return Resolves(next, segments[1..]);
    return next.ValueKind == JsonValueKind.Array && next.GetArrayLength() > 0 && next.EnumerateArray().All(item => Resolves(item, segments[1..]));
}

var areas = new HashSet<string>(StringComparer.Ordinal) { "identity", "spawn-requirements", "health", "damage-limbs-death", "ai-perception",
    "movement-space", "abilities", "birthing-scout", "attacks-projectiles", "appearance-animation", "cleanup-replication" };
var assemblies = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
var dataFiles = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);
var distribution = new SortedDictionary<string, int>(StringComparer.Ordinal);
void Count(string key) => distribution[key] = distribution.GetValueOrDefault(key) + 1;
string forgeHash = Hash(args[2]);
try
{
    using var document = JsonDocument.Parse(File.ReadAllText(args[3]));
    var spec = document.RootElement;
    Shape(spec, new[] { "schemaVersion", "buildId", "gameAssemblySha256", "verification", "gameExecuted", "assemblies",
        "dataEvidenceFiles", "methods", "enumConstants", "absentDeclaredMethods" });
    if (spec.GetProperty("schemaVersion").GetInt32() != 2 || spec.GetProperty("methods").GetArrayLength() == 0
        || spec.GetProperty("gameExecuted").GetBoolean())
        throw new InvalidDataException("Unsupported, empty or game-executed frozen evidence specification.");
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
        Shape(expected, new[] { "file", "sha256", "mvid" });
        string file = expected.GetProperty("file").GetString()!;
        if (Path.GetFileName(file) != file || !file.EndsWith(".dll", StringComparison.Ordinal))
            throw new InvalidDataException("Assembly entries must be plain DLL filenames.");
        string path = Path.Combine(args[0], "interop", file);
        Check("hash." + file, Hash(path) == expected.GetProperty("sha256").GetString(), Hash(path));
        var assembly = AssemblyDefinition.ReadAssembly(path);
        assemblies.Add(file, assembly);
        Check("mvid." + file, assembly.MainModule.Mvid.ToString() == expected.GetProperty("mvid").GetString(), assembly.MainModule.Mvid.ToString());
    }

    // Offline data files that give a native getter its asset-level values (never runtime readback).
    string specDirectory = Path.GetDirectoryName(Path.GetFullPath(args[3]))!;
    foreach (var file in spec.GetProperty("dataEvidenceFiles").EnumerateArray())
    {
        Shape(file, new[] { "path", "sha256" });
        string relative = file.GetProperty("path").GetString()!;
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
            throw new InvalidDataException("Data evidence paths must stay under the specification directory.");
        string full = Path.Combine(specDirectory, relative);
        bool exists = File.Exists(full);
        Check("data-file." + relative, exists && Hash(full) == file.GetProperty("sha256").GetString(), exists ? Hash(full) : "missing");
        if (exists) dataFiles.Add(relative, JsonDocument.Parse(File.ReadAllBytes(full)));
    }

    // What the shipped Enemy plugin actually hooks and calls, read from its IL rather than asserted.
    var gameScopes = assemblies.Keys.Select(f => f[..^4]).ToHashSet(StringComparer.Ordinal);
    var ilHooks = new Dictionary<(string Type, string Name), SortedSet<string>>();
    var ilCalls = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    using (var forge = AssemblyDefinition.ReadAssembly(args[2]))
    {
        foreach (var type in forge.MainModule.Types.SelectMany(Walk))
        {
            foreach (var patch in type.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
            {
                if (patch.ConstructorArguments.Count != 2 || patch.ConstructorArguments[0].Value is not TypeReference target
                    || patch.ConstructorArguments[1].Value is not string name)
                    throw new InvalidDataException("Unsupported HarmonyPatch shape on " + type.FullName);
                var kinds = type.Methods.Where(m => m.Name is "Prefix" or "Postfix"
                        || m.CustomAttributes.Any(a => a.AttributeType.FullName is "HarmonyLib.HarmonyPrefix" or "HarmonyLib.HarmonyPostfix"))
                    .Select(m => m.Name == "Prefix" || m.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPrefix") ? "prefix" : "postfix");
                var key = (target.FullName, name);
                if (!ilHooks.TryGetValue(key, out var set)) ilHooks[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.UnionWith(kinds);
            }
            foreach (var method in type.Methods.Where(m => m.HasBody))
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called) continue;
                    string scope = called.DeclaringType.Scope.Name;
                    if (scope.EndsWith(".dll", StringComparison.Ordinal)) scope = scope[..^4];
                    if (!gameScopes.Contains(scope)) continue;
                    if (!ilCalls.TryGetValue(called.FullName, out var callers)) ilCalls[called.FullName] = callers = new SortedSet<string>(StringComparer.Ordinal);
                    callers.Add(Caller(method));
                }
        }
    }

    var ids = new HashSet<string>(StringComparer.Ordinal);
    var frozen = new HashSet<string>(StringComparer.Ordinal);
    var staticNative = new HashSet<string>(StringComparer.Ordinal);
    var hookOwners = new Dictionary<(string, string), List<string>>();
    foreach (var expected in spec.GetProperty("methods").EnumerateArray())
    {
        Shape(expected, new[] { "id", "area", "assembly", "type", "signature", "isStatic", "isVirtual", "isPublic",
            "evidenceLevel", "nativeCallPhase", "forgeHooks", "forgeCallers", "dataEvidence" }, "nativeRva");
        string id = expected.GetProperty("id").GetString()!;
        string area = expected.GetProperty("area").GetString()!;
        if (!ids.Add(id) || !areas.Contains(area)) throw new InvalidDataException("Duplicate method id or unknown area: " + id);
        string signature = expected.GetProperty("signature").GetString()!;
        string typeName = expected.GetProperty("type").GetString()!;
        if (!frozen.Add(signature)) throw new InvalidDataException("Duplicate frozen signature: " + signature);
        var assembly = assemblies[expected.GetProperty("assembly").GetString()!];
        var type = assembly.MainModule.Types.SelectMany(Walk).SingleOrDefault(t => t.FullName == typeName);
        var matches = type?.Methods.Where(m => m.FullName == signature).ToArray() ?? Array.Empty<MethodDefinition>();
        bool passed = matches.Length == 1 && matches[0].IsStatic == expected.GetProperty("isStatic").GetBoolean()
            && matches[0].IsVirtual == expected.GetProperty("isVirtual").GetBoolean()
            && matches[0].IsPublic == expected.GetProperty("isPublic").GetBoolean();
        Check(id, passed, signature);

        string level = expected.GetProperty("evidenceLevel").GetString()!;
        bool hasRva = expected.TryGetProperty("nativeRva", out var rva);
        bool reviewed = NativeHealth.ReviewedRvas.TryGetValue(signature, out int reviewedRva);
        bool levelHolds = level switch
        {
            "static-native" => reviewed && hasRva && rva.GetString() == "0x" + reviewedRva.ToString("X"),
            "metadata" => !reviewed && !hasRva,
            _ => false,
        };
        Check("evidence-level." + id, levelHolds, level + "; static-native is limited to bodies NativeHealth decodes at a reviewed RVA.");
        if (level == "static-native" && levelHolds) staticNative.Add(signature);
        Count("evidence:" + level);

        Check("call-phase." + id, expected.GetProperty("nativeCallPhase").GetString() == "unknown",
            "No runtime trace exists for this build, so the phase in which the game calls this method stays unknown.");

        string methodName = Regex.Match(signature, @"::([^(]+)\(").Groups[1].Value;
        var hooks = Strings(expected.GetProperty("forgeHooks"));
        var callers = Strings(expected.GetProperty("forgeCallers"));
        var ilHookKinds = ilHooks.TryGetValue((typeName, methodName), out var kindSet) ? kindSet.ToArray() : Array.Empty<string>();
        var ilCallers = ilCalls.TryGetValue(signature, out var callerSet) ? callerSet.ToArray() : Array.Empty<string>();
        Check("forge-use." + id, hooks.SequenceEqual(ilHookKinds) && callers.SequenceEqual(ilCallers),
            $"IL hooks [{string.Join(",", ilHookKinds)}], IL callers [{string.Join(",", ilCallers)}].");
        if (hooks.Length > 0)
        {
            if (!hookOwners.TryGetValue((typeName, methodName), out var owners)) hookOwners[(typeName, methodName)] = owners = new List<string>();
            owners.Add(id);
        }
        Count(hooks.Length > 0 ? "use:forge-hook" : callers.Length > 0 ? "use:forge-call" : "use:inventory");

        int index = 0;
        foreach (var data in expected.GetProperty("dataEvidence").EnumerateArray())
        {
            Shape(data, new[] { "file", "pointer" });
            string file = data.GetProperty("file").GetString()!;
            string pointer = data.GetProperty("pointer").GetString()!;
            Check($"data-evidence.{id}.{index++}", dataFiles.TryGetValue(file, out var dataDocument)
                && Resolves(dataDocument.RootElement, pointer.Split('.')), file + "#" + pointer);
        }
    }
    var unmatchedHooks = ilHooks.Keys.Where(k => !hookOwners.TryGetValue(k, out var owners) || owners.Count != 1).Select(k => k.Type + "::" + k.Name).ToArray();
    Check("forge-use.hook-coverage", unmatchedHooks.Length == 0, "Hooks without exactly one frozen owner: [" + string.Join(",", unmatchedHooks) + "]");
    var unfrozenCalls = ilCalls.Keys.Where(s => !frozen.Contains(s)).ToArray();
    Check("forge-use.call-coverage", unfrozenCalls.Length == 0, "Game APIs called by Forge but not frozen: [" + string.Join(",", unfrozenCalls) + "]");
    var missingStatic = NativeHealth.ReviewedRvas.Keys.Where(s => !staticNative.Contains(s)).ToArray();
    Check("evidence-level.static-native-coverage", missingStatic.Length == 0, "Decoded bodies not frozen as static-native: [" + string.Join(",", missingStatic) + "]");

    foreach (var expected in spec.GetProperty("enumConstants").EnumerateArray())
    {
        Shape(expected, new[] { "id", "assembly", "type", "name", "value" });
        var assembly = assemblies[expected.GetProperty("assembly").GetString()!];
        var type = assembly.MainModule.Types.SelectMany(Walk).SingleOrDefault(t => t.FullName == expected.GetProperty("type").GetString());
        var field = type is { IsEnum: true } ? type.Fields.SingleOrDefault(f => f.IsStatic && f.HasConstant && f.Name == expected.GetProperty("name").GetString()) : null;
        Check(expected.GetProperty("id").GetString()!, field != null && Convert.ToInt64(field.Constant) == expected.GetProperty("value").GetInt64(),
            field == null ? "enum constant missing" : "metadata value " + field.Constant);
        Count("enum-constants");
    }
    foreach (var expected in spec.GetProperty("absentDeclaredMethods").EnumerateArray())
    {
        Shape(expected, new[] { "id", "assembly", "type", "name" });
        var assembly = assemblies[expected.GetProperty("assembly").GetString()!];
        var type = assembly.MainModule.Types.SelectMany(Walk).Single(t => t.FullName == expected.GetProperty("type").GetString());
        Check(expected.GetProperty("id").GetString()!, !type.Methods.Any(m => m.Name == expected.GetProperty("name").GetString()),
            "No declaration on this type; inherited method existence does not prove an implemented receiver.");
    }
    NativeHealth.Verify(nativePath, nativeHash, Check);
}
catch (Exception error) { Check("audit.error", false, error.GetType().Name + ": " + error.Message); }
finally
{
    foreach (var assembly in assemblies.Values) assembly.Dispose();
    foreach (var data in dataFiles.Values) data.Dispose();
}
int failed = checks.Count(c => !c.Passed);
var report = new { schemaVersion = 2, verification = "metadata-static-native-and-forge-il", gameExecuted = false,
    forgeAssemblySha256 = forgeHash, utc = DateTimeOffset.UtcNow, distribution, passed = checks.Count - failed, failed, checks };
string reportPath = Path.GetFullPath(args[4]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Detail);
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {checks.Count - failed}/{checks.Count} static evidence checks; "
    + string.Join(", ", distribution.Select(d => d.Key + "=" + d.Value)) + "; game execution and multiplayer NOT tested.");
return failed == 0 ? 0 : 1;
internal sealed record EvidenceCheck(string Id, bool Passed, string Detail);
