"""Exercise deliberate regressions in isolated copies, never in production or the game."""
from __future__ import annotations
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess

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
     "FAIL: expected rejection equipment.observation-changed"),
    ("allow-retired-life", "EquipmentIdentityIndex.cs",
     "Check(!retiredLives.TryGetValue(value.Entity.Id, out var life) || value.Entity.LifeEpoch > life,",
     "Check(true,", "FAIL: expected rejection equipment.retired-life"),
    ("ignore-owner-probe", "EquipmentIdentitySession.cs",
     "Check(ownerIsCurrent(value.Owner),", "Check(true,",
     "FAIL: expected rejection equipment.owner-not-current"),
    ("ignore-world-change", "EquipmentIdentitySession.cs",
     "value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged",
     "value.Kind == RuntimeLifecycleKind.Snapshot", "FAIL: world flush"),
    ("ignore-native-probe", "EquipmentIdentitySession.cs",
     "Check(nativeIsCurrent(value),", "Check(true,", "FAIL: stale native rejected"),
    ("ignore-life-equality", "EquipmentIdentityIndex.cs",
     "entry.Value.Entity == reference ? entry : null", "entry.Value.Entity.Id == reference.Id ? entry : null",
     "FAIL: late despawn cannot remove replacement"),
]
acceptance_failures = {
    "ignore-ticket-revision": "ABA-transfer-never-revives-old-ticket",
    "allow-retired-life": "retired-and-older-lives-cannot-return",
    "ignore-owner-probe": "owner-disappearance-rejects-captured-use",
    "ignore-world-change": "world-change-invalidates-old-ticket",
    "ignore-native-probe": "native-disappearance-rejects-captured-use",
    "ignore-life-equality": "late-remove-does-not-delete-replacement",
}
original = sources()
receipt = {"verification": "isolated-managed-mutations", "gameExecuted": False,
           "installed": False, "results": [], "sourceHashes": {
               path: hashlib.sha256(data).hexdigest() for path, data in sorted(original.items())}}


def invoke(name: str, argv: list[str], cwd: Path) -> int:
    with (output / (name + ".log")).open("w", encoding="utf-8") as log:
        return subprocess.run(argv, cwd=cwd, stdout=log, stderr=subprocess.STDOUT,
                              timeout=180, check=False).returncode

try:
    for name, file, old, new, expected in [("baseline", "", "", "", "RESULT ")] + mutations:
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
        build_code = invoke(name + "-build", ["dotnet", "build", str(project), "-c", "Release",
                                             "--artifacts-path", str(build)], workspace)
        if build_code != 0:
            raise RuntimeError(name + " failed to compile; not credited as a detected mutation")
        test_code = invoke(name + "-tests", ["dotnet", str(build / "bin/Identity/release/Identity.dll")], workspace)
        text = (output / (name + "-tests.log")).read_text(encoding="utf-8")
        detected = (test_code == 0 if name == "baseline" else test_code != 0) and expected in text
        receipt["results"].append({"name": name, "buildExit": build_code, "testExit": test_code,
                                   "expectedEvidence": expected, "passed": detected})
        if not detected:
            raise RuntimeError("Expected regression evidence missing: " + name)
        independent = workspace / "ForgeWeapon/tests/IdentityAcceptance/IdentityAcceptance.csproj"
        code = invoke(name + "-acceptance-build", ["dotnet", "build", str(independent), "-c", "Release",
                       "--artifacts-path", str(build)], workspace)
        if code != 0:
            raise RuntimeError(name + " independent consumer did not compile; no mutation credit")
        report_path = output / (name + "-acceptance.json")
        code = invoke(name + "-acceptance-tests", ["dotnet", str(build / "bin/IdentityAcceptance/release/IdentityAcceptance.dll"),
                       "--report", str(report_path)], workspace)
        report = json.loads(report_path.read_text(encoding="utf-8"))
        failed = [row["name"] for row in report["tests"] if row["status"] == "failed"]
        valid = code == 0 and not failed if name == "baseline" else code != 0 and acceptance_failures[name] in failed
        receipt["results"][-1]["independentAcceptance"] = {"testExit": code, "testCount": len(report["tests"]),
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
