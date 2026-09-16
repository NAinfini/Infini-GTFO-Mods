using System.Text.Json;
using ForgeDevelopment.Native;

internal static class EngineTests
{
    internal static void Run(Suite suite)
    {
        // A full run: before-snapshot free command, two steps, cleanup, one record per step.
        var (engine, environment, outcomes) = Start("""
        { "id": "run", "target": "localPlayerStatus",
          "steps": [ { "set": "m_shieldValueRef", "path": "m_x", "value": 50.0 }, { "read": ["m_lastShieldVal"], "screenshot": true } ],
          "cleanup": [ { "call": "UpdateShield", "args": [0.0] } ] }
        """);
        Drive(engine, environment, 64);
        suite.Check("engine.finished", engine.Finished);
        suite.Check("engine.notFailed", !engine.Failed, engine.Blocked);
        suite.Equal("engine.recordCount", 3, outcomes.Count);
        suite.Check("engine.setReported", outcomes.Any(outcome => outcome.Kind == "set" && outcome.Message.Contains("m_x = 50")), Describe(outcomes));
        suite.Check("engine.readReported", outcomes.Any(outcome => outcome.Kind == "read" && outcome.Message.Contains("m_lastShieldVal=42")), Describe(outcomes));
        suite.Check("engine.screenshotInRecord", outcomes.Any(outcome => outcome.Kind == "read" && outcome.Message.Contains("screenshot=shots/1.png")), Describe(outcomes));
        suite.Check("engine.cleanupRan", outcomes.Any(outcome => outcome.Message.Contains("UpdateShield")), Describe(outcomes));
        suite.Equal("engine.screenshotCalls", 1, environment.Screenshots);
        // A failing step stops the rest but still runs cleanup.
        (engine, environment, outcomes) = Start("""
        { "id": "fail", "target": "localPlayerStatus",
          "steps": [ { "set": "m_x", "path": "m_missing", "value": 1 }, { "read": ["m_lastShieldVal"] } ],
          "cleanup": [ { "call": "UpdateShield", "args": [0.0] } ] }
        """);
        Drive(engine, environment, 64);
        suite.Check("engine.failed", engine.Failed);
        suite.Equal("engine.failedStopsMain", 2, outcomes.Count);
        suite.Check("engine.cleanupAfterFailure", outcomes.Any(outcome => outcome.Message.Contains("UpdateShield")), Describe(outcomes));
        suite.Check("engine.blockedReason", engine.Blocked.Contains("m_missing"), engine.Blocked);

        // A failure inside cleanup does not start a second cleanup.
        (engine, environment, outcomes) = Start("""
        { "id": "fail-cleanup", "target": "localPlayerStatus", "steps": [ { "read": ["m_lastShieldVal"] } ],
          "cleanup": [ { "set": "m_x", "path": "m_missing", "value": 1 } ] }
        """);
        Drive(engine, environment, 64);
        suite.Check("engine.cleanupFailureTerminates", engine.Finished && engine.Failed);
        suite.Equal("engine.cleanupFailureRecords", 2, outcomes.Count);

        // Waiting on frames takes that many frames and is reported once.
        (engine, environment, outcomes) = Start("""
        { "id": "wait-frames", "target": "localPlayerStatus", "steps": [ { "wait": { "frames": 3 } }, { "read": ["m_lastShieldVal"] } ] }
        """);
        Drive(engine, environment, 64);
        suite.Check("engine.frameWait", outcomes.Count == 2 && outcomes[0].Message.Contains("waited 3 frames"), string.Join(" / ", outcomes.Select(outcome => outcome.Message)));
        suite.Check("engine.frameWaitUsedFrames", environment.Frame >= 2, "frames used: " + environment.Frame);

        // Waiting on seconds uses the clock, not frames.
        (engine, environment, outcomes) = Start("""
        { "id": "wait-seconds", "target": "localPlayerStatus", "steps": [ { "wait": { "seconds": 0.05 } } ] }
        """);
        Drive(engine, environment, 200);
        suite.Check("engine.secondWait", outcomes.Count == 1 && outcomes[0].Message.Contains("waited 0.05s"), string.Join(" / ", outcomes.Select(outcome => outcome.Message)));

        // A trace wait times out instead of hanging.
        (engine, environment, outcomes) = Start("""
        { "id": "wait-trace", "target": "localPlayerStatus", "steps": [ { "wait": { "trace": "LevelGeneration.LG_SecurityDoor.AttemptOpenCloseInteraction", "timeoutFrames": 5 } } ] }
        """);
        Drive(engine, environment, 500);
        suite.Check("engine.traceTimeout", engine.Failed && engine.Blocked.Contains("was not observed"), engine.Blocked);

        // A trace wait succeeds once the tracer reports the hit.
        (engine, environment, outcomes) = Start("""
        { "id": "wait-trace-hit", "target": "localPlayerStatus", "steps": [ { "wait": { "trace": "LevelGeneration.LG_SecurityDoor.AttemptOpenCloseInteraction", "timeoutFrames": 50 } } ] }
        """, traceAfterFrame: 3);
        Drive(engine, environment, 200);
        suite.Check("engine.traceHit", !engine.Failed && outcomes.Any(outcome => outcome.Message.Contains("observed")), engine.Blocked + " | " + Describe(outcomes));

        // A path wait completes when the value changes.
        (engine, environment, outcomes) = Start("""
        { "id": "wait-path", "target": "localPlayerStatus", "steps": [ { "wait": { "path": "m_lastShieldVal", "changed": true, "timeoutFrames": 50 } } ] }
        """, changeAfterFrame: 4);
        Drive(engine, environment, 200);
        suite.Check("engine.pathWait", !engine.Failed && outcomes.Any(outcome => outcome.Message.Contains("changed")), engine.Blocked + " | " + Describe(outcomes));

        // A repeat runs its body the requested number of times.
        (engine, environment, outcomes) = Start("""
        { "id": "repeat", "target": "localPlayerStatus", "steps": [ { "repeat": { "times": 3, "steps": [ { "read": ["m_lastShieldVal"] } ] } } ] }
        """);
        Drive(engine, environment, 400);
        suite.Equal("engine.repeatRecords", 3, outcomes.Count);
        suite.Equal("engine.repeatReads", 3, environment.Reads);

        // Nested repeats multiply: 2 outer iterations of (3 reads + 1 wait).
        (engine, environment, outcomes) = Start("""
        { "id": "nested", "target": "localPlayerStatus", "steps": [ { "repeat": { "times": 2, "steps": [
          { "repeat": { "times": 3, "steps": [ { "read": ["m_lastShieldVal"] } ] } },
          { "wait": 1 } ] } } ] }
        """);
        Drive(engine, environment, 400);
        suite.Equal("engine.nestedRecords", 8, outcomes.Count);

        // A loop that spends real time is paced by the frame budget, not finished inside one frame.
        (engine, environment, outcomes) = Start("""
        { "id": "spread", "target": "localPlayerStatus", "steps": [ { "repeat": { "times": 40, "steps": [ { "read": ["m_lastShieldVal"] } ] } } ] }
        """);
        environment.ReadDelayMilliseconds = 1.0;
        var framesUsed = 0;
        while (!engine.Finished && framesUsed < 200)
        {
            environment.Frame++;
            framesUsed++;
            Step(engine, environment);
            environment.Seconds += 0.02;
        }
        suite.Check("engine.loopSpreadOverFrames", framesUsed >= 10, "frames used: " + framesUsed);
        suite.Check("engine.loopCompleted", engine.Finished && !engine.Failed, engine.Blocked);
        suite.Equal("engine.loopRecords", 40, outcomes.Count);

        // World events and host gating surfaces.
        (engine, environment, outcomes) = Start("""
        { "id": "event", "target": "localPlayerStatus", "steps": [ { "worldEvent": { "Type": 2 } } ] }
        """);
        Drive(engine, environment, 64);
        suite.Check("engine.worldEvent", outcomes.Count == 1 && outcomes[0].Message.Contains("Type=2"), string.Join(" / ", outcomes.Select(outcome => outcome.Message)));

        // A target that cannot resolve is a step failure, not a crash.
        (engine, environment, outcomes) = Start("""
        { "id": "no-target", "target": "lookedAtEnemy", "steps": [ { "call": "SetColor" } ] }
        """);
        environment.TargetError = "the crosshair is not on an EnemyAgent";
        Drive(engine, environment, 64);
        suite.Check("engine.targetFailure", engine.Failed && engine.Blocked.Contains("crosshair"), engine.Blocked);

        // Cancel keeps cleanup.
        (engine, environment, outcomes) = Start("""
        { "id": "cancel", "target": "localPlayerStatus", "steps": [ { "read": ["m_lastShieldVal"] }, { "read": ["m_lastShieldVal"] } ],
          "cleanup": [ { "call": "UpdateShield", "args": [0.0] } ] }
        """);
        engine.Cancel("test");
        Drive(engine, environment, 64);
        suite.Check("engine.cancelKeepsCleanup", outcomes.Any(outcome => outcome.Message.Contains("UpdateShield")));
        suite.Check("engine.cancelledFinishes", engine.Finished);
    }

    /// <summary>One frame of the driver loop: dispatch while the engine yields and the frame budget lasts.</summary>
    private static void Step(ExperimentEngine engine, FakeEnvironment environment)
    {
        var dispatched = 0;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!engine.Finished && dispatched++ < 64)
        {
            var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var action = engine.Next(elapsed, 2.0);
            if (action == null) break;
            engine.Advance(action);
        }
    }

    private static void Drive(ExperimentEngine engine, FakeEnvironment environment, int maximumFrames)
    {
        var frames = 0;
        while (!engine.Finished && frames++ < maximumFrames)
        {
            environment.Frame++;
            Step(engine, environment);
            environment.Seconds += 1.0 / 60.0;
        }
    }

    private static string Describe(List<ExperimentOutcome> outcomes) => string.Join(" / ", outcomes.Select(outcome => outcome.Kind + (outcome.Ok ? "+" : "-") + " " + outcome.Message));

    private static (ExperimentEngine, FakeEnvironment, List<ExperimentOutcome>) Start(string json, int traceAfterFrame = -1, int changeAfterFrame = -1)
    {
        var command = Json.Parse(json, out var problems);
        if (problems.Count != 0) throw new InvalidOperationException(string.Join("; ", problems));
        if (!ExperimentTargetSpec.TryParse(command.Target, out var spec)) throw new InvalidOperationException("target");
        var environment = new FakeEnvironment { TraceAfterFrame = traceAfterFrame, ChangeAfterFrame = changeAfterFrame };
        var outcomes = new List<ExperimentOutcome>();
        return (new ExperimentEngine(command, spec, environment, outcomes.Add), environment, outcomes);
    }
}

