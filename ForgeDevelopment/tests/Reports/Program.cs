using System.Text.Json;
using ForgeDevelopment.Native;

var failures = 0;
var checks = 0;
var directory = Path.Combine(Path.GetTempPath(), "forge-runtime-report-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

void Check(bool condition, string message)
{
    checks++;
    if (condition) return;
    failures++;
    Console.Error.WriteLine("FAIL: " + message);
}

JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));

try
{
    var flooded = new DiagnosticsReport("flood");
    var context = new Dictionary<string, string> { ["jobType"] = "Job", ["zone"] = "50", ["randomAfter"] = "first" };
    for (var i = 0; i < 150000; i++) flooded.Event("generation_job", "update", "job-" + i, context, 2);
    context["randomAfter"] = "last";
    flooded.Event("generation_job", "update", "final-job", context, 5);
    flooded.Event("lifecycle", "level_cleanup", "floor");
    using (var json = Read(flooded.Export(Path.Combine(directory, "flood.json"), "complete")))
    {
        var root = json.RootElement;
        var aggregate = root.GetProperty("eventAggregates")[0];
        Check(root.GetProperty("events").GetArrayLength() == 3073, "detail flood reserves lifecycle capacity");
        Check(root.GetProperty("events")[3072].GetProperty("stage").GetString() == "level_cleanup", "late cleanup survives flood");
        Check(aggregate.GetProperty("count").GetInt64() == 150001, "aggregate retains all observations");
        Check(aggregate.GetProperty("totalElapsedMs").GetDouble() == 300005, "aggregate retains total duration");
        Check(aggregate.GetProperty("maximumElapsedMs").GetDouble() == 5, "aggregate retains maximum duration");
        Check(aggregate.GetProperty("firstFields").GetProperty("randomAfter").GetString() == "first", "aggregate owns copied initial fields");
        Check(aggregate.GetProperty("lastRandomAfter").GetString() == "last", "aggregate retains last random state");
        Check(root.GetProperty("overflow").GetProperty("droppedEvents").GetInt64() == 0, "aggregated details are not reported as lost events");
    }
    var cleanup = new List<string>();
    ShutdownSequence.Run(new (string, Action)[] {
        ("unsubscribe", () => throw new InvalidOperationException("native object destroyed")),
        ("export", () => cleanup.Add("export")),
        ("dispose", () => cleanup.Add("dispose"))
    }, (stage, error) => cleanup.Add(stage));
    Check(cleanup.SequenceEqual(new[] { "unsubscribe", "export", "dispose" }), "cleanup continues through unsubscribe failure");

    var report = new DiagnosticsReport("run-1");
    report.SetMetadata("pluginVersion", "1.0.0");
    report.SetMetadata("configHash", "abc");
    report.SetMetadata("resourceHash", "def");
    report.Event("generation", "place", "geo_test", new()
    {
        ["zone"] = "50",
        ["geomorph"] = "geo_test",
        ["seed"] = "42",
        ["randomBefore"] = "100",
        ["randomAfter"] = "200"
    }, 12.5);
    report.Check("critical_object", "terminal_1", "", "No result was collected.");
    report.Issue("MissingReferenceException", "C_CullingCluster.Update", "destroyed object", "");
    report.Issue("MissingReferenceException", "C_CullingCluster.Update", "second message", "representative stack");

    var basicPath = report.Export(Path.Combine(directory, "basic.json"), "generation_finished");
    using (var json = Read(basicPath))
    {
        var root = json.RootElement;
        Check(!root.TryGetProperty("schemaVersion", out _), "report root has no schema version");
        Check(root.GetProperty("format").GetString() == "gtfo-forge-diagnostics-report", "report format is the current one");
        Check(root.GetProperty("runId").GetString() == "run-1", "run ID is preserved");
        Check(root.GetProperty("playability").GetString() == "not_assessed", "report does not claim playability");
        Check(root.GetProperty("metadata").GetProperty("configHash").GetString() == "abc", "metadata is exported");
        Check(root.GetProperty("metadata").GetProperty("resourceHash").GetString() == "def", "resource hashes are exported");
        Check(root.GetProperty("events")[0].GetProperty("fields").GetProperty("geomorph").GetString() == "geo_test", "generation fields are exported");
        Check(root.GetProperty("checks")[0].GetProperty("status").GetString() == "not_checked", "blank checks remain explicitly unchecked");
        var issue = root.GetProperty("issues")[0];
        Check(issue.GetProperty("count").GetInt64() == 2, "issues aggregate by type and source");
        Check(issue.GetProperty("message").GetString() == "destroyed object", "first issue message is representative");
        Check(issue.GetProperty("representativeStack").GetString() == "representative stack", "first available stack is retained");
        Check(issue.GetProperty("firstSeenUtc").GetDateTimeOffset() <= issue.GetProperty("lastSeenUtc").GetDateTimeOffset(), "issue time range is ordered");
    }

    var bounded = new DiagnosticsReport("bounded");
    for (var index = 0; index < 4100; index++) bounded.Event("test", "event", index.ToString());
    var manyFields = Enumerable.Range(0, 33).ToDictionary(index => "key" + index, index => "value" + index);
    var fieldReport = new DiagnosticsReport("fields");
    fieldReport.Event("test", "fields", new string('x', 5000), manyFields);
    using (var json = Read(bounded.Export(Path.Combine(directory, "bounded.json"), "complete")))
    {
        Check(json.RootElement.GetProperty("events").GetArrayLength() == 4096, "events are bounded");
        Check(json.RootElement.GetProperty("overflow").GetProperty("droppedEvents").GetInt64() == 4, "dropped events are counted");
    }
    using (var json = Read(fieldReport.Export(Path.Combine(directory, "fields.json"), "complete")))
    {
        Check(json.RootElement.GetProperty("events")[0].GetProperty("subject").GetString()!.Length == 4096, "long strings are bounded");
        Check(json.RootElement.GetProperty("overflow").GetProperty("droppedEventFields").GetInt64() == 1, "dropped fields are counted");
        Check(json.RootElement.GetProperty("overflow").GetProperty("truncatedStrings").GetInt64() == 1, "truncated strings are counted");
    }

    var allBounds = new DiagnosticsReport("all-bounds");
    for (var index = 0; index < 130; index++) allBounds.SetMetadata("key" + index, "value");
    for (var index = 0; index < 2050; index++) allBounds.Check("object", index.ToString(), "checked", "detail");
    for (var index = 0; index < 1026; index++) allBounds.Issue("type", "source" + index, "message");
    using (var json = Read(allBounds.Export(Path.Combine(directory, "all-bounds.json"), "complete")))
    {
        var root = json.RootElement;
        Check(root.GetProperty("metadata").EnumerateObject().Count() == 128, "metadata is bounded");
        Check(root.GetProperty("checks").GetArrayLength() == 2048, "checks are bounded");
        Check(root.GetProperty("issues").GetArrayLength() == 1024, "unique issues are bounded");
        Check(root.GetProperty("overflow").GetProperty("droppedMetadata").GetInt64() == 2, "dropped metadata is counted");
        Check(root.GetProperty("overflow").GetProperty("droppedChecks").GetInt64() == 2, "dropped checks are counted");
        Check(root.GetProperty("overflow").GetProperty("droppedIssues").GetInt64() == 2, "dropped unique issues are counted");
    }

    // A flood of distinct trace contexts cannot grow the aggregate table past its cap, an aggregate
    // that already exists keeps counting, and every context refused after the cap is counted.
    var aggregateCap = new DiagnosticsReport("aggregate-cap");
    for (var index = 0; index < 1030; index++)
        aggregateCap.Event("generation_job", "update", "job-" + index, new() { ["jobType"] = "Job", ["geomorph"] = "geo-" + index }, 1);
    aggregateCap.Event("generation_job", "update", "job-0-again", new() { ["jobType"] = "Job", ["geomorph"] = "geo-0" }, 2);
    using (var json = Read(aggregateCap.Export(Path.Combine(directory, "aggregate-cap.json"), "complete")))
    {
        var root = json.RootElement;
        var aggregates = root.GetProperty("eventAggregates");
        Check(aggregates.GetArrayLength() == 1024, "detailed trace aggregates are bounded");
        var first = aggregates.EnumerateArray().Single(entry => entry.GetProperty("context").GetString() == "Job||geo-0");
        Check(first.GetProperty("count").GetInt64() == 2 && first.GetProperty("totalElapsedMs").GetDouble() == 3,
            "an existing aggregate keeps counting after the cap");
        Check(root.GetProperty("overflow").GetProperty("droppedAggregateEvents").GetInt64() == 6,
            "trace contexts beyond the aggregate cap are counted as dropped observations");
    }

    var concurrent = new DiagnosticsReport("concurrent");
    Parallel.For(0, 1000, _ => concurrent.Issue("repeat", "same-source", "message", "stack"));
    using (var json = Read(concurrent.Export(Path.Combine(directory, "concurrent.json"), "complete")))
        Check(json.RootElement.GetProperty("issues")[0].GetProperty("count").GetInt64() == 1000, "issue aggregation is thread safe");

    var blockedPath = Path.Combine(directory, "blocked.json");
    Directory.CreateDirectory(blockedPath);
    var failed = false;
    try
    {
        report.Export(blockedPath, "must-not-succeed");
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        failed = true;
    }
    Check(failed, "failed atomic replacement is reported to the caller");
    Check(Directory.Exists(blockedPath), "failed export does not replace the existing target");
    Check(!Directory.EnumerateFiles(directory, ".blocked.json.*.tmp").Any(), "failed export cleans its temporary file");

    var asynchronousPath = Path.Combine(directory, "asynchronous.json");
    var asynchronousReport = new DiagnosticsReport("asynchronous");
    using (var writer = new AsyncReportWriter(_ => { }))
    {
        Parallel.For(0, 100, index => writer.Enqueue(asynchronousReport, asynchronousPath, "intermediate-" + index));
        Check(writer.Enqueue(asynchronousReport, asynchronousPath, "final-outcome"), "latest coalesced request is accepted");
    }
    using (var json = Read(asynchronousPath))
        Check(json.RootElement.GetProperty("outcome").GetString() == "final-outcome", "coalescing preserves the latest outcome");

    var writerErrors = new List<Exception>();
    var errorTarget = Path.Combine(directory, "writer-error.json");
    Directory.CreateDirectory(errorTarget);
    using (var writer = new AsyncReportWriter(error =>
           {
               lock (writerErrors) writerErrors.Add(error);
           }))
    {
        Check(writer.Enqueue(report, errorTarget, "failure"), "failing background request is accepted for processing");
    }
    lock (writerErrors)
        Check(writerErrors.Count == 1, "background write failure is reported exactly once");

    var stoppedWriter = new AsyncReportWriter(_ => { });
    stoppedWriter.Dispose();
    Check(!stoppedWriter.Enqueue(report, Path.Combine(directory, "stopped.json"), "rejected"), "stopped writer rejects new reports");
    stoppedWriter.Dispose();

    var shutdownRaceErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
    for (var iteration = 0; iteration < 100; iteration++)
    {
        var raceWriter = new AsyncReportWriter(_ => { });
        using var start = new ManualResetEventSlim(false);
        var enqueueTask = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (var index = 0; index < 50; index++)
                    raceWriter.Enqueue(report, Path.Combine(directory, "race-" + iteration + ".json"), index.ToString());
            }
            catch (Exception error)
            {
                shutdownRaceErrors.Enqueue(error);
            }
        });
        var disposeTask = Task.Run(() =>
        {
            start.Wait();
            try
            {
                raceWriter.Dispose();
            }
            catch (Exception error)
            {
                shutdownRaceErrors.Enqueue(error);
            }
        });
        start.Set();
        Task.WaitAll(enqueueTask, disposeTask);
    }
    Check(shutdownRaceErrors.IsEmpty, "concurrent enqueue and dispose do not throw");

    // Byte budget: the count limits do not bound the file, because one event can carry 32 fields of
    // 4096 characters. 4096 characters of a three-byte character and of a JSON-escaped control
    // character are 12 KiB and 24 KiB respectively, so 128 such events are far over the budget.
    var budget = ProjectObjectReferences.MaximumReportBytes;
    var dense = new DiagnosticsReport("byte-budget");
    dense.SetMetadata("projectId", "byte-budget");
    dense.Check("object", "survivor", "observed", "Checks outrank bulk observations.");
    dense.Issue("MissingReferenceException", "C_CullingCluster.Update", "Issues outrank bulk observations.");
    var wide = new string('地', 4096);
    var escaped = new string('\u0001', 4096);
    var denseFields = Enumerable.Range(0, 32).ToDictionary(index => "key" + index, index => index % 2 == 0 ? wide : escaped);
    for (var index = 0; index < 128; index++) dense.Event("test", "event", "subject-" + index, denseFields);
    var densePath = dense.Export(Path.Combine(directory, "byte-budget.json"), "complete");
    using (var json = Read(densePath))
    {
        var root = json.RootElement;
        var kept = root.GetProperty("events").GetArrayLength();
        var overflow = root.GetProperty("overflow");
        Check(new FileInfo(densePath).Length <= budget, "an oversized report is written inside the byte budget");
        Check(new FileInfo(densePath).Length > budget - 2_000_000, "the byte budget is filled instead of over-dropped");
        Check(kept > 0 && overflow.GetProperty("droppedEvents").GetInt64() == 128 - kept,
            "every event outside the byte budget is counted as dropped");
        Check(root.GetProperty("events")[kept - 1].GetProperty("subject").GetString() == "subject-" + (kept - 1),
            "the byte budget keeps a deterministic prefix of the observations");
        Check(root.GetProperty("events")[kept - 1].GetProperty("fields").GetProperty("key0").GetString()!.Length == 4096,
            "a kept event is whole, never cut inside a character");
        Check(overflow.GetProperty("truncatedStrings").GetInt64() == 0, "the byte budget never truncates a string");
        Check(root.GetProperty("checks").GetArrayLength() == 1 && root.GetProperty("issues").GetArrayLength() == 1,
            "checks and issues outrank dropped observations");
        Check(root.GetProperty("metadata").GetProperty("projectId").GetString() == "byte-budget",
            "metadata outranks dropped observations");
        Check(root.GetProperty("objectReferences").GetProperty("scanStatus").GetString() == "rejected",
            "dropping for the byte budget never fabricates a scan receipt");
        Check(root.EnumerateObject().Count() == 13, "the reduced report keeps the current root field set");
    }
    using (var first = Read(densePath))
    using (var again = Read(dense.Export(Path.Combine(directory, "byte-budget-again.json"), "complete")))
    {
        Check(again.RootElement.GetProperty("events").GetArrayLength() == first.RootElement.GetProperty("events").GetArrayLength()
            && again.RootElement.GetProperty("overflow").GetProperty("droppedEvents").GetInt64()
                == first.RootElement.GetProperty("overflow").GetProperty("droppedEvents").GetInt64(),
            "the byte budget drop rule is deterministic");
    }

    var fitting = new DiagnosticsReport("byte-fit");
    var fittingFields = Enumerable.Range(0, 16).ToDictionary(index => "key" + index, index => new string('x', 4096));
    for (var index = 0; index < 128; index++) fitting.Event("test", "event", "subject-" + index, fittingFields);
    var fittingPath = fitting.Export(Path.Combine(directory, "byte-fit.json"), "complete");
    using (var json = Read(fittingPath))
    {
        Check(new FileInfo(fittingPath).Length <= budget, "a report inside the byte budget stays inside it");
        Check(json.RootElement.GetProperty("events").GetArrayLength() == 128
            && json.RootElement.GetProperty("overflow").GetProperty("droppedEvents").GetInt64() == 0,
            "a report inside the byte budget drops nothing");
    }
    Check(!Directory.EnumerateFiles(directory, ".*.tmp").Any(), "a published report leaves no temporary file");

    // Fixed reduction order: detailed events first, then aggregates, then ordinary events. Detailed
    // events and aggregates are 20 three-observation aggregates of 32 three-byte fields each, so
    // only the ordinary events can survive intact.
    var ordered = new DiagnosticsReport("byte-order");
    var embedded = new string('地', 4096);
    var embeddedFields = Enumerable.Range(0, 32).ToDictionary(index => "key" + index, index => embedded);
    for (var group = 0; group < 20; group++)
    {
        var contextFields = new Dictionary<string, string>(embeddedFields) { ["jobType"] = "job-" + group };
        for (var repeat = 0; repeat < 3; repeat++) ordered.Event("generation_job", "update", "detail-" + group, contextFields, 2);
    }
    for (var index = 0; index < 20; index++) ordered.Event("lifecycle", "update", "ordinary-" + index, embeddedFields);
    var orderedPath = ordered.Export(Path.Combine(directory, "byte-order.json"), "complete");
    using (var json = Read(orderedPath))
    {
        var root = json.RootElement;
        var aggregates = root.GetProperty("eventAggregates");
        var overflow = root.GetProperty("overflow");
        Check(new FileInfo(orderedPath).Length <= budget && root.GetProperty("events").GetArrayLength() == 20,
            "the reduction drops detailed events and aggregates before ordinary events");
        Check(root.GetProperty("events").EnumerateArray().All(entry => entry.GetProperty("category").GetString() == "lifecycle"),
            "the surviving observations are exactly the ordinary events");
        Check(overflow.GetProperty("droppedEvents").GetInt64() == 60, "every dropped detailed event is counted once");
        Check(aggregates.GetArrayLength() < 20
            && overflow.GetProperty("droppedAggregateEvents").GetInt64() == 3 * (20 - aggregates.GetArrayLength()),
            "a dropped aggregate is counted with all of its observations");
    }

    // A dropped issue is counted with its observations too, and the critical identities, the receipt
    // and the whole overflow survive the reduction that removes checks and ordinary metadata.
    var findings = new DiagnosticsReport("byte-findings");
    findings.SetMetadata("projectId", "findings-project");
    findings.SetMetadata("projectManifestHash", "manifest-hash");
    findings.SetMetadata("experiment:authoringSha256", new string('a', 64));
    findings.SetMetadata("experiment:packageVersion", "1.0.0");
    for (var index = 0; index < 124; index++) findings.SetMetadata("key" + index, embedded);
    for (var index = 0; index < 3; index++) findings.Check("object", "check-" + index, "observed", embedded);
    var escapedIssue = new string('\u0001', 4096);
    var escapedStack = new string('\u0001', 16384);
    for (var index = 0; index < 140; index++)
    {
        findings.Issue("MissingReferenceException", "C_CullingCluster.Update/" + index, escapedIssue, escapedStack);
        findings.Issue("MissingReferenceException", "C_CullingCluster.Update/" + index, escapedIssue, escapedStack);
    }
    var findingsPath = findings.Export(Path.Combine(directory, "byte-findings.json"), "complete");
    using (var json = Read(findingsPath))
    {
        var root = json.RootElement;
        var metadata = root.GetProperty("metadata");
        var issues = root.GetProperty("issues");
        var overflow = root.GetProperty("overflow");
        Check(new FileInfo(findingsPath).Length <= budget, "a findings-heavy report is written inside the byte budget");
        Check(metadata.EnumerateObject().Select(property => property.Name)
            .SequenceEqual(new[] { "experiment:authoringSha256", "experiment:packageVersion", "projectId", "projectManifestHash" })
            && metadata.GetProperty("projectId").GetString() == "findings-project"
            && metadata.GetProperty("experiment:packageVersion").GetString() == "1.0.0",
            "the critical identities survive and ordinary metadata is dropped");
        Check(overflow.GetProperty("droppedMetadata").GetInt64() == 124,
            "every dropped metadata entry is counted");
        Check(root.GetProperty("checks").GetArrayLength() == 0 && overflow.GetProperty("droppedChecks").GetInt64() == 3,
            "checks are dropped before issues");
        Check(issues.GetArrayLength() > 0
            && overflow.GetProperty("droppedIssues").GetInt64() == 2 * (140 - issues.GetArrayLength()),
            "a dropped issue is counted with all of its occurrences");
        Check(issues[issues.GetArrayLength() - 1].GetProperty("representativeStack").GetString()!.Length == 16384
            && overflow.GetProperty("truncatedStrings").GetInt64() == 0,
            "a surviving issue is whole and nothing was truncated");
        Check(overflow.EnumerateObject().Count() == 8 && root.GetProperty("objectReferences").GetProperty("groups").GetArrayLength() == 0,
            "the whole overflow and the receipt survive the reduction");
    }

    // A receipt that alone exceeds the budget cannot be reduced at all: the export must refuse it
    // with InvalidDataException, keep the previous target bytes and leave no temporary file. The
    // declarations are built directly here, so the receipt is this test's own construction.
    var excessive = new DiagnosticsReport("byte-core");
    var longExpedition = new string('地', 4096);
    var declarations = new List<ProjectObjectDeclaration>();
    for (var index = 0; index < 1600; index++)
        declarations.Add(new ProjectObjectDeclaration(longExpedition, "zone-" + index, new ProjectZoneLocator(1000001u + (uint)index, 0, 0, 0)));
    excessive.AttachObjectReferences(new ProjectObjectReferenceScan(declarations, 1, 0, ProjectSourceVerification.Matched));
    var refusedPath = Path.Combine(directory, "byte-core.json");
    const string previousBytes = "previous report bytes";
    File.WriteAllText(refusedPath, previousBytes);
    var refused = false;
    try
    {
        excessive.Export(refusedPath, "complete");
    }
    catch (InvalidDataException)
    {
        refused = true;
    }
    Check(refused, "a receipt over the byte budget fails the export with InvalidDataException");
    Check(File.ReadAllText(refusedPath) == previousBytes, "a refused export leaves the previous target bytes unchanged");
    Check(!Directory.EnumerateFiles(directory, ".*.tmp").Any(), "a refused export leaves no temporary file");

    ProjectObjectReferenceContractTests.Run(directory, Check);
}
finally
{
    Directory.Delete(directory, recursive: true);
}

Console.WriteLine($"Forge Runtime report checks: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
