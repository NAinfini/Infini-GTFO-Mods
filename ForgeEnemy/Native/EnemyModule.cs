using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    internal EnemyModule(RuntimeKernel kernel, RuntimeLogLevel logLevel, Func<bool> canExecute, Action<string> report,
        Func<EnemyAgent, EntityReference, RuntimeEntitySnapshot?>? observe = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _observe = observe;
        _registration = kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RegistryJson,
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
        }, logLevel);
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
        if (!CanExecute || !(_kernel.HasSubscribers(DamageBinding) || _kernel.HasSubscribers(HealthChangedBinding))
            || damage == null || damage.Owner == null) return null;
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
        double healthAfter = Math.Max(0, damage.Health);
        double actualDamage = Math.Max(0, Math.Max(0, before.HealthBefore) - healthAfter);
        // Only a real loss inside the native damage call is a fact; a rise in this window is not inferred as healing.
        if (actualDamage == 0) return;
        long sequence = checked(++_eventSequence), tick = Math.Max(0, _kernel.CurrentTick);
        string scope = "gtfo.world:" + _kernel.WorldEpoch;
        // The hook only observes health before/after; it cannot identify the attacker, damage type
        // or limb, so those fields are published as null instead of guessed.
        var damageResult = _registration.Publish(new RuntimeEvent(
            "gtfo.enemy.damage:" + _kernel.WorldEpoch + ":" + sequence, DamageBinding, _kernel.WorldEpoch, tick, scope,
            RuntimeJson.From(new { source = (EntityReference?)null, target = before.Target, amount = actualDamage,
                damage_kind = (int?)null, limb = (int?)null })));
        if (damageResult.Status == "rejected") _report("damage fact rejected: " + damageResult.Code);
        // The same observed loss as a health change: value is the clamped health read back after the call, delta the signed loss.
        var healthResult = _registration.Publish(new RuntimeEvent(
            "gtfo.enemy.health:" + _kernel.WorldEpoch + ":" + sequence, HealthChangedBinding, _kernel.WorldEpoch, tick, scope,
            RuntimeJson.From(new { target = before.Target, value = healthAfter, delta = -actualDamage })));
        if (healthResult.Status == "rejected") _report("health change fact rejected: " + healthResult.Code);
    }

    /// <summary>One row of the multi-target heal result. Field names are the wire contract; keep them stable.</summary>
    private sealed record HealRow(EntityReference Target, string Status, string Code, double RequestedAmount,
        double? ActualAmount, float? HealthBefore, float? HealthAfter, double? OverflowAmount);

    private CommandResult Heal(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        // Source carries no faction or targeting restriction (teammate/hostile/self are all valid);
        // it is still a required, kernel-validated recipient reference.
        _ = context.GetEntityInput("source");
        var policy = context.Parameters.GetProperty("overheal_policy").GetString();
        if (policy == "overheal")
            // GTFO health is quantized against HealthMax via SFloat16; there is no representable value
            // beyond that ceiling, so the whole command is rejected up front instead of silently clamping.
            return CommandResult.Rejected("overheal-unsupported");
        double requested = context.Inputs.GetProperty("amount").GetDouble();
        if (!double.IsFinite(requested) || requested < MinimumAmount || requested > MaximumAmount)
            return CommandResult.Rejected("amount-out-of-range");
        double? cap = null;
        if (context.Inputs.TryGetProperty("cap", out var capElement))
        {
            double capValue = capElement.GetDouble();
            if (!double.IsFinite(capValue) || capValue <= 0) return CommandResult.Rejected("invalid-cap");
            cap = capValue;
        }
        var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        // A fully successful heal publishes one fact per target; the command result budget caps at 128.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<HealRow>(targets.Length);
        var facts = new List<RuntimeFact>();
        int committed = 0, rejected = 0, unknown = 0;
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            HealRow Row(string status, string code, float? before = null, float? after = null, double? actual = null, double? overflow = null)
                => new(target, status, code, requested, actual, before, after, overflow);

            if (stopCommitting) { rows.Add(Row("rejected", "not-attempted-after-unknown-commit")); rejected++; continue; }
            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row("rejected", "stale-or-unsupported-recipient")); rejected++; continue; }
            var enemy = entry.Enemy;
            var damage = enemy.Damage;
            if (damage == null || !damage.IsSetup || damage.Pointer == IntPtr.Zero)
            { rows.Add(Row("rejected", "missing-health-receiver")); rejected++; continue; }
            var receiverPointer = damage.Pointer;
            if (damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer)
            { rows.Add(Row("rejected", "health-receiver-owner-mismatch")); rejected++; continue; }
            if (!enemy.Alive || !(damage.Health > 0)) { rows.Add(Row("rejected", "not-alive")); rejected++; continue; }
            float before = damage.Health, maximum = damage.HealthMax;
            if (!float.IsFinite(before) || !float.IsFinite(maximum) || maximum <= 0 || before > maximum)
            { rows.Add(Row("rejected", "invalid-health-state")); rejected++; continue; }
            double effectiveCap = cap.HasValue ? Math.Min(maximum, cap.Value) : maximum;
            double desiredUncapped = before + requested;
            double overflowAmount = Math.Max(0, desiredUncapped - effectiveCap);
            if (desiredUncapped > effectiveCap && policy == "discard")
            { rows.Add(Row("rejected", "would-overheal", before, overflow: overflowAmount)); rejected++; continue; }
            double desired = Math.Max(before, Math.Min(effectiveCap, desiredUncapped));
            float quantized;
            try
            {
                var encoded = new SFloat16();
                encoded.Set((float)desired, maximum);
                quantized = encoded.Get(maximum);
            }
            catch (Exception)
            {
                // No SendSetHealth call has occurred; unlike commit exceptions this is known uncommitted.
                rows.Add(Row("rejected", "quantization-failed", before, overflow: overflowAmount)); rejected++; continue;
            }
            if (!float.IsFinite(quantized) || quantized < 0 || quantized > maximum)
            { rows.Add(Row("rejected", "quantization-failed", before, overflow: overflowAmount)); rejected++; continue; }
            // Native preview/getters can re-enter other mods. Revalidate even a no-op completion;
            // otherwise an absolute HP value computed from an old snapshot could damage the recipient.
            try
            {
                if (!CanExecute) { rows.Add(Row("rejected", "authority-or-phase", before)); rejected++; continue; }
                if (Resolve(target) != entry || enemy.Damage == null || enemy.Damage.Pointer != receiverPointer
                    || damage.Pointer != receiverPointer
                    || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer
                    || !damage.IsSetup || !enemy.Alive || damage.Health != before || damage.HealthMax != maximum)
                { rows.Add(Row("rejected", "state-changed-before-commit", before)); rejected++; continue; }
                if (!CanExecute) { rows.Add(Row("rejected", "authority-or-phase", before)); rejected++; continue; }
            }
            catch (Exception)
            { rows.Add(Row("rejected", "preflight-exception", before)); rejected++; continue; }

            float after = before;
            bool commitFailed = false;
            if (desired > before && quantized > before)
            {
                // Crossing the native call boundary may have sent a packet even when the call throws; that failure
                // mode (native-commit-exception) is distinct from a later, already-committed readback failure
                // (readback-exception), so each gets its own try/catch.
                var committing = true;
                try { damage.SendSetHealth((float)desired); }
                catch (Exception) { rows.Add(Row("unknown", "native-commit-exception", before)); commitFailed = true; committing = false; }
                if (committing)
                {
                    try
                    {
                        if (!CanExecute || Resolve(target) == null
                            || enemy.Damage == null || enemy.Damage.Pointer != receiverPointer || damage.Pointer != receiverPointer
                            || !damage.IsSetup || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer)
                        { rows.Add(Row("unknown", "receiver-changed-during-commit", before)); commitFailed = true; }
                        else
                        {
                            after = damage.Health;
                            if (!enemy.Alive || damage.HealthMax != maximum || !float.IsFinite(after) || after < before || after > maximum)
                            { rows.Add(Row("unknown", "unexpected-health-readback", before)); commitFailed = true; }
                        }
                    }
                    catch (Exception)
                    { rows.Add(Row("unknown", "readback-exception", before)); commitFailed = true; }
                }
            }
            if (commitFailed) { unknown++; stopCommitting = true; continue; }
            double actual = (double)after - before;
            rows.Add(Row("committed", "committed", before, after, actual, overflowAmount));
            committed++;
            // Zero effective healing is a valid result, but is never published as a health-change fact.
            if (actual > 0) facts.Add(new RuntimeFact(HealthChangedBinding, RuntimeJson.From(new { target, value = after, delta = actual })));
        }

        var outputs = RuntimeJson.From(new { results = rows });
        // r11: aggregation branches on whether any row committed, not on unknown==0/facts.Count==0. A committed
        // row (including zero-delta) alongside a rejected or unknown row is Partial with possibly-empty facts.
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs, facts.ToArray());
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed, facts.ToArray());
        if (unknown == 0)
        {
            string code = rows.Count == 1 ? rows[0].Code
                : rows.Select(r => r.Code).Distinct().Count() == 1 ? rows[0].Code : "heal-all-rejected";
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        }
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown,
            rows.Count == 1 ? rows[0].Code : "heal-all-unknown", "", outputs, facts.ToArray());
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
