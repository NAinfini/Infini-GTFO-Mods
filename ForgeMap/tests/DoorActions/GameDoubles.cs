using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;
using LevelGeneration;

// Doubles for the natives the production door action sources read, plus the one substitution this focused
// project makes on purpose.
//
// The game types mirror build 20403457's names, namespaces, member kinds and the visibility the interop
// assembly really exposes (the dump calls MasterActivate private, the interop publishes it). Behaviour is
// synthetic and NOT game-verified: a case can see which member was reached and with what, which is exactly what
// the action layer claims.
//
// `ForgeMap.Native.DoorObservation` is production code owned by the door observation slice — it derives an
// address from the level's zone table and is exercised by `tests/MapNativeAdapter`. This project compiles the
// execute path without the level index, so it supplies the same two members from the doubles' own fields. A
// signature change in the real reader fails to compile here rather than passing silently.

namespace UnityEngine
{
    /// <summary>The base every interop object shares: whether its native instance is gone.</summary>
    public abstract class Il2CppObjectDouble
    {
        public bool WasCollected { get; set; }
        public T? TryCast<T>() where T : class => this as T;
    }

    public abstract class Component : Il2CppObjectDouble { }
    public abstract class Behaviour : Component { }
    public abstract class MonoBehaviour : Behaviour { }

    /// <summary>Unity's Vector3 as the action layer hands one to a native entry.</summary>
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
}

namespace Localization
{
    /// <summary>The game's localized-text value: a plain struct of the untranslated text and two ids, and the
    /// one type the door lock's no-key setup takes for the prompt it shows at the door.</summary>
    public struct LocalizedText
    {
        public string? UntranslatedText;
        public uint Id;
        public uint OldId;
        public LocalizedText(string untranslatedText) { UntranslatedText = untranslatedText; Id = 0; OldId = 0; }
        public bool HasValue => !string.IsNullOrEmpty(UntranslatedText) || Id != 0 || OldId != 0;
    }
}

namespace Agents
{
    /// <summary>The damage source the native damage entry would take. No row here reaches it.</summary>
    public class Agent : UnityEngine.Il2CppObjectDouble { }
}

namespace ChainedPuzzles
{
    /// <summary>The alarm/scan instance a door's lock component holds. Only the members the alarm row reaches
    /// are mirrored, and each call is counted, because the entry returning void is the whole observable.</summary>
    public sealed class ChainedPuzzleInstance : UnityEngine.Il2CppObjectDouble
    {
        private bool active;
        public int ActivateCalls { get; private set; }
        public int DeactivateCalls { get; private set; }
        public bool IsActive
        {
            get => active;
            set => active = value;
        }
        public void MasterActivate() { ActivateCalls++; active = true; }
        public void MasterDeactivate() { DeactivateCalls++; active = false; }
    }
}

namespace LevelGeneration
{
    public enum eDoorStatus : byte
    {
        None = 0, Closed = 1, Closed_BrokenCantOpen = 2, Closed_LockedWithKeyItem = 3,
        Closed_LockedWithChainedPuzzle_Alarm = 4, Closed_LockedWithChainedPuzzle = 5,
        Closed_LockedWithPowerGenerator = 6, Closed_LockedWithNoKey = 7, ChainedPuzzleActivated = 8,
        Unlocked = 9, Open = 10, Destroyed = 11, GluedMax = 12, TryOpenStuckInGlue = 13,
        TryOpenStuckBroken = 14, Closed_LockedWithBulkheadDC = 15, Opening = 16
    }

    public enum eDoorDamageType : byte
    {
        EnemyLight = 0, EnemyHeavy = 1, MeleeWeaponLight = 2, MeleeWeaponHeavy = 3, Explosion = 4,
        MeleeWeaponMinimal = 5
    }

    /// <summary>The interface slot a door holds its lock component in; the production reader is the one that
    /// casts it, exactly like the interop interface — the cast answers with the native object behind the slot,
    /// not with the slot itself.</summary>
    public sealed class iLG_Door_Locks : UnityEngine.Il2CppObjectDouble
    {
        public object? Target;
        public new T? TryCast<T>() where T : class => Target as T;
    }

    public sealed class GateKeyItem : UnityEngine.Il2CppObjectDouble { }

    public sealed class LG_SecurityDoor_Locks : UnityEngine.Il2CppObjectDouble
    {
        public LG_SecurityDoor? m_door;
        public bool m_lockedWithNoKey;
        public GateKeyItem? m_gateKeyItemNeeded;
        public bool m_hasAlarm;
        /// <summary>The alarm the door owns. Null means the door's lock never got a chained puzzle.</summary>
        public ChainedPuzzles.ChainedPuzzleInstance? ChainedPuzzleToSolve { get; set; }
        /// <summary>The two lock setups answer with the status they set, exactly as the interop members do. No
        /// row here is bound to them yet, so nothing records the call; they exist because the native layer names
        /// them.</summary>
        public eDoorStatus SetupForGateKey(GateKeyItem keyItem) => eDoorStatus.Closed_LockedWithKeyItem;
        public eDoorStatus SetupAsLockedNoKey(Localization.LocalizedText interactionTextId)
            => eDoorStatus.Closed_LockedWithNoKey;
    }

    public sealed class LG_SecurityDoor : UnityEngine.MonoBehaviour
    {
        public eDoorStatus LastStatus { get; set; } = eDoorStatus.Closed;
        public iLG_Door_Locks? m_locks { get; set; }
        public bool InteractionAllowed { get; set; } = true;
        /// <summary>The address this door reads as right now. The observation substitution below compares it
        /// with the address a reference was built from, so a case can move a door without moving its reference.</summary>
        public ForgeMap.MapObjectReference? AddressNow { get; set; }
        /// <summary>When set, reading the door's own status throws, which is what a door torn down under a live
        /// reference looks like from the action layer.</summary>
        public bool StatusThrows { get; set; }
        public eDoorStatus Status => StatusThrows ? throw new InvalidOperationException("fixture door read failure") : LastStatus;
        public int OpenCloseCalls;
        public bool? LastOnlyUnlock;
        public int ForceOpenCalls;
        public void AttemptOpenCloseInteraction(bool onlyUnlock = false)
        {
            OpenCloseCalls++; LastOnlyUnlock = onlyUnlock;
        }
        public void ForceOpenSecurityDoor() { ForceOpenCalls++; }
    }

    /// <summary>The terminal states the interaction row writes between: the two members the row names, in the
    /// game's own order (`Sleeping` is 0 and `Awake` is 1).</summary>
    public enum TERM_State
    {
        Sleeping = 0,
        Awake = 1,
        PlayerInteracting = 2
    }

    /// <summary>The terminal the interaction row writes. Only the one member the row reaches is mirrored: the
    /// state it holds and reports, so a case can read back what the row actually set.</summary>
    public sealed class LG_ComputerTerminal : UnityEngine.MonoBehaviour
    {
        /// <summary>Whether reading the state throws, which is what a terminal torn down under a live reference
        /// looks like from the action layer.</summary>
        public bool StateThrows { get; set; }
        private TERM_State state = TERM_State.Sleeping;
        public TERM_State CurrentStateName
        {
            get => StateThrows ? throw new InvalidOperationException("fixture terminal read failure") : state;
            set => state = value;
        }
    }
}

namespace SNetwork
{
    /// <summary>The game's own master flag. The action gate reads it, so a case can take the master role away.</summary>
    public static class SNet
    {
        public static bool IsMaster { get; set; } = true;
    }
}

namespace ForgeMap.Native
{
    /// <summary>The two members of the production door observation the execute path calls, answered from the
    /// doubles' own fields. The reader's real work — deriving an address from the level's zone table — belongs to
    /// the observation slice and is tested by `tests/MapNativeAdapter`; what this project needs is that the
    /// action layer refuses a door whose address moved, reads the state it acts on from the instance, and finds
    /// a door by the address a reference carries.</summary>
    internal static class DoorObservation
    {
        /// <summary>The doors this world's address table holds, filled by `DoorActionWorld.Door`.</summary>
        internal static readonly Dictionary<ForgeMap.MapObjectReference, LG_SecurityDoor> ByAddressTable = new();

        internal static LG_SecurityDoor? ByAddress(ForgeMap.MapObjectReference address)
            => ByAddressTable.TryGetValue(address, out var door) ? door : null;

        internal static bool IsCurrentAddress(LG_SecurityDoor door, ForgeMap.MapObjectReference address)
            => door != null && !door.WasCollected && door.AddressNow == address;

        internal static ForgeMap.MapObjectObservation? Read(LG_SecurityDoor door)
        {
            if (door == null || door.WasCollected) return null;
            int status = (int)door.Status;
            var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
            bool locked = ForgeMap.MapObjectDoorStatus.IsLocked(status);
            if (locks != null && !locks.WasCollected)
            {
                locked |= locks.m_lockedWithNoKey;
                if (locks.m_gateKeyItemNeeded is { WasCollected: false }) locked = true;
            }
            return new ForgeMap.MapObjectObservation(true,
                new ForgeMap.MapObjectDoorSnapshot(ForgeMap.MapObjectDoorStatus.Name(status), status,
                    ForgeMap.MapObjectDoorStatus.Phase(status), locked, ""), null);
        }
    }
}
