# Implementation review

## 2.1.1 corrections

Fixed the three issues found in the post-install review:

- A reconnecting owner's new sequence 1 was rejected behind their old sequence 100. Receiver-issued session challenges now separate tracking incarnations, reset all that owner's weapon accuracy, retain existing host damage and reject delayed old packets/confirmations. Production protocol state is exercised directly by multi-peer regression tests.
- Exact owner accuracy no longer waits for a stats-enabled host epoch. Two participating clients exchange it directly; only damage and estimates require host authority. Host migration/reset does not erase exact owner accuracy.
- Persistent pins no longer select the closest item within 1.5 metres. Local pings use `PlayerAgent.TriggerMarkerPing`'s `targetGameObject`; terminal pings use `terminalItemId`; participating peers share the exact native pickup `SyncID`. Closed-container pickups are rejected. Coordinate-only pings from nonparticipating clients retain native behavior, without a guessed persistent pin.

Verification: **1130 regression assertions**, Release build with **zero warnings/errors**, and **44 native hook contracts** against Temp's installed game interfaces. Target-object/terminal-ID hooks are signature-checked, not executed in Unity. No live game session was run for this release.

Focused live acceptance: two clients with an unmodded/stats-disabled host; client disconnect/rejoin after 100+ snapshots; checkpoint reset; host migration; ping one of two adjacent items, a wall near a pickup, and a closed locker; compare local, participating-teammate and terminal pings. Verify ADS/distance filtering and native generic pings remain unchanged. The broader rendering, resource, booster and multiplayer checks below remain pending.

## 2.1.0 acceptance status

Implemented resource/consumable dropping, open-container deposit, host resource stacking, remembered pickup markers, contextual teammate resource text, booster options and team statistics. No MTFO, TheArchive integration framework or standalone replacement DLLs were added.

Review corrections: fixed tiny negative overflow caused by float rounding; matched one booster effect per random slot instead of entire groups; added Harmony class discovery attributes; preserved independent sequence numbers/source precedence for accuracy and damage; refreshed terminal course nodes and native marker dimensions after placement; moved booster updates to the native inventory-changed event instead of detouring its by-ref SDK payload. Used native world HUD markers, with only the optional table/placement cue rendered by a small overlay.

Offline results: **1088 regression assertions**, Release build with zero warnings/errors, and **44 native hook contracts**. Contracts inspect discoverability, target overloads and injected parameter types; they do not execute IL2CPP. The regression suite links the production rules, not a duplicate algorithm.

Live checks still required before replacing standalone mods in a gameplay profile:

- Host and client: drop/pick up each resource and consumable, place in open boxes/lockers, retain uses, reject occupied slots. Two simultaneous placements must not occupy one slot. Closed/full containers must remain unchanged.
- Host stacking: 2+2→4, 4+3→5+2, mixed types, full-pack swap, fractional uses; verify both players see identical counts and checkpoint restores do not duplicate items.
- Ping locally/from a teammate/from a terminal; move beyond 30 m and back, change dimension, ADS/release, pick up/drop/consume/merge, clear one/all, reload checkpoint. No stale or duplicated icons; merge pin transfer across peers needs explicit checking.
- Teammate text at 1080p/1440p/ultrawide: no bold/enlargement while holding guns; emphasize only the correct resource on pack switch. Verify native update/visibility behavior, dead players, bots and deployed-tool reserve semantics.
- Verify perfect-roll effects in inventory **and actual gameplay**, random-slot combinations, unchanged conditions, unknown-template warning. Verify equipped booster counts across successful/failed session and restart; intentional discard still works. Verify artifact reward multiplier without changing zero rewards.
- Two modded peers plus an unmodded peer/bot: misses, pellets, piercing, armored limbs, overkill, sentry/melee damage, delayed packets, late join, final screen and checkpoint/host resets. Confirm missing damage remains `—` without a participating host. EWC projectile accuracy is outside this release's support.

New gameplay modules default off in the distributable. Per-profile deployment can enable them and remove overlapping standalone modules, but installation and offline checks do not replace the live acceptance checks above.

## 2.0.0 review

The initial 2.0.0 draft compiled but was not ready for gameplay validation.

| Finding | Correction |
| --- | --- |
| Stamina used the clamped loss to calculate a refund, producing incorrect costs near zero. A thread-global nesting counter could also survive an exception or mix players. | Scale positive `ActionCost` inputs once in `UseStamina`; keep negative recovery intact. Remove the refund and nesting state. |
| Zero cost did not restore already-depleted stamina or address combat caps. | Restore full local stamina for zero-cost mode, including after native stamina updates. |
| Stamina and charge-recovery hooks had no local-player scope. | Restrict them to the locally owned human, excluding bots and remote players. |
| Friendly-fire documentation described incoming immunity; the patch suppressed every call. | Explicitly suppress local human shots into other players and correct the documented outgoing-only semantics. |
| Aim punch retained a permanent dictionary keyed by camera instance IDs. | Multiply each hit's punch argument, eliminating cache lifetime and repeated-application issues. |
| Sniper overwrote shared recoil block 9; HEL Gun did not restore its recoil definition. | Allocate private, fully populated recoil definitions for both selected weapon presets. |
| Recoil multiplier only changed aiming power while procedural weapon impulses and firing concussion remained. | Scale these amplitudes too, while retaining direction, recovery, spread, and baked animations. |
| Map bounds were expanded on a temporary struct value. | Expand a local Bounds value and assign it back. |
| Map render textures and mesh/command resources leaked on exceptions; render textures were released without destroying their native objects. | Use pooled temporary render textures and `finally` cleanup, and release generated mesh/renderers on success and failure. |
| Float ranges alone allowed non-finite input; hot unload left modified data behind. | Restore defaults for non-finite values and explicitly reject hot unload. |

Verification: Release build against the Temp profile's real interop assemblies, plus 555 offline assertions exercising linked production patch code and R6/R8 golden data. These assertions use managed doubles and cannot prove native detour execution, rendered map correctness, or multiplayer synchronization. Those remain in-game acceptance checks.

In-game acceptance: verify 0/0.5/1 recoil and stamina, melee/jump toggles and charge recovery, repeat damage after camera re-enable, outgoing friendly fire with both host/client roles, both weapon presets with displayed ammo and firing cadence, map rendering across dimensions and checkpoint reload, and normal flashlight toggling/equipment switching.
