# E3 lifecycle fact integration tests

The suite compiles the current production Enemy provider, native Hook adapters and
plugin session against an explicitly selected, compiled production SDK.
BepInEx, Harmony and native game objects are test doubles; no GTFO methods are executed.

The cases cover death-flow and limb-break facts, exact target/receiver/index identity,
no-op versus real transitions, stale world/life, duplicate and nested observations,
unknown/missing completion, readback changes, queue rejection, permissions and thread gates.
Native Hook prefix/postfix adapters are invoked as managed test code, not injected.

Plans are built locally from the kernel registry (fact -> QA record step); no website fixture is read.
The real-receiver heal coverage lives in [ReceiverProbe](../ReceiverProbe/Program.cs): it loads a fact -> heal plan
from the same registry and dispatches it with the real kernel into the native receiver, so the amount, the clamp
policy, the `not-alive` rejection and the unknown-commit outcome are stated there against a real write path. This
suite states the fact side, so its two `integration.real-heal-*` cases were removed rather than kept as a second,
weaker copy of that coverage. See ../../VALIDATION.md.

This assembly is named in the SDK's `InternalsVisibleTo`
(`ForgeRuntime/Framework/ForgeRuntime.Framework.csproj`) because the built-in variable module a scope-writing plan
pins and the layout derivation the scope plan reads back are internal to the Framework assembly.

Use the host build instructions in [Enemy README](../../README.md) to obtain $sdkDll.
Run from the mod repository root with an isolated output/report directory:

```powershell
dotnet build ForgeEnemy/tests/LifecycleFacts/LifecycleFacts.csproj -c Release `
  "-p:ForgeFrameworkAssembly=$sdkDll" --artifacts-path $out
dotnet "$out/bin/LifecycleFacts/release/LifecycleFacts.dll" "$out/lifecycle-result.json"
python ForgeEnemy/tests/LifecycleFacts/verify_mutations.py --sdk $sdkDll --output "$out/mutations"
```

Any failed case exits 1 and prints `FAIL`; there is no blocked or skipped outcome.
