"""Run the independent compiled Weapon/SDK identity consumer tests.
No native method execution, game launch, installation or network access.
Every run retains logs and exact source snapshots under ForgeWeapon/.artifacts.
"""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-root", help="New subdirectory under ForgeWeapon/.artifacts")
    args = parser.parse_args()
    weapon = Path(__file__).resolve().parents[1]
    repo = weapon.parent
    artifacts = (weapon / ".artifacts").resolve()
    output = Path(args.output_root).resolve() if args.output_root else artifacts / (
        "identity-acceptance-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ"))
    if not output.is_relative_to(artifacts) or output == artifacts:
        parser.error("Output must be a new directory under ForgeWeapon/.artifacts")
    output.mkdir(parents=True, exist_ok=False)
    build = output / "build"

    def snapshot() -> dict[str, str]:
        paths = []
        for root in (weapon, repo / "ForgeRuntime/Framework"):
            for path in root.rglob("*"):
                rel = path.relative_to(root)
                if any(p in ("bin", "obj", ".artifacts", "tests", "tools", ".git") for p in rel.parts):
                    continue
                if path.is_file() and path.suffix in (".cs", ".csproj", ".props", ".targets"):
                    paths.append(path)
        paths += list((weapon / "tests/IdentityAcceptance").glob("*.cs"))
        paths += list((weapon / "tests/IdentityAcceptance").glob("*.csproj"))
        paths.append(Path(__file__).resolve())
        for ancestor in (weapon, repo, repo.parent):
            for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"):
                path = ancestor / name
                if path.is_file(): paths.append(path)
        return dict(sorted((str(p.relative_to(repo)) if p.is_relative_to(repo) else str(p),
                            hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths))

    receipt = {"scope": "compiled-identity-sdk-consumer", "gameExecuted": False,
               "installed": False, "status": "running", "stages": [],
               "startedUtc": datetime.now(timezone.utc).isoformat(), "before": snapshot()}
    def run(name: str, command: list[str]) -> int:
        print("RUN", name, flush=True)
        with (output / (name + ".log")).open("w", encoding="utf-8") as log:
            process = subprocess.run(command, cwd=repo, stdout=log, stderr=subprocess.STDOUT,
                                     timeout=180, check=False)
        receipt["stages"].append({"name": name, "argv": command, "exitCode": process.returncode})
        return process.returncode
    try:
        code = run("build", ["dotnet", "build", str(weapon / "tests/IdentityAcceptance/IdentityAcceptance.csproj"),
                   "-c", "Release", "--artifacts-path", str(build), "-p:NuGetAudit=false", "--ignore-failed-sources"])
        if code != 0:
            raise RuntimeError("Compilation failed; tests were not executed. See build.log")
        code = run("tests", ["dotnet", str(build / "bin/IdentityAcceptance/release/IdentityAcceptance.dll"),
                             "--report", str(output / "tests.json")])
        report = json.loads((output / "tests.json").read_text(encoding="utf-8"))
        receipt.update(testCount=len(report["tests"]), failures=report["failures"])
        if code != 0 or report["failures"] != 0:
            raise RuntimeError("Identity acceptance failed. See tests.log and tests.json")
        receipt["status"] = "passed"
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
        receipt.update(status="failed", error=str(error))
        print(str(error), file=sys.stderr)
    finally:
        receipt["after"] = snapshot()
        changed = sorted(k for k in set(receipt["before"]) | set(receipt["after"])
                         if receipt["before"].get(k) != receipt["after"].get(k))
        receipt["changedInputs"] = changed
        if changed:
            receipt["status"] = "changed-inputs"
            print("Concurrent source changes prevent a stable acceptance claim:", changed, file=sys.stderr)
        receipt["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
        print(output / "receipt.json", flush=True)
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
