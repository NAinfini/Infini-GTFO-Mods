# Changelog

1.0.0–1.1.2 是随本包发布的诊断版历史，已原文迁到 [ForgeDevelopment/CHANGELOG.md](../ForgeDevelopment/CHANGELOG.md)（标明来源为 ForgeRuntime 旧诊断版）。本文件从 1.2.0 起只记宿主的内容，即 GTFO 宿主与公共 SDK。

## 1.2.0

- Split the public SDK into its own `ForgeRuntime.Framework.dll`; the host keeps the simulated clock, world and session bridge, startup configuration, plan discovery and the log writer, and the managed domain projects reference only the SDK.
- Move every diagnostic, report and performance collector out of the host: `Plugin.cs` keeps only configuration, `GameRuntimeBridge`, this assembly's `harmony.PatchAll` and `FrameworkMonitor`; diagnostics live in the separate `NAinfini.ForgeDevelopment` plugin, which starts only when `Runtime.Mode = Authoring`. `Play` and `Authoring` now start the same host.
- Default `Runtime.Mode` to `Play` (D-011) and add `[Logging] Level` (default `error`); both parse the raw text and reject malformed or empty values before native initialization. Startup failure cleans up host stop → unpatch → component teardown and rethrows the original exception.
- Replace the single configured plan path with I-PACK D-009 discovery: ordinal-sorted `*.plan.json` files under `BepInEx/plugins/*/forge/plans`, a 4 MiB single-file limit on top of the 256-file/64 MiB merged budget with tail-first eviction, `plan-conflict` messages naming every conflicting path, one scan per process, and the registered capability list written to `BepInEx/ForgeRuntime/capabilities.json`.
- Move the SDK and the plan format to API 2.0.0 and plan schemaVersion 2 with the website's Forge Standard v0.2: registration validation rewritten against `validateCapabilityGraph`, variadic and portGroup expansion, `layout.promoted` parameters, and enum wire values as set-member indices. v1 plans and v1 registration shapes are rejected outright; no compatibility path.
- Add the execution log contract (D-007 phase B): SDK sink and per-provider levels with one-time elevation, and a host `forge.log.v1` JSONL writer with lazy startup, per-tick throttling plus `log.dropped`, a 64 MiB file ceiling and the newest 10 files retained. No record site calls the contract yet, so the player layer writes no records.
- Accept literal and one-to-many step inputs (J-003), and stop treating a partial result with a confirmed commit as a reason to end the entrypoint (r11).
- Publish the canonical combat definitions in the SDK and expose `EntityInstanceResolvers`, `ResolveEntityInstance` and `IsEntityCurrent` so a domain can resolve its own native instances into shared entity references.
- Name the Thunderstore package `ForgeRuntime` (D-013) and describe the host role only; domain bindings ship in their own base packages.
- Offline validation only: the host and `Forge.Architecture.sln` build with 0 warnings and 0 errors. 1.2.0 is not released, not installed and has no game verification.
