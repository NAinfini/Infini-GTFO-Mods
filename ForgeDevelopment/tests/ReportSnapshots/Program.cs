using System.Collections.Concurrent;
using System.Text.Json;
using ForgeDevelopment.Native;

var checks = 0;
var failures = 0;
var root = Path.Combine(Path.GetTempPath(), "forge-report-snapshot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool condition, string message)
{
    checks++;
    if (!condition) { failures++; Console.Error.WriteLine("FAIL: " + message); }
}
JsonDocument Read(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, name)));
var errors = new ConcurrentQueue<Exception>();
try
{
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var blockedPath = Path.Combine(root, "blocked.json");
    Directory.CreateDirectory(blockedPath);
    var writer = new AsyncReportWriter(error =>
    {
        errors.Enqueue(error);
        entered.Set();
        if (!release.Wait(TimeSpan.FromSeconds(20)))
            errors.Enqueue(new TimeoutException("Test writer was not released."));
    });
    var report = new DiagnosticsReport("frozen-run");
    var declaration = new ProjectObjectDeclaration("expedition", "zone-a", new ProjectZoneLocator(10, 0, 0, 1));
    var scan = new ProjectObjectReferenceScan(new[] { declaration }, 7, 12, ProjectSourceVerification.Matched);
    scan.Start(new[] { new ProjectLayoutKey(10, 0, 0) }, 12);
    report.AttachObjectReferences(scan);
    report.SetMetadata("phase", "before");
    report.Event("generation_job", "build", "first", new() { ["jobType"] = "job" }, 2);
    report.Check("world", "floor", "pending", "before");
    report.Issue("error", "source", "before");
    try
    {
        Check(writer.Enqueue(report, blockedPath, "blocker"), "blocking request accepted");
        if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Writer did not enter controlled failure callback.");
        Check(writer.Enqueue(report, Path.Combine(root, "before.json"), "pending"), "snapshot request accepted");
        report.SetMetadata("phase", "after");
        report.Event("generation_job", "build", "second", new() { ["jobType"] = "job" }, 3);
        report.Check("world", "floor", "observed", "after");
        report.Issue("error", "source", "after", "late stack");
        scan.ObserveZone(new ProjectZoneCandidate(101, 10, 0, 0, 1));
        scan.Complete(13);
        Check(writer.Enqueue(report, Path.Combine(root, "after.json"), "complete"), "later independent snapshot accepted");
        report.AttachObjectReferences(new ProjectObjectReferenceScan(Array.Empty<ProjectObjectDeclaration>(), 8, 1, ProjectSourceVerification.NotProvided));
    }
    finally { release.Set(); writer.Dispose(); }
    Check(errors.Count == 1, "a write failure is reported once and later work still drains");
    using (var json = Read("before.json"))
    {
        var doc = json.RootElement;
        Check(doc.GetProperty("metadata").GetProperty("phase").GetString() == "before", "metadata is frozen at enqueue");
        Check(doc.GetProperty("events").GetArrayLength() == 1, "late events cannot enter earlier request");
        Check(doc.GetProperty("eventAggregates")[0].GetProperty("count").GetInt64() == 1, "aggregate count is frozen");
        Check(doc.GetProperty("eventAggregates")[0].GetProperty("totalElapsedMs").GetDouble() == 2, "aggregate cost is frozen");
        Check(doc.GetProperty("checks").GetArrayLength() == 1, "late checks cannot enter earlier request");
        Check(doc.GetProperty("issues")[0].GetProperty("count").GetInt64() == 1, "issue count is frozen");
        Check(doc.GetProperty("issues")[0].GetProperty("representativeStack").GetString() == "", "late stack does not rewrite old evidence");
        var references = doc.GetProperty("objectReferences");
        Check(references.GetProperty("worldEpoch").GetInt64() == 7, "replacement scan cannot change queued world identity");
        Check(references.GetProperty("scanStatus").GetString() == "pending", "pending receipt cannot become completed after enqueue");
        Check(references.GetProperty("groups").GetArrayLength() == 1, "queued declarations cannot be replaced");
        Check(doc.GetProperty("outcome").GetString() == "pending", "outcome and evidence refer to the same request");
        Check(doc.GetProperty("format").GetString() == "gtfo-forge-diagnostics-report" && !doc.TryGetProperty("schemaVersion", out _), "wire format unchanged");
    }
    using (var json = Read("after.json"))
    {
        var doc = json.RootElement;
        Check(doc.GetProperty("metadata").GetProperty("phase").GetString() == "after", "next request captures actual later state");
        Check(doc.GetProperty("events").GetArrayLength() == 2, "next request includes actual later events");
        Check(doc.GetProperty("objectReferences").GetProperty("worldEpoch").GetInt64() == 7, "later request also retains its own scan");
        Check(doc.GetProperty("objectReferences").GetProperty("scanStatus").GetString() == "complete", "next request records completion at capture time");
    }
    Check(!Directory.EnumerateFiles(root, ".*.tmp").Any(), "no temporary files remain");
    QueueBoundaryTests.Run(root, Check);
    WorkerBoundaryTests.Run(root, Check);
    PreservedSnapshotTests.Run(Check);
    TraceBackReferenceTests.Run(root, Check);
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"Forge Development report snapshots: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
