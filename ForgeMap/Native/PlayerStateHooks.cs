using System;
using Agents;
using HarmonyLib;
using Player;

namespace ForgeMap.Native;

/// <summary>Every Harmony patch the player-state observation installs, plus the facade each one calls.
///
/// The damage path is read in two layers, because the game splits it that way:
/// <list type="bullet">
/// <item>Each receive entry (`ReceiveBulletDamage` and its siblings) knows the kind of damage it carries but not
/// whether it lands; its prefix records the kind for the duration of the call, and its postfix clears it.</item>
/// <item>`Dam_PlayerDamageBase.OnIncomingDamage` is the one accept path every receive entry funnels through. Its
/// postfix publishes a fact only when the game's own answer was `true`, so a refused application (ignored damage,
/// a god-mode target) is not an application.</item>
/// </list>
/// The precedence on the two halves is fixed: `Prefix` runs first and the accept path's postfix runs inside it,
/// so the kind a fact carries is the kind of the entry that is running right now.
///
/// The infection write is a prefix/postfix pair on the one write entry: the prefix reads the value the receiver
/// holds before the write, the postfix publishes the value it holds after, and the delta between them is the write
/// that really happened.
///
/// The low-health event is not patched here: it arrives through the game-event funnel, which the player-event
/// observation owns because three other rows are read from the same call.</summary>
/// <summary>The kind of the damage receive entry currently running on this thread, as the index the `damage_kind`
/// set declares, or null when the running call is not one of the entries this half names. Cleared by the same
/// entry's postfix, so a nested call cannot leak a kind into an outer one. The member names live in one place —
/// `PlayerStateContract.DamageKindName` — and the journal spells them through it.</summary>
internal static class PlayerDamageKindToken
{
    [ThreadStatic] private static int? current;

    internal static int? Current => current;

    internal static void Enter(int kind) => current = kind;

    internal static void Leave() => current = null;
}

/// <summary>The one accept path of the player damage base. The fact is published from the postfix, after the
/// game's own answer is known, and carries the damage the path accepted rather than the amount a caller asked
/// for.</summary>
[HarmonyPatch(typeof(Dam_PlayerDamageBase), "OnIncomingDamage")]
internal static class PlayerDamageAccepted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_PlayerDamageBase __instance, float damage, Agent sourceAgent, bool __result)
    {
        if (!__result) return;
        Plugin.Session?.GuardState(facts => facts.DamageApplied(__instance, damage, sourceAgent, PlayerDamageKindToken.Current));
    }
}

/// <summary>The infection write entry the action layer also submits through: the prefix reads the value before
/// the write, the postfix publishes what changed. A write that changed nothing publishes nothing.</summary>
[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ModifyInfection))]
internal static class PlayerInfectionWritten
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Dam_PlayerDamageBase __instance, out float __state) => __state = Infection(__instance);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_PlayerDamageBase __instance, float __state)
        => Plugin.Session?.GuardState(facts => facts.InfectionChanged(__instance, __state));

    private static float Infection(Dam_PlayerDamageBase damage)
    {
        try { return damage == null ? float.NaN : damage.Infection; }
        catch (Exception) { return float.NaN; }
    }
}

// The receive entries and the `damage_kind` member each one names. A kind this half cannot name honestly is left
// unnamed: the entry sets no token, so its fact carries no `damage_kind` port instead of a guess. The mapping
// table and the entries left out are frozen in evidence/player-state-facts.json.

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveBulletDamage))]
internal static class PlayerBulletDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => Plugin.Session?.Guard(_ => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDirect));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => PlayerDamageKindToken.Leave();
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveShooterProjectileDamage))]
internal static class PlayerProjectileDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => Plugin.Session?.Guard(_ => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDirect));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => PlayerDamageKindToken.Leave();
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveMeleeDamage))]
internal static class PlayerMeleeDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => Plugin.Session?.Guard(_ => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindMelee));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => PlayerDamageKindToken.Leave();
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveExplosionDamage))]
internal static class PlayerExplosionDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => Plugin.Session?.Guard(_ => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindExplosion));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => PlayerDamageKindToken.Leave();
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveFallDamage))]
internal static class PlayerFallDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => Plugin.Session?.Guard(_ => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindFall));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => PlayerDamageKindToken.Leave();
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveFireDamage))]
internal static class PlayerFireDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => Plugin.Session?.Guard(_ => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDot));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => PlayerDamageKindToken.Leave();
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveStickyDamage))]
internal static class PlayerStickyDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => Plugin.Session?.Guard(_ => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDot));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => PlayerDamageKindToken.Leave();
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveParasiteDamage))]
internal static class PlayerParasiteDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => Plugin.Session?.Guard(_ => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDot));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => PlayerDamageKindToken.Leave();
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceivePushDamage))]
internal static class PlayerPushDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => Plugin.Session?.Guard(_ => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindCollision));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => PlayerDamageKindToken.Leave();
}

// The receive entries this half deliberately does not name — tentacle attack, tentacle grab, tentacle trap,
// parasite trap, tank grab, grab stop and no-air damage — carry no honest member of the catalog's `damage_kind`
// set, so their applications publish with the port absent. They need no patch of their own: the token is cleared
// by the postfix of the entry that set it, so an entry that never sets one publishes an unnamed kind. They are
// listed in the evidence file.
