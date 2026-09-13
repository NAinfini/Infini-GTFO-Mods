# Changelog

## 1.1.2

- Use native job type and instance identity instead of nullable display names in generation reports.
- Aggregate high-frequency job, placement, spawner and culling observations, retaining counts, time ranges, duration totals/maxima and first/last random states. Reserve 1,024 event slots for other observations; disclose sampling and capacity overflow separately.
- Root the native Unity log callback for its registered lifetime. Continue final export and writer disposal even if another shutdown step fails.
- Traverse floor components cooperatively and only construct hierarchy paths for relevant objects. Cache native component type names within each inventory scan.
- Report comparisons expose aggregation and overflow coverage. Retained detail samples do not prove identical generation.
- Offline validation: 41 report checks (including 150,001 high-frequency observations and shutdown fault injection), 249 native hook contracts. Live performance and shutdown acceptance remain pending.

## 1.1.1

- Fix startup trampoline failure caused by detouring shared IL2CPP constant-return bodies.
- Replace inheritance-based generation hook discovery with explicitly audited native targets; exclude the shared GetShadowRenderGroups body.
- Require native body alias checks as well as managed signature checks before release.
- Preserve collection and native exception behavior; no gameplay or generation repair is introduced.

## 1.1.0

- Add observational generation, RNG, marker, spawner and culling lifecycle hooks.
- Add world/node/terminal/plug checks, project expectations and bounded structured reports.
- Add background report export, historical log import, run comparison and existing LGTuner input snapshots.
- Add opt-in actual damage sampling and effective weapon fields.
- Cancel owned world scans on cleanup; expose unavailable and unverified states.
- No speculative R7D2 or CullingCluster game fix; live reproduction and advanced room/spawner control remain pending.

## 1.0.0

- Extract existing performance diagnostics from Infini Tweaks into a separately loadable authoring plugin.
- Own configuration, process/frame/GC/native sampling, bounded profiler captures, cooperative scene inventory, logs and offline analysis.
- Optional QOL telemetry uses removable subscriptions; no hard dependency on Infini Tweaks.
- Preserve unavailable timing states and report limits.
- Document generation diagnosis and controlled reproduction work as pending; no speculative generation or lifecycle patches.
