using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
if (args.Length != 6 || args[4] is not ("pending" or "cutover"))
{ Console.Error.WriteLine("Usage: NativeLayout <BepInEx> <host.dll> <sdk.dll> <enemy.dll> pending|cutover <report.json>"); return 2; }
var checks = new List<CheckRow>();
void Check(string id, bool passed) => checks.Add(new(id, passed));
IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
string Hash(string p) { using var s = File.OpenRead(p); using var h = SHA256.Create(); return Convert.ToHexString(h.ComputeHash(s)); }
using var host = AssemblyDefinition.ReadAssembly(args[1]);
using var sdk = AssemblyDefinition.ReadAssembly(args[2]);
using var enemy = AssemblyDefinition.ReadAssembly(args[3]);
using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "Modules-ASM.dll"));
var hostTypes = host.MainModule.Types.SelectMany(Walk).ToArray();
var enemyTypes = enemy.MainModule.Types.SelectMany(Walk).ToArray();
var gameTypes = game.MainModule.Types.SelectMany(Walk).ToArray();
bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
var nativeHooks = enemyTypes.Where(Patch).ToArray();
var worldHooks = hostTypes.Where(t => Patch(t) && t.Namespace == "ForgeRuntime.GameBindings").ToArray();
bool cutover = args[4] == "cutover";
// The hook roster comes from the package's own install table source, never from a list written here: the table is
// the one place the hook families are declared, and a hand-kept roster is a second statement that drifts the first
// time a family gains a patch class.
var nativeSource = Path.Combine(RepoRoot(), "ForgeEnemy", "Native");
var tableFamilyTypes = TableFamilyTypes(File.ReadAllText(Path.Combine(nativeSource, "EnemyHookInstall.cs")));
var declaredHooks = tableFamilyTypes.Select(family => new HookFamily(family, FamilyTypes(nativeSource, family))).ToArray();
var expectedHooks = declaredHooks.SelectMany(family => family.Members).OrderBy(x => x, StringComparer.Ordinal).ToArray();
Check("enemy.hook-table-lists-every-family", tableFamilyTypes.Length > 0 && declaredHooks.All(family => family.Members.Length > 0));
Check("host.no-enemy-assembly-reference", !host.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("ForgeEnemy")));
Check("host.receiver-placement", hostTypes.Any(t => t.Name == "EnemyModule") != cutover);
Check("enemy.single-receiver", enemyTypes.Count(t => t.Name == "EnemyModule") == 1);
Check("enemy.no-embedded-kernel", !enemyTypes.Any(t => t.Name == "RuntimeKernel"));
Check("host.no-embedded-kernel", !hostTypes.Any(t => t.Name == "RuntimeKernel"));
Check("sdk.no-game-dependency", !sdk.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("Unity") || r.Name.StartsWith("BepInEx") || r.Name.StartsWith("ForgeEnemy")));
Check("enemy.only-host-and-sdk", enemy.MainModule.AssemblyReferences.Where(r => r.Name.StartsWith("Forge"))
    .Select(r => r.Name).OrderBy(x => x).SequenceEqual(new[] { "ForgeRuntime", "ForgeRuntime.Framework" }));
Check("enemy.same-sdk-identity", enemy.MainModule.AssemblyReferences.Single(r => r.Name == "ForgeRuntime.Framework").FullName == sdk.Name.FullName);
// The one assertion the install table exists for, and it is an exact set: every class that carries a patch is a
// class the table installs, and the table declares no class the assembly does not ship.
Check("enemy.hooks", nativeHooks.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() == nativeHooks.Length
    && nativeHooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expectedHooks));
string[] expectedWorld = { "FrameworkCheckpointRestore" };
var expected = cutover ? expectedWorld : expectedWorld.Concat(new[] { "FrameworkEnemySpawned", "FrameworkEnemyDespawned", "FrameworkEnemyDamage" });
Check("host.exact-hook-set", worldHooks.Select(t => t.Name).OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)));
foreach (var hook in nativeHooks)
{
    var target = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
    var type = target.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
    var name = target.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
    var actualTypes = gameTypes.Where(t => t.FullName == type.FullName).ToArray();
    Check("native.target." + hook.Name, actualTypes.Length == 1
        && actualTypes[0].Methods.Count(m => m.Name == name) == 1);
    foreach (var method in hook.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix")))
    {
        Check("native.static." + hook.Name + "." + method.Name, method.IsStatic);
        // A hook either patches an instance method and names its receiver, or patches a static one and has no
        // `__instance` at all; the parameter it does carry has to be the target type, never a lookalike.
        var receiver = method.Parameters.SingleOrDefault(p => p.Name == "__instance");
        Check("native.instance." + hook.Name + "." + method.Name,
            receiver == null || receiver.ParameterType.FullName == type.FullName);
    }
}
var plugin = enemyTypes.Single(t => t.FullName == "ForgeEnemy.Native.Plugin");
var identity = plugin.CustomAttributes.Single(a => a.AttributeType.Name == "BepInPlugin");
Check("plugin.identity", identity.ConstructorArguments.Select(a => a.Value).SequenceEqual(
    new object[] { "NAinfini.ForgeEnemy", "Infini Forge Enemy", ReleaseVersion("NAinfini-ForgeEnemy") }));
// Two declared dependencies: the host it registers through, and GTFO-API for GameDataAPI.OnGameDataInitialized.
// Their versions come from the built host and from the release identity, so neither is pinned here, and each is a
// minimum version: the shipped loader parses the literal as a SemVer range, so it carries the `>=` floor.
var hostIdentity = hostTypes.Single(t => t.FullName == "ForgeRuntime.Plugin")
    .CustomAttributes.Single(a => a.AttributeType.Name == "BepInPlugin").ConstructorArguments.Select(a => a.Value).ToArray();
var dependencies = plugin.CustomAttributes.Where(a => a.AttributeType.Name == "BepInDependency")
    .Select(a => a.ConstructorArguments.Select(x => x.Value).ToArray()).ToArray();
Check("plugin.dependencies", dependencies.Length == 2
    && dependencies[0].SequenceEqual(new object[] { hostIdentity[0], ">=" + hostIdentity[2] })
    && dependencies[1].SequenceEqual(new object[] { "dev.gtfomodding.gtfo-api", ">=" + ProvidedVersion("dev.gtfomodding.gtfo-api") }));
var load = plugin.Methods.Single(m => m.Name == "Load");
var calls = load.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
Check("plugin.public-host-entry", calls.Any(m => m.DeclaringType.FullName == "ForgeRuntime.Plugin" && m.Name == "get_Runtime"));
Check("plugin.off-gate", calls.Any(m => m.DeclaringType.FullName == "ForgeRuntime.Plugin" && m.Name == "get_ConfiguredMode"));
Check("plugin.tested-session", calls.Any(m => m.DeclaringType.Name == "EnemyPluginSession" && m.Name == "Start"));
Check("plugin.no-second-update-loop", !enemyTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"));
Check("host.no-implicit-enemy", !cutover || !hostTypes.Single(t => t.Name == "GameRuntimeBridge").Methods
    .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>()
    .Any(m => m.DeclaringType.Name == "EnemyModule"));
var report = new { verification = "compiled-assembly-metadata-only", mode = args[4], gameExecuted = false,
    hostSha256 = Hash(args[1]), sdkSha256 = Hash(args[2]), enemySha256 = Hash(args[3]),
    hookClasses = nativeHooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
    declaredHookFamilies = declaredHooks.Select(family => family.Type).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
    passed = checks.Count(c => c.Passed), failed = checks.Count(c => !c.Passed), checks };
string output = Path.GetFullPath(args[5]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id);
Console.WriteLine($"{(report.failed == 0 ? "PASS" : "FAIL")} {report.passed}/{checks.Count} native module layout checks ({args[4]}); no GTFO execution.");
return report.failed == 0 ? 0 : 1;
// The versions this audit compares against come from the release identity (Release/release.json) next to this source
// file; nothing here is a copy, and the lookup does not depend on the working directory.
static JsonDocument ReleaseDocument([CallerFilePath] string source = "")
{
    for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
    {
        var path = Path.Combine(directory.FullName, "Release", "release.json");
        if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllText(path));
    }
    throw new InvalidOperationException("Release/release.json was not found above " + source + "; the audit must run from a repository checkout.");
}
static string ReleaseVersion(string packageName)
{
    using var release = ReleaseDocument();
    return release.RootElement.GetProperty("packages").EnumerateArray()
        .Single(p => p.GetProperty("packageName").GetString() == packageName).GetProperty("version").GetString()!;
}
static string ProvidedVersion(string providerGuid)
{
    using var release = ReleaseDocument();
    return release.RootElement.GetProperty("baseDependencies").EnumerateArray().Where(d => d.TryGetProperty("provides", out _))
        .SelectMany(d => d.GetProperty("provides").EnumerateArray()).Select(p => p.GetString()!.Split('@'))
        .Single(parts => parts[0] == providerGuid)[1];
}
/// <summary>The repository root above this source file, found the way the release-identity lookup below finds
/// `Release/`: the install table the hook roster is read from lives in the provider's own source tree, so the
/// audit has to name the checkout it is running in rather than a path relative to the working directory.</summary>
static string RepoRoot([CallerFilePath] string source = "")
{
    for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
        if (Directory.Exists(Path.Combine(directory.FullName, "ForgeEnemy", "Native"))) return directory.FullName;
    throw new InvalidOperationException("ForgeEnemy/Native was not found above " + source + "; the audit must run from a repository checkout.");
}

/// <summary>The `typeof` names of one already-extracted declaration, in the order they were written.</summary>
static string[] TypeofNames(string body)
    => System.Text.RegularExpressions.Regex.Matches(body, @"typeof\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)")
        .Select(match => match.Groups[1].Value).ToArray();

/// <summary>The family class names the install table's own list names, read from it: the list holds one
/// `<c>XxxHooks.Types</c> <em>member access</em> per family — the union the install loop walks — so the class is
/// the qualifier of each entry. The list is the roster the assembly has to match; the per-family `Types` array is
/// what the table names, not a second list kept here.</summary>
static string[] TableFamilyTypes(string text)
{
    var index = IndexOfArrayAfter(text, "Families");
    if (index < 0) throw new InvalidOperationException("No array initializer for Families.");
    return System.Text.RegularExpressions.Regex
        .Matches(ArrayBody(text, index), @"([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*Types\b")
        .Select(match => match.Groups[1].Value).ToArray();
}

/// <summary>One family's declared class list, keyed by the class the install table names. Each entry is a
/// `<c>typeof(...)</c>` of that family's own `Types` array, in declaration order.</summary>
static string[] FamilyTypes(string nativeDirectory, string family)
{
    var path = Path.Combine(nativeDirectory, family + ".cs");
    if (!File.Exists(path)) throw new InvalidOperationException("No hook family source " + path + ".");
    var text = File.ReadAllText(path);
    var index = IndexOfArrayAfter(text, "Types");
    if (index < 0) throw new InvalidOperationException("No Types array in " + path + ".");
    return TypeofNames(ArrayBody(text, index));
}

/// <summary>The offset of the `{` that opens the array initializer a named field is assigned, or -1 when the file
/// declares no such field. The one declaration shape both the families and the install table use is
/// `... <field> ... = new[] { ... }` or `= Array.AsReadOnly(new[] { ... })`, so the initializer is the first brace
/// after the field's own assignment.</summary>
static int IndexOfArrayAfter(string text, string field)
{
    var name = System.Text.RegularExpressions.Regex.Match(text, @"\b" + field + @"\b");
    if (!name.Success) return -1;
    var assignment = text.IndexOf('=', name.Index + field.Length);
    if (assignment < 0) return -1;
    var open = text.IndexOf('{', assignment);
    return open < 0 ? -1 : open;
}

/// <summary>The text between the braces at <paramref name="open"/>, brace-balanced so a nested initializer cannot
/// end the array early.</summary>
static string ArrayBody(string text, int open)
{
    int depth = 0;
    for (int index = open; index < text.Length; index++)
    {
        if (text[index] == '{') depth++;
        else if (text[index] == '}' && --depth == 0) return text[(open + 1)..index];
    }
    throw new InvalidOperationException("Unbalanced array initializer.");
}

internal sealed record CheckRow(string Id, bool Passed);
internal sealed record HookFamily(string Type, string[] Members);
