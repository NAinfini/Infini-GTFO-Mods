import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).parents[1] / "scripts" / "compare_runs.py"
spec = importlib.util.spec_from_file_location("compare_runs", SCRIPT)
compare_runs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compare_runs)


ROOT_FIELDS = (
    "format",
    "runId",
    "startedAtUtc",
    "exportedAtUtc",
    "outcome",
    "playability",
    "metadata",
    "events",
    "eventAggregates",
    "checks",
    "issues",
    "overflow",
    "objectReferences",
)


def group(author_id="zone-fixture", *, status="matched", reason_code="unique", instances=(41,)):
    return {
        "expeditionId": "expedition-fixture",
        "kind": "zone",
        "authorId": author_id,
        "locator": {"kind": "zone", "layoutId": 1000001, "dimension": 0, "layer": 0, "localIndex": 0},
        "status": status,
        "reasonCode": reason_code,
        "observedCandidateCount": len(instances),
        "candidatesTruncated": False,
        "candidates": [{"kind": "zone", "instanceId": instance, "layoutId": 1000001} for instance in instances],
    }


def report(
    run_id,
    *,
    zone="50",
    random_after="2",
    issue_count=1,
    lifecycle_count="1",
    scan_status="complete",
    source_verification="matched",
    groups=None,
    receipt_overflow=None,
    metadata=None,
):
    # One current-format report: the exact root field set, no version field, no retired metadata.
    return {
        "format": "gtfo-forge-diagnostics-report",
        "runId": run_id,
        "startedAtUtc": "2026-09-08T14:00:00Z",
        "exportedAtUtc": "2026-09-08T14:05:00Z",
        "outcome": "level_completed",
        "playability": "not_assessed",
        "metadata": metadata if metadata is not None else {
            "pluginVersion": "1.0.0",
            "gameVersion": "R8",
            "unityVersion": "2021.3",
            "seed": "42",
            "hostSeed": "43",
            "sessionSeed": "44",
            "mainLayout": "100",
            "secondaryLayout": "200",
            "thirdLayout": "none",
            "plugin:MTFO": "4.6.2",
        },
        "events": [
            {
                "category": "generation",
                "stage": "place",
                "subject": "geo_test",
                "elapsedMs": 1.0,
                "fields": {"zone": zone, "geomorph": "geo_test", "seed": "42", "randomBefore": "1", "randomAfter": random_after},
            },
            {
                "category": "culling_lifecycle",
                "stage": "cleanup_lifecycle",
                "subject": "C_CullingCluster",
                "elapsedMs": 0.5,
                "fields": {"remainingCount": lifecycle_count},
            },
        ],
        "eventAggregates": [],
        "checks": [{"kind": "lifecycle_cleanup", "subject": "C_CullingCluster", "status": "checked", "detail": "", "nativeObject": None}],
        "issues": [{"type": "MissingReferenceException", "source": "C_CullingCluster.Update", "message": "m", "representativeStack": "", "count": issue_count, "nativeObject": None}],
        "overflow": {
            "droppedMetadata": 0,
            "droppedEvents": 0,
            "sampledDetailEvents": 0,
            "droppedAggregateEvents": 0,
            "droppedChecks": 0,
            "droppedIssues": 0,
            "droppedEventFields": 0,
            "truncatedStrings": 0,
        },
        "objectReferences": {
            "worldEpoch": 1,
            "simulationTick": 12,
            "scope": "generated-floor",
            "scanStatus": scan_status,
            "sourceVerification": source_verification,
            "groups": [group()] if groups is None else groups,
            "overflow": receipt_overflow or {"droppedNativeObjects": 0, "droppedCandidates": 0, "droppedAreas": 0},
        },
    }


class CompareRunsTests(unittest.TestCase):
    def run_compare(self, before, after=None, *arguments):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "before.json").write_text(json.dumps(before), encoding="utf-8")
            if after is None:
                after = report("after")
            (root / "after.json").write_text(json.dumps(after), encoding="utf-8")
            return subprocess.run(
                [sys.executable, str(SCRIPT), str(root / "before.json"), str(root / "after.json"), *arguments],
                text=True,
                capture_output=True,
                check=False,
            )

    def test_matched_runs_with_identical_events_observe_no_difference(self):
        result = self.run_compare(report("before"), report("after"))
        self.assertEqual(0, result.returncode, result.stderr)
        comparison = json.loads(result.stdout)
        self.assertEqual("matched_and_complete", comparison["sourceState"]["assessment"])
        self.assertEqual("fully_verified", comparison["runtimeIdentity"]["status"])
        self.assertEqual("strict_same_inputs", comparison["readiness"])
        self.assertEqual([], comparison["readinessReasons"])
        self.assertEqual("no_observed_difference", comparison["randomAssessment"])
        self.assertFalse(comparison["bugInferred"])
        self.assertFalse(comparison["objectReferences"]["hasChanges"])

    def test_matched_runs_with_different_events_still_never_claim_a_bug(self):
        result = self.run_compare(report("before"), report("after", zone="51", random_after="3", issue_count=4, lifecycle_count="2"))
        comparison = json.loads(result.stdout)
        self.assertEqual("matched_and_complete", comparison["sourceState"]["assessment"])
        self.assertEqual("strict_same_inputs", comparison["readiness"])
        self.assertEqual("same_verified_inputs_difference_requires_investigation", comparison["randomAssessment"])
        self.assertFalse(comparison["bugInferred"])
        self.assertEqual(["51"], comparison["regions"]["added"])
        self.assertEqual(["50"], comparison["regions"]["removed"])
        self.assertEqual(3, comparison["issueChanges"][0]["delta"])
        self.assertIsNone(comparison["issueChanges"][0]["nativeObject"])
        self.assertTrue(any(change["delta"] == 1 for change in comparison["lifecycleChanges"]))

    def test_source_verification_difference_is_a_different_input(self):
        result = self.run_compare(report("before"), report("after", random_after="9", source_verification="mismatch"))
        comparison = json.loads(result.stdout)
        self.assertEqual("different_source_state", comparison["sourceState"]["assessment"])
        self.assertEqual("mismatch", comparison["sourceState"]["after"]["sourceVerification"])
        self.assertEqual("different_inputs", comparison["readiness"])
        self.assertIn("different_source_state", comparison["readinessReasons"])
        self.assertEqual("different_source_state_no_bug_inference", comparison["randomAssessment"])
        self.assertFalse(comparison["bugInferred"])

    def test_scan_status_difference_is_a_different_input(self):
        result = self.run_compare(report("before"), report("after", random_after="9", scan_status="pending"))
        comparison = json.loads(result.stdout)
        self.assertEqual("different_source_state", comparison["sourceState"]["assessment"])
        self.assertEqual("pending", comparison["sourceState"]["after"]["scanStatus"])
        self.assertEqual("different_inputs", comparison["readiness"])
        self.assertEqual("different_source_state_no_bug_inference", comparison["randomAssessment"])

    def test_unverified_source_state_never_claims_strict_same_inputs(self):
        # Same unverified state on both sides is not a difference, but it is still not comparable.
        for verification, scan in (("not_provided", "rejected"), ("matched", "partial"), ("pending", "complete")):
            with self.subTest(verification=verification, scan=scan):
                before = report("before", scan_status=scan, source_verification=verification)
                after = report("after", random_after="9", scan_status=scan, source_verification=verification)
                result = self.run_compare(before, after)
                comparison = json.loads(result.stdout)
                self.assertEqual("incomplete_source_state", comparison["sourceState"]["assessment"])
                self.assertEqual("not_fully_verified", comparison["readiness"])
                self.assertIn("source_state_incomplete", comparison["readinessReasons"])
                self.assertEqual("source_state_incomplete_no_bug_inference", comparison["randomAssessment"])

    def test_one_sided_unverified_source_state_is_a_different_input(self):
        after = report("after", random_after="9", scan_status="rejected", source_verification="not_provided")
        comparison = json.loads(self.run_compare(report("before"), after).stdout)
        self.assertEqual("different_source_state", comparison["sourceState"]["assessment"])
        self.assertEqual("different_inputs", comparison["readiness"])
        self.assertEqual("different_source_state_no_bug_inference", comparison["randomAssessment"])

    def test_reference_group_change_is_reported_from_declared_identity_only(self):
        after = report("after", groups=[group(status="missing", reason_code="no_candidate", instances=())])
        comparison = json.loads(self.run_compare(report("before"), after).stdout)
        change = comparison["objectReferences"]["groupChanges"][0]
        self.assertEqual("zone-fixture", change["reference"]["authorId"])
        self.assertEqual("matched", change["before"]["status"])
        self.assertEqual("no_candidate", change["after"]["reasonCode"])
        self.assertTrue(comparison["objectReferences"]["hasChanges"])
        self.assertEqual("not_fully_verified", comparison["readiness"])
        self.assertIn("object_reference_group_changes", comparison["readinessReasons"])
        # Reference differences are reported as their own change list; the event signature
        # assessment stays about event signatures only.
        self.assertEqual("no_observed_difference", comparison["randomAssessment"])
        self.assertFalse(comparison["bugInferred"])

    def test_reference_candidate_identity_change_is_reported(self):
        after = report("after", groups=[group(status="ambiguous", reason_code="multiple_candidates", instances=(41, 42))])
        comparison = json.loads(self.run_compare(report("before"), after).stdout)
        change = comparison["objectReferences"]["groupChanges"][0]
        self.assertEqual([{"kind": "zone", "instanceId": 41}], change["before"]["candidates"])
        self.assertEqual([{"kind": "zone", "instanceId": 41}, {"kind": "zone", "instanceId": 42}], change["after"]["candidates"])

    def test_reference_group_added_and_removed_are_reported(self):
        after = report("after", groups=[group(author_id="zone-other")])
        comparison = json.loads(self.run_compare(report("before"), after).stdout)
        changes = {change["reference"]["authorId"]: change for change in comparison["objectReferences"]["groupChanges"]}
        self.assertIsNone(changes["zone-fixture"]["after"])
        self.assertIsNone(changes["zone-other"]["before"])
        self.assertTrue(comparison["objectReferences"]["hasChanges"])

    def test_object_reference_overflow_difference_is_a_difference(self):
        after = report("after", random_after="9", receipt_overflow={"droppedNativeObjects": 5, "droppedCandidates": 2, "droppedAreas": 0})
        comparison = json.loads(self.run_compare(report("before"), after).stdout)
        self.assertEqual(
            [
                {"key": "droppedNativeObjects", "before": 0, "after": 5, "delta": 5},
                {"key": "droppedCandidates", "before": 0, "after": 2, "delta": 2},
            ],
            comparison["objectReferences"]["overflowChanges"],
        )
        self.assertTrue(comparison["objectReferences"]["hasChanges"])
        self.assertIn("object_reference_overflow_changes", comparison["readinessReasons"])
        self.assertEqual("not_fully_verified", comparison["readiness"])
        self.assertEqual("not_fully_verified_no_bug_inference", comparison["randomAssessment"])

    def test_runtime_identity_difference_is_a_different_input(self):
        after = report("after", random_after="9")
        after["metadata"]["gameVersion"] = "changed"
        comparison = json.loads(self.run_compare(report("before"), after).stdout)
        self.assertEqual("different_runtime_identity", comparison["runtimeIdentity"]["status"])
        self.assertTrue(any(item["key"] == "gameVersion" for item in comparison["runtimeIdentity"]["differences"]))
        self.assertEqual("different_inputs", comparison["readiness"])
        self.assertEqual("different_runtime_identity_no_bug_inference", comparison["randomAssessment"])

    def test_missing_runtime_identity_never_claims_a_strict_same_input_run(self):
        before, after = report("before"), report("after", random_after="8")
        del before["metadata"]["hostSeed"]
        del after["metadata"]["plugin:MTFO"]
        comparison = json.loads(self.run_compare(before, after).stdout)
        self.assertEqual("not_fully_verified", comparison["runtimeIdentity"]["status"])
        self.assertIn("hostSeed", comparison["runtimeIdentity"]["missingBefore"])
        self.assertEqual("not_fully_verified", comparison["readiness"])
        self.assertIn("runtime_identity_not_fully_verified", comparison["readinessReasons"])

    def test_missing_game_and_unity_versions_are_required_runtime_identity(self):
        before, after = report("before"), report("after", random_after="8")
        del before["metadata"]["gameVersion"]
        del after["metadata"]["unityVersion"]
        comparison = json.loads(self.run_compare(before, after).stdout)
        self.assertIn("gameVersion", comparison["runtimeIdentity"]["missingBefore"])
        self.assertIn("unityVersion", comparison["runtimeIdentity"]["missingAfter"])
        self.assertEqual("not_fully_verified", comparison["readiness"])

    def test_imported_report_is_comparable(self):
        # The exact root shape the log import tool writes: real ISO-8601 session timestamps and the
        # epoch-0 rejected receipt. Nothing here may need a compatibility path.
        before, after = report("before"), report("after")
        for document, started, exported in ((before, "2026-09-08T14:00:00Z", "2026-09-08T14:01:00Z"), (after, "2026-09-08T15:00:00Z", "2026-09-08T15:01:00Z")):
            document["startedAtUtc"] = started
            document["exportedAtUtc"] = exported
            document["outcome"] = "historical_log_imported"
            document["metadata"] = {
                "imported": "true",
                "sourceName": "fixture.log",
                "sourceTimeAvailability": "not_available_in_source",
                "gameStartTimeAvailability": "not_available_in_source",
                "runSeedAvailability": "not_available_in_source",
            }
            document["events"] = []
            document["issues"] = []
            document["checks"] = []
            document["objectReferences"] = {
                "worldEpoch": 0,
                "simulationTick": None,
                "scope": "generated-floor",
                "scanStatus": "rejected",
                "sourceVerification": "not_provided",
                "groups": [],
                "overflow": {"droppedNativeObjects": 0, "droppedCandidates": 0, "droppedAreas": 0},
            }
        result = self.run_compare(before, after)
        self.assertEqual(0, result.returncode, result.stderr)
        comparison = json.loads(result.stdout)
        self.assertEqual("incomplete_source_state", comparison["sourceState"]["assessment"])
        self.assertEqual("not_fully_verified", comparison["readiness"])
        # A text log carries no runtime identity either, so this stays explicitly unverified.
        self.assertEqual("not_fully_verified", comparison["runtimeIdentity"]["status"])
        self.assertIn("runtime_identity_not_fully_verified", comparison["readinessReasons"])
        self.assertEqual("no_observed_difference", comparison["randomAssessment"])

    def test_timestamps_must_be_iso_strings(self):
        for value in (None, "", "not a timestamp", 1757332800):
            with self.subTest(value=value):
                after = report("after")
                after["startedAtUtc"] = value
                result = self.run_compare(report("before"), after)
                self.assertEqual(2, result.returncode)
                self.assertIn("startedAtUtc", result.stderr)

    def test_plugin_version_difference_is_a_different_runtime_identity(self):
        before, after = report("before"), report("after", random_after="8")
        after["metadata"]["plugin:MTFO"] = "changed"
        comparison = json.loads(self.run_compare(before, after).stdout)
        self.assertEqual("different_runtime_identity", comparison["runtimeIdentity"]["status"])
        self.assertEqual("different_inputs", comparison["readiness"])
        self.assertEqual("different_runtime_identity_no_bug_inference", comparison["randomAssessment"])

    def test_prefab_field_is_compared_as_geomorph(self):
        before, after = report("before"), report("after")
        for document, value in ((before, "geo_before"), (after, "geo_after")):
            document["events"][0]["fields"].pop("geomorph")
            document["events"][0]["fields"]["prefab"] = value
        comparison = json.loads(self.run_compare(before, after).stdout)
        self.assertEqual("geo_before", comparison["seedGeomorph"]["removed"][0]["geomorph"])
        self.assertEqual("geo_after", comparison["seedGeomorph"]["added"][0]["geomorph"])

    def test_schema_version_is_rejected_not_migrated(self):
        for run_id in ("before", "after"):
            with self.subTest(run_id=run_id):
                before, after = report("before"), report("after")
                (before if run_id == "before" else after)["schemaVersion"] = 1
                result = self.run_compare(before, after)
                self.assertEqual(2, result.returncode)
                self.assertIn("schemaVersion", result.stderr)

    def test_source_hash_status_metadata_is_rejected(self):
        before, after = report("before"), report("after")
        after["metadata"]["sourceHashStatus"] = "complete"
        result = self.run_compare(before, after)
        self.assertEqual(2, result.returncode)
        self.assertIn("sourceHashStatus", result.stderr)

    def test_retired_root_field_is_rejected(self):
        for field in ("checksPresent", "sourceHashStatus"):
            with self.subTest(field=field):
                after = report("after")
                after[field] = True
                result = self.run_compare(report("before"), after)
                self.assertEqual(2, result.returncode)
                self.assertIn(field, result.stderr)

    def test_wrong_format_is_rejected(self):
        after = report("after")
        after["format"] = "gtfo-forge-diagnostics-report-other"
        result = self.run_compare(report("before"), after)
        self.assertEqual(2, result.returncode)
        self.assertIn("gtfo-forge-diagnostics-report", result.stderr)

    def test_missing_object_references_is_rejected(self):
        after = report("after")
        del after["objectReferences"]
        result = self.run_compare(report("before"), after)
        self.assertEqual(2, result.returncode)
        self.assertIn("objectReferences", result.stderr)

    def test_incomplete_object_reference_receipt_is_rejected(self):
        after = report("after")
        del after["objectReferences"]["scanStatus"]
        result = self.run_compare(report("before"), after)
        self.assertEqual(2, result.returncode)
        self.assertIn("scanStatus", result.stderr)

    def test_unknown_scan_status_is_rejected(self):
        after = report("after", scan_status="finished")
        result = self.run_compare(report("before"), after)
        self.assertEqual(2, result.returncode)
        self.assertIn("scanStatus", result.stderr)

    def test_oversized_report_is_rejected_before_parsing(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            payload = b"x" * (compare_runs.MAX_REPORT_BYTES + 1)
            (root / "before.json").write_bytes(payload)
            (root / "after.json").write_text(json.dumps(report("after")), encoding="utf-8")
            result = subprocess.run([sys.executable, str(SCRIPT), str(root / "before.json"), str(root / "after.json")], text=True, capture_output=True, check=False)
            self.assertEqual(2, result.returncode)
            self.assertIn(str(compare_runs.MAX_REPORT_BYTES), result.stderr)

    def test_cli_writes_the_comparison_to_the_requested_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "before.json").write_text(json.dumps(report("before")), encoding="utf-8")
            (root / "after.json").write_text(json.dumps(report("after")), encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(SCRIPT), str(root / "before.json"), str(root / "after.json"), "--output", str(root / "comparison.json")],
                text=True,
                capture_output=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            comparison = json.loads((root / "comparison.json").read_text(encoding="utf-8"))
            self.assertEqual("before", comparison["beforeRunId"])
            self.assertEqual("after", comparison["afterRunId"])


if __name__ == "__main__":
    unittest.main()
