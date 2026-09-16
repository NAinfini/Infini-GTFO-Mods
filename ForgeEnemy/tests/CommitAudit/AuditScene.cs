using System.Linq;
using System.Reflection;
using Enemies;
using ForgeRuntime.Framework;
using ForgeEnemy.Native;

internal sealed class AuditScene
{
    internal static bool UseManagedBoundary;
    internal readonly RuntimeKernel Kernel;
    internal readonly EnemyModule Module;
    internal readonly EnemyAgent Actor;
    internal readonly EntityReference Reference;
    internal bool Allowed = true;
    internal readonly List<string> Messages = new();
    /// <summary>Commands dispatched by the damage_applied -> record plan of a subscribed scene.</summary>
    internal readonly List<CommandContext> Records = new();
    private ushort _nextGlobalId = 8;
    internal AuditScene(bool subscribe = false)
    {
        Kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"), new RuntimeLimits());
        Kernel.BeginWorld(1); Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        LocalPlan.OwnMounts(Kernel);
        Module = new EnemyModule(Kernel, RuntimeLogLevel.Off, () => Allowed, Messages.Add);
        Actor = CreateEnemy(); Reference = Module.TrackSpawn(Actor);
        if (!subscribe) return;
        Kernel.RegisterModule(LocalPlan.Recorder(Records.Add), RuntimeLogLevel.Off);
        LocalPlan.Load(Kernel, LocalPlan.Build(Kernel, "test.audit.damage", EnemyModule.DamageBinding, LocalPlan.RecordBinding, ("target", "target")));
    }
    internal static EnemyAgent CreateEnemy(long pointer = 10, ushort globalId = 7)
    {
        var actor = new EnemyAgent { GlobalID = globalId, Pointer = new IntPtr(pointer) };
        actor.Damage = new Dam_EnemyDamageBase { Owner = actor, Pointer = new IntPtr(pointer + 100) };
        return actor;
    }
    /// <summary>Spawns and tracks a second (or further) enemy for multi-target heal cases. Each call uses a
    /// fresh GlobalID so the module's entity table keys the new actor independently of `Actor`.</summary>
    internal (EnemyAgent Actor, EntityReference Reference) SpawnActor(long pointer = 20)
    {
        var actor = CreateEnemy(pointer, _nextGlobalId++);
        return (actor, Module.TrackSpawn(actor));
    }
    /// <summary>Drives one heal command over one or more targets. `policy` mirrors the overheal_policy parameter
    /// (clamp/discard/overheal); "overheal" is structurally unsupported and is rejected the same way in both
    /// boundary implementations.</summary>
    internal CommandResult Heal(EntityReference? target = null, double amount = 5, double? cap = null, string policy = "clamp")
        => Heal(new[] { target ?? Reference }, amount, cap, policy);
    internal CommandResult Heal(EntityReference[] targets, double amount = 5, double? cap = null, string policy = "clamp")
    {
        if (policy == "overheal") return CommandResult.Rejected("overheal-unsupported");
        if (UseManagedBoundary)
        {
            var boundary = new ForgeEnemy.Receivers.EnemyHealthCommit(() => Allowed && SNetwork.SNet.IsMaster,
                ReadForBoundary, (value, maximum) =>
                { var encoded = new SNetwork.SFloat16(); encoded.Set(value, maximum); return encoded.Get(maximum); },
                SubmitForBoundary, EnemyModule.HealthChangedBinding);
            return boundary.Execute(targets, amount, cap, policy == "discard");
        }
        var origin = new RuntimeEvent("audit.damage", EnemyModule.DamageBinding, Kernel.WorldEpoch, 0, "audit.scope", RuntimeJson.EmptyObject);
        var inputs = new Dictionary<string, object?> { ["targets"] = targets, ["source"] = Reference, ["amount"] = amount };
        if (cap.HasValue) inputs["cap"] = cap.Value;
        var context = (CommandContext)Activator.CreateInstance(typeof(CommandContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { origin, 0L, "audit.command", "audit.plan", "audit.resource", "1", "audit.node",
                RuntimeJson.From(new { overheal_policy = policy }), RuntimeJson.From(inputs), true }, null)!;
        return (CommandResult)typeof(EnemyModule).GetMethod("Heal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Module, new object[] { context })!;
    }
    /// <summary>Resolves the tracked EnemyAgent behind a reference, the same way EnemyModule.Resolve does
    /// internally, so the managed boundary reads/writes the correct actor for each target in a multi-target call.
    /// The entry is a private nested type with internal members, so both bindings are needed to read it.</summary>
    private EnemyAgent? ResolveEnemy(EntityReference target)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var entry = typeof(EnemyModule).GetMethod("Resolve", Flags)!.Invoke(Module, new object[] { target });
        return entry == null ? null : (EnemyAgent)entry.GetType().GetProperty("Enemy", Flags)!.GetValue(entry)!;
    }
    private ForgeEnemy.Receivers.EnemyHealthSnapshot? ReadForBoundary(EntityReference target)
    {
        var enemy = ResolveEnemy(target);
        var d = enemy?.Damage;
        return d != null ? new(target, d.Pointer, d.IsSetup, enemy!.Alive, d.Health, d.HealthMax) : null;
    }
    private void SubmitForBoundary(ForgeEnemy.Receivers.EnemyHealthSnapshot expected, float value)
    {
        var enemy = ResolveEnemy(expected.Target);
        if (enemy == null || enemy.Damage.Pointer != expected.Receiver) throw new InvalidOperationException("Changed test receiver.");
        enemy.Damage.SendSetHealth(value);
    }
    internal static string Describe(CommandResult result, int sends)
        => $"status={result.Status}; commit={result.CommitState}; code={result.Code}; sends={sends}; output={result.Outputs}";
    /// <summary>First row of the heal result's `results` array.</summary>
    internal static System.Text.Json.JsonElement Row(CommandResult result)
        => result.Outputs.GetProperty("results").EnumerateArray().First();
    /// <summary>All rows of the heal result's `results` array, in target order.</summary>
    internal static System.Text.Json.JsonElement[] Rows(CommandResult result)
        => result.Outputs.GetProperty("results").EnumerateArray().ToArray();
    /// <summary>Reflects into ForgeRuntime.Framework.CommandResultRules.TryValidate (internal, cross-assembly)
    /// to check that a handler's raw result would be accepted as-is, i.e. would never be rewritten by
    /// RuntimeKernel.NormalizeInvokedResult into FailedUnknown("invalid-handler-result").</summary>
    internal static bool IsValidHandlerResult(CommandResult result, out string violation)
    {
        var rulesType = typeof(RuntimeKernel).Assembly.GetType("ForgeRuntime.Framework.CommandResultRules")!;
        var method = rulesType.GetMethod("TryValidate", BindingFlags.Static | BindingFlags.NonPublic)!;
        var args = new object?[] { result, null };
        bool ok = (bool)method.Invoke(null, args)!;
        violation = (string?)args[1] ?? "";
        return ok;
    }
    internal void RequireUnknown(CommandResult result, Dam_EnemyDamageBase original)
    {
        var row = Row(result);
        Audit.Require(result.Status == "failed" && result.CommitState == CommitStates.Unknown && original.Sends == 1
            && result.Facts.Count == 0 && (!row.TryGetProperty("actualAmount", out var actual)
                || actual.ValueKind == System.Text.Json.JsonValueKind.Null), Describe(result, original.Sends));
    }
}
