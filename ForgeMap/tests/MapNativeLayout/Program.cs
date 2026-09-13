using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Compiled-assembly metadata only: proves boundaries, plugin identity and hook shape, not native call timing.
if (args.Length != 7)
{
    Console.Error.WriteLine("Usage: MapNativeLayout <BepInEx> <sdk.dll> <host ForgeRuntime.dll> <map.dll> <map.native.dll> <hook-spec.json> <report.json>");
    return 2;
}
var checks = new List<CheckRow>();
void Check(string id, bool passed, string detail = "") => checks.Add(new(id, passed, detail));
IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); }
using var sdk = AssemblyDefinition.ReadAssembly(args[1]);
using var host = AssemblyDefinition.ReadAssembly(args[2]);
using var map = AssemblyDefinition.ReadAssembly(args[3]);
using var native = AssemblyDefinition.ReadAssembly(args[4]);
using var modules = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "Modules-ASM.dll"));
using var snet = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "SNet_ASM.dll"));
using var specDocument = JsonDocument.Parse(File.ReadAllText(args[5]));
var specHooks = specDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
var specReadbacks = specDocument.RootElement.GetProperty("readbacks").EnumerateArray().ToArray();
var nativeTypes = native.MainModule.Types.SelectMany(Walk).ToArray();
var mapTypes = map.MainModule.Types.SelectMany(Walk).ToArray();
var gameTypes = modules.MainModule.Types.Concat(snet.MainModule.Types).SelectMany(Walk).ToArray();
bool GameAssembly(string name) => name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("BepInEx", StringComparison.Ordinal)
    || name.Contains("Harmony", StringComparison.Ordinal) || name.StartsWith("Il2Cpp", StringComparison.Ordinal) || name.EndsWith("-ASM", StringComparison.Ordinal) || name.EndsWith("_ASM", StringComparison.Ordinal);
string[] Forge(AssemblyDefinition assembly) => assembly.MainModule.AssemblyReferences.Where(r => r.Name.StartsWith("Forge", StringComparison.Ordinal))
    .Select(r => r.FullName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
var instructions = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
var calls = instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
string Scope(MethodReference m) => m.DeclaringType.Scope is AssemblyNameReference scope ? scope.Name : "";
string Member(MethodReference m) => (m.DeclaringType is GenericInstanceType g ? g.ElementType.FullName : m.DeclaringType.FullName) + "::" + m.Name;

Check("map.game-independent", !map.MainModule.AssemblyReferences.Any(r => GameAssembly(r.Name)));
Check("map.only-sdk", Forge(map).SequenceEqual(new[] { sdk.Name.FullName }), string.Join(", ", Forge(map)));
Check("native.forge-references", Forge(native).SequenceEqual(new[] { sdk.Name.FullName, host.Name.FullName, map.Name.FullName }.OrderBy(x => x, StringComparer.Ordinal)),
    string.Join(", ", Forge(native)));
Check("host.no-map-reference", !Forge(host).Any(n => n.StartsWith("ForgeMap", StringComparison.Ordinal)), string.Join(", ", Forge(host)));
Check("native.no-embedded-kernel", !nativeTypes.Concat(mapTypes).Any(t => t.Name == "RuntimeKernel"));
Check("native.no-update-loop", !nativeTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"));

// One provider identity: the native plugin extends ForgeMap.ModuleDefinition instead of declaring a second registry.
Check("native.single-provider-source", calls.Any(m => Member(m) == "ForgeMap.ModuleDefinition::Create")
    && !instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference c && c.DeclaringType.FullName == "ForgeRuntime.Framework.RuntimeModule")
    && !instructions.Any(i => i.Operand is string s && s.Contains("forge.module.", StringComparison.Ordinal)));
Check("native.player-resolver-and-instance-lookup-without-observer", calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeModule::set_EntityResolvers")
    && calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeModule::set_EntityInstanceResolvers")
    && !calls.Any(m => m.Name == "set_EntityObservers") && instructions.Any(i => i.Operand is string s && s == "gtfo.player"));
// Only the spawn/despawn readback allocates lives; the SDK instance lookup reads the recorded table and nothing else.
var lookup = nativeTypes.Where(t => t.Name == "PlayerIdentityModule").SelectMany(t => t.Methods).SingleOrDefault(m => m.Name == "ResolveInstance");
var lookupCalls = lookup?.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray() ?? Array.Empty<MethodReference>();
Check("native.instance-lookup-never-allocates", lookup != null && lookup.Parameters.Count == 1 && lookup.Parameters[0].ParameterType.FullName == "System.Object"
    && !lookup.Body.Instructions.Any(i => i.OpCode.Code is Code.Stfld or Code.Stsfld)
    && !lookupCalls.Any(m => m.Name is "Reconcile" or "Add" or "Remove" or "Clear" or "get_Lookup" or "get_PlayerAgentsInLevel"),
    string.Join(", ", lookupCalls.Select(Member).Distinct(StringComparer.Ordinal)));
Check("native.no-gameplay-gate", !calls.Any(m => m.Name == "get_CanExecuteGameplay"));
// SNet_Player.Lookup is a Steam64 account ID: compared and used as a private dictionary key, never formatted or boxed.
Check("native.account-lookup-never-formatted", !calls.Any(m => m.DeclaringType.FullName == "System.UInt64" && m.Name == "ToString")
    && !instructions.Any(i => i.OpCode == OpCodes.Box && i.Operand is TypeReference t && t.FullName == "System.UInt64")
    && !calls.Any(m => m is GenericInstanceMethod g && g.GenericArguments.Any(a => a.FullName == "System.UInt64"))
    && !calls.Any(m => m.Parameters.Any(p => p.ParameterType.FullName == "System.UInt64") && m.DeclaringType.FullName is "System.String" or "System.Convert"));

// Native state is read, never written, and identity reads only the player key and agent links: no names or slots.
var gameCalls = calls.Where(m => GameAssembly(Scope(m)) && !Scope(m).StartsWith("BepInEx", StringComparison.Ordinal)
        && !Scope(m).Contains("Harmony", StringComparison.Ordinal))
    .Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
string[] queries = { "TryCast", "op_Equality", "op_Inequality", "op_Implicit" };
var writes = gameCalls.Where(c => { var name = c[(c.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
    return !name.StartsWith("get_", StringComparison.Ordinal) && !queries.Contains(name); }).ToArray();
Check("native.read-only-game-access", gameCalls.Length > 0 && writes.Length == 0, writes.Length == 0 ? string.Join(", ", gameCalls) : string.Join(", ", writes));
var identityReads = gameCalls.Where(c => c.StartsWith("Player.", StringComparison.Ordinal) || c.StartsWith("SNetwork.", StringComparison.Ordinal)).ToArray();
var expectedReads = specReadbacks.Select(r => { var s = r.GetProperty("signature").GetString()!; int start = s.IndexOf(' ') + 1;
        return s[start..s.IndexOf('(')]; })
    .Append("SNetwork.SNet::get_IsMaster").OrderBy(x => x, StringComparer.Ordinal).ToArray();
Check("native.exact-player-reads", identityReads.SequenceEqual(expectedReads), string.Join(", ", identityReads));
foreach (var readback in specReadbacks)
{
    string id = readback.GetProperty("id").GetString()!, signature = readback.GetProperty("signature").GetString()!;
    var found = gameTypes.Where(t => t.FullName == readback.GetProperty("type").GetString()).SelectMany(t => t.Methods).Where(m => m.FullName == signature).ToArray();
    Check("readback." + id + ".game-member", found.Length == 1 && found[0].IsStatic == readback.GetProperty("isStatic").GetBoolean(), signature);
}
var harmonyCalls = calls.Where(m => Scope(m).Contains("Harmony", StringComparison.Ordinal)).Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
Check("native.harmony-install-and-unpatch-only", harmonyCalls.All(c => c is "HarmonyLib.Harmony::.ctor" or "HarmonyLib.Harmony::CreateClassProcessor"
    or "HarmonyLib.PatchClassProcessor::Patch" or "HarmonyLib.Harmony::UnpatchSelf"), string.Join(", ", harmonyCalls));

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
    // Identity is read from the in-level list after the body; hook arguments are never consulted.
    Check("hook." + name + ".ignores-arguments", postfix != null && !postfix.Body.Instructions.Any(i => i.OpCode.Code is Code.Ldarg or Code.Ldarg_0
        or Code.Ldarg_1 or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S));
    var priority = postfix?.CustomAttributes.SingleOrDefault(a => a.AttributeType.Name == "HarmonyPriority");
    Check("hook." + name + ".priority-last", priority != null && priority.ConstructorArguments[0].Value is int value && value == 0);
    var postfixCalls = postfix?.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray() ?? Array.Empty<MethodReference>();
    Check("hook." + name + ".guarded-by-session", postfixCalls.Any(m => Member(m) == "ForgeMap.Native.Plugin::get_Session")
        && postfixCalls.Any(m => Member(m) == "ForgeMap.Native.MapPluginSession::Guard"));
}

var plugins = nativeTypes.Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin")).ToArray();
Check("plugin.single-entry", plugins.Length == 1 && plugins[0].FullName == "ForgeMap.Native.Plugin"
    && plugins[0].BaseType?.FullName == "BepInEx.Unity.IL2CPP.BasePlugin", string.Join(", ", plugins.Select(p => p.FullName)));
string[] Args(CustomAttribute a) => a.ConstructorArguments.Select(x => x.Value as string ?? "").ToArray();
var hostPlugin = host.MainModule.Types.SelectMany(Walk).Single(t => t.FullName == "ForgeRuntime.Plugin")
    .CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin");
if (plugins.Length == 1)
{
    var plugin = plugins[0];
    var identity = Args(plugin.CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
    Check("plugin.identity", identity.SequenceEqual(new[] { "NAinfini.ForgeMap", "Infini Forge Map", "0.1.0" })
        && map.Name.Version.ToString(3) == identity[2], string.Join(", ", identity) + "; ForgeMap " + map.Name.Version);
    var dependencies = plugin.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency").Select(Args).ToArray();
    Check("plugin.depends-on-host-only", dependencies.Length == 1 && dependencies[0].SequenceEqual(new[] { Args(hostPlugin)[0], Args(hostPlugin)[2] })
        && dependencies[0][0] == "NAinfini.ForgeRuntime", string.Join(" | ", dependencies.Select(d => string.Join(", ", d))));
    var load = plugin.Methods.Single(m => m.Name == "Load").Body.Instructions.ToList();
    int Index(string member) => load.FindIndex(i => i.Operand is MethodReference m && Member(m) == member);
    int off = Index("ForgeRuntime.Plugin::get_ConfiguredMode"), runtime = Index("ForgeRuntime.Plugin::get_Runtime"),
        harmony = load.FindIndex(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m && Member(m) == "HarmonyLib.Harmony::.ctor"),
        start = Index("ForgeMap.Native.MapPluginSession::Start");
    Check("plugin.off-gate-before-runtime-and-hooks", off >= 0 && off < runtime && runtime < harmony && harmony < start, $"{off} {runtime} {harmony} {start}");
    var unload = plugin.Methods.Single(m => m.Name == "Unload").Body.Instructions;
    Check("plugin.no-hot-unload", unload.Count == 2 && unload[0].OpCode == OpCodes.Ldc_I4_0 && unload[1].OpCode == OpCodes.Ret);
}
var report = new { verification = "compiled-assembly-metadata-only", gameExecuted = false,
    sdkSha256 = Hash(args[1]), hostSha256 = Hash(args[2]), mapSha256 = Hash(args[3]), nativeSha256 = Hash(args[4]),
    passed = checks.Count(c => c.Passed), failed = checks.Count(c => !c.Passed), checks };
string output = Path.GetFullPath(args[6]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Detail);
Console.WriteLine($"{(report.failed == 0 ? "PASS" : "FAIL")} {report.passed}/{checks.Count} Map native layout checks; no GTFO execution.");
return report.failed == 0 ? 0 : 1;
internal sealed record CheckRow(string Id, bool Passed, string Detail);
