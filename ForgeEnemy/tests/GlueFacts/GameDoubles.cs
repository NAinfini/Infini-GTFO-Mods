// The game types this suite's production sources are compiled against. Every member declared here is one the
// production code really reads or writes in build 20403457; nothing here models behaviour the suite does not
// exercise. `NoWarn CS0436` covers the deliberate shadowing of the interop assembly's own definitions, exactly as
// the other ForgeEnemy suites do.
//
// This suite compiles the provider whole (`EnemyModule*.cs`), so the declarations the sibling suites needed are
// carried here too, including the ones only the phase row uses. The foam family's own declarations are at the end:
// `GlueVolumeDesc`, the `ProjectileManager` glue entry points, the receiver's glue volume, and the glue target
// index the enemy publishes.
using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace UnityEngine
{
    // The one Unity value type the damage entry point names; nothing reads its components.
    public struct Vector3
    {
        public float x, y, z;
        public static Vector3 zero => new Vector3();
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
    public enum AgentAbility { None = 0, Primary = 1, Secondary = 2 }
    // The common native base: an AI target is an Agent, and only its registered provider names it.
    public abstract class Agent { }
    // Only the members the behaviour facts read: the canonical ai_state mapping is not this double's business.
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
        public void OnDead() { Alive = false; }
        public ushort GlobalID = 7;
        public IntPtr Pointer = new(10);
        public bool Alive = true;
        public bool IsSetup = true;
        public (float x, float y, float z) Position = (0, 0, 0);
        public Dam_EnemyDamageBase Damage = null!;
        public EnemyAI AI = null!;
        public EnemyLocomotion Locomotion = null!;
        public EnemyAbilities Abilities = null!;
        private bool _hasTarget;
        public Action? OnTargetRead;
        public bool m_hasValidTarget { get { OnTargetRead?.Invoke(); return _hasTarget; } set { _hasTarget = value; } }
        public bool Invisible;
        public Action? OnInvisibleRead;
        public bool IsInvisible() { OnInvisibleRead?.Invoke(); return Invisible; }
        public GameData.EnemyDataBlock EnemyData = null!;
    }

    public sealed class EnemySync
    {
        public EnemyAgent? m_agent;
        public void OnSpawn() { }
        public void OnDespawn() { }
        public SNetwork.Packet<pEnemyStateData> m_aiStatePacket = null!;
        public SNetwork.Packet<pExtraBehaviourStateData> m_aiExtraBehaviourStatePacket = null!;
        public pEnemyStateData m_enemyStateData;

        public struct pEnemyStateData
        {
            public Agents.AgentTarget target;
            public int agentMode;
            public EB_States behaviourState;
        }

        // The replicated extra behaviour payload: exactly one `byte`, the boss phase. The production write path
        // only has to prove the field exists and is one byte wide, which is why the phase domain is 0..255.
        public struct pExtraBehaviourStateData { public byte bossPhase; }
    }

    // The build's own values (interop `Modules-ASM.dll`, ES_StateEnum): the state machines are read as integers
    // and never written by the module.
    public enum ES_StateEnum : byte
    {
        None, StandStill, PathMove, Knockdown, JumpDissolve, LiquidSnake, KnockdownRecover, Hitreact,
        ShortcutJump, FloaterFly, FloaterHitReact, Dead, ScoutDetection, ScoutScream, Hibernate,
        HibernateWakeUp, Scream, StuckInGlue, ShooterAttack, StrikerAttack, TankAttack, TankMultiTargetAttack,
        TentacleDragMove, StrikerMelee, ClimbLadder, Jump, BirtherGiveBirth, TriggerFogSphere, PathMoveFlyer,
        HitReactFlyer, ShooterAttackFlyer, DeadFlyer, DeadSquidBoss, ScreamFlyer
    }

    // AIAgent state names are the native EB_States members the behaviour facts map.
    public enum EB_States : byte
    {
        Hibernating, Patrolling, Patrolling_Investigate, FollowingGroup, FollowingGroup_MoveToNode, InCombat,
        InCombat_MoveToPoint, InCombat_MoveToTarget, InCombat_MoveToNextNode, InCombat_MoveToNextNode_PathBlocked,
        InCombat_MoveToNextNode_PathOpen, InCombat_MoveToNextNode_DestroyDoor, SquidBoss_Hibernating,
        SquidBoss_Intro, SquidBoss_Combat, SquidBoss_Raging, SquidBoss_Spawning, SquidBoss_Cooldown,
        SquidBoss_RageTransition, Dead, InCombat_ChargedAttack, Incombat_GraphTraversal_Flyer,
        Incombat_FlyOutOfBoss_Flyer, InCombat_Dash, InCombat_HeldPlayer, InCombat_AfterHeldPlayer,
        InCombat_Consume, InCombat_SpitOut, InCombat_Stagger
    }

    public enum ScoutScreamState { Setup, Chargeup, Scream, Response, Done }

    public sealed class EnemyAI
    {
        public EnemyAgent? m_enemyAgent;
        public EnemyBehaviour m_behaviour = null!;
        public EnemyDetection m_detection = null!;
        public EnemyLocomotion m_locomotion = null!;
        public Agents.AgentTarget? Target = null!;
        public bool IsTargetValid = false;
        public IntPtr Pointer = new(11);
    }

    public class EnemyBehaviour
    {
        public EnemyAI? m_ai;
        public IntPtr Pointer = new(12);
        public EB_States m_currentStateName = EB_States.Patrolling;
        // The game's own phase replication: the host captures the payload, a client applies it. A test asserts the
        // production write path never touches these, because the write goes through the boss's own setter.
        public virtual EnemySync.pExtraBehaviourStateData GetExtraStateDataForCapture() => default;
        public virtual void SetIncomingExtraStateData(EnemySync.pExtraBehaviourStateData data) { }
    }

    /// <summary>The boss behaviour the phase action writes through. `Phase` is an auto-property in the build, so
    /// the double is one too: it validates nothing, which is what puts the value domain on the Forge side.</summary>
    public sealed class SquidBossBehaviour : EnemyBehaviour
    {
        public int Phase { get; set; }
        public int WeakspotActivations { get; private set; }
        public Action? OnWeakspotCheck;
        public void ActivateWeakspotIfNeeded() { WeakspotActivations++; OnWeakspotCheck?.Invoke(); }
    }

    public sealed class EnemyDetection
    {
        public EnemyAI? m_ai;
        public IntPtr Pointer = new(13);
        public float m_biggestDetectionBuildup;
        // The perception fields the unimplemented `perception_profile` row would have written: public instance
        // fields, and no replication channel anywhere in the build. Declared so the evidence in the actions file
        // names members this suite compiles against.
        public float m_movementDetectionDistance;
        public float m_detectionBuildupSpeed;
        public float m_detectionCooldownSpeed;
        public float maxAttackDistance;
        public bool m_noiseDetectionOn;
        public float m_noiseDetectionRange;
        public void UpdateTargets() { }
    }

    public sealed class EnemyLocomotion
    {
        public EnemyAgent? m_agent;
        private ES_StateEnum _state;
        public Action? OnStateRead;
        public ES_StateEnum CurrentStateEnum { get { OnStateRead?.Invoke(); return _state; } set { _state = value; } }
        public ES_ScoutScream ScoutScream = null!;
        // The attack state instances a future attack row would have activated.
        public ES_StrikerAttack StrikerAttack = null!;
        public ES_TankAttack TankAttack = null!;
        public ES_ShooterAttack ShooterAttack = null!;
        public void ForceState(ES_Base state) => Forced.Add(state);
        public ES_Base AddState(ES_StateEnum stateEnum, ES_Base stateInstance) => stateInstance;
        public void ChangeState(ES_StateEnum state) => Changed.Add(state);
        public readonly List<ES_Base> Forced = new();
        public readonly List<ES_StateEnum> Changed = new();
    }

    public sealed class ES_ScoutScream
    {
        public ScoutScreamState m_state = ScoutScreamState.Setup;
    }

    public sealed class EnemyAbilities
    {
        public EnemyAgent? m_agent;
        private Agents.AgentAbility _active;
        public Action? OnAbilityRead;
        public Agents.AgentAbility ActiveAbility { get { OnAbilityRead?.Invoke(); return _active; } set { _active = value; } }
        public bool CanTriggerAbilities = true;
        public Agents.AgentAbility UseAbilityCalls;
        public bool UseAbility(Agents.AgentAbility ability, int index) { UseAbilityCalls = ability; return true; }
    }

    /// <summary>`ES_Base`: `Stop()` is declared but not overridden anywhere in the build, which is exactly why the
    /// `behavior_interrupt` row is absent from the contract — the double records calls so a suite could not
    /// accidentally claim an effect the game would not produce.</summary>
    public class ES_Base
    {
        public EnemyAI? m_ai;
        public int Stops;
        public virtual void Stop() { Stops++; }
        public virtual bool AllowedToForceState(ES_StateEnum stateEnum) => true;
        public virtual void SetAI(ES_StateEnum stateEnum, EnemyAI ai) { }
    }

    /// <summary>The shipped `ES_EnemyAttackBase` declarations the attack facts read, plus the two public
    /// `ActivateState` overloads the absent `attack` row would have called. Neither is called by this suite.</summary>
    public class ES_EnemyAttackBase : ES_Base
    {
        public IntPtr Pointer = new(500);
        public Agents.Agent? m_attackTarget;
        public int m_lastAttackIndex = -1;
        public float m_attackWindupDuration;
        public float m_attackDoneTimer;
        public bool m_attackWasPerformed;
        public int Activations;
        public virtual void ActivateState(UnityEngine.Vector3 targetPosition, Agents.AgentAbility abilityType, int abilityIndex) => Activations++;
        public virtual void ActivateState(Agents.Agent? agentTarget, Agents.AgentAbility abilityType, int abilityIndex) => Activations++;
        public virtual void OnAttackWindUp(int animIndex, Agents.AgentAbility abilityType, int abilityIndex) { }
        public virtual void OnAttackPerform(UnityEngine.Vector3 aimTarget) { }
        public virtual bool AbilityIsDone() => true;
    }

    public sealed class ES_StrikerAttack : ES_EnemyAttackBase { }
    public sealed class ES_TankAttack : ES_EnemyAttackBase { }
    public sealed class ES_ShooterAttack : ES_EnemyAttackBase { }
}

public sealed class ES_ScoutDetection
{
    public Enemies.EnemyAgent? m_owner;
    public void OnTargetRegistered(Agents.AgentTarget target) { }
}

public sealed class Dam_EnemyDamageLimb
{
    public IntPtr Pointer = new IntPtr(210);
    public Dam_EnemyDamageBase m_base = null!;
    public int m_limbID;
    // The four fields the absent `limb_profile` row was surveyed against: every one of them is writable, and none
    // of them is replicated. Declared here so the evidence names fields this suite compiles against.
    public float m_health = 25, m_healthMax = 50;
    public float m_weakspotDamageMulti = 1f, m_armorDamageMulti = 1f;
    public bool Destroyed;
    public Action? OnDestroyedRead;
    public bool IsDestroyed { get { OnDestroyedRead?.Invoke(); return Destroyed; } set { Destroyed = value; } }
    public void DestroyLimb() { IsDestroyed = true; }
}

public sealed class Dam_EnemyDamageBase
{
    public Dam_EnemyDamageLimb[] DamageLimbs = System.Array.Empty<Dam_EnemyDamageLimb>();
    public Enemies.EnemyAgent Owner = null!;
    public IntPtr Pointer = new(110);
    public bool IsSetup = true;
    public float Health = 50, HealthMax = 100;
    public int Sends;
    public Action<float>? Commit;
    public void SendSetHealth(float amount) { Sends++; if (Commit == null) Health = amount; else Commit(amount); }
    public int Attacks;
    public Action<Dam_EnemyDamageBase>? OnBulletDamage;
    public void BulletDamage(float dam, Agents.Agent? sourceAgent, UnityEngine.Vector3 position, UnityEngine.Vector3 direction,
        UnityEngine.Vector3 normal, bool allowDirectionalBonus, int limbID, float staggerMulti, float precisionMulti, uint gearCategoryId)
    { Attacks++; OnBulletDamage?.Invoke(this); }
    public bool ProcessReceivedDamage() => true;

    // The foam family's half of the receiver. `AttachedGlueVolume` is the readback the handler proves a spawn with,
    // and the two write entries are the game's own: `RemoveGlueVolume` takes a volume back off, `ClearGlue` empties
    // the receiver. Every one of them is observable here so a case can assert what the handler really submitted.
    // The volume is a property rather than a field only so a case can make the read itself misbehave.
    private float _attachedGlueVolume;
    public float AttachedGlueVolume
    {
        get => OnAttachedGlueVolumeRead?.Invoke() ?? _attachedGlueVolume;
        set => _attachedGlueVolume = value;
    }
    public Func<float>? OnAttachedGlueVolumeRead;
    public int Removals;
    public float RemovedVolume;
    public int Clears;
    public Action<Dam_EnemyDamageBase>? OnRemoveGlueVolume;
    public Action<Dam_EnemyDamageBase>? OnClearGlue;
    public void RemoveGlueVolume(float deltaToRemove, float duration = 0f)
    {
        Removals++;
        RemovedVolume += deltaToRemove;
        // The field, not the property: a case that makes the read misbehave is modelling the readback, not the
        // receiver's own arithmetic.
        _attachedGlueVolume = Math.Max(0f, _attachedGlueVolume - deltaToRemove);
        OnRemoveGlueVolume?.Invoke(this);
    }
    public void ClearGlue() { Clears++; _attachedGlueVolume = 0f; OnClearGlue?.Invoke(this); }
    public IGlueTarget? GetGlueTarget(int index) => index < 0 ? null : new IGlueTarget { GlueTargetSubIndex = index };
}

/// <summary>`IGlueTarget`'s one member this package reads: the index the enemy publishes for its own glue target,
/// which is what the spawn entry point takes instead of an array position. The rest of the interface is not
/// modelled because no production source reaches it. It sits in the global namespace because that is where the
/// game's own damage types live.</summary>
public class IGlueTarget
{
    public int GlueTargetSubIndex;
}

/// <summary>The game's own volume descriptor: exactly the three fields the build declares. `volume` and
/// `expandVolume` are the author's numbers; `currentScale` belongs to the projectile in flight.</summary>
public struct GlueVolumeDesc
{
    public float volume;
    public float expandVolume;
    public float currentScale;
}

/// <summary>The game's glue entry points, down to the signatures the build declares. Every glue request the
/// provider makes lands here, so a case can assert the exact arguments and the sync id sequence. The volume a
/// spawn leaves behind lands on the receiver the call names, because the real entry point replicates to that
/// enemy's own damage base and not to a shared counter.</summary>
public static class ProjectileManager
{
    public static uint NextSyncID = 1;
    public static float ExpandAttached;
    public static float MultiplierSeen;
    public static int SubIndexSeen = int.MinValue;
    public static int Spawns;
    public static Action? OnSpawnGlueOnEnemyAgent;

    public static uint GetNextSyncID() => NextSyncID++;

    public static void WantToSpawnGlueOnEnemyAgent(uint syncID, Enemies.EnemyAgent enemy, int limbID,
        UnityEngine.Vector3 localPos, GlueVolumeDesc volumeDesc, float effectMultiplier)
    {
        Spawns++;
        SubIndexSeen = limbID;
        MultiplierSeen = effectMultiplier;
        ExpandAttached = volumeDesc.expandVolume;
        enemy.Damage.AttachedGlueVolume += volumeDesc.volume;
        OnSpawnGlueOnEnemyAgent?.Invoke();
    }
}

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

namespace SNetwork
{
    public static class SNet { public static bool IsMaster = true; }
    public struct SFloat16
    {
        private float _value;
        public void Set(float value, float maximum) => _value = value;
        public float Get(float maximum) => _value;
    }
    public sealed class Packet<T>
    {
        public int Sends;
        public T Last = default!;
        public bool Send(T data) { Sends++; Last = data; return true; }
        public bool Receive(T data) { Last = data; return true; }
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type type, string methodName) { }
        public HarmonyPatch(Type type) { }
    }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPostfix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int First = 0, Last = 800; }
    public sealed class Harmony
    {
        public Harmony(string id) { }
        public void UnpatchSelf() { }
    }
}
