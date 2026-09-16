using System;
using ForgeRuntime.Framework;
using Gear;

namespace ForgeWeapon.Native;

/// <summary>Host-side weapon combat facts. One shot is counted per `Fire` body, and the native hits that shot
/// causes are correlated to it by the open shot this observer keeps; nothing here applies damage or reads a
/// target's health, and every published reference is checked through Runtime before it leaves.</summary>
internal sealed class WeaponCombatObserver
{
    private readonly EquipmentNativeAdapter _adapter;
    private readonly Func<RuntimeKernel?> _kernel;
    private readonly Action<string> _report, _info;
    private Shot? _open;
    private long _sequence;

    /// <summary>The shot a native hit belongs to: the firing equipment life and the 1-based number of the shot
    /// inside that life. A shot is opened by its `Fire` and stays open until the next `Fire`, because a shotgun
    /// fires its several pellets from one body and the game resolves those hits after that body has returned.</summary>
    internal sealed record Shot(EntityReference Equipment, long Index);

    internal WeaponCombatObserver(EquipmentNativeAdapter adapter, Func<RuntimeKernel?> kernel,
        Action<string> report, Action<string> info)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>A hit with no open shot is treated as unobservable rather than as a shot of its own: a deployable's
    /// own firing reaches the same bullet routine without any weapon `Fire`, so it must not be billed to a player.</summary>
    internal void Hit(Weapon.WeaponHitData? data)
    {
        var kernel = Ready();
        if (kernel == null || data == null) return;
        var shot = _open;
        if (shot == null)
        {
            _info("weapon.hit-outside-shot: a native hit had no open weapon shot; no candidate published.");
            return;
        }
        var owner = _adapter.OwnerOf(data.owner);
        if (owner == null)
        {
            _info("weapon.hit-owner-unresolved: equipment=" + shot.Equipment.Id + " index=" + Number(shot.Index)
                + "; no candidate published.");
            return;
        }
        // The ray hit is a by-value struct on the data, so a miss or a non-collider hit reads as a zero point
        // rather than throwing. The target is resolved from the hit object's own native type through its owning
        // domain; when that fails — world geometry, a deployable, a kind no installed package resolves — the port
        // is left out of the fact rather than published as null. The equipment comes from the open shot itself and
        // never from the hit object: this shot is what the candidate belongs to, so a `gear-block` mount can claim
        // it, and it is always present because a candidate is only ever published inside a firing window. The limb
        // stays null: the candidate exists before any damage is worked out, so no limb index has been decided yet.
        var point = data.rayHit.point;
        var position = new[] { (double)point.x, (double)point.y, (double)point.z };
        if (!Finite(position)) return;
        // Nothing is listening on the hit-candidate row: the kernel would answer `no-consumer` for the event this
        // body is about to build, so the native read and the event value are both skipped.
        if (_adapter.Unsubscribed(ModuleDefinition.HitCandidateBinding)) return;
        var target = _adapter.HitTarget(data);
        object outputs = target == null
            ? new { source = (EntityReference?)owner, equipment = shot.Equipment, limb = (int?)null, position }
            : new { source = (EntityReference?)owner, equipment = shot.Equipment, target, limb = (int?)null, position };
        var result = _adapter.Publish(new RuntimeEvent(
            "gtfo.weapon.hit:" + Number(kernel.WorldEpoch) + ":" + Number(checked(++_sequence)),
            ModuleDefinition.HitCandidateBinding, kernel.WorldEpoch, Math.Max(0, kernel.CurrentTick),
            "gtfo.weapon.shot:" + shot.Equipment.Id + ":" + Number(shot.Index),
            RuntimeJson.From(outputs)));
        // The target being unnamed is a normal outcome, so it is logged as data; a rejected candidate is a defect
        // in this provider's own payload and must be as visible as a rejected shot fact.
        if (result.Status == "rejected") _report("weapon.hit-fact-rejected: " + result.Code);
        else _info("weapon.hit-fact equipment=" + shot.Equipment.Id + " index=" + Number(shot.Index)
            + " target=" + (target == null ? "unresolved" : target.Id) + " status=" + result.Status + " code=" + result.Code);
    }

    /// <summary>Counts one shot of a weapon and opens it. Null when the weapon is not a recorded equipment life,
    /// the item is authority-less or no owner reference is current — a shot without its actor is not a fact. The
    /// shot this replaces is no longer open, so a hit arriving after a later shot was fired belongs to that later
    /// shot; at most one shot is ever open, which is what keeps one hit from being billed twice.</summary>
    internal Shot? Open(Weapon? weapon)
    {
        var kernel = Ready();
        if (kernel == null || weapon == null) return null;
        var recorded = _adapter.RecordShot(weapon);
        if (recorded == null) return null;
        var owner = _adapter.OwnerOf(weapon);
        if (owner == null)
        {
            _info("weapon.shot-owner-unresolved: equipment=" + recorded.Value.Equipment.Id + "; no shot fact published.");
            return null;
        }
        var shot = new Shot(recorded.Value.Equipment, recorded.Value.Index);
        _open = shot;
        // Nothing is listening on the shot row: the event value is never built, and the shot stays open because
        // the window is this observer's own state rather than the fact's.
        if (_adapter.Unsubscribed(ModuleDefinition.ShotCommittedBinding)) return shot;
        var result = _adapter.Publish(new RuntimeEvent(
            "gtfo.weapon.shot:" + Number(kernel.WorldEpoch) + ":" + Number(checked(++_sequence)),
            ModuleDefinition.ShotCommittedBinding, kernel.WorldEpoch, Math.Max(0, kernel.CurrentTick),
            "gtfo.weapon.equipment:" + recorded.Value.Equipment.Id,
            RuntimeJson.From(new { source = (EntityReference?)owner, equipment = recorded.Value.Equipment,
                index = recorded.Value.Index })));
        if (result.Status == "rejected") _report("weapon.shot-fact-rejected: " + result.Code);
        else _info("weapon.shot-fact equipment=" + recorded.Value.Equipment.Id + " index=" + Number(recorded.Value.Index)
            + " status=" + result.Status + " code=" + result.Code);
        return shot;
    }

    private RuntimeKernel? Ready()
    {
        if (!_adapter.Authoritative()) return null;
        var kernel = _kernel();
        return kernel == null || kernel.StartupState != RuntimeStartupState.Ready ? null : kernel;
    }

    private static bool Finite(double[] position)
        => double.IsFinite(position[0]) && double.IsFinite(position[1]) && double.IsFinite(position[2]);
    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
