using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeWeapon.Native;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("NAinfini.ForgeRuntime", "1.2.0")]
// Equipment owners are ForgeMap's gtfo.player references; without Map every backpack would stay ownerless.
[BepInDependency("NAinfini.ForgeMap", "0.1.0")]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeWeapon";
    public const string PluginName = "Infini Forge Weapon";
    public const string PluginVersion = "0.1.0";
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
        var kernel = HostPlugin.Runtime
            ?? throw new InvalidOperationException("Forge Runtime is unavailable; Weapon cannot initialize.");
        var harmony = new Harmony(PluginGuid);
        WeaponNativeSession? created = null;
        try
        {
            created = WeaponNativeSession.Start(kernel, () => HostPlugin.CanExecuteGameplay,
                message => Log.LogWarning(message), message => Log.LogInfo(message),
                () =>
                {
                    foreach (var type in WeaponNativeHooks.Types)
                        harmony.CreateClassProcessor(type).Patch();
                }, harmony.UnpatchSelf);
            Log.LogInfo("Forge Weapon registered equipment identity with gtfo.player owners; native bindings remain implementation-only.");
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
}
