using Mono.Cecil;

if (args.Length != 4) throw new ArgumentException("Arguments: BepInEx directory, compiled InfiniTweaks.dll, compiled ForgeDevelopment.Native.dll, native dump.cs for this game build");
var bodies = new NativeBodyMap(args[3]);
using var resolver = new DefaultAssemblyResolver();
foreach (string directory in new[] { "core", "interop", "plugins" }) resolver.AddSearchDirectory(Path.Combine(args[0], directory));
using var mod = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
using var forge = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters { AssemblyResolver = resolver });
// Quality of Life stays free of Forge; performance and authoring diagnostics live only in the optional Development plugin.
if (mod.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("ForgeRuntime") || r.Name.StartsWith("ForgeDevelopment")))
    throw new Exception("InfiniTweaks must not depend on a Forge assembly.");
if (forge.MainModule.AssemblyReferences.Any(r => r.Name == "InfiniTweaks"))
    throw new Exception("ForgeDevelopment must not depend on the InfiniTweaks assembly.");
foreach (var name in new[] { "PerformanceDiagnostics", "PerformanceMonitor", "PerformanceSampleWindow", "SceneInventory" })
{
    if (mod.MainModule.Types.Any(t => t.Name == name) || !forge.MainModule.Types.Any(t => t.Name == name))
        throw new Exception($"Diagnostic type {name} must belong exclusively to Forge Development.");
}
var telemetry = mod.MainModule.Types.Single(t => t.FullName == "InfiniTweaks.Telemetry");
if (!telemetry.IsPublic || telemetry.Events.Count != 3) throw new Exception("QOL optional telemetry contract is missing.");
Console.WriteLine("PASS: independent assembly ownership and optional telemetry contract.");
int count = 0;
IEnumerable<TypeDefinition> Walk(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes) foreach (var child in Walk(nested)) yield return child;
}
bool Patch(CustomAttribute attr) => attr.AttributeType.FullName == "HarmonyLib.HarmonyPatch";
bool Hook(CustomAttribute attr) => attr.AttributeType.FullName is "HarmonyLib.HarmonyPrefix" or "HarmonyLib.HarmonyPostfix" or "HarmonyLib.HarmonyFinalizer";
string Plain(TypeReference type) => type is ByReferenceType byRef ? Plain(byRef.ElementType) : type.FullName;
bool AcceptsInstance(TypeReference actual, TypeReference requested)
{
    for (TypeReference? type = actual; type != null; type = type.Resolve().BaseType)
        if (Plain(type) == Plain(requested)) return true;
    return false;
}
void CheckHook(MethodDefinition hook, MethodDefinition target)
{
    foreach (var parameter in hook.Parameters)
    {
        if (parameter.Name is "__state" or "__exception") continue;
        if (parameter.Name == "__originalMethod" && Plain(parameter.ParameterType) == "System.Reflection.MethodBase") continue;
        if (parameter.Name == "__instance")
        {
            if (target.IsStatic || !AcceptsInstance(target.DeclaringType, parameter.ParameterType))
                throw new Exception($"Invalid instance type in {hook.FullName}; native declaring type is {target.DeclaringType}.");
            continue;
        }
        TypeReference expected;
        if (parameter.Name == "__result") expected = target.ReturnType;
        else
        {
            int index = parameter.Name.StartsWith("__") && int.TryParse(parameter.Name[2..], out var number) ? number : -1;
            var native = index >= 0 ? target.Parameters.ElementAtOrDefault(index) : target.Parameters.FirstOrDefault(p => p.Name == parameter.Name);
            if (native == null) throw new Exception($"Unknown injected argument {hook.FullName}: {parameter.Name}");
            expected = native.ParameterType;
        }
        if (Plain(expected) != Plain(parameter.ParameterType)) throw new Exception($"Hook type mismatch: {hook.FullName}: {parameter.Name} != {expected}");
    }
    count++;
}
foreach (var type in mod.MainModule.Types.SelectMany(Walk))
{
    foreach (var hook in type.Methods.Where(method => method.CustomAttributes.Any(Hook)))
    {
        if (!type.CustomAttributes.Any(Patch)) throw new Exception($"PatchAll would skip {type.FullName}: missing class attribute.");
        if (type.Name == "CombatFire")
        {
            using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "Modules-ASM.dll"), new ReaderParameters { AssemblyResolver = resolver });
            foreach (string name in new[] { "BulletWeapon", "Shotgun", "BulletWeaponSynced", "ShotgunSynced" })
                CheckHook(hook, game.MainModule.Types.Single(t => t.Name == name).Methods.Single(m => m.Name == "Fire" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Boolean"));
            continue;
        }
        if (type.Name == "MeleeCostPatch")
        {
            using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "Modules-ASM.dll"), new ReaderParameters { AssemblyResolver = resolver });
            foreach (string name in new[] { "UseWeaponLightSwingStamina", "UseWeaponChargedSwingStamina" })
                CheckHook(hook, game.MainModule.Types.Single(t => t.Name == "PlayerStamina").Methods.Single(m => m.Name == name));
            continue;
        }
        TypeReference? targetType = null;
        string? methodName = null;
        string[]? signature = null;
        int[]? variations = null;
        foreach (var attribute in type.CustomAttributes.Concat(hook.CustomAttributes).Where(Patch))
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Value is TypeReference reference) targetType = reference;
                else if (argument.Value is string name) methodName = name;
                else if (argument.Value is CustomAttributeArgument[] array)
                {
                    if (argument.Type.FullName == "System.Type[]") signature = array.Select(a => ((TypeReference)a.Value).FullName).ToArray();
                    else if (argument.Type.FullName == "HarmonyLib.ArgumentType[]") variations = array.Select(a => Convert.ToInt32(a.Value)).ToArray();
                }
            }
        if (targetType == null || methodName == null) throw new Exception($"No native target for {hook.FullName}");
        if (signature != null && variations != null)
            for (int i = 0; i < signature.Length; i++) if (variations[i] is 1 or 2) signature[i] += "&";
        var candidates = targetType.Resolve().Methods.Where(m => m.Name == methodName && (signature == null || m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(signature))).ToArray();
        if (candidates.Length != 1) throw new Exception($"Expected one target for {hook.FullName}, found {candidates.Length}");
        CheckHook(hook, candidates[0]);
    }
}
if (mod.MainModule.Types.SelectMany(Walk).Any(t => t.Methods.Any(m => m.Name == "OnGUI")))
    throw new Exception("An independent IMGUI panel is still compiled.");
if (mod.MainModule.AssemblyReferences.Any(r => r.Name == "UnityEngine.IMGUIModule"))
    throw new Exception("Unexpected IMGUI dependency.");
if (mod.MainModule.Types.SelectMany(Walk).Any(t => t.Namespace == "InfiniTweaks.WeaponDescriptions" || t.Name is "ChatEnhancements" or "ChatHistoryView" or "ChatTyping"))
    throw new Exception("User-excluded chat or weapon-description implementation is still compiled.");
if (mod.MainModule.Resources.Any(r => r.Name.Contains("StatDisplay", StringComparison.OrdinalIgnoreCase)) ||
    mod.MainModule.AssemblyReferences.Any(r => r.Name == "StatDisplay"))
    throw new Exception("Rejected embedded/external StatDisplay binary is still present.");
foreach (string name in new[] { "CombatStatistics", "CombatStatsView", "BoosterTweaks", "ResourceHud", "ItemMarkers", "TerminalFixes", "InteractionFixes" })
    if (!mod.MainModule.Types.Any(t => t.Name == name)) throw new Exception($"Missing integrated feature: {name}");
var markerResources = mod.MainModule.Resources.OfType<EmbeddedResource>().Where(r => r.Name.StartsWith("InfiniTweaks.MarkerIcons.")).ToArray();
if (markerResources.Length != 58) throw new Exception("Expected 58 embedded marker PNGs");
foreach (var resource in markerResources)
{
    if (!resource.GetResourceData().Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        throw new Exception($"Invalid embedded marker PNG: {resource.Name}");
}
Console.WriteLine($"PASS: {count} QOL native hook contracts and 58 embedded marker PNGs; integrated features present, OnGUI panels absent.");
var qolCount = count;
using var nativeGame = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "Modules-ASM.dll"), new ReaderParameters { AssemblyResolver = resolver });
var jobs = nativeGame.MainModule.Types.Single(t => t.Name == "LG_FactoryJob");
foreach (var type in forge.MainModule.Types.Where(t => t.CustomAttributes.Any(Patch)))
{
    var targets = new List<MethodDefinition>();
    if (type.Name == "GenerationJob")
        targets.AddRange(type.Methods.Single(m => m.Name == "TargetMethods").Body.Instructions
            .Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldtoken && i.Operand is TypeReference)
            .Select(i => ((TypeReference)i.Operand).Resolve())
            .Select(t => t.Methods.Single(m => m.Name == "Build" && m.IsPublic && !m.IsStatic && m.Parameters.Count == 0)));
    else if (type.Name == "CullingLifecycle")
        targets.AddRange(nativeGame.MainModule.Types.Where(t => t.Name is "C_CullingCluster" or "C_CullBucket" or "C_Cullable")
            .SelectMany(t => t.Methods).Where(m => m.IsPublic && !m.IsStatic && m.Name is "OnDestroy" or "CleanupLists" or "AddRenderer" or "RemoveRenderer"));
    else if (type.Name == "CullingFailure")
        targets.AddRange(nativeGame.MainModule.Types.Single(t => t.Name == "C_CullingCluster").Methods.Where(m => m.Name is "Show" or "Hide" or "HideSafe" or "GetShadowRenderingData"));
    else
    {
        var parameters = type.CustomAttributes.Where(Patch).SelectMany(a => a.ConstructorArguments).ToArray();
        var target = parameters.Select(a => a.Value).OfType<TypeReference>().Single();
        var name = parameters.Select(a => a.Value).OfType<string>().Single();
        targets.Add(target.Resolve().Methods.Single(m => m.Name == name));
    }
    if (targets.Count == 0) throw new Exception($"Empty diagnostic target set: {type.Name}");
    foreach (var target in targets) bodies.Check(target);
    foreach (var hook in type.Methods.Where(m => m.CustomAttributes.Any(Hook)))
    {
        if (hook.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix") && hook.ReturnType.FullName != "System.Void")
            throw new Exception($"Observer may not skip the native operation: {hook.FullName}");
        if (hook.Parameters.Any(p => p.Name == "__result" && p.ParameterType is ByReferenceType))
            throw new Exception($"Observer may not replace a native result: {hook.FullName}");
        foreach (var target in targets) CheckHook(hook, target);
    }
}
Console.WriteLine($"PASS: {count - qolCount} authoring hook contracts; no skip-prefix or by-reference result replacement. Native detours still need live acceptance.");
