"""Integration tests for the real metadata auditor, not GTFO gameplay tests.

The supplied binaries are read only. All mutations affect copies of the evidence
contract, never installed game files. Keep the output directory for provenance.
"""
from __future__ import annotations
import argparse
import copy
import json
from pathlib import Path
import subprocess
import sys
import unittest

CONFIG: argparse.Namespace


class NativeAuditTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.root = Path(CONFIG.output_root).resolve()
        cls.root.mkdir(parents=True, exist_ok=False)
        cls.contract = Path(CONFIG.contract).resolve()
        cls.baseline_bytes = cls.contract.read_bytes()
        cls.baseline = json.loads(cls.baseline_bytes)
        cls.invocations: list[dict] = []

    @classmethod
    def tearDownClass(cls) -> None:
        (cls.root / "invocations.json").write_text(
            json.dumps(cls.invocations, indent=2) + "\n", encoding="utf-8")
        if cls.contract.read_bytes() != cls.baseline_bytes:
            raise AssertionError("Original contract changed during tests")

    def run_audit(self, expected: int, *, mutation=None, raw: str | None = None,
                  extra: list[str] | None = None, capture: bool = False,
                  game: str | None = None, output: Path | None = None) -> tuple[dict | None, str]:
        case = self.root / self._testMethodName
        case.mkdir(exist_ok=False)
        result_path = output if output is not None else case / "result.json"
        arguments = ["dotnet", str(Path(CONFIG.audit_dll).resolve()),
                     "--bepinex", str(Path(CONFIG.bepinex).resolve()),
                     "--game", game or str(Path(CONFIG.game).resolve()),
                     "--output", str(result_path)]
        if not capture:
            contract_path = self.contract
            if mutation is not None or raw is not None:
                value = copy.deepcopy(self.baseline)
                if mutation is not None:
                    mutation(value)
                contract_path = case / "input-contract.json"
                contract_path.write_text(raw if raw is not None else json.dumps(value), encoding="utf-8")
            arguments += ["--contract", str(contract_path)]
        arguments += extra or []
        run = subprocess.run(arguments, capture_output=True, text=True,
                             encoding="utf-8", errors="replace", timeout=45, check=False)
        combined = run.stdout + run.stderr
        (case / "process.log").write_text(combined, encoding="utf-8")
        self.invocations.append({"case": self._testMethodName, "argv": arguments,
                                 "exitCode": run.returncode, "expectedExitCode": expected})
        self.assertEqual(expected, run.returncode, combined)
        report = json.loads(result_path.read_text(encoding="utf-8")) if expected in (0, 1) else None
        if report is not None:
            self.assertEqual("metadata-only", report["verification"])
            self.assertIs(False, report["gameExecuted"])
            self.assertEqual(expected == 0, len(report["errors"]) == 0)
        return report, combined

    @staticmethod
    def type_record(contract: dict, name: str) -> dict:
        return next(t for t in contract["types"] if t["name"] == name)

    def test_01_actual_locked_metadata(self) -> None:
        report, _ = self.run_audit(0)
        self.assertEqual("metadata-lock-matched", report["status"])
        expected = 3 + len(self.baseline["files"]) + sum(
            2 + len(t["methods"]) + len(t["properties"]) + len(t["fields"])
            for t in self.baseline["types"])
        self.assertEqual(expected, report["checks"])
        self.assertEqual(len(self.baseline["types"]), len(report["types"]))

    def test_02_wrong_binary_hash(self) -> None:
        _, log = self.run_audit(1, mutation=lambda c: c["files"][0].update(sha256="0" * 64))
        self.assertIn("file hash/size drift", log)

    def test_03_wrong_method_signature(self) -> None:
        def mutate(c):
            self.type_record(c, "Gear.BulletWeapon")["methods"][0] += "_missing"
        _, log = self.run_audit(1, mutation=mutate)
        self.assertIn("method drift", log)

    def test_04_wrong_base_type(self) -> None:
        _, log = self.run_audit(1, mutation=lambda c: c["types"][0].update(baseType="Missing.Base"))
        self.assertIn("base type drift", log)

    def test_05_wrong_property_type(self) -> None:
        def mutate(c):
            self.type_record(c, "Player.BackpackItem")["properties"][0] = "System.Int64 Instance"
        _, log = self.run_audit(1, mutation=mutate)
        self.assertIn("property drift", log)

    def test_06_wrong_replicator_width(self) -> None:
        def mutate(c):
            self.type_record(c, "SNetwork.SNetStructs/pReplicator")["fields"][0] = "System.UInt32 keyPlusOne"
        _, log = self.run_audit(1, mutation=mutate)
        self.assertIn("field drift", log)

    def test_07_missing_type(self) -> None:
        _, log = self.run_audit(1, mutation=lambda c: c["types"][0].update(name="Missing.WeaponType"))
        self.assertIn("missing/ambiguous type", log)

    def test_08_wrong_build(self) -> None:
        _, log = self.run_audit(1, mutation=lambda c: c.update(gameBuild="not-this-build"))
        self.assertIn("game build drift", log)

    def test_09_wrong_revision(self) -> None:
        _, log = self.run_audit(1, mutation=lambda c: c.update(gameRevision="not-this-revision"))
        self.assertIn("game revision drift", log)

    def test_10_empty_contract(self) -> None:
        _, log = self.run_audit(2, mutation=lambda c: c.update(types=[]))
        self.assertIn("Incomplete or unsupported", log)

    def test_11_duplicate_type(self) -> None:
        _, log = self.run_audit(2, mutation=lambda c: c["types"].append(copy.deepcopy(c["types"][0])))
        self.assertIn("duplicated type", log)

    def test_12_game_verified_claim_rejected(self) -> None:
        _, log = self.run_audit(2, mutation=lambda c: c.update(verification="game-verified"))
        self.assertIn("Incomplete or unsupported", log)

    def test_13_duplicate_file(self) -> None:
        _, log = self.run_audit(2, mutation=lambda c: c["files"].append(copy.deepcopy(c["files"][0])))
        self.assertIn("duplicated input fingerprint", log)

    def test_14_invalid_json(self) -> None:
        _, log = self.run_audit(2, raw="{not-json")
        self.assertIn("JsonException", log)

    def test_15_output_cannot_overwrite(self) -> None:
        existing = self.root / "existing-output.json"
        original = b'{"keep":"user evidence"}'
        existing.write_bytes(original)
        _, log = self.run_audit(2, output=existing)
        self.assertIn("never overwritten", log)
        self.assertEqual(original, existing.read_bytes())

    def test_16_missing_game_files(self) -> None:
        _, log = self.run_audit(2, game=str(self.root / "missing" / "common" / "GTFO"))
        self.assertTrue("FileNotFoundException" in log or "DirectoryNotFoundException" in log, log)

    def test_17_duplicate_cli_option(self) -> None:
        _, log = self.run_audit(2, extra=["--game", CONFIG.game])
        self.assertIn("unique option/value", log)

    def test_18_unknown_cli_option(self) -> None:
        _, log = self.run_audit(2, extra=["--unrecognized", "value"])
        self.assertIn("unique option/value", log)

    def test_19_capture_filter_cannot_weaken_lock(self) -> None:
        _, log = self.run_audit(2, extra=["--pattern", "^Nothing$"])
        self.assertIn("capture-only", log)

    def test_20_non_json_output(self) -> None:
        output = self.root / "not-a-report.dll"
        _, log = self.run_audit(2, output=output)
        self.assertIn("NEW .json", log)
        self.assertFalse(output.exists())

    def test_21_invalid_hash_encoding(self) -> None:
        _, log = self.run_audit(2, mutation=lambda c: c["files"][0].update(sha256="not-a-hash"))
        self.assertIn("Invalid or duplicated input", log)

    def test_22_fingerprint_coverage(self) -> None:
        _, log = self.run_audit(1, mutation=lambda c: c["files"].pop())
        self.assertIn("input fingerprint coverage drift", log)

    def test_23_capture_is_not_verification(self) -> None:
        report, _ = self.run_audit(0, capture=True, extra=["--pattern", "^Item$"])
        self.assertEqual("captured-not-verified", report["status"])
        self.assertEqual(0, report["checks"])
        self.assertEqual(["Item"], [t["name"] for t in report["types"]])

    def test_24_missing_cli_value(self) -> None:
        _, log = self.run_audit(2, extra=["--pattern"])
        self.assertIn("unique option/value", log)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--audit-dll", required=True)
    parser.add_argument("--bepinex", required=True)
    parser.add_argument("--game", required=True)
    parser.add_argument("--output-root", required=True, help="New evidence directory; retained, not deleted")
    parser.add_argument("--contract", default=str(Path(__file__).resolve().parents[1] / "evidence/w1-native-contract.json"))
    CONFIG = parser.parse_args()
    unittest.main(argv=[sys.argv[0]], verbosity=2)
