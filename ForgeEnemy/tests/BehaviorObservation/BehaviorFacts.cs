using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using SNetwork;

/// <summary>Enemy AI behaviour facts: state, wake-up, alert crossings, target lock and scout events, driven by
/// managed doubles of the native pump. No GTFO code runs.</summary>
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

        Case("state.change-publishes-once", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 1, "A state change did not publish exactly once.");
            Require(Scene.Dispatched(s.Frame()) == 0, "The unchanged state published again.");
        });
        Case("state.repeat-write-is-silent", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            for (var i = 0; i < 4; i++) s.Frame();
            Require(Scene.Dispatched(s.Frame()) == 0, "A repeated identical state published.");
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Dead;
            Require(Scene.Dispatched(s.Frame()) == 1, "A real transition after repeats did not publish.");
        });
        Case("state.spawn-state-is-not-a-transition", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            Require(Scene.Dispatched(s.Frame()) == 0, "The spawn state was published as a change.");
        });
        Case("state.client-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            SNet.IsMaster = false;
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 0, "A client published host-owned behaviour facts.");
        });
        Case("state.gate-denied-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start(); s.Allowed = false;
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 0, "A denied authority gate published.");
        });
        Case("state.dead-enemy-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            s.Enemy.Alive = false; s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.Dead;
            Require(Scene.Dispatched(s.Frame()) == 0, "A dead enemy published a behaviour fact.");
        });
        Case("state.world-change-clears-watermarks", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 1, "The first transition did not publish.");
            s.Kernel.BeginWorld(2); s.Module.TrackSpawn(s.Enemy);
            Require(Scene.Dispatched(s.Frame()) == 0, "A new world reused the previous life's state watermark.");
        });
        Case("state.stale-life-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            var retired = s.Module.CaptureDespawn(s.Enemy); s.Module.CompleteDespawn(retired);
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            Require(Scene.Dispatched(s.Frame()) == 0, "A retired life published a behaviour fact.");
        });
        Case("state.foreign-instance-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            var foreign = Scene.NewEnemy(9, 40); foreign.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            s.Module.ObserveEnemyBehavior(foreign.AI);
            Require(Scene.Dispatched(s.Tick()) == 0, "An untracked enemy instance published.");
        });
        Case("state.no-subscriber-skips-the-read", () =>
        {
            using var s = new Scene();
            s.Enemy.AI.m_behaviour = null!;
            s.Module.ObserveEnemyBehavior(s.Enemy.AI);
            Require(Scene.Dispatched(s.Tick()) == 0, "The pump read native AI with no subscriber.");
        });
        Case("state.disposed-module-skips-the-read", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
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
        Case("alert.crossing-publishes-and-does-not-repeat", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AlertChangedBinding); s.Start();
            s.Enemy.AI.m_detection.m_biggestDetectionBuildup = 0.3f;
            Require(Scene.Dispatched(s.Frame()) == 1, "An alert crossing did not publish.");
            Require(Scene.Dispatched(s.Frame()) == 0, "The same alert bucket published again.");
            s.Enemy.AI.m_detection.m_biggestDetectionBuildup = 0.1f;
            Require(Scene.Dispatched(s.Frame()) == 1, "A falling alert crossing did not publish.");
        });
        Case("alert.same-bucket-is-silent", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AlertChangedBinding); s.Start();
            s.Enemy.AI.m_detection.m_biggestDetectionBuildup = 0.3f;
            Require(Scene.Dispatched(s.Frame()) == 1, "The first crossing did not publish.");
            s.Enemy.AI.m_detection.m_biggestDetectionBuildup = 0.4f;
            Require(Scene.Dispatched(s.Frame()) == 0, "A value inside the same bucket published.");
        });
        Case("alert.non-finite-value-is-not-a-fact", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.AlertChangedBinding); s.Start();
            s.Enemy.AI.m_detection.m_biggestDetectionBuildup = float.NaN;
            Require(Scene.Dispatched(s.Frame()) == 0, "A non-finite detection value published.");
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
        Case("port.declared-state-values-are-mapped", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat_Stagger;
            Require(Scene.Dispatched(s.Frame()) == 1, "A mapped native state did not publish.");
        });
        Case("gate.unregistered-module-does-not-publish", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
            s.Module.Dispose();
            s.Enemy.AI.m_behaviour.m_currentStateName = EB_States.InCombat;
            s.Module.ObserveEnemyBehavior(s.Enemy.AI);
            Require(Scene.Dispatched(s.Tick()) == 0, "A disposed module published a behaviour fact.");
        });
        Case("gate.thread-off-pump-is-refused", () =>
        {
            using var s = new Scene(); s.Subscribe(EnemyModule.StateChangedBinding); s.Start();
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
