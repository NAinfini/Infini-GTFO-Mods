using ForgeRuntime.Framework;

internal static class CleanupTests
{
    internal static void Stop()
    {
        var f = new WorkFixture(); var timer = f.Schedule(); var lease = f.Lease();
        Probe.That(f.Owner.Publish(f.Event("queued")).Status == "queued", "future event setup failed");
        Probe.That(f.Kernel.QueuedEvents == 2 && f.Kernel.LoadedPlans == 1, "loaded work setup incomplete");
        bool observedClean = false;
        using var observer = f.Owner.ObserveLifecycle(e => {
            if (e.Current.StartupState == RuntimeStartupState.Stopped)
                observedClean = f.Kernel.QueuedEvents == 0 && f.Kernel.LoadedPlans == 0
                    && timer.Status == "cancelled" && lease.Status == "cancelled";
        }, false);
        f.Kernel.StopRuntime(); f.Kernel.StopRuntime();
        Probe.That(observedClean && !observer.IsActive, "stop notified before cleanup or retained observer");
        Probe.That(timer.Code == "runtime-stopped" && lease.Code == "runtime-stopped", "stop reason lost");
        Probe.That(f.Commits == 0 && timer.NextTick == null, "stop dispatched pending work");
        Probe.Denied(() => { f.Kernel.Advance(20, true); return "accepted"; }, "runtime-not-ready");
        Probe.Denied(() => f.Owner.Publish(f.Event("after-stop")).Code, "runtime-not-ready");
        Probe.That(!timer.Cancel(), "already-cancelled timer was released twice");
        Probe.That(!lease.Release(), "already-cancelled lease was released twice");
        timer.Dispose(); lease.Dispose(); timer.Dispose(); lease.Dispose();
        Probe.That(f.Commits == 0 && f.Kernel.QueuedEvents == 0, "disposal revived gameplay");
        f.Owner.Dispose(); Probe.That(!f.Owner.IsRegistered, "owner could not clean up after stop");
    }
    internal static void Failure()
    {
        var f = new WorkFixture(false);
        f.Kernel.LoadPlan(f.Plan); f.Kernel.Advance(0, true);
        var timer = f.Schedule(); var lease = f.Lease();
        Probe.That(f.Owner.Publish(f.Event("pending")).Status == "queued", "pending event setup failed");
        int attempts = 0;
        try { f.Kernel.StartRuntime(() => { attempts++; throw new IOException("startup fixture"); }); }
        catch (IOException) { }
        Probe.That(f.Kernel.StartupState == RuntimeStartupState.Failed, "startup failure not terminal");
        Probe.That(f.Kernel.QueuedEvents == 0 && f.Kernel.LoadedPlans == 0, "failure retained loaded work");
        Probe.That(timer.Code == "startup-failed" && lease.Code == "startup-failed", "failure did not cancel handles");
        for (int i = 0; i < 20; i++) f.Kernel.StartRuntime(() => attempts++);
        Probe.That(attempts == 1 && f.Commits == 0, "failure retried or committed pending work");
        Probe.Denied(() => { f.Kernel.BeginWorld(2); return "accepted"; }, "runtime-not-ready");
        Probe.Denied(() => { f.Kernel.LoadPlan(f.Plan); return "accepted"; }, "runtime-not-ready");
        Probe.Denied(() => f.Owner.Publish(f.Event("retry")).Code, "runtime-not-ready");
        Probe.That(!lease.Release(), "failed startup lease disposed twice");
        Probe.That(!timer.Cancel(), "failed startup timer disposed twice");
        lease.Dispose(); timer.Dispose();
        f.Kernel.StopRuntime(); lease.Dispose(); timer.Dispose();
        Probe.That(f.Commits == 0, "cleanup after failed startup committed work");
    }
    internal static void World()
    {
        var f = new WorkFixture(); var oldTimer = f.Schedule(); var oldLease = f.Lease();
        bool clean = false;
        using var observer = f.Owner.ObserveLifecycle(e => {
            if (e.Kind == RuntimeLifecycleKind.WorldChanged)
                clean = oldTimer.Code == "world-ended" && oldLease.Code == "world-ended"
                    && e.PreviousWorldEpoch == 1 && e.Current.WorldEpoch == 2
                    && e.Current.SimulationTick == -1 && f.Kernel.QueuedEvents == 0;
        }, false);
        f.Kernel.BeginWorld(2); f.Kernel.Advance(0, true);
        Probe.That(clean && f.Kernel.LoadedPlans == 1, "world transition mixed lifetimes or discarded the plan");
        var freshTimer = f.Schedule(); var freshLease = f.Lease();
        Probe.That(!oldTimer.Cancel() && !oldLease.Release(), "old handles touched new-world work");
        oldTimer.Dispose(); oldLease.Dispose();
        Probe.That(freshTimer.Status == "active" && freshLease.Status == "active", "replacement handles were cancelled");
        Probe.That(f.Kernel.EvaluateNumericState(f.Target, WorkFixture.StateId, "test.stack", 10).Value == 15,
            "new world restored an old contribution or lost the new one");
        var tick = f.Kernel.Advance(10, true);
        Probe.That(tick.CommandsExecuted == 1 && f.Commits == 1, "old world pulse replayed into new life");
        Probe.That(freshTimer.Cancel() && freshLease.Release(), "live cleanup failed");
        Probe.That(!freshTimer.Cancel() && !freshLease.Release(), "live cleanup is not idempotent");
        f.Kernel.StopRuntime();
    }
    internal static void Threading()
    {
        var f = new WorkFixture(); var timer = f.Schedule(); var lease = f.Lease();
        f.Kernel.StopRuntime();
        foreach (Action action in new Action[] { () => timer.Cancel(), () => lease.Release(),
            () => timer.Dispose(), () => lease.Dispose() })
        {
            Exception? error = null;
            var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } });
            thread.Start();
            Probe.That(thread.Join(5000), "cleanup thread timed out");
            Probe.That(error is RuntimeContractException ex && ex.Code == "wrong-thread",
                "terminal cleanup bypassed simulation-thread ownership");
        }
        Probe.That(timer.Code == "runtime-stopped" && lease.Code == "runtime-stopped", "wrong-thread cleanup changed evidence");
    }
}
