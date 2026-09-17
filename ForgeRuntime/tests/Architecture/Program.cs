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
    (typeof(ForgeTrigger.ModuleDefinition), ForgeTrigger.ModuleDefinition.Create),
    (typeof(ForgeMap.ModuleDefinition), ForgeMap.ModuleDefinition.Create),
    // Weapon's declaration takes the bodies its native half supplies; this check registers the declaration
    // without any of them, so its rows are the observed ones and nothing here is executable. A method group
    // cannot bind those optional parameters, hence the lambda.
    (typeof(ForgeWeapon.ModuleDefinition), () => ForgeWeapon.ModuleDefinition.Create()),
};
var sdk = typeof(RuntimeKernel).Assembly;
Check(sdk.GetName().Name == "ForgeRuntime.Framework", "the shared SDK has its own assembly");
var assemblies = definitions.Select(d => d.Type.Assembly).Append(typeof(ForgeEnemy.ModuleDefinition).Assembly).Append(sdk).ToArray();
Check(assemblies.Distinct().Count() == 5, "four managed domain assemblies and one SDK; native Enemy is checked by NativeLayout");
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

var kernel = new RuntimeKernel(new RuntimeIdentity("forge.architecture.check", "1.0.0", RuntimeKernel.ApiVersion, "offline-no-game"));
var handles = new List<RuntimeModuleHandle>();
// The runtime's own trigger contract is one of the builtin providers a host registers before any package: the
// domain declarations below bind canonical Trigger ids whose shapes it owns, and a domain module carries no copy.
handles.Add(kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off));
foreach (var definition in definitions)
{
    var module = definition.Create();
    Check(module.GetType().Assembly == sdk, definition.Type.Namespace + " uses the public SDK type");
    // The game-independent Map declaration carries the selector shapes but no evaluator: the world-bound ones
    // live in the native assembly, which this check does not compile. The level-object and generator value rows
    // are the same case — the declaration binds them and the family that implements them composes their shapes —
    // so the check composes the declaration's own table with the two families' tables and adds one stand-in
    // evaluator per declared value row, which keeps the registration the subject here and evaluates nothing. The
    // set comes from the tables themselves instead of a second list kept here, because a hand-kept list is
    // exactly what this check fell behind on when the zone selector was declared beside the player one and again
    // when the generator rows joined the declaration.
    if (definition.Type == typeof(ForgeMap.ModuleDefinition))
    {
        var shapes = new Dictionary<string, HandlerShape>(module.Shapes, StringComparer.Ordinal);
        foreach (var (handler, shape) in ForgeMap.LevelObjectContract.Shapes()) shapes[handler] = shape;
        foreach (var (handler, shape) in ForgeMap.GeneratorContract.Shapes()) shapes[handler] = shape;
        module = module with
        {
            Shapes = shapes,
            Evaluators = shapes.Keys.ToDictionary(
                static handler => handler,
                static _ => new EvaluatorHandler(_ =>
                    throw new InvalidOperationException("The architecture check does not evaluate a Map value row.")),
                StringComparer.Ordinal)
        };
    }
    handles.Add(kernel.RegisterModule(module, RuntimeLogLevel.Off));
}
Check(handles.Select(h => h.ProviderId).Distinct().Count() == definitions.Length + 1, "all providers can coexist");
var manifestBeforeDuplicate = kernel.ExportManifest();
using (var manifest = JsonDocument.Parse(manifestBeforeDuplicate))
{
    var registry = manifest.RootElement.GetProperty("registry");
    Check(registry.GetProperty("providers").GetArrayLength() == definitions.Length + 1, "one registry contains all providers");
    var weapon = ForgeWeapon.ModuleDefinition.ProviderId;
    var owners = registry.GetProperty("capabilities").EnumerateArray().Select(c => c.GetProperty("owner").GetString()).ToArray();
    // The count comes from the declaration under test, not from a number that has to be edited whenever a package
    // observes one more fact: what this file checks is that every declared row lands under its own provider.
    var declaredWeapon = JsonDocument.Parse(ForgeWeapon.ModuleDefinition.Create().RegistryJson).RootElement
        .GetProperty("capabilities").GetArrayLength();
    Check(owners.Count(o => o == weapon) == declaredWeapon,
        "every capability the Weapon declaration carries is owned by Weapon [" + declaredWeapon + "]");
    var trigger = ForgeTrigger.ModuleDefinition.ProviderId;
    var declaredTriggerCapabilities = JsonDocument.Parse(ForgeTrigger.ModuleDefinition.Create().RegistryJson).RootElement
        .GetProperty("capabilities").GetArrayLength();
    var declaredTriggerBindings = JsonDocument.Parse(ForgeTrigger.ModuleDefinition.Create().RegistryJson).RootElement
        .GetProperty("bindings").EnumerateArray().ToArray();
    Check(owners.Count(o => o == trigger) == declaredTriggerCapabilities,
        "every capability the Trigger declaration carries is owned by Trigger [" + declaredTriggerCapabilities + "]");
    Check(registry.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "forge.condition.predicate.compare")
        .GetProperty("owner").GetString() == trigger, "Trigger now owns the canonical compare condition");
    var bindings = registry.GetProperty("bindings").EnumerateArray()
        .Select(b => (Provider: b.GetProperty("providerId").GetString(), Role: b.GetProperty("role").GetString(), Id: b.GetProperty("id").GetString())).ToArray();
    Check(bindings.Count(b => b.Provider == trigger) == declaredTriggerBindings.Length
        && declaredTriggerBindings.All(row => bindings.Any(b => b.Provider == trigger && b.Id == row.GetProperty("id").GetString()
            && b.Role == row.GetProperty("role").GetString())), "Trigger registers exactly the bindings its own declaration carries");
    // Weapon binds every canonical Trigger row it observes and declares no shape for them, so the number of rows
    // it binds is its declaration's own binding count, not its capability count.
    var declaredWeaponBindings = JsonDocument.Parse(ForgeWeapon.ModuleDefinition.Create().RegistryJson).RootElement
        .GetProperty("bindings").GetArrayLength();
    Check(bindings.Count(b => b.Provider == weapon && b.Role == "observe") == declaredWeaponBindings,
        "Weapon observes through every declared binding [" + declaredWeaponBindings + "]");
    Check(bindings.All(b => b.Role != "execute"), "nothing registered here is executable");
    Check(manifest.RootElement.GetProperty("bindingSupport").EnumerateArray()
        .All(s => s.GetProperty("verification").GetString() == "implementation-only"), "no provider claims game verification");
}
try
{
    kernel.RegisterModule(definitions[0].Create(), RuntimeLogLevel.Off);
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
using (var combat = kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off))
using (var control = kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off))
using (var manifest = JsonDocument.Parse(kernel.ExportManifest()))
{
    var registry = manifest.RootElement.GetProperty("registry");
    Check(combat.ProviderId == "forge.contract.combat", "existing canonical combat owner is preserved");
    var capabilities = registry.GetProperty("capabilities").EnumerateArray().ToArray();
    var bindings = registry.GetProperty("bindings").EnumerateArray().ToArray();
    // Both counts are what the two SDK declarations carry, so the vocabulary can grow without this file having to
    // restate a number; what is checked is that the registry holds exactly the declared rows and nothing else.
    int Declared(RuntimeModule module, string list)
        => JsonDocument.Parse(module.RegistryJson).RootElement.GetProperty(list).GetArrayLength();
    Check(capabilities.Length == Declared(CombatContracts.Module(), "capabilities") + Declared(ControlContracts.Module(), "capabilities")
        && bindings.Length == Declared(ControlContracts.Module(), "bindings"),
        "shared combat and lifecycle definitions create exactly the control contract's own native bindings");
    Check(capabilities.Count(c => c.GetProperty("owner").GetString() == combat.ProviderId) == Declared(CombatContracts.Module(), "capabilities"),
        "every combat and enemy trigger definition belongs to the combat provider");
    Check(control.ProviderId == "forge.contract.control", "the branch capability has one native contract owner");
    var branch = capabilities.Single(c => c.GetProperty("id").GetString() == "forge.control.flow.branch");
    Check(branch.GetProperty("owner").GetString() == "forge.contract.control" && branch.GetProperty("kind").GetString() == "control",
        "the branch contract is a control capability owned by the control provider");
    var branchBinding = bindings.Single(b => b.GetProperty("id").GetString() == "forge.contract.control.binding.branch");
    Check(branchBinding.GetProperty("role").GetString() == "execute"
        && branchBinding.GetProperty("handler").GetString() == "runtime.control.branch", "the branch binding is registered without a handler function");
    Check(kernel.ExportManifest().Contains("runtime.control.branch", StringComparison.Ordinal), "the branch binding ships in the exported manifest");
}
RecordPointProbe.Run(Check);
// The walk above only proves anything while it still sees composition: these helpers compose exactly the way a record
// point must not, one by concatenating and one through the handler an interpolated string compiles to. A comparison is
// there to pin the other side of the rule, that reading a string is not composing one.
Check(RecordPointProbe.BuildsText(typeof(RecordPointTextCases).GetMethod(nameof(RecordPointTextCases.Concatenating))!).Length > 0,
    "the record-point text walk missed a helper that concatenates");
Check(RecordPointProbe.BuildsText(typeof(RecordPointTextCases).GetMethod(nameof(RecordPointTextCases.Interpolating))!).Length > 0,
    "the record-point text walk missed a helper that interpolates");
Check(RecordPointProbe.BuildsText(typeof(RecordPointTextCases).GetMethod(nameof(RecordPointTextCases.Comparing))!).Length == 0,
    "the record-point text walk read a string comparison as composed text");

static class RecordPointTextCases
{
    public static string Concatenating(string a, string b, int index) => a + b + index;
    public static string Interpolating(string name, int check) => $"{name} failed at {check}";
    public static bool Comparing(string a, string b) => a == b;
}
