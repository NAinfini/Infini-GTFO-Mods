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
[BepInDependency("NAinfini.ForgeRuntime", ">=1.2.0")]
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
        ExperimentRunner? experimentRunner = null;
        ExperimentPanel? experimentPanel = null;
        bool diagnosticsAttempted = false;
        bool recorderAttempted = false;
        bool captureAttempted = false;
        try
        {
            Settings.Bind(Config);
            Settings.BindAuthoring(Config);
            Settings.BindRecorder(Config);
            var tweaks = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "InfiniTweaks");
            if (tweaks != null && tweaks.GetType("InfiniTweaks.Telemetry") == null)
                throw new InvalidOperationException("When installed together, Infini Forge Development requires Infini Tweaks 2.5.0 or newer. Older versions already contain a diagnostics collector.");
            diagnosticsAttempted = true;
            RuntimeDiagnostics.Initialize();
            // The recorder starts after the diagnostics that report its failures and before this assembly's hooks, so
            // a trace patch that fails is reported by a pipeline that is already listening.
            recorderAttempted = true;
            RecRuntime.Start(BepInEx.Paths.BepInExRootPath);
            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            monitor = AddComponent<AuthoringMonitor>();
            // The capture and the experiments are this package's own components and are attached in the one branch
            // that may collect anything. The capture monitor rides the authoring monitor's game object, and the
            // experiment runner and panel have to exist before F5 can start or draw a command.
            captureAttempted = true;
            CaptureRegistry.EnsureStarted(monitor, CaptureRegistry.DefaultPeriodicSeconds);
            experimentRunner = AddComponent<ExperimentRunner>();
            experimentPanel = AddComponent<ExperimentPanel>();
            ExperimentPanel.Load();
            if (Settings.PerformanceLogging.Value) performanceMonitor = AddComponent<PerformanceMonitor>();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded for Runtime {HostPlugin.PluginVersion}. Diagnostics only observe; native hooks are not game-verified.");
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            // Teardown is the reverse of startup: the components that write into the session are released first, then
            // the recorder, whose trace patches sit on game methods and whose session owns a writer thread, and only
            // then the hooks that report the failure.
            if (experimentPanel != null) Rollback("experiment_panel", () => UnityEngine.Object.Destroy(experimentPanel!), failures);
            if (experimentRunner != null) Rollback("experiment_runner", () => UnityEngine.Object.Destroy(experimentRunner!), failures);
            if (captureAttempted) Rollback("capture", CaptureRegistry.Stop, failures);
            if (recorderAttempted) Rollback("recorder", RecRuntime.Stop, failures);
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
    public override bool Unload()
    {
        RecRuntime.Stop();
        return false;
    }
}
