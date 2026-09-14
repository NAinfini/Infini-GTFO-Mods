# Independent Enemy commit-path audit

This test executable links the real existing `EnemyModule.cs` and the concurrent
migration-ready `EnemyHealthCommit.cs` against an explicitly compiled public SDK.
It uses game doubles, not loaded GTFO/native code. The two paths are named separately
in the JSON report; migration-boundary success does NOT activate it in the host.
Tests must not be changed to accept known failures just to obtain exit code zero.

From the mod repository root, using PowerShell:

```powershell
$out = Join-Path $env:TEMP ('forge-commit-audit-' + [guid]::NewGuid().ToString('N'))
dotnet build ForgeRuntime/Framework/ForgeRuntime.Framework.csproj -c Release --artifacts-path $out
$sdk = Join-Path $out 'bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll'
dotnet build ForgeEnemy/tests/CommitAudit/CommitAudit.csproj -c Release --artifacts-path $out "-p:ForgeFrameworkAssembly=$sdk"
dotnet "$out/bin/CommitAudit/release/CommitAudit.dll" "$out/report.json"
```

Damage cases load a damage_applied -> QA record plan built from the kernel registry; no website fixture is read.
Every case either passes or fails; there is no blocked or skipped outcome.

Stop after any failed build; never run an older executable as the result of a new build.
The report records SDK/source hashes. Compare source hashes before and after
building in an active workspace; a changed source means the report is version-specific,
not proof of the latest file. Source copies in build output are test evidence only.

At the recorded 2026-09-12 checkpoint, existing path: 21/32; migration health: 20/20.
The combined runner correctly returns exit 1 because 11 existing-path tests fail.
The 20 health cases are intentionally run on both paths, not 20 new independent features.
See ../../VALIDATION.md and ../../evidence/commit-audit-comparison-20260912.json.
