using System.Reflection;
using System.Text.Json;
using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>The suite's world: a started kernel with the provider's own registration in it, a tracked enemy whose
/// behaviour machine is whatever the case needs, and a way to dispatch one `phase_set` command through a command
/// context built the way the kernel builds one.
///
/// The handler is dispatched directly rather than through a loaded plan: a plan would have to be authored and
/// loaded to reach the row, which this slice does not do. The row itself is in the provider's registry — the real
/// `EnemyModule` in this scene registers it through `EnemyRegistration`, which composes the contract — so
/// `EvidenceCases` resolves the layout of the row this running module was registered with.
///
/// The context is constructed by reflection only because its constructor is internal to the Framework assembly;
/// every value handed to it (the entity set, the parameter object, the world epoch, the tick) is built with the
/// SDK's own public helpers, so the handler sees the shapes a real dispatch hands it.</summary>
internal sealed class Scene : IDisposable
{
    private static readonly ConstructorInfo ContextConstructor = typeof(CommandContext)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 10);

    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly EnemyModule Module;
    internal readonly EnemyAgent Enemy;
    internal readonly EntityReference Reference;
    internal readonly SquidBossBehaviour Boss = new() { Pointer = new IntPtr(12) };
    internal readonly List<string> Messages = new();
    internal bool Allowed = true;

    internal Scene(bool start = true)
    {
        Kernel.BeginWorld(1);
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        // The provider's own constructor is what runs here: one provider id, registered once, with the rows it
        // already carries. The suite never registers a second copy of the same provider.
        Module = new EnemyModule(Kernel, RuntimeLogLevel.Off, () => Allowed, Messages.Add);
        Enemy = NewEnemy();
        Reference = Module.TrackSpawn(Enemy);
        if (start) Kernel.StartRuntime(static () => { });
    }

    internal static EnemyAgent NewEnemy(ushort id = 7, long pointer = 10)
    {
        var actor = new EnemyAgent { GlobalID = id, Pointer = new(pointer) };
        actor.Damage = new() { Owner = actor, Pointer = new(pointer + 100) };
        actor.Damage.DamageLimbs = Enumerable.Range(0, 2).Select(i => new Dam_EnemyDamageLimb
            { m_base = actor.Damage, m_limbID = i, Pointer = new(pointer + 200 + i) }).ToArray();
        return actor;
    }

    /// <summary>Points the enemy's AI at a behaviour machine of the caller's choosing. The AI is wired exactly the
    /// way the production read expects: it names the enemy it belongs to, and the machine names the AI back.</summary>
    internal void UseBehaviour(EnemyBehaviour behaviour)
    {
        var ai = new EnemyAI { Pointer = new(11) };
        ai.m_enemyAgent = Enemy;
        behaviour.m_ai = ai;
        ai.m_behaviour = behaviour;
        ai.m_detection = new EnemyDetection { m_ai = ai };
        ai.m_locomotion = new EnemyLocomotion { m_agent = Enemy };
        Enemy.AI = ai;
    }

    /// <summary>One `phase_set` dispatch whose inputs and parameters are exactly the frame shapes the capability
    /// declares: `enemies` is a set of entity references, `phase` and `expected_phase` are integers, and
    /// `reset_policy` is the structural parameter's member index. The world epoch and tick come from the kernel,
    /// so a case that moves the world moves the context with it.</summary>
    internal CommandResult Dispatch(int phase, int expected, int resetPolicy, params EntityReference[] targets)
    {
        var inputs = RuntimeJson.From(new { enemies = targets, phase, expected_phase = expected });
        var parameters = RuntimeJson.From(new { reset_policy = resetPolicy });
        var origin = new RuntimeEvent("test.phase:" + Kernel.WorldEpoch, EnemyProfileContract.PhaseSetBinding,
            Kernel.WorldEpoch, Math.Max(0, Kernel.CurrentTick), "gtfo.world:" + Kernel.WorldEpoch, RuntimeJson.EmptyObject);
        var context = (CommandContext)ContextConstructor.Invoke(new object?[]
        {
            origin, Math.Max(0, Kernel.CurrentTick), "test.command", "test.plan", "test.resource", "1", "Step", parameters, inputs, true
        });
        return Module.PhaseSet(context);
    }

    internal CommandResult DispatchOne(int phase, int expected, int resetPolicy = 0)
        => Dispatch(phase, expected, resetPolicy, Reference);

    public void Dispose()
    {
        Module.Dispose();
        Kernel.StopRuntime();
    }
}

internal static class T
{
    internal sealed record CheckRow(string Id, bool Passed, string Detail);
    internal static readonly List<CheckRow> Rows = new();
    internal static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    internal static void Case(string id, Action test)
    {
        try { test(); Rows.Add(new(id, true, "passed")); }
        catch (Exception e) { Rows.Add(new(id, false, e.ToString())); Console.Error.WriteLine("FAIL " + id + ": " + e.Message); }
        finally { SNetwork.SNet.IsMaster = true; }
    }
    internal static JsonElement ResultRow(CommandResult result, int index)
        => result.Outputs.GetProperty("results").EnumerateArray().ElementAt(index);
    internal static string Code(CommandResult result, int index) => ResultRow(result, index).GetProperty("code").GetString()!;
    internal static string Status(CommandResult result, int index) => ResultRow(result, index).GetProperty("status").GetString()!;
}
