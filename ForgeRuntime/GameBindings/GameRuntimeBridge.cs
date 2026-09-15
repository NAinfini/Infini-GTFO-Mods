using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using ForgeRuntime.Framework;
using ForgeRuntime.Logging;
using SNetwork;
using UnityEngine;

namespace ForgeRuntime.GameBindings;

internal static class GameRuntimeBridge
{
    internal const string GameBuild = "20403457";
    internal const string GameAssemblySha256 = "C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55";
    internal static RuntimeKernel? Kernel { get; private set; }
    private static RuntimeLogWriter? _log;
    private static bool _inLevel, _suspended, _wasHost, _blockedUntilLobby, _dispatching, _pendingInvalidation;
    private static long _epoch, _tick;
    private static bool _pendingStop;
    internal static bool CanExecute
    {
        get
        {
            var kernel = Kernel;
            return kernel != null && kernel.Lifecycle.StartupState == RuntimeStartupState.Ready
                && _inLevel && !_suspended && !_pendingInvalidation && !_pendingStop && _wasHost && SNet.IsMaster
                && GameStateManager.CurrentStateName == eGameStateName.InLevel
                && (SNet.MasterManagement == null || !SNet.MasterManagement.IsMigrating);
        }
    }

    internal static void Initialize(RuntimeLogLevel logLevel)
    {
        if (Kernel != null) throw new InvalidOperationException("Forge Runtime is already initialized.");
        using (var stream = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll")))
        {
            using var sha256 = SHA256.Create();
            string actual = Convert.ToHexString(sha256.ComputeHash(stream));
            if (!string.Equals(actual, GameAssemblySha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Forge native binding does not support this GameAssembly hash: " + actual);
        }
        _pendingStop = false;
        _inLevel = _suspended = _blockedUntilLobby = _dispatching = _pendingInvalidation = false; _epoch = 1; _tick = 0;
        // The writer carries the Runtime provider id itself: a `log.dropped` record written before the first accepted record
        // has no level table to read it from.
        _log = new RuntimeLogWriter(Path.Combine(Paths.BepInExRootPath, RuntimeLogWriter.DirectoryName), "forge.runtime", Plugin.PluginLog, RuntimeLogLimits.Default);
        Kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, GameBuild), new RuntimeLimits(), _log, logLevel);
        Kernel.BeginWorld(_epoch);
        // D-007: the Runtime's own providers carry the Runtime cfg level; only packages with their own plugin pass one.
        Kernel.RegisterBuiltinModule(CombatContracts.Module());
        Kernel.RegisterBuiltinModule(ControlContracts.Module());
        Kernel.RegisterBuiltinModule(ForgeTrigger.ModuleDefinition.Create());
    }

    internal static void FixedTick()
    {
        var kernel = Kernel;
        if (kernel == null) return;
        if (kernel.StartupState == RuntimeStartupState.Registering)
        {
            kernel.StartRuntime(() =>
            {
                // BepInEx dependent plugins have registered through Plugin.Runtime before the first Unity tick.
                FrameworkFiles.WriteManifest(Paths.BepInExRootPath, kernel.ExportManifest());
                var discovered = PlanDiscovery.Scan(Paths.BepInExRootPath);
                if (discovered.Count > 0) kernel.LoadPlans(discovered);
            });
        }
        if (kernel.StartupState != RuntimeStartupState.Ready) return;
        if (!_inLevel || _suspended) return;
        if (SNet.IsMaster != _wasHost || (SNet.MasterManagement != null && SNet.MasterManagement.IsMigrating))
        { Suspend("host-migration-unsupported", "Host migration is unsupported for this native binding; return to lobby and start a new expedition.", true); return; }
        _dispatching = true;
        try { kernel.Advance(checked(++_tick), SNet.IsMaster); }
        finally
        {
            _dispatching = false;
            if (_pendingStop) Stop();
            else if (_pendingInvalidation) { _pendingInvalidation = false; InvalidateWorld(); }
        }
    }

    internal static void StateChanged(eGameStateName nextState)
    {
        if (Kernel == null) return;
        if (nextState is eGameStateName.Lobby or eGameStateName.NoLobby or eGameStateName.Offline) _blockedUntilLobby = false;
        if (nextState == eGameStateName.Generating)
        {
            InvalidateWorld(); _suspended = _blockedUntilLobby; _inLevel = false;
        }
        else if (nextState == eGameStateName.InLevel)
        {
            if (!_inLevel) _wasHost = SNet.IsMaster;
            _inLevel = true;
        }
        else if (_inLevel)
        {
            _inLevel = false; InvalidateWorld();
        }
    }

    internal static void InvalidateWorld()
    {
        if (Kernel == null) return;
        // Native callbacks may invalidate the world during a command; flush the core after its current dispatch returns.
        if (_dispatching) { _pendingInvalidation = true; return; }
        if (Kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        Kernel.BeginWorld(checked(++_epoch)); _tick = 0;
    }

    internal static void EndWorld() { _inLevel = false; InvalidateWorld(); }

    internal static void Suspend(string code, string detail, bool untilLobby = false)
    {
        _blockedUntilLobby |= untilLobby;
        if (_suspended) return;
        _suspended = true; InvalidateWorld();
        // I-DIAG: the pause is a kernel record written through the same sink as every other one; the host adds no BepInEx line.
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
        Kernel?.StopRuntime();
        _log?.Dispose(); _log = null;
        _pendingStop = _pendingInvalidation = false;
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
