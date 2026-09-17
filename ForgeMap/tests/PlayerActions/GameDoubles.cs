using System.Runtime.CompilerServices;
using UnityEngine;

// Doubles mirror only the interop members the production player-action sources read. Names, namespaces and member
// kinds follow build 20403457 (dump.cs / the frozen QA profile interop); behaviour is synthetic and NOT
// game-verified. A member the production code names but the real build does not have is a compile error here,
// which is the one drift this fixture can catch.
namespace UnityEngine
{
    /// <summary>Unity's Vector3 as the action layer builds and reads one: three floats, exactly the interop
    /// struct's own fields. The euler-to-direction conversion reads no Unity member at all, so nothing here has to
    /// model Unity's maths.</summary>
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0f, 0f, 0f);
        public static Vector3 operator +(Vector3 left, Vector3 right) => new(left.x + right.x, left.y + right.y, left.z + right.z);
        public static Vector3 operator -(Vector3 left, Vector3 right) => new(left.x - right.x, left.y - right.y, left.z - right.z);
        public static Vector3 operator *(Vector3 value, float scale) => new(value.x * scale, value.y * scale, value.z * scale);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude <= 0f ? zero : new Vector3(x / magnitude, y / magnitude, z / magnitude);
        public static float Distance(Vector3 left, Vector3 right) => (left - right).magnitude;
    }

    /// <summary>Unity's clock, as the movement-state read uses it: the seconds the game has been running. A case
    /// sets it, because the state-entry time the native machine carries is on this same clock.</summary>
    public static class Time
    {
        public static float time { get; set; }
    }
}

/// <summary>The game's own collection type. The production warp gate reads `Il2CppSystem`'s `HashSet` — the
/// interop's own type and not the BCL one — so the fixture declares that type instead of aliasing it.</summary>
namespace Il2CppSystem.Collections.Generic
{
    public sealed class HashSet<T>
    {
        private readonly List<T> _items = new();
        public bool Contains(T item) => _items.Contains(item);
        public void Add(T item) => _items.Add(item);
        public int Count => _items.Count;
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

/// <summary>The level's own dimension vocabulary; `Reality` is the level geometry a `vector3` destination
/// names.</summary>
public enum eDimensionIndex
{
    Reality = 0, Dimension_1 = 1, Dimension_2 = 2, Dimension_3 = 3, Dimension_4 = 4, Dimension_5 = 5,
    Dimension_6 = 6, Dimension_7 = 7, Dimension_8 = 8, Dimension_9 = 9, Dimension_10 = 10, Dimension_11 = 11,
    Dimension_12 = 12, Dimension_13 = 13, Dimension_14 = 14, Dimension_15 = 15, Dimension_16 = 16,
    Dimension_17 = 17, Dimension_18 = 18, Dimension_19 = 19, Dimension_20 = 20, MAX_COUNT = 21
}

/// <summary>The two members of the native infection value's mode: the payload is either an absolute value or an
/// addition to the value the receiver holds.</summary>
public enum pInfectionMode : byte { Set = 0, Add = 1 }

/// <summary>The native infection effect vocabulary. The action submits `None`: the catalog row is an infection
/// change, not a disinfection.</summary>
public enum pInfectionEffect : byte { None = 0, DisinfectionPack = 1, DisinfectionStation = 2 }

/// <summary>The native `pInfection` value: amount, mode and effect, in the interop struct's own order.</summary>
public struct pInfection
{
    public float amount;
    public pInfectionMode mode;
    public pInfectionEffect effect;
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
        // The interop member is a method, not a property.
        public int PlayerSlotIndex() => SlotIndex;
    }
}

namespace Agents
{
    /// <summary>PlayerAgent's base type in the interop assembly; position and alive live here.</summary>
    public class Agent : UnityObjectDouble
    {
        public Vector3 Position { get; set; }
        public Vector3 Forward;
        // A spawned agent is alive in the game; a double that defaulted to dead would hide the dead branch.
        public virtual bool Alive { get; set; } = true;
        public virtual int PlayerSlotIndex { get; set; }
        public virtual Vector3 EyePosition => Position;
        public virtual bool IsLocallyOwned { get; set; } = true;
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
        /// <summary>The game clock reading at which the machine entered the state it is in, which is what the
        /// movement-state row turns into the seconds the state has been held.</summary>
        public float m_changeStateTime;
        /// <summary>Every force this machine's own push channel was handed, in submission order, and the channel's
        /// accumulated value. The real member accumulates the same way, which is why a row confirms a write by
        /// reading back at least what it wrote.</summary>
        public List<Vector3> Pushes { get; } = new();
        private Vector3 _externalPushForce;
        public void AddExternalPushForce(Vector3 force) { Pushes.Add(force); _externalPushForce += force; }
        public Vector3 GetExternalPushForce() => _externalPushForce;
        public void ResetExternalPushForce() { Pushes.Clear(); _externalPushForce = Vector3.zero; }
        /// <summary>The state object the interop's own `TryCast` would answer with. A case that needs the downed
        /// state object sets it; null is a locomotion machine with no state object to hand out.</summary>
        public object? CurrentState;
        public T? TryCast<T>() where T : class => CurrentState as T;
    }

    /// <summary>The game's downed locomotion state: whether its revive already ran and the agent it belongs to.
    /// The identity half reads both when it answers a life readback.</summary>
    public sealed class PLOC_Downed : UnityObjectDouble
    {
        public bool m_isRevived;
        public PlayerAgent? m_owner;
    }

    /// <summary>The revive interaction a downed agent carries; the actor that started the rescue is on it.</summary>
    public sealed class Interact_Revive : UnityObjectDouble
    {
        public PlayerAgent? Agent;
    }

    /// <summary>The location struct a teleport argument carries. The identity half reads the position off it and
    /// refuses anything that is not this type.</summary>
    public struct pPlayerLocationData
    {
        public Vector3 position;
        /// <summary>The slot the identity half's own position read uses. The game's struct carries both, and the
        /// production read names this one.</summary>
        public Vector3 goodPosition;
    }

    /// <summary>The synced damage base a player's health and infection state live on, with the members the player
    /// observation reads.</summary>
    public class Dam_SyncedDamageBase : UnityObjectDouble
    {
        public bool IsSetup;
        public float Health;
        public float HealthMax;
    }

    /// <summary>One submitted infection change, kept so a case can prove what reached the native entry and with
    /// which flags.</summary>
    public readonly record struct InfectionCall(pInfection Data, bool Sync, bool UpdatePageMap);

    /// <summary>The player damage base: the infection value and its one replicated write entry. `Commit` is what
    /// the native call does instead of storing the value, for the cases that need a refusal or a receiver that
    /// stores something else; null means the game's own behaviour (an `Add` adds, a `Set` sets).</summary>
    public sealed class Dam_PlayerDamageBase : Dam_SyncedDamageBase
    {
        public PlayerAgent? Owner;
        public float Infection;
        public List<InfectionCall> Calls { get; } = new();
        public Action<pInfection, bool, bool>? Commit;
        public void ModifyInfection(pInfection data, bool sync, bool updatePageMap)
        {
            Calls.Add(new InfectionCall(data, sync, updatePageMap));
            if (Commit != null) { Commit(data, sync, updatePageMap); return; }
            Infection = data.mode == pInfectionMode.Add ? Infection + data.amount : data.amount;
        }
    }

    public sealed class PlayerAgent : Agents.Agent
    {
        /// <summary>The two components the generic-player batch writes through: the agent's own stamina value and
        /// the camera this machine draws for it.</summary>
        public PlayerStamina? Stamina { get; set; }
        public FPSCamera? FPSCamera { get; set; }
        /// <summary>The game's own warp flags, at the enum's own values.</summary>
        [Flags]
        public enum WarpOptions : byte
        {
            None = 0, WithBotsIfAny = 1, ShowScreenEffectForLocal = 2, PlaySounds = 4,
            WithoutBots = ShowScreenEffectForLocal | PlaySounds, All = WithoutBots | WithBotsIfAny
        }

        /// <summary>One submitted warp request, so a case can prove the dimension, the landing point and the
        /// options the action asked for.</summary>
        public readonly record struct WarpCall(eDimensionIndex Dimension, Vector3 Position, Vector3 Look, WarpOptions Options);

        public SNetwork.SNet_Player? Owner;
        public Dam_PlayerDamageBase? Damage;
        public PlayerLocomotion? Locomotion;
        /// <summary>The revive interaction a downed agent carries; the identity half reads the rescuer off it.
        /// Null is an agent with no revive interaction to read.</summary>
        public Interact_Revive? ReviveInteraction;
        /// <summary>The states this agent accepts a warp in. The default mirrors the game's own set for a player on
        /// the ground; a case that needs the refusal clears or replaces it.</summary>
        public Il2CppSystem.Collections.Generic.HashSet<PlayerLocomotion.PLOC_State>? m_warpableStates = new()
        {
            PlayerLocomotion.PLOC_State.Stand, PlayerLocomotion.PLOC_State.Crouch, PlayerLocomotion.PLOC_State.Run,
            PlayerLocomotion.PLOC_State.Jump, PlayerLocomotion.PLOC_State.Fall, PlayerLocomotion.PLOC_State.Land,
            PlayerLocomotion.PLOC_State.StandStill
        };
        public List<WarpCall> Warps { get; } = new();
        /// <summary>What the native request does. Null means the game's own path: the agent is moved to the
        /// landing point. A case can leave the position alone, which is what a declined warp looks like.</summary>
        public Action<PlayerAgent, eDimensionIndex, Vector3, Vector3, WarpOptions>? Warp;
        public void RequestWarpToSync(eDimensionIndex dimensionIndex, Vector3 position, Vector3 lookDir, WarpOptions options)
        {
            Warps.Add(new WarpCall(dimensionIndex, position, lookDir, options));
            if (Warp != null) { Warp(this, dimensionIndex, position, lookDir, options); return; }
            Position = position;
        }

        /// <summary>The game's own landing solver. It answers the reference position unless a case replaces it;
        /// a null answer is the game refusing the destination.</summary>
        public static Func<eDimensionIndex, Vector3, (bool Ok, Vector3 Landed)>? Sampler;
        public static List<(eDimensionIndex Dimension, Vector3 Reference)> Samples { get; } = new();
        public static bool SampleWarpPosition(eDimensionIndex dimensionIndex, Vector3 referencePosition, out Vector3 result)
        {
            Samples.Add((dimensionIndex, referencePosition));
            if (Sampler != null) { var (ok, landed) = Sampler(dimensionIndex, referencePosition); result = landed; return ok; }
            result = referencePosition;
            return true;
        }
        public static void ResetStatics() { Sampler = null; Samples.Clear(); }
    }

    public sealed class PlayerManager
    {
        private static readonly List<PlayerAgent> Agents = new();
        public static bool ThrowOnRead;
        public static List<PlayerAgent> PlayerAgentsInLevel
            => ThrowOnRead ? throw new InvalidOperationException("fixture native read failure") : Agents;
        public void OnPlayerSpawned() { }
        public void OnPlayerDespawned() { }
        public static void Reset() { Agents.Clear(); ThrowOnRead = false; }
    }
}

/// <summary>PlayerStamina's own value, as the stamina row writes and reads it back. The real member is a `0..1`
/// float behind a private setter; the double keeps the same range contract and records every write.</summary>
public sealed class PlayerStamina : UnityObjectDouble
{
    public List<float> Writes { get; } = new();
    private float _stamina;
    public float Stamina
    {
        get => _stamina;
        set { _stamina = value; Writes.Add(value); }
    }
}

/// <summary>The camera a shake is submitted to. Every call is recorded, so a case can prove the amplitude that
/// reached the camera after the distance falloff and that nothing else was called.</summary>
public sealed class FPSCamera : UnityObjectDouble
{
    public readonly record struct ShakeCall(float Duration, float Amplitude, float Frequency, Vector3 Direction);
    public List<ShakeCall> Shakes { get; } = new();
    public void Shake(float duration, float amplitude, float frequency, Vector3 worldDirection)
        => Shakes.Add(new ShakeCall(duration, amplitude, frequency, worldDirection));
}

/// <summary>The native liquid preset vocabulary, in the enum's own order: the contract's preset names are aligned
/// with these indices and the native call takes the index.</summary>
public enum ScreenLiquidSettingName
{
    enemyBlood_BigBloodBomb = 0, enemyBlood_SmallRandomStreak = 1, enemyBlood_Squirt = 2, shooterGoo = 3,
    spitterJizz = 4, elevatorRain = 5, waterDrizzle = 6, waterDrip = 7, playerBlood = 8,
    disinfectionPack_Apply = 9, disinfectionStation_Apply = 10, infectionSweat = 11,
    playerBlood_SmallDamage = 12, playerBlood_BigDamage = 13, playerBlood_Downed = 14, anemoneGoo = 15
}

/// <summary>The game's own screen-liquid entry: it queues one job for the local viewport and answers whether it
/// took it. A case reads the calls back and can make the entry refuse.</summary>
public static class ScreenLiquidManager
{
    public readonly record struct ApplyCall(ScreenLiquidSettingName Setting, Vector3 Position, Vector3 Direction);
    public static List<ApplyCall> Calls { get; } = new();
    public static bool Result { get; set; } = true;
    public static bool Apply(ScreenLiquidSettingName setting, Vector3 position, Vector3 direction)
    {
        Calls.Add(new ApplyCall(setting, position, direction));
        return Result;
    }
    public static void Reset() { Calls.Clear(); Result = true; }
}
