using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;
using Gear;

namespace ForgeWeapon.Native;

/// <summary>The native readback an attack scope needs and nothing more: whether this machine may publish at all,
/// the equipment life a weapon belongs to, that life's own shot counter, and how many hit candidates have been
/// published for it. <see cref="EquipmentNativeAdapter"/> is the one implementation in the package; the interface
/// exists so a focused test can drive the scope's decisions without the game's object graph behind them, and it
/// carries no member the adapter did not already answer for itself.</summary>
internal interface IAttackNativeReads
{
    /// <summary>This machine is the host and the host's runtime is ready: the gate every fact here shares.</summary>
    bool Authoritative();

    /// <summary>The equipment life a weapon belongs to, the player reference its owner resolves to, and the
    /// life's shot counter as it stands right now. Null for a weapon this build has not recorded or whose owner
    /// has no current reference.</summary>
    (EntityReference Source, EntityReference Equipment, long Shots)? AttackTarget(Item? weapon);

    /// <summary>How many hit candidates have been published for one equipment life.</summary>
    long HitCount(EntityReference equipment);

    /// <summary>The one publication path, owned by the adapter.</summary>
    DispatchResult Publish(RuntimeEvent value);

    /// <summary>Whether no loaded plan is mounted on one of this provider's own bindings, read before an event
    /// value is built so a fact nobody subscribes to is never constructed.</summary>
    bool Unsubscribed(string binding);
}

/// <summary>Host-side attack-instance facts. One attack is the lifecycle of one native trigger pull: it opens
/// before the weapon's own body decides anything, is accepted when that body registers the shot, and closes when
/// the body returns — which for a ranged weapon is the whole `Fire` body, so a shotgun's pellets are already
/// resolved, and for melee is `OnAttackHitDone`, the end of the swing. A miss is the same scope closing with no
/// hit candidate published inside it.
///
/// This type never counts a shot, never applies damage and never reads health. The accepted signal is the
/// equipment life's own shot counter, the one <see cref="EquipmentNativeAdapter.RecordShot"/> increments and
/// <see cref="WeaponCombatObserver"/> publishes `shot_committed` from, read here before and after the native
/// body; the hit signal is the same life's `hit_candidate` counter. Neither is a Forge tally of an attack: the
/// scope id this module mints is its own sequence per equipment life, so one attack stays one scope even when a
/// burst registers several shots inside it.</summary>
internal sealed class AttackInstanceModule
{
    /// <summary>Which native body opened the attack scope. Melee has no `Fire` and its swing is only finished by
    /// `OnAttackHitDone`, so the two paths reach <see cref="Request"/> and <see cref="Complete"/> from different
    /// hooks; the mode is what a diagnostic names when a scope is dropped, and it is the only reason the two
    /// entry points are distinguishable at all.</summary>
    internal enum AttackMode { Ranged, Melee }

    /// <summary>One equipment life's attack bookkeeping: the scope number this module mints and the one open
    /// burst sequence. Keyed by the equipment entity's own id, so a handle rebuilt by a backpack readback keeps
    /// its sequence.</summary>
    private sealed class Life
    {
        internal long Attacks;
        internal bool BurstOpen;
        internal long Bursts;
    }

    /// <summary>One attack between its request and its close. The source and equipment are the native reads
    /// taken at the request: a close never re-derives them, so a fact always names the life that started it.</summary>
    private sealed class Attack
    {
        internal AttackMode Mode;
        internal EntityReference Source = null!, Equipment = null!;
        internal long ShotsBefore;
        internal long HitsAtOpen;
        internal string Scope = "";
    }

    private readonly IAttackNativeReads _adapter;
    private readonly Func<RuntimeKernel?> _kernel;
    private readonly Action<string> _report, _info;
    private readonly Dictionary<string, Life> _lives = new(StringComparer.Ordinal);
    private Attack? _open;
    private long _world = -1, _sequence;

    internal AttackInstanceModule(IAttackNativeReads adapter, Func<RuntimeKernel?> kernel,
        Action<string> report, Action<string> info)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>One attack is asked for: the request fact is published and the scope opens. Called from the
    /// `Fire` prefix and from the melee `DoTriggerAttack` prefix. Returns without a fact when the weapon is not
    /// a recorded equipment life, has no current owner reference, or this machine is not the host — the same
    /// gate the shot fact uses, because an attack without its actor is not a fact.
    ///
    /// A second request while one is open closes the first as a miss and reports it: a nested request would
    /// otherwise lose the outer scope's hits. That cannot happen through the hook set this build installs — no
    /// `Fire` body reaches another body, and melee's attack entry is reached from the input path and never from
    /// `OnAttackHitDone` — so the branch exists to make a lost scope impossible rather than to describe a case
    /// the game produces.</summary>
    internal void Request(Item? weapon, AttackMode mode)
    {
        var kernel = Ready();
        if (kernel == null || weapon == null) return;
        var recorded = _adapter.AttackTarget(weapon);
        if (recorded == null)
        {
            _info("weapon.attack-untracked: no fact published for an attack on an equipment life this build has not recorded.");
            return;
        }
        if (_open != null)
        {
            _report("weapon.attack-reentered: an attack was still open on " + _open.Equipment.Id
                + "; it is closed before the new one opens.");
            Close(kernel, _open);
        }
        var life = LifeOf(recorded.Value.Equipment);
        var attack = new Attack
        {
            Mode = mode,
            Source = recorded.Value.Source,
            Equipment = recorded.Value.Equipment,
            ShotsBefore = recorded.Value.Shots,
            HitsAtOpen = _adapter.HitCount(recorded.Value.Equipment),
            Scope = "gtfo.weapon.attack:" + recorded.Value.Equipment.Id + ":" + Number(life.Attacks + 1)
        };
        var published = Publish(kernel, AttackInstanceContract.AttackRequestedBinding, attack.Scope,
            new { source = (EntityReference?)attack.Source, equipment = (EntityReference?)attack.Equipment,
                phase = CommandPhase });
        // The scope opens only when its own request fact was accepted: a scope whose request nobody was told
        // about must not later publish a completion or a miss about it.
        if (published.Status == "rejected")
        {
            _report("weapon.attack-requested-rejected: " + published.Code);
            return;
        }
        _open = attack;
        life.Attacks++;
        _info("weapon.attack-requested scope=" + attack.Scope + " source=" + attack.Source.Id + " mode=" + mode
            + " status=" + published.Status);
    }

    /// <summary>The native body returned: the attack is closed. Called from the `Fire` postfix and from the
    /// melee `OnAttackHitDone` postfix. The accepted fact is published only when the equipment life's own shot
    /// counter moved inside the body — that counter is written by `PlayerSync.RegisterFiredBullets`, whose only
    /// two callers in this build are the two unsynced `Fire` bodies — so a pull the weapon refused (empty clip,
    /// cooldown, a reload in progress) leaves it where it was and no accepted fact is published.</summary>
    internal void Complete(Item? weapon)
    {
        var kernel = Ready();
        if (kernel == null || weapon == null) return;
        var attack = _open;
        if (attack == null)
        {
            _info("weapon.attack-close-without-open: a native body returned with no open attack scope; nothing published.");
            return;
        }
        var recorded = _adapter.AttackTarget(weapon);
        if (recorded == null || recorded.Value.Equipment != attack.Equipment)
        {
            // The equipment life this attack belongs to is gone, so the attack can no longer be attributed to a
            // life a plan could act on. The scope is dropped without a fact rather than closed against a
            // different life's counters.
            _report("weapon.attack-equipment-lost scope=" + attack.Scope + " mode=" + attack.Mode
                + "; the attack is dropped without completion or miss.");
            _open = null;
            return;
        }
        if (recorded.Value.Shots != attack.ShotsBefore)
            Publish(kernel, AttackInstanceContract.AttackAcceptedBinding, attack.Scope,
                new { source = (EntityReference?)attack.Source, equipment = (EntityReference?)attack.Equipment });
        Close(kernel, attack);
    }

    /// <summary>The weapon ran out of ammunition: `BWA_Burst.OnFireShotEmptyClip` and
    /// `BWA_Auto.OnFireShotEmptyClip` are the game's own empty-clip paths, so the fact is published from the
    /// game's own decision and never from comparing a clip readback against zero. The scope is the current
    /// attack's when one is open; a dry pull that opened no scope still carries the equipment's own sequence,
    /// because the row's two ports are the actor and the equipment and it has no scope port.</summary>
    internal void DryFire(BulletWeapon? weapon)
    {
        var kernel = Ready();
        if (kernel == null || weapon == null) return;
        var recorded = _adapter.AttackTarget(weapon);
        if (recorded == null)
        {
            _info("weapon.dry-fire-untracked: no fact published for an empty clip on an equipment life this build has not recorded.");
            return;
        }
        var life = LifeOf(recorded.Value.Equipment);
        var scope = _open != null && _open.Equipment == recorded.Value.Equipment
            ? _open.Scope
            : "gtfo.weapon.attack:" + recorded.Value.Equipment.Id + ":" + Number(life.Attacks);
        var published = Publish(kernel, AttackInstanceContract.DryFireBinding, scope,
            new { actor = (EntityReference?)recorded.Value.Source, equipment = (EntityReference?)recorded.Value.Equipment });
        if (published.Status == "rejected") _report("weapon.dry-fire-rejected: " + published.Code);
        else _info("weapon.dry-fire equipment=" + recorded.Value.Equipment.Id + " status=" + published.Status);
    }

    /// <summary>A burst sequence opened or closed, counted by the weapon's own `m_burstMax`. `BWA_Burst` and
    /// `BWA_Auto` declare both ends of the sequence, so the count is the weapon's own burst length and never a
    /// Forge tally. A close with no open sequence, or a second open with one already open, publishes nothing:
    /// the game reports a sequence, and this module does not invent the missing end of one.</summary>
    internal void Burst(BulletWeapon? weapon, bool started)
    {
        var kernel = Ready();
        if (kernel == null || weapon == null) return;
        var recorded = _adapter.AttackTarget(weapon);
        if (recorded == null) return;
        var life = LifeOf(recorded.Value.Equipment);
        if (started == life.BurstOpen) return;
        life.BurstOpen = started;
        var count = weapon.m_burstMax;
        if (count < 0) count = 0;
        var binding = started ? AttackInstanceContract.BurstStartedBinding : AttackInstanceContract.BurstEndedBinding;
        var published = Publish(kernel, binding,
            "gtfo.weapon.burst:" + recorded.Value.Equipment.Id + ":" + Number(++life.Bursts),
            new { source = (EntityReference?)recorded.Value.Source, equipment = (EntityReference?)recorded.Value.Equipment,
                count });
        if (published.Status == "rejected") _report("weapon.burst-fact-rejected: " + published.Code);
        else _info("weapon." + (started ? "burst-started" : "burst-ended") + " equipment=" + recorded.Value.Equipment.Id
            + " count=" + Number(count) + " status=" + published.Status);
    }

    /// <summary>Closes one scope: the completion fact, then the miss when the scope saw no hit candidate at
    /// all. The hit question is answered by the equipment life's own `hit_candidate` count, which the hit
    /// observer increments on the same thread inside the native body this close follows, so the number is
    /// already final here.</summary>
    private void Close(RuntimeKernel kernel, Attack attack)
    {
        _open = null;
        Publish(kernel, AttackInstanceContract.AttackCompletedBinding, attack.Scope,
            new { source = (EntityReference?)attack.Source, equipment = (EntityReference?)attack.Equipment });
        if (_adapter.HitCount(attack.Equipment) != attack.HitsAtOpen) return;
        Publish(kernel, AttackInstanceContract.AttackMissedBinding, attack.Scope,
            new { source = (EntityReference?)attack.Source, equipment = (EntityReference?)attack.Equipment });
    }

    private Life LifeOf(EntityReference equipment)
    {
        if (!_lives.TryGetValue(equipment.Id, out var life))
        {
            life = new Life();
            _lives[equipment.Id] = life;
        }
        return life;
    }

    /// <summary>Publishes one fact under this provider's own registration, the same path the equipment and shot
    /// facts use; Runtime owns ordering, dedupe and causality. The fact id is this module's own sequence, so two
    /// facts of one attack are two ids.</summary>
    private DispatchResult Publish(RuntimeKernel kernel, string bindingId, string scope, object outputs)
    {
        var id = "gtfo.weapon.attack-fact:" + Number(kernel.WorldEpoch) + ":" + Number(checked(++_sequence));
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this call
        // is about to build, so the event value is never built and the answer is the kernel's own word for it.
        if (_adapter.Unsubscribed(bindingId)) return new DispatchResult("ignored", "no-consumer", id);
        return _adapter.Publish(new RuntimeEvent(id, bindingId, kernel.WorldEpoch, Math.Max(0, kernel.CurrentTick),
            scope, RuntimeJson.From(outputs)));
    }

    /// <summary>The gate every fact here shares: the host's own ready kernel, and a world whose counters and
    /// scopes are this module's. A world change drops the open scope and every life's sequence without a fact,
    /// because a completion in the new world would attribute an attack to a world it never ran in.</summary>
    private RuntimeKernel? Ready()
    {
        var kernel = _kernel();
        if (kernel == null || kernel.StartupState != RuntimeStartupState.Ready) return null;
        if (kernel.WorldEpoch != _world)
        {
            _open = null;
            _lives.Clear();
            _world = kernel.WorldEpoch;
        }
        return _adapter.Authoritative() ? kernel : null;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The catalog's `command_phase` member this row's `phase` port carries. The request is the only
    /// point this row exists at, so the value is fixed; it is a constant so a scheme change is a compile-time
    /// edit and not a string buried in a payload.</summary>
    private const string CommandPhase = "requested";
}
