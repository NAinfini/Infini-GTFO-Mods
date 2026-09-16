using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

internal static class EnemyNativeHooks
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
        { typeof(EnemySpawned), typeof(EnemyDespawned), typeof(EnemyDamage), typeof(EnemyDeathStarted), typeof(EnemyLimbBroken),
          typeof(EnemyBehaviorPump), typeof(EnemyScoutDetected) });
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

// `ProcessReceivedDamage` is the busiest hook in this package and the one funnel every committed hit passes
// through, so both windows a received hit opens share this one prefix/postfix pair instead of racing two patches
// on the same member. The health window (`BeforeDamage`/`AfterDamage`) publishes the pre-existing
// `damage_applied` and `health_changed` facts; the instance window
// (`BeforeDamageInstance`/`AfterDamageInstance`) publishes `forge.trigger.combat.killed` and
// `forge.trigger.combat.limb_damaged`. Both close in the order they opened, and each consumes its own token
// exactly once, so a replayed callback cannot publish twice.
//
// `limbID` is a parameter of the receiver's own declaration (build 20403457:
// `ProcessReceivedDamage(float, Agent, Vector3, Vector3, ES_HitreactType, bool, int limbID, float,
// DamageNoiseLevel, uint)`), recorded by Harmony by name off the interop signature. It is the array position the
// caller named, not an `m_limbID`.
[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ProcessReceivedDamage))]
internal static class EnemyDamage
{
    /// <summary>The four facts this window can publish. The prefix is entered only while something subscribes
    /// to at least one of them, so a build with no plan for enemy combat pays a four-key lookup and nothing
    /// else.</summary>
    private static readonly string[] ObservedBindings =
    {
        EnemyModule.DamageBinding, EnemyModule.HealthChangedBinding,
        ForgeEnemy.AttackInstanceContract.BindingId, ForgeEnemy.AttackInstanceContract.LimbDamagedBindingId
    };

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Dam_EnemyDamageBase __instance, int limbID, out State __state)
    {
        State captured = default;
        Plugin.Session?.Guard(module =>
        {
            if (!Observed(module)) return;
            captured.Health = module.BeforeDamage(__instance);
            captured.Instance = module.BeforeDamageInstance(__instance, limbID);
        });
        __state = captured;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_EnemyDamageBase __instance, State __state)
        => Plugin.Session?.Guard(module =>
        {
            module.AfterDamage(__instance, __state.Health);
            module.AfterDamageInstance(__instance, __state.Instance);
        });

    /// <summary>Whether any of the four facts has a plan. A window nothing subscribes to is never opened, so
    /// the two observations of this call stay unallocated.</summary>
    private static bool Observed(EnemyModule module)
    {
        foreach (var binding in ObservedBindings) if (module.HasSubscriber(binding)) return true;
        return false;
    }

    /// <summary>The two observations of one native call, carried as one token.</summary>
    private struct State
    {
        internal EnemyModule.DamageObservation? Health;
        internal EnemyModule.DamageInstanceObservation? Instance;
    }
}

// Every awake behaviour update calls this member (21 EB_*.UpdateBehaviour bodies), and the two Setup paths call
// it once when an enemy is created, so one frame hook sees both the spawn baseline and every later transition of
// AI state, detection build-up and target validity. A sleeping enemy in EB_Hibernating runs its own detection
// path instead and is next sampled when it wakes, which is exactly the sample that carries the wake-up.
// The hook only reads native state and publishes facts; it never writes the world.
[HarmonyPatch(typeof(EnemyDetection), nameof(EnemyDetection.UpdateTargets))]
internal static class EnemyBehaviorPump
{
    [HarmonyPostfix]
    private static void Postfix(EnemyDetection __instance) => Plugin.Session?.Guard(module =>
    {
        // The interop getter returns null when the native EnemyDetection.m_ai pointer is zero, so a detection
        // component without its AI is skipped instead of reporting behaviour for an enemy that cannot be named.
        if (__instance.m_ai is { } ai) module.ObserveEnemyBehavior(ai);
    });
}

// Native detection registration for a scout tendril: the game has already accepted the target, so the hook
// reports it with the position the native target carries instead of scanning for one.
[HarmonyPatch(typeof(ES_ScoutDetection), nameof(ES_ScoutDetection.OnTargetRegistered))]
internal static class EnemyScoutDetected
{
    [HarmonyPostfix]
    private static void Postfix(ES_ScoutDetection __instance, AgentTarget target)
        => Plugin.Session?.Guard(module => module.ObserveScoutDetection(__instance.m_owner, target));
}
