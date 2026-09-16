using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;
using Gear;

namespace ForgeWeapon.Native;

/// <summary>The native readback an attack-instance fact needs and nothing more: whether this machine may publish
/// at all, the equipment life a weapon belongs to, and the player reference its owner resolves to.
/// <see cref="EquipmentNativeAdapter"/> is the one implementation in the package; the interface exists so a
/// focused test can drive the module's decisions without the game's object graph behind them, and it carries no
/// member the adapter did not already answer for itself.</summary>
internal interface IAttackNativeReads
{
    /// <summary>This machine is the host and the host's runtime is ready: the gate every fact here shares.</summary>
    bool Authoritative();

    /// <summary>The equipment life a weapon belongs to and the player reference its owner resolves to. Null for a
    /// weapon this build has not recorded or whose owner has no current reference.</summary>
    (EntityReference Source, EntityReference Equipment)? AttackTarget(Item? weapon);

    /// <summary>The one publication path, owned by the adapter.</summary>
    DispatchResult Publish(RuntimeEvent value);

    /// <summary>Whether no loaded plan is mounted on one of this provider's own bindings, read before an event
    /// value is built so a fact nobody subscribes to is never constructed.</summary>
    bool Unsubscribed(string binding);
}

/// <summary>Host-side attack-instance facts: the two ends of a burst sequence and the game's own empty-clip
/// decision. Neither is a Forge tally of a firing: the sequence ends are the weapon archetype's own
/// `OnStartFiring` / `OnStopFiring` and the count is the weapon's own `m_burstMax`, and the empty-clip fact is
/// the game's own refusal path — never a comparison of a clip readback against zero.
///
/// This type never counts a shot, never applies damage and never reads health. Every fact names the equipment
/// life the adapter recorded and the player reference its owner resolves to. A world change drops the per-life
/// bookkeeping without a fact, because a sequence reported in the new world would be attributed to a world it
/// never ran in.</summary>
internal sealed class AttackInstanceModule
{
    /// <summary>One equipment life's attack bookkeeping: the one open burst sequence, its own sequence number and
    /// the dry-fire sequence. Keyed by the equipment entity's own id, so a handle rebuilt by a backpack readback
    /// keeps its sequences.</summary>
    private sealed class Life
    {
        internal bool BurstOpen;
        internal long Bursts;
        internal long DryFires;
    }

    private readonly IAttackNativeReads _adapter;
    private readonly Func<RuntimeKernel?> _kernel;
    private readonly Action<string> _report, _info;
    private readonly Dictionary<string, Life> _lives = new(StringComparer.Ordinal);
    private long _world = -1, _sequence;

    internal AttackInstanceModule(IAttackNativeReads adapter, Func<RuntimeKernel?> kernel,
        Action<string> report, Action<string> info)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>The weapon ran out of ammunition: `BWA_Burst.OnFireShotEmptyClip` and
    /// `BWA_Auto.OnFireShotEmptyClip` are the game's own empty-clip paths, so the fact is published from the
    /// game's own decision and never from comparing a clip readback against zero. The row's two ports are the
    /// actor and the equipment and it has no scope port, so the fact carries the equipment life's own dry-fire
    /// sequence: one sequence per equipment life, the same identity scheme the burst ends use.</summary>
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
        var published = Publish(kernel, AttackInstanceContract.DryFireBinding,
            "gtfo.weapon.dry-fire:" + recorded.Value.Equipment.Id + ":" + Number(++life.DryFires),
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
    /// facts of one life are two ids.</summary>
    private DispatchResult Publish(RuntimeKernel kernel, string bindingId, string scope, object outputs)
    {
        var id = "gtfo.weapon.attack-fact:" + Number(kernel.WorldEpoch) + ":" + Number(checked(++_sequence));
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this call
        // is about to build, so the event value is never built and the answer is the kernel's own word for it.
        if (_adapter.Unsubscribed(bindingId)) return new DispatchResult("ignored", "no-consumer", id);
        return _adapter.Publish(new RuntimeEvent(id, bindingId, kernel.WorldEpoch, Math.Max(0, kernel.CurrentTick),
            scope, RuntimeJson.From(outputs)));
    }

    /// <summary>The gate every fact here shares: the host's own ready kernel, and a world whose sequences are this
    /// module's. A world change drops every life's sequences without a fact, because an end reported in the new
    /// world would close a sequence that belonged to another one.</summary>
    private RuntimeKernel? Ready()
    {
        var kernel = _kernel();
        if (kernel == null || kernel.StartupState != RuntimeStartupState.Ready) return null;
        if (kernel.WorldEpoch != _world)
        {
            _lives.Clear();
            _world = kernel.WorldEpoch;
        }
        return _adapter.Authoritative() ? kernel : null;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
