using ForgeRuntime.Framework;
using Mono.Cecil;

internal static class HostAssemblyProbe
{
    internal static void Run(string path)
    {
        using var host = AssemblyDefinition.ReadAssembly(Path.GetFullPath(path));
        var references = host.MainModule.AssemblyReferences;
        Verify.That(references.Count(r => r.Name == "ForgeRuntime.Framework") == 1, "host must reference exactly one shared SDK");
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
        // GTFO-API is the framework's required level-lifecycle base dependency shipped with BepInExPack_GTFO, so the host
        // declares it as a hard dependency; every other plugin dependency would make the host optional-plugin dependent.
        // The attribute arguments are read straight from the blob: this project holds no BepInEx reference, so Cecil
        // cannot resolve the constructor arguments. The host uses the (string, string) constructor, whose version
        // argument the shipped loader parses as a SemVer range: the literal is therefore a `>=` floor, and the version
        // it names is compared against the release identity by Release/check-identity.ps1.
        var dependencies = plugin.CustomAttributes.Where(a => a.AttributeType.Name == "BepInDependency").ToArray();
        var dependency = dependencies.Length == 1 ? DependencyArguments(dependencies[0]) : default;
        Verify.That(dependency.Guid == "dev.gtfomodding.gtfo-api", "host does not declare exactly its GTFO-API hard dependency");
        Verify.That(dependency.Flags == null && dependency.Version != null && dependency.Version.StartsWith(">=", StringComparison.Ordinal),
            "host does not declare a minimum GTFO-API version instead of an exact one");
        Verify.That(references.Any(r => r.Name == "GTFO-API"), "host compiles against GTFO-API without a metadata reference");
        // The one allowed plugin reference is the base dependency above; a reference to a domain package such as
        // ForgeDevelopment would make the host optional-plugin dependent.
        Verify.That(!references.Any(r => r.Name.StartsWith("ForgeDevelopment", StringComparison.Ordinal)), "host depends on an optional plugin");
        var calls = host.MainModule.Types.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>();
        // The trigger module is its own package: the host neither links its sources nor registers it, so no type from
        // that package and no call into it may survive in the host binary.
        Verify.That(!host.MainModule.Types.Any(t => t.Namespace == "ForgeTrigger" || t.Namespace.StartsWith("ForgeTrigger.", StringComparison.Ordinal)),
            "host still compiles types from the ForgeTrigger package");
        Verify.That(!calls.Any(m => m.DeclaringType.Namespace.StartsWith("ForgeTrigger", StringComparison.Ordinal)),
            "host still calls into the ForgeTrigger package");
        Verify.That(calls.Any(m => m.DeclaringType.FullName == typeof(RuntimeKernel).FullName && m.Name == "StartRuntime"),
            "production host never invokes the public startup lifecycle");
        using var sdk = AssemblyDefinition.ReadAssembly(typeof(RuntimeKernel).Assembly.Location);
        Verify.That(!sdk.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("Unity", StringComparison.Ordinal)
            || r.Name.StartsWith("BepInEx", StringComparison.Ordinal) || r.Name is "ForgeRuntime" or "ForgeEnemy" or "ForgeDevelopment"),
            "public SDK gained a native/domain dependency");
    }

    /// <summary>The BepInDependency payload as a (guid, flags, version) triple, read from its blob. Both shipped
    /// constructors are handled: <c>(string, DependencyFlags)</c> encodes an int32 flags field and reports no version,
    /// <c>(string, string)</c> a version string, which the loader always treats as a hard dependency and which
    /// therefore reports flags as null.</summary>
    private static (string? Guid, int? Flags, string? Version) DependencyArguments(CustomAttribute attribute)
    {
        var blob = attribute.GetBlob();
        int at = 2; // prolog
        int length = CompressedUInt32(blob, ref at);
        var guid = System.Text.Encoding.UTF8.GetString(blob, at, length); at += length;
        // A fixed-arg blob ends with a two-byte terminator: an int32 flags argument runs into it and ends on a
        // four-byte boundary, a version string does not.
        if (blob.Length - at == 6) return (guid, BitConverter.ToInt32(blob, at), null);
        int versionLength = CompressedUInt32(blob, ref at);
        return (guid, null, System.Text.Encoding.UTF8.GetString(blob, at, versionLength));
    }

    /// <summary>ECMA-335 compressed unsigned integer: the first byte's high bit selects a 1-byte or 4-byte length.</summary>
    private static int CompressedUInt32(byte[] blob, ref int at)
    {
        var first = blob[at++];
        if ((first & 0x80) == 0) return first;
        if ((first & 0xC0) == 0x80) return ((first & 0x3F) << 8) | blob[at++];
        var value = (first & 0x1F) << 24;
        for (int i = 0; i < 3; i++) value |= blob[at++] << (8 * (2 - i));
        return value;
    }
}
