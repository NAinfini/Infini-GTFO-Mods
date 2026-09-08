using Mono.Cecil;

if (args.Length != 2) throw new ArgumentException("Arguments: BepInEx directory, compiled InfiniTweaks.dll");
using var resolver = new DefaultAssemblyResolver();
foreach (string directory in new[] { "core", "interop", "plugins" }) resolver.AddSearchDirectory(Path.Combine(args[0], directory));
using var mod = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
int count = 0;
IEnumerable<TypeDefinition> Walk(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes) foreach (var child in Walk(nested)) yield return child;
}
bool Patch(CustomAttribute attr) => attr.AttributeType.FullName == "HarmonyLib.HarmonyPatch";
bool Hook(CustomAttribute attr) => attr.AttributeType.FullName is "HarmonyLib.HarmonyPrefix" or "HarmonyLib.HarmonyPostfix" or "HarmonyLib.HarmonyFinalizer";
string Plain(TypeReference type) => type is ByReferenceType byRef ? Plain(byRef.ElementType) : type.FullName;
void CheckHook(MethodDefinition hook, MethodDefinition target)
{
    foreach (var parameter in hook.Parameters)
    {
        if (parameter.Name is "__instance" or "__state" or "__exception") continue;
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
        if (type.Name == "FirePatch")
        {
            using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "Modules-ASM.dll"), new ReaderParameters { AssemblyResolver = resolver });
            foreach (string name in new[] { "Gear.BulletWeapon", "Gear.BulletWeaponSynced", "Gear.Shotgun", "Gear.ShotgunSynced" })
                CheckHook(hook, game.MainModule.Types.Single(t => t.FullName == name).Methods.Single(m => m.Name == "Fire" && m.Parameters.Count == 1));
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
Console.WriteLine($"PASS: {count} native hook contracts resolved. This does not execute IL2CPP detours.");
