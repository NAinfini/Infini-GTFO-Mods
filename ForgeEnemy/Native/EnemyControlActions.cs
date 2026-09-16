using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Enemies;
using ForgeRuntime.Framework;
using SNetwork;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeEnemy.Native;

/// <summary>Host-authoritative enemy control actions: the enemy a plan asks about is the one this module already
/// tracks, and each action submits exactly one native entry point the game itself calls.
///
/// The three rows implemented here are `forge.action.enemy.awaken` (ES_HibernateWakeUp.ActivateState),
/// `forge.action.enemy.sleep` (EnemyBehaviour.ChangeState(EB_States.Hibernating)) and
/// `forge.action.enemy.move_to` (EnemyAI.NavmeshAgentGoal). Each is a member of the enemy's own state machine or
/// AI, so the write is a transition the game's own replication already carries:
/// EnemyBehaviour.UpdateState calls EnemySync.ReplicateAIState (0x1568038), which reaches UpdateStateData
/// (0x15810F9) and TryCollectCaptureData (0x15813CB), and a client applies the replicated behaviour state through
/// EnemySync.IncomingState -> EnemyBehaviour.ChangeState (0x1580A6A). The locomotion state a wake enters is
/// replicated the same way: ES_HibernateWakeUp.RecieveStateData forces it on a client (0x17FDC6F).
///
/// Three rows of this family are not implemented, and no partial version of them is offered:
/// `forge.action.enemy.target_set` (it must answer with a `lease` handle), `forge.action.enemy.target_clear` and
/// `forge.action.enemy.state_request` (both consume `lease` handles). The runtime's handle pool is the kernel's:
/// `RuntimeKernel.CreateHandle` is private and `RuntimeModuleHandle` exposes only Publish/CancelScope/Dispose, so
/// a provider cannot mint the handle its own row declares nor read one back. A handle this assembly invented
/// would be a second handle namespace the kernel does not check, which is worse than reporting the gap, so the
/// gap is reported instead (see `ForgeEnemy/evidence/enemy-control-actions.json`).
///
/// What the native paths cannot express is refused by name rather than ignored: an alert amount the wake call has
/// no parameter for, a wake direction the caller's `source` reference cannot be resolved into, a sleep the game
/// has no timer for, and an `area`/`arrival_tolerance` the navigation contract has no member for.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The declared `wake_policy` members. `gradual` has no native parameter: the wake call plays one
    /// wake-up with one delay, so a request for a gradual wake is refused rather than served as an immediate
    /// one.</summary>
    private const string WakeImmediate = "immediate";
    /// <summary>The declared `sleep_policy` members. `when_idle` is the one this provider can check natively and
    /// still honour, so it is served; nothing else is accepted.</summary>
    private const string SleepImmediate = "immediate";
    private const string SleepWhenIdle = "when_idle";
    /// <summary>The declared `interrupt_policy` members. `damage_only` asks for a wake filter the hibernation
    /// state does not expose as a setting: the game decides what interrupts a sleeper, so the whole command is
    /// refused instead of quietly installing the `any` behaviour.</summary>
    private const string InterruptAny = "any";

    /// <summary>Native `EB_States.Hibernating` (0) and `EB_States.SquidBoss_Hibernating` (12). The same pair the
    /// behaviour facts treat as the sleeping baseline, so `awakened` and this action agree on what sleep is.</summary>
    private const int NativeHibernating = 0;
    private const int NativeSquidBossHibernating = 12;
    /// <summary>Native `EB_States.Patrolling` (1) and `ES_StateEnum.HibernateWakeUp` (15): the state a sleeping
    /// enemy's behaviour machine holds and the locomotion state the wake call enters.</summary>
    private const int NativePatrolling = 1;
    private const int NativeHibernateWakeUp = 15;
    /// <summary>Native `AgentMode.Patrolling` (2): with no valid target, this is the game's own idle.</summary>
    private const int NativeModePatrolling = 2;
    private const double MinimumDestinationComponent = -100000;
    private const double MaximumDestinationComponent = 100000;
    private const double MinimumSpeed = 0.01;
    private const double MaximumSpeed = 1000;

    /// <summary>One target's line in an action's result, in the row order the catalog declares: the four fixed
    /// columns first, then that action's own fields. Field names are the wire contract's, spelled here exactly as
    /// the catalog spells them.</summary>
    private sealed record AwakenRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("alert_amount")] double AlertAmount, [property: JsonPropertyName("target_count")] int TargetCount);
    private sealed record SleepRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("duration")] int Duration, [property: JsonPropertyName("target_count")] int TargetCount);
    private sealed record MoveToRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("speed")] double? Speed, [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>What one native submission did. The commit state is the ABI's own column, and `committed` is the
    /// only code that claims the world moved.</summary>
    private readonly record struct Outcome(string Status, string Committed, string Code)
    {
        internal static Outcome Confirmed() => new(CommandStatuses.Succeeded, CommitStates.Confirmed, "committed");
        internal static Outcome Refused(string code) => new(CommandStatuses.Rejected, CommitStates.None, code);
        internal static Outcome Unseen(string code) => new(CommandStatuses.Failed, CommitStates.Unknown, code);
    }

    private static bool IsSleepingState(int state) => state is NativeHibernating or NativeSquidBossHibernating;

    /// <summary>The wake-up the catalog asks for. `ES_HibernateWakeUp.ActivateState` is the game's own entry: its
    /// three callers are all hibernation detection paths (EB_Hibernating.UpdateDetection 0x166CC65,
    /// EB_Hibernating.OnNoiseDetected 0x166C2BB and 0x166C124), so what this submits is the transition the game
    /// makes when something wakes an enemy.
    ///
    /// The request is validated before the first target, because a wake that cannot be carried out as asked must
    /// not half-apply: `gradual` has no parameter on the native call, a caller-supplied alert amount has nowhere
    /// to go, and the caller's `source` reference is not a native agent this provider can resolve. The wake call
    /// itself is then given the enemy's own facing and no propagation, which is what "wake this enemy" means
    /// without the direction a noise source would have supplied.
    ///
    /// The call applies the wake immediately, so a later rejection cannot un-wake it: every failure after the
    /// call is reported as an unknown commit rather than as a refusal, and only a read of the locomotion state
    /// the game entered confirms the commit.</summary>
    internal CommandResult Awaken(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        var policy = context.Parameters.GetProperty("wake_policy").GetString();
        if (policy != WakeImmediate) return CommandResult.Rejected("wake-policy-unsupported");
        if (HasValue(context.Inputs, "source")) return CommandResult.Rejected("source-unsupported");
        double alertAmount = 0;
        if (context.Inputs.TryGetProperty("alert_amount", out var alert) && alert.ValueKind != JsonValueKind.Null)
        {
            if (alert.ValueKind != JsonValueKind.Number || !alert.TryGetDouble(out alertAmount) || !double.IsFinite(alertAmount))
                return CommandResult.Rejected("alert-amount-out-of-range");
            if (alertAmount != 0) return CommandResult.Rejected("alert-amount-unsupported");
        }
        _ = context.Inputs.GetProperty("reason");
        var targets = context.Inputs.GetProperty("enemies").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<AwakenRow>(targets.Length);
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            AwakenRow Row(Outcome outcome) => new(target, outcome.Status, outcome.Committed, outcome.Code, alertAmount, targets.Length);
            if (stopCommitting) { rows.Add(Row(Outcome.Refused("not-attempted-after-unknown-commit"))); continue; }
            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row(Outcome.Refused("stale-or-unsupported-recipient"))); continue; }
            var enemy = entry.Enemy;
            // The behaviour machine, not the locomotion machine, owns the wake: the game's own wake path ends in
            // EnemyBehaviour.ChangeState, so the behaviour state decides whether an enemy is still asleep.
            EnemyBehaviour? behaviour;
            EnemyLocomotion? locomotion;
            try
            {
                if (!enemy.Alive) { rows.Add(Row(Outcome.Refused("not-alive"))); continue; }
                var ai = enemy.AI;
                behaviour = ai?.m_behaviour;
                locomotion = ai?.m_locomotion;
            }
            catch (Exception) { rows.Add(Row(Outcome.Refused("behavior-unavailable"))); continue; }
            if (behaviour == null || locomotion == null) { rows.Add(Row(Outcome.Refused("behavior-unavailable"))); continue; }
            var behaviourPointer = behaviour.Pointer;
            var locomotionPointer = locomotion.Pointer;
            int before;
            ES_HibernateWakeUp? wakeup;
            try
            {
                before = (int)behaviour.m_currentStateName;
                wakeup = locomotion.HibernateWakeup;
            }
            catch (Exception) { rows.Add(Row(Outcome.Refused("behavior-unavailable"))); continue; }
            if (!IsSleepingState(before)) { rows.Add(Row(Outcome.Refused("not-hibernating"))); continue; }
            if (wakeup == null || wakeup.Pointer == IntPtr.Zero) { rows.Add(Row(Outcome.Refused("wake-receiver-missing"))); continue; }

            var called = true;
            // The native call is the only place the world is written. Whatever it did, a failure observed after
            // it stays unknown, and the entry stops committing further targets. No direction is supplied: the
            // wake has no noise source behind it, and the native signature takes the absent direction as the
            // zero vector (its own turn test is a dot product with the enemy's forward).
            try { wakeup.ActivateState(Vector3.zero, 0f, 0f, false); }
            catch (Exception) { rows.Add(Row(Outcome.Unseen("native-commit-exception"))); stopCommitting = true; called = false; }
            if (!called) continue;

            try
            {
                if (!CanExecute) { rows.Add(Row(Outcome.Unseen("authority-or-phase"))); stopCommitting = true; continue; }
                var current = Resolve(target);
                if (current == null || !ReferenceEquals(current, entry) || behaviour.Pointer != behaviourPointer
                    || locomotion.Pointer != locomotionPointer)
                { rows.Add(Row(Outcome.Unseen("receiver-changed-during-commit"))); stopCommitting = true; continue; }
                // The wake is committed when the game's own locomotion machine reports the state the wake call
                // enters. The behaviour machine follows through ES_HibernateWakeUp.Exit, a frame later, so it is
                // not the readback this action waits for.
                if ((int)locomotion.CurrentStateEnum != NativeHibernateWakeUp)
                { rows.Add(Row(Outcome.Unseen("wake-unseen"))); stopCommitting = true; continue; }
            }
            catch (Exception) { rows.Add(Row(Outcome.Unseen("readback-exception"))); stopCommitting = true; continue; }
            rows.Add(Row(Outcome.Confirmed()));
        }
        return Aggregate(rows.Select(row => row.Committed switch
        {
            CommitStates.Confirmed => Outcome.Confirmed(),
            CommitStates.None => Outcome.Refused(row.Code),
            _ => Outcome.Unseen(row.Code)
        }).ToArray(), RuntimeJson.From(new { results = rows }), "awaken");
    }

    /// <summary>The sleep the catalog asks for, submitted as the one native transition that means it:
    /// `EnemyBehaviour.ChangeState(EB_States.Hibernating)`. `ChangeState` writes `m_currentStateName` only when
    /// the incoming state differs (0x1567380), so the write is a real transition, and the behaviour state it
    /// writes is what `EnemySync` replicates and what the awakened fact samples.
    ///
    /// A sleep the game has no timer for is refused by name rather than faked: `duration` is a plan-side
    /// composition (`delay` then `awaken`), not a native field, and `interrupt_policy: damage_only` asks for a
    /// filter the hibernation state does not expose. `sleep_policy: when_idle` is served by checking the game's
    /// own idle before writing, so an enemy that is fighting is refused instead of being frozen mid-attack.
    ///
    /// The commit is confirmed by reading the state back; a state the machine did not take is an unknown commit,
    /// never a claimed one.</summary>
    internal CommandResult Sleep(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        var policy = context.Parameters.GetProperty("sleep_policy").GetString();
        if (policy is not (SleepImmediate or SleepWhenIdle)) return CommandResult.Rejected("sleep-policy-unsupported");
        var interrupt = context.Parameters.GetProperty("interrupt_policy").GetString();
        if (interrupt != InterruptAny) return CommandResult.Rejected("interrupt-policy-unsupported");
        int duration = 0;
        if (context.Inputs.TryGetProperty("duration", out var durationValue) && durationValue.ValueKind != JsonValueKind.Null)
        {
            if (durationValue.ValueKind != JsonValueKind.Number || !durationValue.TryGetInt32(out duration) || duration < 0)
                return CommandResult.Rejected("duration-out-of-range");
            if (duration != 0) return CommandResult.Rejected("duration-unsupported");
        }
        var targets = context.Inputs.GetProperty("enemies").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<SleepRow>(targets.Length);
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            SleepRow Row(Outcome outcome) => new(target, outcome.Status, outcome.Committed, outcome.Code, duration, targets.Length);
            if (stopCommitting) { rows.Add(Row(Outcome.Refused("not-attempted-after-unknown-commit"))); continue; }
            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row(Outcome.Refused("stale-or-unsupported-recipient"))); continue; }
            var enemy = entry.Enemy;
            EnemyBehaviour? behaviour;
            EnemyLocomotion? locomotion;
            try
            {
                if (!enemy.Alive) { rows.Add(Row(Outcome.Refused("not-alive"))); continue; }
                var ai = enemy.AI;
                behaviour = ai?.m_behaviour;
                locomotion = ai?.m_locomotion;
            }
            catch (Exception) { rows.Add(Row(Outcome.Refused("behavior-unavailable"))); continue; }
            if (behaviour == null) { rows.Add(Row(Outcome.Refused("behavior-unavailable"))); continue; }
            var behaviourPointer = behaviour.Pointer;
            var locomotionPointer = locomotion?.Pointer ?? IntPtr.Zero;
            int before;
            try { before = (int)behaviour.m_currentStateName; }
            catch (Exception) { rows.Add(Row(Outcome.Refused("behavior-unavailable"))); continue; }
            if (before == NativeHibernating) { rows.Add(Row(Outcome.Refused("already-hibernating"))); continue; }
            // The boss has its own hibernation state and its own scripted schedule; sending it to the ordinary
            // sleeper would leave the two machines disagreeing about which sleep this is.
            if (before == NativeSquidBossHibernating) { rows.Add(Row(Outcome.Refused("boss-hibernation-unsupported"))); continue; }

            if (policy == SleepWhenIdle)
            {
                bool idle;
                try
                {
                    var ai = behaviour.m_ai;
                    var detection = ai?.m_detection;
                    idle = ai != null && (int)ai.m_mode == NativeModePatrolling && !ai.IsTargetValid
                        && detection != null && detection.m_biggestDetectionBuildup <= 0;
                }
                catch (Exception) { rows.Add(Row(Outcome.Refused("behavior-unavailable"))); continue; }
                if (!idle) { rows.Add(Row(Outcome.Refused("not-idle"))); continue; }
            }

            var called = true;
            try { behaviour.ChangeState(EB_States.Hibernating); }
            catch (Exception) { rows.Add(Row(Outcome.Unseen("native-commit-exception"))); stopCommitting = true; called = false; }
            if (!called) continue;

            try
            {
                if (!CanExecute) { rows.Add(Row(Outcome.Unseen("authority-or-phase"))); stopCommitting = true; continue; }
                var current = Resolve(target);
                if (current == null || !ReferenceEquals(current, entry) || behaviour.Pointer != behaviourPointer
                    || (locomotion != null && locomotion.Pointer != locomotionPointer))
                { rows.Add(Row(Outcome.Unseen("receiver-changed-during-commit"))); stopCommitting = true; continue; }
                if ((int)behaviour.m_currentStateName != NativeHibernating)
                { rows.Add(Row(Outcome.Unseen("sleep-unseen"))); stopCommitting = true; continue; }
            }
            catch (Exception) { rows.Add(Row(Outcome.Unseen("readback-exception"))); stopCommitting = true; continue; }
            rows.Add(Row(Outcome.Confirmed()));
        }
        return Aggregate(rows.Select(row => row.Committed switch
        {
            CommitStates.Confirmed => Outcome.Confirmed(),
            CommitStates.None => Outcome.Refused(row.Code),
            _ => Outcome.Unseen(row.Code)
        }).ToArray(), RuntimeJson.From(new { results = rows }), "sleep");
    }

    /// <summary>The navigation request, submitted as `EnemyAI.NavmeshAgentGoal` — the member every native move
    /// behaviour writes (EB_InCombat_MoveToTarget 0x17F0457, EB_Patrolling 0x17F2570/0x17F25DC/0x17F2744,
    /// EB_Patrolling_Investigate 0x17F184B/0x17F1B22, EB_InCombat_MoveToPoint 0x1671121 and the flyer and
    /// pouncer move states). Writing it is how the game itself tells an enemy where to walk, and the AI that
    /// consumes it is the host's, so the movement itself is host-simulated and replicated as path data.
    ///
    /// The two inputs the navigation contract cannot carry are refused by name: `area` is a Forge resource
    /// reference with no native member behind it, and `arrival_tolerance` is not a value the game's agent keeps —
    /// a tolerance that silently did nothing would be a claim this action cannot keep. What the agent does keep
    /// is `speed`, so a requested speed is applied and read back.
    ///
    /// The goal is written before it is read back; a write that did not take is an unknown commit, never a
    /// claimed one, and the entry then stops committing further targets.</summary>
    internal CommandResult MoveTo(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        if (HasValue(context.Inputs, "area")) return CommandResult.Rejected("area-unsupported");
        if (HasValue(context.Inputs, "arrival_tolerance")) return CommandResult.Rejected("arrival-tolerance-unsupported");
        if (!context.Inputs.TryGetProperty("destination", out var destinationValue) || destinationValue.ValueKind != JsonValueKind.Array
            || !TryVector(destinationValue, out var destination))
            return CommandResult.Rejected("invalid-destination");
        double? speed = null;
        if (context.Inputs.TryGetProperty("speed", out var speedValue) && speedValue.ValueKind != JsonValueKind.Null)
        {
            if (speedValue.ValueKind != JsonValueKind.Number || !speedValue.TryGetDouble(out var requested)
                || !double.IsFinite(requested) || requested < MinimumSpeed || requested > MaximumSpeed)
                return CommandResult.Rejected("speed-out-of-range");
            speed = requested;
        }
        var targets = context.Inputs.GetProperty("enemies").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<MoveToRow>(targets.Length);
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            MoveToRow Row(Outcome outcome, double? applied = null) => new(target, outcome.Status, outcome.Committed, outcome.Code, applied, targets.Length);
            if (stopCommitting) { rows.Add(Row(Outcome.Refused("not-attempted-after-unknown-commit"))); continue; }
            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row(Outcome.Refused("stale-or-unsupported-recipient"))); continue; }
            var enemy = entry.Enemy;
            EnemyAI? ai;
            try
            {
                if (!enemy.Alive) { rows.Add(Row(Outcome.Refused("not-alive"))); continue; }
                ai = enemy.AI;
            }
            catch (Exception) { rows.Add(Row(Outcome.Refused("navigation-unavailable"))); continue; }
            if (ai == null) { rows.Add(Row(Outcome.Refused("navigation-unavailable"))); continue; }
            var aiPointer = ai.Pointer;
            var goal = new Vector3((float)destination.X, (float)destination.Y, (float)destination.Z);
            // A requested speed is a second write through the agent's own navigation interface, so the receiver
            // is checked before anything is written: an agent without one refuses the whole target rather than
            // applying half of what was asked.
            INavigation? navigation;
            try { navigation = ai.m_navMeshAgent; }
            catch (Exception) { rows.Add(Row(Outcome.Refused("navigation-unavailable"))); continue; }
            if (speed.HasValue && navigation == null) { rows.Add(Row(Outcome.Refused("navigation-unavailable"))); continue; }
            var navigationPointer = navigation?.Pointer ?? IntPtr.Zero;

            var called = true;
            try
            {
                ai.NavmeshAgentGoal = goal;
                if (speed.HasValue && navigation != null) navigation.speed = (float)speed.Value;
            }
            catch (Exception) { rows.Add(Row(Outcome.Unseen("native-commit-exception"))); stopCommitting = true; called = false; }
            if (!called) continue;

            try
            {
                if (!CanExecute) { rows.Add(Row(Outcome.Unseen("authority-or-phase"))); stopCommitting = true; continue; }
                var current = Resolve(target);
                if (current == null || !ReferenceEquals(current, entry) || ai.Pointer != aiPointer
                    || (navigation != null && navigation.Pointer != navigationPointer))
                { rows.Add(Row(Outcome.Unseen("receiver-changed-during-commit"))); stopCommitting = true; continue; }
                // The goal is what this action writes, so the goal is what it reads back: a value the AI did not
                // keep, or kept as something else, is not a movement the host ever asked for.
                var read = ai.NavmeshAgentGoal;
                if (!float.IsFinite(read.x) || !float.IsFinite(read.y) || !float.IsFinite(read.z)
                    || read.x != goal.x || read.y != goal.y || read.z != goal.z)
                { rows.Add(Row(Outcome.Unseen("unexpected-goal-readback"))); stopCommitting = true; continue; }
                double? applied = null;
                if (speed.HasValue && navigation != null)
                {
                    double currentSpeed = navigation.speed;
                    if (!double.IsFinite(currentSpeed) || Math.Abs(currentSpeed - speed.Value) > 0.001)
                    { rows.Add(Row(Outcome.Unseen("unexpected-speed-readback"))); stopCommitting = true; continue; }
                    applied = currentSpeed;
                }
                rows.Add(Row(Outcome.Confirmed(), applied));
            }
            catch (Exception) { rows.Add(Row(Outcome.Unseen("readback-exception"))); stopCommitting = true; continue; }
        }
        return Aggregate(rows.Select(row => row.Committed switch
        {
            CommitStates.Confirmed => Outcome.Confirmed(),
            CommitStates.None => Outcome.Refused(row.Code),
            _ => Outcome.Unseen(row.Code)
        }).ToArray(), RuntimeJson.From(new { results = rows }), "move_to");
    }

    /// <summary>One command's conclusion, by the rule every multi-target action in this repository uses:
    /// everything confirmed is a success, nothing confirmed is a rejection or an unknown failure, and anything in
    /// between is partial with the weaker commit state. `action` names the fallback code so a run of identical
    /// refusals reports the refusal itself.</summary>
    private static CommandResult Aggregate(IReadOnlyList<Outcome> outcomes, JsonElement outputs, string action)
    {
        int committed = 0, unknown = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome.Committed == CommitStates.Confirmed) committed++;
            else if (outcome.Committed == CommitStates.Unknown) unknown++;
        }
        if (committed == outcomes.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(outcomes, action + "-all-rejected"), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(outcomes, action + "-all-unknown"), "", outputs);
    }

    private static string Single(IReadOnlyList<Outcome> outcomes, string fallback)
    {
        if (outcomes.Count == 1) return outcomes[0].Code;
        var first = outcomes[0].Code;
        foreach (var outcome in outcomes) if (outcome.Code != first) return fallback;
        return first;
    }

    /// <summary>An input the plan actually supplied: absent, or a present null, is the same "not asked for".</summary>
    private static bool HasValue(JsonElement inputs, string port)
        => inputs.TryGetProperty(port, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool TryVector(JsonElement value, out (double X, double Y, double Z) vector)
    {
        vector = default;
        if (value.GetArrayLength() != 3) return false;
        Span<double> components = stackalloc double[3];
        var index = 0;
        foreach (var component in value.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out var number)
                || !double.IsFinite(number) || number < MinimumDestinationComponent || number > MaximumDestinationComponent)
                return false;
            components[index++] = number;
        }
        vector = (components[0], components[1], components[2]);
        return true;
    }
}
