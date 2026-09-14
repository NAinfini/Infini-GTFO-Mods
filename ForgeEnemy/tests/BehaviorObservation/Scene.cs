using Agents;
using Enemies;
using ForgeEnemy.Native;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;

internal sealed class Scene : IDisposable
{
    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly EnemyModule Module;
    internal readonly EnemyAgent Enemy;
    internal readonly EntityReference Ref;
    internal bool Allowed = true;

    internal Scene(bool start = true)
    {
        Kernel.BeginWorld(1); Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Module = new(Kernel, RuntimeLogLevel.Off, () => Allowed, _ => { });
        Enemy = NewEnemy(); Ref = Module.TrackSpawn(Enemy);
        if (start) Kernel.StartRuntime(() => { });
    }

    internal static EnemyAgent NewEnemy(ushort id = 7, long pointer = 10)
    {
        var actor = new EnemyAgent { GlobalID = id, Pointer = new(pointer) };
        actor.Damage = new() { Owner = actor, Pointer = new(pointer + 100) };
        actor.AI = new() { m_enemyAgent = actor };
        actor.Locomotion = new() { m_agent = actor, CurrentStateEnum = ES_StateEnum.PathMove };
        actor.Abilities = new() { m_agent = actor, ActiveAbility = AgentAbility.Primary, CanTriggerAbilities = true };
        actor.m_hasValidTarget = true;
        return actor;
    }

    internal EnemyBehaviorObserver.Snapshot? Read() => Module.ObserveBehavior(Ref);

    public void Dispose()
    {
        Module.Dispose();
        if (Kernel.StartupState != RuntimeStartupState.Stopped) Kernel.StopRuntime();
    }
}
