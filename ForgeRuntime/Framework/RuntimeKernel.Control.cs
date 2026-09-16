using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>
/// The kernel's one control dispatcher. Every control step the plan declares — branch, sequence, delay, interval,
/// repeat, for_each, cancel, present — is walked here, in the same place the plan's successor table is followed, so there is
/// no second router and no control-specific execution path. A control's execution outputs are ordinary forward
/// successors: `next` continues, `pulse`/`body` names the step the next activation starts from, and the kernel
/// holds the continuation (plan, control step, output index, frame snapshot, handle) itself. Nothing rewinds the
/// plan and no region or `iterations` field exists in it.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>One control region being walked, or one loop being run. The successor table and the step
    /// descriptor are copied in because resuming must not look either up again; `Round`/`Rounds` only mean
    /// something on a loop frame.</summary>
    private struct ControlFrame
    {
        internal ResolvedStep Descriptor;
        internal int Step;
        internal int Output;
        internal int Round;
        internal int Rounds;
        internal bool Loop;
        internal bool ForEach;
        internal int[] Successors;
        internal JsonElement Items;
    }

    /// <summary>What a schedule resumes: the work item it belongs to, the control step it re-enters, the output
    /// whose successor is the first step of the new activation, and the handle value that step's frame carries.</summary>
    private sealed record Continuation(Work Work, int Step, int Output, JsonElement HandleValue);
    private readonly Dictionary<int, JsonElement> stepFrames = new();
    /// <summary>The slots of this activation that hold a `pure`/`query` frame: a frame validated against its own
    /// contract when it was evaluated, which no later step writes again inside the activation.</summary>
    private readonly HashSet<int> validatedSlots = new();
    /// <summary>The entities each validated slot's port carries, with the resolver that answered for their kind.
    /// A repeated read of the same frame re-asks these resolvers instead of parsing the frame, and the values are
    /// re-read here rather than only at the frame's own validation because a handler of this activation can still
    /// end an entity's life.</summary>
    private readonly Dictionary<(int Step, string Port), EntityCheck[]> memoEntities = new();
    private readonly List<ControlFrame> controlWalk = new();
    /// <summary>The host's own draw source for a control row that needs a number nobody authored: a random
    /// branch's index. It lives with the kernel rather than with a plan, because the plan is the same file on
    /// every machine and must not carry the number that decides which branch this dispatch takes.</summary>
    private readonly Random draws = new();
    private long stepExecutions;

    /// <summary>Starts an activation: `pure` and `query` results live inside one, so each dispatch, loop round and
    /// schedule resumption re-evaluates on demand instead of reusing what an earlier activation observed.</summary>
    private void BeginActivation()
    {
        stepFrames.Clear();
        validatedSlots.Clear();
        memoEntities.Clear();
        stepExecutions = 0;
    }

    /// <summary>True once this activation has spent its step executions; the dispatch is then refused by name
    /// instead of running a plan that looped further than one activation may.</summary>
    private bool StepBudgetSpent(RuntimeLimits limits) => stepExecutions > limits.MaxStepExecutionsPerDispatch;

    /// <summary>Re-enters a control region after a schedule fired: the control step writes its timer frame again —
    /// the handle a `cancel` later in the pulse body reads — and the walk starts where the continuation said.</summary>
    private int? ResumeActivation(ResolvedEntry entry, Continuation continuation)
    {
        BeginActivation();
        controlWalk.Clear();
        stepFrames[continuation.Step] = TimerFrame(continuation.HandleValue);
        return Successor(entry.Steps[continuation.Step], continuation.Output);
    }

    private static JsonElement TimerFrame(JsonElement handle) => RuntimeJson.From(new { timer = handle });

    /// <summary>The step one output of a control step starts from, or null when the plan wired none.</summary>
    private static int? Successor(ResolvedStep step, int output)
        => output >= 0 && output < step.Successors.Count ? step.Successors[output] : null;

    /// <summary>
    /// Enters one control step. A return value other than null is the code the dispatch is rejected with; a null
    /// return leaves the walk in <paramref name="cursor"/>, which is null when this control suspended the event
    /// (a `delay` waits for its timer) and the path is finished for now.
    /// </summary>
    private string? EnterControl(Work item, int stepIndex, ResolvedStep step, Pending pending,
        JsonElement inputs, JsonElement parameters, out int? cursor)
    {
        cursor = null;
        var plan = item.Plan;
        var successors = step.Successors.Select(value => value ?? -1).ToArray();
        switch (step.Control)
        {
            case "forge.control.flow.branch":
                cursor = Successor(step, Flag(inputs, "condition") ? 0 : 1);
                return null;
            case "forge.control.flow.sequence":
                // Depth first, in output order: each exit is walked to its end before the next one starts.
                controlWalk.Add(new ControlFrame { Descriptor = step, Step = stepIndex, Output = 0, Successors = successors });
                cursor = Successor(step, 0);
                return null;
            case "forge.control.flow.parallel_all":
            {
                // Every branch is entered once, for this one activation, and `next` runs after the last of them.
                // The walk owns a single cursor, so the branches are started one after another inside this
                // dispatch — they share its step budget — and the frame is what makes the last branch fall
                // through to `next` instead of ending the path.
                RuntimeJson.Require(successors.Length >= 2, RuntimeAbiCodes.ControlShape, step.NodeId);
                controlWalk.Add(new ControlFrame { Descriptor = step, Step = stepIndex, Output = 0, Successors = successors });
                cursor = Successor(step, 0);
                return null;
            }
            case "forge.control.flow.random_branch":
            {
                // The host draws one branch at execution time and enters only that one. The draw is the
                // execution's own result: no seed travels in the plan, so two machines can never disagree
                // about which branch ran, and a later dispatch draws again.
                var branches = successors.Length - 1;
                RuntimeJson.Require(branches >= 1, RuntimeAbiCodes.ControlShape, step.NodeId);
                cursor = Successor(step, draws.Next(branches));
                return null;
            }
            case "forge.control.flow.delay":
            {
                // The timer is written on entry; `next` is entered when it fires, not now.
                var failure = ScheduleContinuation(item, stepIndex, step, pending, resumeOutput: 0,
                    new PulseSchedule(Ticks(inputs, "duration"), FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 1), out _);
                return failure;
            }
            case "forge.control.flow.interval":
            {
                // The schedule is the pulse source: `next` runs now, every pulse re-enters the `pulse` successor.
                // An omitted `count` is an unbounded pulse series: it ends only where its handle is cancelled, its
                // scope ends or its world ends, so the job carries no pulse limit at all (ruling 160.2). A count the
                // author did write is still bounded by the control-iteration limit, the same ceiling `repeat` uses.
                int? count = inputs.TryGetProperty("count", out var raw) ? (int)RuntimeJson.Integer(raw, 0) : null;
                RuntimeJson.Require(count == null || (count >= 1 && count <= Limits.MaxControlIterations), RuntimeAbiCodes.IterationBudget, step.NodeId);
                var first = parameters.TryGetProperty("first_pulse", out var policy) && policy.ValueKind == JsonValueKind.String
                    && policy.GetString() == "after_interval" ? FirstPulse.AfterInterval : FirstPulse.Immediate;
                var failure = ScheduleContinuation(item, stepIndex, step, pending, resumeOutput: 1,
                    new PulseSchedule(Ticks(inputs, "interval"), first, MissedPulsePolicy.SkipMissed, count), out _);
                if (failure != null) return failure;
                cursor = Successor(step, 0);
                return null;
            }
            case "forge.control.flow.repeat":
            case "forge.control.flow.for_each":
            {
                var forEach = step.Control == "forge.control.flow.for_each";
                var rounds = forEach ? CandidateCount(inputs, step) : Count(inputs, "count", step);
                if (rounds == 0) { cursor = Successor(step, 0); return null; }
                var frame = new ControlFrame
                {
                    Descriptor = step, Step = stepIndex, Output = 1, Round = 0, Rounds = rounds, Loop = true, ForEach = forEach,
                    Successors = successors, Items = forEach ? inputs.GetProperty("candidates") : default
                };
                controlWalk.Add(frame);
                WriteLoopFrame(frame);
                cursor = Successor(step, 1);
                return null;
            }
            case "forge.control.flow.cancel":
            {
                // The count is what actually happened: zero until a live schedule is really ended, so a handle whose
                // schedule already died reports 0 and fails by name instead of claiming a cancellation.
                var input = step.Inputs.First(port => port.Name == "task");
                stepFrames[stepIndex] = RuntimeJson.From(new { cancelled = 0 });
                var cancelled = inputs.TryGetProperty("task", out var handle) ? CancelHandle(handle, input.Port, step.NodeId) : 0;
                stepFrames[stepIndex] = RuntimeJson.From(new { cancelled });
                cursor = Successor(step, 0);
                return null;
            }
            case "forge.control.flow.restart":
            {
                // The rolling window: the same live timer is re-armed from now, so the handle the delay/interval
                // step published keeps working and the author needs no place to keep it. The answer says whether a
                // live schedule was really re-armed; a handle whose schedule already ended fails the slot check.
                var input = step.Inputs.First(port => port.Name == "task");
                stepFrames[stepIndex] = RuntimeJson.From(new { restarted = false });
                var restarted = inputs.TryGetProperty("task", out var handle) && RestartHandle(handle, input.Port, step.NodeId);
                stepFrames[stepIndex] = RuntimeJson.From(new { restarted });
                cursor = Successor(step, 0);
                return null;
            }
            case "forge.control.flow.present":
            {
                // One value, two ways out. The tested value is published under the port id the plan's own contract
                // declared, so the region the loader narrowed to the `present` exit reads exactly what this step
                // tested; the `missing` region has no reader at all, which is a plan rule the loader enforces
                // rather than a null this code would have to mark.
                var value = inputs.TryGetProperty("value", out var raw) ? raw : NullValue;
                stepFrames[stepIndex] = RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [step.ValuePort!] = value });
                cursor = Successor(step, value.ValueKind == JsonValueKind.Null ? 1 : 0);
                return null;
            }
            default:
                // The variable, object, message and wait rows are the second half of the kernel's own control
                // vocabulary: they route the same walk from the same place, which is why they are dispatched here
                // rather than through a handler.
                return IsVariableControl(step.Control)
                    ? EnterVariableControl(item, stepIndex, step, pending, inputs, parameters, out cursor)
                    : RuntimeAbiCodes.ControlUnsupported;
        }
    }

    /// <summary>Advances the innermost control whose region just ended: the next exit of a sequence, the next round
    /// of a loop, or — when none is left — whatever encloses it. False means the whole walk is done.</summary>
    private bool ResumeControl(out int cursor)
    {
        while (controlWalk.Count > 0)
        {
            var frame = controlWalk[^1];
            if (frame.Loop)
            {
                var round = frame.Round + 1;
                if (round >= frame.Rounds)
                {
                    controlWalk.RemoveAt(controlWalk.Count - 1);
                    // A loop's exit is `next`; a null one means the enclosing control continues instead.
                    if (frame.Successors[0] >= 0) { cursor = frame.Successors[0]; return true; }
                    continue;
                }
                frame.Round = round;
                controlWalk[^1] = frame;
                BeginActivation();
                WriteLoopFrame(frame);
                cursor = frame.Successors[1];
                if (cursor >= 0) return true;
                continue;
            }
            var next = frame.Output + 1;
            while (next < frame.Successors.Length && frame.Successors[next] < 0) next++;
            if (next >= frame.Successors.Length)
            {
                controlWalk.RemoveAt(controlWalk.Count - 1);
                continue;
            }
            frame.Output = next;
            controlWalk[^1] = frame;
            cursor = frame.Successors[next];
            return true;
        }
        cursor = -1;
        return false;
    }

    /// <summary>Writes a loop control step's value outputs for the round it is about to run: `index` counts rounds
    /// and `item` is the candidate the round is about.</summary>
    private void WriteLoopFrame(in ControlFrame frame)
    {
        if (frame.ForEach) stepFrames[frame.Step] = RuntimeJson.From(new { item = frame.Items[frame.Round], index = frame.Round });
        else stepFrames[frame.Step] = RuntimeJson.From(new { index = frame.Round });
    }

    /// <summary>The rounds a `for_each` may run: the candidate count bounded by the input's own budget and by the
    /// per-control iteration ceiling. A candidate set larger than the budget is refused rather than truncated —
    /// the plan asked for all of them.</summary>
    private int CandidateCount(JsonElement inputs, ResolvedStep step)
    {
        var candidates = inputs.TryGetProperty("candidates", out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
        var budget = inputs.TryGetProperty("budget", out var limit) ? (int)RuntimeJson.Integer(limit, 0) : candidates;
        RuntimeJson.Require(budget <= Limits.MaxControlIterations, RuntimeAbiCodes.IterationBudget, step.NodeId);
        RuntimeJson.Require(candidates <= budget, RuntimeAbiCodes.IterationBudget, step.NodeId);
        return candidates;
    }

    private int Count(JsonElement inputs, string port, ResolvedStep step)
    {
        var count = inputs.TryGetProperty(port, out var value) ? (int)RuntimeJson.Integer(value, 0) : 0;
        RuntimeJson.Require(count <= Limits.MaxControlIterations, RuntimeAbiCodes.IterationBudget, step.NodeId);
        return count;
    }

    private static long Ticks(JsonElement inputs, string port)
        => inputs.TryGetProperty(port, out var value) ? RuntimeJson.Integer(value, 1) : 1;
    private static bool Flag(JsonElement inputs, string port)
        => inputs.TryGetProperty(port, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Registers the continuation a `delay` or `interval` control leaves behind and writes its timer handle into
    /// the control step's frame. The pulse re-enters the plan's own entry, so the schedule is the kernel's, not the
    /// provider's: it dispatches no new event and triggers no new subscription. `resumeOutput` is the execution
    /// output the next activation starts from — `next` for a delay, `pulse` for an interval.
    /// </summary>
    private string? ScheduleContinuation(Work item, int stepIndex, ResolvedStep step, Pending pending,
        int resumeOutput, PulseSchedule spec, out int handleSlot)
    {
        handleSlot = -1;
        try
        {
            var plan = item.Plan;
            RuntimeJson.Require(schedules.Count < MaximumSchedules, "schedule-budget", step.NodeId);
            RuntimeJson.Require(Successor(step, resumeOutput) is not null, RuntimeAbiCodes.ControlShape, step.NodeId);
            var scheduleId = plan.Plan.Id + ".control." + stepIndex;
            // The handle names the source provider, which is what the schedule registry is keyed by and what a
            // scope cancellation matches on: `cancel` resolves the timer through the same pair.
            var handleValue = CreateHandle(pending.Provider, "timer", "encounter", out handleSlot, scheduleId);
            stepFrames[stepIndex] = TimerFrame(handleValue);
            // The resumed activation is the same plan on the same dispatch, so it keeps the subjects that dispatch's
            // mount accepted rather than matching again.
            var work = new Work(plan, item.Entry, item.Subjects);
            var first = pending.Event.SimulationTick + (spec.FirstPulse == FirstPulse.Immediate ? 0 : spec.IntervalTicks);
            var handle = new RuntimeScheduleHandle(this, pending.Provider, scheduleId, WorldEpoch);
            var job = new ScheduledJob(handle, pending.Generation, pending.Event, spec, new[] { work },
                Fingerprint(new { provider = pending.Provider, world = WorldEpoch, plan = plan.Plan.Id, entry = item.Entry.NodeId, step = stepIndex, output = resumeOutput }),
                first, null, spec.MaxPulses)
            {
                Resume = new Continuation(work, stepIndex, resumeOutput, handleValue),
                HandleSlot = handleSlot
            };
            schedules.Add((handle.ProviderId, handle.ScheduleId), job);
            QueuePulse(job);
            return null;
        }
        catch (RuntimeContractException error) { return error.Code; }
    }

    /// <summary>
    /// Evaluates one on-demand step — `pure` or `query` — and memoizes it for the current activation. A `query`
    /// step is handed the budgeted world read and its refusal is reported as `query-budget` instead of being
    /// answered with a partial frame; a `pure` step is handed a session that refuses every read.
    ///
    /// The table this memoizes into is the activation's one slot table, so it is also where a `control` step writes
    /// its own value outputs and where an `action` step's result row is published when it commits: those two are
    /// read back out of the table, and a step this activation produced no value for — a `pure` step of an earlier
    /// activation, an action a schedule resumed past — is refused by name instead of answered with a stranger's
    /// frame.
    /// </summary>
    private JsonElement EvaluateStep(Work item, int stepIndex, Pending pending)
    {
        if (stepFrames.TryGetValue(stepIndex, out var cached)) return cached;
        var step = item.Entry.Steps[stepIndex];
        // A frame a control or an action published is always in the slot table by the time a later step reads it,
        // so a slot this table does not hold names a step no activation produced a value for; the refusal is the
        // slot's own, not a claim about the source's kind.
        RuntimeJson.Require(step.NodeKind is "pure" or "query", "from-step-slot", step.NodeId);
        JsonElement frame;
        try
        {
            var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var merged = step.Promoted.Count == 0 ? null : step.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            foreach (var input in step.Inputs)
            {
                JsonElement value;
                if (input.Literal is { } literal) value = literal;
                else if (input.FromStep is { } from)
                {
                    var published = EvaluateStep(item, from.Step, pending);
                    var port = RuntimeJson.Text(input.Port, "id");
                    value = StepSlotValue(published, port, step.NodeId + "." + input.Name);
                    // The same rule the dispatch walk follows: a frame this activation already validated is not
                    // parsed again, and only the entities it carries are re-asked. A read that widens a single
                    // value into a collection keeps the full check, because the port it lands on is not the port
                    // the frame was validated against.
                    if (!input.Wrap && validatedSlots.Contains(from.Step))
                    {
                        if (memoEntities.TryGetValue((from.Step, port), out var entities)) RecheckEntities(entities);
                    }
                    else { ResolveValue(value, input.Port); ValidateEntities(value, input.Port); }
                    if (input.Wrap) value = RuntimeJson.From(new[] { value });
                }
                else
                {
                    if (!pending.Event.Outputs.TryGetProperty(input.EventPort!, out value)) continue;
                    ResolveValue(value, input.Port); ValidateEntities(value, input.Port);
                    if (input.Wrap) value = RuntimeJson.From(new[] { value });
                }
                if (merged != null && step.Promoted.Contains(input.Name)) merged.Add(input.Name, value);
                else inputs.Add(input.Name, RuntimeJson.EnumPortToHandlerValue(value, input.Port));
            }
            var capability = registry.Capabilities[step.CapabilityId];
            var parameters = step.Parameters;
            if (merged != null) { parameters = RuntimeJson.From(merged); RuntimeJson.Parameters(parameters, capability); }
            parameters = RuntimeJson.ResolveEnumParameters(parameters, capability);
            var evaluator = registry.Evaluators[step.BindingId];
            var session = step.NodeKind == "query" ? BeginQuery(step.NodeId) : NoQuery(step.NodeId);
            // The event being dispatched is the whole context a step is evaluated in: its actor roles come from the
            // event's own payload — `self` from the entities this plan's mount accepted, carried on the work item —
            // and the world's faction relations are the ones the current world published. A role the contract does
            // not allow to be absent refuses the step by name before the handler ever runs, and `self` is refused
            // as `actor-ambiguous` rather than `actor-missing` when the mount accepted more than one entity.
            var actors = ActorContext(pending.Event, item);
            foreach (var role in RequiredActors(step.BindingId))
                RuntimeJson.Require(actors.Get(role) != null, "actor-missing", role);
            var result = evaluator(new EvaluationContext(step.NodeId, parameters, RuntimeJson.From(inputs), session, actors, relations));
            // A refused read is a rejected step, never a frame that silently holds fewer candidates.
            RuntimeJson.Require(queryRefusal == null, step.NodeKind == "query" ? queryRefusal! : RuntimeAbiCodes.PureWorldPort, step.NodeId);
            var outputs = RuntimeJson.Rows(step.Contract, "outputs");
            RuntimeJson.Shape(result, string.Join(" ", outputs.Select(p => RuntimeJson.Text(p, "id"))));
            List<EntityCheck>? live = null;
            foreach (var port in outputs)
            {
                var name = RuntimeJson.Text(port, "id");
                var value = result.GetProperty(name);
                // An entity port's references are parsed once here, checked and kept: every later read of this frame
                // in this activation re-asks their resolvers instead of parsing the list again.
                if (RuntimeJson.Text(port, "type") != "entity") { ValidateFrameValue(value, port, null); continue; }
                live ??= new List<EntityCheck>();
                live.Clear();
                ValidateFrameValue(value, port, live);
                memoEntities[(stepIndex, name)] = live.Count == 0 ? Array.Empty<EntityCheck>() : live.ToArray();
            }
            frame = result;
        }
        catch (RuntimeContractException ex) when (queryRefusal != null)
        {
            // The first refusal the session recorded outranks whatever the evaluator reported, so an exhausted
            // budget reads as the one `query-budget` code whether the evaluator threw or returned a short frame.
            throw new RuntimeContractException(step.NodeKind == "query" ? queryRefusal! : RuntimeAbiCodes.PureWorldPort,
                ex.Code + ": " + ex.Message);
        }
        // A refusal the evaluator raised keeps its own code: the more specific the code, the closer it points the
        // author at the node to repair. Only a failure with no code at all is encoded here.
        catch (RuntimeContractException) { throw; }
        catch (Exception ex) { throw new RuntimeContractException("pure-evaluation-failed", ex.GetType().Name + ": " + ex.Message); }
        stepFrames[stepIndex] = frame;
        validatedSlots.Add(stepIndex);
        return frame;
    }

    /// <summary>
    /// One value out of a frame a step published, addressed by the port id the plan declared. A declared port the
    /// publishing step did not actually produce is refused by name: an `action` publishes the row its own handler
    /// returned, and a row missing a column is a step that asked for a value nothing wrote — refused where it is
    /// read rather than surfacing as a missing JSON property out of the dispatch.
    /// </summary>
    private static JsonElement StepSlotValue(JsonElement frame, string name, string at)
    {
        RuntimeJson.Require(frame.ValueKind == JsonValueKind.Object, "from-step-value", at + "." + name);
        RuntimeJson.Require(frame.TryGetProperty(name, out var value), "from-step-value", at + "." + name);
        return value;
    }

    /// <summary>
    /// The work of one dispatch: every candidate plan whose own mount targets claim this event, each carrying the
    /// entities those targets accepted — the plan's `self` for this dispatch. A plan no target claims is left out
    /// entirely, so an event costs nothing until it belongs to that plan. An entrypoint whose trigger is attached to
    /// one map object is judged on that object first, so the mount targets and their resolvers are never asked about
    /// an event the trigger was not attached to.
    /// </summary>
    private Work[] ClaimedWork(IReadOnlyList<Work> candidates, RuntimeEvent value)
    {
        var claimed = new List<Work>();
        foreach (var candidate in candidates)
        {
            if (!EventScopeMatches(candidate.Entry.Scope, value, candidate.Entry.TriggerContract)) continue;
            if (MatchesAttachments(candidate.Plan.Plan, value, candidate.Entry.TriggerContract, out var subjects))
                claimed.Add(candidate with { Subjects = subjects });
        }
        return claimed.ToArray();
    }

    /// <summary>
    /// Whether an entrypoint's trigger receives this event: one attached to no object receives every event of its
    /// binding, and one attached to an object receives only the events whose own subject is that object (rule
    /// 143.11). The trigger's object is the map object address the plan carries; the event's is the address segment
    /// of the first subject entity a registered namespace owns, so the two texts compared are one spelling of one
    /// object. An event that resolves to no such entity never matches a bound trigger: a filter can only narrow the
    /// events of a binding, so a trigger that named an object it cannot see stays silent instead of firing for every
    /// object of that category.
    /// </summary>
    private bool EventScopeMatches(string? scope, RuntimeEvent value, JsonElement trigger)
    {
        if (scope == null) return true;
        foreach (var port in RuntimeJson.Rows(trigger, "outputs"))
        {
            if (RuntimeJson.Text(port, "type") != "entity") continue;
            if (!value.Outputs.TryGetProperty(RuntimeJson.Text(port, "id"), out var data) || data.ValueKind == JsonValueKind.Null) continue;
            if (!RuntimeGraphContracts.Many(port))
            {
                if (SubjectAddress(RuntimeJson.Entity(data)) == scope) return true;
                continue;
            }
            foreach (var item in data.EnumerateArray())
                if (SubjectAddress(RuntimeJson.Entity(item)) == scope) return true;
        }
        return false;
    }

    /// <summary>The address segment of one entity reference, or null when the reference is not owned by a registered
    /// namespace. The namespace is the text before the first colon and the address is everything after it, which is
    /// the one identity shape every provider writes (<c>gtfo.map_object:door/0/0/3/security</c>); a reference whose
    /// namespace nothing registered is passed over rather than read as an address of its own.</summary>
    private string? SubjectAddress(EntityReference entity)
    {
        var split = entity.Id.IndexOf(':');
        if (split <= 0 || split == entity.Id.Length - 1) return null;
        return registry.Resolvers.ContainsKey(entity.Id[..split]) ? entity.Id[(split + 1)..] : null;
    }

    /// <summary>
    /// Whether a plan's behaviour applies to this event at all, and which entities its own mount targets accepted.
    /// Each mount target asks the provider that registered its kind: a subject-free kind is answered from the target
    /// alone, so the events that carry no subject are judged too, and a subject-matched kind is asked about every
    /// entity the trigger payload carries. The accepted entities are what the plan's steps read `self` from — the
    /// same entity on two ports is one subject, two different entities are none.
    /// </summary>
    private bool MatchesAttachments(ResolvedPlan plan, RuntimeEvent value, JsonElement trigger, out IReadOnlyList<EntityReference> subjects)
    {
        var matched = false;
        var accepted = new List<EntityReference>();
        foreach (var attachment in plan.Attachments)
        {
            if (!registry.AttachmentMatchers.TryGetValue(attachment.Kind, out var registered)) continue;
            if (registered.Matcher.Scope is { } scope)
            {
                if (scope(attachment.Category, attachment.Reference)) matched = true;
                continue;
            }
            if (Matches(attachment, registered.Matcher.Subject!, value, trigger, accepted)) matched = true;
        }
        subjects = accepted;
        return matched;
    }

    private static bool Matches(PlanAttachment attachment, AttachmentMatcher matcher, RuntimeEvent value, JsonElement trigger,
        List<EntityReference> accepted)
    {
        var matched = false;
        foreach (var port in RuntimeJson.Rows(trigger, "outputs"))
        {
            if (RuntimeJson.Text(port, "type") != "entity") continue;
            if (!value.Outputs.TryGetProperty(RuntimeJson.Text(port, "id"), out var data) || data.ValueKind == JsonValueKind.Null) continue;
            if (!RuntimeGraphContracts.Many(port))
            {
                if (Accept(matcher, attachment, RuntimeJson.Entity(data), accepted)) matched = true;
                continue;
            }
            foreach (var item in data.EnumerateArray())
                if (Accept(matcher, attachment, RuntimeJson.Entity(item), accepted)) matched = true;
        }
        return matched;
    }

    /// <summary>One port value the matcher accepted: it both claims the plan and becomes one of the plan's subjects.</summary>
    private static bool Accept(AttachmentMatcher matcher, PlanAttachment attachment, EntityReference entity, List<EntityReference> accepted)
    {
        if (!matcher(attachment.Category, attachment.Reference, entity)) return false;
        accepted.Add(entity);
        return true;
    }
}
