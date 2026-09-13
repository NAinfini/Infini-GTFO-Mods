using System;
using System.Collections.Generic;
using System.Globalization;
using Enemies;
using ForgeRuntime.Framework;
using SNetwork;

namespace ForgeEnemy.Native;

/// <summary>GTFO receiver implementation. Entity kind is a supported API boundary, never an allegiance rule.</summary>
internal sealed partial class EnemyModule : IDisposable
{
    internal const string ProviderId = "forge.module.gtfo.enemy";
    internal const string DamageBinding = ProviderId + ".binding.damage_applied";
    internal const string HealBinding = ProviderId + ".binding.heal";
    internal const string HealthChangedBinding = ProviderId + ".binding.health_changed";
    internal const double MinimumAmount = 0.000001;
    internal const double MaximumAmount = 1000000;
    private sealed record Entry(EnemyAgent Enemy, EntityReference Reference, IntPtr EnemyPointer)
    {
        internal LifecycleObservation? DeathObservation;
        internal readonly Dictionary<int, LifecycleObservation> LimbObservations = new();
    }
    internal sealed record DespawnObservation(EnemyModule Owner, EntityReference Target, IntPtr EnemyPointer);
    internal sealed class DamageObservation
    {
        private readonly EnemyModule _owner;
        private bool _consumed;
        internal EntityReference Target { get; }
        internal float HealthBefore { get; }
        internal IntPtr DamagePointer { get; }
        internal DamageObservation(EnemyModule owner, EntityReference target, float healthBefore, IntPtr damagePointer)
        { _owner = owner; Target = target; HealthBefore = healthBefore; DamagePointer = damagePointer; }
        internal bool TryConsume(EnemyModule owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true;
            return true;
        }
    }
    private readonly Dictionary<ushort, Entry> _entities = new();
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Func<EnemyAgent, EntityReference, RuntimeEntitySnapshot?>? _observe;
    private readonly Action<string> _report;
    private readonly RuntimeModuleHandle _registration;
    private long _nextLife, _eventSequence;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private bool _disposed;
    internal bool IsRegistered => !_disposed && _registration.IsRegistered;

    internal EnemyModule(RuntimeKernel kernel, Func<bool> canExecute, Action<string> report,
        Func<EnemyAgent, EntityReference, RuntimeEntitySnapshot?>? observe = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _observe = observe;
        _registration = kernel.RegisterModule(new RuntimeModule("1.0.0", RegistryJson,
            new Dictionary<string, CommandHandler> { ["gtfo.enemy.heal"] = Heal },
            new[] {
                new BindingSupport(DamageBinding, "implementation-only", new[] { "gtfo.enemy.health.read" }),
                new BindingSupport(HealBinding, "implementation-only", new[] { "gtfo.enemy.health.write" }),
                new BindingSupport(HealthChangedBinding, "implementation-only", new[] { "gtfo.enemy.health.read" }),
                new BindingSupport(DeathStartedBinding, "implementation-only", new[] { "gtfo.enemy.lifecycle.read" }),
                new BindingSupport(LimbBrokenBinding, "implementation-only", new[] { "gtfo.enemy.limbs.read" })
            }, new Dictionary<string, Func<EntityReference, bool>> { ["gtfo.enemy"] = IsCurrent })
        {
            EntityObservers = observe == null ? null : new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                { ["gtfo.enemy"] = ObserveEntity }
        });
        try
        {
            _lifecycle = _registration.ObserveLifecycle(value =>
            {
                if (value.Kind == RuntimeLifecycleKind.WorldChanged
                    || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                    ClearWorld();
            });
        }
        catch { _registration.Dispose(); throw; }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Enemy APIs require the owning simulation thread.");
    }
    private bool CanExecute
    {
        get
        {
            CheckThread();
            return IsRegistered && _kernel.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                && _canExecute() && SNet.IsMaster;
        }
    }
    internal void ClearWorld() { CheckThread(); _entities.Clear(); }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _registration.Dispose(); _lifecycle.Dispose(); ClearWorld(); _disposed = true;
    }

    internal EntityReference TrackSpawn(EnemyAgent enemy)
    {
        CheckThread();
        if (!IsRegistered || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            throw new InvalidOperationException("Enemy module is not accepting native instances.");
        if (enemy == null) throw new ArgumentNullException(nameof(enemy));
        if (enemy.Pointer == IntPtr.Zero) throw new ArgumentException("Enemy must have a native instance.", nameof(enemy));
        // A new wrapper alone is not a new native life; despawn/world boundaries retire the old life.
        if (_entities.TryGetValue(enemy.GlobalID, out var existing)
            && existing.Reference.WorldEpoch == _kernel.WorldEpoch && existing.EnemyPointer == enemy.Pointer
            && Resolve(existing.Reference) != null) return existing.Reference;
        var reference = new EntityReference("gtfo.enemy:" + enemy.GlobalID.ToString(CultureInfo.InvariantCulture),
            _kernel.WorldEpoch, checked(++_nextLife));
        _entities[enemy.GlobalID] = new Entry(enemy, reference, enemy.Pointer);
        return reference;
    }

    // Synchronous native entry. Deferred work must retain CaptureDespawn's token, not an EnemyAgent pointer.
    internal void TrackDespawn(EnemyAgent enemy) => CompleteDespawn(CaptureDespawn(enemy));

    internal DespawnObservation? CaptureDespawn(EnemyAgent enemy)
    {
        CheckThread();
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry)
            || entry.EnemyPointer != enemy.Pointer || Resolve(entry.Reference) == null) return null;
        return new DespawnObservation(this, entry.Reference, entry.EnemyPointer);
    }

    internal void CompleteDespawn(DespawnObservation? observation)
    {
        CheckThread();
        if (observation == null || !ReferenceEquals(observation.Owner, this)) return;
        var entry = Resolve(observation.Target);
        if (entry != null && entry.EnemyPointer == observation.EnemyPointer) _entities.Remove(entry.Enemy.GlobalID);
    }

    private Entry? Resolve(EntityReference reference)
    {
        CheckThread();
        if (!IsRegistered || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return null;
        const string prefix = "gtfo.enemy:";
        if (reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !ushort.TryParse(reference.Id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !_entities.TryGetValue(id, out var entry) || entry.Reference != reference || entry.Enemy == null
            || entry.Enemy.GlobalID != id || entry.Enemy.Pointer != entry.EnemyPointer) return null;
        return entry;
    }
    private bool IsCurrent(EntityReference reference) => Resolve(reference) != null;

    private RuntimeEntitySnapshot? ObserveEntity(EntityReference reference)
    {
        if (_observe == null || _kernel.StartupState != RuntimeStartupState.Ready || !CanExecute) return null;
        var entry = Resolve(reference);
        if (entry == null) return null;
        var snapshot = _observe(entry.Enemy, reference);
        // A callback may replace the instance or invalidate the world. Never return its old life.
        return snapshot?.Ref == reference && CanExecute && Resolve(reference) == entry ? snapshot : null;
    }

    internal DamageObservation? BeforeDamage(Dam_EnemyDamageBase damage)
    {
        if (!CanExecute || !_kernel.HasSubscribers(DamageBinding) || damage == null || damage.Owner == null) return null;
        if (!_entities.TryGetValue(damage.Owner.GlobalID, out var entry)
            || Resolve(entry.Reference) == null || entry.EnemyPointer != damage.Owner.Pointer
            || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer) return null;
        return float.IsFinite(damage.Health) ? new DamageObservation(this, entry.Reference, damage.Health, damage.Pointer) : null;
    }

    internal void AfterDamage(Dam_EnemyDamageBase damage, DamageObservation? before)
    {
        CheckThread();
        // Consume on first delivery, including a rejected/zero-loss delivery. There is no later retry window.
        if (before == null || !before.TryConsume(this)) return;
        if (!CanExecute || damage == null || damage.Pointer != before.DamagePointer) return;
        var entry = Resolve(before.Target);
        if (entry == null || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer
            || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer || !float.IsFinite(damage.Health)) return;
        double actualDamage = Math.Max(0, Math.Max(0, before.HealthBefore) - Math.Max(0, damage.Health));
        if (actualDamage == 0) return;
        var result = _registration.Publish(new RuntimeEvent(
            "gtfo.enemy.damage:" + _kernel.WorldEpoch + ":" + checked(++_eventSequence), DamageBinding,
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + _kernel.WorldEpoch,
            RuntimeJson.From(new { target = before.Target, actual_damage = actualDamage })));
        if (result.Status == "rejected") _report("damage fact rejected: " + result.Code);
    }

    private CommandResult Heal(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("gtfo.enemy.authority_or_phase");
        var target = context.GetEntityInput("target");
        var entry = Resolve(target);
        if (entry == null) return CommandResult.Rejected("gtfo.enemy.stale_or_unsupported_recipient");
        var enemy = entry.Enemy;
        var damage = enemy.Damage;
        if (damage == null || !damage.IsSetup || damage.Pointer == IntPtr.Zero) return CommandResult.Rejected("gtfo.enemy.missing_health_receiver");
        var receiverPointer = damage.Pointer;
        if (damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer)
            return CommandResult.Rejected("gtfo.enemy.health_receiver_owner_mismatch");
        if (!enemy.Alive || !(damage.Health > 0)) return CommandResult.Rejected("gtfo.enemy.not_alive");
        double requested = context.Parameters.GetProperty("amount").GetDouble();
        if (!double.IsFinite(requested) || requested < MinimumAmount || requested > MaximumAmount)
            return CommandResult.Rejected("gtfo.enemy.amount_out_of_range");
        float before = damage.Health, maximum = damage.HealthMax;
        if (!float.IsFinite(before) || !float.IsFinite(maximum) || maximum <= 0 || before > maximum)
            return CommandResult.Rejected("gtfo.enemy.invalid_health_state");
        double desired = Math.Min(maximum, before + requested);
        float quantized;
        try
        {
            var encoded = new SFloat16();
            encoded.Set((float)desired, maximum);
            quantized = encoded.Get(maximum);
        }
        catch (Exception error)
        {
            // No SendSetHealth call has occurred; unlike commit exceptions this is known uncommitted.
            return CommandResult.Failed("gtfo.enemy.quantization_failed", error.GetType().Name);
        }
        if (!float.IsFinite(quantized) || quantized < 0 || quantized > maximum)
            return CommandResult.Rejected("gtfo.enemy.invalid_native_quantization");
        // Native preview/getters can re-enter other mods. Revalidate even a no-op completion;
        // otherwise an absolute HP value computed from an old snapshot could damage the recipient.
        try
        {
            if (!CanExecute)
                return CommandResult.Rejected("gtfo.enemy.authority_or_phase");
            if (Resolve(target) != entry || enemy.Damage == null || enemy.Damage.Pointer != receiverPointer
                || damage.Pointer != receiverPointer
                || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer
                || !damage.IsSetup || !enemy.Alive || damage.Health != before || damage.HealthMax != maximum)
                return CommandResult.Rejected("gtfo.enemy.state_changed_before_commit");
            if (!CanExecute)
                return CommandResult.Rejected("gtfo.enemy.authority_or_phase");
        }
        catch (Exception error)
        {
            return CommandResult.Failed("gtfo.enemy.preflight_exception", error.GetType().Name);
        }
        float after = before;
        if (desired > before && quantized > before)
        {
            // Crossing the native call boundary may have sent a packet even when the call throws.
            var attempted = RuntimeJson.From(new { target, requestedAmount = requested, healthBefore = before,
                nativeSubmissionAttempted = true });
            try
            {
                damage.SendSetHealth((float)desired);
                if (!CanExecute || Resolve(target) == null
                    || enemy.Damage == null || enemy.Damage.Pointer != receiverPointer || damage.Pointer != receiverPointer
                    || !damage.IsSetup || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer)
                    return CommandResult.FailedUnknown(attempted, "gtfo.enemy.receiver_changed_during_commit");
                after = damage.Health;
                if (!enemy.Alive || damage.HealthMax != maximum || !float.IsFinite(after) || after < before || after > maximum)
                    return CommandResult.FailedUnknown(attempted, "gtfo.enemy.unexpected_health_readback");
            }
            catch (Exception error)
            {
                return CommandResult.FailedUnknown(attempted, "gtfo.enemy.native_commit_exception", error.GetType().Name);
            }
        }
        double actual = (double)after - before;
        var output = RuntimeJson.From(new { target, requestedAmount = requested, actualAmount = actual,
            healthBefore = before, healthAfter = after, overflowAmount = Math.Max(0, before + requested - maximum) });
        // Zero effective healing is a valid result, but is never published as a health-change fact.
        return actual > 0
            ? CommandResult.Succeeded(output, new RuntimeFact(HealthChangedBinding,
                RuntimeJson.From(new { target, health_before = before, health_after = after, delta = actual })))
            : CommandResult.Succeeded(output);
    }

    internal const string RegistryJson = """
    {
      "providers": [
        {
          "id": "forge.module.gtfo.enemy",
          "kind": "native",
          "version": "1.0.0",
          "dependencies": []
        }
      ],
      "capabilities": [],
      "bindings": [
        {
          "id": "forge.module.gtfo.enemy.binding.damage_applied",
          "capabilityId": "forge.trigger.combat.damage_applied",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.damage_applied",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.heal",
          "capabilityId": "forge.action.combat.heal",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.heal",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.health_changed",
          "capabilityId": "forge.trigger.combat.health_changed",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.health_changed",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.death_started",
          "capabilityId": "forge.trigger.enemy.death_started",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.death_started",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.limb_broken",
          "capabilityId": "forge.trigger.combat.limb_broken",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.limb_broken",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
      ]
    }
    """;
}
