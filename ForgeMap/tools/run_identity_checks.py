"""Run compiled Map identity, SDK-consumer and architecture checks without game execution."""
from __future__ import annotations
import argparse
import datetime
import hashlib
import json
import pathlib
import subprocess
import sys
import uuid


def sha(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    module = pathlib.Path(__file__).resolve().parents[1]
    root = module.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=pathlib.Path, required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    if not out.is_relative_to(module) or out == module:
        parser.error("Evidence output must be a NEW directory within ForgeMap.")
    out.mkdir(parents=True, exist_ok=False)
    artifacts = module / "bin" / ("identity-checks-" + uuid.uuid4().hex)
    roots = [root / name for name in ("ForgeMap", "ForgeRuntime/Framework",
        "ForgeDevelopment", "ForgeEnemy", "ForgeWeapon", "ForgeTrigger", "ForgeRuntime/tests/Architecture")]
    excluded = {"bin", "obj", "artifacts", ".artifacts", "evidence"}
    def snapshot() -> dict[str, str]:
        return {p.relative_to(root).as_posix(): sha(p) for base in roots
            for p in base.rglob("*") if p.is_file() and p.suffix in (".cs", ".csproj", ".sln")
            and not excluded.intersection(p.relative_to(base).parts)}
    def write(name: str, value: object) -> None:
        (out / name).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    before = snapshot()
    before["Forge.Architecture.sln"] = sha(root / "Forge.Architecture.sln")
    write("source-before.json", before)
    commands: list[dict[str, object]] = []
    def run(label: str, command: list[str]) -> subprocess.CompletedProcess[str]:
        result = subprocess.run(command, cwd=root, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=180)
        (out / (label + ".stdout.log")).write_text(result.stdout, encoding="utf-8")
        (out / (label + ".stderr.log")).write_text(result.stderr, encoding="utf-8")
        commands.append({"label": label, "command": command, "exitCode": result.returncode})
        print(label, "exit", result.returncode, flush=True)
        if result.returncode:
            print(result.stdout[-4000:] + result.stderr[-4000:], flush=True)
        return result
    summaries = {}
    groups = [("identity", module / "tests/MapIdentity/MapIdentity.csproj", "MapIdentity", "ForgeMap.Identity.Tests"),
        ("sdk-consumer", module / "tests/MapContracts/MapContracts.csproj", "MapContracts", "MapContracts"),
        ("architecture", root / "Forge.Architecture.sln", "Architecture", "Architecture")]
    for label, project, folder, assembly in groups:
        build = run(label + "-build", ["dotnet", "build", str(project), "-c", "Release",
            "--artifacts-path", str(artifacts), "-p:UseSharedCompilation=false"])
        if build.returncode:
            continue  # Never execute stale binaries after a failed source build.
        test = run(label + "-tests", ["dotnet", str(artifacts / "bin" / folder / "release" / (assembly + ".dll"))])
        if test.returncode == 0 and label != "architecture":
            summaries[label] = json.loads(test.stdout)
            write(label + "-tests.json", summaries[label])
    after = snapshot()
    after["Forge.Architecture.sln"] = sha(root / "Forge.Architecture.sln")
    write("source-after.json", after)
    changed = sorted(key for key in before.keys() | after.keys() if before.get(key) != after.get(key))
    passed = len(commands) == 6 and all(c["exitCode"] == 0 for c in commands) and not changed
    write("result.json", {"utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "passed": passed, "verification": "compiled-managed-code-and-synthetic-probes-only",
        "nativeGameExecuted": False, "artifacts": str(artifacts), "commands": commands,
        "sourcesUnchanged": not changed, "changedSources": changed,
        "assertions": {k: v["passed"] for k, v in summaries.items()},
        "dllSha256": {p.relative_to(artifacts).as_posix(): sha(p) for p in artifacts.rglob("*.dll")}})
    print(json.dumps({"passed": passed, "changedSources": changed,
        "assertions": {k: v["passed"] for k, v in summaries.items()}, "evidence": str(out)}), flush=True)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
