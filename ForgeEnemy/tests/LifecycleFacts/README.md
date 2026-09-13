# E3 lifecycle fact integration tests

The suite compiles the current production Enemy provider, native Hook adapters and
plugin session against an explicitly selected, compiled production SDK.
BepInEx, Harmony and native game objects are test doubles; no GTFO methods are executed.

52 cases cover death-flow and limb-break facts, exact target/receiver/index identity,
no-op versus real transitions, stale world/life, duplicate and nested observations,
unknown/missing completion, readback changes, queue rejection, permissions and thread gates.
Native Hook prefix/postfix adapters are invoked as managed test code, not injected.

Two real-receiver integrations exercise the existing Runtime plan loader and Heal handler:
limb_broken requests +5 HP exactly once; death_started cannot implicitly revive an enemy.
The passing limb case emits the reusable example plan into the report's directory.
The checked copy is [examples/limb-broken-heal.plan.json](../../examples/limb-broken-heal.plan.json).
It remains an offline implementation test, not a verified playable content pack.

Use the host build instructions in [Enemy README](../../README.md) to obtain $sdkDll.
Run from the mod repository root with an isolated output/report directory:

```powershell
dotnet build ForgeEnemy/tests/LifecycleFacts/LifecycleFacts.csproj -c Release `
  "-p:ForgeFrameworkAssembly=$sdkDll" --artifacts-path $out
dotnet "$out/bin/LifecycleFacts/release/LifecycleFacts.dll" `
  ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime "$out/lifecycle-result.json"
python ForgeEnemy/tests/LifecycleFacts/verify_mutations.py --sdk $sdkDll `
  --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime --output "$out/mutations"
```
