using System.Text.Json;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using static T;

/// <summary>Focused cases for the three node actions: `forge.action.enemy.kill`, `forge.action.enemy.mark` and
/// `forge.action.enemy.target`.
///
/// Each case drives the production handler through a kernel-built command context, so what is asserted is the
/// handler's own decision: which native entry it submitted, what it read back to confirm the commit, which code
/// it refused with, and whether an authority gate or a retired life stopped it before the write. The mark cases
/// also assert the two things only Forge's own marker can carry — the colour and the caller-set lifetime — and
/// that the vanilla tag was not touched.</summary>
internal static class ActionCases
{
    internal static void Run()
    {
        Case("kill.ends-the-life-and-confirms-it", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            var result = s.Dispatch(EnemyNodeEffectContract.KillCapability, new { targets = new[] { reference } });
            Check(result.Status == CommandStatuses.Succeeded, $"Expected succeeded, got {result.Status}/{result.Code}.");
            Check(result.CommitState == CommitStates.Confirmed, "A kill that landed was not confirmed.");
            Check(enemy.Damage!.InstantDeaths == 1, "The instant-death entry did not run exactly once.");
            Check(!enemy.Alive, "The enemy is still alive.");
            Check(Code(result, 0) == "committed", "The row code is not committed.");
            Check(ResultRow(result, 0).GetProperty("target_count").GetInt32() == 1, "target_count is not the recipient count.");
        });

        Case("kill.is-refused-before-the-write", () =>
        {
            using var s = new Scene();
            var live = Scene.NewEnemy();
            var dead = Scene.NewEnemy(id: 8, pointer: 20);
            dead.Alive = false;
            var stale = Scene.NewEnemy(id: 9, pointer: 30);
            var liveRef = s.Track(live);
            var deadRef = s.Track(dead);
            var staleRef = s.Track(stale);
            s.Retire(staleRef);
            var result = s.Dispatch(EnemyNodeEffectContract.KillCapability,
                new { targets = new[] { deadRef, staleRef } });
            Check(result.Status == CommandStatuses.Rejected, $"Expected a rejection, got {result.Status}.");
            Check(Code(result, 0) == "not-alive", "A dead target was not refused by name: " + Code(result, 0));
            Check(Code(result, 1) == "stale-or-unsupported-recipient", "A retired life was not refused by name: " + Code(result, 1));
            Check(dead.Damage!.InstantDeaths == 0 && stale.Damage!.InstantDeaths == 0, "A refused target was written.");
        });

        Case("kill.stops-committing-after-an-unknown-commit", () =>
        {
            using var s = new Scene();
            var throwing = Scene.NewEnemy();
            throwing.Damage!.OnInstantDead = _ => throw new InvalidOperationException("boom");
            var later = Scene.NewEnemy(id: 8, pointer: 20);
            var throwingRef = s.Track(throwing);
            var laterRef = s.Track(later);
            var result = s.Dispatch(EnemyNodeEffectContract.KillCapability,
                new { targets = new[] { throwingRef, laterRef } });
            Check(Status(result, 0) == CommandStatuses.Failed && Committed(result, 0) == CommitStates.Unknown,
                "A native failure was not reported as an unknown commit.");
            Check(Code(result, 1) == "not-attempted-after-unknown-commit", "The run did not stop after an unknown commit.");
            Check(later.Damage!.InstantDeaths == 0, "A target was written after an unknown commit.");
        });

        Case("kill.reports-an-unseen-death-as-unknown", () =>
        {
            using var s = new Scene();
            var survivor = Scene.NewEnemy();
            // The entry point is entered and returns, but the life is still there when it does, which is what a
            // native call whose effect cannot be observed looks like from here.
            survivor.Damage!.Kills = false;
            var reference = s.Track(survivor);
            var result = s.Dispatch(EnemyNodeEffectContract.KillCapability, new { targets = new[] { reference } });
            Check(Committed(result, 0) == CommitStates.Unknown && Code(result, 0) == "death-unseen",
                $"A kill that did not land was not reported as unknown: {Code(result, 0)}.");
            Check(survivor.Damage.InstantDeaths == 1 && survivor.Alive, "The case did not model an unseen death.");
        });

        Case("kill.a-client-is-refused-with-no-write", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            SNetwork.SNet.IsMaster = false;
            var result = s.Dispatch(EnemyNodeEffectContract.KillCapability, new { targets = new[] { reference } });
            Check(result.Status == CommandStatuses.Rejected && result.Code == "authority-or-phase",
                $"A non-host was not refused by authority: {result.Status}/{result.Code}.");
            Check(enemy.Damage!.InstantDeaths == 0, "A non-host wrote the world.");
        });

        Case("kill.a-closed-session-gate-is-refused", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            s.Allowed = false;
            var result = s.Dispatch(EnemyNodeEffectContract.KillCapability, new { targets = new[] { reference } });
            Check(result.Code == "authority-or-phase", "A closed session gate was not refused.");
            Check(enemy.Damage!.InstantDeaths == 0, "The gate was bypassed.");
        });

        Case("mark.places-a-forge-marker-in-the-asked-colour", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            var result = s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 1.0, 0.8, 0.2 }, opacity = 0.5, duration = 120 },
                new { visibility_policy = "team" });
            Check(result.Status == CommandStatuses.Partial && result.CommitState == CommitStates.None,
                $"A presented mark was not a non-committing result: {result.Status}/{result.CommitState}/{result.Code}.");
            Check(s.Layer.Placements == 1, "The marker layer was not asked for exactly one marker.");
            Check(s.Layer.LastOption == NavMarkerOption.EnemyTitleDistance, "The marker is not the enemy style: " + s.Layer.LastOption);
            Check(ReferenceEquals(s.Layer.LastTracking, enemy.Model), "The marker does not track the enemy's model object.");
            var marker = s.Layer.Placed[0];
            Check(marker.ColorCalls == 1, "The marker was not coloured exactly once.");
            Check(Math.Abs(marker.Color!.Value.r - 1.0) < 0.001 && Math.Abs(marker.Color.Value.g - 0.8) < 0.001
                && Math.Abs(marker.Color.Value.b - 0.2) < 0.001 && Math.Abs(marker.Color.Value.a - 0.5) < 0.001,
                "The marker is not the requested colour.");
            Check(result.Outputs.TryGetProperty("marker", out var handle)
                && handle.ValueKind == JsonValueKind.Object && handle.TryGetProperty("local", out _),
                "A placed mark did not carry its handle.");
            Check(Math.Abs(ResultRow(result, 0).GetProperty("duration").GetDouble() - 2.0) < 0.001,
                "The row's duration is not the requested lifetime in seconds.");
        });

        Case("mark.presents-on-a-recipient-that-is-not-the-master", () =>
        {
            // The row is a `presentation` step, so it runs on each addressed player's own machine: neither the
            // master flag nor the dispatching side is a condition of placing the marker. The gameplay gate still
            // is, and a closed one refuses before anything is placed.
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            SNetwork.SNet.IsMaster = false;
            var result = s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 0.0, 1.0, 0.0 }, duration = 60 },
                new { visibility_policy = "team" }, isHost: false);
            Check(result.Status == CommandStatuses.Partial && result.CommitState == CommitStates.None,
                $"A recipient's presentation was not a non-committing result: {result.Status}/{result.CommitState}.");
            Check(s.Layer.Placements == 1, "A non-master recipient did not place its own marker.");
            s.Allowed = false;
            var refused = s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 0.0, 1.0, 0.0 }, duration = 60 },
                new { visibility_policy = "team" }, isHost: false);
            Check(refused.Code == "authority-or-phase" && s.Layer.Placements == 1,
                "A closed gameplay gate did not stop the presented mark.");
        });

        Case("mark.refuses-what-the-row-cannot-carry", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            object targets = new { targets = new[] { reference }, color = new[] { 1.0, 0.0, 0.0 }, opacity = 1.0 };
            Check(s.Dispatch(EnemyNodeEffectContract.MarkCapability, targets, new { visibility_policy = "self" }).Code
                == "visibility-unsupported", "A policy the marker cannot reach was accepted.");
            Check(s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 2.0, 0.0, 0.0 }, opacity = 1.0 },
                new { visibility_policy = "team" }).Code == "invalid-color", "An out-of-range colour was accepted.");
            Check(s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 1.0, 0.0 }, opacity = 1.0 },
                new { visibility_policy = "team" }).Code == "invalid-color", "A two-component colour was accepted.");
            Check(s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 1.0, 0.0, 0.0 }, opacity = 2.0 },
                new { visibility_policy = "team" }).Code == "invalid-color", "An out-of-range opacity was accepted.");
            Check(s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 1.0, 0.0, 0.0 }, duration = -1 },
                new { visibility_policy = "team" }).Code == "duration-out-of-range", "A negative lifetime was accepted.");
            Check(s.Layer.Placements == 0, "A refused request placed a marker.");
        });

        Case("mark.refuses-an-enemy-without-a-model", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Model = null;
            var reference = s.Track(enemy);
            var result = s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 1.0, 0.0, 0.0 }, duration = 60 },
                new { visibility_policy = "team" });
            Check(Code(result, 0) == "enemy-model-unavailable", "A marker with nothing to track was not refused: " + Code(result, 0));
            Check(s.Layer.Placements == 0, "A marker was placed on an enemy with no model.");
        });

        Case("mark.reports-an-unplaced-marker-as-refused", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            s.Layer.ReturnNull = true;
            var result = s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 1.0, 0.0, 0.0 }, duration = 60 },
                new { visibility_policy = "team" });
            Check(Code(result, 0) == "marker-unplaced", "A declined placement was not refused by name: " + Code(result, 0));
            Check(!result.Outputs.TryGetProperty("marker", out var handle) || handle.ValueKind == JsonValueKind.Null,
                "A dispatch that placed nothing carried a handle.");
        });

        Case("mark.expires-the-marker-at-the-asked-tick", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 1.0, 0.0, 0.0 }, duration = 10 },
                new { visibility_policy = "team" });
            Check(s.Layer.Removals == 0, "The marker was removed before its lifetime ran out.");
            s.Module.ExpireMarks();
            Check(s.Layer.Removals == 0, "A marker with lifetime left was expired.");
            s.Kernel.Advance(10, true);
            s.Module.ExpireMarks();
            Check(s.Layer.Removals == 1, "The marker outlived its requested lifetime.");
        });

        Case("mark.a-dead-enemies-marker-goes-with-the-life", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { reference }, color = new[] { 1.0, 0.0, 0.0 }, duration = 10000 },
                new { visibility_policy = "team" });
            s.Retire(reference);
            s.Module.ExpireMarks();
            Check(s.Layer.Removals == 1, "A marker whose life was retired was not removed.");
        });

        Case("mark.cancel-removes-every-marker-the-dispatch-placed", () =>
        {
            using var s = new Scene();
            var first = Scene.NewEnemy();
            var second = Scene.NewEnemy(id: 8, pointer: 20);
            var firstRef = s.Track(first);
            var secondRef = s.Track(second);
            var result = s.Dispatch(EnemyNodeEffectContract.MarkCapability,
                new { targets = new[] { firstRef, secondRef }, color = new[] { 1.0, 0.0, 0.0 }, duration = 10000 },
                new { visibility_policy = "team" });
            // The handle is the kernel's own frame; the cancel it is registered under is keyed by its text, which
            // is what the kernel's own handle table answers with.
            var handle = result.Outputs.GetProperty("marker").GetRawText();
            s.Module.RemoveMarked(handle);
            Check(s.Layer.Removals == 2, "Cancelling the handle did not remove both markers: " + s.Layer.Removals);
            s.Module.RemoveMarked(handle);
            Check(s.Layer.Removals == 2, "Cancelling a spent handle removed something twice.");
        });

        Case("target.refuses-a-player-no-provider-can-resolve", () =>
        {
            // The propagated agent comes from the kind's own lookup, so a reference no provider answers for has
            // no agent behind it. That is a refusal by name, and no enemy is touched.
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            var unknown = new EntityReference("gtfo.player:99", s.Kernel.WorldEpoch, 1);
            var result = s.Dispatch(EnemyNodeEffectContract.TargetCapability,
                new { enemies = new[] { reference }, target = unknown });
            Check(result.Status == CommandStatuses.Rejected && result.Code == "target-unavailable",
                $"An unresolvable player was not refused by name: {result.Status}/{result.Code}.");
            Check(enemy.FullPropagations == 0 && enemy.LimitedPropagations == 0, "A refused target reached the native call.");
        });

        Case("target.propagates-through-the-registered-agent", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            var playerReference = s.PlayerReference(out var player);
            var result = s.Dispatch(EnemyNodeEffectContract.TargetCapability,
                new { enemies = new[] { reference }, target = playerReference });
            Check(result.Status == CommandStatuses.Succeeded, $"Expected succeeded, got {result.Status}/{result.Code}.");
            Check(enemy.FullPropagations == 1 && enemy.LimitedPropagations == 0,
                "The full propagation form was not the one submitted.");
            Check(ReferenceEquals(enemy.LastTarget, player), "The propagated agent is not the player's own instance.");
            Check(ResultRow(result, 0).GetProperty("propagated").GetBoolean(),
                "A committed propagation did not read back as propagated.");
        });

        Case("target.the-chance-form-reports-what-the-game-kept", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            var playerReference = s.PlayerReference(out var player);
            enemy.LimitedResult = false;
            var refused = s.Dispatch(EnemyNodeEffectContract.TargetCapability,
                new { enemies = new[] { reference }, target = playerReference, chance = 0.25 });
            Check(refused.Status == CommandStatuses.Rejected && Code(refused, 0) == "propagation-refused",
                $"A refused propagation was not reported: {refused.Status}/{Code(refused, 0)}.");
            Check(Math.Abs(enemy.LastChance - 0.25f) < 0.001, "The chance is not the one the row asked for.");
            Check(ReferenceEquals(enemy.LastTarget, player), "The limited form did not receive the player's agent.");
            enemy.LimitedResult = true;
            var kept = s.Dispatch(EnemyNodeEffectContract.TargetCapability,
                new { enemies = new[] { reference }, target = playerReference, chance = 0.25 });
            Check(kept.Status == CommandStatuses.Succeeded, "A kept propagation was not confirmed.");
            Check(ResultRow(kept, 0).GetProperty("propagated").GetBoolean(), "A kept propagation did not read back.");
            Check(enemy.LimitedPropagations == 2 && enemy.FullPropagations == 0,
                "The chance form did not select the limited propagation entry.");
        });

        Case("target.a-retired-enemy-is-refused", () =>
        {
            using var s = new Scene();
            var retired = Scene.NewEnemy();
            var live = Scene.NewEnemy(id: 8, pointer: 20);
            var retiredRef = s.Track(retired);
            var liveRef = s.Track(live);
            var playerReference = s.PlayerReference(out _);
            s.Retire(retiredRef);
            // The target instance is the scene's own resolvable player, so the only refusal left is the retired
            // life — the row's own code is what a case reads.
            var result = s.Dispatch(EnemyNodeEffectContract.TargetCapability,
                new { enemies = new[] { retiredRef, liveRef }, target = playerReference });
            Check(Code(result, 0) == "stale-or-unsupported-recipient",
                "A retired life was not refused by name: " + Status(result, 0) + "/" + Code(result, 0));
            Check(retired.FullPropagations == 0, "A retired life was written.");
        });
        Case("target.refuses-a-chance-outside-the-unit-interval", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            var playerReference = s.PlayerReference(out _);
            // The request checks run before the first target, so a malformed chance refuses the whole command
            // before the target is even resolved — which is why an unresolvable target is enough to tell the two
            // refusals apart: an out-of-range chance is never reported as a target problem.
            var unknown = new EntityReference("gtfo.player:99", s.Kernel.WorldEpoch, 1);
            var over = s.Dispatch(EnemyNodeEffectContract.TargetCapability,
                new { enemies = new[] { reference }, target = unknown, chance = 1.5 });
            Check(over.Code == "chance-out-of-range", "A chance above one was accepted: " + over.Status + "/" + over.Code);
            var under = s.Dispatch(EnemyNodeEffectContract.TargetCapability,
                new { enemies = new[] { reference }, target = unknown, chance = -0.1 });
            Check(under.Code == "chance-out-of-range", "A negative chance was accepted: " + under.Status + "/" + under.Code);
            Check(enemy.FullPropagations == 0 && enemy.LimitedPropagations == 0, "A refused chance was submitted.");        });

        Case("target.refuses-a-target-that-is-not-a-player", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            var result = s.Dispatch(EnemyNodeEffectContract.TargetCapability,
                new { enemies = new[] { reference }, target = reference });
            Check(result.Code == "target-not-a-player", "An enemy used as the target was accepted: " + result.Code);
            Check(enemy.FullPropagations == 0, "A refused target was submitted.");
        });
    }
}
