using System;
using ForgeMap;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>What an open request may do about the door's own lock conditions. The two policies are the two
/// entries the door really offers: its own interaction entry, which still lets the lock, the interaction gate
/// and the glue decide, and its own force-open entry.</summary>
internal enum DoorBypassPolicy
{
    /// <summary>Ask through the door's own interaction entry. A door that reads locked, broken or not
    /// interactable is refused before anything is written instead of being asked anyway.</summary>
    Respect,
    /// <summary>Ask the door to force itself open, which is what the game's own force entry is for.</summary>
    Force
}

/// <summary>What a close request may do about a body standing in the doorway. The door's own interaction entry
/// carries no occupancy decision, so `Crush` is refused rather than quietly run as a normal close.</summary>
internal enum DoorOccupancyPolicy
{
    /// <summary>Do not displace anything: the door's own interaction entry is asked and nothing overrides it.</summary>
    Block,
    /// <summary>Displace whatever stands in the doorway. No native entry takes this decision.</summary>
    Crush
}

/// <summary>What a close request may do about the door's own resistance. The door's interaction entry has no
/// force form, so `Force` is refused rather than reported as a close.</summary>
internal enum DoorForcePolicy
{
    /// <summary>The door's own interaction entry decides whether it may close.</summary>
    Normal,
    /// <summary>Close whatever the door's own state says. No native entry takes this decision.</summary>
    Force
}

internal enum DoorAlarmMode
{
    /// <summary>Start the door's own alarm puzzle.</summary>
    Start,
    /// <summary>Stop the door's own alarm puzzle.</summary>
    Stop
}

/// <summary>
/// The door half of the native execution layer: one method per door action, each taking the instance the address
/// layer resolved and the address it was resolved through, and each writing through the door's or its lock
/// component's own member. Nothing here is a plan, a handle or a result frame — a later binding resolves the
/// instance and the parameters and calls in, and this layer answers with the one decision it made.
///
/// Two rules hold for every method. The instance is re-checked against its address before anything is written,
/// so a door that was replaced, moved to another zone or torn down under a stale reference is refused rather
/// than acted on. And a request whose arguments no native entry can carry is refused by name — the interaction
/// entry takes neither an occupancy policy nor a force flag, and the addressed door kind's damage entry carries
/// nothing at all — because running the plain form of such a request and reporting it as the requested one would
/// be a success this layer cannot back.
///
/// A native read that throws is not caught here: an action runs inside the same session guard as the readbacks,
/// which disables the provider once instead of letting this layer swallow a failure the rest of the session
/// would keep acting on.
///
/// Three of these entries have no binding in this batch, and their rows are deliberately unregistered — see
/// `DoorActionContract`'s header and `evidence/door-actions.json`: `LockWithKeyItem` needs a native
/// `GateKeyItem` instance only an item-resource provider can resolve and `LockWithoutKey` needs an interaction
/// text the catalog row has no port for, `Damage` has no addressable door kind to ask (the addressed
/// `security` entrance's own damage entry is the folded empty body), and no entry here unlocks. They stay
/// because they are the native half those rows will need and because they carry the evidence-backed refusals;
/// they are not reachable from any command yet.
/// </summary>
internal static class DoorActions
{
    /// <summary>The door, or a member the action reads, did not read at all.</summary>
    internal const string Unavailable = "door-unavailable";
    /// <summary>The instance no longer reads as the address it was resolved through.</summary>
    internal const string Stale = "door-stale";
    /// <summary>The door's own status says it was destroyed, so no door action can be asked of it.</summary>
    internal const string Destroyed = "door-destroyed";
    /// <summary>The door's own status says it is closed and cannot open.</summary>
    internal const string ClosedBroken = "door-closed-broken";
    internal const string AlreadyOpen = "door-already-open";
    internal const string AlreadyClosed = "door-already-closed";
    /// <summary>The door's own lock condition still holds: a key item is required, or it is locked with no key.</summary>
    internal const string Locked = "door-locked";
    /// <summary>The door's own interaction gate refuses interaction right now.</summary>
    internal const string InteractionNotAllowed = "door-interaction-not-allowed";
    /// <summary>An occupancy policy no native entry carries.</summary>
    internal const string CrushUnsupported = "door-crush-unsupported";
    /// <summary>A close force policy no native entry carries.</summary>
    internal const string ForceCloseUnsupported = "door-force-close-unsupported";
    /// <summary>The door holds no lock component, so it has no lock condition and no alarm puzzle to write.</summary>
    internal const string NoLockComponent = "door-no-lock-component";
    /// <summary>The door's own alarm puzzle did not read, so no alarm can be started or stopped on it.</summary>
    internal const string NoAlarmPuzzle = "door-no-alarm-puzzle";
    /// <summary>The door's own lock is not an alarm lock: its puzzle is a scan or a lock of another kind, and
    /// this action is the alarm row's.</summary>
    internal const string NotAnAlarm = "door-not-an-alarm";
    /// <summary>The door's own alarm puzzle already reads active, so nothing had to be started.</summary>
    internal const string AlarmAlreadyActive = "door-alarm-already-active";
    /// <summary>The door's own alarm puzzle already reads inactive, so nothing had to be stopped.</summary>
    internal const string AlarmNotActive = "door-alarm-not-active";
    internal const string Opened = "door-open-issued";
    internal const string Closed = "door-close-issued";
    /// <summary>The door's own alarm puzzle was activated.</summary>
    internal const string AlarmStarted = "door-alarm-started";
    /// <summary>The door's own alarm puzzle was deactivated.</summary>
    internal const string AlarmStopped = "door-alarm-stopped";
    internal const string LockedWithKey = "door-lock-key-issued";
    internal const string LockedWithoutKey = "door-lock-no-key-issued";
    internal const string KeyItemUnusable = "door-key-item-unusable";
    internal const string LockTextUnusable = "door-lock-text-unusable";
    /// <summary>The addressed door kind has no damage implementation to ask.</summary>
    internal const string NotDamageable = "door-not-damageable";
    internal const string DamageTypeUnknown = "door-damage-type-unknown";
    internal const string SourcePositionUnusable = "door-source-position-unusable";

    /// <summary>Asks a door to open. The door's own status decides first: an open or opening door is left alone
    /// — the native interaction entry toggles, so asking an open door to open would close it — and a destroyed
    /// or unopenable door is refused instead of asked. A `Respect` request then passes the door's own lock
    /// condition, its own can-not-open status and its own interaction gate; a `Force` request goes through the
    /// door's force entry, which is the one entry allowed to ignore those conditions.</summary>
    internal static MapActionOutcome Open(LG_SecurityDoor door, MapObjectReference address, DoorBypassPolicy policy)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (Read(door) is not { } state) return MapActionOutcome.Refused(Stale);
        if (state.Status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(Destroyed);
        if (IsOpen(state.Status)) return MapActionOutcome.AlreadyInState(AlreadyOpen);
        if (policy == DoorBypassPolicy.Force)
        {
            door.ForceOpenSecurityDoor();
            return MapActionOutcome.Issued(Opened);
        }
        // The status before the lock reading: a door the level built as unable to open is not asked even when a
        // key would satisfy it, and the lock reading is what the door itself publishes rather than a second one.
        if (state.Status == (int)eDoorStatus.Closed_BrokenCantOpen) return MapActionOutcome.Refused(ClosedBroken);
        if (state.Locked) return MapActionOutcome.Refused(Locked);
        if (!door.InteractionAllowed) return MapActionOutcome.Refused(InteractionNotAllowed);
        // The argument is the entry's own `onlyUnlock` flag; a plain open asks for it as the game's own
        // interaction does, so the door unlocks and opens under its own rules.
        door.AttemptOpenCloseInteraction(false);
        return MapActionOutcome.Issued(Opened);
    }

    /// <summary>Asks a door to close. A door that is not open is left alone, because the same interaction entry
    /// toggles. The two catalog policies this request can carry are checked before anything is written: the
    /// native entry takes neither, so a request for either is refused by name.</summary>
    internal static MapActionOutcome Close(LG_SecurityDoor door, MapObjectReference address,
        DoorOccupancyPolicy occupancy, DoorForcePolicy force)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (Read(door) is not { } state) return MapActionOutcome.Refused(Stale);
        if (occupancy == DoorOccupancyPolicy.Crush) return MapActionOutcome.Refused(CrushUnsupported);
        if (force == DoorForcePolicy.Force) return MapActionOutcome.Refused(ForceCloseUnsupported);
        if (state.Status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(Destroyed);
        if (!IsOpen(state.Status)) return MapActionOutcome.AlreadyInState(AlreadyClosed);
        door.AttemptOpenCloseInteraction(false);
        return MapActionOutcome.Issued(Closed);
    }

    /// <summary>Starts or stops the alarm the door itself owns, through the puzzle instance its lock component
    /// holds.
    ///
    /// The door's alarm is a `ChainedPuzzleInstance`, not the lock component's active-enemy-wave flag: the level
    /// creates it in `LG_SecurityDoor.SetupChainedPuzzleLock` (which calls `ChainedPuzzleManager.CreatePuzzleInstance`
    /// and subscribes to its solved callback) and stores it in the lock's own `ChainedPuzzleToSolve`, and the
    /// instance's `MasterActivate` is the master-side activation whose verified call edge is the enemy wave the
    /// alarm raises. `MasterDeactivate` is the matching master-side stop. The lock's own `m_hasAlarm` says whether
    /// its puzzle is an alarm at all, so a door locked with a scan or with anything else is refused by name
    /// instead of being started as an alarm.
    ///
    /// The mode is applied to the door's own state: a puzzle that already reads active is left alone when asked
    /// to start and deactivated when asked to stop, and the other way round, so a repeated request never
    /// re-triggers a wave or stops one that is already stopped.</summary>
    internal static MapActionOutcome SetAlarm(LG_SecurityDoor door, MapObjectReference address, DoorAlarmMode mode)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (Read(door) is not { } state) return MapActionOutcome.Refused(Stale);
        if (state.Status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(Destroyed);
        if (Locks(door) is not { } locks) return MapActionOutcome.Refused(NoLockComponent);
        if (!locks.m_hasAlarm) return MapActionOutcome.Refused(NotAnAlarm);
        var puzzle = locks.ChainedPuzzleToSolve;
        if (puzzle == null || puzzle.WasCollected) return MapActionOutcome.Refused(NoAlarmPuzzle);
        if (puzzle.IsActive)
        {
            if (mode == DoorAlarmMode.Start) return MapActionOutcome.AlreadyInState(AlarmAlreadyActive);
            puzzle.MasterDeactivate();
            return MapActionOutcome.Issued(AlarmStopped);
        }
        if (mode == DoorAlarmMode.Stop) return MapActionOutcome.AlreadyInState(AlarmNotActive);
        puzzle.MasterActivate();
        return MapActionOutcome.Issued(AlarmStarted);
    }

    /// <summary>Locks a door with a key item, through the lock component's own key setup. The key item is the
    /// item the game itself hands to that setup, so the resource half resolves it and this layer only checks
    /// that it resolved to a live instance.</summary>
    internal static MapActionOutcome LockWithKeyItem(LG_SecurityDoor door, MapObjectReference address, GateKeyItem keyItem)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (Read(door) is not { } state) return MapActionOutcome.Refused(Stale);
        if (state.Status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(Destroyed);
        if (keyItem == null || keyItem.WasCollected) return MapActionOutcome.Refused(KeyItemUnusable);
        if (Locks(door) is not { } locks) return MapActionOutcome.Refused(NoLockComponent);
        locks.SetupForGateKey(keyItem);
        return MapActionOutcome.Issued(LockedWithKey);
    }

    /// <summary>Locks a door with no key, through the lock component's own no-key setup. That setup takes the
    /// interaction text the game shows a player at the door, so a request without one is refused rather than
    /// sent as an empty prompt.</summary>
    internal static MapActionOutcome LockWithoutKey(LG_SecurityDoor door, MapObjectReference address,
        Localization.LocalizedText interactionText)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (Read(door) is not { } state) return MapActionOutcome.Refused(Stale);
        if (state.Status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(Destroyed);
        if (!interactionText.HasValue) return MapActionOutcome.Refused(LockTextUnusable);
        if (Locks(door) is not { } locks) return MapActionOutcome.Refused(NoLockComponent);
        locks.SetupAsLockedNoKey(interactionText);
        return MapActionOutcome.Issued(LockedWithoutKey);
    }

    /// <summary>Damages a door through the structural damage entry of the door kind this provider addresses.
    ///
    /// That entry is refused here instead of invoked, and the reason is native rather than a policy: on
    /// build 20403457 `LG_SecurityDoor.AttemptDamage` shares the folded empty body at file offset 0x351790
    /// (`C2 00 00` — `ret 0`), while the damageable `LG_WeakDoor` has its own implementation at 0x15C0800. A
    /// zone entrance is a security door and no address names a weak door, so a damage request against an
    /// addressed door would be a request nothing carries out; reporting it as issued would be a success this
    /// layer cannot back.
    ///
    /// The damage type, the source position and the source agent are the arguments the binding resolves and the
    /// signature the call would take, so they are validated first: an unusable request is named as unusable, and
    /// the source agent may be absent because the native signature carries no requirement that it exists.</summary>
    internal static MapActionOutcome Damage(LG_SecurityDoor door, MapObjectReference address, eDoorDamageType damageType,
        UnityEngine.Vector3 sourcePosition, Agents.Agent? sourceAgent)
    {
        if (Unusable(door, address) is { } refused) return refused;
        if (!Enum.IsDefined(typeof(eDoorDamageType), damageType)) return MapActionOutcome.Refused(DamageTypeUnknown);
        if (!float.IsFinite(sourcePosition.x) || !float.IsFinite(sourcePosition.y) || !float.IsFinite(sourcePosition.z))
            return MapActionOutcome.Refused(SourcePositionUnusable);
        if (Read(door) is not { } state) return MapActionOutcome.Refused(Stale);
        if (state.Status == (int)eDoorStatus.Destroyed) return MapActionOutcome.Refused(Destroyed);
        // The source agent is deliberately not required: the native signature carries no requirement that one
        // exists and a damage source may well be the environment, so an absent agent is not a refusal.
        return MapActionOutcome.Refused(NotDamageable);
    }

    /// <summary>Whether the door's own status is a door standing open. Both open states count: a door that is
    /// still animating is open for a second request as much as one that finished.</summary>
    private static bool IsOpen(int status)
        => status == (int)eDoorStatus.Open || status == (int)eDoorStatus.Opening;

    /// <summary>The one gate every door action passes: the instance must read, and it must still be the door the
    /// address was resolved to. A door that was replaced or whose entrance zone changed is refused rather than
    /// acted on under a reference the address layer would no longer produce.</summary>
    private static MapActionOutcome? Unusable(LG_SecurityDoor door, MapObjectReference address)
        => door == null || door.WasCollected ? MapActionOutcome.Refused(Unavailable)
            : DoorObservation.IsCurrentAddress(door, address) ? null
            : MapActionOutcome.Refused(Stale);

    /// <summary>The door's own reading, taken once per action: the status, the lock condition and the key the
    /// lock wants all come from the one read the observation path uses, so a refusal here and a published state
    /// there can never describe two different doors.</summary>
    private static MapObjectDoorSnapshot? Read(LG_SecurityDoor door) => DoorObservation.Read(door)?.Door;

    /// <summary>The lock component this door is set up with, or null when it holds none or holds one that
    /// belongs to another door. The slot is an interface in the interop assembly, so the cast is how the
    /// concrete component is reached, exactly as the door reader reaches it.</summary>
    private static LG_SecurityDoor_Locks? Locks(LG_SecurityDoor door)
    {
        var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
        if (locks == null || locks.WasCollected) return null;
        var owner = locks.m_door;
        return owner == null || owner.WasCollected || owner != door ? null : locks;
    }
}
