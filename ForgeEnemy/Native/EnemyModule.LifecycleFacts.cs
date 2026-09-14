using System;
using System.Collections.Generic;
using System.Text.Json;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    internal const string DeathStartedBinding = ProviderId + ".binding.death_started";
    internal const string LimbBrokenBinding = ProviderId + ".binding.limb_broken";
    internal const int MaximumObservedLimbs = 256;

    internal sealed class LifecycleObservation
    {
        private readonly EnemyModule _owner;
        private bool _consumed;
        internal EntityReference Target { get; }
        internal IntPtr EnemyPointer { get; }
        internal IntPtr ReceiverPointer { get; }
        internal IntPtr LimbPointer { get; }
        internal int LimbId { get; }
        internal LifecycleObservation(EnemyModule owner, EntityReference target,
            IntPtr enemy, IntPtr receiver, IntPtr limb, int limbId)
        { _owner = owner; Target = target; EnemyPointer = enemy;
          ReceiverPointer = receiver; LimbPointer = limb; LimbId = limbId; }
        internal bool TryConsume(EnemyModule owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true; return true;
        }
    }

    private bool CanObserveFacts => _kernel.StartupState == RuntimeStartupState.Ready && CanExecute;

    internal LifecycleObservation? BeforeDeath(EnemyAgent enemy)
    {
        CheckThread();
        if (!CanObserveFacts || enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry)
            || Resolve(entry.Reference) != entry || enemy.Pointer != entry.EnemyPointer
            || entry.DeathObservation != null) return null;
        var observation = new LifecycleObservation(this, entry.Reference, entry.EnemyPointer,
            IntPtr.Zero, IntPtr.Zero, -1);
        // Claim once per life, even without consumers. Failed/unknown native completion is not replayed.
        entry.DeathObservation = observation;
        return observation;
    }

    internal void AfterDeath(EnemyAgent enemy, LifecycleObservation? before)
    {
        CheckThread();
        if (before == null || before.LimbId != -1 || !before.TryConsume(this) || !CanObserveFacts) return;
        var entry = Resolve(before.Target);
        if (entry == null || !ReferenceEquals(entry.DeathObservation, before)
            || enemy == null || enemy.Pointer != before.EnemyPointer
            || enemy.GlobalID != entry.Enemy.GlobalID || enemy.Alive) return;
        if (!CanObserveFacts || Resolve(before.Target) != entry || enemy.Pointer != before.EnemyPointer
            || enemy.Alive) return;
        // OnDead carries no verifiable killer, so the nullable source stays unknown instead of guessed.
        PublishLifecycleFact(DeathStartedBinding, before.Target,
            RuntimeJson.From(new { enemy = before.Target, source = (EntityReference?)null }));
    }

    internal LifecycleObservation? BeforeLimbBreak(Dam_EnemyDamageLimb limb)
    {
        CheckThread();
        if (!CanObserveFacts || limb == null || limb.Pointer == IntPtr.Zero) return null;
        var damage = limb.m_base;
        if (damage == null || damage.Owner == null
            || !_entities.TryGetValue(damage.Owner.GlobalID, out var entry)
            || Resolve(entry.Reference) != entry) return null;
        int id = limb.m_limbID;
        if (id < 0 || id >= MaximumObservedLimbs || entry.LimbObservations.ContainsKey(id)) return null;
        var observation = new LifecycleObservation(this, entry.Reference, entry.EnemyPointer,
            damage.Pointer, limb.Pointer, id);
        if (!MatchesLimb(entry, limb, observation) || limb.IsDestroyed
            || !MatchesLimb(entry, limb, observation) || limb.IsDestroyed
            || !CanObserveFacts || Resolve(entry.Reference) != entry) return null;
        return entry.LimbObservations.TryAdd(id, observation) ? observation : null;
    }

    internal void AfterLimbBreak(Dam_EnemyDamageLimb limb, LifecycleObservation? before)
    {
        CheckThread();
        if (before == null || before.LimbId < 0 || !before.TryConsume(this) || !CanObserveFacts) return;
        var entry = Resolve(before.Target);
        if (entry == null || !entry.LimbObservations.TryGetValue(before.LimbId, out var captured)
            || !ReferenceEquals(captured, before) || !MatchesLimb(entry, limb, before)) return;
        bool destroyed = limb.IsDestroyed;
        if (!MatchesLimb(entry, limb, before) || limb.IsDestroyed != destroyed
            || !CanObserveFacts || Resolve(before.Target) != entry) return;
        if (!destroyed)
        {
            // A proven no-op may have a later genuine transition. This token remains consumed.
            entry.LimbObservations.Remove(before.LimbId); return;
        }
        PublishLifecycleFact(LimbBrokenBinding, before.Target,
            RuntimeJson.From(new { target = before.Target, limb = before.LimbId }));
    }

    private bool MatchesLimb(Entry entry, Dam_EnemyDamageLimb limb, LifecycleObservation observation)
    {
        if (limb == null || limb.Pointer != observation.LimbPointer || limb.m_limbID != observation.LimbId
            || entry.EnemyPointer != observation.EnemyPointer) return false;
        var damage = entry.Enemy.Damage;
        if (damage == null || damage.Pointer == IntPtr.Zero || damage.Pointer != observation.ReceiverPointer
            || !damage.IsSetup || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer
            || damage.Owner.GlobalID != entry.Enemy.GlobalID
            || limb.m_base == null || limb.m_base.Pointer != observation.ReceiverPointer) return false;
        var limbs = damage.DamageLimbs;
        if (limbs == null || limbs.Length > MaximumObservedLimbs || observation.LimbId >= limbs.Length) return false;
        var indexed = limbs[observation.LimbId];
        return indexed != null && indexed.Pointer == observation.LimbPointer;
    }

    private void PublishLifecycleFact(string binding, EntityReference target, JsonElement outputs)
    {
        if (!CanObserveFacts || Resolve(target) == null) return;
        var result = _registration.Publish(new RuntimeEvent(
            "gtfo.enemy.lifecycle:" + _kernel.WorldEpoch + ":" + checked(++_eventSequence), binding,
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + _kernel.WorldEpoch, outputs));
        // Runtime owns causal propagation and dispatch. Rejection must never reopen this native observation.
        if (result.Status == "rejected") _report("lifecycle fact rejected: " + result.Code);
    }
}
