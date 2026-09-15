using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeRuntime.Framework;
using HarmonyLib;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeMap.Native;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("NAinfini.ForgeRuntime", "1.2.0")]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeMap";
    public const string PluginName = "Infini Forge Map";
    public const string PluginVersion = "0.1.0";
    private bool _loadAttempted;
    internal static MapPluginSession? Session { get; private set; }

    public override void Load()
    {
        if (_loadAttempted || Session != null)
            throw new InvalidOperationException("Map Load is single-attempt; restart after failure.");
        _loadAttempted = true;
        if (HostPlugin.ConfiguredMode == ForgeRuntime.RuntimeMode.Off)
        {
            Log.LogInfo("Forge Map Off: no registration or native hooks.");
            return;
        }
        var logLevel = LoggingLevel(Config);
        var kernel = HostPlugin.Runtime
            ?? throw new InvalidOperationException("Forge Runtime is unavailable; Map cannot initialize.");
        var harmony = new Harmony(PluginGuid);
        MapPluginSession? created = null;
        try
        {
            created = MapPluginSession.Start(kernel, logLevel, message => Log.LogWarning(message), message => Log.LogInfo(message),
                () =>
                {
                    foreach (var type in MapNativeHooks.Types)
                        harmony.CreateClassProcessor(type).Patch();
                }, harmony.UnpatchSelf);
            Session = created;
            Log.LogInfo("Forge Map registered gtfo.player identity; native bindings remain implementation-only.");
        }
        catch (Exception original)
        {
            try
            {
                if (created != null)
                {
                    try { created.Dispose(); }
                    catch (Exception cleanup)
                    { MapPluginSession.PreserveCleanupFailure(original, "ForgeMap.LoadCleanupFailure", cleanup, message => Log.LogWarning(message)); }
                }
            }
            finally { Session = null; }
            throw;
        }
    }

    // Native IL2CPP hooks are process-lifetime; live reload has no validated recovery contract.
    public override bool Unload() => false;

    // D-007: this package's own player-tier level, read once per Load and handed to the kernel with the registration.
    // A malformed value fails startup instead of silently selecting a default.
    private static RuntimeLogLevel LoggingLevel(ConfigFile config)
    {
        var level = config.Bind("Logging", "Level", "error",
            "Forge Map records written to BepInEx/forge-logs/*.jsonl: off, error or info. error writes only when something goes wrong; info also writes normal records. Error and info records are mirrored to the console. Restart required.");
        return RuntimeLogConfiguration.ParseLevel(level.Value);
    }
}
