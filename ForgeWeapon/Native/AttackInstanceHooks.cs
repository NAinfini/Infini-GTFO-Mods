using Gear;
using HarmonyLib;

namespace ForgeWeapon.Native;

/// <summary>The attack-instance hook set. Each hook only chooses when to read; it reads nothing itself and
/// publishes nothing itself.
///
/// The ranged half is one prefix and one postfix on each of this build's four `Fire` bodies. Those four are the
/// complete set — `RifleWeapon` and `RifleWeaponSynced` declare no `Fire` of their own and use their family's
/// body, and no `Fire` body reaches another — which is why one patch per body yields exactly one attack scope
/// per trigger pull. The synced pair is not a duplicate of the unsynced pair: a remote player's weapon never
/// runs the unsynced body on this machine, so without them the host would see no attack from any client. The
/// call structure is frozen in `evidence/w4-player-fire.json`.
///
/// The postfix is the close, not a second observation: `BulletWeapon.BulletHit` is called from inside the `Fire`
/// body (twice for a shotgun, both sites inside the pellet loop), so by the time the body returns every hit this
/// attack caused has already been published and the scope can answer the miss question.
///
/// Melee has no `Fire`. `MeleeWeaponFirstPerson.DoTriggerAttack` is the attack entry and `OnAttackHitDone` is the
/// end of the swing, which is the only completion signal the melee path has.
///
/// The empty-clip fact comes from the game's own empty-clip paths rather than from a clip readback: `BWA_Auto`
/// and `BWA_Burst` each declare `OnFireShotEmptyClip`, and those are the two burst-capable archetypes this build
/// has (`BWA_SemiBurst` derives from `BWA_Semi` and declares no empty-clip override of its own). The burst
/// sequence ends are the two archetypes that declare them, `BWA_Burst` and `BWA_Auto`.
///
/// Every body below is inert unless <see cref="WeaponNativeSession.Current"/> is a live session, which is what
/// keeps a hook from running over a half-built one.</summary>

[HarmonyPatch(typeof(Gear.BulletWeapon), nameof(BulletWeapon.Fire))]
internal static class AttackFireRequested
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(BulletWeapon __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Request(__instance, AttackInstanceModule.AttackMode.Ranged));
}

[HarmonyPatch(typeof(Gear.BulletWeapon), nameof(BulletWeapon.Fire))]
internal static class AttackFireCompleted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BulletWeapon __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Complete(__instance));
}

[HarmonyPatch(typeof(Gear.Shotgun), nameof(Shotgun.Fire))]
internal static class AttackShotgunRequested
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Shotgun __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Request(__instance, AttackInstanceModule.AttackMode.Ranged));
}

[HarmonyPatch(typeof(Gear.Shotgun), nameof(Shotgun.Fire))]
internal static class AttackShotgunCompleted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Shotgun __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Complete(__instance));
}

// The synced pair: the copy every other machine holds of somebody else's weapon, replayed from the replicated
// shot count. A remote player's attack reaches this machine here and nowhere else.
[HarmonyPatch(typeof(Gear.BulletWeaponSynced), nameof(BulletWeaponSynced.Fire))]
internal static class AttackSyncedFireRequested
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(BulletWeaponSynced __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Request(__instance, AttackInstanceModule.AttackMode.Ranged));
}

[HarmonyPatch(typeof(Gear.BulletWeaponSynced), nameof(BulletWeaponSynced.Fire))]
internal static class AttackSyncedFireCompleted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BulletWeaponSynced __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Complete(__instance));
}

[HarmonyPatch(typeof(Gear.ShotgunSynced), nameof(ShotgunSynced.Fire))]
internal static class AttackSyncedShotgunRequested
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(ShotgunSynced __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Request(__instance, AttackInstanceModule.AttackMode.Ranged));
}

[HarmonyPatch(typeof(Gear.ShotgunSynced), nameof(ShotgunSynced.Fire))]
internal static class AttackSyncedShotgunCompleted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ShotgunSynced __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Complete(__instance));
}

// Melee: the swing starts at its own attack entry and ends at `OnAttackHitDone`. No ammo is spent, so the
// accepted rule of the ranged path (the shot counter moved) has nothing to read here; the swing's own end is the
// only evidence the melee path produces, and the attack is reported as a request and a completion around it.
[HarmonyPatch(typeof(Gear.MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.DoTriggerAttack))]
internal static class AttackMeleeRequested
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(MeleeWeaponFirstPerson __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Request(__instance, AttackInstanceModule.AttackMode.Melee));
}

[HarmonyPatch(typeof(Gear.MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.OnAttackHitDone))]
internal static class AttackMeleeCompleted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MeleeWeaponFirstPerson __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Attack.Complete(__instance));
}

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
