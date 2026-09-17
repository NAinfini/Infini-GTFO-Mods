using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

internal sealed class Scene : IDisposable
{
    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
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
        Kernel.BeginWorld(1); Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        RegisterVariables(Kernel);
        LocalPlan.OwnMounts(Kernel);
        Module = new(Kernel, RuntimeLogLevel.Off, () => Allowed, Messages.Add);
        Sink = Kernel.RegisterModule(LocalPlan.Recorder(c => { Records.Add(c); OnRecord?.Invoke(c); }), RuntimeLogLevel.Off);
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
    /// <summary>Fact -> record plan. death_started names its subject "enemy"; limb_broken keeps "target" and forwards the limb index.</summary>
    internal static LocalPlan.Plan FactPlan(RuntimeKernel kernel, string suffix) => suffix == "death_started"
        ? LocalPlan.Build(kernel, "test.lifecycle.death_started", EnemyModule.DeathStartedBinding, LocalPlan.RecordBinding, ("enemy", "target"))
        : LocalPlan.Build(kernel, "test.lifecycle.limb_broken", EnemyModule.LimbBrokenBinding, LocalPlan.RecordBinding, ("target", "target"), ("limb", "limb_id"));
    internal void Load(string suffix, RuntimeLimits? budget = null)
    {
        var plan = FactPlan(Kernel, suffix);
        LocalPlan.Load(Kernel, budget == null ? plan : plan.WithLimits(budget));
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

    /// <summary>The runtime's own variable family, stood up the way the host stands it up.
    ///
    /// The write capability a variable step pins belongs to a built-in provider, not to a domain package: its
    /// factory and the built-in registration entry point are both internal to the SDK assembly, and a kernel
    /// without it refuses a plan that writes a variable with `capability-unavailable` before the write ever
    /// happens. Every other contract this suite needs is a public `Module()`; these two are reached directly
    /// because this test assembly is named in the SDK's `InternalsVisibleTo`.</summary>
    private static void RegisterVariables(RuntimeKernel kernel) => kernel.RegisterBuiltinModule(VariableContracts.Module());
}
