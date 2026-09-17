using System.Text.Json;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using static T;

/// <summary>Focused cases for the two rows the effect-volume batch adds: `forge.action.combat.effect_volume` and
/// `forge.action.enemy.remove`.
///
/// The volume cases assert what the handler submits to the game's own manager and what it reads back: the sphere
/// the request described, the fog sphere drawn for it, and the release that follows the ending of the step's own
/// effect — the kernel's handle ends every volume one dispatch placed, through the restore callback the module
/// registers. The remove cases assert the one native entry it submits and the two readings it refuses on — the
/// same shape the kill cases in `ActionCases` use, because both rows report one life per line.
///
/// Every scenario is the production handler driven through a kernel-built command context, so a refusal code, the
/// pre-write check order and the authority gate are the module's own decisions rather than a restatement.</summary>
internal static class VolumeCases
{
    private static object Volume(object[] anchors) => new { anchors };

    private static object Parameters(string contents = "all", string modification = "inflict", double scale = 1.0,
        double radiusMin = 0.0, double radiusMax = 5.0, bool? follow = false)
        => follow == null
            ? (object)new { contents, modification, scale, radius_min = radiusMin, radius_max = radiusMax }
            : new { contents, modification, scale, radius_min = radiusMin, radius_max = radiusMax, follow_anchor = follow.Value };

    internal static void Run()
    {
        Case("effect_volume.places-the-sphere-the-request-described", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Position = (1f, 2f, 3f);
            var reference = s.Track(enemy);
            var result = s.Dispatch(EnemyVolumeContract.VolumeCapability, Volume(new[] { reference }),
                Parameters(contents: "infection", modification: "shield", scale: 2.5, radiusMin: 1, radiusMax: 6),
                namesEffect: true);

            Check(result.Status == CommandStatuses.Succeeded, $"Expected succeeded, got {result.Status}/{result.Code}.");
            Check(result.CommitState == CommitStates.Confirmed, "A placed volume was not confirmed.");
            var spheres = EffectVolumeManager.Registered.OfType<EV_Sphere>().ToArray();
            Check(spheres.Length == 1, $"Expected one registered sphere, got {spheres.Length}.");
            Check(spheres[0].position.x == 1f && spheres[0].position.y == 2f && spheres[0].position.z == 3f,
                "The sphere was not placed at the anchor's own position.");
            Check(spheres[0].contents == eEffectVolumeContents.Infection, "The contents choice did not reach the sphere.");
            Check(spheres[0].modification == eEffectVolumeModification.Shield, "The modification choice did not reach the sphere.");
            Check(spheres[0].modificationScale == 2.5f, "The scale did not reach the sphere.");
            Check(spheres[0].minRadius == 1f && spheres[0].maxRadius == 6f, "The two radii did not reach the sphere.");
            Check(FogSphereAllocator.LastAllocation is { Allocated: true }, "No fog sphere was drawn for the volume.");
            Check(Code(result, 0) == "volume-placed", "The row code is not volume-placed: " + Code(result, 0));
            Check(ResultRow(result, 0).GetProperty("target_count").GetInt32() == 1, "target_count is not the anchor count.");
            Check(!ResultRow(result, 0).TryGetProperty("visual", out _), "The result still carries a column the schema does not.");
        });

        Case("effect_volume.one-effect-ends-every-volume-it-placed", () =>
        {
            using var s = new Scene();
            var first = Scene.NewEnemy(id: 7, pointer: 10);
            var second = Scene.NewEnemy(id: 8, pointer: 20);
            var result = s.Dispatch(EnemyVolumeContract.VolumeCapability,
                Volume(new[] { s.Track(first), s.Track(second) }), Parameters(), namesEffect: true);
            Check(result.Status == CommandStatuses.Succeeded, "Two anchors were not both placed: " + result.Code);
            Check(EffectVolumeManager.Registered.Count == 2, "A dispatch did not register one volume per anchor.");
            Check(s.LastHandle != null, "The step's own effect named no handle for the volumes to be filed under.");
            s.EndEffect(s.LastHandle!.Value, EffectEndReasons.Expired);
            Check(EffectVolumeManager.Registered.Count == 0, "The effect's ending left a volume registered.");
            Check(EffectVolumeManager.Unregistrations == 2, "The effect's ending did not unregister every volume of the dispatch.");
            // The kernel keeps calling a callback whose effect the module may have released itself; a second
            // ending finds nothing and is not a failure.
            s.EndEffect(s.LastHandle!.Value, EffectEndReasons.Reclaimed);
            Check(EffectVolumeManager.Unregistrations == 2, "A second ending released a volume twice.");
        });

        Case("effect_volume.the-handle-ends-the-volume-and-its-fog", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            s.Dispatch(EnemyVolumeContract.VolumeCapability, Volume(new[] { reference }), Parameters(),
                namesEffect: true);
            Check(EffectVolumeManager.Registered.Count == 1, "The volume was not registered.");
            var fog = FogSphereAllocator.LastAllocation;
            Check(fog is { Allocated: true }, "No fog sphere was drawn for the volume.");
            s.Module.PumpVolumes();
            Check(EffectVolumeManager.Registered.Count == 1, "The volume was released before its own effect ended.");
            s.EndEffect(s.LastHandle!.Value, EffectEndReasons.Expired);
            Check(EffectVolumeManager.Registered.Count == 0, "The volume outlived its own effect.");
            Check(EffectVolumeManager.Unregistrations == 1, "The ended volume was not released.");
            Check(fog is { Allocated: false }, "The volume's fog sphere was not returned.");
        });

        Case("effect_volume.a-card-with-no-effect-keeps-its-volume-until-the-module-lets-it-go", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            s.Dispatch(EnemyVolumeContract.VolumeCapability, Volume(new[] { s.Track(enemy) }), Parameters());
            Check(s.LastHandle == null, "A step with no effect block was given a handle.");
            Check(EffectVolumeManager.Registered.Count == 1, "The volume was not registered.");
            s.Module.ReleaseVolumes();
            Check(EffectVolumeManager.Registered.Count == 0, "The module's own release left a volume registered.");
        });

        Case("effect_volume.follow-moves-the-sphere-with-its-anchor", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Position = (0f, 0f, 0f);
            var reference = s.Track(enemy);
            s.Dispatch(EnemyVolumeContract.VolumeCapability, Volume(new[] { reference }), Parameters(follow: true),
                namesEffect: true);
            enemy.Position = (9f, 8f, 7f);
            s.Module.PumpVolumes();
            var sphere = EffectVolumeManager.Registered.OfType<EV_Sphere>().Single();
            Check(sphere.position.x == 9f && sphere.position.y == 8f && sphere.position.z == 7f,
                "A following volume did not move to its anchor.");
        });

        Case("effect_volume.refuses-every-bad-request-before-the-first-anchor", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            CommandResult Refuse(object parameters, string expected)
            {
                var result = s.Dispatch(EnemyVolumeContract.VolumeCapability, Volume(new[] { reference }), parameters);
                Check(result.Status == CommandStatuses.Rejected, $"`{expected}` was not refused: {result.Status}/{result.Code}.");
                Check(result.Code == expected, $"Expected `{expected}`, got `{result.Code}`.");
                return result;
            }
            Refuse(Parameters(contents: "fog"), "contents-unknown");
            Refuse(Parameters(modification: "drain"), "modification-unknown");
            Refuse(Parameters(scale: -1), "scale-out-of-range");
            Refuse(Parameters(radiusMin: 5, radiusMax: 1), "radius-out-of-range");
            Refuse(new { contents = "all", modification = "inflict", scale = 1, radius_min = 0, radius_max = 1, follow_anchor = "yes" },
                "follow-flag-invalid");
            Check(EffectVolumeManager.Registered.Count == 0, "A refused request still registered a volume.");
            Check(FogSphereAllocator.Allocations == 0, "A refused request still allocated a fog sphere.");
        });

        Case("effect_volume.follow-is-optional-and-defaults-to-a-fixed-volume", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Position = (0f, 0f, 0f);
            var reference = s.Track(enemy);
            var result = s.Dispatch(EnemyVolumeContract.VolumeCapability, Volume(new[] { reference }),
                Parameters(follow: null), namesEffect: true);
            Check(result.Status == CommandStatuses.Succeeded, "A card that said nothing about following was refused: " + result.Code);
            enemy.Position = (9f, 8f, 7f);
            s.Module.PumpVolumes();
            var sphere = EffectVolumeManager.Registered.OfType<EV_Sphere>().Single();
            Check(sphere.position.x == 0f && sphere.position.y == 0f && sphere.position.z == 0f,
                "A volume with no follow choice moved with its anchor anyway.");
        });

        Case("effect_volume.refuses-an-anchor-it-cannot-place", () =>
        {
            using var s = new Scene();
            var live = Scene.NewEnemy();
            var dead = Scene.NewEnemy(id: 8, pointer: 20);
            dead.Alive = false;
            var liveRef = s.Track(live);
            var deadRef = s.Track(dead);
            var nonFinite = Scene.NewEnemy(id: 9, pointer: 30);
            var nonFiniteRef = s.Track(nonFinite);
            nonFinite.Position = (float.NaN, 0f, 0f);
            var result = s.Dispatch(EnemyVolumeContract.VolumeCapability,
                Volume(new[] { deadRef, nonFiniteRef }), Parameters());
            Check(result.Status == CommandStatuses.Rejected, "An unplaceable anchor was not refused: " + result.Status);
            Check(Code(result, 0) == "not-alive", "A dead anchor was not refused by name: " + Code(result, 0));
            Check(Code(result, 1) == "enemy-position-unavailable", "A non-finite position was not refused by name: " + Code(result, 1));
            Check(EffectVolumeManager.Registered.Count == 0, "A refused anchor still registered a volume.");
        });

        Case("effect_volume.a-full-fog-budget-does-not-fail-the-volume", () =>
        {
            using var s = new Scene();
            FogSphereAllocator.RefuseAllocation = true;
            try
            {
                var enemy = Scene.NewEnemy();
                var result = s.Dispatch(EnemyVolumeContract.VolumeCapability,
                    Volume(new[] { s.Track(enemy) }), Parameters());
                Check(result.Status == CommandStatuses.Succeeded && result.CommitState == CommitStates.Confirmed,
                    "A volume without a drawn sphere was not committed: " + result.Status + "/" + result.Code);
                Check(Code(result, 0) == "volume-placed-undrawn", "The undrawn placement was not reported: " + Code(result, 0));
                Check(EffectVolumeManager.Registered.Count == 1, "The volume itself was not registered.");
            }
            finally { FogSphereAllocator.RefuseAllocation = false; }
        });

        Case("effect_volume.a-client-is-refused-with-no-write", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            SNetwork.SNet.IsMaster = false;
            var result = s.Dispatch(EnemyVolumeContract.VolumeCapability,
                Volume(new[] { s.Track(enemy) }), Parameters(), isHost: false);
            Check(result.Status == CommandStatuses.Rejected && result.Code == "authority-or-phase",
                "A client was not refused by the authority gate: " + result.Status + "/" + result.Code);
            Check(EffectVolumeManager.Registered.Count == 0, "A client wrote the world.");
        });

        Case("remove.takes-the-life-out-through-the-games-own-despawn", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            var replicator = (SNetwork.Replicator)enemy.Sync!.Replicator!;
            replicator.OnDespawn = () => s.Retire(reference);
            var result = s.Dispatch(EnemyNodeEffectContract.RemoveCapability,
                new { targets = new[] { reference } });
            Check(result.Status == CommandStatuses.Succeeded, $"Expected succeeded, got {result.Status}/{result.Code}.");
            Check(result.CommitState == CommitStates.Confirmed, "A removal the game confirmed was not confirmed.");
            Check(replicator.Despawns == 1, "The despawn entry did not run exactly once.");
            Check(enemy.Damage!.InstantDeaths == 0, "The removal went through the death settlement instead.");
            Check(Code(result, 0) == "committed", "The row code is not committed: " + Code(result, 0));
        });

        Case("remove.reports-a-despawn-it-cannot-see-as-unknown", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            var result = s.Dispatch(EnemyNodeEffectContract.RemoveCapability,
                new { targets = new[] { reference } });
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown,
                "A despawn that left the life resolvable was not reported as unknown: " + result.Status + "/" + result.CommitState);
            Check(Code(result, 0) == "removal-unseen", "The unseen removal was not named: " + Code(result, 0));
        });

        Case("remove.refuses-what-it-cannot-take-out", () =>
        {
            using var s = new Scene();
            var dead = Scene.NewEnemy();
            dead.Alive = false;
            var stale = Scene.NewEnemy(id: 8, pointer: 20);
            var noReplicator = Scene.NewEnemy(id: 9, pointer: 30);
            noReplicator.Sync!.Replicator = null;
            var deadRef = s.Track(dead);
            var staleRef = s.Track(stale);
            var noReplicatorRef = s.Track(noReplicator);
            s.Retire(staleRef);
            s.Retire(noReplicatorRef);
            var result = s.Dispatch(EnemyNodeEffectContract.RemoveCapability,
                new { targets = new[] { deadRef, staleRef } });
            Check(Code(result, 0) == "not-alive", "A dead target was not refused by name: " + Code(result, 0));
            Check(Code(result, 1) == "stale-or-unsupported-recipient", "A retired life was not refused by name: " + Code(result, 1));
            Check(((SNetwork.Replicator)dead.Sync!.Replicator!).Despawns == 0, "A refused target was written.");

            var live = Scene.NewEnemy(id: 10, pointer: 40);
            live.Sync!.Replicator = null;
            var liveRef = s.Track(live);
            var missing = s.Dispatch(EnemyNodeEffectContract.RemoveCapability,
                new { targets = new[] { liveRef } });
            Check(missing.Status == CommandStatuses.Rejected && Code(missing, 0) == "replicator-unavailable",
                "An enemy without a replicator was not refused by name: " + missing.Status + "/" + Code(missing, 0));
        });

        Case("remove.a-client-is-refused-and-an-unknown-commit-stops-the-run", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var later = Scene.NewEnemy(id: 8, pointer: 20);
            var reference = s.Track(enemy);
            var laterRef = s.Track(later);
            SNetwork.SNet.IsMaster = false;
            var client = s.Dispatch(EnemyNodeEffectContract.RemoveCapability,
                new { targets = new[] { reference } }, isHost: false);
            Check(client.Status == CommandStatuses.Rejected && client.Code == "authority-or-phase",
                "A client was not refused by the authority gate: " + client.Status + "/" + client.Code);
            SNetwork.SNet.IsMaster = true;
            Check(((SNetwork.Replicator)enemy.Sync!.Replicator!).Despawns == 0, "A client wrote the world.");

            var throwing = Scene.NewEnemy(id: 9, pointer: 30);
            var throwingReplicator = (SNetwork.Replicator)throwing.Sync!.Replicator!;
            throwingReplicator.OnDespawn = () => throw new InvalidOperationException("boom");
            var throwingRef = s.Track(throwing);
            var result = s.Dispatch(EnemyNodeEffectContract.RemoveCapability,
                new { targets = new[] { throwingRef, laterRef } });
            Check(Status(result, 0) == CommandStatuses.Failed && Committed(result, 0) == CommitStates.Unknown,
                "A throwing despawn was not reported as an unknown commit.");
            Check(Code(result, 1) == "not-attempted-after-unknown-commit", "The run did not stop after an unknown commit.");
            Check(((SNetwork.Replicator)later.Sync!.Replicator!).Despawns == 0, "A target was written after an unknown commit.");
        });

        Case("remove.refuses-more-targets-than-a-result-can-carry", () =>
        {
            using var s = new Scene();
            var targets = new EntityReference[CommandResult.MaximumFacts + 1];
            for (int index = 0; index < targets.Length; index++)
                targets[index] = s.Track(Scene.NewEnemy(id: (ushort)(index + 1), pointer: 100 + index));
            var result = s.Dispatch(EnemyNodeEffectContract.RemoveCapability,
                new { targets });
            Check(result.Status == CommandStatuses.Rejected && result.Code == "too-many-targets",
                "An oversized dispatch was not refused: " + result.Status + "/" + result.Code);
            Check(EffectVolumeManager.Registered.Count == 0, "A refused dispatch wrote the world.");
        });
    }
}
