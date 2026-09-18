using System.Text.Json;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;
using static T;

/// <summary>Focused cases for the four enemy-domain Data reads that remain domain-specific after generic
/// position, life-state and health reads moved to ForgeRuntime.</summary>
internal static class ValueCases
{
    internal static void Run()
    {
        Case("value.type-answers-the-blocks-persistent-id", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Block = new GameData.EnemyDataBlock { persistentID = 421 };
            var reference = s.Track(enemy);
            Check(s.Evaluate(EnemyNodeValueContract.TypeCapability, new { enemy = reference }).GetProperty("value").GetString() == "421",
                "The enemy type is not the block's persistent id as decimal text.");
        });

        Case("value.type-refuses-when-the-block-does-not-read", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.TypeCapability, new { enemy = reference }))
                == EnemyNodeValueReads.TypeUnavailableCode, "A life with no readable block did not refuse by name.");
        });

        Case("value.sleeping-reads-the-behaviour-state", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            Check(!s.Evaluate(EnemyNodeValueContract.SleepingCapability, new { enemy = reference }).GetProperty("value").GetBoolean(),
                "A patrolling enemy read as hibernating.");
            enemy.AI!.m_behaviour!.m_currentStateName = EB_States.Hibernating;
            Check(s.Evaluate(EnemyNodeValueContract.SleepingCapability, new { enemy = reference }).GetProperty("value").GetBoolean(),
                "A hibernating enemy did not read as sleeping.");
            enemy.AI.m_behaviour.m_currentStateName = EB_States.SquidBoss_Hibernating;
            Check(s.Evaluate(EnemyNodeValueContract.SleepingCapability, new { enemy = reference }).GetProperty("value").GetBoolean(),
                "The boss's own hibernation is not treated as sleep.");
        });

        Case("value.sleeping-refuses-a-state-no-member-covers", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.AI = null;
            var reference = s.Track(enemy);
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.SleepingCapability, new { enemy = reference }))
                == EnemyNodeValueReads.StateUnavailableCode, "A life with no behaviour machine did not refuse by name.");
        });

        Case("value.tagged-answers-the-flag-and-the-time-left", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.IsTagged = true;
            enemy.EnemyTaggedTimer = 12.5f;
            var reference = s.Track(enemy);
            var answer = s.Evaluate(EnemyNodeValueContract.TaggedCapability, new { enemy = reference });
            Check(answer.GetProperty("value").GetBoolean(), "A tagged enemy did not read as tagged.");
            Check(Math.Abs(answer.GetProperty("remaining").GetDouble() - 12.5) < 0.001, "remaining is not the enemy's own timer.");
        });

        Case("value.group-answers-state-type-and-frustration", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.AI!.m_group = Scene.NewGroup(EGS.GuardsHunting, EnemyGroupType.Patrolling, 4.5f);
            var reference = s.Track(enemy);
            var answer = s.Evaluate(EnemyNodeValueContract.GroupCapability, new { enemy = reference });
            // Q3: an enum port carries the member index, so the state is the `enemy_group_state` position of the
            // native member and not its own name.
            Check(answer.GetProperty("state").GetInt32() == EnemyNodeValueReads.GroupStateIndex(EGS.GuardsHunting),
                "state is not the group's own `EGS` member index.");
            Check(EnemyNodeValueReads.GroupStateIndex(EGS.GuardsHunting) == 7,
                "the group-state index no longer matches the native member's own position.");
            Check(answer.GetProperty("group_type").GetString() == "patrolling",
                "group_type is not the group's own type member.");
            Check(Math.Abs(answer.GetProperty("patrol_frustration").GetDouble() - 4.5) < 0.001,
                "patrol_frustration is not the group's own counter.");
        });

        Case("value.group-refuses-a-member-the-shared-set-does-not-carry", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.AI!.m_group = Scene.NewGroup((EGS)200, EnemyGroupType.Patrolling, 0f);
            var reference = s.Track(enemy);
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.GroupCapability, new { enemy = reference }))
                == EnemyNodeValueReads.GroupUnavailableCode, "An out-of-vocabulary group state was reported.");
        });

        Case("value.group-refuses-a-life-the-game-put-in-no-group", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.GroupCapability, new { enemy = reference }))
                == EnemyNodeValueReads.GroupUnavailableCode, "A life with no group answered the group row.");
        });

        Case("value.group-refuses-a-group-replaced-under-the-read", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var first = Scene.NewGroup(EGS.Idle, pointer: 901);
            enemy.AI!.m_group = first;
            var reference = s.Track(enemy);
            // The first read answers the authored group; the read-back answers a group at another address, so
            // the row has no single group it can claim to have read and refuses instead of reporting either.
            var reads = 0;
            enemy.AI.OnGroupRead = () =>
            {
                if (++reads > 1) enemy.AI.Group = Scene.NewGroup(EGS.HuntersHunt, pointer: 902);
            };
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.GroupCapability, new { enemy = reference }))
                == EnemyNodeValueReads.GroupUnavailableCode, "A group swapped under the read was reported.");
        });

        Case("value.every-row-refuses-a-retired-life", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            s.Retire(reference);
            foreach (var capability in EnemyNodeValueContract.CapabilityIds)
                Check(Scene.Refusal(() => s.Evaluate(capability, new { enemy = reference })) != null,
                    "A retired life answered " + capability + ".");
        });

        Case("value.every-row-refuses-another-world", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            s.NextWorld(2);
            foreach (var capability in EnemyNodeValueContract.CapabilityIds)
                Check(Scene.Refusal(() => s.Evaluate(capability, new { enemy = reference })) != null,
                    "A reference from the old world answered " + capability + ".");
        });

        Case("value.every-row-refuses-a-frame-without-the-enemy-port", () =>
        {
            using var s = new Scene();
            // The row's port is required, so a frame that does not carry it is the caller's error and is
            // refused before the world is read at all — the kernel reports it through the same exception type
            // the row's own refusals use.
            foreach (var capability in EnemyNodeValueContract.CapabilityIds)
            {
                string? code = null;
                try { s.Evaluate(capability, new { subject = 1 }); }
                catch (RuntimeContractException error) { code = error.Code; }
                Check(code != null, "A frame without the entity port answered " + capability + ".");
            }
        });

        Case("value.rows-declare-the-query-tier-and-their-own-ports", () =>
        {
            // The rows are the ones this provider registers, so the shape the kernel resolved is the statement:
            // a row that declared a different port set would have failed registration in the scene's own
            // constructor. What is asserted here is the family's own vocabulary.
            Check(EnemyNodeValueContract.CapabilityIds.Length == 4, "The value family is not four domain-specific rows.");
            foreach (var capability in EnemyNodeValueContract.CapabilityIds)
                Check(capability.StartsWith("forge.query.enemy.", StringComparison.Ordinal),
                    "The row is not spelled as the values section's own grammar: " + capability);
            Check(EnemyNodeValueContract.TypeHandler != EnemyNodeValueContract.TaggedHandler, "Two rows share a handler.");
        });
    }
}
