using ForgeRuntime.Framework;
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
namespace UnityEngine
{
    // The only Unity value type the damage entry point names; nothing reads its components.
    public partial struct Vector3
    {
        public float x, y, z;
        public static Vector3 zero => new Vector3();
    }
}
namespace UnityEngine.AI
{
    // The navigation component a tracked enemy's AI exposes: the snapshot reader asks it for the agent's current
    // speed. Only the one member that reader touches is declared.
    public partial class INavigation { public float speed; }
}
namespace Agents
{
    public enum AgentAbility { None = 0, Primary = 1, Secondary = 2 }
    // The common native base: an AI target is an Agent, and only its registered provider names it. Its interop
    // wrapper carries the native pointer, which is the identity a fact compares when two reads of one field
    // each mint a fresh wrapper.
    public abstract class Agent { public IntPtr Pointer = IntPtr.Zero; }
    // Only the members the behaviour facts read: the canonical ai_state mapping is not this double's business.
    public sealed class AgentTarget
    {
        public Agent? m_agent;
        public (float x, float y, float z) m_position;
    }
}
namespace Enemies
{
    public sealed partial class EnemyAgent : Agents.Agent
    {
        public void OnDead() { Alive = false; }
        public ushort GlobalID = 7;
        public IntPtr Pointer = new(10);
        public bool Alive = true;
        public bool IsSetup = true;
        private (float x, float y, float z) _position = (0, 0, 0);
        // The snapshot reader asks where the agent stands, and it asks more than once to keep only a reading that
        // holds: the hook answers the position this read sees, so a case moves the agent between the reads or fails
        // the getter, and the plain field answers every other suite.
        public Func<(float x, float y, float z)>? OnPositionRead;
        public (float x, float y, float z) Position
        { get => OnPositionRead?.Invoke() ?? _position; set => _position = value; }
        public Dam_EnemyDamageBase Damage = null!;
        public EnemyAI AI = null!;
        public EnemyLocomotion Locomotion = null!;
        public EnemyAbilities Abilities = null!;
        // `EnemyAgent.m_lastDamageInflictor` (interop `Modules-ASM.dll`): the agent the receiver registered for
        // the hit currently being processed. The kill fact reads it twice and keeps only a reading that holds;
        // OnInflictorRead lets a case move the field between the two reads or fail the getter.
        private Agents.Agent? _lastDamageInflictor;
        public Action? OnInflictorRead;
        public Agents.Agent? LastDamageInflictor
        { get { OnInflictorRead?.Invoke(); return _lastDamageInflictor; } set { _lastDamageInflictor = value; } }
        private bool _hasTarget;
        public Action? OnTargetRead;
        public bool m_hasValidTarget { get { OnTargetRead?.Invoke(); return _hasTarget; } set { _hasTarget = value; } }
        public bool Invisible;
        public Action? OnInvisibleRead;
        public bool IsInvisible() { OnInvisibleRead?.Invoke(); return Invisible; }
        // The official enemy type this agent was built from; the `enemy-type` mount reads its persistentID. The
        // mount asks twice, so a case replaces the block between the two reads, fails the getter or answers none.
        private GameData.EnemyDataBlock? _enemyData;
        public Func<GameData.EnemyDataBlock>? OnEnemyDataRead;
        public GameData.EnemyDataBlock EnemyData
        { get => OnEnemyDataRead?.Invoke() ?? _enemyData!; set => _enemyData = value; }
        // The members the snapshot reader touches: where the agent is heading, which way it faces, and whether it
        // is currently tagged. The course node names the zone the agent stands in.
        public AIG_CourseNode? CourseNode;
        public UnityEngine.Vector3 Forward = new() { x = 0f, y = 0f, z = 1f };
        public bool IsTagged;
        public float EnemyTaggedTimer;
    }
    public sealed partial class AIG_CourseNode
    {
        public AIG_Zone? m_zone;
    }
    public sealed partial class AIG_Zone
    {
        public AIG_Layer? m_layer;
        public int m_dimensionIndex;
        public int LocalIndex;
    }
    public sealed partial class AIG_Layer
    {
        public int m_type;
    }
    public sealed class EnemySync
    {
        public EnemyAgent? m_agent;
        public void OnSpawn() { }
        public void OnDespawn() { }
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
    public sealed partial class EnemyAI
    {
        public EnemyAgent? m_enemyAgent;
        public EnemyBehaviour m_behaviour = null!;
        public EnemyDetection m_detection = null!;
        public EnemyLocomotion m_locomotion = null!;
        public Agents.AgentTarget? Target = null!;
        public bool IsTargetValid = false;
        public IntPtr Pointer = new(11);
        // The navigation component the snapshot reader asks for the agent's speed.
        public UnityEngine.AI.INavigation? m_navMeshAgent;
    }
    public partial class EnemyBehaviour
    {
        public EnemyAI? m_ai;
        public IntPtr Pointer = new(12);
        public EB_States m_currentStateName = EB_States.Patrolling;
    }
    /// <summary>`Enemies.SquidBossBehaviour` (interop `Modules-ASM.dll`): the one behaviour machine a phase can be
    /// written to, which is what makes a plain `EnemyBehaviour` a refusal. `Phase` is the game's own auto-property
    /// and `ActivateWeakspotIfNeeded` the boss's own step after the write; the row's readback is their only proof.</summary>
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
        public void UpdateTargets() { }
    }
    public sealed partial class EnemyLocomotion
    {
        public EnemyAgent? m_agent;
        private ES_StateEnum _state;
        public Action? OnStateRead;
        public ES_StateEnum CurrentStateEnum { get { OnStateRead?.Invoke(); return _state; } set { _state = value; } }
        public ES_ScoutScream ScoutScream = null!;
    }
    public sealed class ES_ScoutScream
    {
        public ScoutScreamState m_state = ScoutScreamState.Setup;
    }
    public sealed partial class EnemyAbilities
    {
        public EnemyAgent? m_agent;
        private Agents.AgentAbility _active;
        public Action? OnAbilityRead;
        public Agents.AgentAbility ActiveAbility { get { OnAbilityRead?.Invoke(); return _active; } set { _active = value; } }
        public bool CanTriggerAbilities = true;
    }
    // The shipped `ES_EnemyAttackBase` declarations the attack facts read: the AI the state was set up with
    // (m_ai, read back to decide whose attack it is) and m_lastAttackIndex (+0x54), which DoStartAttack writes
    // before the wind-up dispatch. Both attack members are virtual and dispatched through the type's own vtable
    // slots; the declarations exist at least so the two patch declarations resolve against the type the game
    // dispatches them on, and no body here is ever called.
    public partial class ES_EnemyAttackBase
    {
        public IntPtr Pointer = new(500);
        public EnemyAI? m_ai;
        public Agents.Agent? m_attackTarget;
        public int m_lastAttackIndex = -1;
        public virtual void OnAttackWindUp(int animIndex, Agents.AgentAbility abilityType, int abilityIndex) { }
        public virtual void OnAttackPerform(UnityEngine.Vector3 aimTarget) { }
    }
}
public sealed class ES_ScoutDetection
{
    public Enemies.EnemyAgent? m_owner;
    public void OnTargetRegistered(Agents.AgentTarget target) { }
}
public sealed partial class Dam_EnemyDamageLimb
{
    public IntPtr Pointer = new IntPtr(210);
    public Dam_EnemyDamageBase m_base = null!;
    public int m_limbID;
    public bool Destroyed;
    public Action? OnDestroyedRead;
    public bool IsDestroyed { get { OnDestroyedRead?.Invoke(); return Destroyed; } set { Destroyed = value; } }
    public void DestroyLimb() { IsDestroyed = true; }
}
public sealed partial class Dam_EnemyDamageBase
{
    public Dam_EnemyDamageLimb[] DamageLimbs = System.Array.Empty<Dam_EnemyDamageLimb>();
    public Enemies.EnemyAgent Owner = null!;
    public IntPtr Pointer = new(110);
    public bool IsSetup = true;
    public float Health = 50, HealthMax = 100;
    public int Sends;
    public Action<float>? Commit;
    public void SendSetHealth(float amount) { Sends++; if (Commit == null) Health = amount; else Commit(amount); }
    // The stand-in for the one damage entry point, recorded the way Sends records a heal: a test decides the
    // outcome the call models. Suites that never dispatch a damage row only need the declaration to exist.
    public int Attacks;
    public Action<Dam_EnemyDamageBase>? OnBulletDamage;
    public void BulletDamage(float dam, Agents.Agent? sourceAgent, UnityEngine.Vector3 position, UnityEngine.Vector3 direction,
        UnityEngine.Vector3 normal, bool allowDirectionalBonus, int limbID, float staggerMulti, float precisionMulti, uint gearCategoryId)
    { Attacks++; OnBulletDamage?.Invoke(this); }
    public bool ProcessReceivedDamage() => true;
    // The glue accumulation entry the node family's hook brackets, and the volume it reads back: the double adds
    // nothing of its own, because a case writes the volume inside the call the same way the game's own body does.
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
    /// <summary>The block table an enemy block belongs to (interop `GameDataBlockBase`1&lt;EnemyDataBlock&gt;`):
    /// the enemy-type mount reads a block's persistentID through this declaration. The spawn-requirement double
    /// declares the block's own data members as the other half of the same partial class.</summary>
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
    public static partial class SNet { public static bool IsMaster = true; }
    public struct SFloat16
    {
        /// <summary>The value the native setter would store, before the game's own quantization: a case replaces it
        /// to watch what the receiver submitted, and the identity keeps the plain write for every other suite.</summary>
        public static System.Func<float, float, float> Preview = (value, maximum) => value;
        private float _value;
        public void Set(float value, float maximum) => _value = Preview(value, maximum);
        public float Get(float maximum) => _value;
    }
}
