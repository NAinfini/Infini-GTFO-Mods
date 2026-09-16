using System.Text.Json;
using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using SNetwork;
using static T;

/// <summary>Focused cases for `forge.action.combat.foaming`: the host puts the game's own foam on an enemy through
/// the game's own spawn entry point, only a host may, a retired life or a moved world is refused, a spawn whose
/// volume cannot be read back is an unknown commit rather than a success, a Forge-side duration takes exactly the
/// volume it added back off, the `foam` handle clears what it covers, and the result rows carry the contract's own
/// field names.</summary>
internal static class GlueCases
{
    internal const string Capability = "forge.action.combat.foaming";

    internal static void Run()
    {
        Case("foaming.host-commits-and-reads-back", () =>
        {
            using var s = new Scene();
            var result = s.DispatchOne(volume: 25, strength: 2);
            Check(result.Status == CommandStatuses.Succeeded, $"Expected succeeded, got {result.Status}/{result.Code}.");
            Check(result.CommitState == CommitStates.Confirmed, $"Expected a confirmed commit, got {result.CommitState}.");
            Check(ProjectileManager.Spawns == 1, $"The native spawn entry point did not run exactly once: {ProjectileManager.Spawns}.");
            Check(Math.Abs(s.Enemy.Damage.AttachedGlueVolume - 25f) < 0.001, $"The receiver does not carry the volume: {s.Enemy.Damage.AttachedGlueVolume}.");
            Check(Math.Abs(ProjectileManager.MultiplierSeen - 2f) < 0.001, $"The effect multiplier is not the strength: {ProjectileManager.MultiplierSeen}.");
            Check(Math.Abs(ProjectileManager.ExpandAttached - 25f) < 0.001, $"The expand volume did not follow the volume: {ProjectileManager.ExpandAttached}.");
            Check(ProjectileManager.SubIndexSeen == -1, $"The entry point was not asked for no particular target: {ProjectileManager.SubIndexSeen}.");
            var row = ResultRow(result, 0);
            Check(row.GetProperty("status").GetString() == "committed", "Row status is not committed.");
            Check(row.GetProperty("committed").GetString() == CommitStates.Confirmed, "Row commit state is not confirmed.");
            Check(row.GetProperty("code").GetString() == "committed", "Row code is not committed.");
            Check(Math.Abs(row.GetProperty("amount").GetDouble() - 25) < 0.001, "Row amount is not the requested volume.");
            Check(ResultRow(result, 0).GetProperty("target_count").GetInt32() == 1, "Row target count is not the recipient count.");
            Check(RuntimeJson.Entity(row.GetProperty("target")) == s.Reference, "Row target is not the recipient.");
            Check(result.Outputs.TryGetProperty("foam", out var foam) && foam.ValueKind == JsonValueKind.Object
                && foam.GetRawText().Length > 2, "A committed foam dispatch did not carry its handle.");
        });

        Case("foaming.client-is-refused-with-no-write", () =>
        {
            using var s = new Scene();
            SNet.IsMaster = false;
            var result = s.DispatchOne(volume: 25, strength: 1);
            Check(result.Status == CommandStatuses.Rejected && result.Code == "authority-or-phase",
                $"A non-host was not refused by authority: {result.Status}/{result.Code}.");
            Check(result.CommitState == CommitStates.None, "A refused command reported a commit.");
            Check(ProjectileManager.Spawns == 0 && s.Enemy.Damage.AttachedGlueVolume == 0f, "A non-host wrote the world.");
        });

        Case("foaming.caller-gate-closed-is-refused", () =>
        {
            using var s = new Scene();
            s.Allowed = false;
            var result = s.DispatchOne(volume: 25, strength: 1);
            Check(result.Status == CommandStatuses.Rejected && result.Code == "authority-or-phase",
                $"A closed host gate was not refused: {result.Status}/{result.Code}.");
            Check(ProjectileManager.Spawns == 0, "The gate was bypassed.");
        });

        Case("foaming.domain-is-enforced-before-any-write", () =>
        {
            using var s = new Scene();
            Check(s.DispatchOne(volume: 0, strength: 1).Code == "volume-out-of-range", "A zero volume was accepted.");
            Check(s.DispatchOne(volume: -1, strength: 1).Code == "volume-out-of-range", "A negative volume was accepted.");
            Check(s.DispatchOne(volume: 1e9, strength: 1).Code == "volume-out-of-range", "An unbounded volume was accepted.");
            Check(s.DispatchOne(volume: 25, strength: 0).Code == "strength-out-of-range", "A zero strength was accepted.");
            Check(s.DispatchOne(volume: 25, strength: -1).Code == "strength-out-of-range", "A negative strength was accepted.");
            Check(s.DispatchOne(volume: 25, strength: 1e9).Code == "strength-out-of-range", "An unbounded strength was accepted.");
            Check(s.DispatchOne(volume: 25, strength: 1, duration: -1).Code == "duration-out-of-range", "A negative duration was accepted.");
            Check(ProjectileManager.Spawns == 0 && s.Enemy.Damage.AttachedGlueVolume == 0f, "A refused command wrote the world.");
        });

        Case("foaming.world-mismatch-is-refused", () =>
        {
            using var s = new Scene();
            var foreign = new EntityReference(s.Reference.Id, s.Reference.WorldEpoch + 1, s.Reference.LifeEpoch);
            var result = s.Dispatch(25, 1, 0, foreign);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "stale-or-unsupported-recipient",
                $"A reference from another world was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(result.CommitState == CommitStates.None && ProjectileManager.Spawns == 0, "A foreign reference wrote the world.");
        });

        Case("foaming.world-change-drops-the-life", () =>
        {
            using var s = new Scene();
            s.Kernel.BeginWorld(2);
            var result = s.DispatchOne(volume: 25, strength: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "stale-or-unsupported-recipient",
                $"A life retired by a world change was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ProjectileManager.Spawns == 0, "A retired life wrote the world.");
        });

        Case("foaming.retired-life-is-refused", () =>
        {
            using var s = new Scene();
            // The same native agent after a despawn and respawn is a new life with a new reference; the old one
            // must never resolve again, even though the object behind it is the same.
            s.Retire(s.Reference);
            var next = s.Track(s.Enemy);
            Check(next.LifeEpoch != s.Reference.LifeEpoch, "A respawn reused the retired life epoch.");
            var result = s.Dispatch(25, 1, 0, s.Reference);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "stale-or-unsupported-recipient",
                $"A retired life was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ProjectileManager.Spawns == 0, "A retired life wrote the world.");
        });

        Case("foaming.missing-receiver-is-refused", () =>
        {
            using var s = new Scene();
            s.Enemy.Damage = null!;
            var result = s.DispatchOne(volume: 25, strength: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == GlueActions.NoReceiver,
                $"A recipient without a glue receiver was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ProjectileManager.Spawns == 0, "A recipient without a receiver wrote the world.");
        });

        Case("foaming.foreign-receiver-owner-is-refused", () =>
        {
            using var s = new Scene();
            s.Enemy.Damage.Owner = Scene.NewEnemy(id: 99, pointer: 900);
            var result = s.DispatchOne(volume: 25, strength: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == GlueActions.ReceiverMismatch,
                $"A receiver owned by another enemy was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ProjectileManager.Spawns == 0, "A receiver owned by another enemy wrote the world.");
        });

        Case("foaming.dead-enemy-is-refused", () =>
        {
            using var s = new Scene();
            s.Enemy.OnDead();
            var result = s.DispatchOne(volume: 25, strength: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == "not-alive",
                $"A dead enemy was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ProjectileManager.Spawns == 0, "A dead enemy wrote the world.");
        });

        Case("foaming.native-failure-is-unknown-not-success", () =>
        {
            using var s = new Scene();
            ProjectileManager.OnSpawnGlueOnEnemyAgent = () => throw new InvalidOperationException("native");
            var result = s.DispatchOne(volume: 25, strength: 1);
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown,
                $"A native failure was not reported as unknown: {result.Status}/{result.CommitState}.");
            Check(Status(result, 0) == "unknown" && Code(result, 0) == "native-commit-exception",
                $"A native failure carried the wrong row: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ResultRow(result, 0).GetProperty("committed").GetString() == CommitStates.Unknown,
                "An unknown commit reported a confirmed column.");
            Check(!s.Actions.IsFoamed(s.Reference), "A failed spawn was recorded as foam.");
        });

        Case("foaming.unmoved-volume-is-unknown", () =>
        {
            using var s = new Scene();
            // The receiver's own rules take back what the spawn added — the cap already held this much — so the
            // entry point ran and nothing observable changed.
            ProjectileManager.OnSpawnGlueOnEnemyAgent = () => s.Enemy.Damage.AttachedGlueVolume -= 25f;
            var result = s.DispatchOne(volume: 25, strength: 1);
            Check(Status(result, 0) == "unknown" && Code(result, 0) == "glue-unseen",
                $"A spawn nothing could observe was claimed: {Status(result, 0)}/{Code(result, 0)}.");
            Check(!s.Actions.IsFoamed(s.Reference), "An unobserved spawn was recorded as foam.");
        });

        Case("foaming.unreadable-readback-is-rejected", () =>
        {
            using var s = new Scene();
            // The readback after the spawn is what proves it, so a receiver whose published volume is not a volume
            // is refused before anything is written: the same answer a receiver that does not read at all gets.
            s.Enemy.Damage.OnAttachedGlueVolumeRead = () => -5f;
            var result = s.DispatchOne(volume: 25, strength: 1);
            Check(Status(result, 0) == "rejected" && Code(result, 0) == GlueActions.NoReceiver,
                $"An unreadable receiver was not refused: {Status(result, 0)}/{Code(result, 0)}.");
            Check(ProjectileManager.Spawns == 0 && !s.Actions.IsFoamed(s.Reference), "An unreadable receiver was written.");
        });

        Case("foaming.duration-takes-back-exactly-what-it-added", () =>
        {
            using var s = new Scene();
            var result = s.DispatchOne(volume: 25, strength: 1, duration: 10);
            Check(result.Status == CommandStatuses.Succeeded, $"The applying dispatch was refused: {result.Status}/{result.Code}.");
            Check(s.Actions.IsFoamed(s.Reference), "A committed application was not remembered.");
            // Nothing has run out yet: the simulation is still at tick 0.
            s.Advance(5);
            Check(s.Enemy.Damage.Removals == 0, $"The foam expired before its duration: {s.Enemy.Damage.Removals}.");
            s.Advance(10);
            Check(s.Enemy.Damage.Removals == 1, $"The foam did not expire at its duration: {s.Enemy.Damage.Removals}.");
            Check(Math.Abs(s.Enemy.Damage.RemovedVolume - 25f) < 0.001, $"The removal took a different volume back: {s.Enemy.Damage.RemovedVolume}.");
            Check(Math.Abs(s.Enemy.Damage.AttachedGlueVolume) < 0.001, $"The receiver still carries foam: {s.Enemy.Damage.AttachedGlueVolume}.");
            Check(!s.Actions.IsFoamed(s.Reference), "An expired application is still remembered.");
            s.Advance(11);
            Check(s.Enemy.Damage.Removals == 1, "An expired application expired a second time.");
        });

        Case("foaming.no-duration-has-no-timer", () =>
        {
            using var s = new Scene();
            s.DispatchOne(volume: 25, strength: 1);
            s.Advance(1000);
            Check(s.Enemy.Damage.Removals == 0, "A foam with no duration was taken back by a timer.");
            Check(s.Actions.IsFoamed(s.Reference), "A foam with no duration left the ledger.");
        });

        Case("foaming.world-change-abandons-the-timer", () =>
        {
            using var s = new Scene();
            s.DispatchOne(volume: 25, strength: 1, duration: 10);
            s.Kernel.BeginWorld(2);
            s.Advance(10);
            Check(s.Enemy.Damage.Removals == 0, "A new world removed foam through the old world's ledger.");
            Check(!s.Actions.IsFoamed(s.Reference), "A new world kept the old world's foam record.");
        });

        Case("foaming.repeat-application-adds-up-and-expires-once", () =>
        {
            using var s = new Scene();
            s.DispatchOne(volume: 10, strength: 1, duration: 10);
            s.DispatchOne(volume: 15, strength: 1, duration: 10);
            Check(Math.Abs(s.Enemy.Damage.AttachedGlueVolume - 25f) < 0.001, $"The volumes did not add up: {s.Enemy.Damage.AttachedGlueVolume}.");
            s.Advance(10);
            Check(s.Enemy.Damage.Removals == 1, $"One life produced {s.Enemy.Damage.Removals} removals.");
            Check(Math.Abs(s.Enemy.Damage.RemovedVolume - 25f) < 0.001, $"The removal did not take both volumes back: {s.Enemy.Damage.RemovedVolume}.");
        });

        Case("foaming.expiry-of-a-lost-life-clears-the-entry", () =>
        {
            using var s = new Scene();
            s.DispatchOne(volume: 25, strength: 1, duration: 10);
            // The life is gone before its duration runs out: there is no receiver left to take the foam off, and the
            // ledger must not keep the record forever or hand the volume to whatever the slot is reused for.
            s.Retire(s.Reference);
            s.Advance(10);
            Check(s.Enemy.Damage.Removals == 0, "A retired life's foam was removed through a reference it no longer names.");
            Check(!s.Actions.IsFoamed(s.Reference), "A retired life's foam record survived its expiry.");
        });

        Case("foaming.expiry-that-throws-does-not-repeat", () =>
        {
            using var s = new Scene();
            s.DispatchOne(volume: 25, strength: 1, duration: 10);
            // A receiver that throws mid-removal must not leave an entry every later tick would try again.
            ProjectileManager.OnSpawnGlueOnEnemyAgent = null;
            s.Enemy.Damage.OnRemoveGlueVolume = _ => throw new InvalidOperationException("native");
            s.Advance(10);
            Check(s.Enemy.Damage.Removals == 1, $"The removal was attempted {s.Enemy.Damage.Removals} times.");
            Check(!s.Actions.IsFoamed(s.Reference), "A removal that threw left its record behind.");
            s.Advance(11);
            Check(s.Enemy.Damage.Removals == 1, "The removal was retried after it threw.");
        });

        Case("foaming.handle-cancel-clears-every-enemy-it-covers", () =>
        {
            using var s = new Scene();
            var second = Scene.NewEnemy(id: 8, pointer: 20);
            var secondReference = s.Track(second);
            var result = s.Dispatch(25, 1, 0, s.Reference, secondReference);
            Check(result.Status == CommandStatuses.Succeeded, $"The two-target dispatch was refused: {result.Status}/{result.Code}.");
            Check(Math.Abs(s.Enemy.Damage.AttachedGlueVolume - 25f) < 0.001, "The first enemy did not get its volume.");
            Check(Math.Abs(second.Damage.AttachedGlueVolume - 25f) < 0.001, "The second enemy did not get its volume.");
            var key = result.Outputs.GetProperty("foam").GetRawText();
            s.Actions.ClearFoamed(key);
            Check(s.Enemy.Damage.Removals == 1 && second.Damage.Removals == 1,
                $"Cancelling the handle cleared {s.Enemy.Damage.Removals}/{second.Damage.Removals} receivers.");
            Check(Math.Abs(s.Enemy.Damage.RemovedVolume - 25f) < 0.001 && Math.Abs(second.Damage.RemovedVolume - 25f) < 0.001,
                "Cancelling the handle took a different volume back.");
            Check(!s.Actions.IsFoamed(s.Reference) && !s.Actions.IsFoamed(secondReference),
                "Cancelling the handle left ledger entries behind.");
            s.Actions.ClearFoamed(key);
            Check(s.Enemy.Damage.Removals == 1, "Cancelling a spent handle cleared a receiver again.");
        });

        Case("foaming.multi-target-rows-are-per-recipient", () =>
        {
            using var s = new Scene();
            var second = Scene.NewEnemy(id: 8, pointer: 20);
            var secondReference = s.Track(second);
            var foreign = new EntityReference(secondReference.Id, secondReference.WorldEpoch + 1, secondReference.LifeEpoch);
            var result = s.Dispatch(25, 1, 0, s.Reference, foreign);
            Check(result.Status == CommandStatuses.Partial, $"A mixed batch was not partial: {result.Status}/{result.Code}.");
            Check(result.Outputs.GetProperty("results").GetArrayLength() == 2, "The batch did not answer one row per recipient.");
            Check(Status(result, 0) == "committed" && Status(result, 1) == "rejected",
                $"Rows did not follow the plan's order: {Status(result, 0)}/{Status(result, 1)}.");
            Check(Code(result, 1) == "stale-or-unsupported-recipient", $"The second row carried the wrong reason: {Code(result, 1)}.");
            Check(ResultRow(result, 1).GetProperty("target_count").GetInt32() == 2, "Row target count is not the batch width.");
            Check(ProjectileManager.Spawns == 1, $"A refused recipient was written anyway: {ProjectileManager.Spawns} spawns.");
        });

        Case("foaming.all-rejected-aggregates-onto-one-code", () =>
        {
            using var s = new Scene();
            var foreign = new EntityReference(s.Reference.Id, s.Reference.WorldEpoch + 1, s.Reference.LifeEpoch);
            var result = s.Dispatch(25, 1, 0, foreign, foreign);
            Check(result.Status == CommandStatuses.Rejected && result.CommitState == CommitStates.None,
                $"A fully refused batch did not aggregate to rejected/none: {result.Status}/{result.CommitState}.");
            Check(result.Code == "stale-or-unsupported-recipient", $"Aggregate code is {result.Code}.");
            Check(result.Outputs.GetProperty("results").GetArrayLength() == 2, "A refused batch still answers every row.");
            Check(!result.Outputs.TryGetProperty("foam", out var foam) || foam.ValueKind == JsonValueKind.Null,
                "A dispatch that applied nothing still handed out a handle.");
        });

        Case("foaming.too-many-targets-is-refused", () =>
        {
            using var s = new Scene();
            var many = Enumerable.Range(0, CommandResult.MaximumFacts + 1).Select(_ => s.Reference).ToArray();
            var result = s.Dispatch(25, 1, 0, many);
            Check(result.Status == CommandStatuses.Rejected && result.Code == "too-many-targets",
                $"A batch over the row budget was not refused: {result.Status}/{result.Code}.");
            Check(ProjectileManager.Spawns == 0, "An over-budget batch wrote the world.");
        });

        Case("foaming.row-budget-batch-reports-the-glue-budget-code", () =>
        {
            using var s = new Scene();
            // The ledger cap is checked per target, so the full batch the row budget allows is the largest one that
            // can reach it. Each enemy is its own life, which is what the ledger count follows.
            var agents = new List<EnemyAgent> { s.Enemy };
            var targets = new List<EntityReference> { s.Reference };
            for (int index = 1; index < CommandResult.MaximumFacts; index++)
            {
                var agent = Scene.NewEnemy(id: (ushort)(100 + index), pointer: 1000 + index * 10);
                agents.Add(agent);
                targets.Add(s.Track(agent));
            }
            var result = s.Dispatch(1, 1, 0, targets.ToArray());
            Check(result.Status == CommandStatuses.Succeeded, $"The row-budget batch was refused: {result.Status}/{result.Code}.");
            Check(result.Outputs.GetProperty("results").GetArrayLength() == CommandResult.MaximumFacts,
                "The batch did not answer one row per recipient.");
            var foam = result.Outputs.GetProperty("foam");
            Check(foam.ValueKind == JsonValueKind.Object, "A fully committed batch did not hand out one handle.");
            var codes = result.Outputs.GetProperty("results").EnumerateArray()
                .Select(row => row.GetProperty("code").GetString()).Distinct().ToArray();
            Check(codes.Length == 1 && codes[0] == "committed", $"The batch carried mixed codes: {string.Join(",", codes)}.");
        });

        Case("foaming.every-spawn-takes-a-fresh-sync-id", () =>
        {
            using var s = new Scene();
            ProjectileManager.NextSyncID = 100;
            s.DispatchOne(volume: 5, strength: 1);
            s.DispatchOne(volume: 5, strength: 1);
            Check(ProjectileManager.NextSyncID == 102, $"Two spawns consumed {ProjectileManager.NextSyncID - 100} sync ids.");
        });
    }
}
