using System.Text.Json;
using ForgeRuntime.Framework;

internal static class PlanBoundaryTests
{
    internal static void Run(JsonElement vectors)
    {
        var fixture = vectors.GetProperty("v1"); var calls = 0;
        RuntimeModule Module(JsonElement seed) => new("1.0.0", seed.GetRawText(),
            new Dictionary<string, CommandHandler>
            {
                ["record"] = _ => { calls++; return CommandResult.Succeeded(RuntimeJson.From(new { a = 1, b = 2 })); }
            }, seed.GetProperty("bindings").EnumerateArray().Select(b => new BindingSupport(
                b.GetProperty("id").GetString()!, "implementation-only", Array.Empty<string>())).ToArray());
        Suite.Test("v1-explicit-variable-refusal", () =>
        {
            var kernel = Suite.Kernel();
            kernel.RegisterModule(Module(fixture.GetProperty("variableSeed")));
            Suite.Reject(() => kernel.LoadPlan(fixture.GetProperty("plan").GetRawText(), Array.Empty<string>()),
                "unsupported-variable-ports");
            Suite.Check(kernel.LoadedPlans == 0 && kernel.QueuedEvents == 0 && calls == 0,
                "Rejected plan created execution or side effects.");
            Suite.Check(!kernel.HasSubscribers("test.graphports.binding.start"), "Rejected plan left a subscription.");
        });
        Suite.Test("unchanged-v1-executes-once", () =>
        {
            var kernel = Suite.Kernel(); var handle = kernel.RegisterModule(Module(fixture.GetProperty("fixedSeed")));
            kernel.BeginWorld(1);
            kernel.StartRuntime(() => kernel.LoadPlan(fixture.GetProperty("plan").GetRawText(), Array.Empty<string>()));
            var evt = new RuntimeEvent("v1-guard-event", "test.graphports.binding.start", 1, 0,
                "test-scope", RuntimeJson.EmptyObject);
            Suite.Check(handle.Publish(evt).Status == "queued", "Unchanged v1 could not queue.");
            var tick = kernel.Advance(0, true);
            Suite.Check(tick.CommandsExecuted == 1 && calls == 1, "Unchanged v1 did not execute exactly once.");
            Suite.Check(tick.Commands[0].Result.Status == "succeeded", "Unchanged v1 command failed.");
            Suite.Check(handle.Publish(evt).Status == "duplicate", "Duplicate event was admitted.");
            kernel.Advance(0, true);
            Suite.Check(calls == 1, "Same-tick repeat duplicated execution.");
        });
    }
}
