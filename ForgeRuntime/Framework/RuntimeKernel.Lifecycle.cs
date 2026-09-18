using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeRuntime.Framework;

public sealed partial class RuntimeModuleHandle
{
    public RuntimeLifecycleSubscription ObserveLifecycle(Action<RuntimeLifecycleEvent> observer,
        bool replayCurrent = true) => kernel.ObserveLifecycle(this, observer, replayCurrent);
}

public sealed partial class RuntimeKernel
{
    public const int MaximumLifecycleObservers = 256;
    public const int MaximumLifecycleObserversPerModule = 8;
    private sealed record LifecycleObserver(long Id, string Provider, long Generation,
        Action<RuntimeLifecycleEvent> Callback);
    private readonly Dictionary<long, LifecycleObserver> lifecycleObservers = new();
    private long lifecycleObserverSequence;
    private bool notifyingLifecycle;
    public RuntimeStartupState StartupState { get; private set; } = RuntimeStartupState.Registering;
    public long LifecycleFaultCount { get; private set; }
    public RuntimeLifecycleFault? LastLifecycleFault { get; private set; }
    public RuntimeLifecycleSnapshot Lifecycle
    { get { ReadThread(); return new(StartupState, WorldEpoch, CurrentTick, worldHost); } }
    public bool IsRegistrationOpen => StartupState == RuntimeStartupState.Registering;
    private void ReadThread()
        => RuntimeJson.Require(Environment.CurrentManagedThreadId == threadId, "wrong-thread",
            "Runtime APIs must run on the owning simulation thread.");
    private void NoLifecycleMutation()
    {
        NoEntityObservationMutation();
        RuntimeJson.Require(!notifyingLifecycle, "lifecycle-observer-mutation",
            "Lifecycle observers are read-only; only their subscription may be disposed.");
        RuntimeJson.Require(!notifyingBehaviorTrace, "behavior-trace-observer-mutation",
            "Behavior trace observers are read-only; only their subscription may be disposed.");
    }
    private void AcceptRuntimeWork(bool allowStarting = false)
    {
        NoLifecycleMutation();
        RuntimeJson.Require(StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            && (allowStarting || StartupState != RuntimeStartupState.Starting),
            "runtime-not-ready", "Runtime startup failed, stopped or is still initializing.");
    }

    /// <summary>Freezes registration before initialization. Failure is latched until a new host instance.</summary>
    public bool StartRuntime(Action initialization)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(initialization);
        if (StartupState != RuntimeStartupState.Registering) return false;
        StartupState = RuntimeStartupState.Starting;
        NotifyLifecycle(RuntimeLifecycleKind.StartupChanged);
        try { initialization(); StartupState = RuntimeStartupState.Ready; }
        catch
        {
            StartupState = RuntimeStartupState.Failed; ClearLifecycleWork("startup-failed");
            LogSuspended("startup-failed");
            NotifyLifecycle(RuntimeLifecycleKind.StartupChanged); throw;
        }
        NotifyLifecycle(RuntimeLifecycleKind.StartupChanged);
        return true;
    }

    public void StopRuntime()
    {
        Mutable();
        RuntimeJson.Require(StartupState != RuntimeStartupState.Starting, "startup-in-progress",
            "Runtime cannot stop inside its initialization callback.");
        if (StartupState == RuntimeStartupState.Stopped) return;
        StartupState = RuntimeStartupState.Stopped;
        ClearLifecycleWork("runtime-stopped");
        NotifyLifecycle(RuntimeLifecycleKind.StartupChanged);
        lifecycleObservers.Clear();
        ClearBehaviorTraceObservers();
    }
    private void ClearLifecycleWork(string code)
    {
        StopScheduledSource(null, null, code); StopStateSource(null, null, code);
        queue.Clear(); plans.Clear(); subscriptions.Clear();
    }
    internal RuntimeLifecycleSubscription ObserveLifecycle(RuntimeModuleHandle owner,
        Action<RuntimeLifecycleEvent> callback, bool replayCurrent)
    {
        Mutable(); AcceptRuntimeWork(true); ArgumentNullException.ThrowIfNull(callback);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        RuntimeJson.Require(lifecycleObservers.Count < MaximumLifecycleObservers
            && lifecycleObservers.Values.Count(o => o.Provider == owner.ProviderId) < MaximumLifecycleObserversPerModule,
            "lifecycle-observer-budget", "Lifecycle observer capacity reached.");
        var id = checked(++lifecycleObserverSequence);
        var observer = new LifecycleObserver(id, owner.ProviderId, owner.Generation, callback);
        lifecycleObservers.Add(id, observer);
        if (replayCurrent)
        {
            notifyingLifecycle = true;
            try { InvokeLifecycle(observer, new(RuntimeLifecycleKind.Snapshot, Lifecycle)); }
            finally { notifyingLifecycle = false; }
        }
        return new RuntimeLifecycleSubscription(this, id);
    }
    internal bool IsLifecycleObserverActive(long id)
    { ReadThread(); return lifecycleObservers.ContainsKey(id); }
    internal void RemoveLifecycleObserver(long id)
    { ReadThread(); NoEntityObservationMutation(); lifecycleObservers.Remove(id); }
    private void RemoveLifecycleObservers(string provider, long moduleGeneration)
    {
        foreach (var observer in lifecycleObservers.Values
            .Where(o => o.Provider == provider && o.Generation == moduleGeneration).ToArray())
            lifecycleObservers.Remove(observer.Id);
    }
    private void NotifyLifecycle(RuntimeLifecycleKind kind, long? previousWorldEpoch = null)
    {
        if (lifecycleObservers.Count == 0) return;
        var value = new RuntimeLifecycleEvent(kind, Lifecycle, previousWorldEpoch);
        notifyingLifecycle = true;
        try
        {
            foreach (var observer in lifecycleObservers.Values.OrderBy(o => o.Id).ToArray())
                if (lifecycleObservers.ContainsKey(observer.Id)
                    && IsRegistered(observer.Provider, observer.Generation)) InvokeLifecycle(observer, value);
        }
        finally { notifyingLifecycle = false; }
    }
    private void InvokeLifecycle(LifecycleObserver observer, RuntimeLifecycleEvent value)
    {
        try { observer.Callback(value); }
        catch (Exception error)
        {
            lifecycleObservers.Remove(observer.Id);
            if (LifecycleFaultCount < long.MaxValue) LifecycleFaultCount++;
            LastLifecycleFault = new(observer.Provider, RuntimeLogReasonCodes.LifecycleObserverFailed,
                CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message));
            LogObserverFailed(observer.Provider, error.Message);
        }
    }

    public TickResult Advance(long simulationTick, bool isHost)
    {
        Thread();
        var previousTick = CurrentTick;
        var result = AdvanceCore(simulationTick, isHost);
        if (CurrentTick != previousTick) NotifyLifecycle(RuntimeLifecycleKind.TickAdvanced);
        return result;
    }
}
