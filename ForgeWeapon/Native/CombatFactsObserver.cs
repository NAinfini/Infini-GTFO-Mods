using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;
using Gear;
using HarmonyLib;
using Player;

namespace ForgeWeapon.Native;

/// <summary>
/// The weapon-state facts of this batch and the impact records the hit-context read answers from. Every body
/// here reads the state a native object really holds and publishes it where the state really changes; nothing
/// here writes, and every published reference is checked through the adapter before it leaves.
///
/// <b>Charge.</b> The two native charge families are read where each one runs. A melee charge is the
/// <c>MWS_ChargeUp</c> state object: `Enter` is the charge starting, `Update` is it running, `Exit` is it
/// ending, and `OnChargeupRelease` is the charged attack really being swung. A ranged charge is the archetype's
/// own update, whose <c>m_inChargeup</c> flag is the state and whose <c>m_chargeupTimer</c> against
/// <c>ChargeupDelay()</c> is the proportion. The proportion is only published while the native threshold is a
/// positive number this machine can read: a threshold of zero means "this weapon has no charge threshold", which
/// is not a ratio of one.
///
/// <b>Aim.</b> The holder's own <c>ItemAimTrigger</c> is the weapon state and not a key: a weapon whose sights
/// take time to come up, or one an EMP folded away, is entered and left by the state machine while the key that
/// asked for it stays held. Only the transition is published, so no plan ever sees a repeat of a state it
/// already knows.
///
/// <b>Shot resolution.</b> One `Fire` body is one shot: the prefix opens it, the shared hit routine counts what
/// it reached, and the postfix publishes the outcome — `hit` when at least one impact reached a damageable
/// target, `world` when the shot reached geometry only, and `miss` when the body resolved with no impact at all.
/// A miss therefore exists as a fact and can never be doubled by a later hit, which is exactly what a plan that
/// counts consecutive misses needs. The unit is the shot and not the pellet: a shotgun's several pellets are one
/// shot with a `hit_count`, because one native body produced them.
/// </summary>
internal sealed class CombatFactsObserver
{
    /// <summary>The live observer, or null before the session is built and after it is disposed.</summary>
    internal static CombatFactsObserver? Current { get; private set; }

    private readonly EquipmentNativeAdapter _adapter;
    private readonly WeaponCombatObserver _shots;
    private readonly Func<RuntimeKernel?> _kernel;
    private readonly Func<Item?, EntityReference?> _equipmentOf;
    private readonly Func<PlayerAgent?, EntityReference?> _ownerOf;
    private readonly Action<string> _report, _info;
    private readonly Dictionary<IntPtr, MeleeCharge> _melee = new();
    private readonly Dictionary<IntPtr, bool> _aiming = new();
    private long _world = -1, _sequence;
    private bool _disposed;

    private sealed record MeleeCharge(MeleeWeaponFirstPerson Weapon, bool Swung);

    internal CombatFactsObserver(EquipmentNativeAdapter adapter, WeaponCombatObserver shots, Func<RuntimeKernel?> kernel,
        Func<Item?, EntityReference?> equipmentOf, Func<PlayerAgent?, EntityReference?> ownerOf,
        Action<string> report, Action<string> info)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _shots = shots ?? throw new ArgumentNullException(nameof(shots));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _equipmentOf = equipmentOf ?? throw new ArgumentNullException(nameof(equipmentOf));
        _ownerOf = ownerOf ?? throw new ArgumentNullException(nameof(ownerOf));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>Installs the observer. Called by the session before its hooks are patched, so no hook can run
    /// over a half-built observer.</summary>
    internal static CombatFactsObserver Attach(EquipmentNativeAdapter adapter, WeaponCombatObserver shots,
        Func<RuntimeKernel?> kernel, Func<Item?, EntityReference?> equipmentOf,
        Func<PlayerAgent?, EntityReference?> ownerOf, Action<string> report, Action<string> info)
    {
        if (Current != null) throw new InvalidOperationException("Combat facts are single-instance.");
        var facts = new CombatFactsObserver(adapter, shots, kernel, equipmentOf, ownerOf, report, info);
        Current = facts;
        return facts;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private void Clear()
    {
        _melee.Clear(); _aiming.Clear(); _sequence = 0;
    }

    // -------------------------------------------------------------------------------------------------------
    // Charge: the melee state object.
    // -------------------------------------------------------------------------------------------------------

    internal void MeleeEntered(MWS_ChargeUp charge)
    {
        if (!Ready() || charge == null) return;
        var weapon = charge.m_weapon;
        if (weapon == null) return;
        _melee[charge.Pointer] = new MeleeCharge(weapon, false);
        PublishCharge(weapon, WeaponStateTriggerContract.ChargeStart, null);
    }

    internal void MeleeRan(MWS_ChargeUp charge)
    {
        if (!Ready() || charge == null) return;
        if (!_melee.TryGetValue(charge.Pointer, out var state)) return;
        PublishCharge(state.Weapon, WeaponStateTriggerContract.ChargeProgress, MeleeRatio(charge));
    }

    /// <summary>The charge ended. A charge whose release the game announced is the charged attack being swung;
    /// one that ends without it is the charge being abandoned, which is the path the weapon's own
    /// `OnMeleeChargeCancel` names.</summary>
    internal void MeleeEnded(MWS_ChargeUp charge)
    {
        if (!Ready() || charge == null) return;
        if (!_melee.Remove(charge.Pointer, out var state)) return;
        var ratio = MeleeRatio(charge);
        PublishCharge(state.Weapon, state.Swung ? WeaponStateTriggerContract.ChargeSwing : WeaponStateTriggerContract.ChargeEnd, ratio);
    }

    /// <summary>The charged attack was released. The release is remembered on the charge it belongs to rather
    /// than published here, because the state object's own `Exit` is what ends the charge and the two events must
    /// not both be reported as its ending.</summary>
    internal void MeleeReleased(MWS_ChargeUp charge)
    {
        if (!Ready() || charge == null) return;
        if (_melee.TryGetValue(charge.Pointer, out var state)) _melee[charge.Pointer] = state with { Swung = true };
    }

    private static double? MeleeRatio(MWS_ChargeUp charge)
    {
        var full = charge.m_maxDamageTime;
        if (!float.IsFinite(full) || full <= 0f) return null;
        var elapsed = charge.m_elapsed;
        if (!float.IsFinite(elapsed) || elapsed < 0f) return null;
        return Math.Clamp(elapsed / full, 0d, 1d);
    }

    // -------------------------------------------------------------------------------------------------------
    // Charge: the ranged archetype.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>The flag before one archetype update, so a charge that starts and ends inside that update is
    /// still seen as both an entry and an ending.</summary>
    internal static bool RangedCharging(BulletWeaponArchetype archetype)
        => archetype != null && archetype.m_inChargeup;

    internal void RangedRan(BulletWeaponArchetype archetype, bool before)
    {
        if (!Ready() || archetype == null) return;
        var now = archetype.m_inChargeup;
        var weapon = archetype.m_weapon;
        if (weapon == null) return;
        var ratio = RangedRatio(archetype);
        if (now && !before) PublishCharge(weapon, WeaponStateTriggerContract.ChargeStart, ratio);
        else if (now) PublishCharge(weapon, WeaponStateTriggerContract.ChargeProgress, ratio);
        else if (before) PublishCharge(weapon, WeaponStateTriggerContract.ChargeEnd, ratio);
    }

    private static double? RangedRatio(BulletWeaponArchetype archetype)
    {
        var full = Safe(() => archetype.ChargeupDelay(), 0f);
        var elapsed = archetype.m_chargeupTimer;
        if (!float.IsFinite(full) || full <= 0f || !float.IsFinite(elapsed) || elapsed < 0f) return null;
        return Math.Clamp(elapsed / full, 0d, 1d);
    }

    // -------------------------------------------------------------------------------------------------------
    // Aim.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One holder update, with the sights' state read from the holder's own trigger. The wielded item is what
    /// the fact is about; a holder with nothing in hand, or one whose item this machine has no current equipment
    /// reference for, publishes nothing — an aim fact without its weapon names no equipment to act on.
    /// </summary>
    internal void AimSampled(FirstPersonItemHolder holder)
    {
        if (!Ready() || holder == null) return;
        var item = holder.WieldedItem;
        if (!_aiming.TryGetValue(holder.Pointer, out var was)) _aiming[holder.Pointer] = false;
        var aiming = item != null && Safe(() => holder.ItemAimTrigger, false);
        if (aiming == was) return;
        _aiming[holder.Pointer] = aiming;
        if (item == null) return;
        var equipment = _equipmentOf(item);
        if (equipment == null) return;
        Publish(WeaponStateTriggerContract.AimStateBinding, equipment, RuntimeJson.From(new
        {
            equipment,
            actor = (EntityReference?)_ownerOf(Safe(() => holder.m_owner, null)),
            phase = aiming ? WeaponStateTriggerContract.AimEnter : WeaponStateTriggerContract.AimExit
        }));
    }

    // -------------------------------------------------------------------------------------------------------
    // Shot resolution.
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One shot's own ending: what the shot reached, read from the impacts the shared hit routine counted while
    /// the body was running. An impact the hit routine could not name a damageable target for is a world hit, so
    /// the outcome is decided by whether any impact reached a target rather than by whether one happened at all.
    /// </summary>
    internal void ShotResolved(WeaponCombatObserver.Shot shot)
    {
        if (!Ready()) return;
        var tally = _shots.HitsOf(shot);
        var hits = tally.Targets + tally.Worlds;
        var outcome = tally.Targets > 0 ? CombatPrimitiveContract.Hits
            : tally.Worlds > 0 ? CombatPrimitiveContract.World : CombatPrimitiveContract.Misses;
        Publish(CombatPrimitiveContract.ShotResolvedBinding, shot.Equipment, RuntimeJson.From(new
        {
            source = (EntityReference?)_shots.Owner(shot),
            equipment = shot.Equipment,
            outcome,
            target = (EntityReference?)tally.Target,
            position = hits > 0 ? (double[]?)tally.Position : null,
            normal = hits > 0 ? (double[]?)tally.Normal : null,
            hit_count = hits
        }));
    }

    // -------------------------------------------------------------------------------------------------------
    // Shared plumbing.
    // -------------------------------------------------------------------------------------------------------

    private void PublishCharge(MeleeWeaponFirstPerson weapon, string phase, double? ratio)
        => PublishCharge((Item)weapon, phase, ratio);

    private void PublishCharge(BulletWeapon weapon, string phase, double? ratio)
        => PublishCharge((Item)weapon, phase, ratio);

    private void PublishCharge(Item weapon, string phase, double? ratio)
    {
        var equipment = _equipmentOf(weapon);
        if (equipment == null) return;
        Publish(WeaponStateTriggerContract.ChargeStateBinding, equipment, RuntimeJson.From(new { equipment, phase, ratio }));
    }

    /// <summary>
    /// One fact, published only when a consumer is really listening on that row: the kernel answers `no-consumer`
    /// for a fact nobody subscribes to, and the native read that built its value is skipped rather than thrown
    /// away. A rejection is reported and never swallowed, because a payload this provider cannot validate is a
    /// defect here rather than a game condition.
    /// </summary>
    private void Publish(string binding, EntityReference subject, JsonElement outputs)
    {
        if (subject == null) return;
        var kernel = _kernel();
        if (kernel == null || kernel.StartupState != RuntimeStartupState.Ready) return;
        if (_adapter.Unsubscribed(binding)) return;
        var value = new RuntimeEvent(NextEventId(kernel, binding), binding, kernel.WorldEpoch,
            Math.Max(0, kernel.CurrentTick), subject.Id, outputs);
        var result = _adapter.Publish(value);
        if (result.Status == "rejected") _report("weapon.fact-rejected " + binding + ": " + result.Code);
        else _info("weapon.fact " + binding + " subject=" + subject.Id + " status=" + result.Status);
    }

    private string NextEventId(RuntimeKernel kernel, string binding)
        => "gtfo.weapon." + binding + ":" + Number(kernel.WorldEpoch) + ":" + Number(checked(++_sequence));

    /// <summary>The gate every body above starts with: this session's authority (the host's own advance and the
    /// gameplay gate it was started with) and a kernel that is really running. The tables are dropped when the
    /// world changes, because every one of them is keyed by native pointers of objects in one world.</summary>
    private bool Ready()
    {
        if (_disposed) return false;
        var kernel = _kernel();
        if (kernel == null || kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (kernel.WorldEpoch != _world) { _world = kernel.WorldEpoch; Clear(); }
        return _adapter.Authoritative();
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
