// The game types this suite's production sources are compiled against. Every member declared here is one the
// production code really reads or writes in build 20403457; nothing here models behaviour the suite does not
// exercise. `NoWarn CS0436` covers the deliberate shadowing of the interop assembly's own definitions, exactly as
// the other ForgeEnemy suites do.
using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace UnityEngine
{
    /// <summary>The one Unity value type these handlers name: the impulse force and the hitreact damage position.
    /// The suite reads its components and never asks the engine to integrate anything.</summary>
    public struct Vector3
    {
        public float x, y, z;
        public static Vector3 zero => new Vector3();
        public override string ToString() => $"({x},{y},{z})";
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
    public abstract class Agent
    {
        public IntPtr Pointer = new(1);
    }
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
        public new IntPtr Pointer = new(10);
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

        public struct pExtraBehaviourStateData { public byte bossPhase; }
    }

    // The build's own values (interop `Modules-ASM.dll`, ES_StateEnum): the state machines are read as integers
    // and never written by this module.
    public enum ES_StateEnum : byte
    {
        None, StandStill, PathMove, Knockdown, JumpDissolve, LiquidSnake, KnockdownRecover, Hitreact,
        ShortcutJump, FloaterFly, FloaterHitReact, Dead, ScoutDetection, ScoutScream, Hibernate,
        HibernateWakeUp, Scream, StuckInGlue, ShooterAttack, StrikerAttack, TankAttack, TankMultiTargetAttack,
        TentacleDragMove, StrikerMelee, ClimbLadder, Jump, BirtherGiveBirth, TriggerFogSphere, PathMoveFlyer,
        HitReactFlyer, ShooterAttackFlyer, DeadFlyer, DeadSquidBoss, ScreamFlyer
    }

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
        public virtual EnemySync.pExtraBehaviourStateData GetExtraStateDataForCapture() => default;
        public virtual void SetIncomingExtraStateData(EnemySync.pExtraBehaviourStateData data) { }
    }

    public sealed class SquidBossBehaviour : EnemyBehaviour
    {
        public int Phase { get; set; }
        public int WeakspotActivations { get; private set; }
        public void ActivateWeakspotIfNeeded() { WeakspotActivations++; }
    }

    public sealed class EnemyDetection
    {
        public EnemyAI? m_ai;
        public IntPtr Pointer = new(13);
        public float m_biggestDetectionBuildup;
        public float m_movementDetectionDistance;
        public float m_detectionBuildupSpeed;
        public float m_detectionCooldownSpeed;
        public float maxAttackDistance;
        public bool m_noiseDetectionOn;
        public float m_noiseDetectionRange;
        public void UpdateTargets() { }
    }

    /// <summary>`EnemyLocomotion` as this slice reads it: the hitreact state machine the stagger and interrupt
    /// rows write through, the four attack states the interrupt row asks about a hit in flight, and the state enum
    /// the observers read. `Hitreact` is where the game keeps the one `ES_HitreactBase` an enemy has, which is the
    /// entry point both rows share; the four attack fields are the build's own set, one per attack archetype.</summary>
    public sealed class EnemyLocomotion
    {
        public EnemyAgent? m_agent;
        private ES_StateEnum _state;
        public Action? OnStateRead;
        public ES_StateEnum CurrentStateEnum { get { OnStateRead?.Invoke(); return _state; } set { _state = value; } }
        public ES_ScoutScream ScoutScream = null!;
        public ES_StrikerAttack StrikerAttack = null!;
        public ES_TankAttack TankAttack = null!;
        public ES_TankMultiTargetAttack TankMultiTargetAttack = null!;
        public ES_ShooterAttack ShooterAttack = null!;
        public ES_HitreactBase Hitreact = null!;
        public IntPtr Pointer = new(14);
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

    public class ES_Base
    {
        public EnemyAI? m_ai;
        public EnemyAgent? m_enemyAgent;
        public IntPtr Pointer = new(15);
        public int Stops;
        public virtual void Stop() { Stops++; }
        public virtual bool AllowedToForceState(ES_StateEnum stateEnum) => true;
        public virtual void SetAI(ES_StateEnum stateEnum, EnemyAI ai) { m_ai = ai; }
    }

    /// <summary>The shipped `ES_EnemyAttackBase` members the interrupt row reads: the attack state's own enemy,
    /// and the two public predicates that say whether a hit is still in flight.</summary>
    public class ES_EnemyAttackBase : ES_Base
    {
        public new IntPtr Pointer = new(500);
        public Agents.Agent? m_attackTarget;
        public int m_lastAttackIndex = -1;
        public float m_attackWindupDuration;
        public float m_attackDoneTimer;
        public bool m_attackWasPerformed;
        public bool Performing;
        public bool Charging;
        public Action? OnStateRead;
        public int Activations;
        public virtual void ActivateState(UnityEngine.Vector3 targetPosition, Agents.AgentAbility abilityType, int abilityIndex) => Activations++;
        public virtual void ActivateState(Agents.Agent? agentTarget, Agents.AgentAbility abilityType, int abilityIndex) => Activations++;
        public virtual void OnAttackWindUp(int animIndex, Agents.AgentAbility abilityType, int abilityIndex) { }
        public virtual void OnAttackPerform(UnityEngine.Vector3 aimTarget) { }
        public virtual bool AbilityIsDone() => true;
        public virtual bool IsPerformingAttack() { OnStateRead?.Invoke(); return Performing; }
        public virtual bool IsChargingAttack() { OnStateRead?.Invoke(); return Charging; }
    }

    /// <summary>One subclass per attack-state field `EnemyLocomotion` declares, in the same order. The interrupt
    /// row reaches an enemy's attack through those four fields, so a case can place the state under test in any
    /// one of them.</summary>
    public sealed class ES_StrikerAttack : ES_EnemyAttackBase { }
    public sealed class ES_TankAttack : ES_EnemyAttackBase { }
    public sealed class ES_TankMultiTargetAttack : ES_EnemyAttackBase { }
    public sealed class ES_ShooterAttack : ES_EnemyAttackBase { }

    /// <summary>The shipped `ES_Hitreact` state machine's public surface: the write entry point both the stagger
    /// and the interrupt row use, the game's own gate in front of it, and the two fields the row writes when the
    /// immunity policy says the gate's own answer is to be overridden.</summary>
    public class ES_HitreactBase : ES_Base
    {
        public new IntPtr Pointer = new(16);
        public ES_HitreactType CurrentReactionType { get; protected set; } = ES_HitreactType.None;
        /// <summary>Lets a case move the machine's own reaction after a write, which is the one way the readback
        /// can disagree with what was submitted.</summary>
        public void ForceReaction(ES_HitreactType reaction) => CurrentReactionType = reaction;
        public bool HitreactForbidden { get; set; }
        /// <summary>The answer `CanHitreact` gives. The suite sets it to model a forbidden or throttled reaction.
        /// `overrideTimer` is recorded so a case can prove the `ignore` policy used it.</summary>
        public bool Gate = true;
        public bool LastOverrideTimer;
        public Action? OnGate;
        /// <summary>Makes the write itself fail before it reaches the machine, which is the one way a case can
        /// model a native call that threw: the submission is real, its effect is unknown, and nothing may be
        /// claimed either way.</summary>
        public bool ThrowOnActivate;
        /// <summary>Runs after a write, so a case can model a state machine that kept another reaction: the call
        /// is real and the effect is not observable, which is exactly the unknown-commit path.</summary>
        public Action? OnActivate;
        public readonly List<(ES_HitreactType Reaction, ImpactDirection Direction, bool AttackerIsPlayer,
            Agents.Agent? Attacker, UnityEngine.Vector3 Position, DamageNoiseLevel Noise)> Activations = new();
        public readonly List<ES_HitreactType> GateAsks = new();
        public virtual bool CanHitreact(ES_HitreactType suggestedType, bool overrideTimer = false)
        {
            OnGate?.Invoke();
            GateAsks.Add(suggestedType);
            LastOverrideTimer = overrideTimer;
            return Gate;
        }
        public virtual void ActivateState(ES_HitreactType hitreactType, ImpactDirection impactDirection,
            bool attackerIsPlayer, Agents.Agent? attacker, UnityEngine.Vector3 damagePos,
            DamageNoiseLevel noiseLevel = DamageNoiseLevel.Normal)
        {
            if (ThrowOnActivate) throw new InvalidOperationException("fixture native failure");
            Activations.Add((hitreactType, impactDirection, attackerIsPlayer, attacker, damagePos, noiseLevel));
            CurrentReactionType = hitreactType;
            OnActivate?.Invoke();
        }
    }
}

/// <summary>`ES_HitreactType` is a top-level type in `Modules-ASM.dll`; the members and their order are the
/// build's own. `ToDeath` and `InstantRagdollDeath` are kills and are deliberately not offered as reactions.</summary>
public enum ES_HitreactType : byte
{
    Unspecified, None, Micro, Light, Heavy, ToDeath, InstantRagdollDeath
}

/// <summary>The direction an impact came from. This shape has no direction port, so the handlers pass
/// `Unspecified`; the member exists so a case can prove exactly that.</summary>
public enum ImpactDirection : byte { Unspecified, Front, Back, Right, Left }

public enum DamageNoiseLevel : byte { Normal, Low }

public sealed class ES_ScoutDetection
{
    public Enemies.EnemyAgent? m_owner;
    public void OnTargetRegistered(Agents.AgentTarget target) { }
}

/// <summary>The limb's own physics body: the only reachable impulse write in the build. `AddForce` returns void
/// and queues the force for the applicator's own `FixedUpdate`, which is why a submitted shove is an unknown
/// commit rather than a confirmed displacement.</summary>
public sealed class LimbForceApplicator
{
    public IntPtr Pointer = new IntPtr(300);
    public UnityEngine.Vector3? BodyMassScale;
    public readonly List<(UnityEngine.Vector3 Force, float Duration)> Forces = new();
    public Action? OnAddForce;
    public bool ThrowOnAddForce;
    public void AddForce(UnityEngine.Vector3 force, float duration)
    {
        OnAddForce?.Invoke();
        if (ThrowOnAddForce) throw new InvalidOperationException("fixture native failure");
        Forces.Add((force, duration));
    }
}

public sealed class Dam_EnemyDamageLimb
{
    public IntPtr Pointer = new IntPtr(210);
    public Dam_EnemyDamageBase m_base = null!;
    public int m_limbID;
    public float m_health = 25, m_healthMax = 50;
    public float m_weakspotDamageMulti = 1f, m_armorDamageMulti = 1f;
    public bool Destroyed;
    public Action? OnDestroyedRead;
    public bool IsDestroyed { get { OnDestroyedRead?.Invoke(); return Destroyed; } set { Destroyed = value; } }
    public LimbForceApplicator ForceApplicator = null!;
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
