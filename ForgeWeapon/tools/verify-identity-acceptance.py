"""Run the independent compiled Weapon/SDK identity consumer tests through dotnet test.
No native method execution, game launch, installation or network access.
Every run retains the dotnet log, TRX results and exact source snapshots under ForgeWeapon/.artifacts.
"""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ElementTree

TRX_NAMESPACE = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def trx_results(path: Path) -> tuple[int, int, list[str]]:
    """(total, passed, failed test names) from a dotnet test TRX report."""
    root = ElementTree.parse(path).getroot()
    definitions = {d.get("id"): d.get("name") for d in root.iter(TRX_NAMESPACE + "UnitTest")}
    names = [definitions.get(r.get("testId"), "") for r in root.iter(TRX_NAMESPACE + "UnitTestResult")]
    failed = [definitions.get(r.get("testId"), "") for r in root.iter(TRX_NAMESPACE + "UnitTestResult")
              if r.get("outcome") != "Passed"]
    return len(names), len(names) - len(failed), failed


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
                                     timeout=900, check=False)
        receipt["stages"].append({"name": name, "argv": command, "exitCode": process.returncode})
        return process.returncode
    results = output / "results"
    try:
        code = run("tests", ["dotnet", "test", str(weapon / "tests/IdentityAcceptance/IdentityAcceptance.csproj"),
                             "-c", "Release", "--artifacts-path", str(build),
                             "--results-directory", str(results), "-p:NuGetAudit=false", "--ignore-failed-sources"])
        total, passed, failed = trx_results(results / "tests.trx")
        receipt.update(testCount=total, failures=len(failed))
        if code != 0 or not total or failed:
            raise RuntimeError("Identity acceptance failed: " + ", ".join(failed))
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
