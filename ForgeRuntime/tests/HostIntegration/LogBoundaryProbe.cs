using ForgeRuntime.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

/// <summary>I-DIAG call-site rules checked on compiled IL: record text fields never receive a string built at the call site
/// (the sink composes messages off the simulation thread), and the log path never resolves a player through SNet_Player.Lookup.
/// The text rule follows the value producer directly before the setter and through one local; branches that merge a built
/// string into the setter are not followed.</summary>
internal static class LogBoundaryProbe
{
    private static readonly string[] RecordTypes = { typeof(RuntimeLogRecord).FullName!, typeof(RuntimeLogPlan).FullName!, typeof(RuntimeLogResult).FullName! };

    internal static void Run(string hostPath)
    {
        using var controls = AssemblyDefinition.ReadAssembly(typeof(LogBoundaryFixtures).Assembly.Location);
        var fixture = controls.MainModule.GetType(typeof(LogBoundaryFixtures).FullName);
        foreach (var name in new[] { "Concatenated", "Interpolated", "Formatted", "ThroughLocal" })
            Verify.That(Scan(fixture.Methods.Where(m => m.Name == name)).Violations.Count == 1, "log boundary probe missed built text in fixture " + name);
        var clean = Scan(fixture.Methods.Where(m => m.Name == "Clean"));
        Verify.That(clean.Sites == 8 && clean.Violations.Count == 0, "log boundary probe flagged or miscounted the clean fixture");
        Verify.That(Lookups(new[] { fixture }).Count == 1, "SNet_Player.Lookup probe missed its fixture");

        using var sdk = AssemblyDefinition.ReadAssembly(typeof(RuntimeKernel).Assembly.Location);
        using var host = AssemblyDefinition.ReadAssembly(Path.GetFullPath(hostPath));
        var sdkScan = Scan(All(sdk.MainModule.Types).SelectMany(t => t.Methods));
        var hostScan = Scan(All(host.MainModule.Types).SelectMany(t => t.Methods));
        // The rule covers every Forge assembly beside the host, so a domain package's own record points are held to it
        // too and not only this task's kernel-side ones.
        var domains = Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(hostPath))!, "Forge*.dll")
            .Where(file => Path.GetFileName(file) != Path.GetFileName(hostPath))
            .SelectMany(file => { using var assembly = AssemblyDefinition.ReadAssembly(file); return Scan(All(assembly.MainModule.Types).SelectMany(t => t.Methods)).Violations; })
            .ToList();
        Verify.That(sdkScan.Violations.Count == 0 && hostScan.Violations.Count == 0 && domains.Count == 0,
            "log call site builds text: " + string.Join(", ", sdkScan.Violations.Concat(hostScan.Violations).Concat(domains)));
        // The writer's own log.level/log.dropped records are the only sites today; zero would mean the rule scanned nothing.
        Verify.That(hostScan.Sites > 0, "no record text setter found in the host; the call-site rule is vacuous");
        var logging = host.MainModule.Types.Where(t => t.Namespace == "ForgeRuntime.Logging").ToArray();
        Verify.That(logging.Any(t => t.Name == "RuntimeLogWriter"), "host log writer is not compiled into ForgeRuntime.Logging");
        var lookups = Lookups(sdk.MainModule.Types).Concat(Lookups(logging)).ToList();
        Verify.That(lookups.Count == 0, "log path resolves players: " + string.Join(", ", lookups));
    }

    private static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types)
        => types.SelectMany(t => All(t.NestedTypes).Prepend(t));

    private static (int Sites, List<string> Violations) Scan(IEnumerable<MethodDefinition> methods)
    {
        int sites = 0; var violations = new List<string>();
        foreach (var method in methods.Where(m => m.HasBody))
        {
            var code = method.Body.Instructions;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].Operand is not MethodReference setter || !setter.Name.StartsWith("set_", StringComparison.Ordinal)
                    || !RecordTypes.Contains(setter.DeclaringType.FullName) || setter.Parameters.Count != 1
                    || setter.Parameters[0].ParameterType.FullName != "System.String") continue;
                sites++;
                if (BuildsText(code, i - 1, method, 1)) violations.Add(method.FullName + " " + setter.Name);
            }
        }
        return (sites, violations);
    }

    private static bool BuildsText(Collection<Instruction> code, int index, MethodDefinition method, int localHops)
    {
        if (index < 0) return false;
        if (code[index].Operand is MethodReference call)
            return call.Name is "ToString" or "ToStringAndClear"
                || (call.DeclaringType.FullName == "System.String" && call.Name is "Concat" or "Format" or "Join" or "Create");
        var local = Local(code[index], method, store: false);
        if (local == null || localHops == 0) return false;
        for (int i = index - 1; i >= 0; i--)
            if (Local(code[i], method, store: true) == local) return BuildsText(code, i - 1, method, localHops - 1);
        return false;
    }

    private static VariableDefinition? Local(Instruction instruction, MethodDefinition method, bool store)
    {
        var op = instruction.OpCode.Code;
        int slot = store
            ? op switch { Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3, _ => -1 }
            : op switch { Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3, _ => -1 };
        if (slot >= 0) return method.Body.Variables[slot];
        bool named = store ? op is Code.Stloc_S or Code.Stloc : op is Code.Ldloc_S or Code.Ldloc;
        return named ? instruction.Operand as VariableDefinition : null;
    }

    private static List<string> Lookups(IEnumerable<TypeDefinition> types)
        => All(types).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Where(c => c.DeclaringType.Name == "SNet_Player" && c.Name == "Lookup").Select(_ => m.FullName)).ToList();
}
