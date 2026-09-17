using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeRuntime.Framework;
using HarmonyLib;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeEnemy.Native;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
// A versioned dependency is a SemVer range, so the version literal is a floor: this package loads on the
// release it was built against or any later one.
[BepInDependency("NAinfini.ForgeRuntime", ">=1.0.0")]
// GTFO-API raises GameDataAPI.OnGameDataInitialized after GameData.Initialize, which is where every
// DataBlock row — including custom EnemyDataBlocks injected by MTFO — is present; the spawn
// requirement table is built from that table and from the base prefabs. GUID and minimum version read from
// the shipped GTFO-API.dll (BepInPlugin attribute).
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.5.0")]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeEnemy";
    public const string PluginName = "Infini Forge Enemy";
    public const string PluginVersion = "1.0.0";
    private bool _loadAttempted;
    internal static EnemyPluginSession? Session { get; private set; }

    public override void Load()
    {
        if (_loadAttempted || Session != null)
            throw new InvalidOperationException("Enemy Load is single-attempt; restart after failure.");
        _loadAttempted = true;
        if (HostPlugin.ConfiguredMode == ForgeRuntime.RuntimeMode.Off)
        {
            Log.LogInfo("Forge Enemy Off: no registration or native hooks.");
            return;
        }
        var logLevel = LoggingLevel(Config);
        if (HostPlugin.IsSuspended)
        {
            // A suspended host still publishes its kernel, which is how a package tells this apart from a host that never loaded.
            Log.LogError("Forge Enemy cannot register: the Forge Runtime is suspended (reason "
                + HostPlugin.SuspensionCode + "); no bindings or native hooks were installed.");
            return;
        }
        var kernel = HostPlugin.Runtime
            ?? throw new InvalidOperationException("Forge Runtime is unavailable; Enemy cannot initialize.");
        var harmony = new Harmony(PluginGuid);
        EnemyPluginSession? created = null;
        try
        {
            created = EnemyPluginSession.Start(kernel, logLevel, () => HostPlugin.CanExecuteGameplay,
                message => Log.LogWarning(message),
                // One table, one loop: every hook family this package declares is installed here, and a family
                // left out of `EnemyHookInstall.Families` is a family nothing installs.
                () => EnemyHookInstall.Install(harmony), harmony.UnpatchSelf);
            Session = created;
            EnemySpawnRequirementSource.Attach(message => Log.LogWarning(message));
            Log.LogInfo("Forge Enemy registered; native bindings remain implementation-only.");
        }
        catch (Exception original)
        {
            try
            {
                if (created != null)
                {
                    try { created.Dispose(); }
                    catch (Exception cleanup)
                    { EnemyPluginSession.PreserveCleanupFailure(original, "ForgeEnemy.LoadCleanupFailure", cleanup, message => Log.LogWarning(message)); }
                }
            }
            finally { Session = null; }
            throw;
        }
    }

    // Native IL2CPP hooks are process-lifetime; live reload has no validated recovery contract.
    public override bool Unload() => false;

    // This package's own player-tier level, read once per Load and handed to the kernel with the registration.
    // A malformed value fails startup instead of silently selecting a default.
    private static RuntimeLogLevel LoggingLevel(ConfigFile config)
    {
        var level = config.Bind("Logging", "Level", "error",
            "Forge Enemy records written to BepInEx/forge-logs/*.jsonl: off, error or info. error writes only when something goes wrong; info also writes normal records. Error and info records are mirrored to the console. Restart required.");
        return RuntimeLogConfiguration.ParseLevel(level.Value);
    }
}
