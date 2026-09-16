using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>The one plan the enemy-scope cases need: a behaviour that writes one `enemy`-scoped variable when it
/// fires. The kernel keys that scope by the subject the step addresses, and the step addresses the fact's own
/// subject port — a plan mounted on the level accepts no entity of its own, so nothing else could name the enemy
/// whose life the release has to be observed on.
///
/// The plan is built from the kernel's own registry and its own resolved contracts: every pin, port slot and
/// layout frame below is read back, never restated, so this file cannot drift from the SDK's wire shape. The
/// layout derivation is internal to the Framework assembly, which this test assembly is named in.</summary>
internal static class EnemyScopePlan
{
    /// <summary>The variable the probe writes. It is deliberately not one the provider reads: the case is about
    /// the scope's own lifecycle, and the kernel's own exported snapshot is what the case reads.</summary>
    internal const string VariableName = "test.lifecycle.enemy_hp";

    /// <summary>Declares the probe variable, then loads the plan that writes it on the named fact. Every
    /// rejection propagates, so a case that cannot load its plan fails rather than silently probing nothing.</summary>
    internal static void Load(RuntimeKernel kernel, string triggerBinding, string planId)
    {
        var root = RuntimeJson.Parse(kernel.ExportManifest());
        var registry = root.GetProperty("registry");
        JsonElement Row(string list, string id) => registry.GetProperty(list).EnumerateArray()
            .Single(row => Text(row, "id") == id);
        string Version(JsonElement row) => Text(row, "version");
        var writeBinding = VariableContracts.WriteBinding;
        var parameters = new Dictionary<string, object?> { ["name"] = VariableName, ["value_type"] = "number" };
        var writeContract = kernel.ResolveGraphContract(VariableContracts.WriteCapability, "1.0.0", RuntimeJson.From(parameters));
        var triggerRow = Row("bindings", triggerBinding);
        var triggerCapability = Row("capabilities", Text(triggerRow, "capabilityId"));
        var pins = new[] { triggerBinding, writeBinding }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var permissions = root.GetProperty("bindingSupport").EnumerateArray()
            .Where(support => pins.Contains(Text(support, "bindingId")))
            .SelectMany(support => support.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        object Pin(string id)
        {
            var binding = Row("bindings", id); var capabilityId = Text(binding, "capabilityId");
            var providerId = Text(binding, "providerId");
            return new { bindingId = id, capabilityId, capabilityVersion = Version(Row("capabilities", capabilityId)),
                providerId, providerVersion = Version(Row("providers", providerId)), handler = Text(binding, "handler") };
        }
        // The step addresses the fact's own subject. A plan mounted on the level accepts no entity of its own — a
        // subject-free mount judges the target alone — so the dispatch carries no subject to inherit, and the
        // address is the entity port the fact named its subject on. That is the same address `EnemyModule`
        // releases when the life ends, which is what makes the release observable; the ports the step does not
        // wire stay absent.
        var triggerContract = kernel.ResolveGraphContract(Text(triggerRow, "capabilityId"), Version(triggerCapability), RuntimeJson.EmptyObject);
        var valueSlot = PortIndex(writeContract, "value");
        var subjectSlot = PortIndex(writeContract, "subject");
        var subjectEventSlot = EntityOutput(triggerContract);
        var stepInputs = new[]
        {
            (Slot: subjectSlot, Row: (object)new { slot = subjectSlot, fromEventSlot = subjectEventSlot }),
            (Slot: valueSlot, Row: (object)new { slot = valueSlot, value = (object)1 })
        }.OrderBy(input => input.Slot).Select(input => input.Row).ToArray();
        // The positional constants frame of one node, over the parameter definitions the capability itself
        // declares: the write step names the variable it writes and the type it writes, and the entrypoint's own
        // trigger names none of the parameters it declares.
        object Frame(JsonElement source, object?[] constants) => new
        {
            inputs = RuntimeGraphContracts.Layout(source, "inputs"),
            outputs = RuntimeGraphContracts.Layout(source, "outputs"),
            constants, promoted = Array.Empty<int>()
        };
        object?[] Unset(JsonElement capabilityRow) => capabilityRow.GetProperty("graph").GetProperty("parameters")
            .EnumerateArray().Select(_ => (object?)null).ToArray();
        var json = RuntimeJson.From(new
        {
            schemaVersion = 4, kind = "forge-runtime-plan", planId, resource = new { id = planId, revision = "1" },
            runtime = kernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
            permissions, dependencies = Array.Empty<string>(),
            limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth },
            variables = new[] { new { id = VariableName, scope = VariableScopeKinds.Enemy, type = "number", initial = 0 } },
            bindings = pins.Select(Pin).ToArray(),
            attachments = new[] { new { kind = "level", reference = LocalPlan.MountReference } },
            entrypoints = new[] { new { nodeId = "Fact", binding = Array.IndexOf(pins, triggerBinding), start = 0,
                layout = Frame(triggerContract, Unset(triggerCapability)),
                steps = new object[] { new { nodeId = "Write", nodeKind = "control", binding = Array.IndexOf(pins, writeBinding),
                    layout = Frame(writeContract, new object?[] { VariableName, 0 }),
                    inputs = stepInputs,
                    successors = new int?[] { null } } } } }
        });
        var outcome = kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/" + planId + ".plan.json", json.GetRawText()) })[0];
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code, outcome.Detail);
    }

    /// <summary>How many `enemy`-scoped entries the kernel's snapshot holds for one subject, so a case can see the
    /// value's whole life: absent before the write, present after it, and gone once the life ended.</summary>
    internal static int EnemyEntries(RuntimeKernel kernel, EntityReference subject)
        => RuntimeJson.Parse(kernel.ExportVariables()).GetProperty("entries").EnumerateArray()
            .Count(entry => Text(entry, "scope") == VariableScopeKinds.Enemy
                && entry.GetProperty("subject").GetString() == subject.Id);

    /// <summary>The port a fact names its subject on: the first entity output the trigger declares. Both lifecycle
    /// facts name the enemy first — `enemy` for a death, `target` for a broken part — and the ports after it are
    /// the context of the same event.</summary>
    private static int EntityOutput(JsonElement contract)
    {
        int index = 0;
        foreach (var port in contract.GetProperty("outputs").EnumerateArray())
        {
            if (Text(port, "type") == "entity") return index;
            index++;
        }
        throw new InvalidOperationException("Trigger declares no entity output.");
    }

    /// <summary>One string member of a registry or snapshot row. The SDK's own validating accessor is internal to
    /// its assembly, so this suite spells the read the same way the plan builder beside it does.</summary>
    private static string Text(JsonElement row, string name) => row.GetProperty(name).GetString()!;

    private static int PortIndex(JsonElement contract, string port)
    {
        int index = 0;
        foreach (var candidate in contract.GetProperty("inputs").EnumerateArray())
        {
            if (Text(candidate, "id") == port) return index;
            index++;
        }
        throw new InvalidOperationException("Contract declares no input port " + port + ".");
    }
}
