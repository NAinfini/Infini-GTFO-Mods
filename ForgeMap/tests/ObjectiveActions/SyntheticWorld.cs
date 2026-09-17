using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMap.Tests.NativeObjectiveActions;

/// <summary>
/// The world one case runs against: the native singletons the action layer reaches, plus the kernel whose
/// lifecycle snapshot is the authority gate the handler reads. Every native object here is a managed double;
/// nothing loads a GTFO assembly and nothing is game-verified.
/// </summary>
internal sealed class SyntheticWorld : IDisposable
{
    private SyntheticWorld(RuntimeKernel kernel, WardenObjectiveManager objectives,
        CheckpointManager checkpoints, ElevatorShaftLanding landing)
    {
        Kernel = kernel;
        Objectives = objectives;
        Checkpoints = checkpoints;
        Landing = landing;
    }

    internal RuntimeKernel Kernel { get; }
    internal WardenObjectiveManager Objectives { get; }
    internal CheckpointManager Checkpoints { get; }
    internal ElevatorShaftLanding Landing { get; }
    internal readonly List<string> Reports = new();

    /// <summary>One live world: the kernel in its own world with the host flag the game's session would report,
    /// and the three singletons the game keeps, each standing in for the one instance the process has.</summary>
    internal static SyntheticWorld Start(long worldEpoch = 1, bool host = true)
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        kernel.BeginWorld(worldEpoch);
        // The host flag is published by the first tick, which is what the handler's authority gate reads
        // through the lifecycle snapshot.
        kernel.Advance(0, host);
        var objectives = WardenObjectiveManager.Current = new WardenObjectiveManager();
        var checkpoints = CheckpointManager.Current = new CheckpointManager();
        var landing = ElevatorShaftLanding.Current = new ElevatorShaftLanding();
        return new SyntheticWorld(kernel, objectives, checkpoints, landing);
    }

    /// <summary>Builds the level's objective data: the chain indices one layer was built with, which is also
    /// what makes that layer one `HasWardenObjectiveDataForLayer` answers for.</summary>
    internal SyntheticWorld WithChain(LG_LayerType layer, params int[] chainIndices)
    {
        WardenObjectiveManager.Chains[layer] = new List<int>(chainIndices);
        return this;
    }

    /// <summary>The production handler over this world's singletons. The authority gate is the case's own
    /// answer, so a case can stand on the host side or the client side without a second gate of its own.</summary>
    internal ObjectiveActionHandler Handler(bool canExecute = true)
        => new(() => canExecute, () => WardenObjectiveManager.Current, () => ElevatorShaftLanding.Current,
            Reports.Add);

    internal static void Reset()
    {
        WardenObjectiveManager.Reset();
        CheckpointManager.Reset();
        ElevatorShaftLanding.Reset();
    }

    public void Dispose()
    {
        WardenObjectiveManager.Current = null;
        CheckpointManager.Current = null;
        ElevatorShaftLanding.Current = null;
    }
}

