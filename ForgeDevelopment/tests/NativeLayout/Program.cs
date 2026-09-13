using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
if (args.Length != 5)
{ Console.Error.WriteLine("Usage: DevelopmentNativeLayout <BepInEx> <host.dll> <sdk.dll> <development.dll> <report.json>"); return 2; }
var checks = new List<CheckRow>();
void Check(string id, bool passed) => checks.Add(new(id, passed));
IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
string Hash(string p) { using var s = File.OpenRead(p); using var h = SHA256.Create(); return Convert.ToHexString(h.ComputeHash(s)); }
using var host = AssemblyDefinition.ReadAssembly(args[1]);
using var sdk = AssemblyDefinition.ReadAssembly(args[2]);
using var development = AssemblyDefinition.ReadAssembly(args[3]);
var interop = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
TypeDefinition[] GameTypes(string scope)
{
    if (!interop.TryGetValue(scope, out var assembly))
    {
        var path = Path.Combine(args[0], "interop", scope + ".dll");
        if (!File.Exists(path)) return Array.Empty<TypeDefinition>();
        interop[scope] = assembly = AssemblyDefinition.ReadAssembly(path);
    }
    return assembly.MainModule.Types.SelectMany(Walk).ToArray();
}
TypeDefinition? Resolve(TypeReference type) => GameTypes(type.Scope.Name).SingleOrDefault(t => t.FullName == type.FullName);
var hostTypes = host.MainModule.Types.SelectMany(Walk).ToArray();
var types = development.MainModule.Types.SelectMany(Walk).ToArray();
bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");

string[] diagnostics = { "RuntimeDiagnostics", "AuthoringMonitor", "PerformanceMonitor", "PerformanceDiagnostics", "DiagnosticsReport", "TelemetryBridge", "Settings" };
Check("host.no-diagnostics", !hostTypes.Any(t => diagnostics.Contains(t.Name)));
Check("host.no-development-reference", !host.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("ForgeDevelopment")));
Check("development.single-implementation", diagnostics.All(name => types.Count(t => t.Name == name) == 1));
Check("development.only-host-and-sdk", development.MainModule.AssemblyReferences.Where(r => r.Name.StartsWith("Forge"))
    .Select(r => r.Name).OrderBy(x => x).SequenceEqual(new[] { "ForgeRuntime", "ForgeRuntime.Framework" }));
Check("development.same-sdk-identity", development.MainModule.AssemblyReferences.Single(r => r.Name == "ForgeRuntime.Framework").FullName == sdk.Name.FullName);
Check("development.no-embedded-kernel", !types.Any(t => t.Name == "RuntimeKernel"));

var plugin = types.Single(t => t.FullName == "ForgeDevelopment.Native.Plugin");
var identity = plugin.CustomAttributes.Single(a => a.AttributeType.Name == "BepInPlugin");
Check("plugin.identity", identity.ConstructorArguments.Select(a => a.Value).SequenceEqual(
    new object[] { "NAinfini.ForgeDevelopment", "Infini Forge Development", "1.0.0" }));
Check("plugin.runtime-dependency", plugin.CustomAttributes.Where(a => a.AttributeType.Name == "BepInDependency")
    .Any(a => a.ConstructorArguments.Select(v => v.Value).SequenceEqual(new object[] { "NAinfini.ForgeRuntime", "1.2.0" })));
var calls = plugin.Methods.Single(m => m.Name == "Load").Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
Check("plugin.authoring-gate", calls.Any(m => m.DeclaringType.FullName == "ForgeRuntime.Plugin" && m.Name == "get_ConfiguredMode"));
Check("plugin.public-host-entry", calls.Any(m => m.DeclaringType.FullName == "ForgeRuntime.Plugin" && m.Name == "get_Runtime"));
Check("plugin.own-patch-set", calls.Any(m => m.DeclaringType.FullName == "HarmonyLib.Harmony" && m.Name == "PatchAll"));
Check("plugin.collector-loops", types.Where(t => t.Methods.Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"))
    .Select(t => t.Name).OrderBy(x => x).SequenceEqual(new[] { "AuthoringMonitor", "PerformanceMonitor" }));

var hooks = types.Where(Patch).ToArray();
Check("development.exact-hook-set", hooks.Select(t => t.Name).OrderBy(x => x).SequenceEqual(new[] {
    "CullingFailure", "CullingLifecycle", "DamageSample", "FactoryFinished", "FactoryStart", "GenerationJob",
    "LevelCleanup", "MarkerPlaced", "PrefabSpawned", "WeaponSample" }));
foreach (var hook in hooks)
{
    var arguments = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch").ConstructorArguments.Select(a => a.Value).ToArray();
    var target = arguments.OfType<TypeReference>().SingleOrDefault();
    if (target != null)
    {
        var name = arguments.OfType<string>().Single();
        var resolved = Resolve(target);
        Check("native.target." + hook.Name, resolved != null && resolved.Methods.Count(m => m.Name == name) == 1);
        continue;
    }
    // TargetMethods hooks: every game type the explicit audit list names must exist in this game build.
    var discovery = hook.Methods.Single(m => m.Name == "TargetMethods");
    var named = discovery.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldtoken).Select(i => i.Operand).OfType<TypeReference>().ToArray();
    Check("native.discovery." + hook.Name, named.Length > 0 && named.All(t => Resolve(t) != null));
}

var report = new { verification = "compiled-assembly-metadata-only", gameExecuted = false,
    hostSha256 = Hash(args[1]), sdkSha256 = Hash(args[2]), developmentSha256 = Hash(args[3]),
    passed = checks.Count(c => c.Passed), failed = checks.Count(c => !c.Passed), checks };
string output = Path.GetFullPath(args[4]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id);
Console.WriteLine($"{(report.failed == 0 ? "PASS" : "FAIL")} {report.passed}/{checks.Count} Development native layout checks; no GTFO execution.");
foreach (var assembly in interop.Values) assembly.Dispose();
return report.failed == 0 ? 0 : 1;
internal sealed record CheckRow(string Id, bool Passed);
