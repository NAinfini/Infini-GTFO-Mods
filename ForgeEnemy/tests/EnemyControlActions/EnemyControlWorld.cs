using System;
using System.Collections.Generic;
using System.Reflection;
using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using SNetwork;

/// <summary>The suite's world: one kernel, the provider partial's own entity table holding the enemy the cases
/// dispatch at, and one dispatch path that builds a command context the way the kernel builds one.
///
/// The three handlers are dispatched exactly as the contract hands them to a registration —
/// `EnemyControlContract.Handlers(Module)` — and the same rows and shapes are registered with the kernel, which
/// resolves every port name a handler declares against its capability graph before a case runs. A handler whose
/// name drifted from its binding, or whose shape names a port the row does not carry, fails this suite's
/// construction rather than a case.</summary>
internal sealed class EnemyControlWorld : IDisposable
{
    private static readonly ConstructorInfo ContextConstructor = typeof(CommandContext)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 10);

    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly EnemyModule Module;
    internal readonly Dictionary<string, CommandHandler> Handlers;
    internal readonly EnemyAgent Enemy;
    internal readonly EnemyAI Ai;
    internal readonly EnemyBehaviour Behaviour;
    internal readonly EnemyLocomotion Locomotion;
    internal readonly EntityReference Reference;
    internal readonly List<string> Messages = new();
    internal EnemyAgent? Second;
    internal EntityReference? SecondReference;
    internal bool Allowed = true;

    internal EnemyControlWorld(bool secondEnemy = false)
    {
        Kernel.BeginWorld(1);
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Module = new EnemyModule(Kernel, () => Allowed);
        Handlers = EnemyControlContract.Handlers(Module);
        Registration = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
            Handlers, EnemyControlContract.Support()) { Shapes = EnemyControlContract.Shapes() }, RuntimeLogLevel.Off);

        Enemy = NewEnemy();
        Ai = new EnemyAI { Pointer = new(11) };
        Behaviour = new EnemyBehaviour { m_ai = Ai };
        Locomotion = new EnemyLocomotion { m_agent = Enemy, HibernateWakeup = new ES_HibernateWakeUp() };
        Locomotion.HibernateWakeup.EnterWakeState = () => Locomotion.CurrentStateEnum = ES_StateEnum.HibernateWakeUp;
        Ai.m_enemyAgent = Enemy;
        Ai.m_behaviour = Behaviour;
        Ai.m_locomotion = Locomotion;
        Ai.m_detection = new EnemyDetection { m_ai = Ai };
        Enemy.AI = Ai;
        Reference = Module.Track(Enemy);
        if (secondEnemy)
        {
            Second = NewEnemy(id: 8, pointer: 20);
            var ai = new EnemyAI { Pointer = new(21), m_enemyAgent = Second };
            ai.m_behaviour = new EnemyBehaviour { m_ai = ai };
            var locomotion = new EnemyLocomotion { m_agent = Second, HibernateWakeup = new ES_HibernateWakeUp() };
            locomotion.HibernateWakeup.EnterWakeState = () => locomotion.CurrentStateEnum = ES_StateEnum.HibernateWakeUp;
            ai.m_locomotion = locomotion;
            ai.m_detection = new EnemyDetection { m_ai = ai };
            Second.AI = ai;
            SecondReference = Module.Track(Second);
        }
    }

    internal RuntimeModuleHandle Registration { get; }

    /// <summary>The three rows as one registry text, with this provider's id. The rows are the contract's own
    /// strings, so registering them here registers what the integration patch would register.</summary>
    private static string Registry()
    {
        var capabilities = string.Join(",", EnemyControlContract.CapabilityRowText());
        var bindings = string.Join(",", EnemyControlContract.BindingRowText());
        return "{\n  \"providers\": [\n    { \"id\": \"" + EnemyModule.ProviderId
            + "\",\n      \"kind\": \"native\",\n      \"version\": \"1.0.0\",\n      \"dependencies\": [] }\n  ],\n  \"capabilities\": [\n"
            + capabilities + "\n  ],\n  \"bindings\": [\n" + bindings + "\n  ]\n}";
    }

    internal static EnemyAgent NewEnemy(ushort id = 7, long pointer = 10)
    {
        var actor = new EnemyAgent { GlobalID = id, Pointer = new(pointer) };
        actor.Damage = new() { Owner = actor, Pointer = new(pointer + 100) };
        return actor;
    }

    /// <summary>The three declared optional inputs arrive as present zeros and nulls, in the two shapes a plan can
    /// produce: a literal the compiler wrote into the frame, and a port the plan left unwired.</summary>
    internal CommandResult Awaken(EntityReference[] targets, string policy = "immediate", double alertAmount = 0)
        => Dispatch(EnemyModule.AwakenHandler, new { enemies = targets, alert_amount = alertAmount, reason = "test" }, new { wake_policy = policy });

    internal CommandResult AwakenUnwired(EntityReference[] targets)
        => Dispatch(EnemyModule.AwakenHandler, new { enemies = targets, alert_amount = (double?)null, reason = "test" }, new { wake_policy = "immediate" });

    internal CommandResult Sleep(EntityReference[] targets, string policy = "immediate", string interrupt = "any", int duration = 0)
        => Dispatch(EnemyModule.SleepHandler, new { enemies = targets, duration }, new { sleep_policy = policy, interrupt_policy = interrupt });

    internal CommandResult MoveTo(EntityReference[] targets, double[] destination, double? speed = null)
        => Dispatch(EnemyModule.MoveToHandler, new { enemies = targets, destination, speed }, null);

    /// <summary>One dispatch through the handler the contract hands to a registration under this name, with a
    /// frame shaped the way the capability declares it: `enemies` is an entity set, an optional value arrives as a
    /// present null, a structural enum arrives as the member name the kernel resolves an index into, and the
    /// context is built by the Framework's own internal constructor.</summary>
    private CommandResult Dispatch(string handler, object inputs, object? parameters)
    {
        var origin = new RuntimeEvent("test.control:" + Kernel.WorldEpoch, EnemyModule.AwakenBinding,
            Kernel.WorldEpoch, Math.Max(0, Kernel.CurrentTick), "gtfo.world:" + Kernel.WorldEpoch, RuntimeJson.EmptyObject);
        var context = (CommandContext)ContextConstructor.Invoke(new object?[]
        {
            origin, Math.Max(0, Kernel.CurrentTick), "test.command", "test.plan", "test.resource", "1", "Step",
            RuntimeJson.From(parameters ?? new { }), RuntimeJson.From(inputs), true
        });
        return Handlers[handler](context);
    }

    public void Dispose()
    {
        Registration.Dispose();
        Kernel.StopRuntime();
    }
}
