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
