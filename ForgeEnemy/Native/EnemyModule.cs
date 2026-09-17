using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agents;
using Enemies;
using ForgeRuntime.Framework;
using SNetwork;

namespace ForgeEnemy.Native;

/// <summary>GTFO receiver implementation. Entity kind is a supported API boundary, never an allegiance rule.</summary>
internal sealed partial class EnemyModule : IDisposable
{
    internal const string ProviderId = "forge.module.gtfo.enemy";
    internal const string DamageBinding = ProviderId + ".binding.damage_applied";
    private const string EnemyTypeAttachment = "enemy-type";
    internal const string HealBinding = ProviderId + ".binding.heal";
    internal const string DamageActionBinding = ProviderId + ".binding.damage";
    internal const string DamageActionHandler = "gtfo.enemy.damage";
    internal const string HealthChangedBinding = ProviderId + ".binding.health_changed";
    internal const double MinimumAmount = 0.000001;
    internal const double MaximumAmount = 1000000;
    private const double DamageMinimumAmount = 0.000001;
    private const double DamageMaximumAmount = 1000000;
    /// <summary>Members of the declared `damage_kind` enum set; an index outside it is not a kind.</summary>
    private const int DamageKindCount = 9;
    private sealed record Entry
    {
        internal Entry(EnemyAgent enemy, EntityReference reference, IntPtr enemyPointer)
        { Enemy = enemy; Reference = reference; EnemyPointer = enemyPointer; Behavior = new(enemy, reference); }
        internal EnemyAgent Enemy { get; }
        internal EntityReference Reference { get; }
        internal IntPtr EnemyPointer { get; }
        internal Behavior Behavior { get; }
        internal LifecycleObservation? DeathObservation;
        internal readonly Dictionary<int, LifecycleObservation> LimbObservations = new();
        /// <summary>The last tag state this module published for the life, which is what turns a per-frame
        /// property sample into one rising and one falling fact per tag. Owned by `EnemyNodeFacts`.</summary>
        internal bool Tagged;
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
    private readonly Func<EnemyAgent, uint?>? _enemyType;
    private readonly Action<string> _report;
    private readonly RuntimeModuleHandle _registration;
    private long _nextLife, _eventSequence;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private bool _disposed;
    internal bool IsRegistered => !_disposed && _registration.IsRegistered;

    // The frame pump costs one reading per registered enemy; with no plan subscribed to any behaviour fact it
    // must not read the native AI at all.
    private bool CanPublishBehavior
    {
        get
        {
            if (!CanObserveFacts) return false;
            foreach (var binding in BehaviorBindings) if (_kernel.HasSubscribers(binding)) return true;
            return false;
        }
    }

    internal EnemyModule(RuntimeKernel kernel, RuntimeLogLevel logLevel, Func<bool> canExecute, Action<string> report,
        Func<EnemyAgent, EntityReference, RuntimeEntitySnapshot?>? observe = null, Func<EnemyAgent, uint?>? enemyType = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _observe = observe;
        _enemyType = enemyType;
        var handlers = new Dictionary<string, CommandHandler> { ["gtfo.enemy.heal"] = Heal, [DamageActionHandler] = Damage };
        var shapes = new Dictionary<string, HandlerShape>
        {
            ["gtfo.enemy.heal"] = HealPorts, [DamageActionHandler] = DamagePorts,
            [EnemySelector.HandlerName] = EnemySelectorContract.Shape
        };
        // The node-list family's own handlers and shapes, from the one declaration file that names them: the
        // three node actions, the six value rows, and the action families whose rows are list rows too.
        foreach (var handler in AllNodeHandlers()) handlers.Add(handler.Key, handler.Value);
        foreach (var shape in AllNodeShapes()) shapes.Add(shape.Key, shape.Value);
        // The action families the node-list slice left unwired — enemy control, combat, foam, behaviour and the
        // profile's `phase_set`: their rows, handlers, shapes and support rows are declared in their own
        // contracts, and `EnemyActionFamilies` is the one place they are composed into this registration.
        foreach (var handler in ActionFamilyHandlers()) handlers.Add(handler.Key, handler.Value);
        foreach (var shape in ActionFamilyShapes()) shapes.Add(shape.Key, shape.Value);
        _registration = kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, EnemyRegistration.RegistryJson(),
            handlers, EnemyRegistration.Support(), new Dictionary<string, Func<EntityReference, bool>> { ["gtfo.enemy"] = IsCurrent })
        {
            Shapes = shapes,
            // The selector is evaluated on demand, so its evaluator and the entity set it reads are registered
            // here, on the one provider that already owns the `gtfo.enemy` kind: the kernel lets only a kind's own
            // owner say which entities of it exist.
            Evaluators = NodeEvaluatorsWithSelector(),
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>> { [EnemySelector.EntityKind] = CurrentCandidates },
            EntityObservers = observe == null ? null : new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                { ["gtfo.enemy"] = ObserveEntity },
            // `AgentTarget.m_agent` is a cross-provider native instance: each kind is only ever asked of the
            // provider that registered it, and an instance no registered kind claims stays unresolved.
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
                { ["gtfo.enemy"] = ResolveInstance },
            // Which zone an enemy stands in is this provider's own reading of its own instances: the enemy's course
            // node names its zone, and the coordinates are the same text the Map provider names a zone with, so a
            // plan that filters a candidate set by zone compares one place and not two.
            EntityZones = new Dictionary<string, Func<EntityReference, EntityReference?>>
                { ["gtfo.enemy"] = ZoneOfEnemy },
            // The one row of this provider that is written where it is seen: `forge.action.enemy.mark` is a
            // `presentation` step, so the host decides the timing and each recipient builds the marker on its own
            // machine. The audience is the realm's own player list, answered by the one function declared with
            // the row that needs it.
            PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>(StringComparer.Ordinal)
                { [ProviderId] = PresentationAudience },
            // A session with no way to read an enemy's block does not register the kind at all: a plan naming
            // `enemy-type` is then refused when it loads instead of being accepted and never dispatched. The
            // kind is matched against the event's own subject, the enemy instance the block was read from.
            AttachmentMatchers = enemyType == null ? null : new Dictionary<string, AttachmentMatcherRegistration>
                { [EnemyTypeAttachment] = AttachmentMatcherRegistration.BySubject(MatchesEnemyType) }
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
        // The value rows read this module's own tracked lives, so the resolver is attached once the
        // registration that declares them is live. It goes with the module, and with the world the module
        // clears.
        AttachNodeFamily(kernel);
    }

    /// <summary>The selector's evaluator and the node family's value rows in one table: the selector is the
    /// module's own kind-level query, and the six value rows are the node list's `values` section.</summary>
    private Dictionary<string, EvaluatorHandler> NodeEvaluatorsWithSelector()
    {
        var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
            { [EnemySelector.HandlerName] = EnemySelector.Evaluate };
        foreach (var evaluator in NodeEvaluators()) evaluators.Add(evaluator.Key, evaluator.Value);
        return evaluators;
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

    /// <summary>The gate the provider's one `presentation` handler passes on the machine that presents it: the
    /// registration, the startup state and the gameplay flag, without the master flag. The host decides the step
    /// and dispatches it to the players it addressed; the machine that runs the handler is a recipient, so being
    /// the master is not a condition of presenting.</summary>
    internal bool CanPresent
    {
        get
        {
            CheckThread();
            return IsRegistered && _kernel.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                && _canExecute();
        }
    }
    internal void ClearWorld() { CheckThread(); _entities.Clear(); ClearBehaviors(); }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        DetachNodeFamily();
        DisposeActionFamilies();
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
        if (entry != null && entry.EnemyPointer == observation.EnemyPointer)
        {
            // The ledger row goes first: reporting the ability a despawn cut short needs the entry this call is
            // about to drop — both to read the component back and to name the enemy — and a despawn is the one end
            // path where the entity is already on its way out.
            ForgetBehavior(observation.Target);
            _entities.Remove(entry.Enemy.GlobalID);
            // A retired life owns no variables either: a despawn is the other way an enemy life ends, and a plan
            // that wrote a per-enemy value must not have it survive into whatever takes the instance's place.
            ReleaseEnemyScope(observation.Target);
        }
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

    /// <summary>The one `enemy-type` mount matcher: a plan mounted on an official enemy type runs for an event
    /// whose subject is this provider's own enemy instance of exactly that type. The reference is the block's
    /// `persistentID` as plain decimal text, the single spelling the website exports (ruling 61), and the type
    /// is read from the instance this module registered — an instance it cannot read back, a subject of another
    /// kind, and a life retired while the type was being read all answer false.</summary>
    private bool MatchesEnemyType(string? category, string reference, EntityReference subject)
    {
        // The kind names a type, never an address: a category is not this mount's to interpret.
        if (category != null || _enemyType == null || !TryEnemyTypeId(reference, out var declared)) return false;
        var entry = Resolve(subject);
        if (entry == null || _enemyType(entry.Enemy) != declared) return false;
        // The block getter is native: a callback may retire the life or replace the instance while it is read.
        return Resolve(subject) == entry;
    }

    /// <summary>Strict reference grammar: decimal digits that parse to a `uint` and are that value's own
    /// canonical text, so `007`, `+7`, `-7`, whitespace and anything above `uint.MaxValue` are not spellings of
    /// a type. `NumberStyles.None` already refuses a sign and surrounding space; the round trip refuses a
    /// leading zero, which would be a second spelling of one id.</summary>
    private static bool TryEnemyTypeId(string reference, out uint id)
        => uint.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && reference == id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The kernel's instance resolver for `gtfo.enemy`: a native agent is only ever answered for the
    /// life the module registered, and tracing through the other kinds stays with their own providers.</summary>
    private EntityReference? ResolveInstance(object instance)
        => instance is EnemyAgent enemy && _entities.TryGetValue(enemy.GlobalID, out var entry)
           && ReferenceEquals(entry.Enemy, enemy) && entry.EnemyPointer == enemy.Pointer
           && Resolve(entry.Reference) != null ? entry.Reference : null;

    private RuntimeEntitySnapshot? ObserveEntity(EntityReference reference)
    {        if (_observe == null || _kernel.StartupState != RuntimeStartupState.Ready || !CanExecute) return null;
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

    /// <summary>One row of the multi-target damage result. The field names are the wire contract's, not this
    /// assembly's: `forge.result.combat.damage` declares target, status, committed, code, amount and target_count,
    /// so each is spelled here exactly as the contract spells it.</summary>
    private sealed record DamageRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("amount")] double RequestedAmount,
        [property: JsonPropertyName("target_count")] int TargetCount,
        float? HealthBefore, float? HealthAfter);

    /// <summary>The damage handler's own ports, resolved once at registration against `forge.action.combat.damage`.
    /// `targets` is the recipient collection, whose whole declared width separates `source` from the rest.</summary>
    private static readonly HandlerShape DamagePorts = new HandlerShape()
        .Inputs("targets", "source", "instigator", "amount", "damage_kind", "limb").Outputs("result").Parameters("mitigation_policy");

    /// <summary>Multi-target damage. One row is written per recipient in the plan's own order, and a hit whose
    /// effect cannot be read back is reported as an unknown commit rather than as success: the frozen native
    /// evidence shows that a rejected hit and a hit the receiver rules reduce to nothing are indistinguishable
    /// from the submitting side, so neither may be claimed. The host is the only authority that may submit: the
    /// native local-application gate is not an authority gate for this receiver type, so a client that reached the
    /// entry point would apply the hit locally, and `SNet.IsMaster` is what keeps that from happening.</summary>
    private CommandResult Damage(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        var policy = context.Parameters.GetProperty("mitigation_policy").GetString();
        // The receiver's own rules are the only mitigation this provider can submit; an armour-ignoring or
        // explicit-profile hit would need a damage channel the frozen entry point does not expose.
        if (policy != "receiver_rules") return CommandResult.Rejected("mitigation-policy-unsupported");
        // Both roles are required entity references and are kernel-validated. The native entry point carries one
        // attacker and this provider cannot name the native object behind another provider's reference, so the
        // write path submits no attacker instead of inventing one.
        _ = context.GetEntityInput("source");
        _ = context.GetEntityInput("instigator");
        double requested = context.Inputs.GetProperty("amount").GetDouble();
        if (!double.IsFinite(requested) || requested < DamageMinimumAmount || requested > DamageMaximumAmount)
            return CommandResult.Rejected("amount-out-of-range");
        // Every kind in the declared set is submitted through the one decoded entry point: the game's other damage
        // entry points differ in the multipliers and the packet they pack, and this action has no ports for those.
        // An index outside the declared set is still refused rather than ignored.
        int kind = context.Inputs.GetProperty("damage_kind").GetInt32();
        if (kind < 0 || kind >= DamageKindCount) return CommandResult.Rejected("damage-kind-unsupported");
        int limb = -1;
        if (context.Inputs.TryGetProperty("limb", out var limbElement))
        {
            // An omitted optional port arrives as null; only a number can name a limb.
            if (limbElement.ValueKind != JsonValueKind.Number || !limbElement.TryGetInt32(out limb) || limb < -1)
                return CommandResult.Rejected("invalid-limb");
        }
        var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        // The rows are the whole result, and the protocol's own budget caps the answer.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<DamageRow>(targets.Length);
        int committed = 0, rejected = 0, unknown = 0;
        foreach (var target in targets)
        {
            // The row's own status is the ABI's `committed` column: only a landed hit confirmed it, a refusal
            // confirmed nothing, and a hit this side could not observe afterwards stays unknown.
            DamageRow Row(string status, string code, float? before = null, float? after = null)
                => new(target, status, status switch
                {
                    "committed" => CommitStates.Confirmed,
                    "rejected" => CommitStates.None,
                    _ => CommitStates.Unknown
                }, code, requested, targets.Length, before, after);

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
            if (!EnemyNativeWrite.TryNameLimb(damage, limb, out int limbIndex))
            { rows.Add(Row("rejected", "invalid-limb", before)); rejected++; continue; }

            // Entering the native entry point is the only place the world is written. The hit is one call and the
            // readback is its only proof, so a failure after it stays unknown rather than being retried.
            try { EnemyNativeWrite.ApplyDamage(damage, limbIndex, (float)requested); }
            catch (Exception) { rows.Add(Row("unknown", "native-commit-exception", before)); unknown++; continue; }
            float after;
            try
            {
                if (!CanExecute) { rows.Add(Row("unknown", "authority-or-phase", before)); unknown++; continue; }
                var current = Resolve(target);
                if (current == null || !ReferenceEquals(current, entry) || enemy.Damage == null
                    || enemy.Damage.Pointer != receiverPointer || !enemy.Damage.IsSetup
                    || enemy.Damage.Owner == null || enemy.Damage.Owner.Pointer != entry.EnemyPointer
                    || enemy.Damage.HealthMax != maximum)
                { rows.Add(Row("unknown", "receiver-changed-during-commit", before)); unknown++; continue; }
                after = enemy.Damage.Health;
                if (!float.IsFinite(after) || after < 0 || after > maximum)
                { rows.Add(Row("unknown", "unexpected-health-readback", before)); unknown++; continue; }
            }
            catch (Exception) { rows.Add(Row("unknown", "readback-exception", before)); unknown++; continue; }
            // `after > before` can only be the receiver's own rules responding to the hit; the requested amount is
            // never reported as the committed one.
            if (after > before) { rows.Add(Row("unknown", "unexpected-health-rise", before, after)); unknown++; continue; }
            // A hit that moved no health is either a rejected hit or one the receivers' rules nullified, and this
            // side of the native call cannot tell those apart. The attempt is real, the effect is not observable.
            if (after == before) { rows.Add(Row("unknown", "damage-unseen", before, after)); unknown++; continue; }
            rows.Add(Row("committed", "committed", before, after));
            committed++;
        }

        var outputs = RuntimeJson.From(new { results = rows });
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
        {
            string code = rows.Count == 1 ? rows[0].Code
                : rows.Select(r => r.Code).Distinct().Count() == 1 ? rows[0].Code : "damage-all-rejected";
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        }
        return CommandResult.FailedUnknown(outputs, rows.Count == 1 ? rows[0].Code : "damage-all-unknown");
    }

    /// <summary>The heal handler's own ports, resolved once at registration against `forge.action.combat.heal`.
    /// `targets` is the recipient collection, whose whole declared width separates `source` from the rest.</summary>
    private static readonly HandlerShape HealPorts = new HandlerShape()
        .Inputs("targets", "source", "amount", "cap").Outputs("result").Parameters("overheal_policy");

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
        // Aggregation branches on whether any row committed, not on unknown==0/facts.Count==0. A committed
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
}
