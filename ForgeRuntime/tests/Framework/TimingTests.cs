using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

static class TimingTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string name) { checks++; if (!condition) throw new Exception("FAIL timing: " + name); }
        void Reject(Action action, string code)
        {
            try { action(); } catch (RuntimeContractException ex) when (ex.Code == code) { checks++; return; }
            throw new Exception("FAIL timing: expected " + code);
        }
        var delay = new PulseSchedule(5, FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 1);
        var catchUp = new PulseSchedule(2, FirstPulse.Immediate, MissedPulsePolicy.CatchUp, 5);
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var result = a.Schedule(s.Event(a.ProviderId, "delay"), delay);
            Check(result.Status == "scheduled" && result.Handle!.NextTick == 5, "delay has explicit first due tick");
            Check(s.Kernel.Advance(4, true).CommandsExecuted == 0 && s.Kernel.Advance(5, true).CommandsExecuted == 1, "delay runs only when due");
            Check(s.Kernel.Advance(5, true).CommandsExecuted == 0 && result.Handle!.Status == "completed", "same tick cannot duplicate a completed delay");
            Check(s.Calls.Single().ScheduledTick == 5 && s.Calls.Single().RootEventId == "delay", "schedule preserves due tick and root trace");
            Check(!result.Handle!.Cancel(), "completed handle cannot cancel unrelated work");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var template = s.Event(a.ProviderId, "repeat"); var result = a.Schedule(template, catchUp);
            Check(a.Schedule(template, catchUp).Code == "duplicate-schedule", "repeated schedule request is idempotent");
            Check(a.Schedule(template, catchUp with { MaxPulses = 6 }).Code == "schedule-id-conflict", "same schedule ID cannot change its definition");
            var tick = s.Kernel.Advance(8, true);
            Check(tick.CommandsExecuted == 5 && s.Calls.Select(c => c.ScheduledTick).SequenceEqual(new long[] { 0, 2, 4, 6, 8 }), "fixed inputs catch up every due occurrence in order");
            Check(s.Calls.All(c => c.SimulationTick == 8) && result.Handle!.DispatchedPulses == 5 && result.Handle.SkippedPulses == 0, "execution time stays distinct from scheduled time");
            Check(s.Calls.Select(c => c.EventId).Distinct().Count() == 5 && s.Calls.All(c => c.EventId.StartsWith("scheduled:")), "every occurrence has a unique scheduled identity");
            Check(s.Kernel.Advance(8, true).CommandsExecuted == 0 && s.Kernel.Advance(20, true).CommandsExecuted == 0, "finite repeat does not rearm or duplicate");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha", replay: false); s.Plan("alpha", a.ProviderId);
            Check(a.Schedule(s.Event(a.ProviderId, "unsafe"), catchUp).Code == "schedule-history-required", "missing replay declaration rejects catch-up");
            var skip = a.Schedule(s.Event(a.ProviderId, "skip"), catchUp with { MissedPulsePolicy = MissedPulsePolicy.SkipMissed, MaxPulses = 8 });
            var tick = s.Kernel.Advance(9, true);
            Check(tick.CommandsExecuted == 1 && s.Calls.Single().ScheduledTick == 8, "world-dependent work skips historical occurrences");
            Check(skip.Handle!.SkippedPulses == 4 && tick.Schedules.Any(r => r.Code == "missed-pulses" && r.SkippedPulses == 4), "skipped count is explicit, never silent");
            Check(s.Kernel.Advance(10, true).CommandsExecuted == 1 && skip.Handle.DispatchedPulses == 2, "next ordinary interval remains scheduled");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha", unsafeSecondAction: true); s.Plan("alpha", a.ProviderId, secondAction: true);
            Check(a.Schedule(s.Event(a.ProviderId, "mixed"), catchUp).Code == "schedule-history-required", "one unsafe action in a multi-action entry rejects the whole catch-up");
            Check(a.Schedule(s.Event(a.ProviderId, "mixed-skip"), catchUp with { MissedPulsePolicy = MissedPulsePolicy.SkipMissed }).Status == "scheduled", "same mixed chain explicitly supports skip-missed");
            Check(s.Kernel.Advance(8, true).CommandsExecuted == 2, "one surviving occurrence executes the complete linear chain");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var r = a.Schedule(s.Event(a.ProviderId, "bounded"), catchUp with { IntervalTicks = 1, MaxPulses = 70 });
            var first = s.Kernel.Advance(100, true);
            Check(first.CommandsExecuted == 64 && first.Schedules.Any(r => r.Code == "scheduled-tick-budget") && s.Kernel.QueuedEvents == 1, "catch-up obeys hard per-tick pulse budget and retains backlog");
            Check(s.Kernel.Advance(100, true).CommandsExecuted == 0, "repeating Advance cannot bypass a tick budget");
            Check(s.Kernel.Advance(101, true).CommandsExecuted == 6 && r.Handle!.Status == "completed", "remaining deterministic occurrences resume once on a later tick");
            Check(s.Calls.Select(c => c.EventId).Distinct().Count() == 70, "budget deferral never repeats an occurrence");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var r = a.Schedule(s.Event(a.ProviderId, "lifetime"), catchUp with { MaxPulses = null, LifetimeTicks = 5 });
            Check(s.Kernel.Advance(20, true).CommandsExecuted == 3 && r.Handle!.DispatchedPulses == 3, "fixed-input lifetime captures only ticks before exclusive end, even on delayed dispatch");
            var expired = a.Schedule(s.Event(a.ProviderId, "expired", tick: 20), catchUp with { MissedPulsePolicy = MissedPulsePolicy.SkipMissed, LifetimeTicks = 5 });
            Check(s.Kernel.Advance(25, true).CommandsExecuted == 0 && expired.Handle!.Status == "expired" && expired.Handle.SkippedPulses == 3, "world-dependent lifetime expiry never fabricates a final pulse");
            var empty = a.Schedule(s.Event(a.ProviderId, "empty", tick: 25), delay with { LifetimeTicks = 3 });
            Check(empty.Handle!.Status == "completed" && empty.Handle.Code == "no-pulses-before-end" && s.Kernel.QueuedEvents == 0, "first pulse after exclusive end has an explicit empty result");
            Check(a.Schedule(s.Event(a.ProviderId, "forever", tick: 25), catchUp with { MaxPulses = null }).Code == "unbounded-schedule", "unbounded timers reject");
            Check(a.Schedule(s.Event(a.ProviderId, "bad-interval", tick: 25), delay with { IntervalTicks = 0 }).Code == "invalid-integer", "zero interval rejects");
            Check(a.Schedule(s.Event(a.ProviderId, "unsafe-tick", tick: RuntimeJson.MaxSafeInteger), delay).Code == "invalid-integer", "due tick overflow cannot cross JS integer precision");
            Check(a.Schedule(s.Event(a.ProviderId, "huge", tick: 25), catchUp with { MaxPulses = null, IntervalTicks = 1, LifetimeTicks = 70000 }).Code == "schedule-pulse-budget", "finite lifetime also has a pulse count ceiling");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); var b = s.Register("example.beta"); s.Plan("alpha", a.ProviderId); s.Plan("beta", b.ProviderId);
            var ra = a.Schedule(s.Event(a.ProviderId, "shared"), catchUp); var rb = b.Schedule(s.Event(b.ProviderId, "shared"), catchUp);
            s.Kernel.Advance(0, true);
            Check(s.Calls[0].EventId != s.Calls[1].EventId, "module/source ownership is included in identical schedule IDs");
            Check(ra.Handle!.Cancel() && !ra.Handle.Cancel(), "individual cancellation is idempotent");
            Check(s.Kernel.Advance(2, true).CommandsExecuted == 1 && rb.Handle!.DispatchedPulses == 2, "cancelling A cannot remove B's schedule with the same source scope");
            a.CancelScope("scope"); Check(rb.Handle!.Status == "active", "scope cancellation is namespaced by module");
            b.Dispose(); Check(rb.Handle!.Status == "cancelled" && s.Kernel.QueuedEvents == 0, "module unload cancels and removes only owned timed work");
        }
        {
            var s = new TimingScenario(); RuntimeScheduleHandle? job = null;
            var a = s.Register("example.alpha", handler: _ => { job!.Cancel(); return CommandResult.Succeeded(RuntimeJson.EmptyObject); }); s.Plan("alpha", a.ProviderId, steps: 2);
            job = a.Schedule(s.Event(a.ProviderId, "cancel-in-handler"), catchUp).Handle!;
            var tick = s.Kernel.Advance(0, true);
            Check(tick.CommandsExecuted == 1 && tick.Commands.Last().Result.Code == "schedule-cancelled" && s.Kernel.QueuedEvents == 0, "cancelling the active schedule prevents remaining steps and reserved future pulse");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var target = a.Schedule(s.Event(a.ProviderId, "old-target"), delay).Handle!;
            s.Lives[a.ProviderId + ":target"]++;
            var tick = s.Kernel.Advance(1, true);
            Check(target.Status == "cancelled" && tick.Schedules.Any(r => r.Code == "stale-entity") && s.Kernel.QueuedEvents == 0, "target life change removes even future timers before due tick");
            var source = a.Schedule(s.Event(a.ProviderId, "old-source", tick: 1), delay).Handle!;
            s.Lives[a.ProviderId + ":source"]++;
            Check(s.Kernel.Advance(2, true).CommandsExecuted == 0 && source.Code == "stale-entity", "source life is independently validated");
            var oldWorld = a.Schedule(s.Event(a.ProviderId, "old-world", tick: 2), delay).Handle!;
            s.World = 2; s.Kernel.BeginWorld(2);
            Check(oldWorld.Code == "world-ended" && s.Kernel.QueuedEvents == 0 && !oldWorld.Cancel(), "world transition invalidates handles and clears pending tasks");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); var b = s.Register("example.beta"); s.Plan("alpha", a.ProviderId); s.Plan("beta", b.ProviderId);
            var ra = a.Schedule(s.Event(a.ProviderId, "unloaded"), delay).Handle!; var rb = b.Schedule(s.Event(b.ProviderId, "still-loaded"), delay).Handle!;
            s.Kernel.UnloadPlan("alpha"); var tick = s.Kernel.Advance(1, true);
            Check(ra.Code == "plan-unloaded" && rb.Status == "active" && tick.Schedules.Any(r => r.Code == "plan-unloaded"), "plan removal invalidates its schedule snapshot without affecting another plan");
            Check(s.Kernel.Advance(5, true).CommandsExecuted == 1, "independent plan survives another plan's removal");
        }
        {
            var s = new TimingScenario(new RuntimeLimits { MaxQueuedEvents = 1 }); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var ra = a.Schedule(s.Event(a.ProviderId, "reserved"), catchUp).Handle!;
            Check(a.Schedule(s.Event(a.ProviderId, "overflow"), delay).Code == "queue-budget", "scheduled and ordinary work share the same queue capacity");
            Check(a.Publish(s.Event(a.ProviderId, "ordinary")).Code == "plan-queue-budget", "timed work does not hide in a second unbudgeted queue");
            Check(s.Kernel.Advance(2, true).CommandsExecuted == 2 && s.Kernel.QueuedEvents == 1, "next pulse reserves the dequeued queue slot");
            Check(ra.Cancel() && a.Publish(s.Event(a.ProviderId, "after-cancel", tick: 3)).Status == "queued", "cancelled timer releases queue capacity immediately");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha", targetList: true); s.Plan("alpha", a.ProviderId);
            var targets = Enumerable.Range(0, 17).Select(i => { var name = "target" + i; s.Lives[a.ProviderId + ":" + name] = 1; return s.Entity(a.ProviderId, name); }).ToArray();
            var tooMany = s.Event(a.ProviderId, "large-set") with { Outputs = RuntimeJson.From(new { target = targets }) };
            Check(a.Schedule(tooMany, delay).Code == "schedule-target-budget", "fixed target sets have explicit rejection instead of truncation");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var r = a.Schedule(s.Event(a.ProviderId, "not-host"), delay).Handle!;
            Check(s.Kernel.Advance(5, false).CommandsExecuted == 0 && r.Code == "not-host" && s.Kernel.QueuedEvents == 0, "nonhost never executes scheduled effects");
            Check(a.Schedule(s.Event(a.ProviderId, "still-client", tick: 5), delay).Code == "not-host", "known client cannot create more scheduled work");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var skip = catchUp with { MissedPulsePolicy = MissedPulsePolicy.SkipMissed, MaxPulses = 8 };
            a.Schedule(s.Event(a.ProviderId, "skip-order"), skip);
            a.Publish(s.Event(a.ProviderId, "native-order", tick: 5));
            s.Kernel.Advance(9, true);
            Check(s.Calls.Select(c => c.ScheduledTick).SequenceEqual(new long[] { 5, 8 }), "skipping old occurrences preserves due-time ordering with ordinary events");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var bounded = new PulseSchedule(5, FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 1, LifetimeTicks: 10);
            var full = Enumerable.Range(0, RuntimeKernel.MaximumSchedules).Select(i => a.Schedule(s.Event(a.ProviderId, "cap" + i), bounded)).ToArray();
            Check(full.All(r => r.Status == "scheduled" && r.Handle!.Status == "active"), "active schedule capacity admits exactly the documented limit");
            Check(a.Schedule(s.Event(a.ProviderId, "cap-overflow"), bounded).Code == "schedule-budget", "one schedule beyond capacity is rejected without truncation");
            Check(full[0].Handle!.Cancel() && a.Schedule(s.Event(a.ProviderId, "cap-refill"), bounded).Status == "scheduled", "cancelling one schedule frees exactly one slot");
            var expiry = s.Kernel.Advance(10, true);
            Check(expiry.Schedules.Count(r => r.Status == "expired" && r.Code == "lifetime-ended") == RuntimeKernel.MaximumSchedules && s.Kernel.QueuedEvents == 0, "exclusive lifetime end releases every bounded schedule in one advance");
            Check(a.Schedule(s.Event(a.ProviderId, "cap-after-expiry", tick: 10), bounded).Status == "scheduled", "expired timers release capacity for new schedules");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId, tweak: plan => plan["limits"]!["maxQueuedEvents"] = 1);
            var reserved = a.Schedule(s.Event(a.ProviderId, "plan-capacity"), catchUp);
            Check(reserved.Status == "scheduled", "a lower plan queue budget still admits its first reserved pulse");
            Check(a.Schedule(s.Event(a.ProviderId, "plan-overflow"), delay).Code == "plan-queue-budget", "the lower plan queue budget rejects before global schedule capacity is involved");
            Check(s.Kernel.Advance(0, true).CommandsExecuted == 1 && s.Kernel.QueuedEvents == 1, "the reserved next pulse keeps the plan queue exactly at its own bound");
            Check(reserved.Handle!.Cancel() && a.Schedule(s.Event(a.ProviderId, "plan-refill"), delay).Status == "scheduled", "cancelling timed work releases its plan queue slot immediately");
            Reject(() => s.Plan("command-budget", a.ProviderId, steps: 2, tweak: plan => plan["limits"]!["maxCommandsPerTick"] = 1), "event-command-budget");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId, tweak: plan => plan["limits"]!["maxEventsPerTick"] = 1);
            var r = a.Schedule(s.Event(a.ProviderId, "plan-events"), catchUp);
            Check(s.Kernel.Advance(3, true).CommandsExecuted == 1 && s.Kernel.Advance(3, true).CommandsExecuted == 0, "a lower per-plan tick budget defers the due pulse and repeating Advance cannot reset it");
            Check(s.Kernel.Advance(4, true).CommandsExecuted == 1 && r.Handle!.DispatchedPulses == 2, "the deferred pulse resumes in order on the next tick");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha", handler: _ => { s.Lives["example.alpha:target"]++; return CommandResult.Succeeded(RuntimeJson.EmptyObject); }); s.Plan("alpha", a.ProviderId);
            var r = a.Schedule(s.Event(a.ProviderId, "target-life-change"), catchUp).Handle!;
            var tick = s.Kernel.Advance(8, true);
            Check(s.Calls.Count == 1, "only the first catch-up occurrence runs before the target life changes");
            Check(r.Status == "cancelled" && r.Code == "stale-entity" && r.DispatchedPulses == 1 && tick.Schedules.Any(x => x.Status == "cancelled" && x.Code == "stale-entity"), "the stale-target schedule is cleared inside the same advance with an explicit receipt");
            Check(s.Kernel.QueuedEvents == 0 && s.Kernel.Advance(9, true).CommandsExecuted == 0 && s.Calls.Count == 1, "no remaining occurrence is executed or retried on a later tick");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha", handler: _ => { s.Lives["example.alpha:source"]++; return CommandResult.Succeeded(RuntimeJson.EmptyObject); }); s.Plan("alpha", a.ProviderId);
            var r = a.Schedule(s.Event(a.ProviderId, "source-life-change"), catchUp).Handle!;
            var tick = s.Kernel.Advance(8, true);
            Check(s.Calls.Count == 1 && r.Code == "stale-entity" && tick.Schedules.Any(x => x.Code == "stale-entity"), "a captured source that ends its life also stops the remaining catch-up");
            Check(tick.Commands.Count == 1 && s.Kernel.QueuedEvents == 0, "the old-life source produces no further command receipt");
        }
        {
            var s = new TimingScenario(); var common = s.StateContract(); var a = s.Register("example.alpha"); var b = s.Register("example.beta"); s.Kernel.Advance(0, true);
            var target = s.Entity(a.ProviderId, "target");
            var ra = a.AcquireNumericLease(s.Lease(a.ProviderId, "lease-a", target, additive: 5, multiplier: 2));
            var rb = b.AcquireNumericLease(s.Lease(b.ProviderId, "lease-b", target, additive: 3, multiplier: .5));
            var result = s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10);
            Check(ra.Status == "acquired" && rb.Status == "acquired" && result.Value == 18 && result.Contributions == 2, "independent source contributions use one common registered definition");
            Check(ra.Handle!.Release() && !ra.Handle.Release(), "lease release is idempotent and handle-bound");
            result = s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10);
            Check(result.Value == 6.5 && result.Contributions == 1 && rb.Handle!.Status == "active", "removing A recomputes from base and preserves B");
            Reject(() => common.Dispose(), "module-in-use");
            b.Dispose(); Check(rb.Handle!.Status == "cancelled" && s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10).Value == 10, "module removal releases only that module's contributions");
            common.Dispose(); Check(!common.IsRegistered, "definition unload succeeds after all foreign leases end");
        }
        {
            var s = new TimingScenario(); s.StateContract(); var a = s.Register("example.alpha"); s.Kernel.Advance(0, true);
            var request = s.Lease(a.ProviderId, "idempotent", s.Entity(a.ProviderId, "target"));
            var r = a.AcquireNumericLease(request);
            Check(a.AcquireNumericLease(request).Code == "duplicate-lease", "retry does not double a source contribution");
            Check(a.AcquireNumericLease(request with { Additive = 8 }).Code == "lease-id-conflict", "lease identity cannot be reused with changed contribution");
            Check(a.AcquireNumericLease(request with { LeaseId = "same-source" }).Code == "state-source-conflict", "same source key cannot implicitly stack or refresh");
            r.Handle!.Release(); Check(a.AcquireNumericLease(request).Code == "duplicate-lease", "released lease cannot resurrect from replay");
            var replacement = a.AcquireNumericLease(request with { LeaseId = "new-source-lease", Multiplier = 0 });
            Check(s.Kernel.EvaluateNumericState(request.Target, TimingScenario.State, "speed", 10).Value == 0, "zero multiplier is represented exactly");
            replacement.Handle!.Release();
            Check(s.Kernel.EvaluateNumericState(request.Target, TimingScenario.State, "speed", 10).Value == 10, "removal never divides by previous multiplier");
        }
        {
            var s = new TimingScenario(); s.StateContract(); var a = s.Register("example.alpha"); var b = s.Register("example.beta"); s.Kernel.Advance(0, true);
            var target = s.Entity(a.ProviderId, "target"); var request = s.Lease(a.ProviderId, "expiry", target) with { DurationTicks = 3 };
            var expiry = a.AcquireNumericLease(request).Handle!;
            var rb = b.AcquireNumericLease(s.Lease(b.ProviderId, "longer", target)).Handle!;
            Check(s.Kernel.Advance(2, true).StateLeases.Count == 0 && expiry.Status == "active", "lease remains active before exclusive expiry");
            var tick = s.Kernel.Advance(3, true);
            Check(expiry.Status == "expired" && tick.StateLeases.Single().Code == "lifetime-ended" && rb.Status == "active", "lease expires at the same simulation tick without affecting other source");
            s.Lives[b.ProviderId + ":source"]++;
            Check(s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10).Value == 10 && rb.Code == "stale-entity", "source life change invalidates a lease even before next Advance");
            var targetLease = a.AcquireNumericLease(request with { LeaseId = "target-life" }).Handle!;
            s.Lives[a.ProviderId + ":target"]++; s.Kernel.Advance(4, true);
            Check(targetLease.Code == "stale-entity", "old target life contribution is removed");
            Check(s.Kernel.EvaluateNumericState(s.Entity(a.ProviderId, "target"), TimingScenario.State, "speed", 10).Contributions == 0, "new target life cannot inherit old modifiers");
        }
        {
            var s = new TimingScenario(); s.StateContract(); var a = s.Register("example.alpha"); var b = s.Register("example.beta"); s.Kernel.Advance(0, true);
            var target = s.Entity(a.ProviderId, "target");
            var ra = a.AcquireNumericLease(s.Lease(a.ProviderId, "scope-a", target)).Handle!;
            var rb = b.AcquireNumericLease(s.Lease(b.ProviderId, "scope-b", target)).Handle!;
            a.CancelScope("scope");
            Check(ra.Code == "scope-cancelled" && rb.Status == "active" && s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10).Contributions == 1, "same named state scope is isolated by owning module");
            s.World = 2; s.Kernel.BeginWorld(2);
            Check(rb.Code == "world-ended" && !rb.Release(), "world change invalidates state handles");
            s.Kernel.Advance(0, true);
            Check(s.Kernel.EvaluateNumericState(s.Entity(a.ProviderId, "target"), TimingScenario.State, "speed", 10).Value == 10, "no old-world state survives");
        }
        {
            var s = new TimingScenario(); s.StateContract(); var a = s.Register("example.alpha"); var target = s.Entity(a.ProviderId, "target"); var request = s.Lease(a.ProviderId, "invalid", target);
            Check(a.AcquireNumericLease(request).Code == "not-host", "state cannot mutate before host authority is established");
            s.Kernel.Advance(0, true);
            Check(a.AcquireNumericLease(request with { DefinitionId = "example.missing" }).Code == "state-definition", "unknown state definition is not a private second registry");
            Check(a.AcquireNumericLease(request with { DefinitionVersion = "2.0.0" }).Code == "state-version", "state definition version is pinned");
            Check(a.AcquireNumericLease(request with { Multiplier = -1 }).Code == "state-number" && a.AcquireNumericLease(request with { Additive = double.NaN }).Code == "state-number", "unsafe state numbers reject");
            Check(a.AcquireNumericLease(request with { DurationTicks = 0 }).Code == "invalid-integer", "state duration must be positive");
            Reject(() => s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", double.PositiveInfinity), "state-number");
            var large = a.AcquireNumericLease(request with { Additive = double.MaxValue });
            Reject(() => s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", double.MaxValue), "state-overflow");
            large.Handle!.Release();
            s.Kernel.Advance(RuntimeJson.MaxSafeInteger, true);
            Check(a.AcquireNumericLease(request with { LeaseId = "overflow-end" }).Code == "lease-overflow", "state expiry remains within safe integer ticks");
        }
        {
            var s = new TimingScenario(); s.StateContract(); var a = s.Register("example.alpha"); s.Kernel.Advance(0, true);
            var acquired = new List<NumericLeaseResult>();
            for (var i = 0; i < RuntimeKernel.MaximumStateLeases; i++)
            {
                var name = "cap" + i; s.Lives[a.ProviderId + ":" + name] = 1;
                acquired.Add(a.AcquireNumericLease(s.Lease(a.ProviderId, "lease" + i, s.Entity(a.ProviderId, name))));
            }
            Check(acquired.All(r => r.Status == "acquired" && r.Handle!.Status == "active"), "active lease capacity admits exactly the documented limit");
            s.Lives[a.ProviderId + ":cap-over"] = 1;
            var over = s.Lease(a.ProviderId, "lease-over", s.Entity(a.ProviderId, "cap-over"));
            Check(a.AcquireNumericLease(over).Code == "state-lease-budget", "one lease beyond capacity is rejected without truncation");
            Check(a.AcquireNumericLease(over with { DefinitionId = "example.missing" }).Code == "state-definition", "a full lease budget still reports the real invalid input instead of charging it as capacity");
            Check(acquired[0].Handle!.Release() && a.AcquireNumericLease(s.Lease(a.ProviderId, "lease-refill", s.Entity(a.ProviderId, "cap0"))).Status == "acquired", "releasing one lease frees exactly one slot");
            Check(s.Kernel.Advance(10, true).StateLeases.Count(r => r.Code == "lifetime-ended") == RuntimeKernel.MaximumStateLeases, "exclusive duration end releases the whole bounded set in one advance");
            Check(a.AcquireNumericLease(s.Lease(a.ProviderId, "lease-after-expiry", s.Entity(a.ProviderId, "cap0"))).Status == "acquired", "expired leases release capacity for new contributions");
        }
        {
            var s = new TimingScenario(); s.StateContract(); var a = s.Register("example.alpha"); s.Kernel.Advance(0, true);
            var target = s.Entity(a.ProviderId, "target");
            var lease = a.AcquireNumericLease(s.Lease(a.ProviderId, "exclusive-end", target) with { DurationTicks = 1 }).Handle!;
            Check(s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10).Value == 15, "a contribution applies until its exclusive end tick");
            var tick = s.Kernel.Advance(1, true);
            Check(lease.Status == "expired" && tick.StateLeases.Single().Code == "lifetime-ended" && s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10).Contributions == 0, "expiry on the read tick is applied before the read");
        }
        {
            var s = new TimingScenario(); s.StateContract(); var a = s.Register("example.alpha"); s.Kernel.Advance(0, true);
            var target = s.Entity(a.ProviderId, "target"); s.Lives[a.ProviderId + ":source2"] = 1;
            var speed = a.AcquireNumericLease(s.Lease(a.ProviderId, "group-speed", target)).Handle!;
            var pace = a.AcquireNumericLease(s.Lease(a.ProviderId, "group-pace", target) with { StackGroup = "pace", Additive = 100 }).Handle!;
            Check(s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10).Value == 15 && s.Kernel.EvaluateNumericState(target, TimingScenario.State, "pace", 10).Value == 110, "stack groups never mix on one target");
            var second = a.AcquireNumericLease(s.Lease(a.ProviderId, "source-two", target) with { Source = s.Entity(a.ProviderId, "source2"), Additive = 1 }).Handle!;
            Check(s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10).Value == 16, "two sources of one provider contribute independently");
            s.Lives[a.ProviderId + ":source2"]++;
            Check(s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 10).Value == 15 && second.Code == "stale-entity" && speed.Status == "active", "a source life change removes exactly that source contribution");
            pace.Release(); s.Lives[a.ProviderId + ":target"]++;
            var current = s.Entity(a.ProviderId, "target");
            a.AcquireNumericLease(s.Lease(a.ProviderId, "life-two", current, additive: 7));
            var afterLife = s.Kernel.EvaluateNumericState(current, TimingScenario.State, "speed", 10);
            Check(speed.Code == "stale-entity" && afterLife.Value == 17 && afterLife.Contributions == 1, "a new target life keeps only its own lease");
        }
        {
            NumericStateResult Aggregate(bool reverse)
            {
                var s = new TimingScenario(); s.StateContract(); var a = s.Register("example.alpha"); s.Kernel.Advance(0, true);
                var target = s.Entity(a.ProviderId, "target");
                // Both runs use the same source keys and the same contributions; only the acquisition order differs. 1e16 and -1e16 cancel exactly, so the 1 is silently lost when it is added first: an unstable read order cannot produce the same aggregate.
                var keys = new[] { "sourceA", "sourceB", "sourceC" };
                var contributions = new[] { 1e16, -1e16, 1.0 };
                var acquired = 0;
                for (var i = 0; i < keys.Length; i++)
                {
                    var index = reverse ? keys.Length - 1 - i : i; var name = keys[index];
                    s.Lives[a.ProviderId + ":" + name] = 1;
                    var request = s.Lease(a.ProviderId, "order-" + name, target, additive: contributions[index]) with { Source = s.Entity(a.ProviderId, name) };
                    if (a.AcquireNumericLease(request).Status == "acquired") acquired++;
                }
                if (acquired != keys.Length) throw new Exception("FAIL timing: determinism setup could not acquire all three source keys");
                return s.Kernel.EvaluateNumericState(target, TimingScenario.State, "speed", 0.5);
            }
            var forward = Aggregate(false); var reverse = Aggregate(true);
            Check(forward == reverse && forward.Additive == 1 && forward.Value == 1.5, "stable source ordering keeps the aggregate identical for opposite acquisition orders");
        }
        {
            var s = new TimingScenario(); s.StateContract(); s.StateDefinition("forge.contract.text", "forge.contract.text.value", "text"); s.StateDefinition("forge.contract.plain", "forge.contract.plain.value", null);
            var a = s.Register("example.alpha"); s.Kernel.Advance(0, true);
            var request = s.Lease(a.ProviderId, "invalid-input", s.Entity(a.ProviderId, "target"));
            Check(a.AcquireNumericLease(request with { DefinitionId = "forge.contract.text.value" }).Code == "state-value-type" && a.AcquireNumericLease(request with { DefinitionId = "forge.contract.plain.value" }).Code == "state-value-type", "a state definition without numeric-contribution type rejects, declared or not");
            Check(a.AcquireNumericLease(request with { DefinitionId = "example.missing" }).Code == "state-definition" && a.AcquireNumericLease(request with { DefinitionId = "example.missing" }).Code == "state-definition", "repeated invalid input keeps its specific reason");
            Check(a.AcquireNumericLease(request).Status == "acquired" && s.Kernel.EvaluateNumericState(request.Target, TimingScenario.State, "speed", 10).Contributions == 1, "invalid attempts never consumed the ledger, capacity or the lease identity");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var r = a.Schedule(s.Event(a.ProviderId, "occurrence"), catchUp).Handle!;
            var first = s.Kernel.Advance(0, true);
            var scheduled = first.Commands.Single().EventId;
            var claimed = scheduled[..(scheduled.LastIndexOf(':') + 1)] + "3";
            Check(scheduled.StartsWith("scheduled:") && a.Publish(s.Event(a.ProviderId, "occurrence-taken") with { EventId = claimed, SimulationTick = 4 }).Status == "queued", "a foreign work item can claim an occurrence identity through the ordinary event path");
            var tick = s.Kernel.Advance(8, true);
            Check(tick.Schedules.Any(x => x.Status == "rejected" && x.Code == "event-id-conflict" && x.DispatchedPulses == 3), "a real occurrence identity conflict is reported as a conflict, not as a ledger budget");
            Check(r.Status == "rejected" && r.Code == "event-id-conflict", "the schedule keeps the actual rejection reason on its handle");
            Check(s.Calls.Count == 4 && s.Calls.Count(c => c.EventId == claimed && c.ScheduledTick == 6) == 0, "the conflicting occurrence never executes while the claiming event runs once");
            Check(s.Kernel.Advance(8, true).CommandsExecuted == 0 && a.Publish(s.Event(a.ProviderId, "occurrence-taken") with { EventId = claimed, SimulationTick = 4 }).Status == "duplicate", "the ledger keeps the claimed identity instead of evicting or re-running it");
        }
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var late = a.Schedule(s.Event(a.ProviderId, "late-pulse"), new PulseSchedule(1000, FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 1)).Handle!;
            var admitted = 0;
            for (var batch = 0; batch < RuntimeKernel.MaximumEventHistory / 128; batch++)
            {
                for (var i = 0; i < 128; i++)
                { var n = batch * 128 + i; if (a.Publish(s.Event(a.ProviderId, "fill" + n, tick: batch)).Status == "queued") admitted++; }
                s.Kernel.Advance(batch, true);
            }
            Check(admitted == RuntimeKernel.MaximumEventHistory - 1 && a.Publish(s.Event(a.ProviderId, "fill-end", tick: 511)).Code == "event-history-budget", "the replay ledger fills to its documented limit and then refuses new identities without eviction");
            var tick = s.Kernel.Advance(1000, true);
            Check(late.Status == "rejected" && late.Code == "event-history-budget" && tick.Schedules.Any(x => x.Code == "event-history-budget"), "an occurrence that cannot enter a full ledger reports the budget, not an identity conflict");
        }
        Console.WriteLine($"Timing/state checks: {checks} passed.");
        return checks;
    }
}

sealed class TimingScenario
{
    public const string State = "forge.contract.numeric.value";
    public readonly RuntimeKernel Kernel;
    public readonly List<CommandContext> Calls = new();
    public readonly Dictionary<string, long> Lives = new(StringComparer.Ordinal);
    public long World = 1;
    public TimingScenario(RuntimeLimits? limits = null) { Kernel = new RuntimeKernel(Fixture.Identity, limits); Kernel.BeginWorld(World); }
    public EntityReference Entity(string provider, string name) => new(provider + ":" + name, World, Lives[provider + ":" + name]);
    public RuntimeEvent Event(string provider, string id, long tick = 0) => new(id, Fixture.Trigger(provider), World, tick, "scope", RuntimeJson.From(new { target = Entity(provider, "target") }), Entity(provider, "source"));
    public RuntimeModuleHandle Register(string provider, bool replay = true, bool unsafeSecondAction = false, bool targetList = false, CommandHandler? handler = null)
    {
        Lives[provider + ":target"] = 1; Lives[provider + ":source"] = 1;
        CommandHandler apply = ctx => { Calls.Add(ctx); return handler?.Invoke(ctx) ?? CommandResult.Succeeded(RuntimeJson.EmptyObject); };
        var module = Fixture.Module(provider, apply); var json = JsonNode.Parse(module.RegistryJson)!;
        foreach (var cap in json["capabilities"]!.AsArray()) if (replay) cap!["parameters"]!["scheduleReplay"] = "fixed-inputs";
        if (targetList)
        {
            json["capabilities"]![0]!["graph"]!["outputs"]![1]!["cardinality"] = "many";
            json["capabilities"]![1]!["graph"]!["inputs"]![1]!["cardinality"] = "many";
            json["capabilities"]![1]!["graph"]!["recipients"]!["cardinality"] = "many";
        }
        if (unsafeSecondAction)
        {
            var capability = JsonNode.Parse(json["capabilities"]![1]!.ToJsonString())!;
            capability["id"] = provider + ".unsafe"; capability["parameters"] = new JsonObject(); json["capabilities"]!.AsArray().Add(capability);
            var binding = JsonNode.Parse(json["bindings"]![1]!.ToJsonString())!;
            binding["id"] = provider + ".binding.unsafe"; binding["capabilityId"] = provider + ".unsafe"; binding["handler"] = provider + ".handler.unsafe"; json["bindings"]!.AsArray().Add(binding);
            module = module with { Handlers = new Dictionary<string, CommandHandler> { [Fixture.Handler(provider)] = apply, [provider + ".handler.unsafe"] = apply },
                BindingSupport = module.BindingSupport.Append(new BindingSupport(provider + ".binding.unsafe", "implementation-only", Fixture.Permissions)).ToArray() };
        }
        return Kernel.RegisterModule(module with { RegistryJson = json.ToJsonString(), EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> {
            [provider] = r => r.WorldEpoch == World && Lives.TryGetValue(r.Id, out var life) && r.LifeEpoch == life
        } });
    }
    public void Plan(string id, string provider, bool secondAction = false, int steps = 1, Action<JsonNode>? tweak = null)
    {
        var plan = JsonNode.Parse(Fixture.Plan(Kernel, id, provider, secondAction ? 2 : steps))!;
        if (secondAction) plan["entrypoints"]![0]!["steps"]![1]!["binding"] = plan["bindings"]!.AsArray().Select(b => b!["bindingId"]!.GetValue<string>()).ToList().IndexOf(provider + ".binding.unsafe");
        tweak?.Invoke(plan);
        Kernel.LoadPlan(plan.ToJsonString());
    }
    public RuntimeModuleHandle StateContract() => StateDefinition("forge.contract.numeric", State, "numeric-contribution");
    /// <summary>Registers one isolated state definition so the value-type gate is provable without a second registry.</summary>
    public RuntimeModuleHandle StateDefinition(string provider, string id, string? valueType)
    {
        var module = Fixture.Module(provider); var json = JsonNode.Parse(module.RegistryJson)!;
        var parameters = new JsonObject(); if (valueType != null) parameters["valueType"] = valueType;
        json["capabilities"] = new JsonArray(JsonNode.Parse(RuntimeJson.From(new { id, owner = provider, kind = "state", label = "Numeric contribution", version = "1.0.0", parameters }).GetRawText()));
        json["bindings"] = new JsonArray();
        return Kernel.RegisterModule(module with { RegistryJson = json.ToJsonString(), Handlers = new Dictionary<string, CommandHandler>(), BindingSupport = Array.Empty<BindingSupport>() });
    }
    public NumericLeaseRequest Lease(string provider, string id, EntityReference target, double additive = 5, double multiplier = 1)
        => new(id, State, "1.0.0", target, "scope", "speed", Entity(provider, "source"), 10, additive, multiplier);
}
