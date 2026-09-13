# Infini Tweaks

本目录是独立的 Quality of Life 模组，功能不属于 Forge 重构范围。以下构建和测试命令在本目录执行；仓库入口见 [README](../README.md)。

A configurable GTFO quality-of-life collection in one DLL. Gameplay changes default to disabled or vanilla strength; flashlight improvements and compact statistics are enabled by default. Performance and authoring diagnostics are not part of this Quality of Life mod.

Infini Tweaks does not use MTFO and never controls the flashlight's on/off state.

2.5.4 retains ResourceHelper-style resource marker visibility with our 58 embedded icons: 40m supply/10m consumable defaults, a 60m cap, temporary PING extension, near/focused names, persistent distance, a 0.1s fade-in and 50% ADS opacity. All owned markers use custom artwork, including distinct fixed HSU and HSU activator icons and a new flashbang-shaped C-Foam grenade. Unknown items use a question-mark icon. Visible icons are fully opaque independently of text fading. Security doors are no longer marked. Custom icons retain 0.72 scale with uniformly centered 224px visible-content bounds and ordinary colored keycards have dedicated artwork. Held-pack rows use actual equipment names, bright reserve colors and individually centered alignment beneath the player name. See [current HUD behavior](HUD-BEHAVIOR.md) and [icon coverage](MARKER-COVERAGE.md). Offline checks do not certify Unity rendering or multiplayer acceptance.

Damage statistics now use native health-loss events observed on each client, without a modded-host requirement. Accuracy sync uses v6 and requires matching peers. Damage before joining cannot be recovered.

Performance and authoring diagnostics live in the separate optional Forge Development plugin, which ordinary play does not need. Archive owns chat and weapon descriptions; EWC is excluded.

Marker configuration uses one overall switch, one distance per category, and a clear-marker key. Retired per-item sections and appearance overrides are removed on startup; existing category distances are retained.

## Features and configuration

The config is generated at `BepInEx/config/NAinfini.InfiniTweaks.cfg` after the first launch.

```ini
[Flashlight]
RangeAdjustmentMeters = 10
AngleAdjustmentDegrees = 35
NoSway = true

[Combat]
# 1 = original, 0.5 = half, 0 = no recoil.
RecoilMultiplier = 1
# Accepted range: 0.1 to 1.
AimPunchMultiplier = 1
TeammatesIgnoreBullets = false

[Stamina]
# 1 = original, 0.5 = half costs, 0 = infinite stamina.
StaminaCostMultiplier = 1
EnableChargeRecovery = false
RemoveMeleeCost = false
RemoveJumpCost = false

[Map]
EnableBetterMaps = false
BlurScale = 0.15
OutlineScale = 0.05

[Weapon Presets]
# Disabled, OriginalR6, or R8Current
HelGunVersion = Disabled
SniperVersion = Disabled

```

Restart the game after editing the config.

### Authoring diagnostics

Performance collection belongs to the optional [Forge Development](../ForgeDevelopment/README.md) plugin. Infini Tweaks creates no diagnostics monitor, collector, files or logging thread. Its public `Telemetry` events only let Forge Development time Infini's own hot paths; with no subscriber the measurement scopes do no sampling. The marker and HUD performance comparison is kept in [PERFORMANCE-REVIEW.md](../ForgeDevelopment/PERFORMANCE-REVIEW.md).

### Casual-co-op modules (2.2.4)

These modules are independent. Gameplay-changing modules default **off**; compact statistics default **on**. Each setting includes its effect, limits and interaction with other settings. Updating the DLL does not replace your existing flashlight/combat/stamina values.

| Section / setting | Behavior |
| --- | --- |
| Resources / `EnableItemDrop` | Press `DropItemKey` (G by default) with a resource pack or consumable equipped while looking toward reachable solid ground. Keeps the same item and amount. Weapons/tools cannot be dropped; objective carry items keep native controls. Rebind G if another action uses it. |
| Resources / `EnableContainerDeposit` | Aim near an empty matching slot in an **open** locker/box and hold the game's Use key for 0.35 seconds. Forgiving slot snapping, a translucent pickup-mesh preview and the native localized interaction prompt show the destination. Supports resource packs and consumables. |
| Item Markers / `EnablePersistentItemMarkers` | Remembers pickups and named terminal objects discovered within 4 m and line of sight of a living teammate, native terminal proximity, or explicit targeted ping/terminal QUERY or PING. Matching clients share exact pickup IDs. No unopened-container contents, enemy or global reveal. |
| Item Markers / visibility | Fourteen `Markers - <category>` sections each expose only `DistanceMeters`. Resources default to 40 m, consumables 20 m, carry/objective items 45 m, terminals/HSUs/door locks/controllers 40 m, generators/disinfection stations 30 m and other named items 20 m. Lockers/boxes themselves are excluded; their discovered contents remain eligible. Zero disables a category. Explicit targeted pings/QUERY extend range to at least 60 m; repeated pings update the same pin. Same dimension only; ADS hides these pins by default. Resource pickups disappear. Native carry objectives (cryo/cells) follow the remote carrier with their name, reanchor on drop and hide when solved; your own carried objective has no extra custom icon. Checkpoint/new expedition clears pins. |
| Item Markers / appearance and clearing | Native category icons, localized native names, resource uses/consumable counts and native distance text. Built-in opacity 0.65 and icon scale 0.4. No per-item settings or switches; category colors/icons and native counts are automatic. F8 clears all; Shift+F8 clears the visible remembered item nearest the crosshair. Device markers hide completed/inactive interactions; locked doors show their missing requirement. No undiscovered global reveal. |
| Resource HUD / `EnableResourceHUD` | Disabled: native extra information remains untouched, including when switching to a pack. Enabled: no extra information while holding weapons; a held pack exposes matching teammate percentages (ammo = main/special, tool = tool, medical = health, disinfect = infection). Infinite-ammo equipment uses its native infinity flag; missing slots are omitted, and deployed sentries use their actual reserve. Default 1.2× size, optional bold, native pack-category icon, continuous empty-red/half-yellow/full-green colors (infection reversed). No generic teammate carried-inventory list. |
| Resource HUD / transparency | `AimOpacity` (0.15, set 1 to disable ADS fading) and `DynamicOpacity` (true) affect resource rows only. Names use independent `NameOpacity`; distance uses `ShowTeammateDistance` and `DistanceOpacity`. Rescue/downed-player markers and menus retain native opacity. Turning Resource HUD off restores its owned native visibility/text and opacity. |
| Boosters / `PerfectBoosterRolls` | Uses the best roll for an exact current or authored historical template match, including one choice per random-effect slot. Retains existing effects and conditions. Unmatched templates stay unchanged with a warning; inventory events log checked/changed counts. |
| Boosters / `PreventBoosterConsumption` | Prevents equipped-booster consumption locally and in session-consumption submission. Independent of perfect rolls. Deliberate discarding remains available; previously spent boosters are not restored. |
| Boosters / `EnableBoosterFarmer` | Multiplies **earned** artifact currency per quality by `BoosterRewardMultiplier` (default 100000, range 1–100000). Zero earned remains zero; no fixed rewards are granted to unearned qualities. Floors the result and caps the multiplied reward at 100000 without lowering an already-larger native reward. Actual booster counts follow native conversion and inventory/server limits. Changes persistent progression. |
| Statistics / `EnableStats` | Built into InfiniTweaks.dll. One compact native inventory-anchored row per player: Hit / Crit / DMG, with native player-color abbreviations. No F7, IMGUI window or separate StatDisplay DLL. `ShowTeamStats` selects team/self; `TextScale`, `OffsetX`, `OffsetY` adjust the native HUD. |

#### Statistics ownership

As of 2.2.7, compact statistics, resource HUD, item markers and boosters are all internal modules of the same Infini Tweaks plugin. This supersedes the undeployed 2.2.6 plan to use external StatDisplay. Do not install another statistics, resource-HUD, marker or booster provider for these same selected features. The original large statistics panel is still removed. Hikaria Core remains responsible for ResourceStack as explicitly selected; Archive continues to supply its existing unrelated features.

`[Statistics]` defaults: `EnableStats=true`, `ShowTeamStats=true`, `ShowEndScreenStats=true`, `FullPlayerNames=false`, `TextScale=1`, `OffsetX=100`, `OffsetY=0`. Native success gear rows include per-slot results and evaluation retains original text plus a total. Failure uses four short native-font columns without hiding artifact information. Build/new-expedition and leaving the lobby clear the attempt; repeated enter-level callbacks within the same attempt retain counters. Checkpoint lifecycle and result layout need live acceptance. Result pages continue receiving late peer snapshots. No large-panel toggle or remote-human visual-ray estimate remains.

HUD rows use Dinorush's compact number format: `RED: 75%/40% (1234)`. Grey = hit rate (hit/fired), yellow = weakspot rate (weakspot/hit), parentheses = effective enemy damage. The user selected this weakspot definition, not anatomical headshots. Each pellet hits/crits at most once; Group counts the trigger group once, Full counts every piercing hit. Missing shots/hits or missing peer data displays a dash. Exact remote-human accuracy requires matching **Infini Tweaks 2.4.x**, and authoritative effective damage requires an Infini host; bots are measured by the host. The recipient/session-bound v5 protocol is incompatible with old Infini and original StatDisplay. Host changes clear host-owned damage and bot counters rather than merging incompatible totals; human-owned accuracy is retained.

`HudFormat` and `ResultFormat` accept case-insensitive `{Hit/Fired}`, `{Crit/Hit}`, `{FullHit/Hit}`, `{Damage}`, `{DamageCrit}`, Main/Primary, Special/Secondary, Tool/Class, Melee/Other/All selectors, combined slots, Shot/Group/Full dimensions and `:0`, `:0.0`, `:0.00` precision. They are a documented subset, not the original parser or all presets. Per-weapon means inventory-slot totals; swapping gear does not create another weapon-instance history. Damage uses native gear-category metadata; unattributed events stay Other. **EWC custom projectiles/DOT/explosions are not supported by an adapter in this candidate; EWC accuracy/attribution and full upstream parity are not claimed.** No upstream StatDisplay DLL or source files are bundled.

Chat/weapon ownership: keep Archive's full-GUID `ChatTweaks` and `WeaponStats` enabled. Hikaria Core is not their provider. Infini does not include BetterTextChat multiline/received-history editing or weapon-description paging/custom breakpoints; these omissions are intentional user choices. Core continues to own ResourceStack and PlayerPingHelper. Infini's interaction continuity overlaps Archive `InteractionFix`; disable that one overlapping Archive feature when deliberately deploying and testing this candidate, not ChatTweaks/WeaponStats. Profile configuration has not been changed automatically.

Carry-objective deduplication suppresses only that object's native `PlaceNavMarkerOnGO` while our replacement is visible. Hiding/dismissing/disabling the replacement restores the latest native visibility request; player, enemy, rescue and unrelated ping markers are not globally suppressed. Computer discovery uses the actual terminal interface, interaction-screen anchor and native synchronized interaction, including a teammate using the terminal. Device discovery tolerates hitting the device's own collider, not an unrelated wall.

Dynamic devices reuse native Setup callbacks, not a second polling scanner. Reactor OnBuildDone registers its computer in the native zone list once and in the existing marker registry; missing terminal nodes use the reactor node or actual containing area. Existing native nodes are preserved. Repeated unchanged registration does not rebuild a marker; changed anchors rebind already-known pins, changed IDs retire old QUERY mappings, and old-object destruction cannot erase a replacement's mapping. New registration never means automatic discovery; normal-door/container exclusions and discovered-only visibility remain.

Consumables are included by their native inventory slot, not a hardcoded name list. They use the native consumable icon, name/count/distance and `[Markers - Consumable]` range/color. In 2.2.9, native custom-data updates refresh their remaining count and remove exhausted finite-use stacks. Picking one up removes its ground pin; placing it back makes it discoverable again. No container shell marker or unopened-content reveal is needed for this.

Flashlight ranges are clamped to 0.1–100 metres and cone angles to Unity's valid 1–179 degree range. Positive or negative adjustments are supported.

The recoil multiplier scales aiming recoil, procedural weapon position/rotation impulses, and firing concussion after the selected weapon preset is applied. It does not change accuracy, spread, recoil recovery, or baked firing animations. `StaminaCostMultiplier = 0` keeps local stamina full, including during combat; nonzero values scale action costs before the game's stamina clamp. Recovery strength and the native BPM display are retained. The melee and jump toggles remove only those action costs (melee pushes retain their normal cost).

Stamina changes apply only to the local human player. `TeammatesIgnoreBullets` prevents **your bullets** from hurting teammates, including bots. It does not protect you from teammates' bullets, change enemy damage, or make bullets penetrate teammates. Each teammate must enable it to prevent their own outgoing friendly fire.

Better Maps removes inaccessible navmesh from the map, reduces blur, corrects reversed map icons, and draws resource lockers, boxes, terminals, disinfection stations, generators, and bulkhead controls above minor icons.

The HEL Gun and Sniper presets restore the official original R6 or current R8 combat stats, including full recoil definitions. Models, localization, animations, and sentry settings remain those of the running game. Private recoil definitions keep presets from affecting other weapons that share the native recoil data. `Disabled` leaves weapon stats untouched; the independent global recoil multiplier still applies if it is below 1.

| Weapon | Preset | Damage | Magazine | Ammo cost | Shot delay | Charge |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| HEL Gun | OriginalR6 | 16.25 | 9 | 6.5 | 0.1 s | 0.1 s |
| HEL Gun | R8Current | 16.25 | 9 | 5.74 | 0.1 s | 0.2 s |
| Sniper | OriginalR6 | 40.01 | 3 | 15 | 0.5 s | 0 s |
| Sniper | R8Current | 40.01 | 2 | 17.5 | 0.8 s | 0 s |

## Installation

1. Install [BepInExPack for GTFO](https://thunderstore.io/c/gtfo/p/BepInEx/BepInExPack_GTFO/).
2. Put `InfiniTweaks.dll` in `BepInEx/plugins/InfiniTweaks/`.
3. Enable and test one replacement feature at a time before disabling its overlapping standalone implementation. A feature with the same name is not proof of full parity; see `FEATURE-COMPARISON.md`.

Do not combine enabled modules with Flashlight Overhaul, BrighterLights, NoSway, LightsAdjustment, APEX Prisoners recoil, HeroicHeart, TeammatesIgnoreBullets, AimPunchAdjustment, BetterMaps, R6_HELGun, or MakeSniperGreatAgain.

Do not run two implementations of drop/deposit, resource HUD or booster modification at once. Infini Tweaks does not uninstall other plugins. **Resource stacking is not implemented here as of 2.2.3; keep Hikaria Core's ResourceStack enabled on the host.** Its installed implementation covers matching resources and consumables, with native capacity and retained overflow. TheArchive/Hikaria Core remain separate owners of their fixes, ping helper and L4D-style pack controls. Standalone ItemMarker/ResourceHelper and booster tools still have features this DLL does not cover; use `FEATURE-COMPARISON.md` before replacing them.

When upgrading from Infini Flashlight, remove its DLL to avoid applying beam adjustments twice, then copy your beam values into the new config's `Flashlight` section. The old config is not loaded automatically. Hot unloading is not supported; restart the game to remove applied data/prefab changes.

## Building

.NET 6 targeting files are required. Point the build at a modded GTFO profile's `BepInEx` directory:

```powershell
$env:GTFO_BEPINEX_PATH = "$env:APPDATA\r2modmanPlus-local\GTFO\profiles\YourProfile\BepInEx"
dotnet build InfiniTweaks.csproj -c Release
```

Offline regression tests link the production player patches and datablock code against managed game doubles:

```powershell
dotnet run --project tests/Regression.csproj -c Release
```

They check low-stamina clamping, local/remote/bot scope, friendly-fire direction, repeated-hit aim punch, disabled presets, shared recoil isolation, all covered historical numeric values, recoil scaling, flashlight clamps, and duplicate initialization. They do not run Unity, IL2CPP detours, rendering, or multiplayer replication.

The casual-module checks cover distance/aim visibility, perfect-roll matching, held-pack HUD filtering, bounded rewards, effective damage and statistics. `dotnet run --project tests/ResourceSlots -c Release` executes production native-slot registration/callback/occupancy logic with managed doubles; it replaces the deleted polled-selector geometry tests. Removed stacking tests do not count toward this DLL because Core owns stacking. Statistics model tests exercise independent peer resets, snapshots, authority and host migration. Run native patch contract validation after building:

```powershell
dotnet run --project tests/NativeContracts/NativeContracts.csproj -c Release -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH -- $env:GTFO_BEPINEX_PATH "$PWD/bin/Release/InfiniTweaks.dll" "<ForgeDevelopment.Native.dll 路径，见 ../ForgeDevelopment/scripts/verify-diagnostics.py>" "<本游戏版本的 dump.cs 路径>"
```

This reads assembly metadata without executing game code. Live acceptance remains listed in `REVIEW.md`.

## Sources

Weapon fixtures: [OriginalDataBlocks](https://github.com/UntiIted/OriginalDataBlocks), original R6 commit `3376fbd` and current R8 commit `6244c01`. The test fixture only removes derived Vector metadata such as `normalized` and `magnitude`.

Map behavior is based on [GTFO-Modding/BetterMaps](https://github.com/GTFO-Modding/BetterMaps). HeroicHeart, TeammatesIgnoreBullets, AimPunchAdjustment, and APEX Prisoners informed the feature selection; their standalone DLLs are not bundled.

New feature references: [DropItemPlus](https://thunderstore.io/c/gtfo/p/Hikaria/DropItemPlus/), [ResourceStack](https://thunderstore.io/c/gtfo/p/Hikaria/ResourceStack/), [ItemMarker](https://thunderstore.io/c/gtfo/p/Hikaria/ItemMarker/), [ResourceHelper](https://github.com/GTFO-Modding/ResourceHelper), [Booster Tweaker](https://thunderstore.io/c/gtfo/p/Hikaria/Booster_Tweaker/), [ColorGradingHUDInfoPlus](https://thunderstore.io/c/gtfo/p/Hikaria/ColorGradingHUDInfoPlus/) and [StatDisplay](https://github.com/Dinorush/StatDisplay). The integrations are newly written around native interfaces; those DLLs are not embedded.
