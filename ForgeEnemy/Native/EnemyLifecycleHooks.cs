using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

[HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.OnDead))]
internal static class EnemyDeathStarted
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(EnemyAgent __instance, out EnemyModule.LifecycleObservation? __state)
    {
        EnemyModule.LifecycleObservation? captured = null;
        Plugin.Session?.Guard(module => captured = module.BeforeDeath(__instance));
        __state = captured;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(EnemyAgent __instance, EnemyModule.LifecycleObservation? __state)
        => Plugin.Session?.Guard(module => module.AfterDeath(__instance, __state));
}

[HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.DestroyLimb))]
internal static class EnemyLimbBroken
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Dam_EnemyDamageLimb __instance, out EnemyModule.LifecycleObservation? __state)
    {
        EnemyModule.LifecycleObservation? captured = null;
        Plugin.Session?.Guard(module => captured = module.BeforeLimbBreak(__instance));
        __state = captured;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_EnemyDamageLimb __instance, EnemyModule.LifecycleObservation? __state)
        => Plugin.Session?.Guard(module => module.AfterLimbBreak(__instance, __state));
}
