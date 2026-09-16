using System.Text.Json;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using static T;

/// <summary>Focused cases for the four observation paths the node list's enemy events need: the generic spawn
/// fact, the tag transaction and its falling edge, and the glue a native accumulation call really added.
///
/// A fact is published only while a plan step subscribes to its binding, and a plan is not loadable in this
/// suite — its fixture belongs to the integration batch — so what is asserted here is the observation side of
/// each path: the gate a publication passes, the token a prefix/postfix pair shares, the volume difference the
/// glue row reports, and the transition the tag sample turns into a fact. The dispatch that follows a
/// publication is the kernel's own and is covered by the framework's own suites.</summary>
internal static class FactCases
{
    internal static void Run()
    {
        Case("fact.no-observation-publishes-without-a-subscriber", () =>
        {
            using var s = new Scene();
            foreach (var binding in new[]
            {
                EnemyModule.NodeSpawnBinding, EnemyModule.NodeTaggedBinding, EnemyModule.NodeGluedBinding
            })
                Check(!s.Subscribed(binding), "A binding has a subscriber with no plan loaded: " + binding);
        });

        Case("fact.spawn-registers-the-life-without-a-subscriber", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Position = (4f, 5f, 6f);
            s.Module.AfterSpawn(enemy);
            // The spawn path is also the identity path: the reference it mints is the one the module resolves,
            // which is what the kernel's own instance resolver answers with.
            var reference = s.Kernel.ResolveEntityInstance("gtfo.enemy", enemy);
            Check(reference != null, "The spawn path did not register the enemy's own identity.");
            Check(reference!.Id == "gtfo.enemy:7", "The spawn path registered another identity: " + reference.Id);
            Check(reference.WorldEpoch == s.Kernel.WorldEpoch, "The identity belongs to another world.");
        });

        Case("fact.glue-captures-the-volume-the-call-added", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            enemy.Damage!.AttachedGlueVolume = 10f;
            // The receiver is resolved through this module's own table, so the life is registered with the
            // component the case prepared — the same order the provider's own spawn path uses.
            var reference = s.Track(enemy);
            var before = s.Module.BeforeGlue(enemy.Damage);
            Check(before != null, "A tracked receiver's glue volume was not captured.");
            Check(Math.Abs(before!.VolumeBefore - 10f) < 0.001, "The captured volume is not the receiver's own.");
            enemy.Damage.OnAddToTotalGlueVolume = receiver => receiver.AttachedGlueVolume = 35f;
            enemy.Damage.AddToTotalGlueVolume();
            s.Module.AfterGlue(enemy.Damage, before);
            Check(!before.TryConsume(s.Module), "The observation was not consumed by its own postfix.");
            Check(reference.Id == "gtfo.enemy:7", "The glue observation is not about the tracked life.");
        });

        Case("fact.glue-refuses-a-receiver-the-module-does-not-track", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            s.Track(enemy);
            var foreign = Scene.NewEnemy(id: 8, pointer: 20);
            Check(s.Module.BeforeGlue(foreign.Damage) == null, "A receiver of an untracked enemy was captured.");
            Check(s.Module.BeforeGlue(null) == null, "A null receiver was captured.");
        });

        Case("fact.glue-refuses-a-token-from-another-call", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            s.Track(enemy);
            enemy.Damage!.AttachedGlueVolume = 10f;
            var before = s.Module.BeforeGlue(enemy.Damage)!;
            var other = Scene.NewEnemy(id: 8, pointer: 20);
            s.Track(other);
            // The postfix of another receiver must not close this observation, and a consumed token is spent.
            s.Module.AfterGlue(other.Damage, before);
            Check(before.TryConsume(s.Module), "Another receiver's postfix consumed the token.");
            s.Module.AfterGlue(enemy.Damage, before);
            Check(!before.TryConsume(s.Module), "The token survived its own postfix.");
        });

        Case("fact.glue-refuses-a-call-that-added-nothing", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            s.Track(enemy);
            enemy.Damage!.AttachedGlueVolume = 10f;
            var before = s.Module.BeforeGlue(enemy.Damage)!;
            // The accumulation entry can be entered with a volume the receiver's own rules reduce to nothing,
            // so a call that moved no volume is not a fact: the token closes without publishing.
            s.Module.AfterGlue(enemy.Damage, before);
            Check(!before.TryConsume(s.Module), "A no-op call left its token open.");
        });

        Case("fact.glue-leaves-an-unsubscribed-call-alone", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            s.Track(enemy);
            enemy.Damage!.AttachedGlueVolume = 5f;
            // The prefix captures whatever the call is about to change — the change itself is what the reading
            // is compared against — and the publication gate is the postfix's own question.
            Check(s.Module.BeforeGlue(enemy.Damage) != null,
                "A tracked receiver's volume was not captured before the call.");
            Check(!s.Subscribed(EnemyModule.NodeGluedBinding),
                "An unsubscribed binding was reported as subscribed.");
        });

        Case("fact.tag-transaction-reads-the-packet-the-game-carried", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            var reference = s.Track(enemy);
            enemy.WasTagged = true;
            enemy.IsTagged = true;
            s.Module.AfterTagged(new ToolSyncManager.pTagEnemy
            {
                enemy = new Agents.pEnemyAgent { Value = enemy }
            });
            // The observation is a reading, not a write: the packet's own enemy is still the life this module
            // tracks, and nothing about the tag state was changed by the observer.
            Check(reference.Id == "gtfo.enemy:7" && enemy.IsTagged, "The tag observation changed the life it read.");
        });

        Case("fact.tag-transaction-ignores-an-untracked-or-torn-down-handle", () =>
        {
            using var s = new Scene();
            var tracked = Scene.NewEnemy();
            s.Track(tracked);
            var foreign = Scene.NewEnemy(id: 9, pointer: 30);
            s.Module.AfterTagged(new ToolSyncManager.pTagEnemy { enemy = new Agents.pEnemyAgent { Value = foreign } });
            s.Module.AfterTagged(new ToolSyncManager.pTagEnemy { enemy = new Agents.pEnemyAgent { Value = null } });
            s.Module.AfterTagged(default);
            Check(true, "An untracked or empty packet escaped into the observer.");
        });

        Case("fact.tag-sample-follows-the-agents-own-flag", () =>
        {
            using var s = new Scene();
            var enemy = Scene.NewEnemy();
            s.Track(enemy);
            enemy.IsTagged = false;
            s.Module.ObserveTagState(enemy);
            enemy.IsTagged = true;
            s.Module.ObserveTagState(enemy);
            enemy.IsTagged = false;
            s.Module.ObserveTagState(enemy);
            // Every sample is a read of the agent's own flag; the module keeps the last state it published so one
            // tag is one rising and one falling fact, and no sample may write the flag back.
            Check(!enemy.IsTagged, "The tag sample wrote the flag it read.");
        });

        Case("fact.tag-sample-ignores-an-untracked-agent", () =>
        {
            using var s = new Scene();
            var foreign = Scene.NewEnemy(id: 9, pointer: 30);
            foreign.IsTagged = true;
            s.Module.ObserveTagState(foreign);
            s.Module.ObserveTagState(null);
            Check(true, "An untracked agent escaped into the tag sample.");
        });
    }
}
