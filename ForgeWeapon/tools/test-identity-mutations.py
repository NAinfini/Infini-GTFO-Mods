"""Exercise deliberate regressions in isolated copies, never in production or the game."""
from __future__ import annotations
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ElementTree

TRX_NAMESPACE = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def trx_outcomes(path: Path) -> list[tuple[str, str]]:
    """(test name, outcome) for every recorded result in a dotnet test TRX report."""
    root = ElementTree.parse(path).getroot()
    definitions = {d.get("id"): d.get("name") for d in root.iter(TRX_NAMESPACE + "UnitTest")}
    return [(definitions.get(r.get("testId"), ""), r.get("outcome", ""))
            for r in root.iter(TRX_NAMESPACE + "UnitTestResult")]


def trx_failed_tests(path: Path) -> list[str]:
    return [name for name, outcome in trx_outcomes(path) if outcome != "Passed"]

weapon = Path(__file__).resolve().parents[1]
repo = weapon.parent
output = weapon / ".artifacts" / ("identity-mutations-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ"))
output.mkdir(parents=True, exist_ok=False)


def sources() -> dict[str, bytes]:
    files = {}
    for root in (weapon, repo / "ForgeRuntime/Framework"):
        for path in root.rglob("*"):
            relative = path.relative_to(repo)
            if any(part in ("bin", "obj", ".artifacts", "__pycache__") for part in relative.parts):
                continue
            if path.is_file() and path.suffix in (".cs", ".csproj", ".props", ".targets", ".json"):
                files[relative.as_posix()] = path.read_bytes()
    files["ForgeWeapon/tools/test-identity-mutations.py"] = Path(__file__).read_bytes()
    return files

mutations = [
    ("ignore-ticket-revision", "EquipmentIdentitySession.cs",
     "Check(entry.Revision == ticket.Revision,", "Check(true,",
     "transfer_invalidates_old_owner_and_aba"),
    ("allow-retired-life", "EquipmentIdentityIndex.cs",
     "Check(!retiredLives.TryGetValue(value.Entity.Id, out var life) || value.Entity.LifeEpoch > life,",
     "Check(true,", "replicator_key_reuse_and_delayed_despawn"),
    ("ignore-owner-probe", "EquipmentIdentitySession.cs",
     "Check(ownerIsCurrent(value.Owner),", "Check(true,",
     "owner_respawn_and_live_owner_probe"),
    ("ignore-world-change", "EquipmentIdentitySession.cs",
     "value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged",
     "value.Kind == RuntimeLifecycleKind.Snapshot", "world_reuse"),
    ("ignore-native-probe", "EquipmentIdentitySession.cs",
     "Check(nativeIsCurrent(value),", "Check(true,", "native_probe_reread_and_fail_closed"),
    ("ignore-life-equality", "EquipmentIdentityIndex.cs",
     "entry.Value.Entity == reference ? entry : null", "entry.Value.Entity.Id == reference.Id ? entry : null",
     "replicator_key_reuse_and_delayed_despawn"),
]
acceptance_failures = {
    "ignore-ticket-revision": "ABA_transfer_never_revives_old_ticket",
    "allow-retired-life": "retired_and_older_lives_cannot_return",
    "ignore-owner-probe": "owner_disappearance_rejects_captured_use",
    "ignore-world-change": "world_change_invalidates_old_ticket",
    "ignore-native-probe": "native_disappearance_rejects_captured_use",
    "ignore-life-equality": "late_remove_does_not_delete_replacement",
}
original = sources()
receipt = {"verification": "isolated-managed-mutations", "gameExecuted": False,
           "installed": False, "results": [], "sourceHashes": {
               path: hashlib.sha256(data).hexdigest() for path, data in sorted(original.items())}}


def invoke(name: str, argv: list[str], cwd: Path) -> int:
    with (output / (name + ".log")).open("w", encoding="utf-8") as log:
        return subprocess.run(argv, cwd=cwd, stdout=log, stderr=subprocess.STDOUT,
                              timeout=900, check=False).returncode

try:
    for name, file, old, new, expected in [("baseline", "", "", "", "")] + mutations:
        print("RUN", name, flush=True)
        workspace = output / name / "src"
        for relative, data in original.items():
            target = workspace / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        if file:
            target = workspace / "ForgeWeapon" / file
            text = target.read_text(encoding="utf-8")
            if text.count(old) != 1:
                raise RuntimeError("Mutation anchor changed: " + name)
            target.write_text(text.replace(old, new), encoding="utf-8")
        build = output / name / "build"
        project = workspace / "ForgeWeapon/tests/Identity/Identity.csproj"
        results = output / name / "results"
        test_code = invoke(name + "-tests", ["dotnet", "test", str(project), "-c", "Release",
                                             "--artifacts-path", str(build), "--results-directory", str(results)], workspace)
        failed = trx_failed_tests(results / (name + "-tests.trx"))
        detected = test_code == 0 and not failed if name == "baseline" else test_code != 0 and expected in failed
        receipt["results"].append({"name": name, "testExit": test_code, "expectedFailedTest": expected,
                                   "failedTests": failed, "passed": detected})
        if not detected:
            raise RuntimeError("Expected regression evidence missing: " + name)
        independent = workspace / "ForgeWeapon/tests/IdentityAcceptance/IdentityAcceptance.csproj"
        acceptance_results = output / name / "acceptance-results"
        code = invoke(name + "-acceptance-tests", ["dotnet", "test", str(independent), "-c", "Release",
                       "--artifacts-path", str(build), "--results-directory", str(acceptance_results)], workspace)
        acceptance_trx = acceptance_results / (name + "-acceptance-tests.trx")
        acceptance_outcomes = trx_outcomes(acceptance_trx)
        failed = [test for test, outcome in acceptance_outcomes if outcome != "Passed"]
        valid = code == 0 and not failed if name == "baseline" else code != 0 and acceptance_failures[name] in failed
        receipt["results"][-1]["independentAcceptance"] = {"testExit": code, "testCount": len(acceptance_outcomes),
                                                          "failedTests": failed, "passed": valid}
        if not valid:
            raise RuntimeError("Independent acceptance did not detect required regression: " + name)
    receipt["sourceUnchanged"] = original == sources()
    if not receipt["sourceUnchanged"]:
        raise RuntimeError("Concurrent source change detected; reconcile and rerun.")
    receipt["status"] = "passed"
except (OSError, ValueError, KeyError, RuntimeError, subprocess.TimeoutExpired) as error:
    receipt.update(status="failed", error=str(error))
finally:
    (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(output / "receipt.json", flush=True)
raise SystemExit(0 if receipt["status"] == "passed" else 1)
