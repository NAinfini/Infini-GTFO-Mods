# Enemy native entity observation tests

This executable links the single EnemyModule source and the native EnemyEntityObserver.
It uses an explicitly compiled Runtime SDK; only native objects/getters are test doubles.
The default run executes every case. Failures are not silently skipped or reclassified.

```powershell
dotnet build ForgeEnemy/tests/EntityObservation/EntityObservation.csproj -c Release `
  -p:ForgeFrameworkAssembly=<explicit SDK DLL> --artifacts-path <isolated output>
dotnet <isolated output>/bin/EntityObservation/release/EntityObservation.dll <report.json> worktree
```

The second runtime argument records SDK provenance. Use `isolated-proposal` for a patched
QA-only SDK copy; passing tests against that copy does not mean the shared SDK is fixed.
The report records SDK, receiver-source and native-reader SHA-256 values.

`native-reader.*` cases invoke the typed native reader directly. Other cases use the
real provider and Runtime InspectEntities/InspectActor path. Query completeness covers
only explicit references, never all enemies or all objects in a spatial region.
No game is launched, no native method is actually executed, no profile is modified.

Current native observation is host-gated by the provider's existing gameplay gate.
Faction is null, tags are empty, and only a valid living health receiver advertises
`health.heal`. Unknown fields are not filled from kind, names or current AI targets.
Coordinates are the current EnemyAgent.Position value; this is not collider clearance,
spawn suitability, historical membership, LOS evidence or an authoring-to-world transform.
See ../../VALIDATION.md for current integration status and evidence.
