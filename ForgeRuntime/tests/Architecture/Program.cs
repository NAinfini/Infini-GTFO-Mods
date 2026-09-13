using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    checks++;
}

var definitions = new (Type Type, Func<RuntimeModule> Create)[]
{
    (typeof(ForgeDevelopment.ModuleDefinition), ForgeDevelopment.ModuleDefinition.Create),
    (typeof(ForgeTrigger.ModuleDefinition), ForgeTrigger.ModuleDefinition.Create),
    (typeof(ForgeMap.ModuleDefinition), ForgeMap.ModuleDefinition.Create),
    (typeof(ForgeWeapon.ModuleDefinition), ForgeWeapon.ModuleDefinition.Create),
};
var sdk = typeof(RuntimeKernel).Assembly;
Check(sdk.GetName().Name == "ForgeRuntime.Framework", "the shared SDK has its own assembly");
var assemblies = definitions.Select(d => d.Type.Assembly).Append(typeof(ForgeEnemy.ModuleDefinition).Assembly).Append(sdk).ToArray();
Check(assemblies.Distinct().Count() == 6, "five managed domain assemblies and one SDK; native Enemy is checked by NativeLayout");
Check(typeof(ForgeEnemy.ModuleDefinition).GetMethod("Create") == null, "retired empty Enemy provider cannot register beside native Enemy");
Check(ForgeEnemy.ModuleDefinition.ProviderId == "forge.module.gtfo.enemy", "Enemy provider identity preserved");
foreach (var assembly in assemblies)
{
    var references = assembly.GetReferencedAssemblies();
    Check(references.All(r => !r.Name!.StartsWith("Unity", StringComparison.Ordinal)
        && !r.Name.StartsWith("BepInEx", StringComparison.Ordinal)
        && !r.Name.Contains("Harmony", StringComparison.Ordinal)), assembly.GetName().Name + " is game-independent");
    if (assembly == sdk) continue;
    var forgeReferences = references.Where(r => r.Name!.StartsWith("Forge", StringComparison.Ordinal)).ToArray();
    Check(forgeReferences.Length == 1 && forgeReferences[0].FullName == sdk.GetName().FullName,
        assembly.GetName().Name + " references only the shared SDK");
    Check(assembly.GetType(typeof(RuntimeKernel).FullName!) == null,
        assembly.GetName().Name + " does not embed a second kernel");
}

var kernel = new RuntimeKernel(new RuntimeIdentity("forge.architecture.check", "0.1.0", RuntimeKernel.ApiVersion, "offline-no-game"));
var handles = new List<RuntimeModuleHandle>();
foreach (var definition in definitions)
{
    var module = definition.Create();
    Check(module.GetType().Assembly == sdk, definition.Type.Namespace + " uses the public SDK type");
    handles.Add(kernel.RegisterModule(module));
}
Check(handles.Select(h => h.ProviderId).Distinct().Count() == definitions.Length, "all providers can coexist");
var manifestBeforeDuplicate = kernel.ExportManifest();
using (var manifest = JsonDocument.Parse(manifestBeforeDuplicate))
{
    var registry = manifest.RootElement.GetProperty("registry");
    Check(registry.GetProperty("providers").GetArrayLength() == definitions.Length, "one registry contains all providers");
    Check(registry.GetProperty("capabilities").GetArrayLength() == 0, "scaffolds do not claim capabilities");
    Check(registry.GetProperty("bindings").GetArrayLength() == 0, "scaffolds do not claim executable bindings");
    Check(manifest.RootElement.GetProperty("bindingSupport").GetArrayLength() == 0, "scaffolds do not claim game verification");
}
try
{
    kernel.RegisterModule(definitions[0].Create());
    throw new Exception("FAIL: duplicate provider accepted");
}
catch (RuntimeContractException error)
{
    Check(error.Code == "provider-conflict", "duplicate provider fails with the ownership error");
}
Check(kernel.ExportManifest() == manifestBeforeDuplicate, "rejected registration leaves the registry unchanged");
kernel.BeginWorld(1);
kernel.Advance(0, true);
Check(kernel.LoadedPlans == 0 && kernel.QueuedEvents == 0, "registration starts no gameplay or background work");
foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose();
foreach (var handle in handles) handle.Dispose();
Check(handles.All(h => !h.IsRegistered), "module disposal is idempotent");
using (var manifest = JsonDocument.Parse(kernel.ExportManifest()))
    Check(manifest.RootElement.GetProperty("registry").GetProperty("providers").GetArrayLength() == 0, "all providers unregister cleanly");
// The existing shared combat definitions moved into the SDK without claiming a receiver implementation.
using (var combat = kernel.RegisterModule(CombatContracts.Module()))
using (var manifest = JsonDocument.Parse(kernel.ExportManifest()))
{
    var registry = manifest.RootElement.GetProperty("registry");
    Check(combat.ProviderId == "forge.contract.combat", "existing canonical combat owner is preserved");
    Check(registry.GetProperty("capabilities").GetArrayLength() == 5 && registry.GetProperty("bindings").GetArrayLength() == 0,
        "shared combat and lifecycle definitions do not create native bindings");
}
Console.WriteLine($"PASS {checks} architecture boundary assertions. No GTFO hooks, gameplay, networking or installation exercised.");
