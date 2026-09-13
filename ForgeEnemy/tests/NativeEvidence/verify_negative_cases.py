"""Exercise the real static auditor against isolated, deliberately broken specifications."""
from __future__ import annotations
import argparse
import copy
import json
from pathlib import Path
import subprocess


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("auditor", type=Path)
    parser.add_argument("bepinex", type=Path)
    parser.add_argument("game", type=Path)
    parser.add_argument("spec", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    original = json.loads(args.spec.read_text(encoding="utf-8"))
    results: list[dict] = []

    def run(name: str, candidate: dict, expected_id: str | None) -> bool:
        spec_path = args.output / f"{name}.spec.json"
        report_path = args.output / f"{name}.report.json"
        spec_path.write_text(json.dumps(candidate, indent=2), encoding="utf-8")
        command = ["dotnet", str(args.auditor.resolve()), str(args.bepinex.resolve()),
                   str(args.game.resolve()), str(spec_path.resolve()), str(report_path.resolve())]
        completed = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
        (args.output / f"{name}.log").write_text(completed.stdout + completed.stderr, encoding="utf-8")
        report = json.loads(report_path.read_text(encoding="utf-8"))
        failed_ids = [check["Id"] for check in report["checks"] if not check["Passed"]]
        passed = (completed.returncode == 0 and not failed_ids) if expected_id is None else (
            completed.returncode == 1 and expected_id in failed_ids)
        results.append({"id": name, "passed": passed, "exitCode": completed.returncode,
                        "expectedFailure": expected_id, "observedFailures": failed_ids})
        print(f"{'PASS' if passed else 'FAIL'} {name}: exit={completed.returncode}, failures={failed_ids}")
        return passed

    if not run("baseline", original, None):
        raise RuntimeError("Baseline auditor failed; mutation rejection cannot be credited.")
    mutations = []
    item = copy.deepcopy(original); item["buildId"] = "0"
    mutations.append(("wrong-build", item, "steam.build"))
    item = copy.deepcopy(original); item["gameAssemblySha256"] = "0" * 64
    mutations.append(("wrong-native-hash", item, "native.hash"))
    item = copy.deepcopy(original); item["assemblies"][0]["sha256"] = "0" * 64
    mutations.append(("wrong-interop-hash", item, "hash." + item["assemblies"][0]["file"]))
    item = copy.deepcopy(original); item["assemblies"][0]["mvid"] = "00000000-0000-0000-0000-000000000000"
    mutations.append(("wrong-mvid", item, "mvid." + item["assemblies"][0]["file"]))
    item = copy.deepcopy(original); item["methods"][0]["signature"] = "System.Void Missing::Method()"
    mutations.append(("wrong-signature", item, item["methods"][0]["id"]))
    item = copy.deepcopy(original); item["methods"][0]["isStatic"] = not item["methods"][0]["isStatic"]
    mutations.append(("wrong-static-flag", item, item["methods"][0]["id"]))
    item = copy.deepcopy(original); item["methods"][0]["type"] = "Missing.Type"
    mutations.append(("missing-type", item, item["methods"][0]["id"]))
    item = copy.deepcopy(original); item["absentDeclaredMethods"][0]["name"] = "ReceiveSetHealth"
    mutations.append(("false-absence-claim", item, item["absentDeclaredMethods"][0]["id"]))
    item = copy.deepcopy(original); item["methods"] = []
    mutations.append(("empty-spec", item, "audit.error"))
    item = copy.deepcopy(original); item["schemaVersion"] = 999
    mutations.append(("unsupported-schema", item, "audit.error"))
    for name, candidate, expected_id in mutations:
        run(name, candidate, expected_id)
    summary = {"schemaVersion": 1, "gameExecuted": False, "passed": sum(r["passed"] for r in results),
               "failed": sum(not r["passed"] for r in results), "checks": results}
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return 1 if summary["failed"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
