using ForgeRuntime.Framework;

internal static class ObserverTests
{
    internal static void ReadOnly()
    {
        var f = new WorkFixture(); var timer = f.Schedule(); var lease = f.Lease();
        int observed = 0;
        using var observer = f.Owner.ObserveLifecycle(e => {
            if (e.Kind != RuntimeLifecycleKind.TickAdvanced) return;
            observed++;
            const string denied = "lifecycle-observer-mutation";
            Probe.Denied(() => f.Owner.Publish(f.Event("observer-event")).Code, denied);
            Probe.Denied(() => f.Owner.Schedule(f.Event("observer-timer"),
                new(1, FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 1)).Code, denied);
            Probe.Denied(() => f.Owner.AcquireNumericLease(f.LeaseRequest("observer-lease")).Code, denied);
            Probe.Denied(() => { timer.Cancel(); return "accepted"; }, denied);
            Probe.Denied(() => { lease.Release(); return "accepted"; }, denied);
            Probe.Denied(() => { f.Owner.CancelScope("test.lifecycle.scope"); return "accepted"; }, denied);
            Probe.Denied(() => { f.Kernel.StopRuntime(); return "accepted"; }, denied);
            Probe.Denied(() => { f.Kernel.Advance(2, true); return "accepted"; }, denied);
            Probe.That(f.Kernel.HasSubscribers(f.Trigger) && f.Kernel.ExportManifest().Length > 0,
                "read-only diagnostic inspection unexpectedly failed");
        }, false);
        f.Kernel.Advance(1, true);
        Probe.That(observed == 1 && observer.IsActive && f.Kernel.LifecycleFaultCount == 0,
            "observer test failed inside isolated callback");
        Probe.That(timer.Status == "active" && lease.Status == "active" && f.Kernel.QueuedEvents == 1,
            "rejected observer mutation changed pending work");
        observer.Dispose();
        Probe.That(f.Kernel.Advance(10, true).CommandsExecuted == 1 && f.Commits == 1,
            "observer interfered with the scheduled action");
        f.Kernel.StopRuntime();
    }
    internal static void ReentrantStop()
    {
        var f = new WorkFixture();
        f.OnCommit = _ => f.Kernel.StopRuntime();
        var value = f.Event("committed-before-error", 1);
        Probe.That(f.Owner.Publish(value).Status == "queued", "reentrant fixture failed to queue");
        var result = f.Kernel.Advance(1, true).Commands.Single().Result;
        Probe.That(result.Status == "failed" && result.CommitState == "unknown", "possible commit mislabeled uncommitted");
        Probe.That(f.Kernel.StartupState == RuntimeStartupState.Ready && f.Commits == 1, "reentrant stop corrupted runtime");
        Probe.That(f.Owner.Publish(value).Status == "duplicate", "uncertain commit was accepted for retry");
        Probe.That(f.Kernel.Advance(1, true).CommandsExecuted == 0 && f.Commits == 1, "uncertain commit replayed");
        f.OnCommit = null;
        Probe.That(f.Owner.Publish(f.Event("independent-next", 2)).Status == "queued", "independent event blocked");
        Probe.That(f.Kernel.Advance(2, true).CommandsExecuted == 1 && f.Commits == 2, "independent work failed after rejection");
        f.Kernel.StopRuntime();
    }
}
