// The game types this suite's production sources are compiled against. Every member declared here is one the
// production code really reads or writes in build 20403457; nothing here models behaviour the suite does not
// exercise. `NoWarn CS0436` covers the deliberate shadowing of the interop assembly's own definitions, exactly as
// the other ForgeEnemy suites do.
//
// The enemy action families share one double set, so this file carries the phases, damage and behaviour members
// the sibling suites need as well as the control members this one adds: `EnemyLocomotion.HibernateWakeup`,
// `ES_HibernateWakeUp.ActivateState`, `EnemyBehaviour.ChangeState`, `AgentAI.m_mode`, `EnemyAI.NavmeshAgentGoal`,
// `EnemyAI.m_navMeshAgent`, `EnemyDetection.m_biggestDetectionBuildup` and `UnityEngine.AI.INavigation`.
using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace UnityEngine
{
    // The one Unity value type these receivers name; the navigation goal and the wake direction carry its three
    // components, which is all any of them reads.
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3();
    }
}

namespace UnityEngine.AI
{
    /// <summary>The build's own `INavigation`: an interface, so the interop type is a reference type with these
    /// members. `speed`, `destination` and `SetDestination` are the members the game's own move behaviours and the
    /// move_to row reach; the suite records what was written so a case can hold the handler to it.</summary>
    public class INavigation
    {
        public IntPtr Pointer = new(30);
        public UnityEngine.Vector3 destination;
        public float speed = 1.0f;
        public float arrivalDistance = 0.5f;
        public bool onNavMesh = true;
        public bool pathValid = true;
        public bool SetDestination(UnityEngine.Vector3 target) { destination = target; Attempts++; return pathValid; }
        public bool IsPositionOnGraph(UnityEngine.Vector3 position) => onNavMesh;
        public int Attempts;
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
    /// <summary>The build's own `AgentMode` (`Agents.AgentAI`): the four modes plus the disabled one, in the
    /// declaration order the dump records.</summary>
    public enum AgentMode : byte { Off, Agressive, Patrolling, Scout, Hibernate }
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
        public Agents.AgentMode m_mode = Agents.AgentMode.Patrolling;
        // The navigation interface the game's own agents expose. Reading it is how the move_to row decides whether
        // a requested speed has a destination at all.
        public UnityEngine.AI.INavigation m_navMeshAgent = new();
        // The goal every native move behaviour writes. `DropGoalWrites` is the one failure the readback exists to
        // catch: an AI that did not keep the value it was handed.
        private UnityEngine.Vector3 _goal;
        public bool DropGoalWrites;
        public UnityEngine.Vector3 NavmeshAgentGoal
        {
            get => _goal;
            set { if (!DropGoalWrites) _goal = value; }
        }
    }

    public class EnemyBehaviour
    {
        public EnemyAI? m_ai;
        public IntPtr Pointer = new(12);
        public EB_States m_currentStateName = EB_States.Patrolling;
        // The one native write the sleep row submits, recorded so a case can hold the handler to the state it
        // asked for. `OnChange` is the failure a native call can raise after it has taken effect.
        public readonly List<EB_States> Changes = new();
        public Action<EB_States>? OnChange;
        public void ChangeState(EB_States state) { Changes.Add(state); OnChange?.Invoke(state); m_currentStateName = state; }
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
        public IntPtr Pointer = new(14);
        public EnemyAgent? m_agent;
        private ES_StateEnum _state;
        public Action? OnStateRead;
        public ES_StateEnum CurrentStateEnum { get { OnStateRead?.Invoke(); return _state; } set { _state = value; } }
        public ES_ScoutScream ScoutScream = null!;
        // The two hibernation states. `HibernateWakeup` is the receiver the awaken row submits through; the double
        // records the call and decides what the locomotion machine does with it.
        public ES_HibernateWakeUp HibernateWakeup = null!;
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

    /// <summary>The wake state's one entry point, `ActivateState(Vector3 direction, float distance, float delay,
    /// bool isPropagatedWakeup)`. The build's own body records the four arguments and enters the wake state; the
    /// double does the same, and `StayInWakeState` / `OnActivate` are the two ways a wake can fail after the call
    /// has been made.</summary>
    public class ES_HibernateWakeUp : ES_Base
    {
        public IntPtr Pointer = new(40);
        public int Wakeups;
        public UnityEngine.Vector3 LastDirection;
        public float LastDistance, LastDelay;
        public bool LastPropagated;
        public bool StayInWakeState = true;
        public Action? OnActivate;
        /// <summary>What the locomotion machine does when the wake state is entered: the state the game's own
        /// entry leaves the machine in, recorded by the fixture so the double owns no cross-reference to it.</summary>
        public Action? EnterWakeState;
        public void ActivateState(UnityEngine.Vector3 direction, float distance, float delay, bool isPropagatedWakeup)
        {
            Wakeups++;
            LastDirection = direction; LastDistance = distance; LastDelay = delay; LastPropagated = isPropagatedWakeup;
            OnActivate?.Invoke();
            if (StayInWakeState) EnterWakeState?.Invoke();
        }
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

