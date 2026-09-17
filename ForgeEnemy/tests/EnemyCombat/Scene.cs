using System.Reflection;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>The suite's world: one started kernel with the real `EnemyModule` registered, a tracked enemy whose
/// hitreact state machine and attack states are whatever the case needs, and a way to dispatch one row through
/// a command context built the way the kernel builds one.
///
/// The context is constructed by reflection only because its constructor is internal to the Framework assembly;
/// every value handed to it is built with the SDK's own public helpers, so the handlers see the shapes a real
/// dispatch hands them.</summary>
internal sealed class Scene : IDisposable
{
    private static readonly ConstructorInfo ContextConstructor = typeof(CommandContext)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 10);

    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly EnemyModule Module;
    internal readonly EnemyAgent Enemy;
    internal readonly EnemyLocomotion Locomotion;
    internal readonly ES_HitreactBase Hitreact;
    internal readonly EntityReference Reference;
    /// <summary>The acting entity every dispatch carries as the `source` role. It is a tracked life of its own
    /// rather than the recipient, because the role is a required entity reference and the kernel would have
    /// refused a dispatch without one; no handler here reads the native object behind it.</summary>
    internal readonly EntityReference ActorReference;
    internal readonly List<string> Messages = new();
    internal readonly RuntimeModuleHandle RegistrationHandle;
    internal bool Allowed = true;

    internal Scene(bool start = true)
    {
        Kernel.BeginWorld(1);
        // The runtime's own declaration modules, registered the way the host registers them.
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        // The provider under test also registers bindings for the attack-instance rows a sibling slice owns. Those
        // are `forge.trigger.*` rows and the trigger module above declares them, so this scene declares nothing on
        // their behalf: a module may only *own* its own capabilities, and these belong to `forge.contract.trigger`.
        // The module under test registers the provider's own declaration in its constructor, so the kernel resolves
        // every port name the handlers use without this slice editing the provider's registration.
        Module = new EnemyModule(Kernel, RuntimeLogLevel.Off, () => Allowed, Messages.Add);
        RegistrationHandle = Kernel.RegisterModule(EnemyCombatRegistration.Module(Module), RuntimeLogLevel.Off);
        var locomotion = NewLocomotion();
        Locomotion = locomotion;
        Hitreact = locomotion.Hitreact;
        Enemy = NewEnemy(locomotion);
        Reference = Module.TrackSpawn(Enemy);
        ActorReference = Module.TrackSpawn(NewEnemy(NewLocomotion(36), id: 9, pointer: 60));
        if (start) Kernel.StartRuntime(static () => { });
    }

    /// <summary>One locomotion with its own hitreact state machine, which is what makes two enemies in one world
    /// independently writable: the state machine an interrupt writes through belongs to the enemy's locomotion and
    /// never to the world.</summary>
    internal static EnemyLocomotion NewLocomotion(long pointer = 16)
        => new() { Hitreact = new ES_HitreactBase { Pointer = new(pointer) } };

    /// <summary>One attack state standing in the named attack field of one locomotion, mid-attack or not. The
    /// four field names are `EnemyLocomotion`'s own, so a case proves the interrupt row asks every one of them and
    /// reads both in-flight questions. The state is returned so a case can make its predicates throw.</summary>
    internal static ES_EnemyAttackBase SetAttack(EnemyLocomotion locomotion, string field,
        bool performing = false, bool charging = false)
    {
        switch (field)
        {
            case "striker":
                var striker = new ES_StrikerAttack { Performing = performing, Charging = charging };
                locomotion.StrikerAttack = striker; return striker;
            case "tank":
                var tank = new ES_TankAttack { Performing = performing, Charging = charging };
                locomotion.TankAttack = tank; return tank;
            case "tank_multi":
                var multi = new ES_TankMultiTargetAttack { Performing = performing, Charging = charging };
                locomotion.TankMultiTargetAttack = multi; return multi;
            case "shooter":
                var shooter = new ES_ShooterAttack { Performing = performing, Charging = charging };
                locomotion.ShooterAttack = shooter; return shooter;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown attack field.");
        }
    }

    /// <summary>One enemy wired the way the production read expects: a damage receiver whose owner points back
    /// at the enemy, and two limbs with the ids `m_limbID` declares.</summary>
    internal static EnemyAgent NewEnemy(EnemyLocomotion locomotion, ushort id = 7, long pointer = 10)
    {
        var actor = new EnemyAgent { GlobalID = id, Pointer = new(pointer), Locomotion = locomotion };
        actor.Damage = new() { Owner = actor, Pointer = new(pointer + 100) };
        actor.Damage.DamageLimbs = Enumerable.Range(0, 2).Select(i => new Dam_EnemyDamageLimb
        {
            m_base = actor.Damage,
            m_limbID = i,
            Pointer = new(pointer + 200 + i)
        }).ToArray();
        locomotion.m_agent = actor;
        return actor;
    }

    /// <summary>One `stagger` dispatch whose inputs and parameters are exactly the frame shapes the capability
    /// declares: `targets` is a set of entity references, `source` is the attacker role, and the two structural
    /// parameters travel as their member index.</summary>
    internal CommandResult DispatchStagger(int reaction, int immunity, params EntityReference[] targets)
        => Module.Stagger(Context(EnemyCombatContract.StaggerBinding,
            RuntimeJson.From(new { targets, source = ActorReference }),
            RuntimeJson.From(new { reaction, immunity_policy = immunity })));

    /// <summary>One `attack_interrupt` dispatch over the named enemy recipients. The row carries no structural
    /// parameter, so the dispatch submits an empty parameter object — the same shape a plan step with no structural
    /// member hands a handler.</summary>
    internal CommandResult DispatchAttackInterrupt(params EntityReference[] enemies)
        => Module.AttackInterrupt(Context(EnemyCombatContract.AttackInterruptBinding,
            RuntimeJson.From(new { enemies, source = ActorReference }), RuntimeJson.EmptyObject));

    private CommandContext Context(string binding, System.Text.Json.JsonElement inputs,
        System.Text.Json.JsonElement parameters)
    {
        var origin = new RuntimeEvent("test.enemy-combat:" + Kernel.WorldEpoch, binding, Kernel.WorldEpoch,
            Math.Max(0, Kernel.CurrentTick), "gtfo.world:" + Kernel.WorldEpoch, RuntimeJson.EmptyObject);
        return (CommandContext)ContextConstructor.Invoke(new object?[]
        {
            origin, Math.Max(0, Kernel.CurrentTick), "test.command", "test.plan", "test.resource", "1", "Step",
            parameters, inputs, true
        });
    }

    public void Dispose()
    {
        RegistrationHandle.Dispose();
        Module.Dispose();
        Kernel.StopRuntime();
    }
}
