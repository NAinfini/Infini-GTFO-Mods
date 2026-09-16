# Changelog

## 0.2.0

- Equipment identity covers both shapes this provider owns: a backpack life (`gtfo.equipment:<world>.<life>`) and a deployed world instance (`gtfo.equipment:<world>.<life>.<instance>`), with the slot, wield and deploy state read back from the game.
- Weapon observation adds one shot fact per fire body with hit candidates on that shot's scope, and deployable placement lifetimes, next to the equipped/unequipped facts.
- A hit candidate names the equipment life that fired the shot as a required `equipment` port: the same reference the shot's own fact carries, taken from the open firing window rather than from the object that was hit, so a `gear-block` mount can claim its own weapon's hit.
- A hit candidate's optional `target` also names a map object: after the enemy limb and player limb, the collider a shot was resolved against is handed to the `gtfo.map_object` instance lookup by kind name, and that domain climbs from the hit object to the door or terminal it belongs to. Weapon reads no door or terminal type and its pinned game-read set is unchanged; a hit object with no map object above it keeps the port absent.
- The `gear-block` attachment kind matches an authored equipment block against the game's own offline gear record text.
- Authored gear-part poses: a package writes one `forge/gear-parts/<blockId>.json` per equipment block, read once at plugin start, and a `GearPartHolder.OnAllPartsSpawned` postfix writes the absolute local position, euler angles, scale and visibility of the named parts and their child paths. A child entry may nest further children, and the whole tree is applied: each node is resolved by its full path from the part. One block is defined by one file — when two or more files claim the same block the block is not loaded at all, rather than decided by scan order — and a child path is capped at eight segments counted on the finished path, so nesting cannot pass the cap one level at a time. This is the package's one presentation write; it publishes no fact and sends nothing over the network.
- `ForgeWeapon.csproj` and `ForgeWeapon.Native.csproj` both carry the package version 0.2.0, and the plugin declares versioned hard dependencies on `NAinfini.ForgeRuntime` 1.2.0 and `NAinfini.ForgeMap` 0.1.0.
- Release candidate only: not published, not installed as a package, offline validation only.

## 0.1.0

- First release candidate of `NAinfini-ForgeWeapon`: the game-independent provider definition (`forge.module.gtfo.weapon`, equipped and unequipped observe bindings) plus the native plugin that registers it and observes equipment instances against Forge Map player owners. Both `ForgeWeapon.dll` and `ForgeWeapon.Native.dll` ship in the archive.
- Release candidate only: not published, not installed as a package, offline validation only.
