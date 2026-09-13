"""Build/test diagnostics with isolated outputs; never install or execute the game."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

PROJECTS = (
    ("host", "ForgeRuntime/ForgeRuntime.csproj", False),
    ("development", "ForgeDevelopment/ForgeDevelopment.csproj", False),
    ("development-native", "ForgeDevelopment/Native/ForgeDevelopment.Native.csproj", False),
    ("plugin-startup", "ForgeDevelopment/tests/PluginStartup/PluginStartup.csproj", True),
    ("reports", "ForgeDevelopment/tests/Reports/Reports.csproj", True),
    ("project-checks", "ForgeDevelopment/tests/ProjectChecks/ProjectChecks.csproj", True),
    ("inspection", "ForgeDevelopment/tests/DevelopmentInspection/DevelopmentInspection.csproj", True),
    ("snapshots", "ForgeDevelopment/tests/ReportSnapshots/ReportSnapshots.csproj", True),
    ("shutdown", "ForgeDevelopment/tests/Shutdown/Shutdown.csproj", True),
    ("scene-inventory", "ForgeDevelopment/tests/SceneInventory/SceneInventoryTests.csproj", True),
    ("telemetry", "ForgeDevelopment/tests/Telemetry/Telemetry.csproj", True),
    ("samples", "ForgeDevelopment/tests/Samples/Samples.csproj", True),
)

def source_hashes(repo: Path) -> dict[str, str]:
    result = {}
    for directory in (repo / "ForgeRuntime", repo / "ForgeDevelopment"):
        for parent, dirs, files in os.walk(directory):
            dirs[:] = [d for d in dirs if d not in {"bin", "obj", "evidence", "__pycache__"}]
            for name in files:
                p = Path(parent) / name
                if p.suffix in {".cs", ".csproj", ".py"}:
                    result[p.relative_to(repo).as_posix()] = hashlib.sha256(p.read_bytes()).hexdigest()
    return result

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--bepinex", default=os.environ.get("GTFO_BEPINEX_PATH", ""))
    parser.add_argument("--output", type=Path, help="New evidence/build directory; default is a unique system temp directory")
    parser.add_argument("--timeout", type=int, default=180, help="Per-command timeout in seconds (10-900)")
    args = parser.parse_args()
    repo = args.repo.resolve()
    profile = Path(args.bepinex).resolve()
    if not args.bepinex or not (profile / "core/BepInEx.Core.dll").is_file():
        parser.error("Supply --bepinex or GTFO_BEPINEX_PATH with existing compile references.")
    if not 10 <= args.timeout <= 900:
        parser.error("--timeout must be between 10 and 900 seconds.")
    missing = [p for _, p, _ in PROJECTS if not (repo / p).is_file()]
    if missing:
        parser.error("Required current project missing: " + ", ".join(missing))
    output = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix="forge-diagnostics-"))
    if output == profile or profile in output.parents:
        parser.error("Validation output must be outside the game profile.")
    if args.output:
        output.mkdir(parents=True, exist_ok=False)
    records: list[dict] = []
    before = source_hashes(repo)
    (output / "sources-before.json").write_text(json.dumps(before, indent=2), encoding="utf-8")

    def execute(label: str, command: list[str]) -> int:
        started = time.monotonic()
        try:
            proc = subprocess.run(command, cwd=repo, capture_output=True, timeout=args.timeout)
            code, data = proc.returncode, proc.stdout + proc.stderr
        except subprocess.TimeoutExpired as error:
            code = 124
            data = (error.stdout or b"") + (error.stderr or b"") + b"\nCommand timed out.\n"
        except OSError as error:
            code, data = 126, str(error).encode("utf-8")
        (output / (label + ".log")).write_bytes(data)
        records.append({"label": label, "command": command, "exitCode": code,
                        "durationSeconds": round(time.monotonic() - started, 3)})
        (output / "commands.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
        print(f"{label}: exit={code}", flush=True)
        if code:
            print(data.decode("utf-8", errors="replace")[-3000:], flush=True)
        return code

    artifacts = output / "artifacts"
    options = ["-c", "Release", "--artifacts-path", str(artifacts), "-p:GTFOBepInExPath=" + str(profile)]
    execute("dotnet-version", ["dotnet", "--version"])
    # The plugin compiles against the host and SDK built above, never against an installed profile copy.
    host_inputs = ["-p:ForgeRuntimeAssembly=" + str(artifacts / "bin/ForgeRuntime/release/ForgeRuntime.dll"),
                   "-p:ForgeFrameworkAssembly=" + str(artifacts / "bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll")]
    for label, relative, runnable in PROJECTS:
        extra = host_inputs if label == "development-native" else []
        if execute(label + "-build", ["dotnet", "build", relative] + options + extra) or not runnable:
            continue
        project = repo / relative
        assembly = ET.parse(project).findtext("./PropertyGroup/AssemblyName") or project.stem
        binary = artifacts / "bin" / project.stem / "release" / (assembly + ".dll")
        execute(label, ["dotnet", str(binary)])
    layout = "ForgeDevelopment/tests/NativeLayout/NativeLayout.csproj"
    if not execute("native-layout-build", ["dotnet", "build", layout] + options):
        bin_root = artifacts / "bin"
        execute("native-layout", ["dotnet", str(bin_root / "NativeLayout/release/DevelopmentNativeLayout.dll"), str(profile),
                                  str(bin_root / "ForgeRuntime/release/ForgeRuntime.dll"),
                                  str(bin_root / "ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll"),
                                  str(bin_root / "ForgeDevelopment.Native/release/ForgeDevelopment.Native.dll"),
                                  str(output / "native-layout.json")])
    execute("python-tools", [sys.executable, "-m", "unittest", "discover", "-s", "ForgeDevelopment/tests", "-p", "test_*.py"])
    after = source_hashes(repo)
    (output / "sources-after.json").write_text(json.dumps(after, indent=2), encoding="utf-8")
    changed = sorted(p for p in before.keys() | after.keys() if before.get(p) != after.get(p))
    passed = all(record["exitCode"] == 0 for record in records) and not changed
    summary = {"format": "forge-development-validation", "passed": passed,
               "evidenceLevel": "build-and-managed-tests-only", "sourceChangesDuringRun": changed,
               "commands": records, "gameExecuted": False, "installed": False}
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print("EVIDENCE=" + str(output), flush=True)
    if changed:
        print("Source changed during verification; this is not a stable green baseline: " + ", ".join(changed), flush=True)
    return 0 if passed else 1

if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ET.ParseError) as error:
        print("Validation failed: " + str(error), file=sys.stderr)
        raise SystemExit(2)
