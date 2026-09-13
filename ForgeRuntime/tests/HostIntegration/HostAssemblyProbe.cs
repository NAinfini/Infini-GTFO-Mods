using ForgeRuntime.Framework;
using Mono.Cecil;

internal static class HostAssemblyProbe
{
    internal static void Run(string path)
    {
        using var host = AssemblyDefinition.ReadAssembly(Path.GetFullPath(path));
        var references = host.MainModule.AssemblyReferences;
        Verify.That(references.Count(r => r.Name == "ForgeRuntime.Framework") == 1, "host must reference exactly one shared SDK");
        Verify.That(!references.Any(r => r.Name == "Infini.ForgeRuntime.Core"), "prototype leaked into production references");
        Verify.That(!host.MainModule.Types.Any(t => t.Namespace == "Infini.ForgeRuntime"), "prototype source leaked into production DLL");
        Verify.That(!host.MainModule.Types.Any(t => t.FullName == typeof(RuntimeKernel).FullName), "host contains a duplicate kernel type");
        var plugin = host.MainModule.Types.Single(t => t.FullName == "ForgeRuntime.Plugin");
        var runtime = plugin.Properties.Single(p => p.Name == "Runtime");
        Verify.That(runtime.GetMethod.IsPublic && runtime.GetMethod.IsStatic
            && runtime.PropertyType.FullName == typeof(RuntimeKernel).FullName
            && runtime.PropertyType.Scope.Name == "ForgeRuntime.Framework", "Plugin.Runtime does not expose the shared public SDK type");
        Verify.That(!host.MainModule.Types.Any(t => t.Name == "FrameworkStartup"), "retired private startup latch still compiled");
        string[] diagnostics = { "RuntimeDiagnostics", "AuthoringMonitor", "PerformanceMonitor", "PerformanceDiagnostics", "DiagnosticsReport", "TelemetryBridge", "Settings" };
        Verify.That(!host.MainModule.Types.Any(t => diagnostics.Contains(t.Name)), "diagnostics implementation is still compiled into the host");
        Verify.That(host.MainModule.Types.Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
            .All(t => t.Namespace == "ForgeRuntime.GameBindings"), "host patches game code outside its framework bindings");
        Verify.That(!plugin.CustomAttributes.Any(a => a.AttributeType.Name == "BepInDependency")
            && !references.Any(r => r.Name.StartsWith("ForgeDevelopment", StringComparison.Ordinal)), "host depends on an optional plugin");
        var calls = host.MainModule.Types.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>();
        Verify.That(calls.Any(m => m.DeclaringType.FullName == typeof(RuntimeKernel).FullName && m.Name == "StartRuntime"),
            "production host never invokes the public startup lifecycle");
        using var sdk = AssemblyDefinition.ReadAssembly(typeof(RuntimeKernel).Assembly.Location);
        Verify.That(!sdk.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("Unity", StringComparison.Ordinal)
            || r.Name.StartsWith("BepInEx", StringComparison.Ordinal) || r.Name is "ForgeRuntime" or "ForgeEnemy" or "ForgeDevelopment"),
            "public SDK gained a native/domain dependency");
    }
}
