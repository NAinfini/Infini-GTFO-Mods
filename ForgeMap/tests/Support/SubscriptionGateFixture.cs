using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.Support;

/// <summary>
/// Opens one module's subscription gates for a fixture that observes publications rather than runs plans.
///
/// The kernel only builds an event somebody subscribed to: a provider reads its own gate before it constructs the
/// event, so a fixture whose observation point sits in the module — before the kernel is reached — sees nothing
/// at all while the module has no plan mounted. A case that means to assert what the module published therefore
/// has to give the module a subscriber first, which is what this helper does: one plan per binding the
/// registration publishes under, the way an author's own plan would be mounted on the trigger it listens to.
///
/// The plan is deliberately inert. Its one attachment is an `enemy-type` mount, a kind the helper registers for
/// itself in <see cref="Open"/> and whose matcher answers false for everything, so the plan claims no event: the
/// gate is open, the event is built and observed, and no step of the fixture ever runs. Every other field is the
/// shape the plan loader proves — the trigger's own registered contract for the entry, the kernel's own `branch`
/// for the one step an entrypoint requires, and the binding closure the pin table has to equal.
/// </summary>
internal static class SubscriptionGateFixture
{
    /// <summary>The mount kind this helper owns. None of the four kinds a plan may name is free by itself, so the
    /// helper registers this one with a matcher that answers false: an inert mount target has to be one the
    /// loader can resolve, and owning the kind is what keeps the fixture from depending on what a package under
    /// test happens to have registered.</summary>
    private const string MountKind = "enemy-type";
    private const string MountReference = "test.subscription-gate";
    private const string FixtureProvider = "forge.test.subscription_gates";

    /// <summary>The kernels this helper has already given its own mount and resource kinds. A kernel takes one
    /// registration per provider and one owner per kind, so a fixture that opens gates twice must not register
    /// the fixture module twice.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RuntimeKernel, object> Opened = new();

    /// <summary>The bindings this helper could not open, accumulated across the worlds of one test process. A row
    /// is listed here when no plan can be mounted on it at all — the plan loader requires a constant frame for
    /// every required trigger parameter, and a resource parameter that declares no `resourceKind` cannot be
    /// written: the loader resolves a resource constant through its kind's owner, and this one names no kind. A
    /// fixture whose rows are listed here keeps its gate shut whatever this helper does.</summary>
    internal static readonly List<string> Ungated = new();

    private const string BranchBinding = "forge.contract.control.binding.branch";
    private const string BranchCapability = "forge.control.flow.branch";

    /// <summary>
    /// Mounts one plan on every binding this registration publishes under, so each gate reports a subscriber. The
    /// kernel's own control module has to be registered first — the one step every entrypoint carries is its
    /// `branch` — and this call has to happen while registration is still open, because it registers the mount
    /// and resource kinds the plans are attached to.
    /// </summary>
    internal static void Open(RuntimeKernel kernel, RuntimeModuleHandle registration)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(registration);
        if (!kernel.IsRegistrationOpen) throw new InvalidOperationException("Open the subscription gates before the runtime starts.");
        if (Opened.TryGetValue(kernel, out _)) throw new InvalidOperationException("This kernel's subscription gates are already open.");
        var manifest = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry");
        var capabilities = manifest.GetProperty("capabilities").EnumerateArray().ToDictionary(
            c => c.GetProperty("id").GetString()!,
            c => (Version: c.GetProperty("version").GetString()!, Kind: c.GetProperty("kind").GetString()!,
                Domain: c.GetProperty("graph").GetProperty("domains")[0].GetString()!),
            StringComparer.Ordinal);
        var providers = manifest.GetProperty("providers").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("version").GetString()!, StringComparer.Ordinal);
        var pins = manifest.GetProperty("bindings").EnumerateArray().ToDictionary(
            b => b.GetProperty("id").GetString()!,
            b => (Capability: b.GetProperty("capabilityId").GetString()!, Provider: b.GetProperty("providerId").GetString()!,
                Handler: b.TryGetProperty("handler", out var handler) && handler.ValueKind == JsonValueKind.String ? handler.GetString()! : ""),
            StringComparer.Ordinal);
        var support = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("bindingSupport").EnumerateArray().ToDictionary(
            s => s.GetProperty("bindingId").GetString()!,
            s => s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!).ToArray(),
            StringComparer.Ordinal);
        if (!pins.ContainsKey(BranchBinding)) throw new InvalidOperationException("The kernel's control module is not registered.");
        // Only a binding whose capability is a trigger can carry the entrypoint of a plan, and only the rows that
        // publish have one: a value row's binding is answered by its own evaluator and subscribes to nothing.
        // The gate table is the registration's own list of bindings, so a row added to the module's contract is
        // gated by this call without a second table here naming it.
        var bindings = registration.SubscriptionGates().Keys
            .Where(id => capabilities[pins[id].Capability].Kind == "trigger")
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (bindings.Length == 0) throw new InvalidOperationException("The registration publishes under no trigger binding.");
        var contracts = bindings.ToDictionary(id => id, id => kernel.ResolveGraphContract(
            pins[id].Capability, capabilities[pins[id].Capability].Version, RuntimeJson.EmptyObject), StringComparer.Ordinal);
        var carrier = bindings.Where(id => contracts[id].GetProperty("parameters").EnumerateArray().All(Satisfiable)).ToArray();
        foreach (var binding in bindings.Where(id => !carrier.Contains(id)))
            Ungated.Add(binding);
        if (carrier.Length == 0) throw new InvalidOperationException("No binding of this registration can carry a plan entrypoint: " + string.Join(" ", Ungated));
        kernel.RegisterModule(Fixture(carrier.Select(id => contracts[id])), RuntimeLogLevel.Off);
        Opened.Add(kernel, Opened);
        var branchContract = kernel.ResolveGraphContract(BranchCapability, capabilities[BranchCapability].Version, RuntimeJson.EmptyObject);
        var candidates = carrier.Select(binding => Plan(kernel, binding, pins, capabilities, providers, support,
            contracts[binding], branchContract)).ToArray();
        var refused = kernel.LoadPlans(candidates).Where(outcome => !outcome.Loaded).ToArray();
        if (refused.Length != 0) throw new RuntimeContractException(refused[0].Code!, refused[0].Code + ": " + refused[0].Detail);
        // A plan that loaded is not proof the gate moved: the gate is refreshed where the subscription table is
        // rebuilt, so a binding this helper mounted on must report a subscriber now. A helper that silently
        // subscribed to nothing would leave every fixture asserting on facts that were never built.
        var shut = carrier.Where(binding => !registration.SubscriptionGate(binding).HasSubscribers).ToArray();
        if (shut.Length != 0) throw new InvalidOperationException("A mounted plan did not open these gates: " + string.Join(" ", shut));
    }

    /// <summary>One plan subscribing to one binding. The pin table is the binding plus the kernel's branch, which
    /// is also the exact closure of what the graph uses, and the entrypoint is the binding's own trigger with the
    /// branch as its one step.</summary>
    private static PlanCandidate Plan(RuntimeKernel kernel, string binding,
        Dictionary<string, (string Capability, string Provider, string Handler)> pins,
        Dictionary<string, (string Version, string Kind, string Domain)> capabilities, Dictionary<string, string> providers,
        Dictionary<string, string[]> support, JsonElement triggerContract, JsonElement branchContract)
    {
        var used = new[] { BranchBinding, binding }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var pinRows = used.Select(id => (object)new
        {
            bindingId = id, capabilityId = pins[id].Capability, capabilityVersion = capabilities[pins[id].Capability].Version,
            providerId = pins[id].Provider, providerVersion = providers[pins[id].Provider], handler = pins[id].Handler
        }).ToArray();
        string planId = "test.subscription-gate." + binding;
        string json = RuntimeJson.From(new
        {
            schemaVersion = 4, kind = "forge-runtime-plan", planId,
            resource = new { id = "test.subscription-gate", revision = "revision-1" },
            runtime = kernel.Identity,
            // The author's own domain, read from the trigger this plan subscribes to: a plan only loads under a
            // domain its trigger's graph declares.
            domain = capabilities[pins[binding].Capability].Domain,
            authority = "host", failurePolicy = "stop-entrypoint",
            permissions = used.SelectMany(id => support[id]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            dependencies = Array.Empty<string>(),
            limits = new
            {
                kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick,
                kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth
            },
            bindings = pinRows,
            attachments = new[] { Attachment },
            entrypoints = new[]
            {
                new
                {
                    nodeId = "Entry", binding = Array.IndexOf(used, binding),
                    layout = Layout(triggerContract), start = 0,
                    steps = new object[]
                    {
                        new
                        {
                            nodeId = "S0_branch", nodeKind = "control", binding = Array.IndexOf(used, BranchBinding),
                            layout = Layout(branchContract),
                            inputs = new object[] { new { slot = Port(branchContract, "inputs", "condition"), value = true } },
                            successors = new int?[] { null, null }
                        }
                    }
                }
            }
        }).GetRawText();
        return PlanCandidate.Loaded(planId + ".plan.json", json);
    }

    /// <summary>The plan's port layout, as the loader re-derives it from the registered contract. The constant
    /// frame is one slot per declared parameter: null is the loader's own spelling of "no constant", and a
    /// resource parameter — the one kind of constant an entrypoint can carry — gets the fixture's reference so
    /// the loader does not refuse the frame as missing.</summary>
    private static object Layout(JsonElement contract) => new
    {
        inputs = Sides(contract, "inputs"), outputs = Sides(contract, "outputs"),
        constants = contract.GetProperty("parameters").EnumerateArray()
            .Select(parameter => ResourceKind(parameter) == null ? null : ResourceConstant).ToArray(),
        promoted = Array.Empty<int>()
    };

    /// <summary>One mount target, since a plan declares at least one. A kind whose target is not an entity carries
    /// no category at all, which is the one shape the loader accepts for it.</summary>
    private static readonly object Attachment = new { kind = MountKind, reference = MountReference };

    /// <summary>The compiled reference one resource parameter carries, in the document's own `{id, revision}`
    /// form. The fixture's own provider answers for it below, so the reference resolves to the one resource that
    /// provider holds.</summary>
    private static readonly object ResourceConstant = new { id = FixtureResource, revision = "revision-1" };
    private const string FixtureResource = "test.subscription-gate";

    /// <summary>The resource kind a declared parameter reads, or null for a parameter that is not a resource:
    /// only an entrypoint's compile-time constants are the frame this answers for.</summary>
    private static string? ResourceKind(JsonElement parameter)
        => parameter.GetProperty("type").GetString() == "resource"
            && parameter.TryGetProperty("resourceKind", out var kind) && kind.ValueKind == JsonValueKind.String
            ? kind.GetString() : null;

    /// <summary>Whether a declared trigger parameter can be written in a plan's constant frame at all. A resource
    /// parameter can be, but only through the kind it declares — the loader resolves a resource constant through
    /// that kind's owner — so a resource parameter that declares none is one no plan can carry.</summary>
    private static bool Satisfiable(JsonElement parameter)
        => parameter.GetProperty("type").GetString() != "resource" || ResourceKind(parameter) != null;

    /// <summary>The provider this helper registers its own kinds under. It declares no capability and no binding:
    /// the two things it owns are the mount kind, whose matcher is the answer that claims nothing, and a resource
    /// provider for the kinds the gated triggers declare, so a plan can carry the constant frame those rows
    /// require without naming a resource the package under test does not have.</summary>
    private static RuntimeModule Fixture(IEnumerable<JsonElement> triggerContracts)
    {
        var kinds = triggerContracts.SelectMany(contract => contract.GetProperty("parameters").EnumerateArray())
            .Select(ResourceKind).Where(kind => kind != null).Select(kind => kind!)
            .Distinct(StringComparer.Ordinal).OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
        return new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = FixtureProvider, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(),
            bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(StringComparer.Ordinal), Array.Empty<BindingSupport>())
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
            {
                [MountKind] = AttachmentMatcherRegistration.ByScope((_, _) => false)
            },
            ResourceProviders = kinds.ToDictionary(kind => kind,
                kind => RuntimeResourceProvider.Of(Array.Empty<ResourceRef>, id => new ResourceRef(kind, id)), StringComparer.Ordinal)
        };
    }

    private static int Port(JsonElement contract, string side, string id)
        => contract.GetProperty(side).EnumerateArray().Select((port, position) => (port, position))
            .Single(x => x.port.GetProperty("id").GetString() == id).position;

    /// <summary>The dense slot layout the plan loader re-derives from the registered contract. It is
    /// assembly-internal and this helper needs the same rule the compiler publishes, so it is read here rather
    /// than restated — a restated layout would only prove the fixture agrees with itself.</summary>
    private static object Sides(JsonElement contract, string side)
        => typeof(RuntimeKernel).Assembly.GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!
            .GetMethod("Layout", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { contract, side })!;
}
