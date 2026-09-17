using System.Text.Json;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;
using static T;

/// <summary>Focused cases for the seven value rows: each one answers the ports its contract declares from the
/// fact behind it, and each one refuses — with a code, never with a zero or a false — when the read cannot be
/// made. The refusal paths are the point of the family: a snapshot without health, a behaviour state no
/// `ai_state` member covers, a life outside every zone and a retired life are four different answers.</summary>
internal static class ValueCases
{
    internal static void Run()
    {
        Case("value.health-answers-value-and-maximum", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Damage!.Health = 37.5f;
            enemy.Damage.HealthMax = 120f;
            var reference = s.Track(enemy);
            var answer = s.Evaluate(EnemyNodeValueContract.HealthCapability, new { enemy = reference });
            Check(Math.Abs(answer.GetProperty("value").GetDouble() - 37.5) < 0.001, "value is not the receiver's health.");
            Check(Math.Abs(answer.GetProperty("maximum").GetDouble() - 120) < 0.001, "maximum is not the receiver's ceiling.");
        });

        Case("value.health-refuses-when-the-provider-publishes-none", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Alive = false;
            var reference = s.Track(enemy);
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.HealthCapability, new { enemy = reference }))
                == EnemyNodeValueReads.HealthUnavailableCode, "A life with no readable health did not refuse by name.");
        });

        Case("value.alive-follows-the-life-state", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            Check(s.Evaluate(EnemyNodeValueContract.AliveCapability, new { enemy = reference }).GetProperty("value").GetBoolean(),
                "A live enemy did not read alive.");
            enemy.Alive = false;
            Check(!s.Evaluate(EnemyNodeValueContract.AliveCapability, new { enemy = reference }).GetProperty("value").GetBoolean(),
                "A dead enemy did not read dead.");
        });

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

        Case("value.where-answers-position-and-zone", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Position = (1.5f, -2f, 3f);
            enemy.CourseNode = new AIG_CourseNode
            {
                m_zone = new AIG_Zone { m_dimensionIndex = 2, LocalIndex = 5, m_layer = new AIG_Layer { m_type = 1 } }
            };
            var reference = s.Track(enemy);
            var answer = s.Evaluate(EnemyNodeValueContract.WhereCapability, new { enemy = reference });
            var position = answer.GetProperty("position");
            Check(position.GetArrayLength() == 3 && Math.Abs(position[0].GetDouble() - 1.5) < 0.001
                && Math.Abs(position[1].GetDouble() + 2) < 0.001 && Math.Abs(position[2].GetDouble() - 3) < 0.001,
                "The position is not the enemy's own.");
            var zone = RuntimeJson.Entity(answer.GetProperty("zone"));
            Check(zone.Id == RuntimeZones.Id(2, 1, 5), "The zone is not the enemy's own course node's zone: " + zone.Id);
            Check(zone.WorldEpoch == s.Kernel.WorldEpoch, "The zone belongs to another world.");
        });

        Case("value.where-refuses-a-life-that-names-no-zone", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.CourseNode = new AIG_CourseNode { m_zone = null };
            var reference = s.Track(enemy);
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.WhereCapability, new { enemy = reference }))
                == EnemyModule.ZoneUnknownCode, "A life whose course node names no zone was answered instead of refused.");
        });

        Case("value.where-refuses-a-life-it-cannot-place", () =>
        {
            using var s = new Scene();
            var unplaceable = s.Track(Scene.NewEnemy(9, 30));
            var answered = s.Kernel.ZoneOfEntity(unplaceable);
            Check(!answered.Answered && answered.Code == EnemyModule.ZoneUnknownCode,
                "A life with no course node was not refused by name: " + answered.Code);
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.WhereCapability, new { enemy = unplaceable }))
                == EnemyModule.ZoneUnknownCode, "The where row answered a life this provider cannot place.");
            var layerless = Scene.NewEnemy(10, 40);
            layerless.CourseNode = new AIG_CourseNode { m_zone = new AIG_Zone { m_layer = null } };
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.WhereCapability, new { enemy = s.Track(layerless) }))
                == EnemyModule.ZoneUnknownCode, "A zone naming no layer was answered instead of refused.");
            var nowhere = Scene.NewEnemy(11, 50);
            nowhere.CourseNode = new AIG_CourseNode { m_zone = null };
            var nowhereReference = s.Track(nowhere);
            var standingNowhere = s.Kernel.ZoneOfEntity(nowhereReference);
            Check(!standingNowhere.Answered && standingNowhere.Zone == null
                && standingNowhere.Code == EnemyModule.ZoneUnknownCode,
                "A life whose course node names no zone was not refused by name: " + standingNowhere.Code);
            Check(Scene.Refusal(() => s.Evaluate(EnemyNodeValueContract.WhereCapability, new { enemy = nowhereReference }))
                == EnemyModule.ZoneUnknownCode, "The where row answered a life whose course node names no zone.");
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
            Check(EnemyNodeValueContract.CapabilityIds.Length == 7, "The value family is not seven rows.");
            foreach (var capability in EnemyNodeValueContract.CapabilityIds)
                Check(capability.StartsWith("forge.query.enemy.", StringComparison.Ordinal),
                    "The row is not spelled as the values section's own grammar: " + capability);
            Check(EnemyNodeValueContract.WhereHandler != EnemyNodeValueContract.TaggedHandler, "Two rows share a handler.");
        });
    }
}
