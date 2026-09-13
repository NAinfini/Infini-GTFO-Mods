using System.Text.Json;
using ForgeDevelopment.Native;

// Focused contract tests for the single current DiagnosticsReport format: one format string, no
// version field, one explicit root field set, an object reference receipt that is always present,
// and native identity carried by checks, issues and the issue aggregation key.
//
// Every native identity in this file is synthetic: the layout, zone, geomorph and area instance
// IDs are chosen here and were never observed in GTFO. A matched group proves the matching
// contract only, never that a real object exists.
static class ProjectObjectReferenceContractTests
{
    private static readonly string[] RootFields =
    {
        "format", "runId", "startedAtUtc", "exportedAtUtc", "outcome", "playability", "metadata",
        "events", "eventAggregates", "checks", "issues", "overflow", "objectReferences"
    };

    private static readonly string[] ReceiptFields =
    {
        "worldEpoch", "simulationTick", "scope", "scanStatus", "sourceVerification", "groups", "overflow"
    };

    public static void Run(string directory, Action<bool, string> check)
    {
        SerializedShape(directory, check);
        AggregateByNativeIdentity(directory, check);
        ReceiptWithoutAttachedScan(directory, check);
        ExportTracksLatestScanSnapshot(directory, check);
        MatchSyntheticCandidates(check);
    }

    private static void SerializedShape(string directory, Action<bool, string> check)
    {
        var report = new DiagnosticsReport("contract-shape");
        report.Check("object", "synthetic-subject", "observed", "Check without a native identity.");
        report.Check("object", "synthetic-zone", "observed", "Check bound to a native identity.",
            new DiagnosticNativeObject("zone", 11));
        report.Issue("MissingReferenceException", "C_CullingCluster.Update", "Unbound message");
        report.Issue("MissingReferenceException", "C_CullingCluster.Update", "Bound message", "",
            new DiagnosticNativeObject("geomorph", 22));

        using var json = JsonDocument.Parse(File.ReadAllText(
            report.Export(Path.Combine(directory, "contract-shape.json"), "complete")));
        var root = json.RootElement;
        var fields = root.EnumerateObject().Select(property => property.Name).ToArray();
        check(fields.Length == RootFields.Length && RootFields.All(field => fields.Contains(field)),
            "report root carries exactly the current field set");
        check(!root.TryGetProperty("schemaVersion", out _), "report root has no schemaVersion");
        check(root.GetProperty("format").GetString() == "gtfo-forge-diagnostics-report",
            "report format is the current one");

        var checks = root.GetProperty("checks");
        check(checks[0].TryGetProperty("nativeObject", out var absent) && absent.ValueKind == JsonValueKind.Null,
            "check without a native object still serializes nativeObject as null");
        var boundCheck = checks[1].GetProperty("nativeObject");
        check(boundCheck.GetProperty("kind").GetString() == "zone" && boundCheck.GetProperty("instanceId").GetInt32() == 11,
            "check serializes its native object identity");

        var issues = root.GetProperty("issues").EnumerateArray().ToArray();
        check(issues.All(issue => issue.TryGetProperty("nativeObject", out _)), "every issue serializes nativeObject");
        check(Bound(issues, 22).GetProperty("count").GetInt64() == 1, "bound issue counts once");
        check(Unbound(issues).GetProperty("count").GetInt64() == 1, "a null native object is a distinct identity");
    }

    private static void AggregateByNativeIdentity(string directory, Action<bool, string> check)
    {
        var first = new DiagnosticNativeObject("zone", 31);
        var report = new DiagnosticsReport("contract-aggregation");
        report.Issue("MissingReferenceException", "C_CullingCluster.Update", "first message", "", first);
        report.Issue("MissingReferenceException", "C_CullingCluster.Update", "second message", "", first);
        report.Issue("MissingReferenceException", "C_CullingCluster.Update", "third message", "",
            new DiagnosticNativeObject("zone", 32));
        report.Issue("MissingReferenceException", "C_CullingCluster.Update", "fourth message", "",
            new DiagnosticNativeObject("area", 33));
        report.Issue("MissingReferenceException", "C_CullingCluster.Update", "fifth message", "");

        using var json = JsonDocument.Parse(File.ReadAllText(
            report.Export(Path.Combine(directory, "contract-aggregation.json"), "complete")));
        var issues = json.RootElement.GetProperty("issues").EnumerateArray().ToArray();
        check(issues.Length == 4, "one source with different native identities does not aggregate");
        var repeated = Bound(issues, 31);
        check(repeated.GetProperty("count").GetInt64() == 2 && repeated.GetProperty("message").GetString() == "first message",
            "one source with one native identity still aggregates and keeps its first observation");
        check(issues.Select(Identity).SequenceEqual(new[] { "null", "area:33", "zone:31", "zone:32" }),
            "aggregated issues keep a deterministic native identity order");
    }

    private static void ReceiptWithoutAttachedScan(string directory, Action<bool, string> check)
    {
        var report = new DiagnosticsReport("contract-unattached");
        using var json = JsonDocument.Parse(File.ReadAllText(
            report.Export(Path.Combine(directory, "contract-unattached.json"), "complete")));
        var receipt = json.RootElement.GetProperty("objectReferences");
        var fields = receipt.EnumerateObject().Select(property => property.Name).ToArray();
        check(fields.Length == ReceiptFields.Length && ReceiptFields.All(field => fields.Contains(field)),
            "unattached report still carries an object reference receipt");
        check(receipt.GetProperty("worldEpoch").GetInt64() == 0, "unattached receipt records epoch 0");
        check(receipt.GetProperty("scanStatus").GetString() == "rejected", "unattached receipt is rejected, never complete");
        check(receipt.GetProperty("sourceVerification").GetString() == "not_provided", "unattached receipt verifies no source");
        check(receipt.GetProperty("simulationTick").ValueKind == JsonValueKind.Null, "unattached receipt has no simulation tick");
        check(receipt.GetProperty("scope").GetString() == "generated-floor", "receipt scope is explicit");
        check(receipt.GetProperty("groups").GetArrayLength() == 0, "unattached receipt claims no group");
        var overflow = receipt.GetProperty("overflow");
        check(overflow.EnumerateObject().All(property => property.Value.GetInt64() == 0),
            "unattached receipt reports no dropped observation");
    }

    private static void ExportTracksLatestScanSnapshot(string directory, Action<bool, string> check)
    {
        var scan = SyntheticScan();
        var report = new DiagnosticsReport("contract-snapshot");
        report.AttachObjectReferences(scan);
        scan.Start(new[] { new ProjectLayoutKey(SyntheticLayoutId, 0, 0) }, 0);
        scan.ObserveZone(new ProjectZoneCandidate(41, SyntheticLayoutId, 0, 0, 0));
        var pending = scan.Snapshot();

        var pendingPath = report.Export(Path.Combine(directory, "contract-pending.json"), "complete");
        var pendingDocument = File.ReadAllText(pendingPath);
        scan.Complete(1);
        var completePath = report.Export(Path.Combine(directory, "contract-complete.json"), "complete");

        using (var json = JsonDocument.Parse(pendingDocument))
        {
            var receipt = json.RootElement.GetProperty("objectReferences");
            check(receipt.GetProperty("scanStatus").GetString() == "pending", "export before completion records a pending scan");
            check(receipt.GetProperty("groups")[0].GetProperty("status").GetString() == "unverified",
                "a pending scan cannot claim a match");
        }
        using (var json = JsonDocument.Parse(File.ReadAllText(completePath)))
        {
            var receipt = json.RootElement.GetProperty("objectReferences");
            check(receipt.GetProperty("scanStatus").GetString() == "complete", "export after completion records the completed scan");
            check(receipt.GetProperty("sourceVerification").GetString() == "matched", "receipt carries the source verification");
            var group = receipt.GetProperty("groups")[0];
            check(group.GetProperty("status").GetString() == "matched" && group.GetProperty("reasonCode").GetString() == "unique",
                "completed synthetic scan resolves its single synthetic candidate");
            check(group.GetProperty("candidates")[0].GetProperty("instanceId").GetInt32() == 41,
                "matched group carries the observed synthetic native identity");
        }
        check(File.ReadAllText(pendingPath) == pendingDocument, "a later scan state never rewrites an exported report");
        check(pending.ScanStatus == ProjectScanStatus.Pending
            && pending.Groups[0].Status == ProjectReferenceStatus.Unverified,
            "an already returned snapshot keeps the state it was taken in");
    }

    private static void MatchSyntheticCandidates(Action<bool, string> check)
    {
        var ambiguous = SyntheticScan();
        ambiguous.Start(new[] { new ProjectLayoutKey(SyntheticLayoutId, 0, 0) }, 0);
        ambiguous.ObserveZone(new ProjectZoneCandidate(41, SyntheticLayoutId, 0, 0, 0));
        ambiguous.ObserveZone(new ProjectZoneCandidate(42, SyntheticLayoutId, 0, 0, 0));
        ambiguous.Complete(1);
        var ambiguousGroup = ambiguous.Snapshot().Groups[0];
        check(ambiguousGroup.Status == ProjectReferenceStatus.Ambiguous
            && ambiguousGroup.ReasonCode == ProjectReferenceReason.MultipleCandidates
            && ambiguousGroup.ObservedCandidateCount == 2,
            "two synthetic candidates stay ambiguous instead of collapsing into one identity");

        var missing = SyntheticScan();
        missing.Start(new[] { new ProjectLayoutKey(SyntheticLayoutId, 0, 0) }, 0);
        missing.Complete(1);
        var missingGroup = missing.Snapshot().Groups[0];
        check(missingGroup.Status == ProjectReferenceStatus.Missing
            && missingGroup.ReasonCode == ProjectReferenceReason.NoCandidate
            && missingGroup.ObservedCandidateCount == 0,
            "a scan without observations stays missing, never matched");
    }

    private const uint SyntheticLayoutId = 1000001;

    private static ProjectObjectReferenceScan SyntheticScan() => new(
        new[] { new ProjectObjectDeclaration("expedition-synthetic", "zone-synthetic",
            new ProjectZoneLocator(SyntheticLayoutId, 0, 0, 0)) },
        1, 0, ProjectSourceVerification.Matched);

    private static JsonElement Bound(IEnumerable<JsonElement> issues, int instanceId) =>
        issues.Single(issue => Identity(issue) != "null" && issue.GetProperty("nativeObject").GetProperty("instanceId").GetInt32() == instanceId);

    private static JsonElement Unbound(IEnumerable<JsonElement> issues) => issues.Single(issue => Identity(issue) == "null");

    private static string Identity(JsonElement issue)
    {
        var nativeObject = issue.GetProperty("nativeObject");
        return nativeObject.ValueKind == JsonValueKind.Null
            ? "null"
            : nativeObject.GetProperty("kind").GetString() + ":" + nativeObject.GetProperty("instanceId").GetInt32();
    }
}
