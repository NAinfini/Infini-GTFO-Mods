using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Enemies;
using ForgeRuntime.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>Host authority gate and native write paths for the two registered `C-enemy-combat` rows.
///
/// `forge.action.combat.stagger` writes through the game's one hitreact entry point,
/// `ES_HitreactBase.CanHitreact(ES_HitreactType, bool)` followed by
/// `ActivateState(ES_HitreactType, ImpactDirection, bool, Agent, Vector3, DamageNoiseLevel)`, which the game
/// reaches the same way from its own damage path. The reaction strength is a structural parameter on the row
/// because that entry point cannot omit it. The direction, the attacker agent and the noise level are the neutral
/// values: this shape has neither a direction nor an attacker port, and inventing either from another provider's
/// reference is exactly what this assembly refuses to do elsewhere.
///
/// `forge.action.combat.attack_interrupt` names its recipients directly — `enemies`, entities of the `gtfo.enemy`
/// kind — and interrupts whatever attack they are performing or charging. The attack is proven through the
/// locomotion's own attack states (`ES_EnemyAttackBase.IsPerformingAttack`/`IsChargingAttack`) and the interruption
/// itself is the same hitreact state machine `stagger` uses, because an attack state is left only by the state
/// machine that owns it. An enemy that is not mid-attack is refused with `enemy-not-attacking` and a `none` commit
/// state, before any native call: nothing there was interrupted and nothing may be claimed.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The declared `reaction` enum set's members, in the catalog's own order, as the `ES_HitreactType`
    /// values they name. `ToDeath` and `InstantRagdollDeath` are deliberately not members: those are kills and
    /// belong to `combat.damage`, not to a stagger.</summary>
    private static readonly ES_HitreactType[] StaggerReactions =
    {
        ES_HitreactType.Micro, ES_HitreactType.Light, ES_HitreactType.Heavy
    };
    private const int StaggerReactionCount = 3;

    /// <summary>Whether one reaction is a member of the declared set above. The incoming damage window reads the
    /// same three when it answers whether a received hit staggered its target, so the row that submits a stagger
    /// and the row that reports one cannot disagree about which reactions are stagger-class.</summary>
    internal static bool IsStaggerReaction(ES_HitreactType reaction)
    {
        foreach (var member in StaggerReactions) if (member == reaction) return true;
        return false;
    }

    /// <summary>The declared `immunity_policy` members, in the catalog's own order.</summary>
    private const int ImmunityPolicyRespect = 0;
    private const int ImmunityPolicyIgnore = 1;
    private const int ImmunityPolicyCount = 2;

    /// <summary>The one reaction an interruption is submitted with. The interrupt row has no reaction parameter:
    /// a caller interrupting an attack is not choosing how hard to hit, and `Light` is the member the game's own
    /// attack interruption leaves behind. The readback of `CurrentReactionType` is what proves the write landed.</summary>
    private const ES_HitreactType InterruptReaction = ES_HitreactType.Light;

    /// <summary>One row of the multi-target stagger result. Field names are the wire contract's, not this
    /// assembly's: `forge.result.combat.stagger` declares target, status, committed, code, reaction and
    /// target_count, so each is spelled here exactly as the contract spells it.</summary>
    private sealed record StaggerRow(EntityReference Target, string Status, string Committed, string Code,
        string Reaction, [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>One row of the multi-target attack-interrupt result. `forge.result.combat.attack_interrupt`
    /// declares the four shared columns and `target_count`, and nothing else: the interrupt carries no reaction
    /// strength and no reason string the caller chose.</summary>
    private sealed record AttackInterruptRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>Multi-target stagger. One row is written per recipient in the plan's own order. The hitreact
    /// entry point returns void, so the readback of `CurrentReactionType` is the only proof a write landed; a
    /// submission whose readback disagrees stays unknown rather than being claimed, and every refusal before the
    /// native call is a rejected row.</summary>
    internal CommandResult Stagger(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        int reactionIndex = context.Parameters.GetProperty("reaction").GetInt32();
        if (reactionIndex < 0 || reactionIndex >= StaggerReactionCount) return CommandResult.Rejected("reaction-unsupported");
        int immunity = context.Parameters.GetProperty("immunity_policy").GetInt32();
        if (immunity < 0 || immunity >= ImmunityPolicyCount) return CommandResult.Rejected("immunity-policy-unsupported");
        // `source` is a required, kernel-validated role. The hitreact entry point takes a native `Agent` attacker
        // and this provider cannot name the native object behind another provider's reference, so no attacker is
        // passed instead of one being invented; the role is still read so a row missing it is refused by the
        // kernel before this handler runs.
        _ = context.GetEntityInput("source");
        var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        // The rows are the whole result, and the protocol's own budget caps the answer.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var reaction = StaggerReactions[reactionIndex];
        string reactionName = StaggerReactionName(reactionIndex);
        var rows = new List<StaggerRow>(targets.Length);
        int committed = 0, rejected = 0, unknown = 0;
        foreach (var target in targets)
        {
            StaggerRow Row(string status, string code)
                => new(target, status, status switch
                {
                    "committed" => CommitStates.Confirmed,
                    "rejected" => CommitStates.None,
                    _ => CommitStates.Unknown
                }, code, reactionName, targets.Length);

            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row("rejected", "stale-or-unsupported-recipient")); rejected++; continue; }
            var enemy = entry.Enemy;
            if (enemy.Damage == null || !enemy.Damage.IsSetup || enemy.Damage.Pointer == IntPtr.Zero)
            { rows.Add(Row("rejected", "missing-health-receiver")); rejected++; continue; }
            if (enemy.Damage.Owner == null || enemy.Damage.Owner.Pointer != entry.EnemyPointer)
            { rows.Add(Row("rejected", "health-receiver-owner-mismatch")); rejected++; continue; }
            if (!enemy.Alive || !(enemy.Damage.Health > 0)) { rows.Add(Row("rejected", "not-alive")); rejected++; continue; }
            var locomotion = enemy.Locomotion;
            if (locomotion == null || locomotion.Pointer == IntPtr.Zero)
            { rows.Add(Row("rejected", "missing-locomotion")); rejected++; continue; }
            if (locomotion.Hitreact == null || locomotion.Hitreact.Pointer == IntPtr.Zero)
            { rows.Add(Row("rejected", "no-hitreact-entry")); rejected++; continue; }

            var hitreact = locomotion.Hitreact;
            var hitreactPointer = hitreact.Pointer;
            bool allowed;
            try
            {
                // `respect` asks the game's own gate and takes its answer; `ignore` clears the one flag the game
                // exposes for a forbidden reaction and asks the gate with the retrigger timer overridden, which is
                // the strongest interrupt this build has. Neither path forces the state when the game still says no.
                if (immunity == ImmunityPolicyIgnore) hitreact.HitreactForbidden = false;
                allowed = hitreact.CanHitreact(reaction, immunity == ImmunityPolicyIgnore);
            }
            catch (Exception) { rows.Add(Row("rejected", "hitreact-gate-exception")); rejected++; continue; }
            if (!allowed) { rows.Add(Row("rejected", "stagger-immune")); rejected++; continue; }
            // Entering the native entry point is the only place the world is written. The call is one dispatch and
            // the readback is its only proof, so a failure after it stays unknown rather than being retried.
            try
            {
                hitreact.ActivateState(reaction, ImpactDirection.Unspecified, false, null, Vector3.zero,
                    DamageNoiseLevel.Normal);
            }
            catch (Exception) { rows.Add(Row("unknown", "native-commit-exception")); unknown++; continue; }
            ES_HitreactType after;
            try
            {
                if (!CanExecute) { rows.Add(Row("unknown", "authority-or-phase")); unknown++; continue; }
                var current = Resolve(target);
                if (current == null || !ReferenceEquals(current, entry) || !ReferenceEquals(enemy.Locomotion, locomotion)
                    || locomotion.Hitreact == null || locomotion.Hitreact.Pointer != hitreactPointer)
                { rows.Add(Row("unknown", "receiver-changed-during-commit")); unknown++; continue; }
                after = locomotion.Hitreact.CurrentReactionType;
            }
            catch (Exception) { rows.Add(Row("unknown", "readback-exception")); unknown++; continue; }
            if (after != reaction) { rows.Add(Row("unknown", "unexpected-reaction-readback")); unknown++; continue; }
            rows.Add(Row("committed", "committed"));
            committed++;
        }

        var outputs = RuntimeJson.From(new { results = rows });
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
        {
            string code = rows.Count == 1 ? rows[0].Code
                : rows.Select(r => r.Code).Distinct().Count() == 1 ? rows[0].Code : "stagger-all-rejected";
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        }
        return CommandResult.FailedUnknown(outputs, rows.Count == 1 ? rows[0].Code : "stagger-all-unknown");
    }

    /// <summary>Multi-target attack interrupt. One row is written per recipient in the plan's own order, and the
    /// row's subject is the enemy: nothing about the attack that was interrupted is reported, because the attack
    /// state is what the write consumed, not what the caller keeps. An enemy whose locomotion shows no performing
    /// or charging attack is refused with `enemy-not-attacking` and commits nothing — that is the whole answer for
    /// an enemy that has nothing to interrupt, and it is decided before any native write.</summary>
    internal CommandResult AttackInterrupt(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        // `source` is the required context role every action of this family carries. The hitreact entry point
        // takes a native `Agent` attacker and this provider cannot name the native object behind another
        // provider's reference, so no attacker is passed instead of one being invented.
        _ = context.GetEntityInput("source");
        var enemies = context.Inputs.GetProperty("enemies").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        if (enemies.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<AttackInterruptRow>(enemies.Length);
        int committed = 0, rejected = 0, unknown = 0;
        foreach (var target in enemies)
        {
            AttackInterruptRow Row(string status, string code)
                => new(target, status, status switch
                {
                    "committed" => CommitStates.Confirmed,
                    "rejected" => CommitStates.None,
                    _ => CommitStates.Unknown
                }, code, enemies.Length);

            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row("rejected", "stale-or-unsupported-recipient")); rejected++; continue; }
            var enemy = entry.Enemy;
            if (enemy.Damage == null || !enemy.Damage.IsSetup || enemy.Damage.Pointer == IntPtr.Zero)
            { rows.Add(Row("rejected", "missing-health-receiver")); rejected++; continue; }
            if (enemy.Damage.Owner == null || enemy.Damage.Owner.Pointer != entry.EnemyPointer)
            { rows.Add(Row("rejected", "health-receiver-owner-mismatch")); rejected++; continue; }
            if (!enemy.Alive || !(enemy.Damage.Health > 0)) { rows.Add(Row("rejected", "not-alive")); rejected++; continue; }
            var locomotion = enemy.Locomotion;
            if (locomotion == null || locomotion.Pointer == IntPtr.Zero)
            { rows.Add(Row("rejected", "missing-locomotion")); rejected++; continue; }

            // The only evidence that this enemy has an attack to interrupt is its own attack states. A read that
            // throws proves nothing, so it is a refusal rather than an assumed attack.
            bool inFlight;
            try { inFlight = EnemyCombatWrite.IsAttacking(locomotion); }
            catch (Exception) { rows.Add(Row("rejected", "attack-state-unreadable")); rejected++; continue; }
            if (!inFlight) { rows.Add(Row("rejected", "enemy-not-attacking")); rejected++; continue; }
            if (locomotion.Hitreact == null || locomotion.Hitreact.Pointer == IntPtr.Zero)
            { rows.Add(Row("rejected", "no-hitreact-entry")); rejected++; continue; }

            var hitreact = locomotion.Hitreact;
            var hitreactPointer = hitreact.Pointer;
            // The interruption is the game's own hitreact state machine, the same entry `stagger` uses: an attack
            // state is left only by the state machine that owns it. The game's gate is asked and its answer taken;
            // this row has no immunity policy, so nothing here overrides it. A gate that throws is a refusal — no
            // write was attempted — while a write that throws is an unknown commit, because the call may have
            // reached the state machine before it failed.
            bool allowed;
            try { allowed = hitreact.CanHitreact(InterruptReaction, false); }
            catch (Exception) { rows.Add(Row("rejected", "hitreact-gate-exception")); rejected++; continue; }
            if (!allowed) { rows.Add(Row("rejected", "attack-interrupt-forbidden")); rejected++; continue; }
            try
            {
                hitreact.ActivateState(InterruptReaction, ImpactDirection.Unspecified, false, null, Vector3.zero,
                    DamageNoiseLevel.Normal);
            }
            catch (Exception) { rows.Add(Row("unknown", "native-commit-exception")); unknown++; continue; }
            try
            {
                if (!CanExecute) { rows.Add(Row("unknown", "authority-or-phase")); unknown++; continue; }
                var current = Resolve(target);
                if (current == null || !ReferenceEquals(current, entry) || !ReferenceEquals(enemy.Locomotion, locomotion)
                    || locomotion.Hitreact == null || locomotion.Hitreact.Pointer != hitreactPointer)
                { rows.Add(Row("unknown", "receiver-changed-during-commit")); unknown++; continue; }
                if (locomotion.Hitreact.CurrentReactionType != InterruptReaction)
                { rows.Add(Row("unknown", "unexpected-reaction-readback")); unknown++; continue; }
            }
            catch (Exception) { rows.Add(Row("unknown", "readback-exception")); unknown++; continue; }
            rows.Add(Row("committed", "committed"));
            committed++;
        }

        var outputs = RuntimeJson.From(new { results = rows });
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
        {
            string code = rows.Count == 1 ? rows[0].Code
                : rows.Select(r => r.Code).Distinct().Count() == 1 ? rows[0].Code : "attack-interrupt-all-rejected";
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        }
        return CommandResult.FailedUnknown(outputs, rows.Count == 1 ? rows[0].Code : "attack-interrupt-all-unknown");
    }

    /// <summary>The declared `reaction` member's own name, for the result row. The catalog spells the members in
    /// lower case; the native enum is the storage, and the row reports the member the caller chose.</summary>
    private static string StaggerReactionName(int index) => index switch
    {
        0 => "micro",
        1 => "light",
        _ => "heavy"
    };

}

/// <summary>The native state questions and the one native write path this family owns, kept in its own type so the
/// game assembly is reached from exactly one audited member and the handlers stay readable without it.
///
/// `EnemyLocomotion` owns the enemy's attack states, one field per attack archetype, all derived from
/// `ES_EnemyAttackBase`. That is the only route from an enemy to its own attack: the attack state names its owner
/// and never the other way round, so the four declared fields are what "this enemy is attacking" means.</summary>
internal static class EnemyCombatWrite
{
    /// <summary>Whether any attack state this locomotion owns reports a hit still in flight — performing or
    /// charging, which are the two questions `ES_EnemyAttackBase` answers. A state that is absent is not attacking,
    /// and a state whose wrapper has no native instance is not asked.</summary>
    internal static bool IsAttacking(EnemyLocomotion locomotion)
    {
        foreach (var attack in AttackStates(locomotion))
            if (attack != null && attack.Pointer != IntPtr.Zero
                && (attack.IsPerformingAttack() || attack.IsChargingAttack())) return true;
        return false;
    }

    /// <summary>The four attack states `EnemyLocomotion` declares, in its own declaration order. This is the whole
    /// set the build puts on an enemy: striker, tank, tank multi-target and shooter.</summary>
    private static ES_EnemyAttackBase?[] AttackStates(EnemyLocomotion locomotion) => new ES_EnemyAttackBase?[]
    {
        locomotion.StrikerAttack, locomotion.TankAttack, locomotion.TankMultiTargetAttack, locomotion.ShooterAttack
    };

}
