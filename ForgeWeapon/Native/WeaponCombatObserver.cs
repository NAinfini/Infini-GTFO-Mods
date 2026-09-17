using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;
using Gear;

namespace ForgeWeapon.Native;

/// <summary>Host-side weapon combat facts. One shot is counted per `Fire` body, and the native hits that shot
/// causes are correlated to it by the open shot this observer keeps; nothing here applies damage or reads a
/// target's health, and every published reference is checked through Runtime before it leaves.
///
/// The observer is also where one shot's own tally lives, because the shot is the unit both rows are about: a
/// `hit_candidate` is billed to the shot that is open, and the `shot_resolved` outcome is the same tally read
/// once the firing body has returned. A shot's own context — where it ended, how far it travelled, which part it
/// reached and whether it came through geometry — is kept beside the tally so the resolution and the read that
/// follows it describe the same impact.</summary>
internal sealed class WeaponCombatObserver
{
    private readonly EquipmentNativeAdapter _adapter;
    private readonly Func<RuntimeKernel?> _kernel;
    private readonly Action<string> _report, _info;
    private Shot? _open;
    private Shot? _firing;
    private Tally _tally;
    private long _sequence;

    /// <summary>The shot a native hit belongs to: the firing equipment life and the 1-based number of the shot
    /// inside that life. A shot is opened by its `Fire` and stays open until the next `Fire`, because a shotgun
    /// fires its several pellets from one body and the game resolves those hits after that body has returned.</summary>
    internal sealed record Shot(EntityReference Equipment, long Index);

    /// <summary>What one shot reached: how many impacts reached a damageable target, how many only reached world
    /// geometry, and the first impact's own reading. The first impact is the one a shot's outcome describes,
    /// because that is the impact the game itself worked the damage out from.</summary>
    internal sealed record Tally(int Targets, int Worlds, EntityReference? Target, double[] Position, double[] Normal);

    /// <summary>No impact at all yet: the reading a shot starts from.</summary>
    private static readonly Tally Empty = new(0, 0, null, Array.Empty<double>(), Array.Empty<double>());

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
        // The ray hit is a by-value struct on the data, so a miss or a non-collider hit reads as a zero point
        // rather than throwing. The target is resolved from the hit object's own native type through its owning
        // domain; when that fails — world geometry, a deployable, a kind no installed package resolves — the shot
        // is tallied as a world hit and the candidate leaves the target port out rather than publishing a null.
        // The equipment comes from the open shot itself and never from the hit object: this shot is what the
        // candidate belongs to, so a `gear-block` mount can claim it, and it is always present because a candidate
        // is only ever published inside a firing window. The limb stays null on the candidate: it exists before
        // any damage is worked out, so no limb index has been decided yet.
        var point = data.rayHit.point;
        var position = new[] { (double)point.x, (double)point.y, (double)point.z };
        if (!Finite(position)) return;
        var owner = _adapter.OwnerOf(data.owner);
        var target = _adapter.HitTarget(data);
        Trace(shot, data, position, target);
        // The candidate is only published when a consumer is listening; the tally above is this observer's own
        // state and is kept either way, because the shot's outcome is a fact about the shot and not about who
        // subscribed to the candidate row.
        if (_adapter.Unsubscribed(ModuleDefinition.HitCandidateBinding)) return;
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

    /// <summary>The head of one firing body, taken before it runs: the native hit routine has no context of its
    /// own, so the shot's tally starts here and the postfix reads it once the body is done. A body with no open
    /// shot (an item this machine does not answer for) clears the tally instead of carrying the previous shot's,
    /// which is what keeps an unobservable shot from being billed to the one before it.</summary>
    internal void BeginFire(Weapon? weapon)
    {
        _tally = Empty;
        _firing = weapon == null ? null : _open;
    }

    /// <summary>The tail of one firing body: the shot it produced, or null when this body's shot is not one this
    /// machine answers for. The shot stays open afterwards — a later hit belongs to the later shot, and the shot
    /// that just resolved keeps its own tally for the resolution.</summary>
    internal Shot? ResolveFire(Weapon? weapon)
    {
        var shot = _firing;
        _firing = null;
        if (shot == null || weapon == null) return null;
        if (_open is { } open && open != shot) return null;
        return shot;
    }

    /// <summary>What one resolved shot reached, for the shot-resolution fact.</summary>
    internal Tally HitsOf(Shot shot) => _open == shot ? _tally : Empty;

    /// <summary>The shooter of one resolved shot, re-derived from the shot's own equipment life.</summary>
    internal EntityReference? Owner(Shot shot)
    {
        var weapon = _adapter.WeaponOf(shot.Equipment);
        return weapon == null ? null : _adapter.OwnerOf(weapon);
    }

    /// <summary>
    /// One impact, added to the open shot's tally. The first impact of a shot is the one whose own reading is
    /// kept: it is the impact the game worked the damage out from, and a later pellet of the same shotgun blast
    /// does not describe a different shot.
    /// </summary>
    private void Trace(Shot shot, Weapon.WeaponHitData data, double[] position, EntityReference? target)
    {
        if (_open != shot) return;
        var ray = data.rayHit;
        var normal = new[] { (double)ray.normal.x, (double)ray.normal.y, (double)ray.normal.z };
        if (!Finite(normal)) normal = new[] { 0d, 0d, 0d };
        var first = _tally.Targets == 0 && _tally.Worlds == 0;
        _tally = target == null
            ? _tally with { Worlds = _tally.Worlds + 1 }
            : _tally with { Targets = _tally.Targets + 1 };
        if (!first) return;
        _tally = _tally with
        {
            Target = target,
            Position = position,
            Normal = normal
        };
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
        _tally = Empty;
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
