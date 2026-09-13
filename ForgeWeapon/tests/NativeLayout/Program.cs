using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;

// Compiled-assembly metadata only: proves boundaries and hook shape, not native call timing.
if (args.Length != 6)
{
    Console.Error.WriteLine("Usage: NativeLayout <BepInEx> <sdk.dll> <weapon.dll> <weapon.native.dll> <hook-spec.json> <report.json>");
    return 2;
}
var checks = new List<CheckRow>();
void Check(string id, bool passed, string detail = "") => checks.Add(new(id, passed, detail));
IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); }
using var sdk = AssemblyDefinition.ReadAssembly(args[1]);
using var weapon = AssemblyDefinition.ReadAssembly(args[2]);
using var native = AssemblyDefinition.ReadAssembly(args[3]);
using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "Modules-ASM.dll"));
using var specDocument = JsonDocument.Parse(File.ReadAllText(args[4]));
var specHooks = specDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
var nativeTypes = native.MainModule.Types.SelectMany(Walk).ToArray();
var weaponTypes = weapon.MainModule.Types.SelectMany(Walk).ToArray();
var gameTypes = game.MainModule.Types.SelectMany(Walk).ToArray();
bool GameAssembly(string name) => name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("BepInEx", StringComparison.Ordinal)
    || name.Contains("Harmony", StringComparison.Ordinal) || name.StartsWith("Il2Cpp", StringComparison.Ordinal) || name.EndsWith("-ASM", StringComparison.Ordinal);
string[] Forge(AssemblyDefinition assembly) => assembly.MainModule.AssemblyReferences.Where(r => r.Name.StartsWith("Forge", StringComparison.Ordinal))
    .Select(r => r.FullName).OrderBy(x => x, StringComparer.Ordinal).ToArray();

Check("weapon.game-independent", !weapon.MainModule.AssemblyReferences.Any(r => GameAssembly(r.Name)));
Check("weapon.only-sdk", Forge(weapon).SequenceEqual(new[] { sdk.Name.FullName }), string.Join(", ", Forge(weapon)));
Check("native.only-sdk-and-weapon", Forge(native).SequenceEqual(new[] { sdk.Name.FullName, weapon.Name.FullName }.OrderBy(x => x, StringComparer.Ordinal)),
    string.Join(", ", Forge(native)));
Check("native.no-loader-entry", !native.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("BepInEx", StringComparison.Ordinal))
    && !nativeTypes.Any(t => t.CustomAttributes.Any(a => a.AttributeType.Name is "BepInPlugin" or "BepInDependency")));
Check("native.no-embedded-kernel", !nativeTypes.Concat(weaponTypes).Any(t => t.Name == "RuntimeKernel"));
Check("native.single-session-over-weapon-identity", nativeTypes.Count(t => t.Name == "WeaponNativeSession") == 1
    && !nativeTypes.Any(t => t.Name is "EquipmentIdentitySession" or "EquipmentIdentityIndex"));
Check("native.no-update-loop", !nativeTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"));

// Native state is read, never written: every game member used by the adapter is a getter or a named pure query.
string[] queries = { "TryGetBackpack", "GetChecksum", "TryCast", "op_Equality", "op_Inequality", "op_Implicit" };
var gameCalls = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Select(i => i.Operand).OfType<MethodReference>()
    .Where(m => m.DeclaringType.Scope is AssemblyNameReference scope && GameAssembly(scope.Name) && !scope.Name.Contains("Harmony", StringComparison.Ordinal))
    .Select(m => m.DeclaringType.FullName + "::" + m.Name).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
var writes = gameCalls.Where(c => { var name = c[(c.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
    return !name.StartsWith("get_", StringComparison.Ordinal) && !queries.Contains(name); }).ToArray();
Check("native.read-only-game-access", gameCalls.Length > 0 && writes.Length == 0, writes.Length == 0 ? string.Join(", ", gameCalls) : string.Join(", ", writes));

bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
var hooks = nativeTypes.Where(Patch).ToArray();
Check("native.exact-hook-set", hooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal)
    .SequenceEqual(specHooks.Select(h => h.GetProperty("hook").GetString()!).OrderBy(x => x, StringComparer.Ordinal)),
    string.Join(", ", hooks.Select(t => t.Name)));
foreach (var spec in specHooks)
{
    string name = spec.GetProperty("hook").GetString()!;
    var hook = hooks.SingleOrDefault(t => t.Name == name);
    if (hook == null) { Check("hook." + name + ".present", false); continue; }
    var patch = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
    var type = patch.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
    var method = patch.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
    Check("hook." + name + ".declared-target", type.FullName == spec.GetProperty("type").GetString() && method == spec.GetProperty("name").GetString(),
        type.FullName + "::" + method);
    var targets = gameTypes.Where(t => t.FullName == type.FullName).SelectMany(t => t.Methods.Where(m => m.Name == method)).ToArray();
    Check("hook." + name + ".unique-game-method", targets.Length == 1 && targets[0].FullName == spec.GetProperty("signature").GetString()
        && targets[0].IsVirtual == spec.GetProperty("isVirtual").GetBoolean(), string.Join(" | ", targets.Select(m => m.FullName)));
    var patches = hook.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Namespace == "HarmonyLib"
        && a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyTranspiler" or "HarmonyFinalizer")).ToArray();
    var postfix = patches.Length == 1 && patches[0].CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix") ? patches[0] : null;
    Check("hook." + name + ".static-postfix-only", postfix is { IsStatic: true });
    Check("hook." + name + ".instance-type", postfix != null && postfix.Parameters.Count == 1 && postfix.Parameters[0].Name == "__instance"
        && postfix.Parameters[0].ParameterType.FullName == type.FullName);
    var priority = postfix?.CustomAttributes.SingleOrDefault(a => a.AttributeType.Name == "HarmonyPriority");
    Check("hook." + name + ".priority-last", priority != null && priority.ConstructorArguments[0].Value is int value && value == 0);
    var calls = postfix?.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray() ?? Array.Empty<MethodReference>();
    Check("hook." + name + ".guarded-by-session", calls.Any(m => m.DeclaringType.Name == "WeaponNativeSession" && m.Name == "get_Current")
        && calls.Any(m => m.DeclaringType.Name == "WeaponNativeSession" && m.Name == "Guard"));
}
var report = new { verification = "compiled-assembly-metadata-only", gameExecuted = false,
    sdkSha256 = Hash(args[1]), weaponSha256 = Hash(args[2]), nativeSha256 = Hash(args[3]),
    passed = checks.Count(c => c.Passed), failed = checks.Count(c => !c.Passed), checks };
string output = Path.GetFullPath(args[5]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Detail);
Console.WriteLine($"{(report.failed == 0 ? "PASS" : "FAIL")} {report.passed}/{checks.Count} Weapon native layout checks; no GTFO execution.");
return report.failed == 0 ? 0 : 1;
internal sealed record CheckRow(string Id, bool Passed, string Detail);
