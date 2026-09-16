"""Verify the actual Weapon resolver in the public SDK queue/command lifecycle.
All action bindings are explicit synthetic fixtures. No native call or installation.
The runner retains exact input hashes, the dotnet test log and TRX results, and rejects concurrent-source claims.
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
        "identity-dispatch-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ"))
    if output == artifacts or not output.is_relative_to(artifacts):
        parser.error("Output must be a new subdirectory of ForgeWeapon/.artifacts")
    output.mkdir(parents=True, exist_ok=False)

    def snapshot() -> dict[str, str]:
        paths: set[Path] = {Path(__file__).resolve()}
        for root in (weapon, repo / "ForgeRuntime/Framework", weapon / "tests/IdentityDispatchReview"):
            for p in root.rglob("*"):
                relative = p.relative_to(root)
                if any(x in ("bin", "obj", ".artifacts", "tests", "tools", ".git") for x in relative.parts):
                    continue
                if p.is_file() and p.suffix in (".cs", ".csproj", ".props", ".targets"):
                    paths.add(p)
        ancestors = set(weapon.parents) | {weapon, weapon / "tests", weapon / "tests/IdentityDispatchReview",
                                          repo / "ForgeRuntime", repo / "ForgeRuntime/Framework"}
        for parent in ancestors:
            for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"):
                p = parent / name
                if p.is_file(): paths.add(p)
        return dict(sorted((p.relative_to(repo).as_posix() if p.is_relative_to(repo) else str(p),
                            hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths))

    receipt: dict = {"scope": "compiled-equipment-public-dispatch", "gameExecuted": False,
                     "installed": False, "status": "running", "stages": [],
                     "startedUtc": datetime.now(timezone.utc).isoformat(), "sourceBefore": snapshot()}
    def run(name: str, command: list[str]) -> None:
        print("RUN", name, flush=True)
        with (output / (name + ".log")).open("w", encoding="utf-8") as log:
            result = subprocess.run(command, cwd=repo, stdout=log, stderr=subprocess.STDOUT,
                                    timeout=900, check=False)
        receipt["stages"].append({"name": name, "argv": command, "exitCode": result.returncode})
        if result.returncode != 0: raise RuntimeError(f"{name} failed ({result.returncode}); see {name}.log")
    try:
        results = output / "results"
        code = run("tests", ["dotnet", "test", str(weapon / "tests/IdentityDispatchReview/IdentityDispatchReview.csproj"),
                             "-c", "Release", "--artifacts-path", str(output / "build"),
                             "--results-directory", str(results), "-p:NuGetAudit=false", "--ignore-failed-sources"])
        total, passed, failed = trx_results(results / "tests.trx")
        if code != 0 or not total or failed:
            raise RuntimeError("Unexpected/failed dispatch result: " + ", ".join(failed))
        receipt.update(status="passed", testCount=total, failures=0)
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.TimeoutExpired) as error:
        receipt.update(status="failed", error=str(error))
        print(str(error), file=sys.stderr)
    finally:
        receipt["sourceAfter"] = snapshot()
        receipt["changedInputs"] = sorted(k for k in set(receipt["sourceBefore"]) | set(receipt["sourceAfter"])
                                         if receipt["sourceBefore"].get(k) != receipt["sourceAfter"].get(k))
        if receipt["changedInputs"]: receipt["status"] = "changed-inputs"
        receipt["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
        print(output / "receipt.json", flush=True)
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
