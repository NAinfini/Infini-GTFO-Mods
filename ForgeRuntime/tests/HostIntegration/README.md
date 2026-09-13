# Compiled SDK / host integration regression

This test executable references the actual `ForgeRuntime.Framework` project.
It does not link a second copy of SDK source or execute Unity/GTFO methods.

Coverage: startup ordering and registration freeze; failed startup without retries;
world/tick snapshots; same-tick deduplication; module-owned subscription cleanup;
generation reuse; self-disposal; exception quarantine and bounded fault evidence;
per-module/global observer budgets; simulation-thread guards; stopped-instance rejection.
With `--host <ForgeRuntime.dll>`, Mono.Cecil checks production assembly boundaries,
`Plugin.Runtime`'s shared type identity, absence of the imported prototype and retired
private startup latch, and a real call from the host to the public `StartRuntime` API.
It also checks the log call-site rules on IL (the Architecture project has neither the
host DLL nor Cecil): no string field of `RuntimeLogRecord`/`RuntimeLogPlan`/`RuntimeLogResult`
receives a value from `string.Concat/Format/Join/Create`, `ToString` or an interpolated-string
handler, directly or through one local, in the SDK or host; and neither the SDK nor
`ForgeRuntime.Logging` calls `SNet_Player.Lookup`. Fixtures in `LogBoundaryFixtures.cs`
must be flagged (and a clean one must not), so a probe that detects nothing fails.
Branches that merge a built string into a setter are not followed.
A standalone SDK pass must not be reported as a host integration pass.

From the repository root, with existing local BepInEx compile references:

```powershell
$env:GTFO_BEPINEX_PATH = '<existing profile BepInEx directory>'
& ./ForgeRuntime/tests/HostIntegration/verify.ps1
```

The script creates a new temporary output directory, builds the full host and test
executable, checks identical SDK binary hashes, and runs the metadata assertions.
It records source hashes before/after, binary hashes, logs and `result.json`.
Any build/test failure, source drift or SDK mismatch returns nonzero; an existing
explicit `-ArtifactRoot` is rejected so stale binaries cannot satisfy this check.
No installed profile, game process, package, Git index or network service is changed.
These are implementation/assembly checks, not native detour or multiplayer validation.
