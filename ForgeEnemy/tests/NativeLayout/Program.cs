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
Check("host.no-enemy-assembly-reference", !host.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("ForgeEnemy")));
Check("host.receiver-placement", hostTypes.Any(t => t.Name == "EnemyModule") != cutover);
Check("enemy.single-receiver", enemyTypes.Count(t => t.Name == "EnemyModule") == 1);
Check("enemy.no-embedded-kernel", !enemyTypes.Any(t => t.Name == "RuntimeKernel"));
Check("host.no-embedded-kernel", !hostTypes.Any(t => t.Name == "RuntimeKernel"));
Check("sdk.no-game-dependency", !sdk.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("Unity") || r.Name.StartsWith("BepInEx") || r.Name.StartsWith("ForgeEnemy")));
Check("enemy.only-host-and-sdk", enemy.MainModule.AssemblyReferences.Where(r => r.Name.StartsWith("Forge"))
    .Select(r => r.Name).OrderBy(x => x).SequenceEqual(new[] { "ForgeRuntime", "ForgeRuntime.Framework" }));
Check("enemy.same-sdk-identity", enemy.MainModule.AssemblyReferences.Single(r => r.Name == "ForgeRuntime.Framework").FullName == sdk.Name.FullName);
Check("enemy.five-hooks", nativeHooks.Select(t => t.Name).OrderBy(x => x)
    .SequenceEqual(new[] { "EnemyDamage", "EnemyDeathStarted", "EnemyDespawned", "EnemyLimbBroken", "EnemySpawned" }));
string[] expectedWorld = { "FrameworkCheckpointRestore", "FrameworkSessionReset", "FrameworkStateChanged", "FrameworkWorldCleanup" };
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
        Check("native.instance." + hook.Name + "." + method.Name,
            method.Parameters.Single(p => p.Name == "__instance").ParameterType.FullName == type.FullName);
    }
}
var plugin = enemyTypes.Single(t => t.FullName == "ForgeEnemy.Native.Plugin");
var identity = plugin.CustomAttributes.Single(a => a.AttributeType.Name == "BepInPlugin");
Check("plugin.identity", identity.ConstructorArguments.Select(a => a.Value).SequenceEqual(
    new object[] { "NAinfini.ForgeEnemy", "Infini Forge Enemy", "1.0.0" }));
var dependency = plugin.CustomAttributes.Single(a => a.AttributeType.Name == "BepInDependency");
Check("plugin.runtime-dependency", dependency.ConstructorArguments.Select(a => a.Value)
    .SequenceEqual(new object[] { "NAinfini.ForgeRuntime", "1.2.0" }));
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
    passed = checks.Count(c => c.Passed), failed = checks.Count(c => !c.Passed), checks };
string output = Path.GetFullPath(args[5]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id);
Console.WriteLine($"{(report.failed == 0 ? "PASS" : "FAIL")} {report.passed}/{checks.Count} native module layout checks ({args[4]}); no GTFO execution.");
return report.failed == 0 ? 0 : 1;
internal sealed record CheckRow(string Id, bool Passed);
