using System.Text.Json;
using ForgeDevelopment.Native;

// The running generation trace and the node back-references it carries are captured at enqueue. This
// proves the frozen file, not the native hook: the payloads are the fields RuntimeDiagnostics and
// WorldInspection write, and every native identity here is synthetic.
internal static class TraceBackReferenceTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var report = new DiagnosticsReport("trace-run");
        report.SetMetadata("sessionId", "session-trace");
        report.SetMetadata("generationTraceOverheadMs", "12.5");
        report.SetMetadata("stage:build_geomorph", "calls=1;totalMs=12;maxMs=12");
        var scan = new ProjectObjectReferenceScan(new[]
        {
            new ProjectObjectDeclaration("expedition", "zone-a", new ProjectZoneLocator(10, 0, 0, 1))
        }, 7, 12, ProjectSourceVerification.Matched);
        scan.Start(new[] { new ProjectLayoutKey(10, 0, 0) }, 12);
        report.AttachObjectReferences(scan);

        var trace = new Dictionary<string, string>
        {
            ["jobType"] = "LG_BuildGeomorphJob", ["zone"] = "0/MainLayer/1", ["geomorph"] = "geo-a",
            ["randomBefore"] = "100:200", ["randomAfter"] = "101:200", ["returnedDone"] = "False", ["invocation"] = "1"
        };
        report.Event("generation_job", "build_geomorph", "LG_BuildGeomorphJob:1A", new(trace), 12);
        report.Check("area_course_node", "same-display-path#node-77", "observed",
            "valid=True; nodeAreaMatches=True; nodeZoneMatches=True; zoneRegistersNode=True.",
            new DiagnosticNativeObject("zone", 11));
        report.Issue("MissingReferenceException", "C_CullingCluster.Update", "first message", "first stack",
            new DiagnosticNativeObject("geomorph", 22));

        var frozen = report.Freeze("generating");

        // Everything the running game does next: the same job continues, the node relation is
        // re-read, the repeated exception happens again and the scan finishes in a later world.
        report.Event("generation_job", "build_geomorph", "LG_BuildGeomorphJob:1A",
            new Dictionary<string, string>(trace) { ["randomAfter"] = "999:200", ["returnedDone"] = "True", ["invocation"] = "2" }, 30);
        report.Check("area_course_node", "same-display-path#node-77", "mismatch", "late rewrite",
            new DiagnosticNativeObject("zone", 11));
        report.Issue("MissingReferenceException", "C_CullingCluster.Update", "second message", "second stack",
            new DiagnosticNativeObject("geomorph", 22));
        scan.ObserveZone(new ProjectZoneCandidate(101, 10, 0, 0, 1));
        scan.ObserveAreas(new ProjectGeomorphAreas(55, 101, Array.Empty<ProjectAreaCandidate>()));
        scan.Complete(13);

        var frozenPath = Path.Combine(root, "trace-frozen.json");
        frozen.Export(frozenPath);
        var frozenBytes = File.ReadAllBytes(frozenPath);
        var laterPath = report.Export(Path.Combine(root, "trace-later.json"), "complete");
        report.AttachObjectReferences(new ProjectObjectReferenceScan(Array.Empty<ProjectObjectDeclaration>(), 8, 1, ProjectSourceVerification.NotProvided));

        using (var json = JsonDocument.Parse(frozenBytes))
        {
            var document = json.RootElement;
            check(document.GetProperty("outcome").GetString() == "generating", "the queued file keeps its own outcome");
            check(document.GetProperty("metadata").GetProperty("sessionId").GetString() == "session-trace" &&
                document.GetProperty("metadata").GetProperty("stage:build_geomorph").GetString() == "calls=1;totalMs=12;maxMs=12",
                "trace cost metadata is frozen with the queued file");
            var events = document.GetProperty("events");
            check(events.GetArrayLength() == 1, "a later trace event cannot enter the queued file");
            var fields = events[0].GetProperty("fields");
            check(fields.GetProperty("geomorph").GetString() == "geo-a" && fields.GetProperty("randomAfter").GetString() == "101:200" &&
                fields.GetProperty("returnedDone").GetString() == "False" && fields.GetProperty("invocation").GetString() == "1",
                "the queued trace event keeps the exact job context it was captured with");
            var aggregate = document.GetProperty("eventAggregates")[0];
            check(aggregate.GetProperty("count").GetInt64() == 1 && aggregate.GetProperty("totalElapsedMs").GetDouble() == 12 &&
                aggregate.GetProperty("firstFields").GetProperty("randomBefore").GetString() == "100:200" &&
                aggregate.GetProperty("lastRandomAfter").GetString() == "101:200",
                "the queued trace aggregate keeps its own count, cost and random state");
            var checkRow = document.GetProperty("checks")[0];
            check(checkRow.GetProperty("status").GetString() == "observed" && checkRow.GetProperty("detail").GetString()!.StartsWith("valid=True", StringComparison.Ordinal),
                "a later node re-read cannot rewrite the queued check");
            check(checkRow.GetProperty("nativeObject").GetProperty("kind").GetString() == "zone" &&
                checkRow.GetProperty("nativeObject").GetProperty("instanceId").GetInt32() == 11,
                "the queued check keeps the native identity it was bound to");
            var issue = document.GetProperty("issues")[0];
            check(issue.GetProperty("count").GetInt64() == 1 && issue.GetProperty("message").GetString() == "first message" &&
                issue.GetProperty("representativeStack").GetString() == "first stack",
                "a repeated exception cannot add occurrences to the queued file");
            check(issue.GetProperty("nativeObject").GetProperty("instanceId").GetInt32() == 22,
                "the queued issue keeps its native object identity");
            var references = document.GetProperty("objectReferences");
            check(references.GetProperty("worldEpoch").GetInt64() == 7 && references.GetProperty("scanStatus").GetString() == "pending" &&
                references.GetProperty("simulationTick").GetInt64() == 12 && references.GetProperty("groups").GetArrayLength() == 1,
                "the queued receipt keeps its own world, tick, status and declarations");
            check(references.GetProperty("groups")[0].GetProperty("status").GetString() == "unverified" &&
                references.GetProperty("groups")[0].GetProperty("candidates").GetArrayLength() == 0,
                "a node observed after enqueue is absent from the queued receipt");
            check(references.GetProperty("overflow").EnumerateObject().All(property => property.Value.GetInt64() == 0),
                "a frozen receipt does not report drops it never performed");
        }
        using (var json = JsonDocument.Parse(File.ReadAllBytes(laterPath)))
        {
            var document = json.RootElement;
            var aggregate = document.GetProperty("eventAggregates")[0];
            check(document.GetProperty("events").GetArrayLength() == 2 && aggregate.GetProperty("count").GetInt64() == 2 &&
                aggregate.GetProperty("lastRandomAfter").GetString() == "999:200",
                "the next snapshot records the continued trace it actually observed");
            check(document.GetProperty("issues")[0].GetProperty("count").GetInt64() == 2 &&
                document.GetProperty("issues")[0].GetProperty("representativeStack").GetString() == "first stack",
                "the repeated exception aggregates in the next snapshot without replacing the first stack");
            check(document.GetProperty("checks").EnumerateArray()
                .Select(row => row.GetProperty("status").GetString()).SequenceEqual(new[] { "observed", "mismatch" }),
                "the next snapshot records the later node state");
            var references = document.GetProperty("objectReferences");
            check(references.GetProperty("scanStatus").GetString() == "complete" &&
                references.GetProperty("groups")[0].GetProperty("status").GetString() == "matched" &&
                references.GetProperty("groups")[0].GetProperty("candidates")[0].GetProperty("instanceId").GetInt32() == 101,
                "the next snapshot records the completed scan and its observed native candidate");
        }
        check(File.ReadAllBytes(frozenPath).SequenceEqual(frozenBytes), "a later export never rewrites the queued file");
    }
}
