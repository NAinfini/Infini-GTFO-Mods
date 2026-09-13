using System;
using System.Collections.Generic;
using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

internal static class EnemyNativeHooks
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
        { typeof(EnemySpawned), typeof(EnemyDespawned), typeof(EnemyDamage), typeof(EnemyDeathStarted), typeof(EnemyLimbBroken) });
}

[HarmonyPatch(typeof(EnemySync), nameof(EnemySync.OnSpawn))]
internal static class EnemySpawned
{
    [HarmonyPostfix]
    private static void Postfix(EnemySync __instance) => Plugin.Session?.Guard(module =>
    {
        if (__instance.m_agent != null) module.TrackSpawn(__instance.m_agent);
    });
}

[HarmonyPatch(typeof(EnemySync), nameof(EnemySync.OnDespawn))]
internal static class EnemyDespawned
{
    [HarmonyPrefix]
    private static void Prefix(EnemySync __instance) => Plugin.Session?.Guard(module =>
    {
        // Native synchronous boundary: capture the current life before any teardown.
        if (__instance.m_agent != null)
            module.CompleteDespawn(module.CaptureDespawn(__instance.m_agent));
    });
}

[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ProcessReceivedDamage))]
internal static class EnemyDamage
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Dam_EnemyDamageBase __instance, out EnemyModule.DamageObservation? __state)
    {
        EnemyModule.DamageObservation? captured = null;
        Plugin.Session?.Guard(module => captured = module.BeforeDamage(__instance));
        __state = captured;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_EnemyDamageBase __instance, EnemyModule.DamageObservation? __state)
        => Plugin.Session?.Guard(module => module.AfterDamage(__instance, __state));
}
