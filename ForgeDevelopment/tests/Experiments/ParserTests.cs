using System.Text.Json;
using ForgeDevelopment.Native;

internal static class ParserTests
{
    private const string Minimal = """
    { "id": "hud-shield-write", "points": ["hud-A-1"], "host": "host", "target": "localPlayerStatus",
      "steps": [ { "call": "UpdateShield", "args": [50.0] } ] }
    """;

    internal static void Run(Suite suite)
    {
        suite.Check("parser.minimal", Json.Valid(Minimal), Json.ProblemOf(Minimal));
        var command = Json.Parse(Minimal, out var problems);
        suite.Equal("parser.id", "hud-shield-write", command.Id);
        suite.Equal("parser.host", ExperimentHost.Host, command.Host);
        suite.Equal("parser.points", "hud-A-1", command.Points[0]);
        suite.Equal("parser.stepCount", 1, command.Steps.Count);
        suite.Equal("parser.call", "UpdateShield", command.Steps[0].Name);
        suite.Equal("parser.args", 1, command.Steps[0].Arguments.Count);
        suite.Equal("parser.problems", 0, problems.Count);

        suite.Check("parser.array", ExperimentParser.Parse("[" + Minimal + "," + Minimal + "]", "a.json", out _).Count == 2);
        suite.Check("parser.badsyntax", ExperimentParser.Parse("{", "a.json", out var error).Count == 0 && error.Contains("invalid JSON"));
        suite.Check("parser.nonObjectRoot", ExperimentParser.Parse("3", "a.json", out error).Count == 0 && error.Contains("root"));

        suite.Check("parser.unknownKey", !Json.Valid(Minimal.Replace("\"points\"", "\"pointz\"")), "an unknown command key must be rejected");
        suite.Check("parser.missingTarget", !Json.Valid(Minimal.Replace("\"target\": \"localPlayerStatus\",", "")), "target is required");
        suite.Check("parser.badHost", !Json.Valid(Minimal.Replace("\"host\": \"host\"", "\"host\": \"server\"")), "host must be any/host/client");
        suite.Check("parser.emptySteps", !Json.Valid(Minimal.Replace("[ { \"call\": \"UpdateShield\", \"args\": [50.0] } ]", "[]")), "steps must not be empty");
        suite.Check("parser.badId", !Json.Valid(Minimal.Replace("hud-shield-write", "hud shield")), "ids cannot contain spaces");
        suite.Check("parser.stepTarget", Json.Valid("""
        { "id": "a", "target": "localPlayerStatus", "steps": [ { "read": ["m_x"], "target": "guiManager" } ] }
        """), Json.ProblemOf("""
        { "id": "a", "target": "localPlayerStatus", "steps": [ { "read": ["m_x"], "target": "guiManager" } ] }
        """));
        suite.Check("parser.stepTargetBad", !Json.Valid("""
        { "id": "a", "target": "localPlayerStatus", "steps": [ { "read": ["m_x"], "target": "guimanager" } ] }
        """));

        // Step type errors.
        suite.Check("step.unknown", Json.ProblemOf(Minimal.Replace("\"call\"", "\"invoke\"")).Contains("no step type"));
        suite.Check("step.twoTypes", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "call": "X", "wait": 1 } ] }
        """), "two step types in one object must be rejected");
        suite.Check("step.stringOnlyScreenshot", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ "pause" ] }
        """));
        suite.Check("step.stringScreenshot", Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ "screenshot:before" ] }
        """));
        suite.Check("step.namedArguments", Json.Valid("""
        { "id": "a", "target": "navMarkerLayer",
          "steps": [ { "call": "PlaceCustomMarker", "named": { "type": "Enemy", "trackingObj": null, "name": "x", "destroyDelay": 0.0 } } ] }
        """));
        suite.Check("step.namedWithPositional", Json.Valid("""
        { "id": "a", "target": "navMarkerLayer",
          "steps": [ { "call": "PlaceCustomMarker", "args": ["Enemy"], "named": { "destroyDelay": 0.0 } } ] }
        """), Json.ProblemOf("""
        { "id": "a", "target": "navMarkerLayer",
          "steps": [ { "call": "PlaceCustomMarker", "args": ["Enemy"], "named": { "destroyDelay": 0.0 } } ] }
        """));
        suite.Check("step.namedDuplicated", !Json.Valid("""
        { "id": "a", "target": "navMarkerLayer",
          "steps": [ { "call": "PlaceCustomMarker", "named": { "name": "x", "name": "y" } } ] }
        """));
        suite.Check("step.namedOnRead", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "read": ["m_x"], "named": { "a": 1 } } ] }
        """));

        // Paths.
        suite.Check("path.setMissingValue", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "set": "m_x" } ] }
        """));
        suite.Check("path.badDots", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "set": "m_x", "value": 1, "path": "a..b" } ] }
        """));
        suite.Check("path.readPaths", Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "read": ["m_shield1.rectTransform.sizeDelta", "m_sync.m_stateReplicator.State"], "screenshot": true } ] }
        """), Json.ProblemOf("""
        { "id": "a", "target": "localPlayer", "steps": [ { "read": ["m_shield1.rectTransform.sizeDelta", "m_sync.m_stateReplicator.State"], "screenshot": true } ] }
        """));
        suite.Check("path.readSingle", Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "read": "m_lastShieldVal" } ] }
        """), Json.ProblemOf("""
        { "id": "a", "target": "localPlayer", "steps": [ { "read": "m_lastShieldVal" } ] }
        """));
        suite.Check("path.readNeither", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "read": true } ] }
        """));
        suite.Check("path.readBadPath", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "read": ["m_x..y"] } ] }
        """));
        suite.Check("path.straySetKey", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "call": "X", "value": 3 } ] }
        """));

        // Waits.
        suite.Check("wait.frames", Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "wait": { "frames": 2 } } ] }
        """));
        suite.Check("wait.bareNumber", Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "wait": 2 } ] }
        """));
        suite.Check("wait.negative", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "wait": { "frames": -1 } } ] }
        """));
        suite.Check("wait.twoConditions", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "wait": { "frames": 1, "seconds": 1 } } ] }
        """));
        suite.Check("wait.traceNeedsTimeout", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "wait": { "trace": "Door.Open" } } ] }
        """));
        suite.Check("wait.traceOk", Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "wait": { "trace": "LevelGeneration.LG_SecurityDoor.AttemptOpenCloseInteraction", "timeoutFrames": 300 } } ] }
        """));
        suite.Check("wait.traceMalformed", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "wait": { "trace": "NoDot", "timeoutFrames": 10 } } ] }
        """));
        suite.Check("wait.pathTimeout", Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "wait": { "path": "m_health", "changed": true, "timeoutFrames": 120 } } ] }
        """));
        suite.Check("wait.unknownKey", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "wait": { "frame": 1 } } ] }
        """));

        // Repeats.
        suite.Check("repeat.ok", Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "repeat": { "times": 3, "steps": [ { "wait": 1 } ] } } ] }
        """));
        suite.Check("repeat.zero", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "repeat": { "times": 0, "steps": [ { "wait": 1 } ] } } ] }
        """));
        suite.Check("repeat.emptyBody", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "repeat": { "times": 2, "steps": [] } } ] }
        """));
        suite.Check("repeat.unknownKey", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "repeat": { "times": 2, "step": [], "steps": [ { "wait": 1 } ] } } ] }
        """));
        suite.Check("repeat.tooDeep", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "repeat": { "times": 2, "steps": [
          { "repeat": { "times": 2, "steps": [ { "repeat": { "times": 2, "steps": [ { "repeat": { "times": 2, "steps": [
          { "repeat": { "times": 2, "steps": [ { "wait": 1 } ] } } ] } } ] } } ] } } ] } }
        """), "five nested repeats must be rejected");
        suite.Check("repeat.expansion", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "repeat": { "times": 1024, "steps": [
          { "repeat": { "times": 1024, "steps": [ { "wait": 1 } ] } } ] } } ] }
        """), "a body that expands past the cap must be rejected");

        // World event and stage.
        suite.Check("event.ok", Json.Valid("""
        { "id": "a", "target": "static:LevelGeneration.WorldEventManager",
          "steps": [ { "worldEvent": { "Type": 2, "Layer": "MainLayer", "LocalIndex": 4, "WardenIntel": "hello" } } ] }
        """));
        suite.Check("event.notObject", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "worldEvent": 3 } ] }
        """));
        suite.Check("event.badType", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "worldEvent": { "Type": true } } ] }
        """));
        suite.Check("before.ok", Json.Valid(Minimal.Replace("\"steps\"", "\"before\": { \"snapshot\": [\"m_shieldValueRef\"], \"screenshot\": true }, \"steps\"")), "before with snapshot and screenshot");
        suite.Check("before.badPath", !Json.Valid(Minimal.Replace("\"steps\"", "\"before\": { \"snapshot\": [\"m_shield..x\"] }, \"steps\"")), "before snapshot paths are checked");
        suite.Check("before.unknownKey", !Json.Valid(Minimal.Replace("\"steps\"", "\"before\": { \"shots\": true }, \"steps\"")));

        // Bounds.
        suite.Check("bounds.args", !Json.Valid("""
        { "id": "a", "target": "localPlayer", "steps": [ { "call": "X", "args": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17] } ] }
        """));
        var many = string.Join(",", Enumerable.Range(0, 300).Select(index => "{ \"wait\": 1 }"));
        suite.Check("bounds.steps", !Json.Valid("{ \"id\": \"a\", \"target\": \"localPlayer\", \"steps\": [" + many + "] }"));
    }
}
