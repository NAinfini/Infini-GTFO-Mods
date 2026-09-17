using System.Text.Json;
using ForgeRuntime.Framework;

internal static class LifecycleProbe
{
    private static RuntimeModule Module(string id) => new(RuntimeKernel.ApiVersion,
        JsonSerializer.Serialize(new {
            providers = new[] { new { id, kind = "native", version = "1.0.0", dependencies = Array.Empty<object>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
    private static RuntimeKernel Kernel()
    {
        var kernel = new RuntimeKernel(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "managed-test"));
        kernel.BeginWorld(1); return kernel;
    }
    internal static void Startup()
    {
        var k = Kernel(); var owner = k.RegisterModule(Module("test.lifecycle.owner"), RuntimeLogLevel.Off);
        var seen = new List<RuntimeLifecycleEvent>(); using var sub = owner.ObserveLifecycle(seen.Add);
        Verify.That(seen.Count == 1 && seen[0].Kind == RuntimeLifecycleKind.Snapshot, "current snapshot missing");
        Verify.That(k.IsRegistrationOpen && k.Lifecycle.SimulationTick == -1, "initial lifecycle is fabricated");
        int attempts = 0;
        Verify.That(k.StartRuntime(() => {
            attempts++;
            Verify.That(!k.IsRegistrationOpen && k.StartupState == RuntimeStartupState.Starting, "freeze must precede initialization");
            Verify.Reject(() => k.RegisterModule(Module("test.lifecycle.late"), RuntimeLogLevel.Off), "late registration accepted");
        }), "startup did not complete");
        Verify.That(attempts == 1 && k.StartupState == RuntimeStartupState.Ready, "ready state missing");
        Verify.That(!k.StartRuntime(() => attempts++) && attempts == 1, "startup repeated");
        Verify.Reject(() => k.RegisterModule(Module("test.lifecycle.after"), RuntimeLogLevel.Off), "registration reopened after readiness");
        Verify.That(seen.Select(e => e.Current.StartupState).SequenceEqual(new[] {
            RuntimeStartupState.Registering, RuntimeStartupState.Starting, RuntimeStartupState.Ready }), "startup order changed");
        k.Advance(0, true); k.Advance(0, true);
        Verify.That(seen.Count(e => e.Kind == RuntimeLifecycleKind.TickAdvanced) == 1, "same tick produced duplicate observation");
        Verify.That(k.Lifecycle.IsHost == true && k.Lifecycle.SimulationTick == 0, "tick snapshot differs from kernel");
        k.BeginWorld(2);
        var world = seen.Last();
        Verify.That(world.Kind == RuntimeLifecycleKind.WorldChanged && world.PreviousWorldEpoch == 1
            && world.Current.WorldEpoch == 2 && world.Current.SimulationTick == -1 && world.Current.IsHost == null,
            "world transition mixed old tick/authority into the new world");
        k.StopRuntime(); int stoppedCount = seen.Count; k.StopRuntime();
        Verify.That(seen.Count == stoppedCount && !sub.IsActive, "stop not idempotent or observers retained");
        Verify.That(k.StartupState == RuntimeStartupState.Stopped && !k.StartRuntime(() => attempts++), "stopped instance restarted");
        Verify.Reject(() => k.Advance(1, true), "stopped kernel dispatched");
        owner.Dispose(); Verify.That(!owner.IsRegistered, "module cleanup after stop failed");
    }
    internal static void FailedStartup()
    {
        var k = Kernel(); int attempts = 0;
        var owner = k.RegisterModule(Module("test.lifecycle.failure"), RuntimeLogLevel.Off);
        var seen = new List<RuntimeLifecycleEvent>(); using var sub = owner.ObserveLifecycle(seen.Add);
        try { k.StartRuntime(() => { attempts++; throw new IOException("fixture startup error"); }); }
        catch (IOException) { }
        for (int i = 0; i < 100; i++) k.StartRuntime(() => attempts++);
        Verify.That(attempts == 1 && k.StartupState == RuntimeStartupState.Failed, "failed initialization retried");
        Verify.That(seen.Last().Current.StartupState == RuntimeStartupState.Failed, "failure observation missing");
        Verify.That(k.LoadedPlans == 0 && k.QueuedEvents == 0, "failed startup retained work");
        Verify.Reject(() => k.RegisterModule(Module("test.lifecycle.retry"), RuntimeLogLevel.Off), "failed startup reopened registration");
        Verify.Reject(() => k.Advance(1, true), "failed startup dispatched work");
        k.StopRuntime(); owner.Dispose();
    }
    internal static void Ownership()
    {
        var k = Kernel(); var a = k.RegisterModule(Module("test.lifecycle.a"), RuntimeLogLevel.Off);
        var b = k.RegisterModule(Module("test.lifecycle.b"), RuntimeLogLevel.Off); int first = 0, second = 0;
        var old = a.ObserveLifecycle(_ => first++, false);
        using var other = b.ObserveLifecycle(_ => second++, false);
        a.Dispose(); a.Dispose();
        Verify.That(!old.IsActive && other.IsActive, "unregister leaked observers or removed another module");
        var replacement = k.RegisterModule(Module("test.lifecycle.a"), RuntimeLogLevel.Off);
        using var fresh = replacement.ObserveLifecycle(_ => first++, false);
        old.Dispose(); k.BeginWorld(2);
        Verify.That(first == 1 && second == 1 && fresh.IsActive, "old generation removed new subscription");
        RuntimeLifecycleSubscription? self = null; int once = 0;
        self = replacement.ObserveLifecycle(_ => { once++; self!.Dispose(); }, false);
        k.BeginWorld(3); k.BeginWorld(4);
        Verify.That(once == 1 && !self.IsActive, "observer cannot dispose its own subscription");
        k.StopRuntime();
    }
    internal static void Isolation()
    {
        var k = Kernel(); var owner = k.RegisterModule(Module("test.lifecycle.isolation"), RuntimeLogLevel.Off);
        int healthy = 0;
        using var bad = owner.ObserveLifecycle(_ => throw new InvalidOperationException(new string('x', 9000)), false);
        using var good = owner.ObserveLifecycle(_ => healthy++, false);
        k.StartRuntime(() => { });
        Verify.That(!bad.IsActive && good.IsActive && healthy == 2, "bad observer blocked healthy startup observers");
        Verify.That(k.LifecycleFaultCount == 1 && k.LastLifecycleFault?.ProviderId == owner.ProviderId
            && k.LastLifecycleFault.Detail.Length <= 4096, "fault evidence is missing or unbounded");
        using var mutation = owner.ObserveLifecycle(_ => k.BeginWorld(9), false);
        k.Advance(1, true);
        Verify.That(k.WorldEpoch == 1 && !mutation.IsActive && k.LifecycleFaultCount == 2,
            "observer mutated simulation state or fault was lost");
        Verify.That(k.LastLifecycleFault?.Detail.Contains("read-only", StringComparison.Ordinal) == true,
            "mutation error lost its reason");
        k.Advance(2, true);
        Verify.That(k.StartupState == RuntimeStartupState.Ready && k.LifecycleFaultCount == 2,
            "observer failure stopped gameplay or repeated indefinitely");
        k.StopRuntime();
    }
    internal static void Capacity()
    {
        var k = Kernel(); var owners = new List<RuntimeModuleHandle>();
        var held = new List<RuntimeLifecycleSubscription>();
        for (int m = 0; m < RuntimeKernel.MaximumLifecycleObservers / RuntimeKernel.MaximumLifecycleObserversPerModule; m++)
        {
            var owner = k.RegisterModule(Module("test.lifecycle.p" + m), RuntimeLogLevel.Off); owners.Add(owner);
            for (int i = 0; i < RuntimeKernel.MaximumLifecycleObserversPerModule; i++)
                held.Add(owner.ObserveLifecycle(_ => { }, false));
        }
        Verify.That(held.Count == RuntimeKernel.MaximumLifecycleObservers, "capacity setup incomplete");
        var extra = k.RegisterModule(Module("test.lifecycle.extra"), RuntimeLogLevel.Off);
        Verify.Reject(() => extra.ObserveLifecycle(_ => { }, false), "global observer budget bypassed");
        held[0].Dispose();
        using var recovered = extra.ObserveLifecycle(_ => { }, false);
        Verify.That(recovered.IsActive, "released capacity was not reusable");
        owners[0].Dispose();
        Verify.That(held.Take(RuntimeKernel.MaximumLifecycleObserversPerModule).All(s => !s.IsActive),
            "unregister did not free all owned subscriptions");
        Verify.Reject(() => owners[1].ObserveLifecycle(_ => { }, false), "per-module observer budget bypassed");
        k.StopRuntime();
        Verify.That(held.All(s => !s.IsActive) && !recovered.IsActive, "stop leaked subscriptions");
    }
    internal static void Threading()
    {
        var k = Kernel(); var owner = k.RegisterModule(Module("test.lifecycle.thread"), RuntimeLogLevel.Off);
        using var sub = owner.ObserveLifecycle(_ => { }, false);
        foreach (Action work in new Action[] { () => { _ = k.Lifecycle; },
            () => owner.ObserveLifecycle(_ => { }), () => sub.Dispose(), () => k.StartRuntime(() => { }),
            () => k.Advance(0, true), () => k.StopRuntime() })
        {
            Exception? error = null;
            var thread = new Thread(() => { try { work(); } catch (Exception e) { error = e; } });
            thread.Start(); thread.Join();
            Verify.That(error is RuntimeContractException contract && contract.Code == "wrong-thread", "cross-thread lifecycle call accepted");
        }
        Verify.That(sub.IsActive && k.IsRegistrationOpen, "rejected thread mutated state");
        k.StopRuntime();
    }
}
