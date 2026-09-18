using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>
/// The kernel's variable, level-object and cross-module message half. Everything here hangs off the one store and
/// the one dispatch walk: `g-var`/`g-object` read and write <see cref="RuntimeVariableStore"/>, `g-once` latches in
/// it, `g-message` publishes through <see cref="RuntimeKernel.Publish"/>, and `g-wait` suspends the walk and lets
/// the publish that wakes it re-enter the same entry.
///
/// Host authority is not a policy here, it is the dispatch path: every one of these steps runs inside an advance
/// that only the host performs, so a client never computes a value. What a client has is the snapshot the host
/// sends (<see cref="ExportVariables"/>), which is also what a late joiner is handed.
/// </summary>
public sealed partial class RuntimeKernel
{
    public const int MaximumWaits = 256;

    private readonly RuntimeVariableStore variables = new();
    /// <summary>The trigger option state. It sits beside the variable store because both are host state a
    /// checkpoint carries, and it is a separate table because nothing in it is authored, named or read by a plan.</summary>
    internal readonly RuntimeTriggerGateStore triggerGates = new();
    /// <summary>The suspended `g-wait` steps, by the binding they are waiting on. A wait is the dispatch's own
    /// resource: it is released by the timeout, by the event that wakes it, or by the world ending.</summary>
    private readonly Dictionary<string, List<WaitJob>> waits = new(StringComparer.Ordinal);

    /// <summary>One suspended `g-wait`. It carries the work item and step to re-enter, the two outputs it may leave
    /// by, and its own handle so a `cancel` on that handle ends it.</summary>
    private sealed class WaitJob
    {
        internal RuntimeModuleHandle Handle = null!;
        internal Work Item = null!;
        internal int Step;
        internal int ReceivedOutput;
        internal int TimeoutOutput;
        internal string Target = "";
        internal string? Message;
        internal JsonElement HandleValue = RuntimeJson.EmptyObject;
        /// <summary>Set by whichever of the two outcomes happened first; the other pending is then skipped.</summary>
        internal bool Resolved;
        internal int ResumeOutput = -1;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Plan lifecycle
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Registers a loaded plan's declarations. Called after the plan parsed, so a conflicting name is a
    /// rejected plan rather than a half-loaded one. The entry points' own trigger options are registered in the same
    /// place and for the same reason: they are a structural property of the plan, proved once at load.</summary>
    private void DeclareVariables(ResolvedPlan plan)
    {
        variables.Declare(plan.Id, plan.Variables);
        triggerGates.Declare(plan.Id, plan.Entries);
    }

    /// <summary>Forgets a plan's declarations and its module-scope values. Values of the other scopes stay: they
    /// belong to the world, not to the file that declared them.</summary>
    private void ForgetVariables(string planId)
    {
        variables.ForgetPlan(planId);
        triggerGates.ForgetPlan(planId);
        variables.ClearScopePrefix(VariableScopeKinds.Module, ModuleMount(planId, ""));
        foreach (var key in waits.Keys.ToArray())
        {
            waits[key].RemoveAll(job => job.Item.Plan.Plan.Id == planId);
            if (waits[key].Count == 0) waits.Remove(key);
        }
    }

    /// <summary>The mount key of one module-scope variable: the plan and the entrypoint it hangs on. A module is an
    /// editor construct flattened into the plan, so its mount point is the entrypoint a behaviour was mounted
    /// through, and two entrypoints of one plan hold separate values.</summary>
    private static string ModuleMount(string planId, string entry) => planId + "/" + entry;

    /// <summary>Every value and latch the world holds, dropped when the world ends. Declarations stay: the plans
    /// are still loaded and re-declare the same names in the next world. The gate store is begun with the world's
    /// die seed for the same reason: the counters belong to the world, and the seed is drawn once per world.</summary>
    private void BeginVariableWorld(long gateSeed)
    {
        variables.ClearValues();
        triggerGates.BeginWorld(gateSeed);
        waits.Clear();
        RefreshSubscriptionGates();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Control steps
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Whether one control capability id is dispatched here rather than by the first-step walker.</summary>
    private static bool IsVariableControl(string? control) => control is
        VariableContracts.ReadCapability or VariableContracts.WriteCapability or VariableContracts.OnceCapability
        or VariableContracts.WaitCapability or VariableContracts.EmitCapability
        or VariableContracts.NamedReadCapability or VariableContracts.NamedWriteCapability;

    /// <summary>
    /// One variable, object, latch, message or wait step. A null return leaves the walk where the step sent it: a
    /// value step continues at `next`, a `once` at `first` or `later`, and a `g-wait` suspends it until the event
    /// it named arrives or its deadline passes. A branch with no successor ends naturally.
    /// </summary>
    private string? EnterVariableControl(Work item, int stepIndex, ResolvedStep step, Pending pending,
        JsonElement inputs, JsonElement parameters, out int? cursor)
    {
        cursor = null;
        switch (step.Control)
        {
            case VariableContracts.ReadCapability:
            {
                var (declaration, scope) = VariableAddress(item, parameters, inputs);
                var value = variables.Read(declaration, scope);
                stepFrames[stepIndex] = RuntimeJson.From(new { value });
                cursor = Successor(step, 0);
                return null;
            }
            case VariableContracts.WriteCapability:
            {
                var (declaration, scope) = VariableAddress(item, parameters, inputs);
                var value = inputs.TryGetProperty("value", out var raw) ? raw : default;
                RuntimeVariableContract.Validate(value, declaration.Type, declaration.Name);
                // The old value is read before the write for the same reason the read row reads it: an address
                // nothing has written yet answers with the declared initial value, so `previous` is never absent for
                // a value class the declaration itself can hold.
                var previous = variables.Read(declaration, scope);
                var written = variables.Set(declaration, scope, value);
                stepFrames[stepIndex] = RuntimeJson.From(new { written, previous });
                cursor = Successor(step, 0);
                return null;
            }
            case VariableContracts.NamedReadCapability:
            {
                var declaration = NamedObject(parameters);
                var bound = variables.Has(declaration.Name, RuntimeVariableScope.Named(declaration.Name))
                    ? variables.Read(declaration, RuntimeVariableScope.Named(declaration.Name)) : NullValue;
                stepFrames[stepIndex] = RuntimeJson.From(new
                {
                    value = declaration.Type == VariableValueTypes.Entity ? bound : NullValue,
                    wave = declaration.Type == VariableValueTypes.Handle ? bound : NullValue
                });
                cursor = Successor(step, 0);
                return null;
            }
            case VariableContracts.NamedWriteCapability:
            {
                var declaration = NamedObject(parameters);
                var value = declaration.Type == VariableValueTypes.Entity
                    ? Value(inputs, "value", declaration)
                    : Value(inputs, "wave", declaration);
                RuntimeVariableContract.Validate(value, declaration.Type, declaration.Name);
                var written = variables.Set(declaration, RuntimeVariableScope.Named(declaration.Name), value);
                stepFrames[stepIndex] = RuntimeJson.From(new { written });
                cursor = Successor(step, 0);
                return null;
            }
            case VariableContracts.OnceCapability:
            {
                // The latch is keyed by the mount point: this plan's entrypoint and this node. A re-load of the same
                // behaviour therefore reaches the same latch, and the checkpoint restores it with the values. The
                // separator is a colon because the key is written into the checkpoint as JSON, and neither a plan id
                // nor a node id may contain one.
                var key = item.Plan.Plan.Id + ":" + item.Entry.NodeId + ":" + step.NodeId;
                cursor = Successor(step, variables.ClaimOnce(key) ? 0 : 1);
                return null;
            }
            case VariableContracts.EmitCapability:
            {
                var message = RuntimeJson.Text(parameters, "message");
                RuntimeJson.Require(MessageName(message), "message-name", message);
                var payload = inputs.TryGetProperty("value", out var raw) ? raw : default;
                PublishMessage(message, payload, pending.Event.ScopeId);
                cursor = Successor(step, 0);
                return null;
            }
            case VariableContracts.WaitCapability:
                return EnterWait(item, stepIndex, step, pending, parameters, out cursor);
            default:
                return RuntimeAbiCodes.ControlUnsupported;
        }
    }

    private static readonly JsonElement NullValue = RuntimeJson.Parse("null");

    private static JsonElement Value(JsonElement inputs, string port, VariableDeclaration declaration)
    {
        var value = inputs.TryGetProperty(port, out var raw) ? raw : default;
        RuntimeJson.Require(value.ValueKind != JsonValueKind.Undefined, "variable-value", declaration.Name + "." + port);
        return value;
    }

    /// <summary>The one declaration a step names plus the address it reads it at. The declaration owns the scope:
    /// the step's own `value_type` parameter only has to agree with it, so a plan that names a variable of the
    /// wrong type is refused by name instead of reading a payload as the wrong kind.</summary>
    private (VariableDeclaration Declaration, RuntimeVariableScope Scope) VariableAddress(Work item, JsonElement parameters, JsonElement inputs)
    {
        var name = RuntimeJson.Text(parameters, "name");
        var declaration = variables.Declared(name);
        RuntimeJson.Require(declaration != null, "variable-undeclared", name);
        if (parameters.TryGetProperty("value_type", out var portType) && portType.ValueKind == JsonValueKind.String)
            RuntimeJson.Require(VariableValueTypes.IsPortTypeOf(RuntimeJson.Text(portType), declaration!.Type), "variable-type", name);
        return (declaration!, Scope(item, declaration!, inputs));
    }

    /// <summary>The declaration a level-object step names. The object table is the variable store's `named` scope,
    /// so the declaration is what says whether the name holds an entity or a handle.</summary>
    private VariableDeclaration NamedObject(JsonElement parameters)
    {
        var name = RuntimeJson.Text(parameters, "name");
        var declaration = variables.Declared(name);
        RuntimeJson.Require(declaration != null && declaration.Scope == VariableScopeKinds.Named, "variable-undeclared", name);
        return declaration!;
    }

    /// <summary>
    /// Which address a declaration is read at. The scope kind decides the subject, and a scope whose subject the
    /// dispatch cannot supply is refused by name instead of being keyed under an invented one: a level variable has
    /// no subject, a module variable hangs on this entrypoint, a named object is its own name, and every remaining
    /// scope takes the step's `subject` input or the single entity the plan's mount accepted for this event.
    /// </summary>
    private RuntimeVariableScope Scope(Work item, VariableDeclaration declaration, JsonElement inputs)
    {
        switch (declaration.Scope)
        {
            case VariableScopeKinds.Level: return RuntimeVariableScope.Level();
            case VariableScopeKinds.Named: return RuntimeVariableScope.Named(declaration.Name);
            case VariableScopeKinds.Module: return RuntimeVariableScope.Module(ModuleMount(item.Plan.Plan.Id, item.Entry.NodeId));
            case VariableScopeKinds.Weapon:
            {
                var slot = inputs.TryGetProperty("slot", out var raw) && raw.ValueKind == JsonValueKind.Number
                    ? (int)RuntimeJson.Integer(raw, 0, 64) : -1;
                RuntimeJson.Require(slot >= 0, "variable-subject", declaration.Name);
                return RuntimeVariableScope.Weapon(Subject(item, declaration, inputs).Id, slot);
            }
            default:
                return new RuntimeVariableScope(declaration.Scope, Subject(item, declaration, inputs).Id, -1);
        }
    }

    private static EntityReference Subject(Work item, VariableDeclaration declaration, JsonElement inputs)
    {
        if (inputs.TryGetProperty("subject", out var value) && value.ValueKind != JsonValueKind.Null)
            return RuntimeJson.Entity(value);
        RuntimeJson.Require(item.Subjects.Count == 1, "variable-subject", declaration.Name);
        return item.Subjects[0];
    }

    // ---------------------------------------------------------------------------------------------------------
    // Messages
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A message name is one identifier, so the sender and the receiver spell it the same way.</summary>
    private static bool MessageName(string message) => message.Length is > 0 and <= 64 && RuntimeJson.IsId(message);

    /// <summary>
    /// Sends one custom message. It is an ordinary event on the one queue: it is dispatched to every plan mounted
    /// on `forge.trigger.status.event_received`, it wakes the `g-wait` steps that named it, and it is refused by
    /// the same budgets as any other event. The kernel is its publisher, which is the provider that owns the row.
    /// </summary>
    private void PublishMessage(string message, JsonElement value, string scopeId)
    {
        RuntimeJson.Require(modules.TryGetValue(VariableContracts.ProviderId, out var generation) && IsRegistered(VariableContracts.ProviderId, generation),
            "module-unregistered", VariableContracts.ProviderId);
        var handle = new RuntimeModuleHandle(this, VariableContracts.ProviderId, generation);
        var eventId = "message:" + WorldEpoch + ":" + (++sequence);
        var payload = RuntimeJson.From(new { message, value });
        var published = new RuntimeEvent(eventId, VariableContracts.MessageReceivedBinding, WorldEpoch, CurrentTick, scopeId, payload);
        var dispatch = Publish(handle, published);
        RuntimeJson.Require(dispatch.Status is "queued" or "ignored", "message-refused", dispatch.Code);
    }

    // ---------------------------------------------------------------------------------------------------------
    // g-wait
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Suspends the walk until the named message or engine event arrives, or until the deadline passes. The step
    /// leaves by `received` or by `timeout`; both outcomes re-enter the same entrypoint at the same step, so the
    /// flow after a wait is one path and the two exits are what tells them apart. A deadline of zero waits forever.
    /// </summary>
    private string? EnterWait(Work item, int stepIndex, ResolvedStep step, Pending pending, JsonElement parameters, out int? cursor)
    {
        cursor = null;
        var target = RuntimeJson.Text(parameters, "target");
        RuntimeJson.Require(VariableContracts.IsWaitable(target), "wait-event-unsupported", target);
        var message = parameters.TryGetProperty("message", out var raw) && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
        if (VariableContracts.IsMessageEvent(target))
        {
            RuntimeJson.Require(message != null && MessageName(message), "wait-message-missing", step.NodeId);
            // A client receives the host's messages, but it does not suspend its own walk on one: only the host
            // dispatches, and only the host's wait is the one the message resumes.
        }
        var timeout = parameters.TryGetProperty("timeout", out var limit) && limit.ValueKind == JsonValueKind.Number
            ? RuntimeJson.Integer(limit, 1) : 0;
        RuntimeJson.Require(waits.Sum(x => x.Value.Count) < MaximumWaits, "wait-budget", step.NodeId);
        RuntimeJson.Require(Successor(step, 0) is not null, RuntimeAbiCodes.ControlShape, step.NodeId);
        var handleValue = CreateHandle(pending.Provider, "subscription", "encounter", out var slot, item.Plan.Plan.Id + ".wait." + stepIndex);
        var job = new WaitJob
        {
            Handle = new RuntimeModuleHandle(this, pending.Provider, pending.Generation),
            Item = item, Step = stepIndex, ReceivedOutput = 0, TimeoutOutput = 1,
            Target = target, Message = message, HandleValue = handleValue
        };
        if (!waits.TryGetValue(target, out var list)) waits.Add(target, list = new List<WaitJob>());
        list.Add(job);
        // A waiting flow is a consumer of the binding it named, so a publisher's gate is open from here until the
        // wait leaves: the message it waits for is exactly the publish the gate must not swallow.
        RefreshSubscriptionGates();
        stepFrames[stepIndex] = RuntimeJson.From(new { message = message ?? "", payload = NullValue });
        if (timeout > 0)
        {
            var deadline = pending.Event.SimulationTick + timeout;
            RuntimeJson.Integer(deadline);
            // The deadline is a dispatch of its own, queued like any other pending: the walk resumes from it only
            // if no event woke the wait first.
            queue.Enqueue(new Pending(pending.Provider, pending.Generation, pending.Event, new[] { item }, null, ++sequence, null, job, true),
                (deadline, sequence));
        }
        return null;
    }

    /// <summary>
    /// Admits or drops one pending that carries a suspended wait. A pending whose wait already left by the other
    /// exit, or whose handle was cancelled, is dropped without a receipt: it is the timer of a step that no longer
    /// exists, not an event anyone published.
    /// </summary>
    private bool AdmitWait(Pending pending)
    {
        var job = pending.Wait!;
        if (pending.WaitTimeout)
        {
            if (job.Resolved) return false;
            job.Resolved = true; job.ResumeOutput = job.TimeoutOutput;
        }
        else job.ResumeOutput = job.ReceivedOutput;
        return job.Handle.IsRegistered && IsHandleLive(job.Handle, job.HandleValue);
    }

    /// <summary>Re-enters the entry at the wait step: the frame carries what the wait received, so the steps after
    /// it read the message and its payload as the wait node's own outputs.</summary>
    private int? ResumeWait(ResolvedEntry entry, WaitJob job, Pending pending)
    {
        BeginActivation();
        controlWalk.Clear();
        var outputs = pending.Event.Outputs;
        stepFrames[job.Step] = RuntimeJson.From(new
        {
            message = outputs.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : "",
            payload = outputs.TryGetProperty(VariableContracts.MessageValuePort, out var payload) ? payload : NullValue
        });
        return Successor(entry.Steps[job.Step], job.ResumeOutput);
    }

    /// <summary>
    /// Wakes every suspended wait that named this event. Called by the publish that accepted it, before the tick
    /// that accepted it ends, so a message resumes its waiter in the same advance it was sent in. The waiting work
    /// item is enqueued with the received event as its own event: the resumed steps read the event's frame.
    /// </summary>
    private void WakeWaiters(RuntimeEvent value)
    {
        if (waits.Count == 0 || !waits.TryGetValue(value.BindingId, out var list)) return;
        foreach (var job in list.ToArray())
        {
            if (job.Resolved) continue;
            if (job.Message != null && (!value.Outputs.TryGetProperty("message", out var name) || RuntimeJson.Text(name) != job.Message)) continue;
            job.Resolved = true; job.ResumeOutput = job.ReceivedOutput;
            list.Remove(job);
            queue.Enqueue(new Pending(job.Handle.ProviderId, job.Handle.Generation, value, new[] { job.Item }, null, ++sequence,
                EventRowsOf(new[] { job.Item }, value, sequence), job), (value.SimulationTick, sequence));
        }
        if (list.Count == 0) { waits.Remove(value.BindingId); RefreshSubscriptionGates(); }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Host-side state: clients, late joiners, checkpoints and host migration
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The host's whole variable state, as the snapshot a client applies. It is the whole table rather than
    /// a delta, so one snapshot is enough for a late joiner and a client that missed a message is corrected by the
    /// next one.</summary>
    public string ExportVariables()
    {
        ReadThread();
        return RuntimeVariableSnapshot.Encode(WorldEpoch, variables.Snapshot());
    }

    /// <summary>
    /// The entries this advance wrote or cleared, for the delta a client applies between snapshots. The touch set is
    /// the advance's own: it is emptied when the next advance begins, so reading it does not consume it and one
    /// advance may be handed to several recipients.
    /// </summary>
    public string ExportVariableDelta()
    {
        ReadThread();
        return RuntimeVariableSnapshot.Encode(WorldEpoch, variables.Touched());
    }

    /// <summary>Applies the host's snapshot on a client. Values are the host's; a client never computes one, and an
    /// entry the snapshot omits is gone — which is how a cleared object or a dead enemy propagates.</summary>
    public void ApplyVariables(string json)
    {
        Mutable();
        var snapshot = RuntimeVariableSnapshot.Decode(json);
        RuntimeJson.Require(snapshot.WorldEpoch == WorldEpoch, "variable-snapshot", "Snapshot belongs to another world.");
        variables.Restore(RuntimeJson.StableText(RuntimeJson.From(new
        {
            worldEpoch = WorldEpoch,
            entries = snapshot.Entries.Select(entry => new { name = entry.Name, scope = entry.ScopeKind, subject = entry.Subject, slot = entry.Slot, type = entry.Type, value = entry.Value }).ToArray(),
            once = Array.Empty<string>()
        })));
    }

    /// <summary>What a checkpoint has to carry: every value and every `g-once` latch of the world being saved, and
    /// the trigger options' own counters — a threshold half reached is progress the level made.</summary>
    public string CaptureCheckpoint()
    {
        ReadThread();
        return variables.Save(WorldEpoch, triggerGates.Save());
    }

    /// <summary>How many effect instances this host is currently holding: the counters a caller reporting what a
    /// checkpoint does not carry — a duration is re-derived from the card — and the readable window onto an effect a
    /// test or a diagnostic asks about when its module registered no callback worth counting.</summary>
    public int LiveEffects { get { ReadThread(); return effects.Count; } }

    /// <summary>How many gate rows this host holds, and how many booked sets inside them. A caller that wants to
    /// know what a checkpoint carries without reading it.</summary>
    public (int Gates, int Scopes) GateStateCounts() => (triggerGates.Count, triggerGates.ScopeRows);

    /// <summary>What one gated entry point has fired and accumulated for one scope subject, or null where the plan
    /// declares no gate for it. <paramref name="subject"/> is the entity id a per-player or per-instance gate is
    /// booked against, and the empty string — the default — is the level's own set. The one readable window onto
    /// the gate state: an author asking why a card is silent reads these.</summary>
    public (long Fired, long Accumulated)? GateStateOf(string planId, string nodeId, string subject = "")
    {
        ArgumentNullException.ThrowIfNull(planId); ArgumentNullException.ThrowIfNull(nodeId); ArgumentNullException.ThrowIfNull(subject);
        ReadThread();
        return triggerGates.StateOf(planId, nodeId, subject);
    }

    /// <summary>
    /// Restores a checkpoint's values and latches over the plans that are loaded. Enemy-scoped values are dropped
    /// rather than restored: reloading a checkpoint re-creates the level's enemies, so a saved per-enemy value names
    /// an entity that no longer exists, while a named object and a door are the same objects the checkpoint was
    /// taken in. A checkpoint whose world epoch is not this world's is refused by name.
    ///
    /// The restore is announced as its own lifecycle boundary once it has happened: the epoch does not move — the
    /// same expedition continues — but the level's own objects are rebuilt from the game's data, so a package
    /// holding something derived from an instance has to give it up without waiting for a world change that will
    /// never come.
    /// </summary>
    public void RestoreCheckpoint(string json)
    {
        Mutable();
        var checkpoint = RuntimeJson.Parse(json);
        RuntimeJson.Shape(checkpoint, "worldEpoch entries once", "gates");
        RuntimeJson.Require(RuntimeJson.Integer(checkpoint.GetProperty("worldEpoch")) == WorldEpoch, "variable-checkpoint",
            "Checkpoint belongs to another world.");
        variables.Restore(json);
        variables.ClearScope(VariableScopeKinds.Enemy);
        // The gate rows are restored after the values, and only where the loaded plans still declare them: a reload
        // of a checkpoint re-arms the same plans, so the counters come back to the entry points that own them.
        triggerGates.Restore(checkpoint.TryGetProperty("gates", out var gates) ? gates.GetRawText() : "null");
        // The level's own objects were rebuilt by the restore, so a per-instance counter names an entity that is
        // gone: those sets are dropped after the restore rather than carried back with the checkpoint.
        var instanceNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loaded in plans.Values)
            foreach (var entry in loaded.Plan.Entries)
                if (entry.Gate?.Scope == TriggerGateContract.InstanceScope) instanceNodes.Add(entry.NodeId);
        triggerGates.PruneInstances(instanceNodes);
        NotifyLifecycle(RuntimeLifecycleKind.CheckpointRestored);
    }

    /// <summary>
    /// Restores a checkpoint and re-arms the behaviour: the loaded plans are forgotten and the discovered files are
    /// loaded again, then the saved values and latches are put back over them. This replaces suspending the runtime
    /// on a checkpoint reload — the same expedition continues, so its world epoch stays and only the behaviour is
    /// re-armed.
    /// </summary>
    public IReadOnlyList<PlanLoadOutcome> RestoreCheckpoint(string json, IReadOnlyList<PlanCandidate> plans)
    {
        Mutable();
        foreach (var planId in this.plans.Keys.ToArray()) ForgetVariables(planId);
        this.plans.Clear();
        RebuildSubscriptions();
        var outcomes = LoadPlans(plans);
        RestoreCheckpoint(json);
        return outcomes;
    }

    /// <summary>
    /// Drops one subject's variables. An enemy that died and a deployed item that returned to its owner are the two
    /// cases the scope table names, and both are the producer's own fact: the kernel cannot tell a dead enemy from
    /// a live one, so the package that observed it says so here instead of the value being read later as if the
    /// entity were still there.
    /// </summary>
    public int ReleaseVariableScope(string scopeKind, EntityReference subject)
    {
        Mutable();
        RuntimeJson.Text(subject.Id); RuntimeJson.Integer(subject.WorldEpoch); RuntimeJson.Integer(subject.LifeEpoch);
        RuntimeJson.Require(subject.WorldEpoch == WorldEpoch, "stale-world", subject.Id);
        RuntimeJson.Require(VariableScopeKinds.All.Contains(scopeKind), "variable-scope", scopeKind);
        return variables.ClearScope(scopeKind, subject.Id);
    }

    /// <summary>Binds one level object by name. See <see cref="RuntimeNamedObjects"/> for the package-facing
    /// entry point; this is the same call, host-side.</summary>
    public bool BindNamedObject(string name, JsonElement value)
    {
        Mutable();
        RuntimeJson.Require(RuntimeJson.IsId(name), "variable-name", name);
        var declaration = variables.Declared(name);
        RuntimeJson.Require(declaration != null && declaration.Scope == VariableScopeKinds.Named, "variable-undeclared", name);
        RuntimeVariableContract.Validate(value, declaration!.Type, name);
        return variables.Set(declaration, RuntimeVariableScope.Named(name), value);
    }

    /// <summary>Reads one level object by name, null when it was never bound. The value keeps its kind: a caller
    /// holding an entity reads it as one, and a caller holding a handle checks it against the row that consumes it.
    /// </summary>
    public JsonElement? ReadNamedObject(string name)
    {
        ReadThread();
        RuntimeJson.Require(RuntimeJson.IsId(name), "variable-name", name);
        var declaration = variables.Declared(name);
        RuntimeJson.Require(declaration != null && declaration.Scope == VariableScopeKinds.Named, "variable-undeclared", name);
        return variables.Has(name, RuntimeVariableScope.Named(name)) ? variables.Read(declaration!, RuntimeVariableScope.Named(name)) : null;
    }

    /// <summary>Every level object the loaded plans declare, ordinal sorted.</summary>
    public IReadOnlyList<string> NamedObjectNames()
    {
        ReadThread();
        return variables.Declarations().Where(row => row.Scope == VariableScopeKinds.Named).Select(row => row.Name).ToArray();
    }
}
