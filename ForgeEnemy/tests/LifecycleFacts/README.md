# E3 lifecycle fact integration tests

The suite compiles the current production Enemy provider, native Hook adapters and
plugin session against an explicitly selected, compiled production SDK.
BepInEx, Harmony and native game objects are test doubles; no GTFO methods are executed.

The cases cover death-flow and limb-break facts, exact target/receiver/index identity,
no-op versus real transitions, stale world/life, duplicate and nested observations,
unknown/missing completion, readback changes, queue rejection, permissions and thread gates.
Native Hook prefix/postfix adapters are invoked as managed test code, not injected.

Plans are built locally from the kernel registry (fact -> QA record step); no website fixture is read.
The two fact -> real Heal integrations (`integration.real-heal-*`) are reported as BLOCKED, not passed:
heal takes many-valued `targets`, and no legal plan can feed them before J-003. See ../../VALIDATION.md.

Use the host build instructions in [Enemy README](../../README.md) to obtain $sdkDll.
Run from the mod repository root with an isolated output/report directory:

```powershell
dotnet build ForgeEnemy/tests/LifecycleFacts/LifecycleFacts.csproj -c Release `
  "-p:ForgeFrameworkAssembly=$sdkDll" --artifacts-path $out
dotnet "$out/bin/LifecycleFacts/release/LifecycleFacts.dll" "$out/lifecycle-result.json"
python ForgeEnemy/tests/LifecycleFacts/verify_mutations.py --sdk $sdkDll --output "$out/mutations"
```

A run with blocked cases and no failures exits 0 but prints `INCOMPLETE` and `BLOCKED n`.
