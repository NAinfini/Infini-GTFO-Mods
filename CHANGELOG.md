# Changelog

## 2.1.1

- Fixed accuracy freezing after reconnect: each tracking session negotiates a receiver-issued challenge; old snapshots and confirmations cannot replace the new stream.
- Removed the participating-host requirement for exact owner accuracy sharing. Host damage and estimates retain separate authority and reset without erasing exact peer accuracy.
- Replaced nearest-position pickup guessing with the native ping target object, terminal item ID and exact shared pickup SyncID. Coordinate-only pings remain vanilla; closed-container contents are not remembered.
- Added multi-peer protocol regressions for reconnects, checkpoint resets, delayed packets, unmodded hosts and host migration. Updated setting descriptions and protocol requirements. Existing configuration values are unchanged.

## 2.1.0

- Added independently configurable native resource/consumable dropping and open-container deposit with a placement cue.
- Added host-side same-type resource merging with a 1–5 use cap, fractional-amount conservation and retained overflow.
- Added remembered player/terminal pickup pings with range/dimension filtering, ADS hiding, native icon styling and clear controls.
- Added compact teammate resource information that only enlarges/bolds the relevant resource while holding its pack.
- Added current-template perfect booster rolls, independent equipped-booster non-consumption, and configurable earned artifact-currency multiplication.
- Added optional team accuracy/damage tables, weapon-category details, current-attempt lifecycle and explicit estimated/unknown values.
- Added ordered cumulative stats snapshots, random-slot booster matching and regression/native-interface checks.
- Preserved previous settings and defaults. New modules default off; live multiplayer/rendering/booster acceptance is pending, not implied by the build.

## 2.0.0

- Renamed the package to Infini Tweaks and kept it as one DLL with no MTFO dependency.
- Added numeric recoil, aim-punch, and stamina-cost multipliers.
- Added toggles for melee charge recovery, melee stamina cost, jump stamina cost, and teammate bullet damage.
- Added optional Better Maps rendering, accessible-area filtering, orientation correction, and icon priority.
- Added selectable official Original R6 and current R8 HEL Gun and Sniper presets.
- Corrected stamina scaling before clamping and limited it to the local human player.
- Added isolated full recoil definitions for weapon presets and scaled procedural firing recoil with the global multiplier.
- Scoped teammate protection to local outgoing bullets and made aim-punch scaling per hit.
- Corrected map bounds writeback and guaranteed temporary render-resource cleanup.
- Added historical-data and player-patch regression coverage.

## 1.0.0

- Added configurable positive or negative flashlight range adjustment.
- Added configurable positive or negative flashlight cone-angle adjustment.
- Added optional local first-person flashlight sway removal.
- Preserved GTFO's native flashlight toggle, equipment, detection, and network state handling.
