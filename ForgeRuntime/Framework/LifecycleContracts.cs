using System;

namespace ForgeRuntime.Framework;

public enum RuntimeStartupState { Registering, Starting, Ready, Failed, Stopped }

/// <summary>The boundaries an observer is told about. <c>Snapshot</c> is the state a subscription starts from,
/// <c>StartupChanged</c> a move of the startup state, <c>WorldChanged</c> a new world epoch, <c>TickAdvanced</c>
/// one advance that moved the simulation tick, and <c>CheckpointRestored</c> a save put back over the running
/// world: the same expedition and the same epoch, with the level's own objects rebuilt from the game's data.</summary>
public enum RuntimeLifecycleKind { Snapshot, StartupChanged, WorldChanged, TickAdvanced, CheckpointRestored }

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
