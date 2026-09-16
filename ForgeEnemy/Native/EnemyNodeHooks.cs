using System;
using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

/// <summary>The enemy-domain observation hooks this batch adds: the spawn of an enemy, the game's own tag
/// transaction, and the moment foam really lands on an enemy's own glue receiver.
///
/// Every hook here reads state the native call has already written and publishes a fact; none of them writes the
/// world. They are separate classes from the existing `EnemyNativeHooks` entries on purpose — that list is a
/// shared registration point this batch does not edit — and they are installed by the same `Types` array the
/// integration fragment extends.</summary>
internal static class EnemyNodeHooks
{
    internal static readonly Type[] Types =
    {
        typeof(EnemyNodeSpawned), typeof(EnemyNodeTagged), typeof(EnemyNodeGlued), typeof(EnemyNodeTagPump)
    };
}

/// <summary>The generic entity-spawn fact, published for an enemy from the enemy's own spawn hook. The postfix
/// runs after the existing registration hook, so the reference this publishes is the one the module already
/// tracks; the position is read from the same instance, and an instance that does not read publishes nothing.</summary>
[HarmonyPatch(typeof(EnemySync), nameof(EnemySync.OnSpawn))]
internal static class EnemyNodeSpawned
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(EnemySync __instance) => Plugin.Session?.Guard(module =>
    {
        if (__instance.m_agent != null) module.AfterSpawn(__instance.m_agent);
    });
}

/// <summary>The game's own tag transaction, on the host: `DoTagEnemy` is what `SNet_AuthorativeAction` runs on the
/// master after a scanner client asked for a tag, and its packet carries the one enemy it tagged. The hook reads
/// that enemy back through this module's own registration, so a tag on an enemy this provider cannot name is not
/// a fact. The packet's own agent handle is read inside the module: a packet that arrives after its agent was torn
/// down answers nothing rather than throwing into the native action.</summary>
[HarmonyPatch(typeof(ToolSyncManager), nameof(ToolSyncManager.DoTagEnemy))]
internal static class EnemyNodeTagged
{
    [HarmonyPostfix]
    private static void Postfix(ToolSyncManager.pTagEnemy data) => Plugin.Session?.Guard(module => module.AfterTagged(data));
}

/// <summary>The moment foam lands on an enemy. `AddToTotalGlueVolume` is the game's own glue accumulation entry:
/// the glue gun's projectile calls it for every glued enemy, so a postfix here sees every application — one this
/// provider made and one the game made — and the two volumes it reads are what tells a real application from a
/// call that changed nothing.</summary>
[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.AddToTotalGlueVolume))]
internal static class EnemyNodeGlued
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Dam_EnemyDamageBase __instance, out EnemyModule.GlueObservation? __state)
    {
        EnemyModule.GlueObservation? captured = null;
        Plugin.Session?.Guard(module => captured = module.BeforeGlue(__instance));
        __state = captured;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_EnemyDamageBase __instance, EnemyModule.GlueObservation? __state)
        => Plugin.Session?.Guard(module => module.AfterGlue(__instance, __state));
}

/// <summary>The tag's falling edge and the marker pump, on the one per-frame hook the behaviour facts already run
/// on. A tag's native timer runs out on the game's own side and nothing in the tag transaction reports that, so
/// the state is sampled here; the same call expires every Forge marker whose lifetime ran out, which is why the
/// two live in one postfix rather than in two.</summary>
[HarmonyPatch(typeof(EnemyDetection), nameof(EnemyDetection.UpdateTargets))]
internal static class EnemyNodeTagPump
{
    [HarmonyPostfix]
    private static void Postfix(EnemyDetection __instance) => Plugin.Session?.Guard(module =>
    {
        module.ExpireMarks();
        if (__instance.m_ai is { } ai) module.ObserveTagState(ai.m_enemyAgent);
    });
}
