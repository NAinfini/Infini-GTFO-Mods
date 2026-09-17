using ForgeEnemy.Native;
using ForgeEnemy.Profile;

/// <summary>
/// What a document may say and what the store does with it. Every case writes a real file below a real plugin
/// root and reads it back through the production discovery, so the folder convention, the per-enemy expansion and
/// the one-enemy-type-one-document rule are exercised with the same code an installation runs.
///
/// The refusal cases assert the code, not the sentence: the detail is for the author, the code is the part the
/// rest of the system branches on. Each rule gets its own document that differs from an accepted document in
/// exactly that rule.
/// </summary>
internal static class DocumentCases
{
    /// <summary>One document, one refusal, and the code it must carry.</summary>
    private static void Refuses(string json, string code, string what)
    {
        using var scene = new Scene();
        T.Equal(code, Scene.CodeOf(scene.Install("pkg", "a.json", json)), what);
    }

    internal static void Run()
    {
        T.Case("profile.probe", () => T.Equal(
            "{ \"schemaVersion\": 1, \"enemies\": [ { \"enemyType\": 11, \"limbs\": [ { \"limbId\": 0, \"health\": 10 } ] } ] }",
            Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0, \"health\": 10 } ] }")), "probe"));

        T.Case("profile.discover.package-convention", () =>
        {
            using var scene = new Scene();
            var store = scene.Install("My.Package", "strikers.json",
                Scene.Bag(Scene.For11("{ \"detection\": { \"movementDistance\": 30 } }")));
            T.Check(store.DocumentCount == 1, "one document was installed");
            T.Check(store.EnemyCount == 1, "one enemy type was accepted");
            T.Near(30f, store.Resolve(11).Detection!.MovementDistance!.Value, "the enemy's own value");
        });

        T.Case("profile.discover.other-folders-are-not-profiles", () =>
        {
            using var scene = new Scene();
            string package = Path.Combine(scene.Root, "plugins", "My.Package");
            Directory.CreateDirectory(package);
            string profiles = Path.Combine(package, "forge", "enemies");
            Directory.CreateDirectory(Path.Combine(package, "forge", "other"));
            Directory.CreateDirectory(profiles);
            File.WriteAllText(Path.Combine(package, "forge", "other", "b.json"), Scene.Bag(Scene.For11("")));
            File.WriteAllText(Path.Combine(profiles, "notes.txt"), Scene.Bag(Scene.For11("")));
            var store = EnemyProfileStore.Discover(scene.Root);
            T.Check(store.DocumentCount == 0 && store.EnemyCount == 0, "only `forge/enemies/*.json` is read as profiles");
        });

        T.Case("profile.discover.absent-root-is-not-a-failure", () =>
        {
            var store = EnemyProfileStore.Discover(Path.Combine(Path.GetTempPath(), "forge-enemy-absent-" + Guid.NewGuid().ToString("N")));
            T.Check(store.DocumentCount == 0, "no document was found");
            T.Check(store.Rejections.Count == 0, "an installation with no profile is not a refusal");
            T.Check(store.Resolve(11).IsEmpty, "every enemy resolves to nothing");
        });

        T.Case("profile.duplicate.one-type-twice-in-one-file", () =>
        {
            Refuses(Scene.Bag(Scene.For11("{ \"detection\": { \"movementDistance\": 5 } }"), Scene.For11("{ \"detection\": { \"movementDistance\": 9 } }")),
                "profile-duplicate", "one enemy type declared twice in one file");
        });

        T.Case("profile.duplicate.one-type-in-two-files-refuses-the-type-whole", () =>
        {
            using var scene = new Scene();
            scene.Install("pkg", "a.json", Scene.Bag(Scene.For11("{ \"detection\": { \"movementDistance\": 5 } }")));
            var store = scene.Install("pkg", "b.json", Scene.Bag(Scene.For11("{ \"detection\": { \"movementDistance\": 9 } }")));
            T.Equal("profile-duplicate", Scene.CodeOf(store), "the second claim is a refusal");
            T.Check(store.Rejections[0].Detail.Contains("a.json", StringComparison.Ordinal)
                && store.Rejections[0].Detail.Contains("b.json", StringComparison.Ordinal),
                "the diagnostic names both files: " + store.Rejections[0].Detail);
            T.Check(store.Resolve(11).IsEmpty, "neither file's profile is applied to a refused enemy");
            T.Check(store.EnemyCount == 0, "the refused enemy is not in the table");
        });

        T.Case("profile.duplicate.a-third-file-cannot-revive-a-refused-type", () =>
        {
            using var scene = new Scene();
            scene.Install("pkg", "a.json", Scene.Bag(Scene.For11("")));
            scene.Install("pkg", "b.json", Scene.Bag(Scene.For11("")));
            var store = scene.Install("pkg", "c.json", Scene.Bag(Scene.For11("{ \"detection\": { \"movementDistance\": 5 } }")));
            T.Check(store.Resolve(11).IsEmpty, "a type two documents fought over stays refused");
        });

        T.Case("profile.rejections.one-bad-document-does-not-remove-another", () =>
        {
            using var scene = new Scene();
            scene.Install("pkg", "a.json", "{ not json");
            var store = scene.Install("pkg", "b.json", Scene.Bag(Scene.For11("{ \"detection\": { \"movementDistance\": 30 } }")));
            T.Check(store.DocumentCount == 1, "the readable document loaded");
            T.Equal("profile-json", Scene.CodeOf(store), "the unreadable document was refused with its own code");
        });

        T.Case("profile.enemy-type.missing", () => Refuses(Scene.Bag("{ \"detection\": { \"movementDistance\": 5 } }"),
            "profile-enemy-type", "an enemy with no type"));
        T.Case("profile.enemy-type.zero", () => Refuses(Scene.Bag("{ \"enemyType\": 0 }"), "profile-enemy-type", "enemy type zero"));
        T.Case("profile.enemy-type.negative", () => Refuses(Scene.Bag("{ \"enemyType\": -3 }"), "profile-enemy-type", "a negative enemy type"));
        T.Case("profile.enemy-type.not-a-number", () => Refuses(Scene.Bag("{ \"enemyType\": \"11\" }"), "profile-enemy-type", "a string enemy type"));

        T.Case("profile.limbs.health-multipliers-and-kind", () =>
        {
            using var scene = new Scene();
            var store = scene.Install("pkg", "a.json", Scene.Bag(Scene.For11(
                "{ \"limbs\": [ { \"limbId\": 0, \"health\": 12.5 }, { \"limbId\": 1, \"weakspotMultiplier\": 2, \"armorMultiplier\": 0.5, \"type\": \"weakspot\" } ] }")));
            var limbs = store.Resolve(11).Limbs;
            T.Check(limbs.Count == 2, "two limbs");
            T.Near(12.5f, limbs[0].Health!.Value, "health");
            T.Check(limbs[1].Kind == EnemyLimbKind.Weakspot, "kind");
            T.Near(0.5f, limbs[1].ArmorMultiplier!.Value, "armor multiplier");
        });

        T.Case("profile.limbs.duplicate-limb-id", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0, \"health\": 1 }, { \"limbId\": 0, \"health\": 2 } ] }")),
                "profile-duplicate", "one limb twice"));
        T.Case("profile.limbs.missing-id", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"limbs\": [ { \"health\": 1 } ] }")), "profile-limb", "a limb with no id"));
        T.Case("profile.limbs.empty-limb", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0 } ] }")), "profile-limb", "a limb that sets nothing"));
        T.Case("profile.limbs.health-not-positive", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0, \"health\": 0 } ] }")), "profile-number", "health zero"));
        T.Case("profile.limbs.kind-unknown", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0, \"type\": \"core\" } ] }")), "profile-limb", "an unknown limb kind"));
        T.Case("profile.limbs.not-an-array", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"limbs\": { } }")), "profile-limb", "a limbs object"));

        T.Case("profile.detection.four-numbers-read-back", () =>
        {
            using var scene = new Scene();
            var store = scene.Install("pkg", "a.json", Scene.Bag(Scene.For11(
                "{ \"detection\": { \"movementDistance\": 30, \"buildupSpeed\": 1.5, \"cooldownSpeed\": 0.75, \"noiseRange\": 12 } }")));
            var detection = store.Resolve(11).Detection!;
            T.Near(30f, detection.MovementDistance!.Value, "movement distance");
            T.Near(1.5f, detection.BuildupSpeed!.Value, "buildup");
            T.Near(0.75f, detection.CooldownSpeed!.Value, "cooldown");
            T.Near(12f, detection.NoiseRange!.Value, "noise range");
        });

        T.Case("profile.appearance.four-components-are-kept", () =>
        {
            using var scene = new Scene();
            var store = scene.Install("pkg", "a.json", Scene.Bag(Scene.For11("{ \"appearance\": { \"glowColor\": [1,0.5,0.25,0.75] } }")));
            var colour = store.Resolve(11).Appearance!.GlowColor!;
            T.Check(colour.Length == 4, "four components are kept");
            T.Equal("0.75", colour[3].ToString("0.00"), "alpha");
        });

        T.Case("profile.birthing.five-numbers-read-back", () =>
        {
            using var scene = new Scene();
            var store = scene.Install("pkg", "a.json", Scene.Bag(Scene.For11(
                "{ \"birthing\": { \"childrenPerBirth\": 3, \"childrenPerBirthMin\": 2, \"childrenMax\": 5, \"minDelayUntilNextBirth\": 2.5, \"maxDelayUntilNextBirth\": 9 } }")));
            var birthing = store.Resolve(11).Birthing!;
            T.Check(birthing.ChildrenPerBirth == 3 && birthing.ChildrenPerBirthMin == 2 && birthing.ChildrenMax == 5,
                "the three counts");
            T.Near(2.5f, birthing.MinDelayUntilNextBirth!.Value, "the shortest wait");
            T.Near(9f, birthing.MaxDelayUntilNextBirth!.Value, "the longest wait");
        });

        T.Case("profile.json.not-json", () => Refuses("{ not json", "profile-json", "unparsable text"));
        T.Case("profile.schema.root-not-object", () => Refuses("[ 1 ]", "profile-schema", "an array is not a document"));
        T.Case("profile.schema.version", () =>
            Refuses("{ \"schemaVersion\": 2, \"enemies\": [] }", "profile-schema", "another writer's version"));
        T.Case("profile.schema.enemies-not-an-array", () =>
            Refuses("{ \"schemaVersion\": 1, \"enemies\": { } }", "profile-schema", "an enemies object"));
        T.Case("profile.schema.empty-detection", () => Refuses(Scene.Bag(Scene.For11("{ \"detection\": { } }")), "profile-schema", "an empty block sets nothing"));
        T.Case("profile.schema.glow-transition-without-colour", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"appearance\": { \"glowTransition\": 1 } }")), "profile-schema", "a transition with no colour"));
        T.Case("profile.schema.birthing-empty-object-sets-nothing", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"birthing\": { } }")), "profile-schema", "an empty birthing block"));

        T.Case("profile.unknown-key.top-level", () =>
            Refuses("{ \"schemaVersion\": 1, \"enemy\": [] }", "profile-unknown-key", "a misspelled top-level key"));
        T.Case("profile.unknown-key.enemy", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"detectionn\": { } }")), "profile-unknown-key", "a misspelled enemy key"));
        T.Case("profile.unknown-key.limb", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"limbs\": [ { \"limbId\": 0, \"hp\": 1 } ] }")), "profile-unknown-key", "a misspelled limb key"));
        T.Case("profile.unknown-key.appearance", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"appearance\": { \"glow\": [0,0,0] } }")), "profile-unknown-key", "a misspelled appearance key"));
        T.Case("profile.unknown-key.birthing", () =>
            Refuses(Scene.Bag(Scene.For11("{ \"birthing\": { \"childernPerBirth\": 2 } }")), "profile-unknown-key", "a misspelled birthing key"));

        T.Case("profile.budget.too-many-enemies", () =>
        {
            var enemies = new string[EnemyProfileSchema.MaximumEnemiesPerDocument + 1];
            for (int index = 0; index < enemies.Length; index++) enemies[index] = Scene.For((uint)(100 + index));
            Refuses(Scene.Bag(enemies), "profile-budget", "more enemies than the cap");
        });

        T.Case("profile.budget.too-many-limbs", () =>
        {
            var limbs = new List<string>();
            for (int index = 0; index <= EnemyProfileSchema.MaximumLimbsPerEnemy; index++)
                limbs.Add("{ \"limbId\": " + index + ", \"health\": 1 }");
            Refuses(Scene.Bag(Scene.For11("{ \"limbs\": [ " + string.Join(",", limbs) + " ] }")),
                "profile-budget", "more limbs than the cap");
        });

        T.Case("profile.budget.too-many-bytes", () =>
        {
            using var scene = new Scene();
            var store = scene.Install("pkg", "a.json",
                "{ \"schemaVersion\": 1, \"enemies\": [], \"pad\": \"" + new string('x', EnemyProfileSchema.MaximumDocumentBytes) + "\" }");
            T.Equal("profile-budget", Scene.CodeOf(store), "a file above the byte cap is refused before it is parsed");
        });
    }
}
