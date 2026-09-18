using System.Runtime.CompilerServices;
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
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.Combine(args[0], "core"));
resolver.AddSearchDirectory(Path.Combine(args[0], "interop"));
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
var reader = new ReaderParameters { AssemblyResolver = resolver };
using var host = AssemblyDefinition.ReadAssembly(args[1], reader);
using var sdk = AssemblyDefinition.ReadAssembly(args[2], reader);
using var development = AssemblyDefinition.ReadAssembly(args[3], reader);
var interop = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
TypeDefinition[] GameTypes(string scope)
{
    if (!interop.TryGetValue(scope, out var assembly))
    {
        var path = Path.Combine(args[0], "interop", scope + ".dll");
        if (!File.Exists(path)) return Array.Empty<TypeDefinition>();
        interop[scope] = assembly = AssemblyDefinition.ReadAssembly(path, reader);
    }
    return assembly.MainModule.Types.SelectMany(Walk).ToArray();
}
TypeDefinition? Resolve(TypeReference type) => GameTypes(type.Scope.Name).SingleOrDefault(t => t.FullName == type.FullName);
var hostTypes = host.MainModule.Types.SelectMany(Walk).ToArray();
var types = development.MainModule.Types.SelectMany(Walk).ToArray();
bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
// Every member reference a type's own methods and its nested types' methods make. A nested type such as the session's
// segment writer is where the gzip path lives, so a check that only walked the top level would report a missing
// reference that is really there.
MethodReference[] Calls(params TypeDefinition[] roots) => roots.SelectMany(Walk)
    .SelectMany(t => t.Methods).Where(m => m.HasBody)
    .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
MethodReference[] CallsMethod(MethodDefinition method) => method.HasBody
    ? method.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray()
    : Array.Empty<MethodReference>();

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
    new object[] { "NAinfini.ForgeDevelopment", "Infini Forge Development", ReleaseVersion("NAinfini-ForgeDevelopment") }));
// Development pins the host version declared by the built Runtime plugin. It does not carry a second range
// policy beside the release set: changing the host version means changing this package's release contract too.
var hostIdentity = hostTypes.Single(t => t.FullName == "ForgeRuntime.Plugin")
    .CustomAttributes.Single(a => a.AttributeType.Name == "BepInPlugin").ConstructorArguments.Select(a => a.Value).ToArray();
Check("plugin.runtime-dependency", plugin.CustomAttributes.Where(a => a.AttributeType.Name == "BepInDependency")
    .Any(a => a.ConstructorArguments.Select(v => v.Value).SequenceEqual(new object[] { hostIdentity[0], ">=" + hostIdentity[2] })));
var calls = plugin.Methods.Single(m => m.Name == "Load").Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
Check("plugin.authoring-gate", calls.Any(m => m.DeclaringType.FullName == "ForgeRuntime.Plugin" && m.Name == "get_ConfiguredMode"));
Check("plugin.public-host-entry", calls.Any(m => m.DeclaringType.FullName == "ForgeRuntime.Plugin" && m.Name == "get_Runtime"));
Check("plugin.own-patch-set", calls.Any(m => m.DeclaringType.FullName == "HarmonyLib.Harmony" && m.Name == "PatchAll"));
Check("plugin.collector-loops", types.Where(t => t.Methods.Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"))
    .Select(t => t.Name).OrderBy(x => x).SequenceEqual(new[] { "AuthoringMonitor", "CaptureMonitor", "ExperimentRunner", "PerformanceMonitor" }));

var hooks = types.Where(Patch).ToArray();
// The hook set is this package's own patch classes, both the diagnostics and the capture ones, as a literal list: a
// new hook is a line someone had to write rather than a number that silently stopped meaning anything. The tracer
// contributes no [HarmonyPatch] type at all: it installs its patches at run time from
// BepInEx/config/ForgeDevelopment/trace/*.json, which the checks below assert instead of counting hooks.
string[] expectedHooks = {
    "CullingFailure", "CullingLifecycle", "DamageSample", "FactoryFinished", "FactoryStart",
    "GenerationJob", "LevelCleanup", "MarkerPlaced", "PrefabSpawned", "WeaponSample",
    // The capture's snapshot boundaries: each is declared on the game method that crosses it.
    "CheckpointRestored", "CheckpointStored", "DimensionWarped", "HostMigrated", "LevelEnded",
    "PlayerJoined", "PlayerLeft", "StateChanged" };
var hookNames = hooks.Select(t => t.Name).OrderBy(x => x).ToArray();
Check("development.hook-set", expectedHooks.All(hookNames.Contains)
    && hookNames.All(expectedHooks.Contains));
Check("development.one-patch-attribute-per-hook", hooks.All(t => t.CustomAttributes.Count(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch") == 1));
// A tracer patch is installed with HarmonyX's manual processor, so its prefix and postfix must be visible to the
// audit and its installer must be the one that reads the profile files.
var tracerPatches = types.Single(t => t.Name == "RecTracerPatches");
Check("development.tracer.manual-patch-pair", tracerPatches.Methods.Count(m => m.Name is "Prefix" or "Postfix") == 2
    && !Patch(tracerPatches));
var tracerRuntime = types.Single(t => t.Name == "RecTracerRuntime");
var runtimeCalls = Calls(tracerRuntime);
Check("development.tracer.config-driven-install", runtimeCalls.Any(m => m.DeclaringType.Name == "Harmony" && m.Name == "CreateProcessor")
    && runtimeCalls.Any(m => m.DeclaringType.Name == "RecTracer" && m.Name == "Parse"));
Check("development.tracer.runtime-switch", tracerRuntime.Methods.Any(m => m.Name == "SetProfileEnabled"));
var recRuntime = types.Single(t => t.Name == "RecRuntime");
Check("development.behavior-trace-subscription", Calls(recRuntime)
    .Any(m => m.DeclaringType.FullName == "ForgeRuntime.Framework.RuntimeKernel" && m.Name == "ObserveBehaviorTrace"));
var recForge = types.Single(t => t.Name == "RecForge");
var behaviorTraceWriter = recForge.Methods.Single(m => m.Name == "RecordBehaviorTrace");
Check("development.behavior-trace-recorder", CallsMethod(behaviorTraceWriter)
    .Any(m => m.DeclaringType.Name == "RecSession" && m.Name == "Write"));
var session = types.Single(t => t.Name == "RecSession");
var sessionCalls = Calls(session);
Check("development.recorder.segments", session.Methods.Any(m => m.Name == "Open") && session.Methods.Any(m => m.Name == "Stop")
    && sessionCalls.Any(m => m.DeclaringType.Name == "GZipStream"));
Check("development.recorder.generic-prefix-suffix", Calls(tracerPatches)
    .Any(m => m.DeclaringType.Name == "RecSession" && m.Name == "Write"));
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
// The version this audit compares against comes from the release identity (Release/release.json) next to this source
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
internal sealed record CheckRow(string Id, bool Passed);
