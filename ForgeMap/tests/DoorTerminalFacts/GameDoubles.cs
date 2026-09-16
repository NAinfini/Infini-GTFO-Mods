using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;
using LevelGeneration;

// Doubles for the natives the door and terminal interaction sources read, plus the two substitutions this
// focused project makes on purpose.
//
// The game types mirror build 20403457's names, namespaces, member kinds and the visibility the interop assembly
// really exposes (`LG_SecurityDoor.m_sync` is the `iLG_Door_Sync` slot, `LG_SecurityDoor.m_locks` the
// `iLG_Door_Locks` slot, `LG_WeakLock.OnStateChange` the public replicated callback, `eWeakLockStatus` and
// `eWeakLockType` the two members a broken lock is read from). Behaviour is synthetic and NOT game-verified: a
// case can see which member was reached and with what, which is exactly what these slices claim.
//
// `ForgeMap.Native.DoorObservation` is production code owned by the door observation slice — it derives an
// address from the level's zone table and is exercised by `tests/MapNativeAdapter`. This project compiles the
// fact and action halves without the level index, so it supplies the same three members from the doubles' own
// fields. A signature change in the real reader fails to compile here rather than passing silently.

namespace UnityEngine
{
    /// <summary>The base every interop object shares: whether its native instance is gone.</summary>
    public abstract class Il2CppObjectDouble
    {
        public bool WasCollected { get; set; }
        public T? TryCast<T>() where T : class => this as T;
    }

    /// <summary>The game object a component hangs on. Only the two members the identity readers use are
    /// mirrored: whether the object is gone, and the per-object id the level assigns it.</summary>
    public sealed class GameObject : Il2CppObjectDouble
    {
        private static int _next;
        private readonly int _instanceId = ++_next;

        /// <summary>`UnityEngine.Object.GetInstanceID`: one id per object, and the value a container's or a
        /// level item's identity is read from.</summary>
        public int GetInstanceID() => _instanceId;
    }

    public abstract class Component : Il2CppObjectDouble
    {
        public GameObject gameObject { get; set; } = new GameObject();

        /// <summary>The parent walk the weak-lock row uses to find the door a lock was spawned on. Unity's own
        /// member starts at the component itself, which is why `MonoBehaviour` answers with itself.</summary>
        public virtual T? GetComponentInParent<T>() where T : class => null;
    }

    public abstract class Behaviour : Component { }

    public abstract class MonoBehaviour : Behaviour
    {
        public override T? GetComponentInParent<T>() where T : class => this as T;
    }

    /// <summary>Unity's Vector3 as the action layer hands one to a native entry.</summary>
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    /// <summary>The transform the unlock entry reads the door's position from.</summary>
    public sealed class Transform : Il2CppObjectDouble
    {
        public Vector3 position;
    }
}

namespace Localization
{
    /// <summary>The game's localized-text value: a plain struct of the untranslated text and two ids, and the
    /// one type the no-key lock setup takes for the prompt it shows at the door.</summary>
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
    /// <summary>The agent an interaction entry may carry. An unlock requested by a plan has none.</summary>
    public class Agent : UnityEngine.Il2CppObjectDouble { }
}

namespace ChainedPuzzles
{
    /// <summary>The chained puzzle a door's lock was set up with. The scan row asks it for the one reading that
    /// tells an activation from a completion, which is the puzzle's own `IsSolved`.</summary>
    public sealed class ChainedPuzzleInstance : UnityEngine.Il2CppObjectDouble
    {
        public bool IsSolved { get; set; }
        public bool IsActive { get; set; }
    }
}

namespace LevelGeneration
{
    public enum eDoorStatus : byte    {
        None = 0, Closed = 1, Closed_BrokenCantOpen = 2, Closed_LockedWithKeyItem = 3,
        Closed_LockedWithChainedPuzzle_Alarm = 4, Closed_LockedWithChainedPuzzle = 5,
        Closed_LockedWithPowerGenerator = 6, Closed_LockedWithNoKey = 7, ChainedPuzzleActivated = 8,
        Unlocked = 9, Open = 10, Destroyed = 11, GluedMax = 12, TryOpenStuckInGlue = 13,
        TryOpenStuckBroken = 14, Closed_LockedWithBulkheadDC = 15, Opening = 16
    }

    /// <summary>The door interaction vocabulary, with the two members this slice's unlock row writes.
    /// The numbers are the native enum's own indices.</summary>
    public enum eDoorInteractionType : byte
    {
        Open = 0, SetLockedWithChainedPuzzle_Alarm = 1, SetLockedWithChainedPuzzle = 2, SetLockedNoKey = 3,
        ActivateChainedPuzzle = 4, Unlock = 5, Close = 6, DoDamage = 7, SetGluedMaxEnabled = 8,
        SetGluedMaxDisabled = 9, SetGlueLevel = 10, Approach = 11
    }

    public enum eWeakLockStatus : byte { LockedMelee = 0, LockedHackable = 1, LockMelterApplied = 2, Unlocked = 3 }

    public enum eWeakLockType : byte { None = 0, Melee = 1, Hackable = 2 }

    /// <summary>The interface slot a door holds its lock component in; the production reader is the one that
    /// casts it, exactly like the interop interface.</summary>
    public sealed class iLG_Door_Locks : UnityEngine.Il2CppObjectDouble
    {
        public object? Target;
        public new T? TryCast<T>() where T : class => Target as T;
    }

    /// <summary>The interface slot a door holds its sync component in, with the same cast rule.</summary>
    public sealed class iLG_Door_Sync : UnityEngine.Il2CppObjectDouble
    {
        public object? Target;
        public new T? TryCast<T>() where T : class => Target as T;
    }

    public sealed class GateKeyItem : UnityEngine.Il2CppObjectDouble { }

    /// <summary>The door's own lock component. Each setup records that it was reached, because the entry
    /// returning a status is the whole observable, and the request's policy is what decides which one runs.</summary>
    public sealed class LG_SecurityDoor_Locks : UnityEngine.Il2CppObjectDouble
    {
        public LG_SecurityDoor? m_door;
        public bool m_lockedWithNoKey;
        public GateKeyItem? m_gateKeyItemNeeded;
        public bool m_hasAlarm;
        /// <summary>`LG_SecurityDoor_Locks.ChainedPuzzleToSolve`: the puzzle the lock was set up with. The scan
        /// row asks it for its own `IsSolved` reading, and a lock with no puzzle answers null, which the
        /// derivation treats as absence rather than as "unsolved".</summary>
        public ChainedPuzzles.ChainedPuzzleInstance? ChainedPuzzleToSolve { get; set; }
        public int SimpleLockCalls { get; private set; }
        public int NoKeyLockCalls { get; private set; }
        public Localization.LocalizedText? LastNoKeyText { get; private set; }

        public eDoorStatus SetupSimpleLock()
        {
            SimpleLockCalls++;
            return eDoorStatus.Closed;
        }

        public eDoorStatus SetupAsLockedNoKey(Localization.LocalizedText interactionTextId)
        {
            NoKeyLockCalls++;
            LastNoKeyText = interactionTextId;
            return eDoorStatus.Closed_LockedWithNoKey;
        }
    }

    /// <summary>The door's own synchronized interaction entry. The interaction type it was asked for is what the
    /// unlock row is asserted on: it is the only observable of a void entry point.</summary>
    public sealed class LG_Door_Sync : UnityEngine.Il2CppObjectDouble
    {
        public int InteractionCalls { get; private set; }
        public eDoorInteractionType? LastInteraction { get; private set; }
        public float LastVal1 { get; private set; }
        public float LastVal2 { get; private set; }
        public Agents.Agent? LastAgent { get; private set; }
        public bool Throws { get; set; }

        public void AttemptDoorInteraction(eDoorInteractionType interaction, float val1, float val2,
            UnityEngine.Vector3 position, Agents.Agent? sourceAgent)
        {
            if (Throws) throw new InvalidOperationException("fixture door interaction failure");
            InteractionCalls++;
            LastInteraction = interaction;
            LastVal1 = val1;
            LastVal2 = val2;
            LastAgent = sourceAgent;
        }
    }

    public sealed class LG_SecurityDoor : UnityEngine.MonoBehaviour
    {
        public eDoorStatus LastStatus { get; set; } = eDoorStatus.Closed;
        public iLG_Door_Locks? m_locks { get; set; }
        public iLG_Door_Sync? m_sync { get; set; }
        public UnityEngine.Transform transform { get; set; } = new UnityEngine.Transform();
        /// <summary>The address this door reads as right now. The observation substitution below compares it with
        /// the address a reference was built from, so a case can move a door without moving its reference.</summary>
        public ForgeMap.MapObjectReference? AddressNow { get; set; }

        /// <summary>The door's own simple-lock setup, which is the entry point the lock row uses: the setup
        /// belongs to the door, and the lock component it holds is only the state that setup writes.</summary>
        public eDoorStatus SetupSimpleLock()
            => m_locks?.TryCast<LG_SecurityDoor_Locks>() is { } component
                ? component.SetupSimpleLock() : eDoorStatus.Closed;    }

    /// <summary>The weak lock's own replicated state. Only the members the lock-broken row reads are mirrored:
    /// the status, the type the game set the lock up with, and the holder it was spawned under.</summary>
    public sealed class LG_WeakLock : UnityEngine.MonoBehaviour
    {
        public eWeakLockStatus Status { get; set; } = eWeakLockStatus.LockedMelee;
        public eWeakLockType m_lockType { get; set; } = eWeakLockType.Melee;
        public UnityEngine.Il2CppObjectDouble? m_holder { get; set; }
    }

    /// <summary>The level zone a weak door stands in. Only its three coordinates are mirrored, because that is
    /// the whole of what a zone address is written from.</summary>
    public sealed class LG_Zone : UnityEngine.MonoBehaviour
    {
        public int Dimension { get; set; }
        public int Layer { get; set; }
        public int LocalIndex { get; set; }
    }

    /// <summary>The gate a weak door was spawned on, and the course nodes it links. This is the path the
    /// production weak-door zone walk takes: gate → course node → zone. A gate with no node answers no zone,
    /// which is the case a door the level never placed is.</summary>
    public sealed class LG_Gate : UnityEngine.MonoBehaviour
    {
        public List<AIG_INode> m_nodes { get; } = new();
    }

    /// <summary>A navigation node, of which the zone walk reads one member: a node that is not a course node
    /// stands in no zone, which is what the walk's own cast decides.</summary>
    public class AIG_INode : UnityEngine.Il2CppObjectDouble
    {
        public T? TryCast<T>() where T : class => this as T;
    }

    /// <summary>A course node. The real one lives in the `AIGraph` namespace; it is declared here beside the gate
    /// because this file is the one place the game's shapes are mirrored for this project.</summary>
    public sealed class AIG_CourseNode : AIG_INode
    {
        public LG_Zone? m_zone { get; set; }
    }

    /// <summary>The weak door: the only door kind the game lets enemies break. The members mirrored here are the
    /// ones the broken-door row and its identity read — the gate it was spawned on, its own status, the
    /// replicated damage/destroyed callbacks and its transform.</summary>
    public sealed class LG_WeakDoor : UnityEngine.MonoBehaviour
    {
        public LG_Gate? Gate { get; set; }
        public eDoorStatus LastStatus { get; set; } = eDoorStatus.Closed;
        public float m_healthMax { get; set; } = 100f;
        public UnityEngine.Transform transform { get; set; } = new UnityEngine.Transform();
        public int DamageCalls { get; private set; }
        public int DestroyedCalls { get; private set; }
        public SNetwork.SNet_Player? LastInstigator { get; private set; }

        /// <summary>`LG_WeakDoor.OnSyncDoorGotDamage`: the game's own replication callback while the door is
        /// being hit. The double records that it ran and who the callback named.</summary>
        public void OnSyncDoorGotDamage(float damageDelta, float totalDamageTaken, bool sourceZPos, bool isDropin,
            SNetwork.SNet_Player? instigatorPlayer)
        {
            DamageCalls++;
            LastInstigator = instigatorPlayer;
        }

        /// <summary>`LG_WeakDoor.OnSyncDoorGotDestroyed`: the same callback for the door breaking.</summary>
        public void OnSyncDoorGotDestroyed(float damage, bool sourceZPos, bool isDropinState,
            SNetwork.SNet_Player? instigatorPlayer)
        {
            DestroyedCalls++;
            LastStatus = eDoorStatus.Destroyed;
            LastInstigator = instigatorPlayer;
        }
    }

    /// <summary>The terminal, of which the interaction facts read one member: its own sync id. The command row's
    /// visibility branch reads the terminal's own command state instead, which is the same state its two
    /// synchronized setters write — `pComputerTerminalState.RemovedCommands` on the real component and
    /// `CommandIsHidden` off it.</summary>
    public sealed class LG_ComputerTerminal : UnityEngine.MonoBehaviour
    {
        public uint SyncID { get; set; }
        /// <summary>The address the terminal reads as right now, filled by the fixture.</summary>
        public ForgeMap.MapObjectReference? AddressNow { get; set; }
        /// <summary>The commands this terminal currently hides, which is the state the setters write and the
        /// reader answers from. A command absent from it reads shown.</summary>
        public HashSet<TERM_Command> HiddenCommands { get; } = new();
        public List<TERM_Command> HideCalls { get; } = new();
        public List<TERM_Command> ShowCalls { get; } = new();
        /// <summary>When set, the synced setter writes nothing, which is how a case reaches the unconfirmed
        /// commit path: the call was issued and the terminal's own reading still disagrees with it.</summary>
        public bool SettersDoNothing { get; set; }

        public bool CommandIsHidden(TERM_Command command) => HiddenCommands.Contains(command);

        /// <summary>`LG_ComputerTerminal.TrySyncSetCommandHidden`: the game's own synchronized setter.</summary>
        public void TrySyncSetCommandHidden(TERM_Command command)
        {
            HideCalls.Add(command);
            if (SettersDoNothing) return;
            HiddenCommands.Add(command);
        }

        /// <summary>`LG_ComputerTerminal.TrySyncSetCommandShow`: the same setter in the other direction.</summary>
        public void TrySyncSetCommandShow(TERM_Command command)
        {
            ShowCalls.Add(command);
            if (SettersDoNothing) return;
            HiddenCommands.Remove(command);
        }
    }

    public enum TERM_Command : byte
    {
        None = 0, Help = 1, Commands = 2, Cls = 3, Exit = 4, Open = 5, Close = 6, Activate = 7, Deactivate = 8,
        EmptyLine = 9, InvalidCommand = 10, DownloadData = 11, ViewSecurityLog = 12, Override = 13,
        DisableAlarm = 14, Locate = 15, ActivateBeacon = 16, Find = 17, ShowList = 18, Query = 19, Ping = 20,
        ReactorStartup = 21, ReactorVerify = 22, ReactorShutdown = 23, WardenObjectiveSpecialCommand = 24,
        TerminalUplinkConnect = 25, TerminalUplinkVerify = 26, TerminalUplinkConfirm = 27, ListLogs = 28,
        ReadLog = 29, Start = 30, TryUnlockingTerminal = 31, WardenObjectiveGatherCommand = 32,
        TerminalCorruptedUplinkConnect = 33, TerminalCorruptedUplinkVerify = 34, TimedConnectionSend = 35,
        TimedConnectionVerify = 36, UsedCommand = 37, UniqueCommand1 = 38, UniqueCommand2 = 39,
        UniqueCommand3 = 40, UniqueCommand4 = 41, UniqueCommand5 = 42, Info = 43, MAX_COUNT = 44
    }
}

namespace SNetwork
{
    /// <summary>The game's own master flag. Both halves read it, so a case can take the master role away.</summary>
    public static class SNet
    {
        public static bool IsMaster { get; set; } = true;
    }

    /// <summary>The player a replication callback names. `LG_Door_Sync.OnDoorGotDamage` and
    /// `OnDoorGotDestroyed` carry one, and the production weak-door row resolves it through the player identity
    /// module; the double only has to be the type the callback's signature names.</summary>
    public class SNet_Player : UnityEngine.Il2CppObjectDouble { }
}

namespace ForgeMap.Native
{
    /// <summary>The address decision the production `ZoneIndex` declares: the address, or the one reason there is
    /// none. It is repeated here because `ZoneIndex.cs` is not compiled into this project — the level's zone
    /// table belongs to the observation slice — so the door reader's own signature is mirrored instead.</summary>
    internal readonly record struct MapObjectAddressDecision(ForgeMap.MapObjectReference? Address, MapObjectRefusal? Refusal)
    {
        internal static MapObjectAddressDecision Addressed(ForgeMap.MapObjectReference address) => new(address, null);
        internal static MapObjectAddressDecision Refused(MapObjectRefusal refusal) => new(null, refusal);
    }

    /// <summary>The native reasons a door or terminal cannot be addressed, with the members `ZoneIndex.cs`
    /// declares. Only the two this fixture's door reader produces are reached; the rest exist so the enum is the
    /// same shape as the production one.</summary>
    internal enum MapObjectRefusal
    {
        Unreadable, BulkheadTransition, NotAnEntrance, NoCoordinates, NoOwningZone, NotListedByZone,
        SpecificTerminalSpawns
    }

    /// <summary>The three coordinates one zone address is written from, with the members the production
    /// `ZoneIndex` declares. `ZoneIndex.cs` is not compiled into this project, so the record is mirrored here for
    /// the same reason the address decision is.</summary>
    internal readonly record struct ZoneCoordinates(int Dimension, int Layer, int Zone);

    /// <summary>The three members of the production door observation these two halves call, answered from the
    /// doubles' own fields. The reader's real work — deriving an address from the level's zone table — belongs to
    /// the observation slice and is tested by `tests/MapNativeAdapter`; what this project needs is that a fact is
    /// published against the address a door really reads as, and that an action refuses a door whose address
    /// moved.</summary>
    internal static class DoorObservation
    {
        /// <summary>The doors this world's address table holds, filled by `World.Door`.</summary>
        internal static readonly Dictionary<ForgeMap.MapObjectReference, LG_SecurityDoor> ByAddressTable = new();

        internal static LG_SecurityDoor? ByAddress(ForgeMap.MapObjectReference address)
            => ByAddressTable.TryGetValue(address, out var door) ? door : null;

        internal static LG_SecurityDoor? Door(LG_SecurityDoor_Locks? locks)
            => locks?.m_door is { WasCollected: false } door ? door : null;

        internal static bool IsCurrentAddress(LG_SecurityDoor door, ForgeMap.MapObjectReference address)
            => door != null && !door.WasCollected && door.AddressNow == address;

        internal static MapObjectAddressDecision Decide(LG_SecurityDoor? door)
        {
            if (door == null || door.WasCollected) return MapObjectAddressDecision.Refused(MapObjectRefusal.Unreadable);
            return door.AddressNow is { } address
                ? MapObjectAddressDecision.Addressed(address)
                : MapObjectAddressDecision.Refused(MapObjectRefusal.NotAnEntrance);
        }
    }
}
