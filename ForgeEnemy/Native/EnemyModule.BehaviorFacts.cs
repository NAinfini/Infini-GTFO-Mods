using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>Host authority gate for every AI behaviour fact. The pump is a native frame hook, so nothing here
/// writes the world: it only reads one registered enemy and publishes the transitions it observed.</summary>
internal sealed partial class EnemyModule
{
    internal const string AwakenedBinding = ProviderId + ".binding.awakened";
    internal const string TargetAcquiredBinding = ProviderId + ".binding.target_acquired";
    internal const string TargetLostBinding = ProviderId + ".binding.target_lost";
    internal const string ScoutDetectionBinding = ProviderId + ".binding.scout_detection";
    internal const string ScoutScreamBinding = ProviderId + ".binding.scout_scream";

    /// <summary>Every binding this file publishes; the frame pump is skipped while none of them has a plan. The
    /// target pair is part of the list for the same reason as the rest: a fact a plan subscribed to is a reason
    /// to read the AI, and a fact omitted here would be published never instead of once.</summary>
    internal static readonly string[] BehaviorBindings =
    {
        AwakenedBinding, TargetAcquiredBinding, TargetLostBinding, ScoutDetectionBinding, ScoutScreamBinding
    };

    /// <summary>Native `EB_States.Hibernating` (0) and `EB_States.SquidBoss_Hibernating` (12): the sleeping
    /// baselines the awakened fact leaves. Both sides of the transition are tested, so hibernating to
    /// hibernating — including the boss variant — is not a wake-up.</summary>
    private static bool Hibernating(int state) => state is 0 or 12;

    /// <summary>Per-life behaviour watermarks. They live with the entry, so a despawn, a new life or a world
    /// change drops them and the next life starts from its own observed state.</summary>
    private sealed class Behavior
    {
        internal int State;
        internal int ScoutPhase = -1;
        /// <summary>The life's last observed target lock: whether the AI held a valid target, and which entity it
        /// held. The reference is the one the kernel resolved for the sample, or null when the sample's target was
        /// not nameable; both halves are watermarks, so a lock that survives a frame publishes nothing.</summary>
        internal bool TargetValid;
        internal EntityReference? Target;

        /// <summary>The state an enemy is first seen in is a baseline, not a transition: a life that never
        /// changes state publishes nothing, and the first state change is a real one.</summary>
        internal Behavior(EnemyAgent enemy, EntityReference reference)
        {
            if (EnemyBehaviorFactsObserver.Read(enemy, reference) is not { } sample) return;
            State = sample.BehaviourState;
            ScoutPhase = sample.ScoutScreamPhase;
            TargetValid = sample.HasValidTarget;
        }
    }

    /// <summary>Native frame entry (EnemyDetection.UpdateTargets): one reading per registered enemy per frame.</summary>
    internal void ObserveEnemyBehavior(EnemyAI ai)
    {
        CheckThread();
        if (!CanPublishBehavior || ai == null) return;
        var enemy = ai.m_enemyAgent;
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry)
            || !ReferenceEquals(entry.Enemy, enemy)) return;
        var behavior = entry.Behavior;
        var entryPointer = entry.EnemyPointer; var reference = entry.Reference;
        var sample = EnemyBehaviorFactsObserver.Read(enemy, reference);
        // A different life, a stale registration or a changed instance is not a fact.
        if (sample == null || sample.Reference != reference || Resolve(reference) != entry
            || entry.EnemyPointer != entryPointer) return;
        ObserveStateChange(entry, behavior, sample);
        ObserveTargetChange(reference, behavior, sample);
        ObserveScoutScream(reference, behavior, sample);
    }

    /// <summary>Native frame entry (ES_ScoutDetection.OnTargetRegistered). The scout has already registered the
    /// target natively; this only reports it, with the position the native target carries.</summary>
    internal void ObserveScoutDetection(EnemyAgent? scout, AgentTarget? target)
    {
        CheckThread();
        if (!CanObserveFacts || scout == null || target == null) return;
        if (!_entities.TryGetValue(scout.GlobalID, out var entry) || !ReferenceEquals(entry.Enemy, scout)) return;
        var position = target.m_position;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return;
        var ports = Ports();
        ports["enemy"] = entry.Reference;
        // An unresolved target is an absent port, never a null reference standing in for a real one.
        if (ResolveAgent(target.m_agent) is { } resolved) ports["target"] = resolved;
        ports["position"] = new[] { (double)position.x, (double)position.y, (double)position.z };
        PublishBehavior(ScoutDetectionBinding, entry.Reference, ports);
    }

    /// <summary>The one state edge this module still publishes: leaving a hibernating state is the wake-up. The
    /// per-life state watermark is what decides it, so a life that never leaves hibernation publishes nothing and
    /// a state change between two awake states is not a fact here.</summary>
    private void ObserveStateChange(Entry entry, Behavior behavior, EnemyBehaviorFactsObserver.Sample sample)
    {
        if (sample.BehaviourState == behavior.State) return;
        if (Hibernating(behavior.State) && !Hibernating(sample.BehaviourState))
        {
            // Wake-up itself carries no verifiable cause in the behaviour machine.
            var awake = Ports();
            awake["enemy"] = entry.Reference;
            PublishBehavior(AwakenedBinding, entry.Reference, awake);
        }
        behavior.State = sample.BehaviourState;
    }

    /// <summary>
    /// The target lock, published as two facts: an acquisition when the AI begins holding one, and a loss when it
    /// stops. The native validity flag is the fact — the game answers "is there a target" itself — while the
    /// entity is a port the kernel may fail to name, so a lock whose target belongs to no registered kind is
    /// still a lock and publishes without that port rather than being dropped. The watermark is the pair, so a
    /// held lock publishes once and only a real change publishes again: an unnameable lock that becomes a
    /// different unnameable lock is not a second acquisition of the same nothing. A lock that moves straight from
    /// one entity to another is that second acquisition and no loss in between — the facts are the two edges of
    /// the validity flag, and the port on the acquisition is what says which entity the new lock names.
    /// </summary>
    private void ObserveTargetChange(EntityReference reference, Behavior behavior, EnemyBehaviorFactsObserver.Sample sample)
    {
        var target = sample.HasValidTarget ? ResolveAgent(sample.Target?.m_agent) : null;
        if (sample.HasValidTarget == behavior.TargetValid && target == behavior.Target) return;
        bool acquired = sample.HasValidTarget && !behavior.TargetValid;
        behavior.TargetValid = sample.HasValidTarget;
        behavior.Target = target;
        // A changed target under a still-valid flag is a fresh acquisition of the new one; only the flag falling
        // is a loss.
        if (acquired || sample.HasValidTarget) PublishBehavior(TargetAcquiredBinding, reference, AcquiredPorts(reference, target));
        else PublishBehavior(TargetLostBinding, reference, AcquiredPorts(reference, null));
    }

    /// <summary>The acquisition's ports: the enemy the fact is about and, when the kernel could name it, the
    /// entity the lock holds. An unresolved target is an absent port, never a null reference standing in for a
    /// real one.</summary>
    private static Dictionary<string, object> AcquiredPorts(EntityReference reference, EntityReference? target)
    {
        var ports = Ports();
        ports["enemy"] = reference;
        if (target is { } resolved) ports["target"] = resolved;
        return ports;
    }

    private void ObserveScoutScream(EntityReference reference, Behavior behavior, EnemyBehaviorFactsObserver.Sample sample)
    {
        if (sample.ScoutScreamPhase < 0)
        {
            behavior.ScoutPhase = -1;
            return;
        }
        if (sample.ScoutScreamPhase == behavior.ScoutPhase) return;
        behavior.ScoutPhase = sample.ScoutScreamPhase;
        var ports = Ports();
        ports["enemy"] = reference;
        ports["phase"] = sample.ScoutScreamPhase;
        PublishBehavior(ScoutScreamBinding, reference, ports);
    }

    /// <summary>One native agent through the kernel's registered instance resolvers. The provider that owns a
    /// kind answers for its own instances; a kind with no resolver registered is skipped, and no kind is ever
    /// guessed from a pointer. An instance no registered kind claims stays unresolved.</summary>
    private EntityReference? ResolveAgent(Agent? agent)
    {
        if (agent == null) return null;
        foreach (var kind in AgentKinds)
        {
            EntityReference? reference;
            try { reference = _kernel.ResolveEntityInstance(kind, agent); }
            catch (RuntimeContractException error) when (error.Code == "entity-resolver") { continue; }
            if (reference != null) return reference;
        }
        return null;
    }

    private static readonly string[] AgentKinds = { "gtfo.enemy", "gtfo.player" };

    private void PublishBehavior(string binding, EntityReference subject, Dictionary<string, object> ports)
    {
        // One frame hook covers every behaviour fact, so a fact no plan subscribes to is never submitted.
        if (!CanObserveFacts || !_kernel.HasSubscribers(binding) || Resolve(subject) == null) return;
        var result = _registration.Publish(new RuntimeEvent(
            "gtfo.enemy.behavior:" + _kernel.WorldEpoch + ":" + checked(++_eventSequence), binding,
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + _kernel.WorldEpoch,
            RuntimeJson.From(ports)));
        // Runtime owns causal propagation and dispatch. Rejection must never reopen this native observation.
        if (result.Status == "rejected") _report("behavior fact rejected: " + result.Code);
    }

    // The execution output is the plan's, not the payload's: an event carries declared data ports only, and the
    // kernel's event shape check rejects any name the capability does not declare.
    private static Dictionary<string, object> Ports() => new(StringComparer.Ordinal);

}
