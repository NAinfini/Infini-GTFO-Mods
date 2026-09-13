import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime


SCRIPT = Path(__file__).parents[1] / "scripts" / "import_log.py"
sys.path.insert(0, str(SCRIPT.parent))
from import_log import MAX_SOURCE_BYTES, REPORT_FORMAT, import_stream


# The website report reader. The import output has to survive it unchanged: it is the only
# consumer that decides whether an exported report can be imported at all.
SITE_ROOT = Path(os.environ.get(
    "FORGE_SITE_ROOT", Path(__file__).resolve().parents[3] / "Infini-GTFO-Model-Site"
)).expanduser().resolve()
SITE_READER = SITE_ROOT / "site" / "map-balance-report.ts"
SITE_LOADER = SITE_ROOT / "Tools" / "register-typescript.ts"


# The current diagnostics root: one format string, no version field, no retired field.
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
REPORT_OVERFLOW_FIELDS = (
    "droppedMetadata",
    "droppedEvents",
    "sampledDetailEvents",
    "droppedAggregateEvents",
    "droppedChecks",
    "droppedIssues",
    "droppedEventFields",
    "truncatedStrings",
)
RECEIPT_OVERFLOW = {"droppedNativeObjects": 0, "droppedCandidates": 0, "droppedAreas": 0}


def parsed_timestamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def import_text(text, run_id="historical"):
    return import_stream(io.StringIO(text), "fixture.log", "abc", run_id)


class ImportLogTests(unittest.TestCase):
    def test_repeated_errors_are_aggregated_and_unknowns_stay_explicit(self):
        culling = "ERROR : A renderer has been destroyed that is included in a C_CullingCluster. Dont let this happen!\nCullingSystem.C_CullingCluster:HideSafe()\n\n"
        exception = "NullReferenceException: missing object\nUnityEngine.Debug:LogError(Object)\nGame.Loader:Build()\n\n"
        report = import_text(culling * 3 + exception * 2)
        by_type = {issue["type"]: issue for issue in report["issues"]}
        self.assertEqual(3, by_type["DestroyedRendererInCullingCluster"]["count"])
        self.assertEqual(2, by_type["NullReferenceException"]["count"])
        self.assertEqual("Game.Loader:Build", by_type["NullReferenceException"]["source"])
        self.assertIn("Game.Loader:Build()", by_type["NullReferenceException"]["representativeStack"])
        # The log carries no error timestamp, so the import session time is what gets recorded.
        self.assertEqual(report["startedAtUtc"], by_type["NullReferenceException"]["firstSeenUtc"])
        self.assertEqual(report["startedAtUtc"], by_type["NullReferenceException"]["lastSeenUtc"])
        self.assertEqual("not_assessed", report["playability"])
        self.assertEqual("not_available_in_source", report["metadata"]["runSeedAvailability"])
        self.assertTrue(all(check["status"] == "not_checked" for check in report["checks"] if check["kind"] != "historical_import"))

    def test_report_has_exactly_the_current_root_fields(self):
        report = import_text("")
        self.assertEqual(sorted(ROOT_FIELDS), sorted(report))
        self.assertEqual(13, len(report))
        self.assertEqual(REPORT_FORMAT, report["format"])
        self.assertNotIn("schemaVersion", report)
        self.assertNotIn("checksPresent", report)
        self.assertEqual("historical_log_imported", report["outcome"])
        self.assertEqual([], report["events"])
        self.assertEqual([], report["eventAggregates"])
        self.assertEqual(sorted(REPORT_OVERFLOW_FIELDS), sorted(report["overflow"]))

    def test_root_timestamps_describe_the_import_session(self):
        report = import_text("")
        started, exported = report["startedAtUtc"], report["exportedAtUtc"]
        # Both root timestamps stay ISO-8601 strings: the website reader rejects anything else.
        self.assertIsInstance(started, str)
        self.assertIsInstance(exported, str)
        self.assertLessEqual(parsed_timestamp(started), parsed_timestamp(exported))
        self.assertEqual("not_available_in_source", report["metadata"]["gameStartTimeAvailability"])
        self.assertEqual("not_available_in_source", report["metadata"]["sourceTimeAvailability"])

    def test_import_receipt_is_rejected_and_unverified(self):
        receipt = import_text("Nothing failed here.\n")["objectReferences"]
        self.assertEqual(
            {
                "worldEpoch": 0,
                "simulationTick": None,
                "scope": "generated-floor",
                "scanStatus": "rejected",
                "sourceVerification": "not_provided",
                "groups": [],
                "overflow": RECEIPT_OVERFLOW,
            },
            receipt,
        )
        # A rejected epoch-0 receipt is the whole document: nothing may claim a match.
        self.assertNotIn('"matched"', json.dumps(receipt))

    def test_every_generated_check_and_issue_carries_a_null_native_object(self):
        report = import_text("NullReferenceException: missing object\nGame.Loader:Build()\n[Error  :     Unity] simple failure\n")
        self.assertTrue(report["checks"])
        self.assertTrue(report["issues"])
        for check in report["checks"]:
            self.assertIn("nativeObject", check)
            self.assertIsNone(check["nativeObject"])
        for issue in report["issues"]:
            self.assertIn("nativeObject", issue)
            self.assertIsNone(issue["nativeObject"])
            # Every issue timestamp is a real ISO-8601 string: the website reader rejects null.
            for field in ("firstSeenUtc", "lastSeenUtc"):
                self.assertIsInstance(issue[field], str)
                self.assertLessEqual(parsed_timestamp(report["startedAtUtc"]), parsed_timestamp(issue[field]))
                self.assertLessEqual(parsed_timestamp(issue[field]), parsed_timestamp(report["exportedAtUtc"]))

    def test_report_overflow_only_counts_this_tool_own_losses(self):
        report = import_text("NullReferenceException: missing object\n")
        self.assertEqual(0, report["overflow"]["droppedIssues"])
        self.assertEqual(0, report["overflow"]["truncatedStrings"])
        self.assertTrue(all(value == 0 for key, value in report["overflow"].items() if key not in ("droppedIssues", "truncatedStrings")))

    def test_cli_writes_the_current_report_and_source_identity(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = b"[Error  :     Unity] simple failure\n"
            source, output = root / "input.log", root / "report.json"
            source.write_bytes(data)
            result = subprocess.run([sys.executable, str(SCRIPT), str(source), str(output), "--run-id", "fixture"], text=True, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(sorted(ROOT_FIELDS), sorted(report))
            self.assertEqual(REPORT_FORMAT, report["format"])
            self.assertEqual("fixture", report["runId"])
            self.assertEqual(hashlib.sha256(data).hexdigest(), report["metadata"]["sourceSha256"])
            self.assertEqual("rejected", report["objectReferences"]["scanStatus"])

    def test_cli_rejects_an_oversized_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, output = root / "input.log", root / "report.json"
            source.write_bytes(b"x" * (MAX_SOURCE_BYTES + 1))
            result = subprocess.run([sys.executable, str(SCRIPT), str(source), str(output)], text=True, capture_output=True)
            self.assertEqual(2, result.returncode)
            self.assertIn(str(MAX_SOURCE_BYTES), result.stderr)
            self.assertFalse(output.exists())

    def test_cli_output_is_accepted_by_the_website_report_reader(self):
        self.assertTrue(SITE_READER.is_file(), f"the website report reader is required for this cross-end check: {SITE_READER}")
        self.assertTrue(SITE_LOADER.is_file(), f"the current website TypeScript loader is required: {SITE_LOADER}")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, output = root / "input.log", root / "report.json"
            source.write_text("[Error  :     Unity] simple failure\nNullReferenceException: missing object\nGame.Loader:Build()\n", encoding="utf-8")
            result = subprocess.run([sys.executable, str(SCRIPT), str(source), str(output), "--run-id", "site-check"], text=True, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            script = root / "read-report.mjs"
            script.write_text(
                f"import {{ readBalanceRuntimeReport }} from {json.dumps(SITE_READER.as_uri())};\n"
                "import { readFileSync } from 'node:fs';\n"
                f"const report = JSON.parse(readFileSync({json.dumps(str(output))}, 'utf8'));\n"
                "const parsed = readBalanceRuntimeReport(JSON.stringify(report));\n"
                "if (parsed.runId !== 'site-check') throw new Error('runId changed: ' + parsed.runId);\n"
                "if (parsed.startedAtUtc !== report.startedAtUtc || parsed.exportedAtUtc !== report.exportedAtUtc)\n"
                "  throw new Error('root timestamps were not read back unchanged');\n"
                "if (parsed.objectReferences.scanStatus !== 'rejected' || parsed.objectReferences.worldEpoch !== 0)\n"
                "  throw new Error('the epoch-0 rejected receipt was not preserved');\n"
                "if (parsed.checks.some((check) => check.nativeObject !== null) || parsed.issues.some((issue) => issue.nativeObject !== null))\n"
                "  throw new Error('an imported check or issue claimed a native object');\n"
                "if (parsed.issues.some((issue) => typeof issue.firstSeenUtc !== 'string' || typeof issue.lastSeenUtc !== 'string'))\n"
                "  throw new Error('an imported issue lost its string timestamps');\n"
                "// The reader is the authority on the root timestamp type: null has to be rejected.\n"
                "for (const field of ['startedAtUtc', 'exportedAtUtc']) {\n"
                "  try { readBalanceRuntimeReport(JSON.stringify({ ...report, [field]: null })); }\n"
                "  catch { continue; }\n"
                "  throw new Error('the report reader accepted a null ' + field);\n"
                "}\n"
                "process.stdout.write('accepted ' + parsed.format + ' ' + parsed.issues.length + ' issues');\n",
                encoding="utf-8",
            )
            reader = subprocess.run(
                ["node", "--import", SITE_LOADER.as_uri(), str(script)],
                cwd=SITE_ROOT, text=True, capture_output=True, encoding="utf-8", timeout=30,
            )
            self.assertEqual(0, reader.returncode, reader.stderr)
            self.assertIn("accepted", reader.stdout)


if __name__ == "__main__":
    unittest.main()
