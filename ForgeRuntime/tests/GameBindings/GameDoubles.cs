using System;

namespace ForgeRuntime
{
    internal enum RuntimeMode { Off, Authoring, Play }
    internal static class Plugin { internal const string PluginVersion = "1.2.0"; internal static readonly BepInEx.Logging.ManualLogSource PluginLog = new(); }
}
namespace BepInEx { public static class Paths { public static string GameRootPath = ""; public static string BepInExRootPath = ""; } }
namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public readonly System.Collections.Generic.List<string> Messages = new();
        public void LogInfo(object value) { lock (Messages) Messages.Add("info:" + value); }
        public void LogWarning(object value) { lock (Messages) Messages.Add("warning:" + value); }
        public void LogError(object value) { lock (Messages) Messages.Add("error:" + value); }
    }
}
namespace UnityEngine
{
    public class MonoBehaviour { public MonoBehaviour(IntPtr pointer) { } }
    // The only Unity value type the stand-in damage entry point names; nothing reads its components.
    public struct Vector3
    {
        public float x, y, z;
        public static Vector3 zero => new Vector3();
    }
}
public enum eGameStateName { Inactive, Startup, Offline, FakeLobby, NoLobby, Lobby, Generating, ReadyToStopElevatorRide, StopElevatorRide, ReadyToStartLevel, InLevel, AfterLevel, Slim, CaptureRecall, ExpeditionSuccess, ExpeditionFail, ExpeditionAbort }
public static class GameStateManager { public static eGameStateName CurrentStateName = eGameStateName.Lobby; }

namespace SNetwork
{
    public static class SNet { public static bool IsMaster = true; public static MasterState MasterManagement = new(); }
    public sealed class MasterState { public bool IsMigrating; }
    public struct SFloat16
    {
        public static Func<float, float, float> Preview = (value, maximum) => value;
        private float value;
        public void Set(float v, float maximum) => value = Preview(v, maximum);
        public float Get(float maximum) => value;
    }
}
namespace Agents
{
    public enum AgentAbility { None = 0, Primary = 1, Secondary = 2 }
    // The common native base: an AI target is an Agent, and only its registered provider names it.
    public abstract class Agent { }
    public sealed class AgentTarget
    {
        public Agent? m_agent;
        public (float x, float y, float z) m_position;
    }
}
namespace GameData
{
    // The enemy's authored block. Its `persistentID` is what an `enemy-type` mount names, so the reader only ever
    // reads it back; nothing in this harness authors one.
    public class EnemyDataBlock { public uint persistentID; }
}
namespace Enemies
{
    public sealed class EnemyAgent : Agents.Agent
    {
        public ushort GlobalID;
        public IntPtr Pointer;
        public bool Alive = true;
        public bool IsSetup = true;
        public GameData.EnemyDataBlock? EnemyData;
        public Dam_EnemyDamageBase Damage = null!;
        // The behaviour facts read the AI of the same life and the locomotion state machine beside it.
        public EnemyAI AI = null!;
        public EnemyLocomotion Locomotion = null!;
        public EnemyAbilities Abilities = null!;
        public bool m_hasValidTarget;
    }
    // The build's own values: the state machines are read as integers and never written by the module.
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
        public Agents.AgentTarget? Target;
        public bool IsTargetValid;
        public IntPtr Pointer;
    }
    // The native attack state an attack fact is read from: the AI it was set up with, the attack index the game
    // wrote, and the agent the attack targets. Nothing here publishes; the observer only reads.
    public sealed class ES_EnemyAttackBase
    {
        public EnemyAI? m_ai;
        public Agents.Agent? m_attackTarget;
        public int m_lastAttackIndex;
        public IntPtr Pointer = new IntPtr(220);
    }
    public sealed class EnemyBehaviour
    {
        public EnemyAI? m_ai;
        public EB_States m_currentStateName = EB_States.Patrolling;
        public IntPtr Pointer;
    }
    public sealed class EnemyDetection
    {
        public EnemyAI? m_ai;
        public float m_biggestDetectionBuildup;
        public IntPtr Pointer;
    }
    public sealed class EnemyLocomotion
    {
        public EnemyAgent? m_agent;
        public ES_StateEnum CurrentStateEnum;
        public ES_ScoutScream ScoutScream = null!;
    }
    public sealed class ES_ScoutScream { public ScoutScreamState m_state = ScoutScreamState.Setup; }
    public sealed class EnemyAbilities
    {
        public EnemyAgent? m_agent;
        public Agents.AgentAbility ActiveAbility;
        public bool CanTriggerAbilities = true;
    }
}
public sealed class Dam_EnemyDamageLimb
{
    public IntPtr Pointer = new IntPtr(210);
    public Dam_EnemyDamageBase m_base = null!;
    public int m_limbID;
    public bool Destroyed;
    public Action? OnDestroyedRead;
    public bool IsDestroyed { get { OnDestroyedRead?.Invoke(); return Destroyed; } set { Destroyed = value; } }
    public void DestroyLimb() { IsDestroyed = true; }
}
public sealed class Dam_EnemyDamageBase
{
    public Dam_EnemyDamageLimb[] DamageLimbs = System.Array.Empty<Dam_EnemyDamageLimb>();
    public Enemies.EnemyAgent Owner = null!;
    public IntPtr Pointer;
    public bool IsSetup = true;
    public float Health = 50, HealthMax = 100;
    public int Sends;
    public Action<float>? Commit;
    public void SendSetHealth(float health) { Sends++; if (Commit == null) Health = health; else Commit(health); }
    // The stand-in for a damage entry point: a test decides the outcome it must model (a landed hit, a hit the
    // receiver rules nullify, or a rejected one), while the call itself is recorded the way Sends records a heal.
    public int Attacks;
    public Action<Dam_EnemyDamageBase>? OnBulletDamage;
    public void BulletDamage(float dam, Agents.Agent? sourceAgent, UnityEngine.Vector3 position, UnityEngine.Vector3 direction,
        UnityEngine.Vector3 normal, bool allowDirectionalBonus, int limbID, float staggerMulti, float precisionMulti, uint gearCategoryId)
    { Attacks++; OnBulletDamage?.Invoke(this); }
}
