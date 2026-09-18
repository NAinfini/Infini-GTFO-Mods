using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using ForgeRuntime.Framework;
using ForgeRuntime.GameBindings;

namespace ForgeRuntime;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
// A versioned dependency is always a hard dependency, and the version argument is a SemVer range rather than a
// floor: a bare version would refuse to load on any newer GTFO-API at all. The host therefore requires the
// release it was built against or a later one, instead of silently binding level events that may have moved.
[BepInDependency(GtfoApiGuid, GtfoApiMinimumVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeRuntime";
    /// <summary>GUID declared by the shipped GTFO-API assembly; the host subscribes to its level events.</summary>
    public const string GtfoApiGuid = "dev.gtfomodding.gtfo-api";
    /// <summary>The lowest GTFO-API release this host was built and validated against: the `provides` version
    /// release.json pins for the base dependency that ships the assembly.</summary>
    public const string GtfoApiMinimumVersion = ">=0.5.0";
    public const string PluginName = "Infini Forge Runtime";
    public const string PluginVersion = "1.0.0";
    // The latch is per process, not per instance, so it is static like the readiness flag it guards.
    private static bool _loadAttempted;
    private static bool _loadComplete;
    public static RuntimeKernel? Runtime => _loadComplete ? GameRuntimeBridge.Kernel : null;
    /// <summary>Frozen startup configuration, not readiness or a permission grant.</summary>
    public static RuntimeMode ConfiguredMode { get; private set; } = RuntimeMode.Off;
    /// <summary>True when the host latched the runtime as suspended, so packages must stop registering and report it.</summary>
    public static bool IsSuspended => GameRuntimeBridge.Suspension != null;
    /// <summary>The reason code for <see cref="IsSuspended"/>, null while the runtime is not suspended.</summary>
    public static string? SuspensionCode => GameRuntimeBridge.Suspension;
    /// <summary>Simulation-thread phase/authority gate; each action must still validate its own receiver and permissions.</summary>
    public static bool CanExecuteGameplay => _loadComplete && GameRuntimeBridge.CanExecute;
    internal static ManualLogSource PluginLog { get; private set; } = null!;

    public override void Load()
    {
        if (_loadAttempted) throw new InvalidOperationException("Forge Runtime Load is single-attempt; restart the process after failure.");
        _loadAttempted = true;
        _loadComplete = false;
        ConfiguredMode = RuntimeMode.Off;
        PluginLog = Log;
        RuntimeSettings.Bind(Config);
        ConfiguredMode = RuntimeSettings.Mode;
        var logLevel = RuntimeSettings.LogLevel;
        if (ConfiguredMode == RuntimeMode.Off)
        {
            Log.LogInfo("Infini Forge Runtime Off: framework hooks disabled; dependent plugins stay inactive.");
            return;
        }
        Harmony? harmony = null;
        FrameworkMonitor? frameworkMonitor = null;
        bool hostAttempted = false;
        try
        {
            harmony = new Harmony(PluginGuid);
            hostAttempted = true;
            GameRuntimeBridge.Initialize(logLevel);
            // Authoring is the one mode that promises complete behaviour-editor observability. Elevate while
            // registration is still open so built-in, already-registered and later domain providers all emit
            // Trace lifecycle records for the whole session; Play keeps the configured player-tier level.
            if (ConfiguredMode == RuntimeMode.Authoring && GameRuntimeBridge.Kernel is { } authoringKernel
                && GameRuntimeBridge.Suspension == null)
                authoringKernel.ElevateLogging();
            harmony.PatchAll(typeof(Plugin).Assembly);
            LevelLifecycle.Subscribe();
            frameworkMonitor = AddComponent<FrameworkMonitor>();
            // A suspended host only prints this line; each dependent package reports the suspension from its own plugin entry.
            if (GameRuntimeBridge.Suspension is { } reason)
                Log.LogWarning($"{PluginName} {PluginVersion} suspended ({reason}) in {ConfiguredMode} mode on game build {GameRuntimeBridge.GameBuild}; dependent plugins will not register.");
            else
                Log.LogInfo($"{PluginName} {PluginVersion} loaded in {ConfiguredMode} mode. Plans are discovered under BepInEx/plugins/*/forge/plans; native bindings are not game-verified.");
            _loadComplete = true;
        }
        catch (Exception original)
        {
            _loadComplete = false;
            var failures = new List<Exception>();
            if (hostAttempted) Rollback("runtime", GameRuntimeBridge.Stop, failures);
            // The network binding's SNet subscription and session host are process-lifetime too, so they are cleaned
            // unconditionally: a failure before the host start leaves nothing to release, and releasing twice is a
            // no-op rather than a second teardown path.
            Rollback("network", NetworkBinding.Stop, failures);
            // Subscriptions are process-lifetime, so cleanup is unconditional and idempotent rather than stage-flagged.
            Rollback("level_events", LevelLifecycle.Unsubscribe, failures);
            if (harmony != null) Rollback("hooks", harmony.UnpatchSelf, failures);
            if (frameworkMonitor != null) Rollback("framework_component", () => UnityEngine.Object.Destroy(frameworkMonitor), failures);
            if (failures.Count != 0)
            {
                try { original.Data["ForgeRuntime.StartupCleanupFailures"] = new AggregateException("Forge startup cleanup failures", failures); }
                catch (Exception) { /* An unwritable exception Data dictionary must not replace the startup exception. */ }
            }
            throw;
        }
    }

    // Rollback runs while the original startup exception is being handled, so it must never throw: an escaping
    // exception would replace that original. Every stage failure, the failing stage's own BepInEx report included,
    // is retained on the original exception's Data instead.
    private void Rollback(string stage, Action cleanup, List<Exception> failures)
    {
        try { cleanup(); }
        catch (Exception error)
        {
            failures.Add(new InvalidOperationException("Forge startup cleanup: " + stage, error));
            try { Log.LogError($"Forge startup cleanup {stage} failed: {error}"); }
            catch (Exception reporter) { failures.Add(new InvalidOperationException("Forge startup cleanup reporter: " + stage, reporter)); }
        }
    }

    // This process-lifetime IL2CPP component does not support hot reloading.
    public override bool Unload() => false;
}
