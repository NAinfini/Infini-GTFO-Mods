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
    private int scheduledThisTick;
    private sealed class ScheduledJob
    {
        internal readonly RuntimeScheduleHandle Handle;
        internal readonly long Generation;
        internal readonly RuntimeEvent Template;
        internal readonly PulseSchedule Spec;
        internal readonly IReadOnlyList<Work> Work;
        internal readonly string Identity;
        internal readonly long FirstTick;
        internal readonly long? EndTick;
        internal readonly int TotalPulses;
        internal int Index;
        internal ScheduledJob(RuntimeScheduleHandle handle, long generation, RuntimeEvent template, PulseSchedule spec,
            IReadOnlyList<Work> work, string identity, long firstTick, long? endTick, int total)
        { Handle = handle; Generation = generation; Template = template; Spec = spec; Work = work; Identity = identity; FirstTick = firstTick; EndTick = endTick; TotalPulses = total; }
        internal long NextTick => FirstTick + Index * Spec.IntervalTicks;
    }
    private static string Fingerprint(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RuntimeJson.StableText(RuntimeJson.From(value)))));
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
            RuntimeJson.Require(spec != null && Enum.IsDefined(typeof(FirstPulse), spec.FirstPulse) && Enum.IsDefined(typeof(MissedPulsePolicy), spec.MissedPulsePolicy), "schedule-policy", "Explicit first/missed pulse policies are required.");
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
            var work = subscribed!.ToArray();
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
            var fingerprint = Fingerprint(new { snapshot, spec });
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
            var identity = Fingerprint(new { provider = owner.ProviderId, generation = owner.Generation, world = WorldEpoch, source = snapshot.Source, scope = snapshot.ScopeId, schedule = snapshot.EventId });
            var job = new ScheduledJob(handle, owner.Generation, snapshot, spec, work, identity, first, end, (int)total);
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
    }
    private void PruneSchedules()
    {
        var entries = queue.UnorderedItems.Where(x => x.Element.Schedule == null || x.Element.Schedule.Handle.Status == "active").ToArray();
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
    private ScheduleReceipt ScheduleReport(ScheduledJob job, string status, string code)
        => new(job.Handle.ProviderId, job.Handle.ScheduleId, job.Handle.WorldEpoch, CurrentTick, status, code, job.Handle.DispatchedPulses, job.Handle.SkippedPulses);
    private string? ScheduleEndCode(ScheduledJob job)
    {
        if (!IsRegistered(job.Handle.ProviderId, job.Generation) || job.Handle.WorldEpoch != WorldEpoch) return "source-lifecycle";
        if (cancelled.Contains((job.Handle.ProviderId, job.Template.ScopeId))) return "scope-cancelled";
        if (!job.Work.All(w => plans.TryGetValue(w.Plan.Plan.Id, out var live) && ReferenceEquals(live, w.Plan))) return "plan-unloaded";
        try { ValidateEvent(job.Template, registry.Capabilities[RuntimeJson.Text(registry.Bindings[job.Template.BindingId], "capabilityId")]); }
        catch (RuntimeContractException ex) { return ex.Code; }
        return null;
    }
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
                queue.Dequeue(); job.Handle.SkippedPulses += job.TotalPulses - job.Index;
                EndSchedule(job, "expired", "lifetime-ended"); reports.Add(ScheduleReport(job, "expired", "lifetime-ended"));
                LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "lifetime-ended", pending.Provider);
                return false;
            }
            var skipped = (int)Math.Min(job.TotalPulses - job.Index - 1L, (CurrentTick - job.NextTick) / job.Spec.IntervalTicks);
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
        history.Add(key, Fingerprint(pending.Event));
        job.Index++; job.Handle.DispatchedPulses++; scheduledThisTick++;
        if (job.Index < job.TotalPulses) QueuePulse(job);
        else job.Handle.NextTick = null;
        LogTriggerFired(pending.Provider, pending.Event.BindingId, pending.Event.EventId, pending.Event.CauseId,
            pending.Event.RootEventId ?? job.Template.EventId);
        reports.Add(ScheduleReport(job, "dispatched", "pulse-dispatched")); return true;
    }
    private void CleanScheduledLifetimes(List<ScheduleReceipt> reports)
    {
        if (schedules.Count == 0) return;
        foreach (var job in schedules.Values.ToArray())
        {
            var code = ScheduleEndCode(job);
            if (code != null) { EndSchedule(job, "cancelled", code); reports.Add(ScheduleReport(job, "cancelled", code)); }
        }
        PruneSchedules();
    }
    private void StopScheduledSource(string? provider, string? scope, string code)
    {
        foreach (var job in schedules.Values.Where(j => (provider == null || j.Handle.ProviderId == provider) && (scope == null || j.Template.ScopeId == scope)).ToArray()) EndSchedule(job, "cancelled", code);
        PruneSchedules();
    }
}
