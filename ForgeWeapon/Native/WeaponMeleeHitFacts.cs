using System;
using System.Collections.Generic;
using System.Globalization;
using Agents;
using Enemies;
using ForgeRuntime.Framework;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>
/// The one row of <see cref="WeaponMeleeHitContract"/>, read from the swing's own per-target hit entry on the
/// machine that performs the swing. Two hooks carry it here, and they never describe the same hit on the same
/// machine:
/// <list type="bullet">
/// <item><b>First person</b> — `MeleeWeaponFirstPerson.DoAttackDamage` is this machine's own player swinging. The
/// attacker is the weapon's own `Owner`, the target is the damageable the hit data resolved, the damage is the
/// weapon's own `m_damageToDeal` — the value `SetNextDamageToDeal(eMeleeWeaponDamage, float)` fixed for this hit
/// and never a number this package computes — and the charge is the weapon's own current state: a swing the game
/// labelled as one of its two charge-hit states is charged, one of the two plain hit states is not, and any other
/// state leaves the port absent rather than guessed.</item>
/// <item><b>Third person</b> — `MeleeWeaponThirdPerson.DoAttackDamage` is a swing this machine replays for
/// somebody else, which in this build is the bot melee action's strike: the damage arrives as the body's own
/// argument and the attacker is the weapon's `Owner`. A third-person swing has no charge flag this build can
/// read — the light/heavy choice is made by the wielder's own first-person weapon — so the port stays absent.</item>
/// </list>
/// A shove is not a swing: the first-person body is entered with `isPush` set for the push damage and that hit is
/// left to the push path, so one row does not carry two actions.
///
/// The row's third source is a remote player's swing, which the host observes from the replicated melee damage;
/// that half is <see cref="WeaponMeleeRemoteFacts"/> and it publishes through <see cref="RemoteHit"/> on this
/// observer, so both halves share one ledger and one host gate. The gate is the adapter's own
/// (`Authoritative()`), so a client publishes nothing and its swing leaves the host only as that replicated
/// damage.
///
/// Nothing here applies damage, reads health or counts a swing: every published value is a read of the call the
/// game is already making (the remote half's damage is the health its own receive body took).
/// </summary>
internal sealed class WeaponMeleeHitFacts : IDisposable
{
    /// <summary>The live observer, or null before the session is built and after it is disposed.</summary>
    internal static WeaponMeleeHitFacts? Current { get; private set; }

    private readonly EquipmentNativeAdapter _adapter;
    private readonly RuntimeKernel _kernel;
    private readonly Action<string> _report, _info;
    /// <summary>The one ledger both native sources claim through, so one landed hit is published once however many
    /// bodies observed it. `WeaponMeleeRemoteFacts` is the other source.</summary>
    private readonly MeleeHitLedger _ledger = new();
    private long _world = -1, _sequence;
    private bool _disposed;

    internal WeaponMeleeHitFacts(EquipmentNativeAdapter adapter, RuntimeKernel kernel, Action<string> report,
        Action<string> info)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>Installs the observer. Called by the session before its hooks are patched, so no hook can run
    /// over a half-built observer.</summary>
    internal static WeaponMeleeHitFacts Attach(EquipmentNativeAdapter adapter, RuntimeKernel kernel,
        Action<string> report, Action<string> info)
    {
        if (Current != null) throw new InvalidOperationException("Melee hit facts are single-instance.");
        var facts = new WeaponMeleeHitFacts(adapter, kernel, report, info);
        Current = facts;
        return facts;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>One target of the local player's own swing. The damage is the weapon's `m_damageToDeal`, which is
    /// the value the swing fixed before the hit entry ran; a hit entered as a push is the push path and publishes
    /// nothing here.</summary>
    internal void SwingHit(MeleeWeaponFirstPerson? weapon, MeleeWeaponDamageData? data, bool isPush)
    {
        if (weapon == null || data == null || isPush) return;
        Publish(weapon.Owner, data.damageGO, weapon.m_damageToDeal, Charge(weapon.CurrentStateName), "first-person");
    }

    /// <summary>One target of a swing this machine runs for somebody else: the damage is the body's own argument,
    /// and the charge is the one port a third-person swing cannot answer.</summary>
    internal void ThirdPersonHit(MeleeWeaponThirdPerson? weapon, GameObject? damageGO, float damage)
    {
        if (weapon == null) return;
        Publish(weapon.Owner, damageGO, damage, null, "third-person");
    }

    /// <summary>The row's second native source: a remote player's swing, observed on the host from the replicated
    /// melee damage. The attacker and the limb come from the packet, the target is the enemy life the receiver
    /// belongs to, and the damage is the health the receive body really took — so the charge is the one port this
    /// source cannot answer and stays absent.
    ///
    /// The gate is the ledger's own rule: a hit whose attacker is this machine's own player or a bot is already
    /// published by the hit entry that performed it, so this half publishes neither. A claim already held for the
    /// same attacker, target and limb is the same hit observed twice and is skipped.</summary>
    internal void RemoteHit(PlayerAgent? attacker, EnemyAgent? enemy, int limb, float damage)
    {
        if (attacker == null || enemy == null) return;
        if (!Authoritative()) return;
        var owner = attacker.Owner;
        if (owner == null) return;
        bool isLocal, isBot;
        try { isLocal = owner.IsLocal; isBot = owner.IsBot; }
        catch (Exception) { return; }
        if (!MeleeHitLedger.PublishesRemote(isLocal, isBot)) return;
        var source = _adapter.OwnerOf(attacker);
        if (source == null)
        {
            Say("weapon.melee-hit-owner-unresolved: a remote swing whose attacker has no current player reference published nothing.");
            return;
        }
        var target = _kernel.ResolveEntityInstance(EquipmentNativeAdapter.EnemyKind, enemy);
        if (target == null)
        {
            Say("weapon.melee-hit-target-unresolved: a remote swing hit an enemy life this build cannot name, so no fact was published.");
            return;
        }
        PublishResolved(source, target, limb, damage, null, "remote-receive");
    }

    /// <summary>Publishes one hit, once per target. Every port is required except the two the game really leaves
    /// open: an unresolvable attacker or target is not a hit this row can name and publishes nothing at all, while
    /// a target that is not a body part and a swing with no charge state leave their port out of the fact.</summary>
    private void Publish(PlayerAgent? attacker, GameObject? damageGO, float damage, bool? charged, string via)
    {
        if (!Authoritative()) return;
        var source = _adapter.OwnerOf(attacker);
        if (source == null)
        {
            Say("weapon.melee-hit-owner-unresolved: a " + via
                + " swing whose wielder has no current player reference published nothing.");
            return;
        }
        if (!float.IsFinite(damage) || damage < 0f)
        {
            Say("weapon.melee-hit-damage-refused: a " + via + " hit carried a non-finite damage value.");
            return;
        }
        var target = TargetOf(damageGO, out var limb);
        if (target == null)
        {
            Say("weapon.melee-hit-target-unresolved: a " + via
                + " swing hit something this build cannot name as an entity, so no fact was published.");
            return;
        }
        PublishResolved(source, target, limb, damage, charged, via);
    }

    /// <summary>Publishes one already-resolved hit, and the one place the two native sources meet: the ledger
    /// decides whether this observation is a hit nobody has published yet. A claim another source already holds for
    /// the same attacker, target and limb is the same hit seen twice, so it is reported and dropped.</summary>
    private void PublishResolved(EntityReference source, EntityReference target, int limb, float damage, bool? charged, string via)
    {
        var world = _kernel.WorldEpoch;
        if (!_ledger.TryClaim(source, target, limb, _kernel.CurrentTick, world))
        {
            _info("weapon.melee-hit-deduplicated via=" + via + " source=" + source.Id + " target=" + target.Id
                + " limb=" + (limb >= 0 ? Number(limb) : "none"));
            return;
        }
        // Nothing is listening on the melee-hit row: the kernel would answer `no-consumer` for the event this body
        // is about to build, so the event value is never built.
        if (_adapter.Unsubscribed(WeaponMeleeHitContract.MeleeHitBinding)) return;
        var outputs = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["source"] = source, ["target"] = target, ["damage"] = (double)damage
        };
        if (limb >= 0) outputs["limb"] = limb;
        if (charged.HasValue) outputs["charged"] = charged.Value;
        var id = "gtfo.weapon.melee:" + Number(world) + ":" + Number(checked(++_sequence));
        var result = _adapter.Publish(new RuntimeEvent(id, WeaponMeleeHitContract.MeleeHitBinding, world,
            Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + Number(world), RuntimeJson.From(outputs)));
        if (result.Status == "rejected") _report("weapon.melee-hit-rejected: " + result.Code + " via=" + via);
        else _info("weapon.melee-hit via=" + via + " source=" + source.Id + " target=" + target.Id
            + " limb=" + (limb >= 0 ? Number(limb) : "none")
            + " charged=" + (charged.HasValue ? (charged.Value ? "true" : "false") : "none")
            + " status=" + result.Status);
    }

    /// <summary>The entity a melee hit landed on, resolved the way this package resolves every hit: the damage
    /// limb the game handed the hit names its own base agent, that agent's type decides which domain is asked, and
    /// the limb index is the part's own `m_limbID`. A damageable this build cannot name — world geometry, a
    /// deployable, anything without an agent — answers null, and a body part that names no agent is refused rather
    /// than published against a guessed reference.</summary>
    private EntityReference? TargetOf(GameObject? damageGO, out int limb)
    {
        limb = -1;
        if (damageGO == null) return null;
        var part = damageGO.GetComponentInParent<Dam_EnemyDamageLimb>();
        if (part != null)
        {
            limb = part.m_limbID;
            if (part.GetBaseAgent()?.TryCast<EnemyAgent>() is { } enemy)
                return _kernel.ResolveEntityInstance(EquipmentNativeAdapter.EnemyKind, enemy);
            return null;
        }
        var playerPart = damageGO.GetComponentInParent<Dam_PlayerDamageLimb>();
        if (playerPart?.GetBaseAgent()?.TryCast<PlayerAgent>() is { } player) return _adapter.OwnerOf(player);
        return null;
    }

    /// <summary>The charge a swing carries, read from the weapon's own state. The game's set has exactly two
    /// charge-hit states and two plain hit states; every other member is a state this row does not describe, which
    /// is an absent port rather than a false one.</summary>
    private static bool? Charge(eMeleeWeaponState state) => state switch
    {
        eMeleeWeaponState.AttackChargeHitLeft or eMeleeWeaponState.AttackChargeHitRight => true,
        eMeleeWeaponState.AttackHitLeft or eMeleeWeaponState.AttackHitRight => false,
        _ => null
    };

    /// <summary>The host's own world, read on every use so a fact can never be stamped with an epoch the kernel
    /// has already left. The adapter's authority gate is the one every package fact shares; the sequence is this
    /// observer's own per-world counter and is reset with the world it belongs to.</summary>
    private bool Authoritative()
    {
        var world = _kernel.WorldEpoch;
        if (world != _world) { _world = world; _sequence = 0; }
        return _adapter.Authoritative();
    }

    private void Say(string message)
    {
        try { _report(message); } catch (Exception) { /* a reporter that fails must not stop observation. */ }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

// The first-person per-target hit entry: the local player's own swing, once per entry of `HitsForDamage`. The
// prefix reads only what the body was handed and what the weapon already fixed for this hit.
[HarmonyPatch(typeof(MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.DoAttackDamage))]
internal static class MeleeSwingDamage
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(MeleeWeaponFirstPerson __instance, MeleeWeaponDamageData data, bool isPush)
        => WeaponNativeSession.Current?.Guard(_ => WeaponMeleeHitFacts.Current?.SwingHit(__instance, data, isPush));
}

// The third-person per-target hit entry: a swing this machine runs for somebody else, which in this build is the
// bot melee action's strike state. The damage is the body's own argument, so no readback is involved.
[HarmonyPatch(typeof(MeleeWeaponThirdPerson), nameof(MeleeWeaponThirdPerson.DoAttackDamage))]
internal static class MeleeThirdPersonDamage
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(MeleeWeaponThirdPerson __instance, GameObject damageGO, float damage)
        => WeaponNativeSession.Current?.Guard(_ => WeaponMeleeHitFacts.Current?.ThirdPersonHit(__instance, damageGO, damage));
}
