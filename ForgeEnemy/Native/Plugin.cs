using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeEnemy.Native;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("NAinfini.ForgeRuntime", "1.2.0")]
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
        var kernel = HostPlugin.Runtime
            ?? throw new InvalidOperationException("Forge Runtime is unavailable; Enemy cannot initialize.");
        var harmony = new Harmony(PluginGuid);
        EnemyPluginSession? created = null;
        try
        {
            created = EnemyPluginSession.Start(kernel, () => HostPlugin.CanExecuteGameplay,
                message => Log.LogWarning(message),
                () =>
                {
                    foreach (var type in EnemyNativeHooks.Types)
                        harmony.CreateClassProcessor(type).Patch();
                }, harmony.UnpatchSelf);
            Session = created;
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
}
