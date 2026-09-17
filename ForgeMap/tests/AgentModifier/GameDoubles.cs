using System.Runtime.CompilerServices;
using UnityEngine;

// Doubles mirror only the interop members the production attribute-modifier sources read. Names, namespaces and
// member kinds follow build 20403457 (dump.cs and the frozen QA profile interop); behaviour is synthetic and NOT
// game-verified. `AgentModifier` and `AgentModifierManager` are declared in the interop assembly's global
// namespace, so they are declared in the global namespace here too — a `using` the real assembly does not need
// would be a compile error in the native project and a silent difference here.
namespace UnityEngine
{
    /// <summary>Unity's Vector3 as the player observation builds one: three floats, exactly the interop struct's
    /// own fields.</summary>
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
}

public static class Pointers
{
    private static long _next = 0x10000;
    public static IntPtr Next() => new(Interlocked.Increment(ref _next));
}

/// <summary>Unity equality: a destroyed object compares equal to null.</summary>
public abstract class UnityObjectDouble
{
    public IntPtr Pointer { get; set; } = Pointers.Next();
    public bool Destroyed;
    private static bool Alive(UnityObjectDouble? value) => value is not null && !value.Destroyed;
    public static bool operator ==(UnityObjectDouble? left, UnityObjectDouble? right)
    {
        bool l = Alive(left), r = Alive(right);
        return !l || !r ? l == r : ReferenceEquals(left, right);
    }
    public static bool operator !=(UnityObjectDouble? left, UnityObjectDouble? right) => !(left == right);
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The native `AgentModifier` enum: every member and its own value, taken from the interop assembly's
/// declaration of build 20403457. The values jump, which is the whole reason the production table is explicit.</summary>
public enum AgentModifier
{
    None = 0, RegenerationCap = 1, RegenerationSpeed = 2, HealSupport = 3, ReviveSpeedSupport = 4,
    ReviveStartHealthSupport = 5, MeleeResistance = 6, ProjectileResistance = 7, InfectionResistance = 8,
    DamageOverTime = 9, Nanoswarm_Shield = 10, Nanoswarm_Weakness = 11, ExplosionResistance = 12,
    PistolDamage = 50, SMGDamage = 51, DMRDamage = 52, AssaultRifleDamage = 53, CarbineDamage = 54,
    AutoPistolDamage = 55, HELDamage = 56, ShotgunDamage = 58, RevolverDamage = 59, SniperDamage = 60,
    BurstCannonDamage = 61, MachineGunDamage = 62, MachinePistolDamage = 63, RifleDamage = 64,
    BurstRifleDamage = 65, DoubleTapRifle = 66, BullpupRifleDamage = 67, CombatShotgunDamage = 68,
    ChokeModShotgunDamage = 69, StandardWeaponDamage = 70, SpecialWeaponDamage = 71, GlueStrength = 100,
    GlueEfficiency = 101, SentryGunSpeed = 102, SentryGunDamage = 103, SentryGunLongRangeDamage = 104,
    SentryGunShortRangeDamage = 105, TripMineDamage = 106, ScannerRechargeSpeed = 107, AmmoSupport = 108,
    HackingProficiency = 150, ComputerProcessingSpeed = 151, InitialAmmoStandard = 152, InitialAmmoSpecial = 153,
    InitialAmmoTool = 154, FogRepellerEffect = 155, GlowstickEffect = 156, BioscanSpeed = 157, MeleeDamage = 200,
    MovementSpeed = 250, MovementAcceleration = 251
}

/// <summary>The native write entry, with its calls recorded. `Add` replaces the returned id (a case returns 0 to
/// stand for the entry registering nothing) and `Clear` replaces the clear (a case throws to stand for a native
/// failure); both default to the behaviour the adapter is written against.</summary>
public class AgentModifierManager
{
    public readonly record struct AddCall(Agents.Agent Agent, AgentModifier Modifier, float Value, float DeltaPerSec);

    public static List<AddCall> Adds { get; } = new();
    public static List<uint> Clears { get; } = new();
    public static Func<Agents.Agent, AgentModifier, float, float, uint>? Add { get; set; }
    public static Action<uint>? Clear { get; set; }
    private static uint _nextId = 1;

    public static uint AddSyncedModifierValue(Agents.Agent agent, AgentModifier modifier, float value, float deltaPerSec = 0f)
    {
        Adds.Add(new AddCall(agent, modifier, value, deltaPerSec));
        return Add != null ? Add(agent, modifier, value, deltaPerSec) : _nextId++;
    }

    public static void ClearSyncedModifierChange(uint modificationID)
    {
        Clears.Add(modificationID);
        Clear?.Invoke(modificationID);
    }

    public static void Reset()
    {
        Adds.Clear();
        Clears.Clear();
        Add = null;
        Clear = null;
        _nextId = 1;
    }
}

namespace SNetwork
{
    public static class SNet { public static bool IsMaster { get; set; } = true; }

    public sealed class SNet_IPlayerAgent
    {
        public object? Target;
        public T? TryCast<T>() where T : class => Target as T;
    }

    public sealed class SNet_Player : UnityObjectDouble
    {
        public ulong Lookup;
        public bool IsBot;
        public bool IsLocal;
        public bool IsMaster;
        public int SlotIndex;
        public SNet_IPlayerAgent? PlayerAgent;
        public int PlayerSlotIndex() => SlotIndex;
    }
}

namespace Agents
{
    /// <summary>`PlayerAgent`'s base type in the interop assembly, and the type every `AgentModifierManager`
    /// entry takes.</summary>
    public class Agent : UnityObjectDouble
    {
        public Vector3 Position { get; set; }
        public virtual bool Alive { get; set; } = true;
        public virtual int PlayerSlotIndex { get; set; }
    }
}

namespace Player
{
    public sealed class PlayerLocomotion : UnityObjectDouble
    {
        public enum PLOC_State : byte
        {
            Stand, Crouch, Run, Jump, Fall, Land, Stunned, Downed, ClimbLadder, OnTerminal, Melee, Empty,
            GrabbedByTrap, GrabbedByTank, Testing, InElevator, GrabbedByPouncer, StandStill
        }
        public PLOC_State m_currentStateEnum;
        public object? CurrentState;
        public T? TryCast<T>() where T : class => CurrentState as T;
    }

    public sealed class PLOC_Downed : UnityObjectDouble
    {
        public bool m_isRevived;
        public PlayerAgent? m_owner;
    }

    public sealed class Interact_Revive : UnityObjectDouble
    {
        public PlayerAgent? Agent;
    }

    /// <summary>The location a warp names: the production reader reads `goodPosition` as a field, exactly the
    /// interop member build 20403457 carries.</summary>
    public struct pPlayerLocationData
    {
        public Vector3 goodPosition;
        public Vector3 position;
    }

    public class Dam_SyncedDamageBase : UnityObjectDouble
    {
        public bool IsSetup;
        public float Health;
        public float HealthMax;
    }

    public sealed class Dam_PlayerDamageBase : Dam_SyncedDamageBase
    {
        public PlayerAgent? Owner;
        public float Infection;
        public void ModifyInfection(pInfection data, bool sync, bool updatePageMap) { }
    }

    public struct pInfection
    {
        public float amount;
        public int mode;
        public int effect;
    }

    public sealed class PlayerAgent : Agents.Agent
    {
        public SNetwork.SNet_Player? Owner;
        public Dam_PlayerDamageBase? Damage;
        public PlayerLocomotion? Locomotion;
        public Interact_Revive? ReviveInteraction;
        public static void ResetStatics() { }
    }

    public sealed class PlayerManager
    {
        private static readonly List<PlayerAgent> Agents = new();
        public static List<PlayerAgent> PlayerAgentsInLevel => Agents;
        public void OnPlayerSpawned() { }
        public void OnPlayerDespawned() { }
        public static void Reset() => Agents.Clear();
    }
}

namespace ForgeMap.Native
{
    using ForgeRuntime.Framework;

    /// <summary>The part of the life-world contract the identity half implements that this fixture's own sources
    /// need to name. The full declaration lives with the player-life trigger half in
    /// `Native/PlayerLifeFacts.cs`, a file that installs native hooks and cannot be compiled into an action
    /// fixture; this is a fixture view of the two members the identity half answers, not a second contract.</summary>
    internal interface IPlayerLifeWorld
    {
        bool Authoritative { get; }
        void BeginWorld();
    }

    /// <summary>One read of one player life right now, as the native side reports it.</summary>
    internal readonly record struct RuntimePlayerLife(bool Alive, bool Downed, bool Revived);
}
