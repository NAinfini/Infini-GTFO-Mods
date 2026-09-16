using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>
/// The kernel's `owner` tier. An owner step is a real step of a real plan whose write is only correct on the
/// machine that owns the thing it changes — a weapon's magazine belongs to the inventory that holds it, and the
/// game's own reload and clip writes act on whichever machine runs them. The host therefore owns the decision
/// (which step runs, on which inputs, at which tick) and the holder's client owns the write, exactly the split
/// the `presentation` tier makes for a screen.
///
/// The one thing that differs from `presentation` is what the recipient's write is: a screen write commits
/// nothing, and this one commits world state on a replica. The recipient's result is therefore kept under the
/// ordinary rules — a committed result with facts is allowed and its facts are published on the machine that
/// performed the write — while the host's own receipt for the step reports the dispatch rather than a commit it
/// did not make.
///
/// The route is the same one the presentation tier uses, because it is the same request: a `CommandRequest`
/// addressed to one session, marked with this tier's own node index. Nothing about the message layout is new; only
/// the marker and the tier's answer to "does this handler commit" are.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>The refusal for an owner step whose holder cannot be named. The tier refuses it by name, the same
    /// way the presentation tier refuses a step with no recipients: an action addressed to nobody is not a
    /// broadcast, and a silently accepted one would report a write that never happened.</summary>
    public const string OwnerHolderCode = "owner-holder";

    private readonly List<OwnerOutput> ownerOutputs = new();

    /// <summary>Whether this step is the `owner` tier. Like the presentation tier, the tier is a registration
    /// fact of the capability the step's binding implements, read once when the plan loaded rather than inferred
    /// from a port or an id.</summary>
    private static bool IsOwnerStep(ResolvedStep step) => step.Execution == "owner";

    /// <summary>
    /// The host's half of an owner step: the holder's session, then the intent the network layer sends. The
    /// step's own inputs are already resolved by the walk and are what travels, so the holder executes exactly
    /// what the host decided, on the same plan and the same step.
    /// </summary>
    private string? EnterOwner(LoadedPlan plan, ResolvedStep step, Pending pending, string commandId,
        JsonElement inputs, out CommandResult result)
    {
        result = null!;
        var provider = step.ProviderId;
        var failure = ResolveHolder(provider, step, inputs, out var holder);
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
        ownerOutputs.Add(new OwnerOutput(provider, plan.Plan.Id, step.NodeId, commandId, step.BindingId,
            step.Frames.Index, holder, pending.Event.EventId, pending.Event.ScopeId, WorldEpoch,
            pending.Event.SimulationTick, RuntimeJson.Parse(payload)));
        // The host's receipt is the dispatch and nothing else: it committed nothing here, and a following step
        // must not read this receipt as a world write it made. A confirmed commit on a partial continues the walk,
        // which is what lets an owner step sit in the middle of an entry.
        result = CommandResult.Partial(RuntimeJson.EmptyObject, CommitStates.Confirmed, Array.Empty<RuntimeFact>());
        return null;
    }

    /// <summary>
    /// Which player session holds what this step is about, answered by the module that owns the step's binding.
    /// There is no implicit holder: a provider that declares an owner capability registers a resolver for it, and
    /// a step whose resolver answers null — or a capability no resolver answers for — is refused by name.
    /// </summary>
    private RuntimeContractException? ResolveHolder(string provider, ResolvedStep step, JsonElement inputs,
        out string holder)
    {
        holder = "";
        if (!modules.ContainsKey(provider))
            return new RuntimeContractException("binding-lifecycle", step.BindingId);
        var capabilityId = step.CapabilityId;
        if (!registry.OwnerSessions.TryGetValue(capabilityId, out var registered) || registered.Holder == null)
            return new RuntimeContractException(OwnerHolderCode,
                "No owner-session resolver is registered for " + capabilityId + " (registered: "
                + string.Join(",", registry.OwnerSessions.Keys) + ").");
        // The subject is the step's own entity input: an owner step changes one instance, and the instance is
        // named by the port the plan wired. A step whose inputs carry no entity at all has no subject to route by.
        EntityReference? subject = null;
        if (inputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var input in step.Inputs)
            {
                if (RuntimeJson.Text(input.Port, "type") != "entity") continue;
                if (!inputs.TryGetProperty(input.Name, out var value) || value.ValueKind != JsonValueKind.Object) continue;
                var candidate = RuntimeJson.Entity(value);
                if (!string.IsNullOrEmpty(candidate.Id)) { subject = candidate; break; }
            }
        }
        if (subject == null)
            return new RuntimeContractException(OwnerHolderCode, step.NodeId + " names no entity to route by.");
        var answer = registered.Holder(subject!);
        if (string.IsNullOrEmpty(answer))
            return new RuntimeContractException(OwnerHolderCode, step.NodeId + " names no holder this build can reach.");
        holder = answer!;
        return null;
    }

    /// <summary>
    /// The holder's half: execute one owner command the host decided. The plan and step are named by identity and
    /// resolved in this kernel's own registry, so the holder runs the same handler on the same inputs the host
    /// resolved; the command is invoked with <see cref="CommandContext.IsHost"/> false, because the advance that
    /// decided the timing ran on another machine.
    ///
    /// Unlike the presentation entry point this one keeps the handler's own result, commit state and facts: this
    /// is a real write on a real machine, and the facts it publishes travel back through the ordinary fact
    /// channel. It never publishes them itself — the handler's result is returned to its caller, which is the
    /// network layer, exactly as a host-tier dispatch does.
    /// </summary>
    public CommandResult ExecuteOwnerCommand(string planId, int stepIndex, string commandId, string eventId,
        string scopeId, long simulationTick, JsonElement inputs)
    {
        Thread();
        try
        {
            RuntimeJson.Require(RuntimeJson.IsId(planId), "plan-id", planId);
            RuntimeJson.Text(commandId); RuntimeJson.Text(scopeId); RuntimeJson.Text(eventId);
            RuntimeJson.Integer(simulationTick, 0);
            RuntimeJson.Require(worldStarted, "dispatch-lifecycle", "An owner command needs a started world.");
            RuntimeJson.Require(inputs.ValueKind == JsonValueKind.Object, "invalid-owner-input",
                "Owner command inputs must be an object keyed by input port id.");
            RuntimeJson.Require(plans.ContainsKey(planId), "plan-unloaded", planId);
            var plan = plans[planId];
            var step = plan.Plan.Entries.SelectMany(entry => entry.Steps).FirstOrDefault(candidate => candidate.Frames.Index == stepIndex)
                ?? throw new RuntimeContractException("owner-step", planId + "." + stepIndex);
            RuntimeJson.Require(IsOwnerStep(step), "owner-step", step.NodeId);
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
                resolved.Add(input.Name, RuntimeJson.EnumPortToHandlerValue(value, input.Port));
            }
            var parameters = RuntimeJson.ResolveEnumParameters(step.Parameters, registry.Capabilities[step.CapabilityId]);
            var origin = new RuntimeEvent(eventId, step.BindingId, WorldEpoch, simulationTick, scopeId, RuntimeJson.EmptyObject);
            var context = new CommandContext(origin, simulationTick, commandId, planId, plan.Plan.ResourceId,
                plan.Plan.ResourceRevision, step.NodeId, parameters, RuntimeJson.From(resolved), false);
            var result = NormalizeInvokedResult(handler!(context));
            if (!CommandResultRules.TryValidate(result, out var violation))
                return CommandResult.FailedUnknown(result.Outputs, "invalid-owner-result",
                    "An owner handler returned an invalid result (" + violation + ").");
            // The holder's own write really happened here, so a confirmed result's facts are published here too,
            // under the owning provider and inside the step's own scope. This is the difference from the
            // presentation tier, which publishes nothing because it writes nothing.
            return PublishOwnerFacts(result, step.BindingId, scopeId, simulationTick);
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

    /// <summary>
    /// The facts of one owner command's result, published on the machine that performed the write. A fact is
    /// queued under the owning provider's own handle and carries the scope the command arrived in, so scope
    /// cancellation reaches it exactly as it reaches a fact published on the host.
    ///
    /// A fact whose provider is no longer registered is reported rather than dropped: the handler answered with a
    /// fact, and "the module that owns it is gone" is a result worth returning.
    /// </summary>
    private CommandResult PublishOwnerFacts(CommandResult result, string stepBindingId, string scopeId, long simulationTick)
    {
        if (result.Facts.Count == 0) return result;
        var owner = RuntimeJson.Text(registry.Bindings[stepBindingId], "providerId");
        if (!modules.TryGetValue(owner, out var moduleGeneration))
            return CommandResult.FailedUnknown(result.Outputs, "module-unregistered",
                "The provider " + owner + " that would publish this command's facts is not registered.");
        var handle = new RuntimeModuleHandle(this, owner, moduleGeneration);
        var failures = new List<string>();
        for (var index = 0; index < result.Facts.Count; index++)
        {
            var fact = result.Facts[index];
            try
            {
                var dispatch = Publish(handle, new RuntimeEvent("fact:" + WorldEpoch + ":" + (++sequence),
                    fact.BindingId, WorldEpoch, simulationTick, scopeId, fact.Outputs));
                if (dispatch.Status == "rejected") failures.Add(fact.BindingId + ":" + dispatch.Code);
            }
            catch (RuntimeContractException error) { failures.Add(fact.BindingId + ":" + error.Code); }
        }
        return failures.Count == 0
            ? result
            : CommandResult.FailedUnknown(result.Outputs, "fact-refused", string.Join(",", failures));
    }
}
