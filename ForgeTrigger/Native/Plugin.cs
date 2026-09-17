using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeRuntime.Framework;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeTrigger.Native;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("NAinfini.ForgeRuntime", ">=1.0.0")]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeTrigger";
    public const string PluginName = "Infini Forge Trigger";
    public const string PluginVersion = "1.0.0";
    private bool _loadAttempted;
    internal static RuntimeModuleHandle? Registration { get; private set; }

    public override void Load()
    {
        if (_loadAttempted || Registration != null)
            throw new InvalidOperationException("Trigger Load is single-attempt; restart after failure.");
        _loadAttempted = true;
        if (HostPlugin.ConfiguredMode == ForgeRuntime.RuntimeMode.Off)
        {
            Log.LogInfo("Forge Trigger Off: provider forge.module.trigger is not registered.");
            return;
        }
        var logLevel = LoggingLevel(Config);
        if (HostPlugin.IsSuspended)
        {
            // A suspended host still publishes its kernel, which is how a package tells this apart from a host that never loaded.
            Log.LogError("Forge Trigger cannot register: the Forge Runtime is suspended (reason "
                + HostPlugin.SuspensionCode + "); provider forge.module.trigger is unavailable.");
            return;
        }
        var kernel = HostPlugin.Runtime
            ?? throw new InvalidOperationException("Forge Runtime is unavailable; Trigger cannot initialize.");
        // The kernel owns the duplicate-provider rule: a second module declaring forge.module.trigger is rejected
        // with provider-conflict before anything is registered, so no explicit pre-check is repeated here.
        try
        {
            Registration = kernel.RegisterModule(ModuleDefinition.Create(), logLevel);
        }
        catch
        {
            Registration = null;
            throw;
        }
        Log.LogInfo("Forge Trigger registered provider " + ModuleDefinition.ProviderId
            + " with the evaluate binding for forge.condition.predicate.compare; no game observation or action binding.");
    }

    // The kernel outlives this plugin; a reload would re-register into a closed registration window.
    public override bool Unload() => false;

    // This package's own player-tier level, read once per Load and handed to the kernel with the registration.
    // A malformed value fails startup instead of silently selecting a default.
    private static RuntimeLogLevel LoggingLevel(ConfigFile config)
    {
        var level = config.Bind("Logging", "Level", "error",
            "Forge Trigger records written to BepInEx/forge-logs/*.jsonl: off, error or info. error writes only when something goes wrong; info also writes normal records. Error and info records are mirrored to the console. Restart required.");
        return RuntimeLogConfiguration.ParseLevel(level.Value);
    }
}
