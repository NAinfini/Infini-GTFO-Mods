using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>Host authority gate and native write path for `forge.action.enemy.phase_set`, and the read/write
/// surface the other `forge.enemy.profile` rows were surveyed against.
///
/// A SquidBoss phase is one of the few enemy values the game replicates on its own: `SquidBossBehaviour` overrides
/// `EnemyBehaviour.GetExtraStateDataForCapture` / `SetIncomingExtraStateData`, whose payload is
/// `EnemySync.pExtraBehaviourStateData` — a struct carrying exactly one field, `bossPhase`, a `byte` — sent over
/// `EnemySync.m_aiExtraBehaviourStatePacket`. So writing `Phase` through the game's own public setter on the host
/// is captured and sent by the game in the same frame instead of having to be replicated by Forge.
///
/// The setter itself validates nothing (it is an auto-property), so this handler owns the value domain: the wire
/// field is one byte, which is why a phase outside 0..255 is refused rather than narrowed on the wire.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>Members of the declared `reset_policy` enum set, in the catalog's own order. `keep` is the only
    /// policy this build can honour; the index is still bounded by the declared width, so an undeclared member is
    /// refused instead of being read as one of them.</summary>
    private const int PhaseSetResetPolicyCount = 2;

    /// <summary>The replicated phase field is `EnemySync.pExtraBehaviourStateData.bossPhase`, a `byte`.
    /// `internal` so the focused suite can assert the domain against the same bound the handler uses.</summary>
    internal const int PhaseMinimum = 0;
    internal const int PhaseMaximum = 255;

    /// <summary>One row of the multi-target phase result. Field names are the wire contract's, not this
    /// assembly's: `forge.result.enemy.phase_set` declares target, status, committed, code, phase and
    /// target_count, so each is spelled here exactly as the contract spells it.</summary>
    private sealed record PhaseSetRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("phase")] int? Phase,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>Multi-target phase switch. One row is written per recipient in the plan's own order. A committed
    /// row is one whose phase was read back as the value that was set; a write whose readback disagrees stays
    /// unknown rather than being claimed, and a refusal before the native setter is a rejected row.
    ///
    /// `internal`, not `private`, so the focused suite can dispatch it through a kernel-built command context and
    /// assert the rows it writes; the registration reaches it by the handler name
    /// `EnemyProfileContract.PhaseSetHandler` registers in `EnemyActionFamilies`.</summary>
    internal CommandResult PhaseSet(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        int phase = context.Inputs.GetProperty("phase").GetInt32();
        if (phase < PhaseMinimum || phase > PhaseMaximum) return CommandResult.Rejected("phase-out-of-range");
        int expected = context.Inputs.GetProperty("expected_phase").GetInt32();
        if (expected < PhaseMinimum || expected > PhaseMaximum) return CommandResult.Rejected("phase-out-of-range");
        int policy = context.Parameters.GetProperty("reset_policy").GetInt32();
        if (policy < 0 || policy >= PhaseSetResetPolicyCount) return CommandResult.Rejected("reset-policy-unsupported");
        // The native phase setter is the whole write. It does not touch the boss's behaviour state machine, so a
        // policy that would have to restart that machine is refused instead of being approximated. `keep` is
        // member 0 of the declared set, so anything past it is that policy.
        if (policy != 0) return CommandResult.Rejected("phase-reset-unsupported");

        var targets = context.Inputs.GetProperty("enemies").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        // The rows are the whole result, and the protocol's own budget caps the answer.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<PhaseSetRow>(targets.Length);
        int committed = 0, rejected = 0, unknown = 0;
        foreach (var target in targets)
        {
            PhaseSetRow Row(string status, string code, int? observed = null)
                => new(target, status, status switch
                {
                    "committed" => CommitStates.Confirmed,
                    "rejected" => CommitStates.None,
                    _ => CommitStates.Unknown
                }, code, observed, targets.Length);

            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row("rejected", "stale-or-unsupported-recipient")); rejected++; continue; }
            var enemy = entry.Enemy;
            // The phase lives on the enemy's behaviour machine, so everything the write depends on is re-proved
            // before the setter is reached: the AI, the behaviour instance behind the `enemy-type` the instance
            // was registered with, and the enemy pointer the reference names.
            var ai = enemy.AI;
            if (ai == null || ai.m_enemyAgent == null || ai.m_enemyAgent.Pointer != entry.EnemyPointer)
            { rows.Add(Row("rejected", "ai-owner-mismatch")); rejected++; continue; }
            if (ai.m_behaviour == null || ai.m_behaviour.Pointer == IntPtr.Zero || ai.m_behaviour.m_ai == null
                || ai.m_behaviour.m_ai.Pointer != ai.Pointer)
            { rows.Add(Row("rejected", "missing-phase-receiver")); rejected++; continue; }
            if (!EnemyProfileWrite.TryReadPhase(ai.m_behaviour, out int current, out var boss))
            { rows.Add(Row("rejected", "not-a-phase-receiver")); rejected++; continue; }
            if (current != expected) { rows.Add(Row("rejected", "phase-mismatch", current)); rejected++; continue; }
            if (current == phase) { rows.Add(Row("committed", "committed", current)); committed++; continue; }

            // Entering the native setter is the only place the world is written. The write is one call plus the
            // game's own weakspot step, and the readback is its only proof, so a failure after it stays unknown.
            int? before = current;
            int after;
            try
            {
                EnemyProfileWrite.SetPhase(boss, phase);
            }
            catch (Exception) { rows.Add(Row("unknown", "native-commit-exception", before)); unknown++; continue; }
            try
            {
                if (!CanExecute) { rows.Add(Row("unknown", "authority-or-phase", before)); unknown++; continue; }
                var currentEntry = Resolve(target);
                if (currentEntry == null || !ReferenceEquals(currentEntry, entry) || currentEntry.EnemyPointer != entry.EnemyPointer)
                { rows.Add(Row("unknown", "receiver-changed-during-commit", before)); unknown++; continue; }
                var currentAi = enemy.AI;
                if (currentAi == null || currentAi.m_enemyAgent == null
                    || currentAi.m_enemyAgent.Pointer != entry.EnemyPointer
                    || currentAi.m_behaviour == null || currentAi.m_behaviour.m_ai == null
                    || currentAi.m_behaviour.m_ai.Pointer != currentAi.Pointer
                    || !EnemyProfileWrite.TryReadPhaseValue(currentAi.m_behaviour, out after))
                { rows.Add(Row("unknown", "receiver-changed-during-commit", before)); unknown++; continue; }
            }
            catch (Exception) { rows.Add(Row("unknown", "readback-exception", before)); unknown++; continue; }
            if (after != phase) { rows.Add(Row("unknown", "unexpected-phase-readback", after)); unknown++; continue; }
            rows.Add(Row("committed", "committed", after));
            committed++;
        }

        var outputs = RuntimeJson.From(new { results = rows });
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
        {
            string code = rows.Count == 1 ? rows[0].Code
                : rows.Select(r => r.Code).Distinct().Count() == 1 ? rows[0].Code : "phase-set-all-rejected";
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        }
        return CommandResult.FailedUnknown(outputs, rows.Count == 1 ? rows[0].Code : "phase-set-all-unknown");
    }
}

/// <summary>The one native write path this slice owns, kept in its own type so the game assembly is reached from
/// exactly one audited member and the handlers stay readable without it.
///
/// Two rows of `forge.enemy.profile` were surveyed and are deliberately absent here:
///   * `limb_profile` — `Dam_EnemyDamageLimb.m_health`, `m_healthMax`, `m_weakspotDamageMulti` and
///     `m_armorDamageMulti` are all registered `public` interop fields, so they are writable; but none of them is
///     replicated. The game replicates damage as packets (`SNet_Packet<pBulletDamageData>` and the damage entry
///     points) and every peer computes its own limb health locally, so a single-sided field write is exactly the
///     host/client divergence the row's own evidence warns about. There is no synced limb-health entry point in
///     this build, so no write is attempted.
///   * `perception_profile` — `EnemyDetection.m_movementDetectionDistance`, `m_noiseDetectionRange`,
///     `m_detectionBuildupSpeed` and `m_noiseDetectionOn` are per-instance public fields with no replication
///     channel at all.</summary>
internal static class EnemyProfileWrite
{
    /// <summary>Reads a boss's current phase and hands back the behaviour instance the write goes through. A
    /// behaviour machine that is not a `SquidBossBehaviour` — or one whose cast the interop cannot perform —
    /// answers false instead of being treated as a boss at phase zero.</summary>
    internal static bool TryReadPhase(EnemyBehaviour behaviour, out int phase, out SquidBossBehaviour boss)
    {
        phase = 0;
        boss = null!;
        if (behaviour == null || behaviour.Pointer == IntPtr.Zero) return false;
        if (!TryBoss(behaviour, out var squid)) return false;
        try
        {
            phase = squid.Phase;
            boss = squid;
            return phase >= EnemyModule.PhaseMinimum && phase <= EnemyModule.PhaseMaximum;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Reads only the phase back, for the post-commit proof. The cast is re-done here because the write
    /// may have replaced nothing: this is the same instance the setter was called on, proven again.</summary>
    internal static bool TryReadPhaseValue(EnemyBehaviour behaviour, out int phase)
    {
        phase = 0;
        if (behaviour == null || behaviour.Pointer == IntPtr.Zero) return false;
        if (!TryBoss(behaviour, out var squid)) return false;
        try { phase = squid.Phase; return true; }
        catch (Exception) { return false; }
    }

    /// <summary>One phase submission through the game's own public setter, followed by the boss's own weakspot
    /// step so the phase the game now holds is acted on by the same native code the game uses for it. The setter
    /// performs no validation (an auto-property), so the caller has already bounded the value to the one byte the
    /// replicated field carries. `ActivateWeakspotIfNeeded` is itself a no-op when the current phase needs no
    /// change, so it is not a second decision.</summary>
    internal static void SetPhase(SquidBossBehaviour boss, int phase)
    {
        boss.Phase = phase;
        boss.ActivateWeakspotIfNeeded();
    }

    private static bool TryBoss(EnemyBehaviour behaviour, out SquidBossBehaviour boss)
    {
        boss = null!;
        try
        {
            // A plain `EnemyBehaviour` is not a boss: the interop cast hands back the same managed wrapper only
            // for an instance whose native class really is `SquidBossBehaviour`.
            if (behaviour is not SquidBossBehaviour squid || squid.Pointer == IntPtr.Zero) return false;
            boss = squid;
            return true;
        }
        catch (Exception) { return false; }
    }
}
