using Gear;
using HarmonyLib;

namespace ForgeWeapon.Native;

/// <summary>The attack-instance hook set: the game's own empty-clip paths and the two ends of a burst sequence.
/// Each hook only chooses when to read; it reads nothing itself and publishes nothing itself.
///
/// The empty-clip fact comes from the game's own empty-clip paths rather than from a clip readback: `BWA_Auto`
/// and `BWA_Burst` each declare `OnFireShotEmptyClip`, and those are the two burst-capable archetypes this build
/// has (`BWA_SemiBurst` derives from `BWA_Semi` and declares no empty-clip override of its own). The burst
/// sequence ends are the two archetypes that declare them, `BWA_Burst` and `BWA_Auto`.
///
/// Every body below is inert unless <see cref="WeaponNativeSession.Current"/> is a live session, which is what
/// keeps a hook from running over a half-built one.</summary>

// The game's own empty-clip paths. The fact is published from the game's decision and never from comparing a
// clip readback against zero, which is a value that can be replenished between any two observations.
[HarmonyPatch(typeof(Gear.BWA_Auto), nameof(BWA_Auto.OnFireShotEmptyClip))]
internal static class AttackAutoFiredEmptyClip
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Auto __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.DryFire(__instance.m_weapon));
}

[HarmonyPatch(typeof(Gear.BWA_Burst), nameof(BWA_Burst.OnFireShotEmptyClip))]
internal static class AttackBurstFiredEmptyClip
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Burst __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.DryFire(__instance.m_weapon));
}

// Both ends of the burst sequence. `count` is the weapon's own `m_burstMax`, read from the weapon the archetype
// was set up on; the archetype is the instance the hook already holds.
[HarmonyPatch(typeof(Gear.BWA_Burst), nameof(BWA_Burst.OnStartFiring))]
internal static class AttackBurstStarted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Burst __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Burst(__instance.m_weapon, started: true));
}

[HarmonyPatch(typeof(Gear.BWA_Burst), nameof(BWA_Burst.OnStopFiring))]
internal static class AttackBurstEnded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Burst __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Burst(__instance.m_weapon, started: false));
}

[HarmonyPatch(typeof(Gear.BWA_Auto), nameof(BWA_Auto.OnStartFiring))]
internal static class AttackAutoStarted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Auto __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Burst(__instance.m_weapon, started: true));
}

[HarmonyPatch(typeof(Gear.BWA_Auto), nameof(BWA_Auto.OnStopFiring))]
internal static class AttackAutoEnded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Auto __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Burst(__instance.m_weapon, started: false));
}
