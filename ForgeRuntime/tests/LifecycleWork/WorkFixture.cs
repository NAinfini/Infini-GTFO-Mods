using System.Text.Json;
using ForgeRuntime.Framework;

// Real website plan and production SDK; only the native action/resolver are managed test doubles.
internal sealed class WorkFixture
{
    internal const string StateId = "test.lifecycle.state.contribution";
    internal readonly RuntimeKernel Kernel;
    internal readonly RuntimeModuleHandle Owner;
    internal readonly string Plan;
    internal readonly string[] Grants;
    internal readonly string Trigger;
    internal int Commits;
    internal Action<CommandContext>? OnCommit;
    internal EntityReference Target => new("gtfo.enemy:7", Kernel.WorldEpoch, 1);
    internal static string FixtureRoot = "";
    internal WorkFixture(bool ready = true)
    {
        var cases = Read("cases.json");
        var manifest = Read(cases.GetProperty("manifest").GetString()!);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Kernel = new(JsonSerializer.Deserialize<RuntimeIdentity>(manifest.GetProperty("runtime"), options)!);
        Kernel.BeginWorld(1);
        Plan = File.ReadAllText(Path.Combine(FixtureRoot, cases.GetProperty("validPlan").GetString()!));
        var plan = RuntimeJson.Parse(Plan);
        Trigger = plan.GetProperty("entrypoints")[0].GetProperty("bindingId").GetString()!;
        Grants = Read(cases.GetProperty("compileOptions").GetString()!).GetProperty("grantedPermissions")
            .EnumerateArray().Select(p => p.GetString()!).ToArray();
        var registry = manifest.GetProperty("registry");
        var providers = registry.GetProperty("providers").EnumerateArray().OrderBy(p =>
            registry.GetProperty("capabilities").EnumerateArray().Any(c => c.GetProperty("owner").GetString()
                == p.GetProperty("id").GetString()) ? 0 : 1);
        RuntimeModuleHandle? owner = null;
        foreach (var provider in providers)
        {
            var id = provider.GetProperty("id").GetString()!;
            var bindings = registry.GetProperty("bindings").EnumerateArray()
                .Where(b => b.GetProperty("providerId").GetString() == id).ToArray();
            var capabilities = registry.GetProperty("capabilities").EnumerateArray()
                .Where(c => c.GetProperty("owner").GetString() == id).ToArray();
            var support = manifest.GetProperty("bindingSupport").EnumerateArray()
                .Where(s => bindings.Any(b => b.GetProperty("id").GetString() == s.GetProperty("bindingId").GetString()))
                .Select(s => JsonSerializer.Deserialize<BindingSupport>(s, options)!).ToArray();
            var handlers = bindings.Where(b => b.GetProperty("role").GetString() == "execute")
                .ToDictionary(b => b.GetProperty("handler").GetString()!, _ => (CommandHandler)(ctx =>
                { Commits++; OnCommit?.Invoke(ctx); return CommandResult.Succeeded(RuntimeJson.EmptyObject); }));
            var handle = Kernel.RegisterModule(new(RuntimeKernel.ApiVersion,
                RuntimeJson.From(new { providers = new[] { provider }, capabilities, bindings }).GetRawText(),
                handlers, support, handlers.Count == 0 ? null : new Dictionary<string, Func<EntityReference, bool>>
                { ["gtfo.enemy"] = r => r == Target }));
            if (bindings.Any(b => b.GetProperty("id").GetString() == Trigger)) owner = handle;
        }
        Owner = owner ?? throw new InvalidDataException("Fixture has no trigger owner.");
        RegisterState();
        if (ready) { Kernel.StartRuntime(() => Kernel.LoadPlan(Plan, Grants)); Kernel.Advance(0, true); }
    }
    private static JsonElement Read(string name) => RuntimeJson.Parse(File.ReadAllText(Path.Combine(FixtureRoot, name)));
    private void RegisterState()
    {
        const string id = "test.lifecycle.state";
        Kernel.RegisterModule(new(RuntimeKernel.ApiVersion, RuntimeJson.From(new {
            providers = new[] { new { id, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new[] { new { id = StateId, owner = id, kind = "state", version = "1.0.0",
                label = "Lifecycle cleanup test contribution", parameters = new { valueType = "numeric-contribution" } } },
            bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>()));
    }
    internal RuntimeEvent Event(string id, long tick = 10) => new(id, Trigger, Kernel.WorldEpoch, tick,
        "test.lifecycle.scope", RuntimeJson.From(new { target = Target, actual_damage = 10 }), Target);
    internal RuntimeScheduleHandle Schedule(string id = "test.timer")
    {
        var result = Owner.Schedule(Event(id, Math.Max(0, Kernel.CurrentTick)),
            new(10, FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 3));
        return result.Handle ?? throw new Exception("Schedule setup failed: " + result.Code);
    }
    internal NumericLeaseRequest LeaseRequest(string id = "test.lease") => new(id, StateId, "1.0.0",
        Target, "test.lifecycle.scope", "test.stack", Target, 100, Additive: 5);
    internal RuntimeStateLeaseHandle Lease(string id = "test.lease")
    {
        var result = Owner.AcquireNumericLease(LeaseRequest(id));
        return result.Handle ?? throw new Exception("Lease setup failed: " + result.Code);
    }
}
