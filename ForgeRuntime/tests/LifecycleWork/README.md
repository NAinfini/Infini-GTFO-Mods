# R2a loaded-work lifecycle regression

This executable references the compiled `ForgeRuntime.Framework` SDK. It loads
`cases.json`, the actual exported manifest, compile options and valid plan from
the website's shared runtime fixtures. Native action/resolver calls are managed
counting doubles; the added numeric state is test-only, not a production capability.
No Unity, BepInEx, game installation, network access or extra scheduler is used.

## Covered boundaries

- Stop and failed startup clear queued events, plans, schedules and numeric leases.
- Cleared handles remain safe to cancel/release/dispose repeatedly, including after stop.
- World replacement cancels old work before notification; old handles cannot remove new work with the same IDs.
- Cleanup still rejects wrong-thread access and read-only observer mutation.
- Observers cannot publish, schedule, acquire/release leases, cancel scopes or advance/stop the kernel.
- Reentrant stop after a handler invocation remains `failed/unknown`; duplicate events are not replayed.

## Run from the mod repository root

```powershell
$out = Join-Path $env:TEMP 'forge-lifecycle-work-check'
dotnet build ForgeRuntime/tests/LifecycleWork/LifecycleWork.csproj -c Release --artifacts-path $out
dotnet "$out/bin/LifecycleWork/release/LifecycleWork.dll" --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
```

A nonzero exit signals failure. Results are SDK/fixture evidence, not native or
multiplayer validation. Broader batch evidence: [the Runtime validation record](../../VALIDATION.md).
