using System.Collections.Concurrent;
using System.Text.Json;
using ForgeDevelopment.Native;

// Prior regression assertions preserved from the earlier continuation.
internal static class PreservedSnapshotTests
{
    internal static void Run(Action<bool, string> Check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-report-snapshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();
        var writer = new AsyncReportWriter(error =>
        {
            errors.Enqueue(error);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) errors.Enqueue(new TimeoutException("Test writer gate timed out."));
        });
        try
        {
            var report = new DiagnosticsReport("snapshot-run");
            var blocked = Path.Combine(directory, "blocked.json");
            Directory.CreateDirectory(blocked);
            Check(writer.Enqueue(report, blocked, "blocked"), "blocking request accepted");
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Writer did not reach test gate.");
            var declarations = new[] { new ProjectObjectDeclaration("exp", "zone", new ProjectZoneLocator(10, 0, 0, 1)) };
            var scan = new ProjectObjectReferenceScan(declarations, 7, 11, ProjectSourceVerification.Pending);
            scan.Start(new[] { new ProjectLayoutKey(10, 0, 0) }, 11);
            scan.ObserveZone(new ProjectZoneCandidate(101, 10, 0, 0, 1));
            report.AttachObjectReferences(scan);
            report.SetMetadata("phase", "before");
            report.Event("generation_job", "step", "first", new() { ["randomAfter"] = "before" }, 2);
            report.Issue("test", "source", "original", "stack-before");
            report.Check("test", "first", "pending", "before");
            var firstPath = Path.Combine(directory, "pending.json");
            Check(writer.Enqueue(report, firstPath, "inspection_pending"), "pending snapshot accepted");
            var firstQueuedAt = DateTimeOffset.UtcNow;
            scan.Complete(12);
            scan.SetSourceVerification(ProjectSourceVerification.Matched);
            report.SetMetadata("phase", "after");
            report.Event("generation_job", "step", "second", new() { ["randomAfter"] = "after" }, 3);
            report.Issue("test", "source", "later", "stack-after");
            report.Check("test", "second", "complete", "after");
            var secondPath = Path.Combine(directory, "complete.json");
            Check(writer.Enqueue(report, secondPath, "inspection_complete"), "complete snapshot accepted");
            scan.Cancel(13);
            report.AttachObjectReferences(new ProjectObjectReferenceScan(declarations, 8, 1, ProjectSourceVerification.Pending));
            report.SetMetadata("phase", "late");
            report.Event("lifecycle", "late", "new-world");
            report.Issue("test", "source", "late");
            report.Check("test", "third", "cancelled", "late");
            for (var index = 0; index < 6; index++)
                Check(writer.Enqueue(report, Path.Combine(directory, "filler-" + index + ".json"), "filler"), "bounded queue slot " + index);
            Check(!writer.Enqueue(report, Path.Combine(directory, "overflow.json"), "overflow"), "ninth pending path is rejected");
            report.SetMetadata("phase", "coalesced");
            var coalescedPath = Path.Combine(directory, "filler-5.json");
            Check(writer.Enqueue(report, coalescedPath, "latest"), "existing path coalesces at capacity");
            report.SetMetadata("phase", "after-coalescing");
            Check(!File.Exists(firstPath), "worker remains gated during report mutation");
            release.Set();
            writer.Dispose();
            Check(errors.Count == 1, "only the intentional write failure is reported");
            Check(!writer.Enqueue(report, Path.Combine(directory, "stopped.json"), "stopped"), "disposed writer rejects work");
            using (var json = JsonDocument.Parse(File.ReadAllBytes(firstPath)))
            {
                var root = json.RootElement;
                var receipt = root.GetProperty("objectReferences");
                Check(root.GetProperty("outcome").GetString() == "inspection_pending", "earlier outcome is preserved");
                Check(root.GetProperty("exportedAtUtc").GetDateTimeOffset() <= firstQueuedAt, "snapshot timestamp is captured before Enqueue returns");
                Check(root.GetProperty("metadata").GetProperty("phase").GetString() == "before", "queued metadata is immutable");
                Check(root.GetProperty("events").GetArrayLength() == 1, "queued events exclude later observations");
                Check(root.GetProperty("eventAggregates")[0].GetProperty("count").GetInt64() == 1, "queued aggregate count is immutable");
                Check(root.GetProperty("eventAggregates")[0].GetProperty("totalElapsedMs").GetDouble() == 2, "queued aggregate timing is immutable");
                Check(root.GetProperty("eventAggregates")[0].GetProperty("lastRandomAfter").GetString() == "before", "queued random-state evidence is immutable");
                Check(root.GetProperty("issues")[0].GetProperty("count").GetInt64() == 1, "queued issue count is immutable");
                Check(root.GetProperty("checks").GetArrayLength() == 1, "queued checks exclude later checks");
                Check(receipt.GetProperty("worldEpoch").GetInt64() == 7, "rebinding a report cannot replace the queued world");
                Check(receipt.GetProperty("simulationTick").GetInt64() == 11, "queued scan tick stays at capture time");
                Check(receipt.GetProperty("scanStatus").GetString() == "pending", "queued scan stays pending after completion and cancellation");
                Check(receipt.GetProperty("sourceVerification").GetString() == "pending", "queued source verification stays pending");
            }
            using (var json = JsonDocument.Parse(File.ReadAllBytes(secondPath)))
            {
                var root = json.RootElement;
                var receipt = root.GetProperty("objectReferences");
                Check(root.GetProperty("metadata").GetProperty("phase").GetString() == "after", "second request captures its own metadata");
                Check(root.GetProperty("events").GetArrayLength() == 2 && root.GetProperty("checks").GetArrayLength() == 2, "second request freezes its own observation set");
                Check(root.GetProperty("issues")[0].GetProperty("count").GetInt64() == 2, "second request freezes its own issue count");
                Check(receipt.GetProperty("worldEpoch").GetInt64() == 7 && receipt.GetProperty("simulationTick").GetInt64() == 12, "second request retains completed world and tick");
                Check(receipt.GetProperty("scanStatus").GetString() == "complete", "later cancellation does not mutate a queued complete receipt");
                Check(receipt.GetProperty("sourceVerification").GetString() == "matched", "verified source snapshot survives later rebinding");
            }
            using (var json = JsonDocument.Parse(File.ReadAllBytes(coalescedPath)))
            {
                Check(json.RootElement.GetProperty("outcome").GetString() == "latest", "coalescing preserves latest accepted outcome");
                Check(json.RootElement.GetProperty("metadata").GetProperty("phase").GetString() == "coalesced", "coalescing replaces the snapshot, not a live report");
            }
            Check(!File.Exists(Path.Combine(directory, "overflow.json")), "rejected request creates no report");
            Check(!Directory.EnumerateFiles(directory, ".*.tmp").Any(), "atomic writes leave no temporary files");
        }
        finally
        {
            release.Set();
            writer.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
