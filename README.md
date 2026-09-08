# Infini Tweaks

A configurable GTFO quality-of-life collection in one DLL. Every added gameplay module defaults to disabled or vanilla strength; the original flashlight improvements remain enabled by default.

Infini Tweaks does not use MTFO and never controls the flashlight's on/off state.

Version 2.1.1 fixes reconnect-safe, host-independent accuracy sharing and exact pickup targeting in the casual-co-op modules below. It has passed offline regression and native-interface checks; live Unity rendering, booster transactions and multiplayer/checkpoint behavior still require in-game acceptance. Do not treat a successful build as that acceptance.

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

### Casual-co-op modules (2.1.0)

These modules are independent and default **off**. Enable the ones you want in the generated config; each setting includes its effect, limits and interaction with other settings. Updating the DLL does not replace your existing flashlight/combat/stamina values.

| Section / setting | Behavior |
| --- | --- |
| Resources / `EnableItemDrop` | Press `DropItemKey` (G by default) with a resource pack or consumable equipped while looking toward reachable solid ground. Keeps the same item and amount. Weapons/tools cannot be dropped; objective carry items keep native controls. Rebind G if another action uses it. |
| Resources / `EnableContainerDeposit` | Aim at an empty matching slot in an **open** locker/box and hold the game's Use key for 0.35 seconds. A small cross previews the destination. Supports resource packs and consumables. |
| Resources / `EnableResourceStacking` | **Host-only:** picking up the same resource type merges amounts. Two 2-use ammo packs become one 4-use pack. Excess remains available; different types and already-full packs swap normally. Does not merge consumables. |
| Resources / `MaxResourcePackUses` | Default 5; configurable 1–5. Fractional native amounts are retained. This version does not extend the game's native synchronization capacity above 5 uses. |
| Item Markers / `EnablePersistentItemMarkers` | Remembers the **actual targeted pickup**, not nearby objects. Native terminal item IDs and player target objects identify items exactly. Teammates need 2.1.1 with markers enabled to share player pickup pings. Coordinate-only pings from other clients remain vanilla; no guessing which item they meant. No timer expiry, enemy, unopened-container or global item reveal. |
| Item Markers / visibility | Default range 30 m, same dimension only. Leaving range hides the pin without forgetting it. ADS hides these pins by default. Carried items are hidden, placed items can reappear, consumed/despawned items are removed. Checkpoint restore/new expedition clears remembered pins. Native objective/player markers are unchanged. |
| Item Markers / appearance and clearing | `MarkerOpacity` 0.65; `MarkerScale` 0.4 controls the native icon. F8 clears all; Shift+F8 clears the visible remembered pickup nearest the crosshair. Keys are configurable. |
| Resource HUD / `EnableResourceHUD` | Compact teammate health, infection, ammo reserves, tool reserves and carried resource pack uses. Normal text is 0.7× and not bold. While **you** hold a pack, only its relevant resource becomes 1×/optionally bold. Switching to a weapon removes emphasis. Text sizes and emphasis bolding are configurable. |
| Boosters / `PerfectBoosterRolls` | Uses the current matching template's best roll for existing effects, including one choice per random-effect slot. Does not add effects or remove conditions/negative effects. Unmatched historical boosters stay unchanged with a warning. |
| Boosters / `PreventBoosterConsumption` | Prevents equipped-booster consumption locally and in session-consumption submission. Independent of perfect rolls. Deliberate discarding remains available; previously spent boosters are not restored. |
| Boosters / `EnableBoosterFarmer` | Multiplies **earned** artifact currency by `BoosterRewardMultiplier` (default 5, range 1–100). Zero earned remains zero. Floors the result and caps the multiplied reward at 100000 without lowering an already-larger native reward. Changes persistent progression. |
| Statistics / `EnableStats` | F7 toggles the compact table; Shift+F7 toggles weapon-category details. Optional success/failure display, roster order, no ranking or chat spam. Both key and team/estimated/end-screen visibility are configurable. |

#### Statistics: what the numbers mean

Accuracy is enemy hits per firearm pellet/ray, not per trigger pull. A piercing pellet counts once for accuracy; damage can cover every enemy it hits. Owner-reported accuracy replaces host-observed estimates. `~` means observed/estimated; `—` means unavailable or no shots, not zero accuracy.

Damage uses actual enemy health loss on the **participating host**, excluding overkill and healing. Without that host, complete damage is unavailable and shown as `—`, but participating clients still share exact owner accuracy directly. A participating host can track unmodded teammates' native damage and attempt to observe their accuracy; missing observations are not fabricated. All peers who want exact owner accuracy need 2.1.1 with stats enabled. The 2.1.0 protocol and standalone StatDisplay are not supported.

Details use the native weapon-category attribution supplied by the hit, not a guess from whichever weapon is equipped later. Unattributed damage has its own bucket. Custom/EWC projectile accuracy is **not supported** in this release; this is not a full replacement for StatDisplay's EWC support. Counts cover the current tracking attempt and reset on checkpoint restore/new expedition. Reconnecting starts that owner's accuracy from zero across all weapon categories; existing host damage is retained. Host migration resets host damage/estimates, not exact owner accuracy. Receiver-issued session challenges reject stale data from prior connections and checkpoints without relying on synchronized clocks. Late participation does not reconstruct earlier shots. The table is optional so normal combat remains unobstructed.

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
3. Remove standalone mods for any Infini Tweaks feature you enable to prevent overlapping patches.

Do not combine enabled modules with Flashlight Overhaul, BrighterLights, NoSway, LightsAdjustment, APEX Prisoners recoil, HeroicHeart, TeammatesIgnoreBullets, AimPunchAdjustment, BetterMaps, R6_HELGun, or MakeSniperGreatAgain.

For the new modules, disable overlapping DropItemPlus/DropItem, ResourceStack/Stacks, ItemMarker/ResourceHelper, ColorGradingHUDInfoPlus, Booster Tweaker/booster-consumption or farmer features, and StatDisplay/accuracy overlays as appropriate. This DLL does not uninstall them. Keep any standalone functionality you still need disabled only where it overlaps; do not remove TheArchive/Hikaria Core simply because an individual module was replaced. Enable resource stacking on the host; use matching installs for the multiplayer acceptance checks.

When upgrading from Infini Flashlight, remove its DLL to avoid applying beam adjustments twice, then copy your beam values into the new config's `Flashlight` section. The old config is not loaded automatically. Hot unloading is not supported; restart the game to remove applied data/prefab changes.

## Building

.NET 6 targeting files are required. Point the build at a modded GTFO profile's `BepInEx` directory:

```powershell
$env:GTFO_BEPINEX_PATH = "$env:APPDATA\r2modmanPlus-local\GTFO\profiles\YourProfile\BepInEx"
dotnet build -c Release
```

Offline regression tests link the production player patches and datablock code against managed game doubles:

```powershell
dotnet run --project tests/Regression.csproj -c Release
```

They check low-stamina clamping, local/remote/bot scope, friendly-fire direction, repeated-hit aim punch, disabled presets, shared recoil isolation, all covered historical numeric values, recoil scaling, flashlight clamps, and duplicate initialization. They do not run Unity, IL2CPP detours, rendering, or multiplayer replication.

The casual-module checks additionally cover fractional resource conservation/overflow, distance and aim visibility, perfect-roll slot matching, bounded earned rewards, effective damage, and cumulative/ordered statistics. The 2.1.1 suite also exercises the production protocol across independent peers: no stats-enabled host, sequence-1 reconnects, sender/receiver resets, delayed confirmations/snapshots, source authority and host migration. Run native patch contract validation after building:

```powershell
dotnet run --project tests/NativeContracts/NativeContracts.csproj -c Release -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH -- $env:GTFO_BEPINEX_PATH "$PWD/bin/Release/InfiniTweaks.dll"
```

This reads assembly metadata without executing game code. Live acceptance remains listed in `REVIEW.md`.

## Sources

Weapon fixtures: [OriginalDataBlocks](https://github.com/UntiIted/OriginalDataBlocks), original R6 commit `3376fbd` and current R8 commit `6244c01`. The test fixture only removes derived Vector metadata such as `normalized` and `magnitude`.

Map behavior is based on [GTFO-Modding/BetterMaps](https://github.com/GTFO-Modding/BetterMaps). HeroicHeart, TeammatesIgnoreBullets, AimPunchAdjustment, and APEX Prisoners informed the feature selection; their standalone DLLs are not bundled.

New feature references: [DropItemPlus](https://thunderstore.io/c/gtfo/p/Hikaria/DropItemPlus/), [ResourceStack](https://thunderstore.io/c/gtfo/p/Hikaria/ResourceStack/), [ItemMarker](https://thunderstore.io/c/gtfo/p/Hikaria/ItemMarker/), [ResourceHelper](https://github.com/GTFO-Modding/ResourceHelper), [Booster Tweaker](https://thunderstore.io/c/gtfo/p/Hikaria/Booster_Tweaker/), [ColorGradingHUDInfoPlus](https://thunderstore.io/c/gtfo/p/Hikaria/ColorGradingHUDInfoPlus/) and [StatDisplay](https://github.com/Dinorush/StatDisplay). The integrations are newly written around native interfaces; those DLLs are not embedded.
