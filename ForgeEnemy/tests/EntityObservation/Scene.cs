using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;
using ForgeEnemy.Native;

internal sealed class Scene : IDisposable
{
    internal readonly RuntimeKernel Kernel;
    internal readonly EnemyModule Module;
    internal readonly EnemyAgent Enemy;
    internal readonly EntityReference Ref;
    internal bool Allowed = true;
    internal Func<EnemyAgent, EntityReference, RuntimeEntitySnapshot?> Reader = EnemyEntityObserver.Read;
    internal Scene(bool observe = true, bool start = true)
    {
        Kernel = new(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
        Kernel.BeginWorld(1); Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Module = new(Kernel, RuntimeLogLevel.Off, () => Allowed, _ => { }, observe ? (actor, reference) => Reader(actor, reference) : null);
        Enemy = NewEnemy(); Ref = Module.TrackSpawn(Enemy);
        if (start) Kernel.StartRuntime(() => { });
    }
    internal static EnemyAgent NewEnemy(ushort id = 7, long pointer = 10)
    {
        var actor = new EnemyAgent { GlobalID = id, Pointer = new(pointer) };
        actor.Damage = new() { Owner = actor, Pointer = new(pointer + 100) }; return actor;
    }
    internal RuntimeEntityQueryResult Query() => Kernel.InspectEntities(new[] { Ref });
    public void Dispose() => Module.Dispose();
}
