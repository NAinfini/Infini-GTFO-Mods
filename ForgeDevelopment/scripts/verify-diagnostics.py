"""Build/test diagnostics with isolated outputs; never install or execute the game."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

PROJECTS = (
    ("host", "ForgeRuntime/ForgeRuntime.csproj", False),
    ("map-native", "ForgeMap/Native/ForgeMap.Native.csproj", False),
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

# Negative gate: each mutation is applied to a throwaway copy of the package and the named suite must
# fail. The mutated source must still compile, so a broken build cannot be mistaken for detection.
# Only a reported assertion failure counts as detection: the suite must use its assertion-failure
# exit code (1), exit normally, and print the "<failed>/<total> passed" summary with at least one
# FAIL line. A crash, signal or timeout therefore fails the gate instead of passing as detection.
# Fixed anchors are exact; a missing or duplicated anchor fails the gate instead of skipping it.
SETUP_FAILURE_EXIT = 2
MUTATIONS = (
    {
        "id": "snapshot-freeze",
        "file": "ForgeDevelopment/Native/DiagnosticsReport.cs",
        "project": "ForgeDevelopment/tests/ReportSnapshots/ReportSnapshots.csproj",
        "suite": "report snapshots",
        "breaks": "a queued report is captured at enqueue instead of at export",
        "anchor": """        private readonly ReportDocument document;
        internal FrozenReport(DiagnosticsReport owner, string outcome)
            => document = owner.Capture(outcome);
        internal string Export(string path) => ExportSnapshot(path, ApplyByteBudget(document));
""",
        "replacement": """        private readonly DiagnosticsReport owner;
        private readonly string outcome = "";
        internal FrozenReport(DiagnosticsReport owner, string outcome)
        {
            this.owner = owner;
            this.outcome = outcome;
        }
        internal string Export(string path) => ExportSnapshot(path, ApplyByteBudget(owner.Capture(outcome)));
""",
    },
    {
        "id": "world-isolation",
        "file": "ForgeDevelopment/Native/ProjectInspectionSession.cs",
        "project": "ForgeDevelopment/tests/DevelopmentInspection/DevelopmentInspection.csproj",
        "suite": "inspection",
        "breaks": "a late observation from a newer world is accepted by the old session",
        "anchor": "        if (WorldEpoch > 0 && currentEpoch == WorldEpoch) return true;\n",
        "replacement": "        if (WorldEpoch > 0) return true;\n",
    },
    {
        "id": "bounded-issues",
        "file": "ForgeDevelopment/Native/DiagnosticsReport.cs",
        "project": "ForgeDevelopment/tests/Reports/Reports.csproj",
        "suite": "reports",
        "breaks": "distinct errors are no longer bounded or counted",
        "anchor": """            if (_issues.Count >= MaxIssues)
            {
                Increment(ref _droppedIssues);
                return;
            }

""",
        "replacement": "",
    },
    {
        "id": "bounded-aggregates",
        "file": "ForgeDevelopment/Native/DiagnosticsReport.cs",
        "project": "ForgeDevelopment/tests/Reports/Reports.csproj",
        "suite": "reports",
        "breaks": "repeated trace contexts are no longer bounded",
        "anchor": "    private const int MaxDetailedEvents = 3072, MaxAggregates = 1024;\n",
        "replacement": "    private const int MaxDetailedEvents = 3072, MaxAggregates = 1000000;\n",
    },
    {
        "id": "shutdown-cleanup",
        "file": "ForgeDevelopment/Native/ShutdownSequence.cs",
        "project": "ForgeDevelopment/tests/Shutdown/Shutdown.csproj",
        "suite": "shutdown",
        "breaks": "the first cleanup failure stops the remaining cleanup stages",
        "anchor": "                failures.Add((step.Stage, error));\n",
        "replacement": "                failures.Add((step.Stage, error));\n                return failures.AsReadOnly();\n",
    },
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

    def execute(label: str, command: list[str], cwd: Path = repo, record: bool = True) -> int:
        started = time.monotonic()
        try:
            proc = subprocess.run(command, cwd=cwd, capture_output=True, timeout=args.timeout)
            code, data = proc.returncode, proc.stdout + proc.stderr
        except subprocess.TimeoutExpired as error:
            code = 124
            data = (error.stdout or b"") + (error.stderr or b"") + b"\nCommand timed out.\n"
        except OSError as error:
            code, data = 126, str(error).encode("utf-8")
        (output / (label + ".log")).write_bytes(data)
        if record:
            records.append({"label": label, "command": command, "exitCode": code,
                            "durationSeconds": round(time.monotonic() - started, 3)})
            (output / "commands.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
        print(f"{label}: exit={code}", flush=True)
        if code and record:
            print(data.decode("utf-8", errors="replace")[-3000:], flush=True)
        return code

    def binary_of(artifacts: Path, project: Path) -> Path:
        assembly = ET.parse(project).findtext("./PropertyGroup/AssemblyName") or project.stem
        return artifacts / "bin" / project.stem / "release" / (assembly + ".dll")

    def mutate(mutation: dict) -> dict:
        # The working tree is never edited: the package is copied, mutated and built in the
        # evidence directory, and only a reported assertion failure counts as detection
        # (see the exit-code and output contract above MUTATIONS).
        started = time.monotonic()
        case = output / "mutation" / mutation["id"]
        shutil.copytree(repo / "ForgeDevelopment", case / "ForgeDevelopment",
                        ignore=shutil.ignore_patterns("evidence", "bin", "obj", "__pycache__"))
        result = {"id": mutation["id"], "file": mutation["file"], "suite": mutation["suite"],
                  "breaks": mutation["breaks"], "detected": False}
        target = case / mutation["file"]
        text = target.read_text(encoding="utf-8").replace("\r\n", "\n")
        occurrences = text.count(mutation["anchor"])
        result["anchorOccurrences"] = occurrences
        if occurrences != 1:
            result["reason"] = f"the mutation anchor occurs {occurrences} times; exactly one is required"
        else:
            target.write_text(text.replace(mutation["anchor"], mutation["replacement"]), encoding="utf-8")
            artifacts = case / "artifacts"
            project = case / mutation["project"]
            build = execute("mutation-" + mutation["id"] + "-build",
                            ["dotnet", "build", mutation["project"], "-c", "Release",
                             "--artifacts-path", str(artifacts), "-p:GTFOBepInExPath=" + str(profile)],
                            cwd=case, record=False)
            result["buildExitCode"] = build
            if build != 0:
                result["reason"] = "the mutated source did not compile, so the suite was never exercised"
            else:
                run = execute("mutation-" + mutation["id"] + "-run", ["dotnet", str(binary_of(artifacts, project))],
                              cwd=case, record=False)
                result["testExitCode"] = run
                reported = (output / ("mutation-" + mutation["id"] + "-run.log")).read_bytes().decode("utf-8", errors="replace")
                failures = sum(1 for line in reported.splitlines() if line.startswith("FAIL:"))
                asserts = [(int(failed), int(total)) for failed, total in re.findall(r"\b(\d+)/(\d+) passed\b", reported)
                           if int(failed) and int(failed) < int(total)]
                problem = ""
                if run < 0:
                    problem = "the suite was terminated by a crash or signal"
                elif run != 1:
                    problem = "the suite exited with " + str(run) + " instead of its assertion-failure exit code"
                elif "Unhandled exception" in reported:
                    problem = "the suite reported failures and then crashed on an unhandled exception"
                elif not asserts:
                    problem = "the suite did not report a structured assertion failure"
                elif not failures:
                    problem = "the suite reported a failing summary without any FAIL line"
                result["reportedFailures"] = failures
                result["reportedAssertions"] = [{"failed": failed, "total": total} for failed, total in asserts]
                if problem:
                    result["detected"] = True
                    result["cleanDetection"] = False
                    result["reason"] = problem + "; this is detected but abnormal, not an assertion-level detection"
                elif failures:
                    result["detected"] = True
                    result["cleanDetection"] = True
                else:
                    result["reason"] = "the mutated behaviour still passed every " + mutation["suite"] + " assertion"
        result["durationSeconds"] = round(time.monotonic() - started, 3)
        print("mutation:" + mutation["id"] + ": " + ("detected" if result.get("cleanDetection") else "NOT DETECTED"), flush=True)
        return result

    artifacts = output / "artifacts"
    options = ["-c", "Release", "--artifacts-path", str(artifacts), "-p:GTFOBepInExPath=" + str(profile)]
    execute("dotnet-version", ["dotnet", "--version"])
    # The plugin compiles against the host and SDK built above, never against an installed profile copy.
    host_inputs = ["-p:ForgeRuntimeAssembly=" + str(artifacts / "bin/ForgeRuntime/release/ForgeRuntime.dll"),
                   "-p:ForgeFrameworkAssembly=" + str(artifacts / "bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll")]
    for label, relative, runnable in PROJECTS:
        # Both native halves compile against the host built above. The map half is built first because the
        # diagnostics half links it from the same artifacts path, so a diagnostics build that ran ahead of it
        # would fail on the map assembly the host inputs never name.
        extra = host_inputs if label in ("map-native", "development-native") else []
        if execute(label + "-build", ["dotnet", "build", relative] + options + extra) or not runnable:
            continue
        execute(label, ["dotnet", str(binary_of(artifacts, repo / relative))])
    layout = "ForgeDevelopment/tests/NativeLayout/NativeLayout.csproj"
    if not execute("native-layout-build", ["dotnet", "build", layout] + options):
        bin_root = artifacts / "bin"
        execute("native-layout", ["dotnet", str(bin_root / "NativeLayout/release/DevelopmentNativeLayout.dll"), str(profile),
                                  str(bin_root / "ForgeRuntime/release/ForgeRuntime.dll"),
                                  str(bin_root / "ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll"),
                                  str(bin_root / "ForgeDevelopment.Native/release/ForgeDevelopment.Native.dll"),
                                  str(output / "native-layout.json")])
    execute("python-tools", [sys.executable, "-m", "unittest", "discover", "-s", "ForgeDevelopment/tests", "-p", "test_*.py"])
    mutations = [mutate(mutation) for mutation in MUTATIONS]
    (output / "mutations.json").write_text(json.dumps(mutations, indent=2), encoding="utf-8")
    after = source_hashes(repo)
    (output / "sources-after.json").write_text(json.dumps(after, indent=2), encoding="utf-8")
    changed = sorted(p for p in before.keys() | after.keys() if before.get(p) != after.get(p))
    undetected = [result["id"] for result in mutations if not result["detected"]]
    abnormal = [result["id"] for result in mutations if result.get("cleanDetection") is False]
    passed = all(record["exitCode"] == 0 for record in records) and not changed and not undetected and not abnormal
    summary = {"format": "forge-development-validation", "passed": passed,
               "evidenceLevel": "build-and-managed-tests-only", "sourceChangesDuringRun": changed,
               "commands": records, "mutations": mutations,
               "mutationGate": {"cases": len(mutations), "detected": len(mutations) - len(undetected),
                                "abnormalDetections": abnormal},
               "gameExecuted": False, "installed": False}
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print("EVIDENCE=" + str(output), flush=True)
    if undetected:
        print("Mutation gate failed; these broken behaviours were not detected: " + ", ".join(undetected), flush=True)
    if abnormal:
        print("Mutation gate failed; these broken behaviours were only detected by an abnormal exit, "
              "not by an assertion: " + ", ".join(abnormal), flush=True)
    if changed:
        print("Source changed during verification; this is not a stable green baseline: " + ", ".join(changed), flush=True)
    return 0 if passed else 1

if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ET.ParseError) as error:
        print("Validation failed: " + str(error), file=sys.stderr)
        raise SystemExit(2)
