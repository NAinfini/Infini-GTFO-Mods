using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeRuntime.Framework;
using HarmonyLib;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeWeapon.Native;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("NAinfini.ForgeRuntime", ">=1.2.0")]
// Equipment owners are ForgeMap's gtfo.player references; without Map every backpack would stay ownerless.
[BepInDependency("NAinfini.ForgeMap", ">=0.1.0")]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeWeapon";
    public const string PluginName = "Infini Forge Weapon";
    public const string PluginVersion = "0.2.0";
    private bool _loadAttempted;

    public override void Load()
    {
        if (_loadAttempted || WeaponNativeSession.Current != null)
            throw new InvalidOperationException("Weapon Load is single-attempt; restart after failure.");
        _loadAttempted = true;
        if (HostPlugin.ConfiguredMode == ForgeRuntime.RuntimeMode.Off)
        {
            Log.LogInfo("Forge Weapon Off: no registration or native hooks.");
            return;
        }
        var logLevel = LoggingLevel(Config);
        if (HostPlugin.IsSuspended)
        {
            // A suspended host still publishes its kernel, which is how a package tells this apart from a host that never loaded.
            Log.LogError("Forge Weapon cannot register: the Forge Runtime is suspended (reason "
                + HostPlugin.SuspensionCode + "); no bindings or native hooks were installed.");
            return;
        }
        var kernel = HostPlugin.Runtime
            ?? throw new InvalidOperationException("Forge Runtime is unavailable; Weapon cannot initialize.");
        var harmony = new Harmony(PluginGuid);
        WeaponNativeSession? created = null;
        try
        {
            created = WeaponNativeSession.Start(kernel, logLevel, () => HostPlugin.CanExecuteGameplay,
                message => Log.LogWarning(message), message => Log.LogInfo(message),
                () =>
                {
                    // One list, one loop: every family's hooks are carried in `Installed`, so the package's
                    // reload, placement, device and melee patches are installed with the ones it already had.
                    foreach (var type in WeaponNativeHooks.Installed)
                        harmony.CreateClassProcessor(type).Patch();
                }, harmony.UnpatchSelf, Paths.BepInExRootPath);
            Log.LogInfo("Forge Weapon authored equipment, shot, hit, reload, melee-hit and deployed-device facts"
                + " with gtfo.player owners, and executes the game's own enemy tag, the ammunition add/consume"
                + " pair, the three weapon instance overrides and the inventory give/consume pair; native bindings"
                + " remain implementation-only.");
        }
        catch (Exception original)
        {
            if (created != null)
            {
                try { created.Dispose(); }
                catch (Exception cleanup)
                { WeaponNativeSession.PreserveCleanupFailure(original, "ForgeWeapon.LoadCleanupFailure", cleanup, message => Log.LogWarning(message)); }
            }
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
            "Forge Weapon records written to BepInEx/forge-logs/*.jsonl: off, error or info. error writes only when something goes wrong; info also writes normal records. Error and info records are mirrored to the console. Restart required.");
        return RuntimeLogConfiguration.ParseLevel(level.Value);
    }
}
