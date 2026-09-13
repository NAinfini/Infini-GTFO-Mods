using System;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Receivers;

internal sealed record EnemyDamageDelta(EntityReference Target, double ActualDamage);

/// <summary>Owns synchronous call-window observations, not an event history or damage ledger.</summary>
internal sealed class EnemyDamageWindow : EnemyThreadBoundary
{
    internal sealed class Observation
    {
        internal readonly EnemyDamageWindow Owner;
        internal readonly long Generation;
        internal readonly EntityReference Target;
        internal readonly IntPtr Receiver;
        internal readonly float Health;
        internal bool Consumed;
        internal Observation(EnemyDamageWindow owner, long generation, EntityReference target,
            IntPtr receiver, float health)
        { Owner = owner; Generation = generation; Target = target; Receiver = receiver; Health = health; }
    }
    private readonly Func<bool> _canObserve;
    private readonly Func<EntityReference, IntPtr, bool> _isCurrent;
    private long _generation;

    internal EnemyDamageWindow(Func<bool> canObserve, Func<EntityReference, IntPtr, bool> isCurrent)
    {
        _canObserve = canObserve ?? throw new ArgumentNullException(nameof(canObserve));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
    }
    internal void Invalidate() { CheckThread(); _generation = checked(_generation + 1); }

    internal Observation? Capture(EntityReference target, IntPtr receiver, float health)
    {
        CheckThread(); ArgumentNullException.ThrowIfNull(target);
        if (!_canObserve() || receiver == IntPtr.Zero || !float.IsFinite(health)
            || !_isCurrent(target, receiver)) return null;
        return new Observation(this, _generation, target, receiver, health);
    }

    internal EnemyDamageDelta? Complete(Observation? before, IntPtr receiver, float health)
    {
        CheckThread();
        if (before == null || !ReferenceEquals(before.Owner, this) || before.Consumed) return null;
        before.Consumed = true; // Even rejected/zero windows must never be reinterpreted later.
        if (before.Generation != _generation || !_canObserve() || receiver != before.Receiver
            || !float.IsFinite(health) || !_isCurrent(before.Target, receiver)) return null;
        double actual = Math.Max(0, (double)Math.Max(0, before.Health) - Math.Max(0, health));
        return actual > 0 ? new EnemyDamageDelta(before.Target, actual) : null;
    }

    internal void Discard(Observation? observation)
    {
        CheckThread();
        if (observation != null && ReferenceEquals(observation.Owner, this)) observation.Consumed = true;
    }
}
