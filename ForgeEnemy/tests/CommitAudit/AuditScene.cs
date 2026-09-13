using System.Reflection;
using Enemies;
using ForgeRuntime.Framework;
using ForgeEnemy.Native;

internal sealed class AuditScene
{
    internal static bool UseManagedBoundary;
    internal static string Fixtures = "";
    internal readonly RuntimeKernel Kernel;
    internal readonly EnemyModule Module;
    internal readonly EnemyAgent Actor;
    internal readonly EntityReference Reference;
    internal bool Allowed = true;
    internal readonly List<string> Messages = new();
    internal AuditScene(bool subscribe = false)
    {
        Kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", "1.0.0", "20403457"), new RuntimeLimits());
        Kernel.BeginWorld(1); Kernel.RegisterModule(CombatContracts.Module());
        Module = new EnemyModule(Kernel, () => Allowed, Messages.Add);
        Actor = CreateEnemy(); Reference = Module.TrackSpawn(Actor);
        if (subscribe) Kernel.LoadPlan(File.ReadAllText(Path.Combine(Fixtures, "native-heal.plan.json")),
            new[] { "gtfo.enemy.health.read", "gtfo.enemy.health.write" });
    }
    internal static EnemyAgent CreateEnemy(long pointer = 10)
    {
        var actor = new EnemyAgent { GlobalID = 7, Pointer = new IntPtr(pointer) };
        actor.Damage = new Dam_EnemyDamageBase { Owner = actor, Pointer = new IntPtr(pointer + 100) };
        return actor;
    }
    internal CommandResult Heal(EntityReference? target = null, double amount = 5)
    {
        if (UseManagedBoundary)
        {
            var boundary = new ForgeEnemy.Receivers.EnemyHealthCommit(() => Allowed && SNetwork.SNet.IsMaster,
                ReadForBoundary, (value, maximum) =>
                { var encoded = new SNetwork.SFloat16(); encoded.Set(value, maximum); return encoded.Get(maximum); },
                (expected, value) =>
                { if (Actor.Damage.Pointer != expected.Receiver) throw new InvalidOperationException("Changed test receiver."); Actor.Damage.SendSetHealth(value); },
                EnemyModule.HealthChangedBinding);
            return boundary.Execute(target ?? Reference, amount);
        }
        var origin = new RuntimeEvent("audit.damage", EnemyModule.DamageBinding, Kernel.WorldEpoch, 0, "audit.scope", RuntimeJson.EmptyObject);
        var context = (CommandContext)Activator.CreateInstance(typeof(CommandContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { origin, 0L, "audit.command", "audit.plan", "audit.resource", "1", "audit.node",
                RuntimeJson.From(new { amount }), RuntimeJson.From(new { target = target ?? Reference }) }, null)!;
        return (CommandResult)typeof(EnemyModule).GetMethod("Heal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Module, new object[] { context })!;
    }
    private ForgeEnemy.Receivers.EnemyHealthSnapshot? ReadForBoundary(EntityReference target)
    {
        bool valid = (bool)typeof(EnemyModule).GetMethod("IsCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Module, new object[] { target })!;
        var d = Actor.Damage;
        return valid && d != null ? new(target, d.Pointer, d.IsSetup, Actor.Alive, d.Health, d.HealthMax) : null;
    }
    internal static string Describe(CommandResult result, int sends)
        => $"status={result.Status}; commit={result.CommitState}; code={result.Code}; sends={sends}; output={result.Outputs}";
    internal void RequireUnknown(CommandResult result, Dam_EnemyDamageBase original)
    {
        Audit.Require(result.Status == "failed" && result.CommitState == CommitStates.Unknown && original.Sends == 1
            && result.Facts.Count == 0 && (!result.Outputs.TryGetProperty("actualAmount", out var actual)
                || actual.ValueKind == System.Text.Json.JsonValueKind.Null), Describe(result, original.Sends));
    }
}
