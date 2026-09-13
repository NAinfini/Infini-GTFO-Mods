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
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeRuntime";
    public const string PluginName = "Infini Forge Runtime";
    public const string PluginVersion = "1.2.0";
    private bool _loadAttempted;
    private static bool _loadComplete;
    public static RuntimeKernel? Runtime => _loadComplete ? GameRuntimeBridge.Kernel : null;
    /// <summary>Frozen startup configuration, not readiness or a permission grant.</summary>
    public static RuntimeMode ConfiguredMode { get; private set; } = RuntimeMode.Off;
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
        var planPath = RuntimeSettings.PlanPath.Value;
        var permissions = RuntimeSettings.AllowedPermissions.Value;
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
            GameRuntimeBridge.Initialize(planPath, permissions);
            harmony.PatchAll(typeof(Plugin).Assembly);
            frameworkMonitor = AddComponent<FrameworkMonitor>();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded in {ConfiguredMode} mode. Framework plans require explicit path and permissions; native bindings are not game-verified.");
            _loadComplete = true;
        }
        catch (Exception original)
        {
            _loadComplete = false;
            var failures = new List<Exception>();
            if (hostAttempted) Rollback("runtime", GameRuntimeBridge.Stop, failures);
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
