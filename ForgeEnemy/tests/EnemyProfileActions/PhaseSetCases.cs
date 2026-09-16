using System.Text.Json;
using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using SNetwork;
using static T;

/// <summary>Focused cases for `forge.action.enemy.phase_set`: the host writes a boss phase through the game's own
/// setter, only a host may, a retired life or a moved world is refused, a native failure is an unknown commit
/// rather than a success, and the result rows carry the contract's own field names.</summary>
internal static class PhaseSetCases
{
    internal const string Capability = "forge.action.enemy.phase_set";

    internal static void Run()
    {
        Case("phase_set.host-commits-and-reads-back", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1;
            s.UseBehaviour(s.Boss);
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(result.Status == CommandStatuses.Succeeded, $"Expected succeeded, got {result.Status}/{result.Code}.");
            Check(result.CommitState == CommitStates.Confirmed, $"Expected a confirmed commit, got {result.CommitState}.");
            Check(s.Boss.Phase == 3, $"The native setter did not run: phase={s.Boss.Phase}.");
            Check(s.Boss.WeakspotActivations == 1, $"The boss's own weakspot step did not run once: {s.Boss.WeakspotActivations}.");
            var row = ResultRow(result, 0);
            Check(row.GetProperty("status").GetString() == "committed", "Row status is not committed.");
            Check(row.GetProperty("committed").GetString() == CommitStates.Confirmed, "Row commit state is not confirmed.");
            Check(row.GetProperty("code").GetString() == "committed", "Row code is not committed.");
            Check(row.GetProperty("phase").GetInt32() == 3, "Row phase is not the committed phase.");
            Check(row.GetProperty("target_count").GetInt32() == 1, "Row target count is not the recipient count.");
            Check(RuntimeJson.Entity(row.GetProperty("target")) == s.Reference, "Row target is not the recipient.");
        });

        Case("phase_set.client-is-refused-with-no-write", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1;
            s.UseBehaviour(s.Boss);
            SNet.IsMaster = false;
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(result.Status == CommandStatuses.Rejected && result.Code == "authority-or-phase",
                $"A non-host was not refused by authority: {result.Status}/{result.Code}.");
            Check(result.CommitState == CommitStates.None, "A refused command reported a commit.");
            Check(s.Boss.Phase == 1 && s.Boss.WeakspotActivations == 0, "A non-host wrote the world.");
        });

        Case("phase_set.caller-gate-closed-is-refused", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1; s.UseBehaviour(s.Boss); s.Allowed = false;
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(result.Status == CommandStatuses.Rejected && result.Code == "authority-or-phase",
                $"A closed host gate was not refused: {result.Status}/{result.Code}.");
            Check(s.Boss.Phase == 1, "The gate was bypassed.");
        });

        Case("phase_set.world-mismatch-is-refused", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1; s.UseBehaviour(s.Boss);
            var foreign = new EntityReference(s.Reference.Id, s.Reference.WorldEpoch + 1, s.Reference.LifeEpoch);
            var result = s.Dispatch(3, 1, 0, foreign);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "stale-or-unsupported-recipient",
                $"A reference from another world was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(result.CommitState == CommitStates.None && s.Boss.Phase == 1, "A foreign reference wrote the world.");
        });

        Case("phase_set.world-change-clear-drops-the-life", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1; s.UseBehaviour(s.Boss);
            s.Kernel.BeginWorld(2);
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "stale-or-unsupported-recipient",
                $"A life retired by a world change was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(s.Boss.Phase == 1, "A retired life wrote the world.");
        });

        Case("phase_set.retired-life-is-refused", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1; s.UseBehaviour(s.Boss);
            // The same native agent after a despawn and respawn is a new life with a new reference; the old one
            // must never resolve again, even though the object behind it is the same.
            s.Module.TrackDespawn(s.Enemy);
            var next = s.Module.TrackSpawn(s.Enemy);
            Check(next.LifeEpoch != s.Reference.LifeEpoch, "A respawn reused the retired life epoch.");
            var result = s.Dispatch(3, 1, 0, s.Reference);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "stale-or-unsupported-recipient",
                $"A retired life was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(s.Boss.Phase == 1, "A retired life wrote the world.");
        });

        Case("phase_set.expected-phase-guards-the-write", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 2; s.UseBehaviour(s.Boss);
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "phase-mismatch",
                $"A stale expected phase was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ResultRow(result, 0).GetProperty("phase").GetInt32() == 2, "The refused row did not carry the observed phase.");
            Check(s.Boss.Phase == 2 && s.Boss.WeakspotActivations == 0, "A refused guard wrote the world.");
        });

        Case("phase_set.same-phase-is-a-no-op-commit", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 3; s.UseBehaviour(s.Boss);
            var result = s.DispatchOne(phase: 3, expected: 3);
            Check(result.Status == CommandStatuses.Succeeded, $"An already-current phase was refused: {result.Status}/{result.Code}.");
            Check(s.Boss.WeakspotActivations == 0, "A no-op write still ran the weakspot step.");
        });

        Case("phase_set.domain-and-policy-are-enforced-before-any-write", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1; s.UseBehaviour(s.Boss);
            // The replicated field is one byte, so the domain is the wire's, not the setter's.
            Check(s.DispatchOne(EnemyModule.PhaseMaximum + 1, 1).Code == "phase-out-of-range", "A phase over the byte domain was accepted.");
            Check(s.DispatchOne(EnemyModule.PhaseMinimum - 1, 1).Code == "phase-out-of-range", "A negative phase was accepted.");
            Check(s.DispatchOne(3, EnemyModule.PhaseMaximum + 1).Code == "phase-out-of-range", "An out-of-domain expected phase was accepted.");
            Check(s.DispatchOne(3, 1, resetPolicy: 2).Code == "reset-policy-unsupported", "An undeclared reset policy was accepted.");
            Check(s.DispatchOne(3, 1, resetPolicy: 1).Code == "phase-reset-unsupported", "The reset policy was claimed.");
            Check(s.Boss.Phase == 1 && s.Boss.WeakspotActivations == 0, $"A refused command wrote the world: {s.Boss.Phase}.");
            // The top of the domain is a value the wire can carry, so it is committed rather than narrowed.
            Check(s.DispatchOne(EnemyModule.PhaseMaximum, 1).Status == CommandStatuses.Succeeded,
                "The top of the declared domain was refused.");
            Check(s.Boss.Phase == EnemyModule.PhaseMaximum, $"The top of the domain did not reach the receiver: {s.Boss.Phase}.");
        });

        Case("phase_set.non-boss-receiver-is-refused", () =>
        {
            using var s = new Scene();
            // A plain behaviour machine is not a boss: the phase the row writes does not exist on it, so the row
            // must refuse the recipient instead of treating its default phase as zero.
            s.UseBehaviour(new EnemyBehaviour { Pointer = new IntPtr(12) });
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "not-a-phase-receiver",
                $"A non-boss recipient was not refused: {Status(result, 0)}/{Code(result, 0)}.");
        });

        Case("phase_set.missing-behaviour-is-refused", () =>
        {
            using var s = new Scene();
            s.Enemy.AI = null!;
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "ai-owner-mismatch",
                $"A recipient without an AI was not refused: {Status(result, 0)}/{Code(result, 0)}.");
        });

        Case("phase_set.foreign-ai-owner-is-refused", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1; s.UseBehaviour(s.Boss);
            s.Enemy.AI!.m_enemyAgent = Scene.NewEnemy(id: 99, pointer: 900);
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "ai-owner-mismatch",
                $"An AI owned by another enemy was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(s.Boss.Phase == 1, "An AI owned by another enemy wrote the world.");
        });

        Case("phase_set.native-failure-is-unknown-not-success", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1;
            s.Boss.OnWeakspotCheck = () => throw new InvalidOperationException("native");
            s.UseBehaviour(s.Boss);
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown,
                $"A native failure was not reported as unknown: {result.Status}/{result.CommitState}.");
            Check(Status(result, 0) == "unknown" && Code(result, 0) == "native-commit-exception",
                $"A native failure carried the wrong row: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ResultRow(result, 0).GetProperty("committed").GetString() == CommitStates.Unknown,
                "An unknown commit reported a confirmed column.");
        });

        Case("phase_set.readback-disagreement-is-unknown", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1;
            // The game's own weakspot step rewrites the phase it just read: the write landed, but the value the
            // handler asked for is not what the receiver now holds, so the row may not claim it.
            s.Boss.OnWeakspotCheck = () => s.Boss.Phase = 9;
            s.UseBehaviour(s.Boss);
            var result = s.DispatchOne(phase: 3, expected: 1);
            Check(Status(result, 0) == "unknown" && Code(result, 0) == "unexpected-phase-readback",
                $"A rewritten phase was claimed as committed: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ResultRow(result, 0).GetProperty("phase").GetInt32() == 9, "The unknown row did not carry the read-back phase.");
        });

        Case("phase_set.multi-target-rows-are-per-recipient", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1; s.UseBehaviour(s.Boss);
            var second = Scene.NewEnemy(id: 8, pointer: 20);
            var secondBoss = new SquidBossBehaviour { Pointer = new IntPtr(22), Phase = 5 };
            var ai = new EnemyAI { Pointer = new(21) };
            ai.m_enemyAgent = second; secondBoss.m_ai = ai; ai.m_behaviour = secondBoss;
            second.AI = ai;
            var secondReference = s.Module.TrackSpawn(second);
            var result = s.Dispatch(3, 1, 0, s.Reference, secondReference);
            Check(result.Status == CommandStatuses.Partial, $"A mixed batch was not partial: {result.Status}/{result.Code}.");
            Check(result.Outputs.GetProperty("results").GetArrayLength() == 2, "The batch did not answer one row per recipient.");
            Check(Status(result, 0) == "committed" && Status(result, 1) == "rejected",
                $"Rows did not follow the plan's order: {Status(result, 0)}/{Status(result, 1)}.");
            Check(Code(result, 1) == "phase-mismatch", $"The second row carried the wrong reason: {Code(result, 1)}.");
            Check(s.Boss.Phase == 3 && secondBoss.Phase == 5, "A guarded recipient was written anyway.");
            Check(ResultRow(result, 1).GetProperty("target_count").GetInt32() == 2, "Row target count is not the batch width.");
        });

        Case("phase_set.all-rejected-aggregates-onto-one-code", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1; s.UseBehaviour(s.Boss);
            var foreign = new EntityReference(s.Reference.Id, s.Reference.WorldEpoch + 1, s.Reference.LifeEpoch);
            var result = s.Dispatch(3, 1, 0, foreign, foreign);
            Check(result.Status == CommandStatuses.Rejected && result.CommitState == CommitStates.None,
                $"A fully refused batch did not aggregate to rejected/none: {result.Status}/{result.CommitState}.");
            Check(result.Code == "stale-or-unsupported-recipient", $"Aggregate code is {result.Code}.");
            Check(result.Outputs.GetProperty("results").GetArrayLength() == 2, "A refused batch still answers every row.");
        });

        Case("phase_set.too-many-targets-is-refused", () =>
        {
            using var s = new Scene();
            s.Boss.Phase = 1; s.UseBehaviour(s.Boss);
            var many = Enumerable.Range(0, CommandResult.MaximumFacts + 1).Select(_ => s.Reference).ToArray();
            var result = s.Dispatch(3, 1, 0, many);
            Check(result.Status == CommandStatuses.Rejected && result.Code == "too-many-targets",
                $"A batch over the row budget was not refused: {result.Status}/{result.Code}.");
            Check(s.Boss.Phase == 1, "An over-budget batch wrote the world.");
        });
    }
}
