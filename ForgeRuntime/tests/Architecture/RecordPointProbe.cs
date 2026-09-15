using System.Reflection;
using ForgeRuntime.Framework;

/// <summary>I-DIAG D-007 call-site rules for the kernel, checked on compiled IL: the record type carries no composed
/// message and no inputs payload, the event-code constants are exactly the codes the kernel writes, and no record-point
/// helper composes text. HostIntegration's Cecil probe additionally follows every record text field back to its producer
/// and covers the compiled host; this probe is the part that needs no BepInEx reference and so runs beside the domain
/// assembly boundary checks.</summary>
internal static class RecordPointProbe
{
    private static readonly string[] ExpectedCodes = { "log.dropped", "log.level", "plan.loaded", "plan.rejected",
        "registration.rejected", "binding.registered", "world.began", "trigger.fired", "event.rejected", "budget.exceeded",
        "event.deferred", "event.cancelled", "step.started", "step.finished", "entry.stopped", "observer.failed", "runtime.suspended" };

    internal static void Run(Action<bool, string> check)
    {
        var properties = typeof(RuntimeLogRecord).GetProperties().Select(p => p.Name).ToArray();
        check(!properties.Contains("Message") && !properties.Contains("Inputs")
            && !properties.Any(name => name.Contains("Player", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Steam", StringComparison.OrdinalIgnoreCase)),
            "the I-DIAG record grew a composed message, an inputs payload or a player identity field");
        var declared = typeof(RuntimeLogCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToArray();
        check(declared.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(ExpectedCodes.OrderBy(c => c, StringComparer.Ordinal)),
            "the event code constants and the code table drifted: " + string.Join(",", declared.Except(ExpectedCodes)));
        check(declared.All(c => !c.StartsWith("adapter.", StringComparison.Ordinal)),
            "adapter.* codes were declared before I-ADAPTER-SCHEMA rules them");
        check(RuntimeLogReasonCodes.InvalidHandlerResult == "invalid-handler-result"
            && RuntimeLogReasonCodes.LifecycleObserverFailed == "lifecycle-observer-failed", "kernel reason code value");
        check(Enum.GetValues<RuntimeLogLevel>().Select(l => (int)l).SequenceEqual(new[] { 0, 1, 2, 3 }),
            "the level vocabulary order no longer supports a single integer comparison");
        var recordPoints = typeof(RuntimeKernel).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.DeclaringType == typeof(RuntimeKernel) && m.GetMethodBody() is { } body
                && body.LocalVariables.Any(v => v.LocalType == typeof(RuntimeLogRecord)))
            .ToArray();
        check(recordPoints.Length >= 12, "the record-point walk found too few helpers: " + recordPoints.Length);
        var composed = recordPoints.SelectMany(m => BuildsText(m).Select(text => m.Name + "->" + text)).ToArray();
        check(composed.Length == 0, "a kernel record point composes text: " + string.Join(", ", composed));
        // §3.2 line 619: only step.finished carries commit, so no other record point may set it.
        var commitSites = recordPoints.Where(m => CommitStates(m) && m.Name != "LogStepFinished").Select(m => m.Name).ToArray();
        check(commitSites.Length == 0, "a record point other than step.finished set commit: " + string.Join(", ", commitSites));
    }

    /// <summary>A record point sets commit when its IL calls the <c>Commit</c> setter of the result it builds. The record
    /// types are readonly structs, so their object initializers call the setter directly (0x28) rather than virtual (0x6F);
    /// both forms are read.</summary>
    private static bool CommitStates(MethodInfo method)
    {
        var text = method.GetMethodBody()?.GetILAsByteArray();
        if (text == null) return false;
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == 0xFE) { i += 2 + OperandSize(text[i + 1]); continue; }
            var size = OperandSize(text[i]);
            if (size == 4 && text[i] is 0x28 or 0x6F)
            {
                var target = method.Module.ResolveMethod(BitConverter.ToInt32(text, i + 1));
                if (target is { Name: "set_Commit", DeclaringType: { } owner } && owner == typeof(RuntimeLogResult)) return true;
            }
            i += 1 + size;
        }
        return false;
    }

    /// <summary>Walks the whole method body: a helper that composed text would have to reach a System.String or
    /// StringBuilder member (Concat/Format/Join/Append/...), and the walk reports every such call it finds.</summary>
    private static string[] BuildsText(MethodInfo method)
    {
        var text = method.GetMethodBody()?.GetILAsByteArray();
        if (text == null) return Array.Empty<string>();
        var violations = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == 0xFE)
            {
                var prefixed = text[i + 1];
                // String.Concat/Format/Join with a null argument count use the vararg forms; the SDK never needs them,
                // but a text-building helper would reach for exactly these.
                if (prefixed is >= 0x06 and <= 0x16 or 0x1C) violations.Add("System.String");
                i += 2 + OperandSize(prefixed);
                continue;
            }
            var size = OperandSize(text[i]);
            if (size == 4 && text[i] is 0x28 or 0x6F or 0x73)
            {
                var token = BitConverter.ToInt32(text, i + 1);
                var name = token == 0 ? null : method.Module.ResolveMethod(token)?.DeclaringType?.FullName;
                if (name is "System.String" or "System.Text.StringBuilder") violations.Add(name!);
            }
            i += 1 + size;
        }
        return violations.ToArray();
    }

    // Operand widths follow ECMA-335: a metadata or call-site token, a 4-byte branch target, a byte, an 8-byte integer,
    // or nothing at all. The three token sets are the only opcodes this walk reads an operand from.
    private static readonly HashSet<byte> TokenOperands = new(new byte[] {
        0x27, 0x28, 0x29, 0x6F, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F, 0x80,
        0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x8C, 0x8D, 0x8E, 0x8F, 0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
        0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 });
    private static readonly HashSet<byte> ByteOperands = new(new byte[] {
        0x0E, 0x10, 0x11, 0x12, 0x13, 0x1F, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37 });
    private static readonly HashSet<byte> WideOperands = new(new byte[] { 0x0F, 0x1C, 0x20, 0x22, 0x38, 0x39, 0x3A, 0x3B,
        0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
        0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xC1, 0xC2,
        0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xCE, 0xCF, 0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB,
        0xDC, 0xDD, 0xDE });

    private static int OperandSize(byte opcode)
        => TokenOperands.Contains(opcode) || WideOperands.Contains(opcode) ? 4
            : ByteOperands.Contains(opcode) ? 1
            : opcode is 0x21 or 0x23 ? 8 : 0;
}
