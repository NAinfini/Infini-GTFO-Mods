using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.TriggerZoneFacts;

/// <summary>
/// One plan mounted on one map object, for the cases that prove a zone is the thing a behaviour hangs on. The plan
/// is the shape the compiler publishes — the trigger capability's own contract, one control step that runs when the
/// mount lets the event in — so a case observes the mount rather than restating it.
/// </summary>
internal static class TriggerZonePlan
{
    private const string BranchBinding = "forge.contract.control.binding.branch";

    /// <summary>Loads one plan mounted on one zone's address, without advancing the kernel: the case ticks next,
    /// so the edge that tick publishes is dispatched into this mount.</summary>
    internal static void Mount(TriggerZoneWorld world, string planId, string capability, string zoneId)
    {
        ArgumentNullException.ThrowIfNull(world);
        var (pins, capabilities, providers, support) = PinTable(world.Kernel);
        string trigger = TriggerZoneContract.Binding(capability);
        var used = new[] { BranchBinding, trigger }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var pinRows = used.Select(id => (object)new
        {
            bindingId = id, capabilityId = pins[id].Capability, capabilityVersion = capabilities[pins[id].Capability],
            providerId = pins[id].Provider, providerVersion = providers[pins[id].Provider], handler = pins[id].Handler
        }).ToArray();
        var triggerContract = world.Kernel.ResolveGraphContract(capability, capabilities[capability], RuntimeJson.EmptyObject);
        var branchContract = world.Kernel.ResolveGraphContract(pins[BranchBinding].Capability,
            capabilities[pins[BranchBinding].Capability], RuntimeJson.EmptyObject);
        string json = RuntimeJson.From(new
        {
            schemaVersion = 4, kind = "forge-runtime-plan", planId,
            resource = new { id = "author.resource", revision = "revision-1" },
            runtime = world.Kernel.Identity, domain = "map", authority = "host", failurePolicy = "stop-entrypoint",
            permissions = used.SelectMany(id => support[id]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            dependencies = Array.Empty<string>(),
            limits = new
            {
                world.Kernel.Limits.MaxEventsPerTick, world.Kernel.Limits.MaxCommandsPerTick,
                world.Kernel.Limits.MaxQueuedEvents, world.Kernel.Limits.MaxCausalDepth
            },
            bindings = pinRows,
            attachments = new object[]
            {
                new { kind = "map-object", category = TriggerZoneAddress.Category, reference = TriggerZoneAddress.Of(zoneId)!.ToString() }
            },
            entrypoints = new[]
            {
                new
                {
                    nodeId = "Entry", binding = Array.IndexOf(used, trigger),
                    layout = Layout(triggerContract), start = 0,
                    steps = new object[]
                    {
                        new
                        {
                            nodeId = "S0_branch", nodeKind = "control", binding = Array.IndexOf(used, BranchBinding),
                            layout = Layout(branchContract),
                            // The branch is a structural step whose condition is a compile-time constant: the case
                            // needs a step the kernel actually runs when the mount lets the event in.
                            inputs = new object[] { new { slot = Port(branchContract, "inputs", "condition"), value = true } },
                            successors = new int?[] { null, null }
                        }
                    }
                }
            }
        }).GetRawText();
        var outcome = world.Kernel.LoadPlans(new[] { PlanCandidate.Loaded(planId + ".plan.json", json) })[0];
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Code + ": " + outcome.Detail);
    }

    private static object Layout(JsonElement contract) => new
    {
        inputs = Sides(contract, "inputs"),
        outputs = Sides(contract, "outputs"),
        constants = Array.Empty<object>(),
        promoted = Array.Empty<int>()
    };

    private static int Port(JsonElement contract, string side, string id)
        => contract.GetProperty(side).EnumerateArray()
            .Select((port, index) => (port, index))
            .Single(x => x.port.GetProperty("id").GetString() == id).index;

    /// <summary>The dense slot layout the plan loader re-derives from the registered contract. It is
    /// assembly-internal and this case needs the same rule the compiler publishes, so it is read here rather than
    /// restated — a restated layout would only prove the test agrees with itself.</summary>
    private static object Sides(JsonElement contract, string side)
        => typeof(RuntimeKernel).Assembly.GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!
            .GetMethod("Layout", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { contract, side })!;

    private static (Dictionary<string, (string Capability, string Provider, string Handler)> Pins,
        Dictionary<string, string> Capabilities, Dictionary<string, string> Providers,
        Dictionary<string, string[]> Support) PinTable(RuntimeKernel kernel)
    {
        var manifest = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry");
        var capabilities = manifest.GetProperty("capabilities").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("version").GetString()!, StringComparer.Ordinal);
        var providers = manifest.GetProperty("providers").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("version").GetString()!, StringComparer.Ordinal);
        var pins = manifest.GetProperty("bindings").EnumerateArray().ToDictionary(
            b => b.GetProperty("id").GetString()!,
            b => (Capability: b.GetProperty("capabilityId").GetString()!,
                Provider: b.GetProperty("providerId").GetString()!,
                Handler: b.TryGetProperty("handler", out var handler) && handler.ValueKind == JsonValueKind.String
                    ? handler.GetString()! : ""), StringComparer.Ordinal);
        var support = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("bindingSupport").EnumerateArray().ToDictionary(
            s => s.GetProperty("bindingId").GetString()!,
            s => s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!).ToArray(),
            StringComparer.Ordinal);
        return (pins, capabilities, providers, support);
    }
}
