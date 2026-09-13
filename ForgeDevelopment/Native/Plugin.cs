using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeDevelopment.Native;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("NAinfini.ForgeRuntime", "1.2.0")]
[BepInDependency("NAinfini.InfiniTweaks", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeDevelopment";
    public const string PluginName = "Infini Forge Development";
    public const string PluginVersion = "1.0.0";
    private bool _loadAttempted;
    internal static ManualLogSource PluginLog { get; private set; } = null!;

    public override void Load()
    {
        if (_loadAttempted) throw new InvalidOperationException("Forge Development Load is single-attempt; restart the process after failure.");
        _loadAttempted = true;
        PluginLog = Log;
        // Runtime Authoring is the single opt-in. Off and Play bind no diagnostic settings and start no hooks or collectors.
        if (HostPlugin.ConfiguredMode != ForgeRuntime.RuntimeMode.Authoring)
        {
            Log.LogInfo($"Forge Development inactive: Runtime mode is {HostPlugin.ConfiguredMode}; no diagnostic hooks or collectors.");
            return;
        }
        if (HostPlugin.Runtime == null)
            throw new InvalidOperationException("Forge Runtime is unavailable; Development cannot observe it.");
        Harmony? harmony = null;
        AuthoringMonitor? monitor = null;
        PerformanceMonitor? performanceMonitor = null;
        bool diagnosticsAttempted = false;
        try
        {
            Settings.Bind(Config);
            Settings.BindAuthoring(Config);
            var tweaks = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "InfiniTweaks");
            if (tweaks != null && tweaks.GetType("InfiniTweaks.Telemetry") == null)
                throw new InvalidOperationException("When installed together, Infini Forge Development requires Infini Tweaks 2.5.0 or newer. Older versions already contain a diagnostics collector.");
            diagnosticsAttempted = true;
            RuntimeDiagnostics.Initialize();
            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            monitor = AddComponent<AuthoringMonitor>();
            if (Settings.PerformanceLogging.Value) performanceMonitor = AddComponent<PerformanceMonitor>();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded for Runtime {HostPlugin.PluginVersion}. Diagnostics only observe; native hooks are not game-verified.");
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            if (harmony != null) Rollback("hooks", harmony.UnpatchSelf, failures);
            if (performanceMonitor != null) Rollback("performance_component", () => UnityEngine.Object.Destroy(performanceMonitor), failures);
            if (monitor != null) Rollback("authoring_component", () => UnityEngine.Object.Destroy(monitor), failures);
            if (diagnosticsAttempted) Rollback("diagnostics", RuntimeDiagnostics.Stop, failures);
            if (failures.Count != 0)
            {
                try { original.Data["ForgeDevelopment.StartupCleanupFailures"] = new AggregateException("Forge Development startup cleanup failures", failures); }
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
            failures.Add(new InvalidOperationException("Forge Development startup cleanup: " + stage, error));
            try { Log.LogError($"Forge Development startup cleanup {stage} failed: {error}"); }
            catch (Exception reporter) { failures.Add(new InvalidOperationException("Forge Development startup cleanup reporter: " + stage, reporter)); }
        }
    }

    // Native log callbacks and IL2CPP components are process-lifetime; no hot unload contract exists.
    public override bool Unload() => false;
}
