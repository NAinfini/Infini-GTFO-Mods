using System;

namespace ForgeRuntime.Framework;

public enum RuntimeStartupState { Registering, Starting, Ready, Failed, Stopped }
public enum RuntimeLifecycleKind { Snapshot, StartupChanged, WorldChanged, TickAdvanced }

/// <summary>Managed observation only; no Unity objects, wall clock or gameplay authority grant.</summary>
public sealed record RuntimeLifecycleSnapshot(RuntimeStartupState StartupState,
    long WorldEpoch, long SimulationTick, bool? IsHost);

public sealed record RuntimeLifecycleEvent(RuntimeLifecycleKind Kind,
    RuntimeLifecycleSnapshot Current, long? PreviousWorldEpoch = null);

public sealed record RuntimeLifecycleFault(string ProviderId, string Code, string Detail);

public sealed class RuntimeLifecycleSubscription : IDisposable
{
    private readonly RuntimeKernel kernel;
    internal RuntimeLifecycleSubscription(RuntimeKernel kernel, long id)
    { this.kernel = kernel; Id = id; }
    internal long Id { get; }
    public bool IsActive => kernel.IsLifecycleObserverActive(Id);
    public void Dispose() => kernel.RemoveLifecycleObserver(Id);
}
