# Changelog

## 1.0.0

- First release candidate of `NAinfini-ForgeMap`: the game-independent provider definition (`forge.module.gtfo.map`) plus the native plugin that registers it with the player entity resolver and reacts to host world changes. No capability, binding or observer is published, and `ForgeMap.dll` ships for that provider definition.
- The `gtfo.map_object` instance lookup also answers for the object a bullet was resolved against: a hit collider climbs to the door or terminal it belongs to, and that object is then classified and addressed exactly as one handed over directly. A hit object with no map object above it resolves to nothing.
- Players can now be healed: this package binds the canonical `forge.action.combat.heal` to its own `gtfo.player.heal` handler, which commits through the game's own `Dam_SyncedDamageBase.SendSetHealth` entry on the player life the identity module tracks. A player snapshot advertises `health.heal` only while that receiver is readable and its agent is alive; a dead player, a foreign receiver, a client and an unsupported reference are refused by name, and each row's amount is the receiver's own readback.
- Release candidate only: not published, not installed as a package, offline validation only.
