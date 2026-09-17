using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>
/// The kernel's `presentation` tier. A presentation step is a real step of a real plan, but what it writes is the
/// game's own presentation — a camera effect, a HUD line, a fog volume — so the split of responsibility is
/// different from every other tier: the host owns the decision (which step runs, on which inputs, for which
/// players), the recipient owns the write. Nothing here commits world state, and no presentation step ever
/// produces a fact.
/// <para>
/// One execution path does both halves. The host's walk resolves the step and its recipients and records an
/// intent instead of invoking a handler; a recipient hands that same intent back as a command request — over the
/// network layer's own request channel — and <see cref="ExecutePresentationCommand"/> invokes the very same
/// registered handler through the very same result validation, with <see cref="CommandContext.IsHost"/> false
/// because the advance that decided the timing ran elsewhere.
/// </para>
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>Player sessions one presentation step may address. The set is a routing list, not a value frame,
    /// so it is bounded here: a plan that means "everyone" says so through its own recipient resolver.</summary>
    public const int MaximumPresentationRecipients = 64;
    /// <summary>The largest input frame one presentation command may carry on the wire: the payload slot the
    /// network layer reserves for a command request. A step whose inputs need more is refused rather than
    /// truncated.</summary>
    public const int MaximumPresentationInputBytes = 512;

    private readonly List<PresentationOutput> presentationOutputs = new();

    /// <summary>Whether this step is the `presentation` tier. The tier is a registration fact of the capability
    /// the step's binding implements, read once when the plan was loaded and never inferred from the node's ports
    /// or from its id; a module that unregisters takes its plans with it, so a live step's tier cannot change.</summary>
    private static bool IsPresentationStep(ResolvedStep step) => step.Execution == "presentation";

    /// <summary>
    /// The host's half of a presentation step: the recipient set, then the intent the network layer sends. The
    /// step's own inputs are already resolved by the walk and are what travels — a recipient presents exactly what
    /// the host decided, and the fact that the write lands on a replica is carried by the request itself.
    /// </summary>
    private string? EnterPresentation(LoadedPlan plan, ResolvedStep step, Pending pending, string commandId,
        JsonElement inputs, out CommandResult result)
    {
        result = null!;
        var provider = step.ProviderId;
        var failure = ResolvePresentationRecipients(provider, step, inputs, out var recipients);
        if (failure != null)
        {
            result = CommandResult.Rejected(failure.Code, failure.Message);
            return failure.Code;
        }
        var payload = inputs.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaximumPresentationInputBytes)
        {
            result = CommandResult.Rejected(RuntimeAbiCodes.PresentationInputBudget, step.NodeId);
            return RuntimeAbiCodes.PresentationInputBudget;
        }
        presentationOutputs.Add(new PresentationOutput(provider, plan.Plan.Id, step.NodeId, commandId, step.BindingId,
            step.Frames.Index, recipients, pending.Event.EventId, pending.Event.ScopeId, WorldEpoch,
            pending.Event.SimulationTick, RuntimeJson.Parse(payload)));
        result = CommandResult.Partial(RuntimeJson.EmptyObject, CommitStates.None, Array.Empty<RuntimeFact>());
        return null;
    }

    /// <summary>
    /// Which player sessions a step is addressed to, answered by the module that owns the step's binding from the
    /// entity references the step's own `recipients` port carries. There is no implicit set: a provider that
    /// declares a presentation capability registers a resolver, a step no resolver answers for is refused by name,
    /// and a step whose recipients cannot be converted is refused rather than widened — the same rule the hosting
    /// side of a request follows. Narrowing here is what keeps one player's value frame off every other machine.
    /// </summary>
    private RuntimeContractException? ResolvePresentationRecipients(string provider, ResolvedStep step, JsonElement inputs,
        out IReadOnlyList<string> recipients)
    {
        recipients = Array.Empty<string>();
        if (!modules.ContainsKey(provider))
            return new RuntimeContractException("binding-lifecycle", step.BindingId);
        if (!registry.PresentationSessions.TryGetValue(provider, out var registered) || registered.Sessions == null)
            return new RuntimeContractException(RuntimeAbiCodes.PresentationRecipient,
                "No player-session provider is registered for " + provider + " (registered: " + string.Join(",", registry.PresentationSessions.Keys) + ").");
        var claimed = registered.Sessions(AddressedRecipients(step, inputs));
        if (claimed == null || claimed.Count == 0)
            return new RuntimeContractException(RuntimeAbiCodes.PresentationRecipient,
                "A presentation step must address at least one player: " + step.NodeId);
        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var session in claimed)
        {
            if (string.IsNullOrEmpty(session))
                return new RuntimeContractException(RuntimeAbiCodes.PresentationRecipient, step.NodeId);
            distinct.Add(session);
        }
        if (distinct.Count > MaximumPresentationRecipients)
            return new RuntimeContractException(RuntimeAbiCodes.PresentationRecipient,
                "A presentation step addresses at most " + MaximumPresentationRecipients + " players.");
        recipients = distinct.ToArray();
        return null;
    }

    /// <summary>
    /// The entity references one presentation step addresses: the resolved value of the port the capability's own
    /// `recipients` declaration names, which every presentation action row declares and no plan may leave unwired.
    /// Null means the step carries none this kernel can read, and the provider answers that with its own refusal —
    /// never with the whole session.
    /// </summary>
    private IReadOnlyList<EntityReference>? AddressedRecipients(ResolvedStep step, JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object) return null;
        if (!registry.Capabilities.TryGetValue(step.CapabilityId, out var capability)) return null;
        if (!capability.TryGetProperty("graph", out var graph) || !graph.TryGetProperty("recipients", out var declared)) return null;
        var port = RuntimeJson.Text(declared, "input");
        if (!inputs.TryGetProperty(port, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Select(RuntimeJson.Entity)
                .Where(reference => !string.IsNullOrEmpty(reference.Id)).ToArray();
        var single = RuntimeJson.Entity(value);
        return string.IsNullOrEmpty(single.Id) ? null : new[] { single };
    }

    /// <summary>
    /// The recipient's half: execute one presentation command the host decided. The plan and step are named by
    /// identity and resolved in this kernel's own registry, so a replica runs the same handler on the same inputs
    /// the host resolved; the command is invoked with <see cref="CommandContext.IsHost"/> false, and a handler
    /// that reports anything but a non-committing result is refused rather than trusted. No fact is published and
    /// no world state is written by this path, whatever a handler returns.
    /// </summary>
    public CommandResult ExecutePresentationCommand(string planId, int stepIndex, string commandId, string eventId,
        string scopeId, long simulationTick, JsonElement inputs)
    {
        Thread();
        try
        {
            RuntimeJson.Require(RuntimeJson.IsId(planId), "plan-id", planId);
            RuntimeJson.Text(commandId); RuntimeJson.Text(scopeId); RuntimeJson.Text(eventId);
            RuntimeJson.Integer(simulationTick, 0);
            RuntimeJson.Require(worldStarted, "dispatch-lifecycle", "Presentation needs a started world.");
            RuntimeJson.Require(inputs.ValueKind == JsonValueKind.Object, "invalid-presentation-input",
                "Presentation inputs must be an object keyed by input port id.");
            RuntimeJson.Require(plans.ContainsKey(planId), "plan-unloaded", planId);
            var plan = plans[planId];
            var step = plan.Plan.Entries.SelectMany(entry => entry.Steps).FirstOrDefault(candidate => candidate.Frames.Index == stepIndex)
                ?? throw new RuntimeContractException("presentation-step", planId + "." + stepIndex);
            RuntimeJson.Require(IsPresentationStep(step), "presentation-step", step.NodeId);
            RuntimeJson.Require(plan.Modules.All(module => IsRegistered(module.Key, module.Value)), "binding-lifecycle", step.BindingId);
            RuntimeJson.Require(registry.Handlers.TryGetValue(step.BindingId, out var handler), "missing-handler", step.BindingId);
            var resolved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var input in step.Inputs)
            {
                if (!inputs.TryGetProperty(input.Name, out var value))
                {
                    RuntimeJson.Require(RuntimeJson.Flag(input.Port, "optional"), "missing-input", step.NodeId + "." + input.Name);
                    continue;
                }
                ResolveValue(value, input.Port);
                ValidateEntities(value, input.Port);
                // The same handler boundary every dispatched input crosses: an enum is resolved from its compiled
                // index to its member name right before the handler reads it, so a recipient decodes exactly what
                // the host encoded.
                resolved.Add(input.Name, RuntimeJson.EnumPortToHandlerValue(value, input.Port));
            }
            var origin = new RuntimeEvent(eventId, step.BindingId, WorldEpoch, simulationTick, scopeId, RuntimeJson.EmptyObject);
            var context = new CommandContext(origin, simulationTick, commandId, planId, plan.Plan.ResourceId,
                plan.Plan.ResourceRevision, step.NodeId, step.Parameters, RuntimeJson.From(resolved), false, EntityInstance);
            var result = NormalizeInvokedResult(handler!(context));
            if (!CommandResultRules.TryValidate(result, out var violation))
                return CommandResult.FailedUnknown(result.Outputs, "invalid-presentation-result",
                    "A presentation handler returned an invalid result (" + violation + ").");
            if (result.Status != CommandStatuses.Rejected && result.Status != CommandStatuses.Failed
                && result.Status != CommandStatuses.Cancelled && result.Status != CommandStatuses.Expired
                && result.CommitState != CommitStates.None)
            {
                // The tier's own invariant, refused rather than softened: a presentation write commits nothing, so
                // a handler that reports a commit is claiming a world write this path never performs.
                return CommandResult.Rejected(RuntimeAbiCodes.PresentationCommit,
                    "A presentation handler reports a committed result; got " + result.Status + "/" + result.CommitState + ".");
            }
            // The handler's own result is the answer, unchanged: the non-committing result is part of the tier's
            // contract, and what the handler reported as done is what reached the screen.
            return result;
        }
        catch (RuntimeContractException error)
        {
            return CommandResult.Rejected(error.Code, CommandResult.TruncateDetail(error.Message));
        }
        catch (Exception error)
        {
            return CommandResult.FailedUnknown("handler-exception", CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message));
        }
    }
}
