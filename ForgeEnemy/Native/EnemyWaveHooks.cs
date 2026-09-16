using System;
using System.Collections.Generic;
using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

/// <summary>The native members that bound a survival wave in build 20403457, all of them verified entry points in
/// `ForgeMap/evidence/encounter-wave-hooks.json` (RVA in parentheses):
///
///   * `SurvivalWave.OnSpawn` (0x13F4810) — the replicator spawn callback; the one point where a wave becomes
///     live. Patched before the body for the observation token and after it for the fact, because the wave's own
///     EventID is what its spawn body registers with the Mastermind.
///   * `SurvivalWave.SpawnGroup` (0x13F4FB0) — the private per-group spawn step. Its call is the batch boundary
///     the game itself does not have: everything `RegisterGroup` does between the prefix and the postfix belongs
///     to that one batch.
///   * `SurvivalWave.RegisterGroup` (0x13F4C30) — the wave producing one group: the delivered side.
///   * `SurvivalWave.TryEndEvent` (0x13F5E90) — the wave's own completion test; its true answer is the closest
///     native signal to "nothing left to spend".
///   * `SurvivalWave.OnDespawn` (0x13F4780) — the teardown callback, which fires for level cleanup and
///     cancellation too, so it only retires bookkeeping and publishes nothing.
///   * `Mastermind.MaintainGroups` (0x14F75B0) — the per-tick maintenance of `m_activeGroups`; the group list
///     after it is the native answer to whether a wave still has anything alive.
///
/// Every hook only reads native state and publishes facts; none of them writes the world. A wave hook whose
/// bindings no plan subscribes to returns before reading any game state.</summary>
internal static class EnemyWaveHooks
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
        { typeof(EnemyWaveSpawn), typeof(EnemyWaveGroupStep), typeof(EnemyWaveGroup),
          typeof(EnemyWaveEndTest), typeof(EnemyWaveDespawn), typeof(EnemyGroupMaintenance) });
}

[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.OnSpawn))]
internal static class EnemyWaveSpawn
{
    [HarmonyPrefix]
    private static void Prefix(SurvivalWave __instance, out EnemyWaveFacts.WaveSpawnObservation? __state)
    {
        EnemyWaveFacts.WaveSpawnObservation? captured = null;
        Plugin.Session?.Guard(module => captured = module.BeforeWaveSpawn(__instance));
        __state = captured;
    }

    [HarmonyPostfix]
    private static void Postfix(SurvivalWave __instance, EnemyWaveFacts.WaveSpawnObservation? __state)
        => Plugin.Session?.Guard(module => module.AfterWaveSpawn(__instance, __state));
}

// The private per-batch spawn step. Its prefix opens the batch and its postfix closes it, so a group registered
// from outside this call is never counted into a batch.
[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.SpawnGroup))]
internal static class EnemyWaveGroupStep
{
    [HarmonyPrefix]
    private static void Prefix(SurvivalWave __instance)
        => Plugin.Session?.Guard(module => module.BeforeWaveGroupStep(__instance));

    [HarmonyPostfix]
    private static void Postfix(SurvivalWave __instance)
        => Plugin.Session?.Guard(module => module.AfterWaveGroupStep(__instance));
}

// A produced group joins the wave's live set and, inside a batch, that batch's delivered set.
[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.RegisterGroup))]
internal static class EnemyWaveGroup
{
    [HarmonyPostfix]
    private static void Postfix(SurvivalWave __instance, EnemyGroup group)
        => Plugin.Session?.Guard(module => module.AfterWaveGroup(__instance, group));
}

// The wave's own completion test: only its true answer is a fact, and the postfix reads the returned bool.
[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.TryEndEvent))]
internal static class EnemyWaveEndTest
{
    [HarmonyPostfix]
    private static void Postfix(SurvivalWave __instance, bool __result)
        => Plugin.Session?.Guard(module => module.AfterWaveEndTest(__instance, __result));
}

// `SurvivalWave.OnDespawn` is a replicator teardown callback: level cleanup, event cancellation and a spent wave
// all reach it, so it retires this provider's wave state and publishes no fact.
[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.OnDespawn))]
internal static class EnemyWaveDespawn
{
    [HarmonyPrefix]
    private static void Prefix(SurvivalWave __instance)
        => Plugin.Session?.Guard(module => module.BeforeWaveDespawn(__instance));
}

[HarmonyPatch(typeof(Mastermind), nameof(Mastermind.MaintainGroups))]
internal static class EnemyGroupMaintenance
{
    [HarmonyPostfix]
    private static void Postfix() => Plugin.Session?.Guard(module => module.AfterGroupMaintenance());
}
