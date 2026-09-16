using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using SNetwork;

/// <summary>Enemy AI behaviour facts: wake-up, target lock and scout events, driven by managed doubles of the
/// native pump. No GTFO code runs.</summary>
internal static class BehaviorFacts
{
    internal static List<Row> Run()
    {
        var rows = new List<Row>();
        void Case(string id, Action test)
        {
            try { test(); rows.Add(new(id, true, "passed")); }
            catch (Exception error) { rows.Add(new(id, false, error.ToString())); Console.Error.WriteLine("FAIL " + id + ": " + error.Message); }
            finally { SNet.IsMaster = true; }
        }
        void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

        // The pump's own discipline: what it reads, what it refuses to publish and which life a fact belongs to.
        // The awakened fact is the one state-derived fact, so leaving hibernation is the transition each case
        // drives the pump with.
        Case("pump.client-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Hibernating;
            Require(Scene.Dispatched(s.Frame()) == 0, "Entering hibernation published a wake-up.");
            SNet.IsMaster = false;
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 0, "A client published host-owned behaviour facts.");
        });
        Case("pump.gate-denied-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Hibernating;
            Require(Scene.Dispatched(s.Frame()) == 0, "Entering hibernation published a wake-up.");
            s.Allowed = false;
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 0, "A denied authority gate published.");
        });
        Case("pump.dead-enemy-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Hibernating;
            s.Frame();
            s.Enemy.Alive = false; s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 0, "A dead enemy published a behaviour fact.");
        });
        Case("pump.old-world-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Hibernating;
            s.Frame();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 1, "Leaving hibernation did not publish the awakened fact.");
            s.Kernel.BeginWorld(2);
            Require(Scene.Dispatched(s.Frame()) == 0, "A retired world published a wake-up.");
        });
        Case("pump.stale-life-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Hibernating;
            s.Frame();
            var retired = s.Module.CaptureDespawn(s.Enemy); s.Module.CompleteDespawn(retired);
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 0, "A retired life published a behaviour fact.");
        });
        Case("pump.foreign-instance-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Hibernating;
            s.Frame();
            var foreign = Scene.NewEnemy(9, 40); foreign.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            s.Module.ObserveEnemyBehavior(foreign.AI);
            Require(Scene.Dispatched(s.Tick()) == 0, "An untracked enemy instance published.");
        });
        Case("pump.no-subscriber-skips-the-read", () =>
        {
            using var s = new Scene();
            s.Enemy.AI.m_behaviour = null!;
            s.Module.ObserveEnemyBehavior(s.Enemy.AI);
            Require(Scene.Dispatched(s.Tick()) == 0, "The pump read native AI with no subscriber.");
        });
        Case("pump.disposed-module-skips-the-read", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Module.Dispose();
            s.Enemy.AI.m_behaviour = null!;
            s.Module.ObserveEnemyBehavior(s.Enemy.AI);
            Require(Scene.Dispatched(s.Tick()) == 0, "A disposed module read native AI.");
        });
        Case("awakened.leaves-hibernate", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Hibernating;
            Require(Scene.Dispatched(s.Frame()) == 0, "Entering hibernation published the awakened fact.");
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Patrolling;
            Require(Scene.Dispatched(s.Frame()) == 1, "Leaving hibernation did not publish the awakened fact.");
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 0, "An awake state change published the awakened fact again.");
        });
        Case("awakened.leaves-squid-boss-hibernate", () =>
        {
            // EB_States.SquidBoss_Hibernating is the boss baseline: leaving it is a wake-up, and a transition
            // between the two hibernating states is not.
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = (EB_States)12;
            Require(Scene.Dispatched(s.Frame()) == 0, "Entering the boss hibernation published a wake-up.");
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Patrolling;
            Require(Scene.Dispatched(s.Frame()) == 1, "Leaving the boss hibernation did not publish the awakened fact.");
            s.Enemy.AI.m_behaviour.m_currentStateName = (EB_States)0;
            Require(Scene.Dispatched(s.Frame()) == 0, "Entering hibernation published a wake-up.");
            s.Enemy.AI.m_behaviour.m_currentStateName = (EB_States)12;
            Require(Scene.Dispatched(s.Frame()) == 0, "A hibernating-to-hibernating transition published a wake-up.");
        });
        Case("awakened.patrol-start-is-not-waking", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 0, "A non-hibernating state change published the awakened fact.");
        });
        Case("target.acquired-with-resolvable-target", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.TargetAcquiredBinding); s.Start();
            s.Enemy.m_hasValidTarget = false; s.Enemy.AI.IsTargetValid = false;
            Require(Scene.Dispatched(s.Frame()) == 0, "The baseline target state published.");
            var other = Scene.NewEnemy(11, 50); s.Module.TrackSpawn(other);
            s.Enemy.AI.Target = new Agents.AgentTarget { m_agent = other, m_position = (1, 2, 3) };
            s.Enemy.AI.IsTargetValid = true; s.Enemy.m_hasValidTarget = true;
            Require(Scene.Dispatched(s.Frame()) == 1, "A resolvable acquired target did not publish.");
        });
        Case("target.acquired-unresolved-target-omits-the-port", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.TargetAcquiredBinding, targetClaimed: false); s.Start();
            s.Enemy.AI.Target = new Agents.AgentTarget { m_agent = Scene.NewEnemy(11, 50), m_position = (1, 2, 3) };
            s.Enemy.AI.IsTargetValid = true; s.Enemy.m_hasValidTarget = true;
            s.Frame();
            Require(s.Reported.Count == 0,
                "An unresolved target was reported as a null reference: " + string.Join("; ", s.Reported));
        });
        Case("target.lost-publishes-once", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.TargetLostBinding); s.Start();
            s.Enemy.AI.IsTargetValid = true; s.Enemy.m_hasValidTarget = true;
            Require(Scene.Dispatched(s.Frame()) == 0, "The baseline target state published.");
            s.Enemy.AI.IsTargetValid = false; s.Enemy.m_hasValidTarget = false;
            Require(Scene.Dispatched(s.Frame()) == 1, "Losing a target did not publish.");
            Require(Scene.Dispatched(s.Frame()) == 0, "The lost target published again.");
        });
        Case("scout.scream-phase-change", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.ScoutScreamBinding); s.Start();
            s.Enemy.AI.m_locomotion.CurrentStateEnum = ES_StateEnum.ScoutScream;
            s.Enemy.AI.m_locomotion.ScoutScream = new() { m_state = ScoutScreamState.Chargeup };
            Require(Scene.Dispatched(s.Frame()) == 1, "The first scout scream phase did not publish.");
            Require(Scene.Dispatched(s.Frame()) == 0, "The same scout scream phase published again.");
            s.Enemy.AI.m_locomotion.ScoutScream.m_state = ScoutScreamState.Scream;
            Require(Scene.Dispatched(s.Frame()) == 1, "A later scout scream phase did not publish.");
        });
        Case("scout.scream-outside-its-state-is-silent", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.ScoutScreamBinding); s.Start();
            s.Enemy.AI.m_locomotion.ScoutScream = new() { m_state = ScoutScreamState.Scream };
            Require(Scene.Dispatched(s.Frame()) == 0, "A saved scout scream state published outside its state.");
        });
        Case("scout.detection-publishes-target-and-position", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.ScoutDetectionBinding); s.Start();
            var target = new Agents.AgentTarget { m_agent = s.Enemy, m_position = (4, 5, 6) };
            s.Module.ObserveScoutDetection(s.Enemy, target);
            Require(Scene.Dispatched(s.Tick()) == 1, "A scout detection did not publish.");
        });
        Case("scout.detection-unresolved-target-still-publishes", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.ScoutDetectionBinding, targetClaimed: false); s.Start();
            var target = new Agents.AgentTarget { m_agent = Scene.NewEnemy(11, 50), m_position = (4, 5, 6) };
            s.Module.ObserveScoutDetection(s.Enemy, target);
            Require(Scene.Dispatched(s.Tick()) == 1, "A scout detection without a nameable target was dropped.");
        });
        Case("scout.detection-non-finite-position-is-not-a-fact", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.ScoutDetectionBinding); s.Start();
            var target = new Agents.AgentTarget { m_agent = s.Enemy, m_position = (float.NaN, 5, 6) };
            s.Module.ObserveScoutDetection(s.Enemy, target);
            Require(Scene.Dispatched(s.Tick()) == 0, "A non-finite scout position published.");
        });
        Case("gate.unregistered-module-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            s.Module.Dispose();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            s.Module.ObserveEnemyBehavior(s.Enemy.AI);
            Require(Scene.Dispatched(s.Tick()) == 0, "A disposed module published a behaviour fact.");
        });
        Case("gate.thread-off-pump-is-refused", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AwakenedBinding); s.Start();
            Exception? caught = null;
            try { Task.Run(() => s.Module.ObserveEnemyBehavior(s.Enemy.AI)).GetAwaiter().GetResult(); }
            catch (RuntimeContractException error) { caught = error; }
            Require(caught is RuntimeContractException contract && contract.Code == "wrong-thread",
                "An off-thread pump did not reject.");
        });
        return rows;
    }

    internal sealed record Row(string Id, bool Passed, string Detail);
}
