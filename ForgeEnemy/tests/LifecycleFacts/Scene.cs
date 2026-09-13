using System.Text.Json.Nodes;
using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

internal sealed class Scene : IDisposable
{
    internal static string Fixtures = "";
    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.2.0", "1.0.0", "20403457"));
    internal readonly EnemyModule Module;
    internal readonly RuntimeModuleHandle Sink;
    internal readonly EnemyAgent Enemy;
    internal readonly EntityReference Ref;
    internal readonly List<CommandContext> Records = new();
    internal readonly List<string> Messages = new();
    internal Action<CommandContext>? OnRecord;
    internal bool Allowed = true;
    internal Dam_EnemyDamageLimb Limb => Enemy.Damage.DamageLimbs[0];
    internal Scene(bool load = true, bool start = true)
    {
        Kernel.BeginWorld(1); Kernel.RegisterModule(CombatContracts.Module());
        Module = new(Kernel, () => Allowed, Messages.Add);
        Sink = Kernel.RegisterModule(new("1.0.0", Fixture.SinkRegistry,
            new Dictionary<string, CommandHandler> { ["test.record"] = c =>
            { Records.Add(c); OnRecord?.Invoke(c); return CommandResult.Succeeded(RuntimeJson.EmptyObject); } },
            new[] { new BindingSupport("test.lifecycle.binding.record", "implementation-only", new[] { "test.record" }) }));
        Enemy = NewEnemy(); Ref = Module.TrackSpawn(Enemy);
        if (load) { Load("death_started"); Load("limb_broken"); }
        if (start) Kernel.StartRuntime(() => { });
    }
    internal static EnemyAgent NewEnemy(ushort id = 7, long pointer = 10)
    {
        var actor = new EnemyAgent { GlobalID = id, Pointer = new(pointer) };
        actor.Damage = new() { Owner = actor, Pointer = new(pointer + 100) };
        actor.Damage.DamageLimbs = Enumerable.Range(0, 2).Select(i => new Dam_EnemyDamageLimb
            { m_base = actor.Damage, m_limbID = i, Pointer = new(pointer + 200 + i) }).ToArray();
        return actor;
    }
    internal void Load(string suffix, IEnumerable<string>? grants = null)
    {
        var json = Fixture.Plan(Fixtures, suffix);
        Kernel.LoadPlan(json, grants ?? new[] { "gtfo.enemy.lifecycle.read", "gtfo.enemy.limbs.read", "test.record" });
    }
    internal void Die(EnemyAgent? actor = null)
    {
        actor ??= Enemy; var token = Module.BeforeDeath(actor); actor.Alive = false;
        Module.AfterDeath(actor, token);
    }
    internal void Break(Dam_EnemyDamageLimb? limb = null)
    {
        limb ??= Limb; var token = Module.BeforeLimbBreak(limb); limb.IsDestroyed = true;
        Module.AfterLimbBreak(limb, token);
    }
    internal TickResult Tick(long tick = 1) => Kernel.Advance(tick, true);
    public void Dispose() { Sink.Dispose(); Module.Dispose(); Kernel.StopRuntime(); }
}
