using System.Reflection;
using System.Text.Json;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>The suite's world: one started kernel with the foam row registered the way the integration patch will
/// register it — the contract's own capability row, binding row, support row and handler shape — the handler the
/// provider implements, a tracked enemy whose glue receiver is whatever the case needs, and a way to dispatch one
/// command through a command context built the way the kernel builds one.
///
/// The handler is dispatched directly rather than through a loaded plan. A plan would have to reach the row through
/// the provider's registry, and the row is not in it yet — putting it there is the integration patch this slice
/// reports. The contract row is still resolved by the kernel itself at registration, so a port the handler shape
/// cannot address fails here rather than at dispatch.
///
/// The context is constructed by reflection only because its constructor is internal to the Framework assembly;
/// every value handed to it is built with the SDK's own public helpers, so the handler sees the shapes a real
/// dispatch hands it.</summary>
internal sealed class Scene : IDisposable
{
    private static readonly ConstructorInfo ContextConstructor = typeof(CommandContext)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 10);

    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly RuntimeModuleHandle RowsHandle;
    internal readonly GlueActions Actions;
    internal readonly EnemyAgent Enemy;
    internal readonly EntityReference Reference;
    internal readonly List<string> Messages = new();
    internal bool Allowed = true;

    /// <summary>The entity table the resolver answers from, exactly as the provider's own answers for `gtfo.enemy`:
    /// a reference is current only while it still names the instance it was minted for.</summary>
    private readonly Dictionary<EntityReference, (EnemyAgent Enemy, IntPtr Pointer)> _live = new();
    private long _nextLife = 1;

    internal Scene(bool start = true)
    {
        // The glue entry points are native state, not per-suite state: a case gets a world where nothing has been
        // foamed yet, so no case can read another one's glue.
        ProjectileManager.NextSyncID = 1;
        ProjectileManager.ExpandAttached = 0f;
        ProjectileManager.MultiplierSeen = 0f;
        ProjectileManager.SubIndexSeen = int.MinValue;
        ProjectileManager.Spawns = 0;
        ProjectileManager.OnSpawnGlueOnEnemyAgent = null;
        Kernel.BeginWorld(1);
        _nextLife = 1;
        // The handler table is built over a callback, not over the action instance: the registration is what the
        // action itself needs in hand before it can exist, and nothing is dispatched in between.
        RowsHandle = Kernel.RegisterModule(GlueLocalRows.Module(context => Actions!.Foaming(context)), RuntimeLogLevel.Off);
        Actions = new GlueActions(RowsHandle, Resolve, () => Allowed && SNetwork.SNet.IsMaster, Kernel);
        Enemy = NewEnemy();
        Reference = Track(Enemy);
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

    internal EntityReference Track(EnemyAgent enemy)
    {
        var reference = new EntityReference("gtfo.enemy:" + enemy.GlobalID, Kernel.WorldEpoch, _nextLife++);
        _live[reference] = (enemy, enemy.Pointer);
        return reference;
    }

    /// <summary>Retires one life, which is what a despawn does: the reference stops resolving even though the
    /// native object behind it is still the same one.</summary>
    internal void Retire(EntityReference reference) => _live.Remove(reference);

    private GlueTarget? Resolve(EntityReference reference)
    {
        if (reference.WorldEpoch != Kernel.WorldEpoch) return null;
        if (!_live.TryGetValue(reference, out var entry)) return null;
        // Exactly what the provider's own answer carries: the life and the instance it still names. Whether that
        // instance holds a usable receiver is the row's own question, and it answers with its own code.
        return new GlueTarget(entry.Enemy, entry.Pointer, entry.Enemy.Damage);
    }

    /// <summary>One `foaming` dispatch whose inputs are exactly the frame shapes the capability declares: `targets`
    /// is a set of entity references, `volume`/`strength` are numbers and `duration` is a tick count.</summary>
    internal CommandResult Dispatch(double volume, double strength, long duration, params EntityReference[] targets)
    {
        var inputs = RuntimeJson.From(new { targets, source = Reference, volume, strength, duration });
        var origin = new RuntimeEvent("test.glue:" + Kernel.WorldEpoch, GlueLocalRows.TestFoamingBinding,
            Kernel.WorldEpoch, Math.Max(0, Kernel.CurrentTick), "gtfo.world:" + Kernel.WorldEpoch, RuntimeJson.EmptyObject);
        var context = (CommandContext)ContextConstructor.Invoke(new object?[]
        {
            origin, Math.Max(0, Kernel.CurrentTick), "test.command", "test.plan", "test.resource", "1", "Step",
            RuntimeJson.EmptyObject, inputs, true
        });
        return Actions.Foaming(context);
    }

    internal CommandResult DispatchOne(double volume, double strength, long duration = 0)
        => Dispatch(volume, strength, duration, Reference);

    /// <summary>Moves the simulation forward, which is what publishes the framework's own tick notification the
    /// ledger expires through.</summary>
    internal void Advance(long tick) => Kernel.Advance(tick, true);

    public void Dispose()
    {
        Actions.Dispose();
        RowsHandle.Dispose();
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
