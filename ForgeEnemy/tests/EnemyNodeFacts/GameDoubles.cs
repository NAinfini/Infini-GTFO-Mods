// The game types this suite's production sources are compiled against. Every member declared here is one the
// node-list family really reads or writes in build 20403457; nothing here models behaviour the suite does not
// exercise. `NoWarn CS0436` covers the deliberate shadowing of the interop assembly's own definitions, exactly as
// the other ForgeEnemy suites do.
//
// The four families are: the agent and its damage receiver (kill, health, glue), the game's own target
// propagation (target), the navigation-marker layer (mark), and the tag transaction plus its packet (tagged).
using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace UnityEngine
{
    /// <summary>The colour the marker setter takes, with the four channels the row's two ports fill.</summary>
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    /// <summary>The tracking object a marker follows. The suite only needs identity, which is what the layer
    /// records so a case can prove which object a mark was placed on.</summary>
    public sealed class GameObject
    {
        public string name = string.Empty;
    }

    public struct Vector3
    {
        public float x, y, z;
        public static Vector3 zero => new Vector3();
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        /// <summary>The agent's own `Position` is declared here as a plain tuple, the shape the node family's
        /// reads prefer, while the volume row hands a position to the game's own allocator. The conversion is
        /// what lets one double serve both readings.</summary>
        public static implicit operator Vector3((float x, float y, float z) value)
            => new Vector3(value.x, value.y, value.z);
    }
}

namespace ForgeRuntime
{
    public enum RuntimeMode { Off, Play, Authoring }
    public static class Plugin
    {
        public static RuntimeMode ConfiguredMode;
        public static RuntimeKernel? Runtime;
        public static bool CanExecuteGameplay;
        public static string? SuspensionCode;
        public static bool IsSuspended => SuspensionCode != null;
    }
}

namespace Agents
{
    /// <summary>The native agent base. `PropagateTarget` is `EnemyAgent`'s own, so the base carries only the
    /// identity every agent has.</summary>
    public class Agent
    {
        public IntPtr Pointer = new(1);
    }

    /// <summary>The game's own id table: the inverse of a `&lt;kind&gt;:&lt;globalId&gt;` reference. The suite only
    /// needs the one direction the target action reads, and a player instance is registered under its own id.</summary>
    public static class AgentManager
    {
        public static readonly Dictionary<ushort, Agent> Agents = new();
        public static bool GetAgent(ushort globalId, out Agent? agent)
        {
            agent = Agents.TryGetValue(globalId, out var found) ? found : null;
            return agent != null;
        }
    }

    /// <summary>The packet's own agent handle: the game stores an agent by reference and asks the handle for the
    /// instance, which is why the tag observer cannot read the packet's field directly.</summary>
    public sealed class pEnemyAgent
    {
        public Enemies.EnemyAgent? Value;
        public bool TryGet(out Enemies.EnemyAgent? comp) { comp = Value; return Value != null; }
        public void Set(Enemies.EnemyAgent comp) => Value = comp;
    }

    public sealed class AgentTarget
    {
        public Agent? m_agent;
        public (float x, float y, float z) m_position;
    }
}

namespace Enemies
{
    public sealed class EnemyAgent : Agents.Agent
    {
        public ushort GlobalID = 7;
        public bool Alive = true;
        public bool IsSetup = true;
        public (float x, float y, float z) Position = (0, 0, 0);
        public Dam_EnemyDamageBase? Damage;
        public UnityEngine.GameObject? Model;
        public AIG_CourseNode? CourseNode;
        public UnityEngine.GameObject? MainModelGO => Model;

        /// <summary>The replication half the removal action reaches through: the game's own despawn entry is the
        /// replicator's, not the agent's.</summary>
        public EnemySync? Sync;

        /// <summary>The behaviour machine and the facing direction the snapshot observer reads. Both are read
        /// only by the observer this suite links, so the double models the two members it touches.</summary>
        public EnemyAI? AI;
        public UnityEngine.Vector3 Forward = new(0f, 0f, 1f);

        /// <summary>The official type block, read twice and compared exactly as `EnemyTypeReader` does: a block
        /// replaced while it was being read is not a reading of either block. A case can make the read answer a
        /// different instance each time through `OnBlockRead`.</summary>
        public GameData.EnemyDataBlock? Block;
        public Action? OnBlockRead;
        public GameData.EnemyDataBlock? EnemyData
        {
            get
            {
                OnBlockRead?.Invoke();
                var first = Block;
                OnBlockRead?.Invoke();
                return ReferenceEquals(first, Block) ? first : null;
            }
        }
        public bool WasTagged;
        public bool IsTagged;
        public float EnemyTaggedTimer;
        public AgentMode m_mode = AgentMode.Patrolling;

        // The two propagation forms the build declares: the full one returns void, the chance one answers
        // whether the propagation was kept. Both are recorded so a case can assert which one ran.
        public int FullPropagations;
        public int LimitedPropagations;
        public Agents.Agent? LastTarget;
        public float LastChance = -1f;
        public bool LimitedResult = true;
        public void PropagateTargetFull(Agents.Agent agent) { FullPropagations++; LastTarget = agent; }
        public bool PropagateTargetLimited(Agents.Agent agent, float chance)
        { LimitedPropagations++; LastTarget = agent; LastChance = chance; return LimitedResult; }
    }

    public enum AgentMode { None, Hibernating, Patrolling, InCombat }

    public sealed class AIG_CourseNode
    {
        public AIG_Zone? m_zone;
    }

    public sealed class AIG_Zone
    {
        public AIG_Layer? m_layer;
        public int m_dimensionIndex;
        public int LocalIndex;
        /// <summary>The interop's own liveness flag: a level object the game destroyed mid-read is collected,
        /// which is the one way a zone read fails after it has already produced an object.</summary>
        public bool WasCollected { get; set; }
    }

    public sealed class AIG_Layer
    {
        public int m_type;
        public bool WasCollected { get; set; }
    }

    public sealed class EnemyAI
    {
        public EnemyAgent? m_enemyAgent;
        public EnemyBehaviour? m_behaviour;
        /// <summary>The navigation component the snapshot observer reads the agent's speed from. The type is the
        /// game's own (`UnityEngine.AI.INavigation`), so the member is the one the build declares.</summary>
        public UnityEngine.AI.INavigation? m_navMeshAgent;

        /// <summary>The group the life belongs to. The build declares it a field; the double reads it through a
        /// property so a case can replace the group between the two reads the group row makes, which is the only
        /// way a group swapped under a read can be driven.</summary>
        public EnemyGroup? Group;
        public Action? OnGroupRead;
        public EnemyGroup? m_group
        {
            get { OnGroupRead?.Invoke(); return Group; }
            set => Group = value;
        }
    }

    /// <summary>The live group of one or more lives, modelled on the members the group reader touches: the
    /// replicated packet the group publishes its state through, the type it was spawned as, and its patrol
    /// frustration.</summary>
    public sealed class EnemyGroup
    {
        public IntPtr Pointer = new(900);
        public pEnemyGroupData Data = new();
        public EnemyGroupType GroupType = EnemyGroupType.Patrolling;
        public float PatrolFrustration;
    }

    /// <summary>The group's replicated packet. The build declares it a value type and its `currentState` an `EGS`
    /// member, which is the field the state is read from.</summary>
    public sealed class pEnemyGroupData
    {
        public EGS currentState = EGS.Idle;
    }

    /// <summary>The group-state vocabulary, in the build's own declaration order (`Enemies.EGS`).</summary>
    public enum EGS : byte
    {
        Idle, HuntersSpawn, HuntersHunt, HuntersSearch, GuardsSpawn, GuardRespawn, GuardsIdle, GuardsHunting,
        PatrolSpawn, PatrolMove, PatrolIdle, PatrolSearch, PatrolCombat, SurvivalSpawn, SurvivalHunt, DebugSpawn,
        DebugIdle
    }

    /// <summary>The group-type vocabulary (`Enemies.EnemyGroupType`).</summary>
    public enum EnemyGroupType : byte
    {
        Hibernating, Patrolling, Hunters, Survival, DebugSpawnUnit
    }

    public sealed class EnemyBehaviour
    {
        public EB_States m_currentStateName = EB_States.Patrolling;
    }

    public enum EB_States : byte
    {
        Hibernating, Patrolling, Patrolling_Investigate, FollowingGroup, FollowingGroup_MoveToNode, InCombat,
        InCombat_MoveToPoint, InCombat_MoveToTarget, InCombat_MoveToNextNode, InCombat_MoveToNextNode_PathBlocked,
        InCombat_MoveToNextNode_PathOpen, InCombat_MoveToNextNode_DestroyDoor, SquidBoss_Hibernating,
        SquidBoss_Intro, SquidBoss_Combat, SquidBoss_Raging, SquidBoss_Spawning, SquidBoss_Cooldown,
        SquidBoss_RageTransition, Dead
    }

    public sealed class EnemyDetection
    {
        public EnemyAI? m_ai;
        public void UpdateTargets() { }
    }

    public sealed class EnemySync
    {
        public EnemyAgent? m_agent;
        public void OnSpawn() { }
        /// <summary>The replicator the game's own despawn is asked of. A case can leave it null to model an
        /// instance whose replication half is already gone.</summary>
        public SNetwork.IReplicator? Replicator;
    }

    public sealed class EnemyDataBlock
    {
        public uint persistentID;
    }
}

public sealed class Dam_EnemyDamageBase
{
    public Enemies.EnemyAgent? Owner;
    public IntPtr Pointer = new(110);
    public bool IsSetup = true;
    public float Health = 50, HealthMax = 100;

    /// <summary>The game's own unconditional end-of-life entry. `Kills` is whether this double's call really
    /// ends the life, which is what a case turns off to model an entry point whose effect cannot be observed;
    /// `OnInstantDead` is the hook a case uses to make the call throw instead.</summary>
    public int InstantDeaths;
    public bool Kills = true;
    public Action<Dam_EnemyDamageBase>? OnInstantDead;
    public void InstantDead(bool force = false)
    {
        InstantDeaths++;
        OnInstantDead?.Invoke(this);
        if (Kills && Owner != null) Owner.Alive = false;
    }

    /// <summary>The glue accumulation entry the observation brackets. The double adds nothing of its own: a case
    /// writes `AttachedGlueVolume` inside `OnAddToTotalGlueVolume`, which is what the real call does.</summary>
    private float _attachedGlueVolume;
    public float AttachedGlueVolume
    {
        get => OnAttachedGlueVolumeRead?.Invoke() ?? _attachedGlueVolume;
        set => _attachedGlueVolume = value;
    }
    public Func<float>? OnAttachedGlueVolumeRead;
    public int AddToTotalGlueVolumeCalls;
    public Action<Dam_EnemyDamageBase>? OnAddToTotalGlueVolume;
    public void AddToTotalGlueVolume(GlueGunProjectile? proj = null, GlueVolumeDesc volume = default)
    {
        AddToTotalGlueVolumeCalls++;
        OnAddToTotalGlueVolume?.Invoke(this);
    }
}

/// <summary>The glue projectile the accumulation entry takes; the observation never reads it, but the signature
/// has to exist for the hook to compile against the build's own member.</summary>
public sealed class GlueGunProjectile { }

public struct GlueVolumeDesc { public float volume, expandVolume, currentScale; }

namespace GameData
{
    public abstract class GameDataBlockBase<T> where T : GameDataBlockBase<T>
    {
        public static readonly List<T> Blocks = new();
        public static List<T> GetAllBlocks() => Blocks;
        public uint persistentID;
        public string name = string.Empty;
        public bool internalEnabled = true;
    }
    public sealed partial class EnemyDataBlock : GameDataBlockBase<EnemyDataBlock> { }
}

/// <summary>The marker a Forge mark builds. The double records the colour it was set to, which is the one
/// reading the route-A write path has: the build exposes no colour getter on `NavMarker`.</summary>
public sealed class NavMarker
{
    public UnityEngine.GameObject? Tracking;
    public UnityEngine.Color? Color;
    public int ColorCalls;
    public void SetColor(UnityEngine.Color col) { Color = col; ColorCalls++; }
    public void SetTrackingObject(UnityEngine.GameObject obj) => Tracking = obj;
}

[Flags]
public enum NavMarkerOption : ulong
{
    None = 0uL, Waypoint = 2uL, Distance = 8uL, Title = 0x10uL, Enemy = 0x80uL,
    EnemyDistance = Distance | Enemy, EnemyTitleDistance = EnemyDistance | Title
}

/// <summary>The game's own marker layer. Every placement records what it was asked for, so a case can prove the
/// option, the tracking object and the placement count a mark produced.</summary>
public sealed class NavMarkerLayer
{
    public int Placements;
    public int Removals;
    public NavMarkerOption? LastOption;
    public UnityEngine.GameObject? LastTracking;
    public float LastDestroyDelay = -1f;
    public bool ReturnNull;
    public readonly List<NavMarker> Placed = new();

    public NavMarker? PlaceCustomMarker(NavMarkerOption type, UnityEngine.GameObject? trackingObj, string? name,
        float destroyDelay = 0f, bool debug = false)
    {
        Placements++;
        LastOption = type;
        LastTracking = trackingObj;
        LastDestroyDelay = destroyDelay;
        if (ReturnNull) return null;
        var marker = new NavMarker { Tracking = trackingObj };
        Placed.Add(marker);
        return marker;
    }

    public void RemoveMarker(NavMarker? marker) { Removals++; }
}

public static class GuiManager
{
    public static NavMarkerLayer? NavMarkerLayer;
}

public sealed class ES_Base { }

namespace SNetwork
{
    public static class SNet { public static bool IsMaster = true; }
}

/// <summary>The tag transaction the observation hooks. `pTagEnemy` is a struct holding the handle the build
/// declares, and nothing else.</summary>
public static class ToolSyncManager
{
    public struct pTagEnemy
    {
        public Agents.pEnemyAgent enemy;
    }

    public static int DoTagEnemyCalls;
    public static pTagEnemy LastTagged;
    public static void DoTagEnemy(pTagEnemy data) { DoTagEnemyCalls++; LastTagged = data; }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type type, string methodName) { }
    }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPostfix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int First = 0, Last = 800; }
}

namespace Agents
{
    /// <summary>The ability kinds an enemy's own component table is indexed by, in the build's declaration order.
    /// The observation reads one of these out of the attack-start datum and maps it through the provider's own
    /// resource table, so the double carries the member names and values and nothing else.</summary>
    public enum AgentAbility : byte
    {
        None = 0, Melee = 1, Ranged = 2, Alarm = 3, Defensive = 4, Healing = 5,
        GroupEnhance = 6, Detection = 7, DoorBreaker = 8, SpawnChildren = 9
    }

    /// <summary>The agent handle an attack-start datum names its target with. The game stores the agent by
    /// reference and asks the handle for the instance, so the double answers the same way.</summary>
    public sealed class pAgent
    {
        public Agent? Value;
        public bool TryGet(out Agent? comp) { comp = Value; return Value != null; }
        public void Set(Agent comp) => Value = comp;
    }
}

namespace Enemies
{
    /// <summary>The attack-start body `ES_EnemyAttackBase` sends: which ability, at whom, for how long. The
    /// members are the build's own field names, and a case writes them directly.</summary>
    public struct pES_EnemyAttackData
    {
        public UnityEngine.Vector3 Position;
        public UnityEngine.Vector3 TargetPosition;
        public Agents.pAgent TargetAgent;
        public byte AnimIndex;
        public Agents.AgentAbility AbilityType;
        public byte AbilityIndex;
        public float Duration;
    }
}

namespace SNetwork
{
    /// <summary>The replication interface a despawn is asked of. Only the one member the removal action calls is
    /// modelled; a case records the call on its own implementation.</summary>
    public interface IReplicator
    {
        void Despawn();
    }

    /// <summary>The replicator the double's own `EnemySync` hands out: it answers the despawn by running whatever
    /// the case asked for, which is where a suite models the game's own teardown (or its absence).</summary>
    public sealed class Replicator : IReplicator
    {
        public int Despawns;
        public Action? OnDespawn;
        public void Despawn() { Despawns++; OnDespawn?.Invoke(); }
    }
}

/// <summary>The two volumes the effect-volume row drives. `EffectVolume` and its sphere are the game's own
/// (global namespace in the build); the manager is the static registry every registered volume is applied
/// through, and the double records what it holds so a case can prove registration and release.</summary>
public abstract class EffectVolume
{
    public float modificationScale;
    public bool invert;
    public eEffectVolumeContents contents;
    public eEffectVolumeModification modification;
    public int effectOrder;
}

public sealed class EV_Sphere : EffectVolume
{
    public UnityEngine.Vector3 position;
    public float minRadius;
    public float maxRadius;
}

public enum eEffectVolumeContents { All = 0, Health = 1, Infection = 2 }
public enum eEffectVolumeModification { Inflict = 0, Shield = 1 }

public static class EffectVolumeManager
{
    /// <summary>The volumes the manager currently holds: the one list a release has to take a volume out of, and
    /// the one a case reads to prove what a dispatch left behind.</summary>
    public static readonly List<EffectVolume> Registered = new();
    /// <summary>How many volumes were released, which is what a case asserts about a cancel or an expiry that is
    /// expected to take volumes away even when it is also expected to have removed them all.</summary>
    public static int Unregistrations;
    /// <summary>A case turns this on to model a manager whose own registration refuses.</summary>
    public static bool RefuseRegistration;
    public static void RegisterVolume(EffectVolume volume)
    {
        if (RefuseRegistration) throw new InvalidOperationException("volume refused");
        Registered.Add(volume);
    }
    public static void UnregisterVolume(EffectVolume volume) { Registered.Remove(volume); Unregistrations++; }
    public static void Reset() { Registered.Clear(); Unregistrations = 0; RefuseRegistration = false; }
}

/// <summary>The game's own fog sphere allocator: the visible body the volume row allocates at the same position
/// and radius. The double records the four settings and whether the allocation succeeded.</summary>
public sealed class FogSphereAllocator
{
    public UnityEngine.Vector3 Position;
    public float Range;
    public float Density;
    public UnityEngine.Color Radiance;
    public float Intensity;
    public bool Allocated;
    public int Deallocations;
    /// <summary>The number of allocation attempts this scene made, and the allocator the last successful one was
    /// asked of: both are the suite's own record, so a case can prove a refusal allocated nothing at all and a
    /// placement really drew its own visible body.</summary>
    public static int Allocations;
    public static FogSphereAllocator? LastAllocation;
    /// <summary>A case turns this on to model the game's own sphere budget being full.</summary>
    public static bool RefuseAllocation;
    public void SetPositionRange(UnityEngine.Vector3 position, float range) { Position = position; Range = range; }
    public void SetDensity(float density) => Density = density;
    public void SetRadiance(UnityEngine.Color radiance, float intensity = 1f) { Radiance = radiance; Intensity = intensity; }
    public bool TryAllocate()
    {
        Allocations++;
        Allocated = !RefuseAllocation;
        if (Allocated) LastAllocation = this;
        return Allocated;
    }
    public void Deallocate() { Deallocations++; Allocated = false; }
}
