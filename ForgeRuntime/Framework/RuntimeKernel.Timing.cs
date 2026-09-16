using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace ForgeRuntime.Framework;

public sealed partial class RuntimeKernel
{
    public const int MaximumSchedules = 256;
    public const int MaximumSchedulePulses = 65536;
    public const int MaximumScheduledPulsesPerTick = 64;
    public const int MaximumScheduleEntityReferences = 16;
    private readonly Dictionary<(string Provider, string Id), ScheduledJob> schedules = new();
    /// <summary>The schedules this advance ended, collected while the table is scanned and removed after it: the
    /// scan may not mutate the collection it walks.</summary>
    private readonly List<(ScheduledJob Job, string Code)> endedSchedules = new();
    /// <summary>Whether a schedule ended since the queue was last compacted. Ending one is the only thing that can
    /// leave a waiting pulse behind, so it is also the only thing that asks for the compaction.</summary>
    private bool schedulesDirty;
    private int scheduledThisTick;
    private sealed class ScheduledJob
    {
        internal readonly RuntimeScheduleHandle Handle;
        internal readonly long Generation;
        internal readonly RuntimeEvent Template;
        internal readonly PulseSchedule Spec;
        internal readonly IReadOnlyList<Work> Work;
        internal readonly string Identity;
        /// <summary>The tick this schedule's pulse series is anchored at. A `restart` moves the anchor while the
        /// pulse index keeps counting, so the next pulse lands one interval later without the schedule ever
        /// replaying an occurrence the ledger already recorded.</summary>
        internal long FirstTick;
        internal readonly long? EndTick;
        /// <summary>The pulse limit, or <c>null</c> for a series that only cancellation, its scope's end or its
        /// world's end stops: an `interval` control whose `count` input the plan omitted (ruling 160.2). Every
        /// provider-facing schedule is bounded — <see cref="Schedule"/> refuses one that is not — so this is null
        /// only on a control continuation.</summary>
        internal readonly int? TotalPulses;
        internal int Index;
        /// <summary>Set while a re-armed schedule still has the pulse it had already placed sitting in the queue:
        /// the compaction drops that entry, which is the only way a live schedule's stale pulse is removed.</summary>
        internal bool ReArmed;
        /// <summary>The lifecycle generation this job's capture was last validated at, and the entities that
        /// validation resolved. A capture cannot change, and the registry and the plan table cannot change without
        /// the generation moving, so while the number stands still the only thing left to ask is whether the
        /// entities the template names are still alive — which is what <see cref="Liveness"/> is for.</summary>
        internal long ValidatedGeneration = -1;
        internal EntityCheck[] Liveness = Array.Empty<EntityCheck>();
        /// <summary>Set only on a plan's own control continuation: the pulse re-enters the plan's entry instead of
        /// publishing an event. <see cref="HandleSlot"/> is the pool slot that continuation's timer occupies, so a
        /// schedule ending for any reason — expiry, cancel, world end — frees it in one place.</summary>
        internal Continuation? Resume { get; init; }
        internal int HandleSlot { get; init; } = -1;
        internal ScheduledJob(RuntimeScheduleHandle handle, long generation, RuntimeEvent template, PulseSchedule spec,
            IReadOnlyList<Work> work, string identity, long firstTick, long? endTick, int? total)
        { Handle = handle; Generation = generation; Template = template; Spec = spec; Work = work; Identity = identity; FirstTick = firstTick; EndTick = endTick; TotalPulses = total; }
        internal long NextTick => FirstTick + Index * Spec.IntervalTicks;
    }
    private static string Fingerprint(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RuntimeJson.StableText(RuntimeJson.From(value)))));
    /// <summary>The entities one captured template names, with the resolver that answers for each kind. The capture
    /// is a snapshot and its ports are fixed by the capability it was validated against, so this list is the whole
    /// time-dependent part of the capture and the only thing a later frame re-asks.</summary>
    private EntityCheck[] LivenessOf(RuntimeEvent template, JsonElement capability)
    {
        var live = new List<EntityCheck>();
        foreach (var port in RuntimeJson.Rows(capability.GetProperty("graph"), "outputs"))
        {
            if (RuntimeJson.Text(port, "type") != "entity") continue;
            if (!template.Outputs.TryGetProperty(RuntimeJson.Text(port, "id"), out var data) || data.ValueKind == JsonValueKind.Null) continue;
            if (!RuntimeGraphContracts.Many(port)) Collect(RuntimeJson.EntityRow(data));
            else foreach (var item in data.EnumerateArray()) Collect(RuntimeJson.EntityRow(item));
        }
        return live.Count == 0 ? Array.Empty<EntityCheck>() : live.ToArray();
        // The reference was validated and its resolver proved present by the check that accepted the capture, so
        // this reads the resolver the entity will be re-asked of and resolves nothing now.
        void Collect(EntityReference entity)
        {
            if (registry.Resolvers.TryGetValue(RuntimeJson.KindOf(entity.Id), out var registered)) live.Add(new EntityCheck(entity, registered.Resolve));
        }
    }
    private RuntimeEvent CaptureScheduledEvent(RuntimeModuleHandle handle, RuntimeEvent value)
    {
        RuntimeJson.Require(handle.IsRegistered, "module-unregistered", handle.ProviderId);
        RuntimeJson.Require(worldStarted && value.WorldEpoch == WorldEpoch, "stale-world", value.EventId);
        RuntimeJson.Require(worldHost != false, "not-host", "This world is not authoritative.");
        RuntimeJson.Text(value.EventId); RuntimeJson.Text(value.ScopeId); RuntimeJson.Integer(value.SimulationTick);
        RuntimeJson.Require(value.SimulationTick >= Math.Max(0, CurrentTick), "schedule-in-past", value.EventId);
        RuntimeJson.Require(!cancelled.Contains((handle.ProviderId, value.ScopeId)), "scope-cancelled", value.ScopeId);
        RuntimeJson.Require(registry.Bindings.TryGetValue(value.BindingId, out var binding) && RuntimeJson.Text(binding, "providerId") == handle.ProviderId, "binding-owner", value.BindingId);
        var text = value.Outputs.GetRawText();
        RuntimeJson.Require(Encoding.UTF8.GetByteCount(text) <= MaximumEventPayloadBytes, "event-payload-budget", value.EventId);
        var snapshot = value with { Outputs = RuntimeJson.Parse(text) };
        if (currentCommand != null) snapshot = snapshot with { CauseId = currentCommand.CommandId, RootEventId = currentCommand.RootEventId, CausalDepth = currentCommand.CausalDepth + 1 };
        else RuntimeJson.Require(snapshot.CausalDepth == 0 && snapshot.CauseId == null && (snapshot.RootEventId == null || snapshot.RootEventId == snapshot.EventId), "external-causality", "Root schedules cannot invent causes.");
        var capability = registry.Capabilities[RuntimeJson.Text(binding, "capabilityId")];
        ValidateEvent(snapshot, capability);
        var references = 0;
        foreach (var port in RuntimeJson.Rows(capability.GetProperty("graph"), "outputs"))
        {
            if (!snapshot.Outputs.TryGetProperty(RuntimeJson.Text(port, "id"), out var data) || data.ValueKind == JsonValueKind.Null) continue;
            if (RuntimeJson.Text(port, "type") == "entity") references += RuntimeGraphContracts.Many(port) ? data.GetArrayLength() : 1;
        }
        RuntimeJson.Require(references <= MaximumScheduleEntityReferences, "schedule-target-budget", "Fixed schedule targets exceed the explicit limit; no truncation is performed.");
        return snapshot;
    }
    internal ScheduleResult Schedule(RuntimeModuleHandle owner, RuntimeEvent template, PulseSchedule spec)
    {
        Thread();
        try
        {
            var snapshot = CaptureScheduledEvent(owner, template);
            RuntimeJson.Require(spec != null && spec.FirstPulse is FirstPulse.Immediate or FirstPulse.AfterInterval
                && spec.MissedPulsePolicy is MissedPulsePolicy.CatchUp or MissedPulsePolicy.SkipMissed, "schedule-policy", "Explicit first/missed pulse policies are required.");
            RuntimeJson.Integer(spec!.IntervalTicks, 1);
            RuntimeJson.Require(spec.MaxPulses != null || spec.LifetimeTicks != null, "unbounded-schedule", "A finite pulse count or lifetime is required.");
            if (spec.MaxPulses != null) RuntimeJson.Integer(spec.MaxPulses.Value, 1, MaximumSchedulePulses);
            if (spec.LifetimeTicks != null) RuntimeJson.Integer(spec.LifetimeTicks.Value, 1);
            var first = snapshot.SimulationTick + (spec.FirstPulse == FirstPulse.Immediate ? 0 : spec.IntervalTicks);
            RuntimeJson.Integer(first);
            long? end = spec.LifetimeTicks == null ? null : snapshot.SimulationTick + spec.LifetimeTicks.Value;
            if (end != null) RuntimeJson.Integer(end.Value);
            var total = (long)(spec.MaxPulses ?? MaximumSchedulePulses);
            if (end != null) total = Math.Min(total, end.Value <= first ? 0 : (end.Value - first - 1) / spec.IntervalTicks + 1);
            if (spec.MaxPulses == null && end != null) RuntimeJson.Require(end.Value <= first || (end.Value - first - 1) / spec.IntervalTicks + 1 <= MaximumSchedulePulses, "schedule-pulse-budget", "Lifetime contains too many pulses.");
            if (total > 0) RuntimeJson.Require(spec.IntervalTicks <= (RuntimeJson.MaxSafeInteger - first) / Math.Max(1, total - 1) || total == 1, "schedule-overflow", template.EventId);
            RuntimeJson.Require(subscriptions.TryGetValue(snapshot.BindingId, out var subscribed), "no-consumer", template.EventId);
            // A domain schedule reaches the same plans a direct publish would: the mount filter is applied here too,
            // so a plan attached to another map object never runs off someone else's timer.
            var work = ClaimedWork(subscribed!, snapshot);
            RuntimeJson.Require(work.Length > 0, "attachment-mismatch", template.EventId);
            RuntimeJson.Require(snapshot.CausalDepth >= 0 && snapshot.CausalDepth <= Limits.MaxCausalDepth && work.All(w => snapshot.CausalDepth <= w.Plan.Plan.Limits.MaxCausalDepth), "causal-depth", template.EventId);
            if (spec.MissedPulsePolicy == MissedPulsePolicy.CatchUp)
            {
                foreach (var bindingId in work.SelectMany(w => w.Entry.Steps.Where(s => s.NodeKind != "pure").Select(s => s.BindingId)).Append(snapshot.BindingId).Distinct(StringComparer.Ordinal))
                {
                    var capability = registry.Capabilities[RuntimeJson.Text(registry.Bindings[bindingId], "capabilityId")];
                    RuntimeJson.Require(capability.GetProperty("parameters").TryGetProperty("scheduleReplay", out var replay) && replay.ValueKind == JsonValueKind.String && replay.GetString() == "fixed-inputs", "schedule-history-required", bindingId);
                }
            }
            var key = owner.ProviderId + "\0schedule\0" + snapshot.EventId;
            var fingerprint = LedgerFingerprint(RuntimeJson.StableText(RuntimeJson.From(new { snapshot, spec })));
            if (history.TryGetValue(key, out var previous))
            {
                if (previous == fingerprint) return new ScheduleResult("duplicate", "duplicate-schedule", null);
                LogEventRejected(snapshot.BindingId, snapshot.EventId, "schedule-id-conflict", owner.ProviderId);
                return new ScheduleResult("rejected", "schedule-id-conflict", null);
            }
            RuntimeJson.Require(history.Count < MaximumEventHistory, "event-history-budget", "Replay ledger capacity reached.");
            RuntimeJson.Require(schedules.Count < MaximumSchedules, "schedule-budget", "Active schedule capacity reached.");
            RuntimeJson.Require(total == 0 || queue.Count < Limits.MaxQueuedEvents, "queue-budget", snapshot.EventId);
            foreach (var group in work.GroupBy(w => w.Plan))
            {
                RuntimeJson.Require(group.Sum(w => w.Entry.DispatchableStepCount) <= group.Key.Plan.Limits.MaxCommandsPerTick, "event-command-budget", group.Key.Plan.Id);
                RuntimeJson.Require(queue.UnorderedItems.Count(x => x.Element.Work.Any(w => ReferenceEquals(w.Plan, group.Key))) < group.Key.Plan.Limits.MaxQueuedEvents, "plan-queue-budget", group.Key.Plan.Id);
            }
            RuntimeJson.Require(work.Sum(w => w.Entry.DispatchableStepCount) <= Limits.MaxCommandsPerTick, "event-command-budget", snapshot.EventId);
            var handle = new RuntimeScheduleHandle(this, owner.ProviderId, snapshot.EventId, WorldEpoch);
            var identity = Fingerprint(new { provider = owner.ProviderId, generation = owner.Generation, world = WorldEpoch, scope = snapshot.ScopeId, schedule = snapshot.EventId });
            var job = new ScheduledJob(handle, owner.Generation, snapshot, spec, work, identity, first, end, (int)total)
            {
                // The capture was validated on the way in, so the job starts out already checked at this generation
                // and carries the entities that check resolved.
                ValidatedGeneration = lifecycleGeneration,
                Liveness = LivenessOf(snapshot, registry.Capabilities[RuntimeJson.Text(registry.Bindings[snapshot.BindingId], "capabilityId")])
            };
            history.Add(key, fingerprint);
            if (total == 0) { handle.Status = "completed"; handle.Code = "no-pulses-before-end"; }
            else { schedules.Add((owner.ProviderId, handle.ScheduleId), job); QueuePulse(job); }
            return new ScheduleResult("scheduled", handle.Code, handle);
        }
        // A refused schedule is an event that never entered the queue, recorded once under the same split as a publish.
        catch (RuntimeContractException ex)
        {
            if (ex.Code.EndsWith("-budget", StringComparison.Ordinal)) LogBudgetExceeded(template.BindingId, template.EventId, ex.Code);
            else LogEventRejected(template.BindingId, template.EventId, ex.Code, owner.ProviderId);
            return new ScheduleResult("rejected", ex.Code, null);
        }
        catch (ObjectDisposedException)
        {
            LogEventRejected(template.BindingId, template.EventId, "disposed-payload", owner.ProviderId);
            return new ScheduleResult("rejected", "disposed-payload", null);
        }
    }
    private void QueuePulse(ScheduledJob job)
    {
        job.Handle.NextTick = job.NextTick;
        var value = job.Template with { EventId = "scheduled:" + job.Identity + ":" + job.Index, SimulationTick = job.NextTick, RootEventId = job.Template.RootEventId ?? job.Template.EventId };
        queue.Enqueue(new Pending(job.Handle.ProviderId, job.Generation, value, job.Work, job), (job.NextTick, ++sequence));
    }
    private void EndSchedule(ScheduledJob job, string status, string code)
    {
        job.Handle.Status = status; job.Handle.Code = code; job.Handle.NextTick = null;
        schedules.Remove((job.Handle.ProviderId, job.Handle.ScheduleId));
        // A plan continuation's timer dies with its schedule, so the handle it wrote into a step frame can never
        // be read as live again — the generation is what makes that visible to a `cancel` that still names it.
        ReleaseHandle(job.HandleSlot);
        // The queue still holds this job's waiting pulses; they are dropped by the next compaction, which is the
        // only thing this flag schedules.
        schedulesDirty = true;
    }
    /// <summary>
    /// Drops the queue entries that belong to a schedule that has ended. Compaction is the only way a dead pulse
    /// stops occupying queue capacity — a cancelled timer releases its slot immediately, which is what the callers
    /// that end a schedule and then publish rely on — so it runs exactly when a schedule ended and never on a frame
    /// that ended none.
    /// </summary>
    private void PruneSchedules()
    {
        if (!schedulesDirty) return;
        schedulesDirty = false;
        var entries = queue.UnorderedItems.Where(x => x.Element.Schedule == null || Live(x.Element.Schedule)).ToArray();
        if (entries.Length == queue.Count) return;
        queue.Clear(); foreach (var entry in entries) queue.Enqueue(entry.Element, entry.Priority);
    }
    internal bool CancelSchedule(RuntimeScheduleHandle handle)
    {
        // Terminal handle disposal is cleanup, not new runtime work.
        ReadThread(); NoLifecycleMutation();
        if (!schedules.TryGetValue((handle.ProviderId, handle.ScheduleId), out var job) || !ReferenceEquals(job.Handle, handle)) return false;
        EndSchedule(job, "cancelled", "explicit-cancel"); PruneSchedules(); return true;
    }

    /// <summary>Whether one queued entry still belongs to a live schedule. The one entry a re-armed schedule had
    /// already placed is stale although its handle is live, so the compaction clears the flag as it drops it.</summary>
    private static bool Live(ScheduledJob job)
    {
        if (job.Handle.Status != "active") return false;
        if (!job.ReArmed) return true;
        job.ReArmed = false;
        return false;
    }

    /// <summary>Re-arms one live schedule from now: its next pulse lands one interval later, the handle keeps naming
    /// the same schedule for its whole life, and the pulse index is not reset, so no occurrence identity is ever
    /// replayed. A schedule whose pulse already fired has ended and released its slot, so a `restart` that still
    /// names it is refused there rather than here.</summary>
    private bool RestartSchedule(ScheduledJob job)
    {
        if (job.Handle.Status != "active") return false;
        var next = CurrentTick + job.Spec.IntervalTicks;
        RuntimeJson.Integer(next);
        job.FirstTick = next - job.Index * job.Spec.IntervalTicks;
        RuntimeJson.Integer(job.FirstTick);
        job.ReArmed = true; schedulesDirty = true; PruneSchedules();
        QueuePulse(job);
        return true;
    }
    private ScheduleReceipt ScheduleReport(ScheduledJob job, string status, string code)
        => new(job.Handle.ProviderId, job.Handle.ScheduleId, job.Handle.WorldEpoch, CurrentTick, status, code, job.Handle.DispatchedPulses, job.Handle.SkippedPulses);
    /// <summary>
    /// Why a live schedule cannot dispatch any more, or null while it can. The answer has two halves, and only one
    /// of them can change from frame to frame: the source, the scope and the plans behind the capture are the
    /// lifecycle generation's business — a module or plan cannot come or go without it moving — while an entity's
    /// life ends under the kernel without any registration changing at all. So a frame at a standing generation
    /// asks the capture's resolvers, and the full check runs only where the number moved.
    /// </summary>
    private string? ScheduleEndCode(ScheduledJob job)
    {
        if (job.ValidatedGeneration != lifecycleGeneration)
        {
            var code = RevalidateSchedule(job);
            if (code != null) return code;
        }
        return EntityLivenessCode(job.Liveness);
    }
    /// <summary>The full check of one capture against the tables it was resolved from.</summary>
    private string? RevalidateSchedule(ScheduledJob job)
    {
        if (!IsRegistered(job.Handle.ProviderId, job.Generation) || job.Handle.WorldEpoch != WorldEpoch) return "source-lifecycle";
        if (cancelled.Count > 0 && cancelled.Contains((job.Handle.ProviderId, job.Template.ScopeId))) return "scope-cancelled";
        for (var i = 0; i < job.Work.Count; i++)
            if (!plans.TryGetValue(job.Work[i].Plan.Plan.Id, out var live) || !ReferenceEquals(live, job.Work[i].Plan)) return "plan-unloaded";
        try
        {
            var capability = registry.Capabilities[RuntimeJson.Text(registry.Bindings[job.Template.BindingId], "capabilityId")];
            ValidateEvent(job.Template, capability);
            job.Liveness = LivenessOf(job.Template, capability);
        }
        catch (RuntimeContractException ex) { return ex.Code; }
        job.ValidatedGeneration = lifecycleGeneration;
        return null;
    }
    /// <summary>Whether an entity one value names has lost its life: the whole of what a repeated look at a
    /// validated value still asks.</summary>
    private string? EntityLivenessCode(EntityCheck[] checks, out string subject)
    {
        for (var i = 0; i < checks.Length; i++)
        {
            var check = checks[i];
            subject = check.Entity.Id;
            if (check.Entity.WorldEpoch != WorldEpoch) return "stale-world";
            bool valid;
            try { valid = check.Resolve(check.Entity); }
            catch (Exception) { return "entity-resolver-failed"; }
            if (!valid) return "stale-entity";
        }
        subject = "";
        return null;
    }
    private string? EntityLivenessCode(EntityCheck[] checks) => EntityLivenessCode(checks, out _);
    private bool PreparePulse(Pending pending, List<ScheduleReceipt> reports)
    {
        var job = pending.Schedule!;
        if (job.Handle.Status != "active")
        {
            queue.Dequeue();
            LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "source-lifecycle", pending.Provider);
            return false;
        }
        // A handler earlier in this tick can end the life of the captured source/target. Revalidating the template before every pulse keeps the remaining catch-up from reaching an object that no longer exists and removes the whole timer on its next step instead of leaving it to reject one pulse at a time.
        var invalid = ScheduleEndCode(job);
        if (invalid != null)
        {
            queue.Dequeue(); EndSchedule(job, "cancelled", invalid);
            reports.Add(ScheduleReport(job, "cancelled", invalid));
            LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, invalid, pending.Provider);
            return false;
        }
        if (job.Spec.MissedPulsePolicy == MissedPulsePolicy.SkipMissed)
        {
            if (job.EndTick != null && CurrentTick >= job.EndTick)
            {
                // A lifetime bound only ever comes with a finite pulse count, so this schedule is bounded here.
                queue.Dequeue(); job.Handle.SkippedPulses += (job.TotalPulses ?? 0) - job.Index;
                EndSchedule(job, "expired", "lifetime-ended"); reports.Add(ScheduleReport(job, "expired", "lifetime-ended"));
                LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "lifetime-ended", pending.Provider);
                return false;
            }
            var missed = (CurrentTick - job.NextTick) / job.Spec.IntervalTicks;
            var skipped = (int)(job.TotalPulses is { } limit ? Math.Min(limit - job.Index - 1L, missed) : missed);
            if (skipped > 0)
            {
                queue.Dequeue(); job.Index += skipped; job.Handle.SkippedPulses += skipped;
                LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "missed-pulses", pending.Provider);
                reports.Add(ScheduleReport(job, "skipped", "missed-pulses")); QueuePulse(job); return false;
            }
        }
        return true;
    }
    private bool AdmitPulse(Pending pending, List<ScheduleReceipt> reports)
    {
        var job = pending.Schedule!;
        var key = pending.Provider + "\0" + pending.Event.EventId;
        // A different work item claiming the same occurrence identity is a real conflict; only a full ledger is a budget refusal. Both stop this schedule without retrying or evicting recorded IDs.
        var refusal = history.ContainsKey(key) ? "event-id-conflict"
            : history.Count >= MaximumEventHistory ? "event-history-budget" : null;
        if (refusal != null)
        {
            EndSchedule(job, "rejected", refusal); reports.Add(ScheduleReport(job, "rejected", refusal));
            if (refusal.EndsWith("-budget", StringComparison.Ordinal)) LogBudgetExceeded(pending.Event.BindingId, pending.Event.EventId, refusal);
            else LogEventRejected(pending.Event.BindingId, pending.Event.EventId, refusal, pending.Provider);
            return false;
        }
        history.Add(key, LedgerFingerprint(RuntimeJson.StableText(RuntimeJson.From(pending.Event))));
        job.Index++; job.Handle.DispatchedPulses++; scheduledThisTick++;
        if (job.TotalPulses is not { } pulses || job.Index < pulses) QueuePulse(job);
        else job.Handle.NextTick = null;
        LogTriggerFired(pending.Provider, pending.Event.BindingId, pending.Event.EventId, pending.Event.CauseId,
            pending.Event.RootEventId ?? job.Template.EventId);
        reports.Add(ScheduleReport(job, "dispatched", "pulse-dispatched")); return true;
    }
    private void CleanScheduledLifetimes(List<ScheduleReceipt> reports)
    {
        if (schedules.Count == 0) return;
        endedSchedules.Clear();
        foreach (var job in schedules.Values)
        {
            var code = ScheduleEndCode(job);
            if (code != null) endedSchedules.Add((job, code));
        }
        for (var i = 0; i < endedSchedules.Count; i++)
        {
            var (job, code) = endedSchedules[i];
            EndSchedule(job, "cancelled", code);
            reports.Add(ScheduleReport(job, "cancelled", code));
        }
        PruneSchedules();
    }
    private void StopScheduledSource(string? provider, string? scope, string code)
    {
        foreach (var job in schedules.Values.Where(j => (provider == null || j.Handle.ProviderId == provider) && (scope == null || j.Template.ScopeId == scope)).ToArray()) EndSchedule(job, "cancelled", code);
        PruneSchedules();
    }
}
