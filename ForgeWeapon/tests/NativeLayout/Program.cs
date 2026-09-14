using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Compiled-assembly metadata only: proves boundaries, plugin identity and hook shape, not native call timing.
if (args.Length != 8)
{
    Console.Error.WriteLine("Usage: NativeLayout <BepInEx> <sdk.dll> <host ForgeRuntime.dll> <map.native.dll> <weapon.dll> <weapon.native.dll> <hook-spec.json> <report.json>");
    return 2;
}
var checks = new List<CheckRow>();
void Check(string id, bool passed, string detail = "") => checks.Add(new(id, passed, detail));
IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); }
using var sdk = AssemblyDefinition.ReadAssembly(args[1]);
using var host = AssemblyDefinition.ReadAssembly(args[2]);
using var mapNative = AssemblyDefinition.ReadAssembly(args[3]);
using var weapon = AssemblyDefinition.ReadAssembly(args[4]);
using var native = AssemblyDefinition.ReadAssembly(args[5]);
using var modules = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "Modules-ASM.dll"));
using var snet = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "SNet_ASM.dll"));
using var specDocument = JsonDocument.Parse(File.ReadAllText(args[6]));
var specHooks = specDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
var nativeTypes = native.MainModule.Types.SelectMany(Walk).ToArray();
var weaponTypes = weapon.MainModule.Types.SelectMany(Walk).ToArray();
var gameTypes = modules.MainModule.Types.Concat(snet.MainModule.Types).SelectMany(Walk).ToArray();
bool GameAssembly(string name) => name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("BepInEx", StringComparison.Ordinal)
    || name.Contains("Harmony", StringComparison.Ordinal) || name.StartsWith("Il2Cpp", StringComparison.Ordinal) || name.EndsWith("-ASM", StringComparison.Ordinal) || name.EndsWith("_ASM", StringComparison.Ordinal);
string[] Forge(AssemblyDefinition assembly) => assembly.MainModule.AssemblyReferences.Where(r => r.Name.StartsWith("Forge", StringComparison.Ordinal))
    .Select(r => r.FullName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
var instructions = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
var calls = instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
string Scope(MethodReference m) => m.DeclaringType.Scope is AssemblyNameReference scope ? scope.Name : "";
string Member(MethodReference m) => (m.DeclaringType is GenericInstanceType g ? g.ElementType.FullName : m.DeclaringType.FullName) + "::" + m.Name;

Check("weapon.game-independent", !weapon.MainModule.AssemblyReferences.Any(r => GameAssembly(r.Name)));
Check("weapon.only-sdk", Forge(weapon).SequenceEqual(new[] { sdk.Name.FullName }), string.Join(", ", Forge(weapon)));
Check("native.forge-references", Forge(native).SequenceEqual(new[] { sdk.Name.FullName, host.Name.FullName, weapon.Name.FullName }.OrderBy(x => x, StringComparer.Ordinal)),
    string.Join(", ", Forge(native)));
Check("native.no-map-reference", !native.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("ForgeMap", StringComparison.Ordinal)));
Check("native.no-embedded-kernel", !nativeTypes.Concat(weaponTypes).Any(t => t.Name == "RuntimeKernel"));
Check("native.single-session-over-weapon-identity", nativeTypes.Count(t => t.Name == "WeaponNativeSession") == 1
    && !nativeTypes.Any(t => t.Name is "EquipmentIdentitySession" or "EquipmentIdentityIndex"));
Check("native.no-update-loop", !nativeTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"));

// Owners come only from the player-owning domain through the SDK: no injected or cached player identity, no account key.
Check("native.owner-through-sdk-player-lookup", calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeKernel::ResolveEntityInstance")
    && calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeKernel::IsEntityCurrent")
    && instructions.Any(i => i.Operand is string s && s == "gtfo.player")
    && !nativeTypes.Any(t => t.Name == "WeaponPlayerReferences")
    && !calls.Any(m => m.Name is "set_EntityResolvers" or "set_EntityInstanceResolvers" or "set_EntityObservers"));
Check("native.no-account-lookup", !calls.Any(m => m.Name == "get_Lookup"));

// Native state is read, never written: every game member used is a getter or a named pure query, pinned exactly.
var gameCalls = calls.Where(m => GameAssembly(Scope(m)) && !Scope(m).StartsWith("BepInEx", StringComparison.Ordinal)
        && !Scope(m).Contains("Harmony", StringComparison.Ordinal))
    .Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
string[] queries = { "TryGetBackpack", "IsDeployed", "GetChecksum", "TryCast", "op_Equality", "op_Inequality", "op_Implicit" };
var writes = gameCalls.Where(c => { var name = c[(c.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
    return !name.StartsWith("get_", StringComparison.Ordinal) && !queries.Contains(name); }).ToArray();
Check("native.read-only-game-access", gameCalls.Length > 0 && writes.Length == 0, writes.Length == 0 ? string.Join(", ", gameCalls) : string.Join(", ", writes));
// Pinned from the reviewed build: any new native read, including an account lookup, must be reviewed here first.
string[] expectedReads =
{
    "Gear.GearIDRange::GetChecksum",
    "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1::get_Item",
    "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1::get_Length",
    "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase::TryCast",
    "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase::get_Pointer",
    "Player.BackpackItem::get_GearIDRange",
    "Player.BackpackItem::get_Instance",
    "Player.BackpackItem::get_IsLoaded",
    "Player.BackpackItem::get_ItemID",
    "Player.PlayerAgent::get_Inventory",
    "Player.PlayerAgent::get_Owner",
    "Player.PlayerBackpack::IsDeployed",
    "Player.PlayerBackpack::get_Owner",
    "Player.PlayerBackpack::get_Slots",
    "Player.PlayerBackpackManager::TryGetBackpack",
    "PlayerInventoryBase::get_Owner",
    "PlayerInventoryBase::get_WieldedItem",
    "SNetwork.SNet::get_IsMaster",
    "SNetwork.SNet_Player::get_HasPlayerAgent",
    "SNetwork.SNet_Player::get_PlayerAgent",
    "UnityEngine.Object::op_Equality",
    "UnityEngine.Object::op_Inequality"
};
Check("native.exact-game-reads", gameCalls.SequenceEqual(expectedReads, StringComparer.Ordinal), string.Join(", ", gameCalls));
var snetReads = gameCalls.Where(c => c.StartsWith("SNetwork.", StringComparison.Ordinal)).ToArray();
Check("native.snet-read-only", snetReads.Length > 0 && snetReads.All(c => c.Contains("::get_", StringComparison.Ordinal)), string.Join(", ", snetReads));
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
    var priority = postfix?.CustomAttributes.SingleOrDefault(a => a.AttributeType.Name == "HarmonyPriority");
    Check("hook." + name + ".priority-last", priority != null && priority.ConstructorArguments[0].Value is int value && value == 0);
    var postfixCalls = postfix?.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray() ?? Array.Empty<MethodReference>();
    Check("hook." + name + ".guarded-by-session", postfixCalls.Any(m => m.DeclaringType.Name == "WeaponNativeSession" && m.Name == "get_Current")
        && postfixCalls.Any(m => m.DeclaringType.Name == "WeaponNativeSession" && m.Name == "Guard"));
}

var plugins = nativeTypes.Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin")).ToArray();
Check("plugin.single-entry", plugins.Length == 1 && plugins[0].FullName == "ForgeWeapon.Native.Plugin"
    && plugins[0].BaseType?.FullName == "BepInEx.Unity.IL2CPP.BasePlugin", string.Join(", ", plugins.Select(p => p.FullName)));
string[] Args(CustomAttribute a) => a.ConstructorArguments.Select(x => x.Value as string ?? "").ToArray();
string[] PluginIdentity(AssemblyDefinition assembly, string type) => Args(assembly.MainModule.Types.SelectMany(Walk).Single(t => t.FullName == type)
    .CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
var hostIdentity = PluginIdentity(host, "ForgeRuntime.Plugin");
var mapIdentity = PluginIdentity(mapNative, "ForgeMap.Native.Plugin");
if (plugins.Length == 1)
{
    var plugin = plugins[0];
    var identity = Args(plugin.CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
    Check("plugin.identity", identity.SequenceEqual(new[] { "NAinfini.ForgeWeapon", "Infini Forge Weapon", "0.1.0" })
        && weapon.Name.Version.ToString(3) == identity[2] && native.Name.Version.ToString(3) == identity[2],
        string.Join(", ", identity) + "; ForgeWeapon " + weapon.Name.Version + "; native " + native.Name.Version);
    // The dependency versions are read from the built host and Map plugins, so a renamed or re-versioned owner fails here.
    var dependencies = plugin.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency").Select(a => string.Join("@", Args(a)))
        .OrderBy(x => x, StringComparer.Ordinal).ToArray();
    var expectedDependencies = new[] { hostIdentity[0] + "@" + hostIdentity[2], mapIdentity[0] + "@" + mapIdentity[2] }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    Check("plugin.depends-on-host-and-map", dependencies.SequenceEqual(expectedDependencies, StringComparer.Ordinal)
        && hostIdentity[0] == "NAinfini.ForgeRuntime" && mapIdentity[0] == "NAinfini.ForgeMap", string.Join(" | ", dependencies));
    var load = plugin.Methods.Single(m => m.Name == "Load").Body.Instructions.ToList();
    int Index(string member) => load.FindIndex(i => i.Operand is MethodReference m && Member(m) == member);
    int off = Index("ForgeRuntime.Plugin::get_ConfiguredMode"), runtime = Index("ForgeRuntime.Plugin::get_Runtime"),
        harmony = load.FindIndex(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m && Member(m) == "HarmonyLib.Harmony::.ctor"),
        start = Index("ForgeWeapon.Native.WeaponNativeSession::Start");
    Check("plugin.off-gate-before-runtime-and-hooks", off >= 0 && off < runtime && runtime < harmony && harmony < start, $"{off} {runtime} {harmony} {start}");
    Check("plugin.gameplay-gate-from-host", calls.Any(m => Member(m) == "ForgeRuntime.Plugin::get_CanExecuteGameplay"));
    var unload = plugin.Methods.Single(m => m.Name == "Unload").Body.Instructions;
    Check("plugin.no-hot-unload", unload.Count == 2 && unload[0].OpCode == OpCodes.Ldc_I4_0 && unload[1].OpCode == OpCodes.Ret);
}
var report = new { verification = "compiled-assembly-metadata-only", gameExecuted = false,
    sdkSha256 = Hash(args[1]), hostSha256 = Hash(args[2]), mapNativeSha256 = Hash(args[3]), weaponSha256 = Hash(args[4]), nativeSha256 = Hash(args[5]),
    passed = checks.Count(c => c.Passed), failed = checks.Count(c => !c.Passed), checks };
string output = Path.GetFullPath(args[7]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Detail);
Console.WriteLine($"{(report.failed == 0 ? "PASS" : "FAIL")} {report.passed}/{checks.Count} Weapon native layout checks; no GTFO execution.");
return report.failed == 0 ? 0 : 1;
internal sealed record CheckRow(string Id, bool Passed, string Detail);
