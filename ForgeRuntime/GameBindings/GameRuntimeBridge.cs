using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using ForgeRuntime.Framework;
using ForgeRuntime.Logging;
using ForgeRuntime.Network;
using SNetwork;
using UnityEngine;

namespace ForgeRuntime.GameBindings;

internal static class GameRuntimeBridge
{
    internal const string GameBuild = "20403457";
    // Not a const: the compiled metadata check reads it as a field. A mismatch harness cannot replace this readonly
    // field once the type is initialized, so it points the game root at another binary instead.
    internal static readonly string GameAssemblySha256 = "C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55";
    /// <summary>Reason code for a host that refused to start; the only reason raised before the kernel exists.</summary>
    internal const string StartupFailed = "startup-failed";
    internal static RuntimeKernel? Kernel { get; private set; }
    /// <summary>The runtime latched as suspended, so no package may execute anything. Read by packages through
    /// <see cref="Plugin.IsSuspended"/>; a suspended host still publishes its kernel, which is how a package tells
    /// "suspended" apart from "the host never loaded".</summary>
    internal static string? Suspension { get; private set; }
    private static RuntimeLogWriter? _log;
    private static bool _inLevel, _suspended, _wasHost, _blockedUntilLobby, _dispatching, _pendingStop;
    private static long _epoch, _tick;
    /// <summary>What the last checkpoint of this expedition carried: every variable value and `g-once` latch the
    /// world held when the game saved. A reload puts these back instead of suspending the runtime, so the same
    /// expedition keeps running with the behaviour re-armed. Null until this expedition saved one.</summary>
    private static string? _checkpoint;
    /// <summary>A world transition that arrived while a command was dispatching. The kernel moves at its safe point,
    /// so the transition is remembered here and replayed once, at the epoch the kernel really reached.</summary>
    private static PendingWorldChange _pendingWorldChange;
    internal static bool CanExecute
    {
        get
        {
            var kernel = Kernel;
            return kernel != null && kernel.Lifecycle.StartupState == RuntimeStartupState.Ready
                && _inLevel && !_suspended && !_pendingWorldChange.Pending && !_pendingStop && _wasHost && SNet.IsMaster
                && GameStateManager.CurrentStateName == eGameStateName.InLevel
                && (SNet.MasterManagement == null || !SNet.MasterManagement.IsMigrating);
        }
    }

    /// <summary>Checks the native GameAssembly identity and builds the kernel plus its writer. A hash mismatch is not a
    /// startup exception: the kernel is latched as suspended, one runtime.suspended record is written, and dependent
    /// packages keep loading against a runtime that never executes anything.</summary>
    internal static void Initialize(RuntimeLogLevel logLevel)
    {
        if (Kernel != null) throw new InvalidOperationException("Forge Runtime is already initialized.");
        _pendingStop = false;
        _inLevel = _suspended = _wasHost = _blockedUntilLobby = _dispatching = false; _epoch = 1; _tick = 0;
        _pendingWorldChange = default;
        _checkpoint = null;
        Suspension = null;
        // The writer carries the Runtime provider id itself: a `log.dropped` record written before the first accepted record
        // has no level table to read it from.
        _log = new RuntimeLogWriter(Path.Combine(Paths.BepInExRootPath, RuntimeLogWriter.DirectoryName), "forge.runtime", Plugin.PluginLog, RuntimeLogLimits.Default);
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", Plugin.PluginVersion, RuntimeKernel.ApiVersion, GameBuild), new RuntimeLimits(), _log, logLevel);
        Kernel = kernel;
        // The Runtime's own providers carry the Runtime cfg level; only packages with their own plugin pass one.
        kernel.RegisterBuiltinModule(CombatContracts.Module());
        kernel.RegisterBuiltinModule(ControlContracts.Module());
        // The variable, level-object and message rows are the kernel's own too: a plan pins them like any other
        // binding, and no package registers them on the runtime's behalf.
        kernel.RegisterBuiltinModule(VariableContracts.Module());
        // Every Trigger shape the runtime publishes, before any domain package registers the binding that
        // publishes it: a binding whose capability is not registered yet is refused as `missing-capability`.
        kernel.RegisterBuiltinModule(TriggerContracts.Module());
        // The rows that read any entity a wire already carries: the kernel answers them for every provider's own
        // entity kind, so no domain package declares them and this is where they are registered.
        kernel.RegisterBuiltinModule(ObservationContracts.Module());
        var mismatch = HashMismatch();
        if (mismatch != null) { SuspendStartup(kernel, mismatch); return; }
        kernel.BeginWorld(_epoch);
        // No session exists yet; the binding only subscribes to SNet here, and a session's own host is built when
        // SNet reports one, so the plan snapshot it compares is the one discovered after this point.
        NetworkBinding.Start();
    }

    /// <summary>The actual GameAssembly hash when it differs from the supported binding, null when it matches.</summary>
    private static string? HashMismatch()
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
            using var sha256 = SHA256.Create();
            var actual = Convert.ToHexString(sha256.ComputeHash(stream));
            return string.Equals(actual, GameAssemblySha256, StringComparison.Ordinal) ? null : actual;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return "unreadable (" + error.Message + ")"; }
    }

    /// <summary>No StartRuntime and no world: the binding does not support this binary, so nothing derived from it may run.</summary>
    private static void SuspendStartup(RuntimeKernel kernel, string actual)
    {
        Suspension = StartupFailed;
        _suspended = true;
        var reason = actual.StartsWith("unreadable", StringComparison.Ordinal)
            ? "GameAssembly could not be read: " + actual
            : "GameAssembly hash " + actual + " does not match the supported binding " + GameAssemblySha256 + " (game build " + GameBuild + ").";
        // The one record this host writes. It is gated exactly like every other suspension, so the cfg level decides.
        kernel.LogSuspended(StartupFailed, reason);
    }

    internal static void FixedTick()
    {
        var kernel = Kernel;
        if (kernel == null) return;
        // A suspended host never starts: it exports no manifest and loads no plan from a binary the binding does not support.
        if (Suspension != null) return;
        if (kernel.StartupState == RuntimeStartupState.Registering)
        {
            kernel.StartRuntime(() =>
            {
                // BepInEx dependent plugins have registered through Plugin.Runtime before the first Unity tick.
                FrameworkFiles.WriteManifest(Paths.BepInExRootPath, kernel.ExportManifest());
                var discovered = PlanDiscovery.Scan(Paths.BepInExRootPath);
                if (discovered.Count > 0) kernel.LoadPlans(discovered);
                // Until this tick this side had adopted no plan set, so every hello it answered was refused with
                // plans-unavailable and left owed. Adopting the set here announces this side's own hello: on the host
                // that announcement is the readiness signal a refused client waits for, and its retry is then
                // answerable on content.
                NetworkBinding.AdoptPlans(kernel.PlanIdentities);
            });
        }
        if (kernel.StartupState != RuntimeStartupState.Ready) return;
        if (!_inLevel || _suspended) return;
        if (SNet.IsMaster != _wasHost || (SNet.MasterManagement != null && SNet.MasterManagement.IsMigrating))
        { Suspend("host-migration-unsupported", "Host migration is unsupported for this native binding; return to lobby and start a new expedition.", true); return; }
        _dispatching = true;
        try
        {
            var tick = kernel.Advance(checked(++_tick), SNet.IsMaster);
            // The presentation steps this tick decided go out to the players their provider named. Sending them
            // here, inside the dispatch guard, is what keeps one advance's intents with the epoch they belong to:
            // a world that ends while they are on the wire ends with them.
            NetworkBinding.Present(tick.Presentations);
            // The owner steps follow the same rule and the same guard: each is addressed to the one session that
            // holds what it changes, and a world that ends while one is on the wire ends with it.
            NetworkBinding.Own(tick.OwnerCommands);
            // The host's variable state follows the same rule: one body per advance, sent after it, so the values a
            // client applies are the ones the advance it just executed produced.
            NetworkBinding.SyncVariables();
        }
        finally
        {
            _dispatching = false;
            if (_pendingStop) Stop();
            // The deferred transition is announced by the one advance it belongs to: taking it consumes it, so a
            // dispatch that queued several transitions still sends a single world epoch.
            else if (_pendingWorldChange.Take(out var transition, out var detail)) InvalidateWorld(transition, detail);
        }
    }

    /// <summary>GTFO-API <c>LevelAPI.OnBuildStart</c>: level generation began, so the previous world is dropped and a
    /// suspension that waited for a fresh expedition is released. This is the only release point: the previous lobby
    /// transition is not observable through GTFO-API 0.5.0, and a new build is a stronger guarantee than a game state
    /// name, so a session that never starts another expedition stays suspended instead of resuming inside the old one.</summary>
    internal static void BeginGeneration()
    {
        // A startup suspension is process-wide: no lifecycle event may begin a world on a kernel that never started.
        if (Kernel == null || Suspension != null) return;
        // A new expedition starts: the previous one's checkpoint names a world that is gone.
        _checkpoint = null;
        InvalidateWorld(WorldTransition.NewGeneration, "level generation started");
        _suspended = _blockedUntilLobby;
        _blockedUntilLobby = false;
        _inLevel = false;
    }

    /// <summary>GTFO-API <c>LevelAPI.OnEnterLevel</c>: the local player can move, the point at which the game state
    /// reaches InLevel. The host baseline is taken once per world, so a later promotion still reads as a migration.</summary>
    internal static void EnterLevel()
    {
        if (Kernel == null || Suspension != null) return;
        if (!_inLevel) _wasHost = SNet.IsMaster;
        _inLevel = true;
        NetworkBinding.RefreshSession();
    }

    /// <summary>GTFO-API <c>LevelAPI.OnLevelCleanup</c>: the level is gone, which replaces the previous
    /// <c>GameStateManager.OnLevelCleanup</c> and <c>OnResetSession</c> prefixes.</summary>
    internal static void LeaveLevel()
    {
        if (Kernel == null || Suspension != null) return;
        _inLevel = false;
        InvalidateWorld(WorldTransition.LevelCleanup, "level cleanup");
    }

    /// <summary>
    /// The kernel moves to a new world, once, at the point where no dispatch is running: the epoch rises, the tick
    /// restarts, and the host announces the epoch it really reached. A transition that arrives mid-dispatch is
    /// remembered instead, because the kernel has not moved yet and a broadcast of a world that does not exist yet
    /// would be a lie the clients would act on.
    /// </summary>
    internal static void InvalidateWorld(WorldTransition transition, string detail)
    {
        if (Kernel == null) return;
        if (_dispatching) { _pendingWorldChange.Remember(transition, detail); return; }
        if (Kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        Kernel.BeginWorld(checked(++_epoch)); _tick = 0;
        NetworkBinding.AdvanceWorld(_epoch, transition, detail);
    }

    /// <summary>
    /// GTFO-API 0.5.0 has no checkpoint event, so the save point is the game's own
    /// <c>CheckpointManager.StoreCheckpoint</c>. What it captures is the variables and `g-once` latches of the world
    /// being saved; only the host has them (a client's copy is the host's snapshot), which is what the execute gate
    /// says.
    /// </summary>
    internal static void CaptureCheckpoint()
    {
        var kernel = Kernel;
        if (kernel == null || Suspension != null || !CanExecute) return;
        _checkpoint = kernel.CaptureCheckpoint();
    }

    /// <summary>
    /// <c>CheckpointManager.ReloadCheckpoint</c>: the level goes back to the checkpoint, so the behaviour is
    /// re-armed and the saved values are put back — the runtime is not suspended and the world epoch does not move,
    /// because this is the same expedition. A checkpoint this expedition never saved restores nothing.
    /// </summary>
    internal static void RestoreCheckpoint()
    {
        var kernel = Kernel;
        if (kernel == null || Suspension != null || _checkpoint == null || !_inLevel) return;
        var discovered = PlanDiscovery.Scan(Paths.BepInExRootPath);
        if (discovered.Count > 0) kernel.RestoreCheckpoint(_checkpoint, discovered);
        else kernel.RestoreCheckpoint(_checkpoint);
    }

    internal static void Suspend(string code, string detail, bool untilLobby = false)    {
        _blockedUntilLobby |= untilLobby;
        if (_suspended) return;
        _suspended = true; InvalidateWorld(WorldTransition.HostSuspend, detail);
        // The pause is a kernel record written through the same sink as every other one; the host adds no BepInEx line.
        if (Kernel is { } kernel && kernel.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped))
            kernel.LogSuspended(code, detail);
    }

    internal static void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { Suspend("bridge-exception", error.Message); }
    }

    internal static void Stop()
    {
        _inLevel = false; _suspended = true;
        // Native teardown may occur inside a handler. Block commits immediately, clean the kernel at its safe point.
        if (_dispatching) { _pendingStop = true; return; }
        NetworkBinding.Stop();
        Kernel?.StopRuntime();
        _log?.Dispose(); _log = null;
        _pendingStop = false;
        _pendingWorldChange = default;
        Kernel = null;
    }
}

public sealed class FrameworkMonitor : MonoBehaviour
{
    public FrameworkMonitor(IntPtr pointer) : base(pointer) { }
    public void FixedUpdate() => GameRuntimeBridge.Guard(GameRuntimeBridge.FixedTick);
    public void OnApplicationQuit() => GameRuntimeBridge.Stop();
    public void OnDestroy() => GameRuntimeBridge.Stop();
}
