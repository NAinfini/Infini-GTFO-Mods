using Agents;
using Enemies;
using ForgeEnemy.Native;

/// <summary>
/// What a profile does to a live enemy, and where it is applied from. The application point is the production
/// spawn path — `TrackSpawn` is what the receiver reaches through its own `EnemySync.OnSpawn` postfix — so these
/// cases drive that method rather than the applier directly, and the native writes land on the same doubles the
/// other native-evidence suites compile.
///
/// The statements here are about the values written, the native entry point used for each one, what is
/// deliberately left alone, and how often a repeated situation is reported. Whether the receiver honours each
/// write in a running game is not claimed: nothing here executes GTFO.
/// </summary>
internal static class ApplyCases
{
    /// <summary>Installs one accepted document and hands it to the module the way `Plugin.Load` does.</summary>
    private static EnemyProfileStore Load(Scene scene, string json)
    {
        var store = scene.Install("pkg", "a.json", json);
        scene.Module.LoadProfiles(store);
        return store;
    }

    internal static void Run()
    {
        T.Case("apply.limb.health-keeps-the-ratio-the-receiver-chose", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0, \"health\": 400 } ] }")));
            var enemy = Scene.NewEnemy();
            scene.Module.TrackSpawn(enemy);
            // The limb started at 50 of 200 — a quarter hurt — so it must stand at 100 of 400, not at 400 or 50.
            var limb = enemy.Damage.DamageLimbs[0];
            T.Near(400f, limb.m_healthMax, "the authored maximum was replaced");
            T.Near(100f, limb.m_health, "the current health kept its ratio");
        });

        T.Case("apply.limb.kind-goes-through-the-native-setter", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11(
                "{ \"limbs\": [ { \"limbId\": 0, \"type\": \"armor\", \"weakspotMultiplier\": 3, \"armorMultiplier\": 0.25 } ] }")));
            var enemy = Scene.NewEnemy();
            scene.Module.TrackSpawn(enemy);
            var limb = enemy.Damage.DamageLimbs[0];
            T.Check(limb.TypeSets == 1, "the receiver's own setter was used once");
            T.Check(limb.m_type == eLimbDamageType.Armor, "the limb role");
            T.Near(3f, limb.m_weakspotDamageMulti, "weakspot multiplier");
            T.Near(0.25f, limb.m_armorDamageMulti, "armor multiplier");
        });

        T.Case("apply.limb.an-unlisted-limb-is-untouched", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0, \"health\": 400 } ] }")));
            var enemy = Scene.NewEnemy();
            scene.Module.TrackSpawn(enemy);
            var untouched = enemy.Damage.DamageLimbs[1];
            T.Near(100f, untouched.m_healthMax, "a limb the document does not name keeps its authored maximum");
            T.Near(100f, untouched.m_health, "a limb the document does not name keeps its authored health");
            T.Check(untouched.TypeSets == 0, "a limb the document does not name keeps its authored role");
        });

        T.Case("apply.limb.a-limb-the-type-does-not-declare-is-reported-once", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0, \"health\": 400 }, { \"limbId\": 9, \"health\": 1 } ] }")));
            var first = Scene.NewEnemy(globalId: 7, pointer: 10);
            scene.Module.TrackSpawn(first);
            T.Near(400f, first.Damage.DamageLimbs[0].m_healthMax, "the limb the type declares was still written");
            T.Check(scene.Messages.Count == 1 && scene.Messages[0].Contains("does not declare: 9"),
                "the missing limb was named: " + string.Join(" | ", scene.Messages));
            // A second enemy of the same type repeats the same sentence, which is why it is not said twice.
            scene.Module.TrackSpawn(Scene.NewEnemy(globalId: 8, pointer: 20));
            T.Check(scene.Messages.Count == 1, "the same enemy type is diagnosed once per session");
        });

        T.Case("apply.detection.writes-the-four-parameters-only", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11(
                "{ \"detection\": { \"movementDistance\": 30, \"buildupSpeed\": 0.5, \"cooldownSpeed\": 2, \"noiseRange\": 12 } }")));
            var enemy = Scene.NewEnemy();
            scene.Module.TrackSpawn(enemy);
            var detection = enemy.AI.m_detection;
            T.Near(30f, detection.m_movementDetectionDistance, "movement distance");
            T.Near(0.5f, detection.m_detectionBuildupSpeed, "buildup speed");
            T.Near(2f, detection.m_detectionCooldownSpeed, "cooldown speed");
            T.Near(12f, detection.m_noiseDetectionRange, "noise range");
            // Switching noise detection on is a behaviour, not a value: a profile that turned it on would make an
            // enemy notice sounds it was authored to ignore, so the switch is not the data layer's to flip.
            T.Check(!detection.m_noiseDetectionOn, "the noise switch is left as the prefab authored it");
        });

        T.Case("apply.appearance.glow-uses-the-native-interpolator", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"appearance\": { \"glowColor\": [1,0.5,0.25,0.75], \"glowTransition\": 1.5 } }")));
            var enemy = Scene.NewEnemy();
            scene.Module.TrackSpawn(enemy);
            var appearance = enemy.Appearance;
            T.Check(appearance.Interpolations == 1, "the receiver's own glow entry point was used once");
            T.Near(1f, appearance.LastGlow.r, "red");
            T.Near(0.5f, appearance.LastGlow.g, "green");
            T.Near(0.25f, appearance.LastGlow.b, "blue");
            T.Near(0.75f, appearance.LastGlow.a, "alpha");
            T.Near(1.5f, appearance.LastTransition, "the transition the document named");
        });

        T.Case("apply.appearance.glow-without-a-transition-is-immediate", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"appearance\": { \"glowColor\": [0,1,0] } }")));
            var enemy = Scene.NewEnemy();
            scene.Module.TrackSpawn(enemy);
            T.Near(0f, enemy.Appearance.LastTransition, "no transition means the colour is immediate");
            T.Near(1f, enemy.Appearance.LastGlow.a, "a three-component colour is opaque");
        });

        T.Case("apply.spawn.a-profile-goes-on-once-per-native-life", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"appearance\": { \"glowColor\": [0,0,1] }, \"detection\": { \"movementDistance\": 30 } }")));
            var enemy = Scene.NewEnemy();
            var reference = scene.Module.TrackSpawn(enemy);
            // The game reaches the spawn postfix more than once for one native life; the second call resolves the
            // same life and must not write a second time.
            var again = scene.Module.TrackSpawn(enemy);
            T.Check(reference == again, "the same native life keeps its reference");
            T.Check(enemy.Appearance.Interpolations == 1, "the profile was written once");
            T.Near(30f, enemy.AI.m_detection.m_movementDetectionDistance, "and the detection values were not re-based on themselves");
        });

        T.Case("apply.spawn.an-enemy-with-no-entry-is-untouched", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"appearance\": { \"glowColor\": [1,0,0] } }")));
            var enemy = Scene.NewEnemy(typeId: 99, globalId: 9, pointer: 30);
            scene.Module.TrackSpawn(enemy);
            T.Check(enemy.Appearance.Interpolations == 0, "no colour was written");
            T.Check(scene.Messages.Count == 0, "no profile is not a diagnostic");
        });

        T.Case("apply.spawn.an-unreadable-type-applies-nothing", () =>
        {
            using var scene = new Scene(profileTypeReadable: false);
            Load(scene, Scene.Bag(Scene.For11("{ \"appearance\": { \"glowColor\": [1,0,0] } }")));
            var enemy = Scene.NewEnemy();
            scene.Module.TrackSpawn(enemy);
            T.Check(enemy.Appearance.Interpolations == 0, "an enemy whose type cannot be read has no profile");
            T.Check(scene.Messages.Count == 0, "an unreadable type is not a refusal");
        });

        T.Case("apply.spawn.an-enemy-type-two-files-claim-applies-nothing-and-is-reported-once", () =>
        {
            using var scene = new Scene();
            scene.Install("pkg", "a.json", Scene.Bag(Scene.For11("{ \"appearance\": { \"glowColor\": [1,0,0] } }")));
            scene.Module.LoadProfiles(scene.Install("pkg", "b.json",
                Scene.Bag(Scene.For11("{ \"appearance\": { \"glowColor\": [0,1,0] } }"))));
            var first = Scene.NewEnemy(globalId: 7, pointer: 10);
            scene.Module.TrackSpawn(first);
            T.Check(first.Appearance.Interpolations == 0, "nothing is written for a refused enemy");
            T.Check(scene.Messages.Count == 1 && scene.Messages[0].Contains("a.json") && scene.Messages[0].Contains("b.json"),
                "the duplicate names both files: " + string.Join(" | ", scene.Messages));
            scene.Module.TrackSpawn(Scene.NewEnemy(globalId: 8, pointer: 20));
            T.Check(scene.Messages.Count == 1, "the duplicate is reported once per enemy type");
        });

        T.Case("apply.spawn.a-component-the-type-does-not-have-is-a-skip", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11(
                "{ \"appearance\": { \"glowColor\": [1,0,0] }, \"detection\": { \"movementDistance\": 30 } }")));
            var enemy = Scene.NewEnemy();
            enemy.Appearance = null!;
            scene.Module.TrackSpawn(enemy);
            T.Near(30f, enemy.AI.m_detection.m_movementDetectionDistance, "the component the type does have was written");
            T.Check(scene.Messages.Count == 0, "an absent component is a skip, not a failure");
        });

        T.Case("apply.birthing.writes-the-five-numbers-on-the-types-own-component", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11(
                "{ \"birthing\": { \"childrenPerBirth\": 3, \"childrenPerBirthMin\": 2, \"childrenMax\": 5, \"minDelayUntilNextBirth\": 2.5, \"maxDelayUntilNextBirth\": 9 } }")));
            var enemy = Scene.NewEnemy();
            var birthing = new EAB_Birthing();
            enemy.Abilities = new() { AllComps = new EnemyAbility[] { birthing } };
            scene.Module.TrackSpawn(enemy);
            T.Check(birthing.m_childrenPerBirth == 3, "children per birth");
            T.Check(birthing.m_childrenPerBirthMin == 2, "the smallest birth");
            T.Check(birthing.m_childrenMax == 5, "the total cap");
            T.Near(2.5f, birthing.m_minDelayUntilNextBirth, "the shortest wait");
            T.Near(9f, birthing.m_maxDelayUntilNextBirth, "the longest wait");
            T.Check(scene.Messages.Count == 0, "a writable component reports nothing");
        });

        T.Case("apply.birthing.a-number-the-document-leaves-out-keeps-the-prefabs-own", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"birthing\": { \"childrenMax\": 0 } }")));
            var enemy = Scene.NewEnemy();
            var birthing = new EAB_Birthing();
            enemy.Abilities = new() { AllComps = new EnemyAbility[] { birthing } };
            scene.Module.TrackSpawn(enemy);
            T.Check(birthing.m_childrenMax == 0, "a zero the document stated is written");
            T.Check(birthing.m_childrenPerBirth == 1, "an unstated count stays as the prefab authored it");
            T.Near(5f, birthing.m_minDelayUntilNextBirth, "an unstated delay stays as the prefab authored it");
        });

        T.Case("apply.birthing.an-enemy-without-the-component-is-a-skip", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"birthing\": { \"childrenPerBirth\": 4 } }")));
            var enemy = Scene.NewEnemy();
            enemy.Damage.DamageLimbs[0].m_healthMax = 321f;
            // A type whose ability list carries other kinds, exactly as a non-birthing enemy does: the lookup finds
            // no component of the kind and the profile writes nothing rather than a wrong component.
            var melee = new EnemyAbility { m_abilityType = AgentAbility.Melee };
            enemy.Abilities = new() { AllComps = new EnemyAbility[] { melee } };
            scene.Module.TrackSpawn(enemy);
            T.Check(scene.Messages.Count == 0, "an absent component is a skip, not a failure");
            T.Near(321f, enemy.Damage.DamageLimbs[0].m_healthMax, "the rest of the enemy is untouched");
        });

        T.Case("apply.load.refusals-are-reported-with-their-codes", () =>
        {
            using var scene = new Scene();
            scene.Install("pkg", "a.json", "{ not json");
            var store = scene.Install("pkg", "b.json", Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0 } ] }")));
            scene.Module.LoadProfiles(store);
            T.Check(scene.Module.ProfileRejectionCount == 2, "both refusals reached the module");
            T.Check(scene.Messages.Any(m => m.Contains("profile-json")) && scene.Messages.Any(m => m.Contains("profile-limb")),
                "each refusal was reported with its code: " + string.Join(" | ", scene.Messages));
            T.Check(scene.Module.ProfileEnemyCount == 0, "no entry from a refused document is reachable");
        });

        T.Case("apply.load.a-throwing-logger-cannot-abort-the-spawn-path", () =>
        {
            using var scene = new Scene();
            Load(scene, Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 9, \"health\": 1 } ], \"appearance\": { \"glowColor\": [1,0,0] } }")));
            scene.ThrowOnReport = true;
            var enemy = Scene.NewEnemy();
            var reference = scene.Module.TrackSpawn(enemy);
            T.Check(reference != null, "the spawn path returned its reference");
            T.Check(enemy.Appearance.Interpolations == 1, "the profile was applied even though the logger threw");
        });
    }
}
