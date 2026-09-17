using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The three facts a received damage transaction settles, read on the one window that already spans it.
///
/// `forge.trigger.combat.killed` (`next / target / source? / damage_kind`) is published only when a committed
/// damage call took the enemy from alive to not alive: the catalog says "this damage killed the target", and
/// the enemy's own death flow is a different row (`forge.trigger.enemy.death_started`), which enters death
/// without necessarily settling as a kill. The window is `ProcessReceivedDamage`, the only funnel every hit
/// passes through, so a kill reached from an explosion, a melee swing or a Forge submission is the same
/// reading.
///
/// `forge.trigger.combat.limb_damaged` (`next / target / limb? / amount`) is the health the same call took and
/// the array position the receiver's own `limbID` argument named. The argument is a position into
/// `DamageLimbs`, not an `m_limbID`, so it is published as it arrives and never re-mapped — the reverse
/// mapping belongs to the writer that turns a declared limb id into an entry point argument.
///
/// `forge.trigger.combat.staggered` (`next / target / source?`) is the one fact of the three the receiver's own
/// arguments alone cannot settle: the hit names the reaction it is asking for, and the enemy's hitreact state
/// machine is asked afterwards what reaction it actually stands on. A hit the receiver refused, a hit that asked
/// for no reaction or for a kill, and a hit the receiver applied while its own immunity or retrigger rule left no
/// such reaction behind are all not staggers — the same standard the `stagger` action's submissions are held to.
///
/// Nothing here writes the world: every fact is a reading of a commit the game already made.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The two bindings this window's own observations publish through, in registration order. They are
    /// the same two ids <see cref="ForgeEnemy.AttackInstanceContract"/> declares for the registration rows, so the
    /// publication gate and the registry can never name different rows. The stagger the closing half of the same
    /// call reports is a third binding and is deliberately not a member: opening an observation is what the prefix
    /// pays for, and the stagger needs none of it, so a plan that only wants staggers never pays for either
    /// observation.</summary>
    internal static readonly string[] AttackInstanceBindings = ForgeEnemy.AttackInstanceContract.BindingIds;

    /// <summary>An observation of one native damage window. It is opened before the receiver runs and closed
    /// after it returns, so every reading in it is the transaction's own.</summary>
    internal sealed class DamageInstanceObservation
    {
        private readonly EnemyModule _owner;
        private bool _consumed;
        internal EntityReference Target { get; }
        internal IntPtr ReceiverPointer { get; }
        internal IntPtr EnemyPointer { get; }
        /// <summary>The array position the receiver's own `limbID` parameter named, or `-1` for a hit that
        /// named no part.</summary>
        internal int LimbIndex { get; }
        /// <summary>Whether the enemy was alive when the call was entered; a fact is published only for a
        /// transition out of life, so death already in progress is not reported a second time.</summary>
        internal bool AliveBefore { get; }
        /// <summary>The health the receiver showed before the call, which is the other end of the loss the
        /// limb row reports.</summary>
        internal double HealthBefore { get; }
        internal DamageInstanceObservation(EnemyModule owner, EntityReference target, IntPtr receiver,
            IntPtr enemy, int limbIndex, bool aliveBefore, double healthBefore)
        { _owner = owner; Target = target; ReceiverPointer = receiver; EnemyPointer = enemy;
          LimbIndex = limbIndex; AliveBefore = aliveBefore; HealthBefore = healthBefore; }
        internal bool TryConsume(EnemyModule owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true; return true;
        }
    }

    /// <summary>Opens the window. A hit no plan subscribes to is never observed at all, so the cost with no
    /// consumer is the subscription lookup and nothing else. The authority gate is the one every other
    /// host-owned fact in this module passes (<see cref="CanExecute"/>): both windows share one native pair, so a
    /// peer that may not publish the health facts may not publish these either.</summary>
    internal DamageInstanceObservation? BeforeDamageInstance(Dam_EnemyDamageBase damage, int limbId)
    {
        CheckThread();
        if (!CanExecute || !CanPublishDamageInstance || damage == null || damage.Owner == null) return null;
        if (!_entities.TryGetValue(damage.Owner.GlobalID, out var entry)
            || Resolve(entry.Reference) == null || entry.EnemyPointer != damage.Owner.Pointer
            || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer
            || !float.IsFinite(damage.Health)) return null;
        return new DamageInstanceObservation(this, entry.Reference, damage.Pointer, entry.EnemyPointer,
            limbId, entry.Enemy.Alive, damage.Health);
    }

    /// <summary>Closes the window and publishes what the commit settled. A receiver that did not move health
    /// publishes nothing: a rejected hit and a hit the receiver's own rules reduce to nothing are
    /// indistinguishable from here, so neither may be claimed.</summary>
    internal void AfterDamageInstance(Dam_EnemyDamageBase damage, DamageInstanceObservation? before)
    {
        CheckThread();
        // Consume on the first delivery, including a delivery that took nothing off: there is no later window.
        if (before == null || !before.TryConsume(this) || !CanExecute || !CanPublishDamageInstance) return;
        if (damage == null || damage.Pointer != before.ReceiverPointer || !float.IsFinite(damage.Health)) return;
        var entry = Resolve(before.Target);
        if (entry == null || entry.EnemyPointer != before.EnemyPointer || damage.Owner == null
            || damage.Owner.Pointer != before.EnemyPointer || entry.Enemy.Damage == null
            || entry.Enemy.Damage.Pointer != damage.Pointer) return;
        double healthAfter = Math.Max(0, damage.Health);
        bool alive = entry.Enemy.Alive;
        // The kill is the transition this call caused. Health at or below zero alone is not a kill — a
        // receiver can leave a corpse on its feet — and `Alive` alone is not this transaction's doing unless
        // the call is the one that took the last of it.
        if (before.AliveBefore && !alive && healthAfter <= 0) PublishKilled(entry);
        PublishLimbDamaged(entry, before, healthAfter);
    }

    /// <summary>Publishes the staggered row for a hit the closing half of the window reports. Three readings have
    /// to agree: the receiver's own answer says the damage landed, the hit asked for one of the stagger-class
    /// reactions, and the enemy's hitreact state machine still stands on exactly that reaction. Only the last one
    /// separates a stagger from a hit the receiver applied while its own immunity or retrigger rule swallowed the
    /// reaction — the applied answer alone cannot, and a reaction still current from an earlier hit can leave the
    /// same reading behind, which is the one claim this fact cannot rule out.</summary>
    internal void AfterDamageStaggered(Dam_EnemyDamageBase damage, ES_HitreactType hitreact, bool applied)
    {
        CheckThread();
        if (!applied || !IsStaggerReaction(hitreact)) return;
        if (!CanObserveFacts || !_kernel.HasSubscribers(ForgeEnemy.EnemyStaggerContract.BindingId)) return;
        if (damage == null || damage.Owner == null) return;
        if (!_entities.TryGetValue(damage.Owner.GlobalID, out var entry) || Resolve(entry.Reference) == null
            || entry.EnemyPointer != damage.Owner.Pointer || entry.Enemy.Damage == null
            || entry.Enemy.Damage.Pointer != damage.Pointer) return;
        var locomotion = entry.Enemy.Locomotion;
        if (locomotion == null || locomotion.Pointer == IntPtr.Zero) return;
        ES_HitreactType current;
        try
        {
            var hitreactState = locomotion.Hitreact;
            if (hitreactState == null || hitreactState.Pointer == IntPtr.Zero) return;
            current = hitreactState.CurrentReactionType;
        }
        catch (Exception) { return; }
        if (current != hitreact) return;
        var ports = new Dictionary<string, object> { ["target"] = entry.Reference };
        // The inflictor the receiver registered inside this same call, for the kinds a registered entity resolver
        // claims. An explosion packet carries no agent at all and a Forge submission submits none, so the optional
        // port is absent there rather than filled with a guessed reference.
        if (ReadInflictor(entry) is { } source) ports["source"] = source;
        PublishDamageFact("gtfo.enemy.stagger", ForgeEnemy.EnemyStaggerContract.BindingId, entry.Reference, ports);
    }

    /// <summary>Whether a plan subscribes to one binding. The damage window is one native pair for four facts,
    /// so the hook asks this before it allocates either observation.</summary>
    internal bool HasSubscriber(string binding) => _kernel.HasSubscribers(binding);

    /// <summary>A plan that subscribes to either fact is what makes a window worth opening; both rows are read
    /// from the same call, so one subscription opens it for both.</summary>
    private bool CanPublishDamageInstance
    {
        get
        {
            if (!CanObserveFacts) return false;
            foreach (var binding in AttackInstanceBindings) if (_kernel.HasSubscribers(binding)) return true;
            return false;
        }
    }

    private void PublishKilled(Entry entry)
    {
        var ports = new Dictionary<string, object> { ["target"] = entry.Reference };
        // The game registers the inflictor inside the same call, so the reading is this transaction's own. An
        // agent no registered kind claims — a Forge submission submits none at all — is an absent port rather
        // than a guessed reference.
        if (ReadInflictor(entry) is { } source) ports["source"] = source;
        // Every kind in the declared set is decoded before it reaches this receiver: the one window that
        // carries the source agent is the direct-damage receiver, and the other damage types arrive through
        // their own receive methods without one.
        ports["damage_kind"] = DamageKindDirect;
        PublishDamageFact("gtfo.enemy.kill", AttackInstanceContract.BindingId, entry.Reference, ports);
    }

    private void PublishLimbDamaged(Entry entry, DamageInstanceObservation before, double healthAfter)
    {
        // The row is the health this call took. A commit that left the target at its old health names no loss,
        // a limb that took none is not a damaged limb, and a hit naming no part publishes nothing at all.
        if (before.LimbIndex < 0 || !before.AliveBefore
            || !_kernel.HasSubscribers(AttackInstanceContract.LimbDamagedBindingId)) return;
        double amount = Math.Max(0, before.HealthBefore) - healthAfter;
        if (amount <= 0) return;
        PublishDamageFact("gtfo.enemy.limb", AttackInstanceContract.LimbDamagedBindingId, entry.Reference,
            new Dictionary<string, object> { ["target"] = entry.Reference, ["limb"] = before.LimbIndex, ["amount"] = amount });
    }

    /// <summary>The inflictor the game registered for the hit that just landed, resolved to the entity of
    /// whichever registered kind owns that agent. Read twice and compared by native pointer: each interop read
    /// of the field mints a fresh wrapper for the same agent, so the identity that has to hold is the pointer
    /// underneath, and a field another callback in this same chain is still writing is not reported.</summary>
    private EntityReference? ReadInflictor(Entry entry)
    {
        Agent? first, second;
        try { first = entry.Enemy.LastDamageInflictor; second = entry.Enemy.LastDamageInflictor; }
        catch (Exception) { return null; }
        if (first == null || second == null || first.Pointer != second.Pointer) return null;
        return ResolveAgent(first);
    }

    /// <summary>One fact of the damage window, published under the scope of the half that read it. Whether any
    /// plan wants the fact is the caller's own gate, because the window's subscription tests differ: the two
    /// observations share one test taken before the call, and the stagger is a binding a plan can hold alone. The
    /// subject is re-resolved here so a life that ended inside the same call publishes nothing.</summary>
    private void PublishDamageFact(string scope, string binding, EntityReference subject, Dictionary<string, object> ports)
    {
        if (Resolve(subject) == null) return;
        var result = _registration.Publish(new RuntimeEvent(
            scope + ":" + _kernel.WorldEpoch + ":" + checked(++_eventSequence), binding,
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + _kernel.WorldEpoch,
            RuntimeJson.From(ports)));
        // Runtime owns causal propagation and dispatch. Rejection must never reopen this native observation.
        if (result.Status == "rejected") _report("damage window fact rejected: " + result.Code);
    }

    /// <summary>The `direct` member of the declared `damage_kind` set, which is the index the receiver this
    /// window belongs to carries. The set is ordered `direct, melee, explosion, dot, shrapnel, collision, fall,
    /// environment, reflection`, and the barrel-damage receiver every dispatched hit resolves to is the
    /// direct-damage one; the other kinds reach the target through their own receive methods and never carry
    /// the agent this fact reads.</summary>
    internal const int DamageKindDirect = 0;
}
