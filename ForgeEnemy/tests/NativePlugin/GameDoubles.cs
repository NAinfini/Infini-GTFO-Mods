using ForgeRuntime.Framework;
namespace ForgeRuntime
{
    public enum RuntimeMode { Off, Play, Authoring }
    public static class Plugin
    {
        public static RuntimeMode ConfiguredMode;
        public static RuntimeKernel? Runtime;
        public static bool CanExecuteGameplay;
    }
}
namespace Agents
{
    public enum AgentAbility { None = 0, Primary = 1, Secondary = 2 }
}
namespace Enemies
{
    public sealed class EnemyAgent
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
    }
    public sealed class EnemySync
    {
        public EnemyAgent? m_agent;
        public void OnSpawn() { }
        public void OnDespawn() { }
    }
    public enum ES_StateEnum : byte { None, StandStill, PathMove, Dead, Hibernate, ScoutScream, ShooterAttack }
    public sealed class EnemyAI { public EnemyAgent? m_enemyAgent; }
    public sealed class EnemyLocomotion
    {
        public EnemyAgent? m_agent;
        private ES_StateEnum _state;
        public Action? OnStateRead;
        public ES_StateEnum CurrentStateEnum { get { OnStateRead?.Invoke(); return _state; } set { _state = value; } }
    }
    public sealed class EnemyAbilities
    {
        public EnemyAgent? m_agent;
        private Agents.AgentAbility _active;
        public Action? OnAbilityRead;
        public Agents.AgentAbility ActiveAbility { get { OnAbilityRead?.Invoke(); return _active; } set { _active = value; } }
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
    public IntPtr Pointer = new(110);
    public bool IsSetup = true;
    public float Health = 50, HealthMax = 100;
    public int Sends;
    public Action<float>? Commit;
    public void SendSetHealth(float amount) { Sends++; if (Commit == null) Health = amount; else Commit(amount); }
    public bool ProcessReceivedDamage() => true;
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
}
