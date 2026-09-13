using System.Text.Json;
using ForgeDevelopment.Native;

internal static class D1InspectionReviewTests
{
    internal static void Run(Action<bool, string> check, string directory)
    {
        var declarations = new ProjectObjectDeclaration[]
        {
            new("exp", "zone", new ProjectZoneLocator(10, 0, 0, 1)),
            new("exp", "room", new ProjectRoomLocator("zone",
                new ProjectResourcePin("room-a", new string('a', 64)), "Assets/Rooms/A.prefab"))
        };
        var layouts = new[] { new ProjectLayoutKey(10, 0, 0) };
        ProjectObjectReferenceScan Scan(long epoch = 7, ProjectSourceVerification source = ProjectSourceVerification.Matched)
            => new(declarations, epoch, 11, source);
        JsonElement Receipt(DiagnosticsReport report, string name)
        {
            var path = report.Export(Path.Combine(directory, "d1-" + name + ".json"), "tested");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.GetProperty("objectReferences").Clone();
        }
        var report = new DiagnosticsReport("d1-same-owner");
        var scan = Scan();
        var session = new ProjectInspectionSession(report, scan, 7);
        var before = scan.Snapshot();
        var early = Receipt(report, "before");
        var earlyBytes = File.ReadAllBytes(Path.Combine(directory, "d1-before.json"));
        check(session.AcceptWorld(7, 11), "D1: current native world is accepted");
        session.Start(layouts, 11);
        session.Start(layouts, 12); // Duplicate FactoryDone must not restart a started scan.
        scan.ObserveZone(new ProjectZoneCandidate(101, 10, 0, 0, 1));
        scan.ObserveGeomorph(new ProjectGeomorphObservation(201, 101, "Assets/Rooms/A.prefab", false,
            new[] { new ProjectAreaCandidate(301, 41) }));
        session.Complete(13);
        var snapshot = scan.Snapshot();
        check(session.IsClosed && session.Outcome == "inspection_complete", "D1: native enumeration can complete once");
        check(snapshot.Groups[0].Status == ProjectReferenceStatus.Matched, "D1: explicit native zone tuple matches");
        check(snapshot.Groups[1].ReasonCode == ProjectReferenceReason.CreationContextUnverified,
            "D1: even an identical prefab string without creation evidence cannot match an authored room");
        var latest = Receipt(report, "after");
        check(latest.GetProperty("worldEpoch").GetInt64() == 7 &&
            latest.GetProperty("simulationTick").GetInt64() == 13, "D1: report uses the same scan epoch and completion tick");
        check(latest.GetProperty("groups")[0].GetProperty("status").GetString() == "matched",
            "D1: the attached report sees observations on the exact owned scan");
        check(before.ScanStatus == ProjectScanStatus.NotRequested &&
            early.GetProperty("scanStatus").GetString() == "not_requested", "D1: earlier snapshots stay immutable");
        check(earlyBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "d1-before.json"))),
            "D1: later observations do not rewrite an earlier exported file");
        session.Cancel(14, "cleanup_after_complete");
        check(scan.Snapshot().ScanStatus == ProjectScanStatus.Complete, "D1: cleanup preserves completed historical evidence");

        var oldReport = new DiagnosticsReport("d1-old-world");
        var oldScan = Scan();
        var old = new ProjectInspectionSession(oldReport, oldScan, 7);
        old.Start(layouts, 11);
        check(!old.AcceptWorld(8, 1), "D1: a new world's epoch rejects an old iterator");
        oldScan.ObserveZone(new ProjectZoneCandidate(999, 10, 0, 0, 1));
        old.Complete(1);
        var cancelled = oldScan.Snapshot();
        check(cancelled.ScanStatus == ProjectScanStatus.Cancelled && old.Outcome == "inspection_cancelled",
            "D1: late completion cannot revive a cancelled scan");
        check(cancelled.WorldEpoch == 7 && cancelled.SimulationTick != 1,
            "D1: the new world's tick is not written into the old receipt");
        check(cancelled.Groups.All(group => group.ObservedCandidateCount == 0),
            "D1: observations arriving after cancellation are ignored");
        var nextScan = new ProjectObjectReferenceScan(declarations, 8, 0, ProjectSourceVerification.Matched);
        var nextReport = new DiagnosticsReport("d1-next-world");
        var next = new ProjectInspectionSession(nextReport, nextScan, 8);
        next.Start(layouts, 1);
        nextScan.ObserveZone(new ProjectZoneCandidate(102, 10, 0, 0, 1));
        next.Complete(2);
        check(nextScan.Snapshot().Groups[0].Status == ProjectReferenceStatus.Matched,
            "D1: cancellation does not poison the next run");
        check(Receipt(oldReport, "old").GetProperty("worldEpoch").GetInt64() == 7 &&
            Receipt(nextReport, "next").GetProperty("worldEpoch").GetInt64() == 8,
            "D1: two reports retain independent receipts");
        var beforeStartScan = Scan();
        var beforeStart = new ProjectInspectionSession(new DiagnosticsReport("d1-cancel-before-start"), beforeStartScan, 7);
        beforeStart.Cancel(11, "cleanup_before_factory_done");
        beforeStart.Start(layouts, 12);
        beforeStart.Complete(13);
        check(beforeStartScan.Snapshot().ScanStatus == ProjectScanStatus.Cancelled,
            "D1: cleanup cancels ownership even before an iterator exists");
        var partialScan = Scan();
        var partial = new ProjectInspectionSession(new DiagnosticsReport("d1-partial"), partialScan, 7);
        partial.MarkPartial(); // A missing floor/layout can be detected before native enumeration starts.
        partial.Start(layouts, 11);
        partialScan.ObserveZone(new ProjectZoneCandidate(103, 10, 0, 0, 1));
        partial.Complete(12);
        check(partialScan.Snapshot().ScanStatus == ProjectScanStatus.Partial && partial.Outcome == "inspection_partial",
            "D1: a pre-start coverage failure is not lost at Start");
        check(partialScan.Snapshot().Groups.All(group => group.Status == ProjectReferenceStatus.Unverified),
            "D1: partial coverage does not claim unique authored matches");
        var rejectedScan = Scan();
        rejectedScan.Reject();
        var rejected = new ProjectInspectionSession(new DiagnosticsReport("d1-rejected"), rejectedScan, 7);
        rejected.Start(layouts, 11);
        rejected.Complete(12);
        check(rejected.Outcome == "inspection_rejected" && rejectedScan.Snapshot().ScanStatus == ProjectScanStatus.Rejected,
            "D1: invalid project receipt cannot be promoted to complete");
        var noManifest = new DiagnosticsReport("d1-no-manifest");
        var standalone = new ProjectInspectionSession(noManifest, null, 7);
        standalone.Start(layouts, 11);
        standalone.Complete(12);
        var absent = Receipt(noManifest, "absent");
        check(absent.GetProperty("worldEpoch").GetInt64() == 0 &&
            absent.GetProperty("scanStatus").GetString() == "rejected",
            "D1: standalone diagnostics do not invent a successful project receipt");
        check(standalone.Outcome == "inspection_complete", "D1: native inspection remains separate from project verification");
        var unavailableScan = Scan(0);
        var unavailable = new ProjectInspectionSession(new DiagnosticsReport("d1-epoch-zero"), unavailableScan, 0);
        check(!unavailable.AcceptWorld(0, null), "D1: an unavailable native world is never accepted as epoch zero");
        unavailable.Start(layouts, 12);
        check(unavailableScan.Snapshot().ScanStatus == ProjectScanStatus.Rejected,
            "D1: an unavailable world cannot be started later by a stale callback");
        var pendingScan = Scan(source: ProjectSourceVerification.Pending);
        var pending = new ProjectInspectionSession(new DiagnosticsReport("d1-pending-sources"), pendingScan, 7);
        pending.Start(layouts, 11);
        pendingScan.ObserveZone(new ProjectZoneCandidate(104, 10, 0, 0, 1));
        pending.Complete(12);
        check(pendingScan.Snapshot().Groups[0].ReasonCode == ProjectReferenceReason.SourcePending,
            "D1: native completion cannot stand in for unfinished source verification");
        pendingScan.SetSourceVerification(ProjectSourceVerification.Mismatch);
        check(pendingScan.Snapshot().Groups[0].ReasonCode == ProjectReferenceReason.SourceMismatch,
            "D1: a mismatched source never becomes a match through native completion");
        bool wrongOwnerRejected = false;
        try { _ = new ProjectInspectionSession(new DiagnosticsReport("d1-wrong-owner"), Scan(8), 7); }
        catch (ArgumentException) { wrongOwnerRejected = true; }
        check(wrongOwnerRejected, "D1: constructing a report owner with another world's scan is rejected");
        var neverStarted = new ProjectInspectionSession(new DiagnosticsReport("d1-never-started"), null, 7);
        neverStarted.Complete(12);
        check(neverStarted.Outcome == "inspection_partial", "D1: completion without enumeration cannot claim full coverage");
    }
}
