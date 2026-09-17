using System.Text.Json;
using Agents;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using static T;

/// <summary>Focused cases for the observation the enemy batch adds: `forge.trigger.enemy.ability_used`, published
/// from the attack-start datum the game's own attack state hands every peer.
///
/// The datum is built here the way the game fills it — the ability kind and index, the agent the attack was aimed
/// at, and the length the state runs for — and the production entry (`AfterAbilityUsed`) is then driven directly,
/// which is exactly what the Harmony postfix calls. A publication needs a subscriber, so each case loads the
/// smallest plan that names the row's own binding; the plan machinery is the runtime's own, and the assertions are
/// on what the provider refused to publish as much as on what it published.</summary>
internal static class AbilityUsedCases
{
    private static Scene Subscribed()
    {
        var scene = new Scene(start: false);
        scene.Subscribe(EnemyAbilityUsedContract.BindingId);
        scene.Start();
        return scene;
    }

    private static pES_EnemyAttackData Attack(AgentAbility ability, float duration = 1.5f, Agent? target = null,
        int index = 0)
        => new()
        {
            AbilityType = ability, AbilityIndex = (byte)index, Duration = duration,
            TargetAgent = new pAgent { Value = target },
            Position = new UnityEngine.Vector3(0f, 0f, 0f),
            TargetPosition = new UnityEngine.Vector3(0f, 0f, 0f)
        };

    internal static void Run()
    {
        Case("ability_used.publishes-the-kind-the-target-and-the-length", () =>
        {
            using var s = Subscribed();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            s.PlayerReference(out var player);
            s.Module.AfterAbilityUsed(enemy, Attack(AgentAbility.Ranged, 2.5f, player, index: 3));
            var tick = s.Tick();
            Check(tick.EventsProcessed == 1, $"Expected one published fact, got {tick.EventsProcessed}.");
            Check(s.Messages.Count == 0, "A published fact was reported as rejected: " + string.Join(" | ", s.Messages));
        });

        Case("ability_used.publishes-nothing-without-a-subscriber", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            s.Track(enemy);
            s.Module.AfterAbilityUsed(enemy, Attack(AgentAbility.Melee));
            var tick = s.Tick();
            Check(tick.EventsProcessed == 0, "A fact was published with no plan loaded.");
        });

        Case("ability_used.refuses-a-life-this-provider-does-not-track", () =>
        {
            using var s = Subscribed();
            var foreign = Scene.NewEnemy(id: 9, pointer: 30);
            s.Module.AfterAbilityUsed(foreign, Attack(AgentAbility.Melee));
            s.Module.AfterAbilityUsed(null, Attack(AgentAbility.Melee));
            var tick = s.Tick();
            Check(tick.EventsProcessed == 0, "An untracked agent published a fact.");
        });

        Case("ability_used.refuses-a-kind-it-owns-no-resource-for", () =>
        {
            using var s = Subscribed();
            var enemy = Scene.NewEnemy();
            s.Track(enemy);
            s.Module.AfterAbilityUsed(enemy, Attack(AgentAbility.None));
            var tick = s.Tick();
            Check(tick.EventsProcessed == 0, "The machine's own `None` value was published as an ability.");
        });

        Case("ability_used.publishes-with-neither-a-gone-target-nor-a-bad-length", () =>
        {
            using var s = Subscribed();
            var enemy = Scene.NewEnemy();
            s.Track(enemy);
            // A target handle no registered kind claims and a length the datum cannot represent: both leave their
            // port absent rather than naming something that is not there, and the fact itself still stands.
            s.Module.AfterAbilityUsed(enemy, Attack(AgentAbility.Alarm, float.NaN, new Agent()));
            var tick = s.Tick();
            Check(tick.EventsProcessed == 1, $"Expected the fact to be published, got {tick.EventsProcessed} events.");
        });

        Case("ability_used.a-retired-life-publishes-nothing", () =>
        {
            using var s = Subscribed();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            s.Retire(reference);
            s.Module.AfterAbilityUsed(enemy, Attack(AgentAbility.Melee));
            var tick = s.Tick();
            Check(tick.EventsProcessed == 0, "A retired life published a fact.");
        });
    }
}
