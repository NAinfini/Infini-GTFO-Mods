using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

internal static class PlanBoundaryTests
{
    const string Provider = "test.graphports", Trigger = "test.graphports.binding.start";
    /// <summary>The mount reference every vector plan carries, answered by the owner the module below registers: a
    /// plan's kind has to have a registered matcher before the plan loads, and a level target names no entity, so the
    /// matcher is a scope one.</summary>
    const string Mount = "graph-port-expansion";

    internal static void Run(JsonElement vectors)
    {
        var fixture = vectors.GetProperty("plans"); var calls = 0;
        RuntimeModule Module(JsonElement seed) => new(RuntimeKernel.ApiVersion, seed.GetRawText(),
            new Dictionary<string, CommandHandler>
            {
                ["record"] = _ => { calls++; return CommandResult.Succeeded(RuntimeJson.From(new { value_1 = 1, value_2 = 2 })); }
            }, seed.GetProperty("bindings").EnumerateArray().Select(b => new BindingSupport(
                b.GetProperty("id").GetString()!, "implementation-only", Array.Empty<string>())).ToArray(),
            new Dictionary<string, Func<EntityReference, bool>> { [Provider] = r => r.Id == Provider + ":1" && r.LifeEpoch == 1 })
        {
            // The port-group capability expands its outputs per plan, so the double names no port: its shape is empty.
            Shapes = new Dictionary<string, HandlerShape> { ["record"] = new HandlerShape() },
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
            {
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == Mount)
            }
        };
        void Refused(string seed, string plan, string name)
        {
            var kernel = Suite.Kernel();
            kernel.RegisterModule(Module(fixture.GetProperty(seed)), RuntimeLogLevel.Off);
            Suite.Reject(() => kernel.LoadPlan(plan), "layout-mismatch");
            Suite.Check(kernel.LoadedPlans == 0 && kernel.QueuedEvents == 0 && calls == 0,
                name + ": rejected plan created execution or side effects.");
            Suite.Check(!kernel.HasSubscribers(Trigger), name + ": rejected plan left a subscription.");
        }
        // The website compiles both plans; C# re-derives every layout and must agree exactly.
        foreach (var (seed, plan) in new[] { ("fixedSeed", "fixedPlan"), ("variableSeed", "variablePlan") })
            Suite.Test(plan + "-executes-once", () =>
            {
                calls = 0;
                var kernel = Suite.Kernel(); var handle = kernel.RegisterModule(Module(fixture.GetProperty(seed)), RuntimeLogLevel.Off);
                kernel.BeginWorld(1);
                kernel.StartRuntime(() => kernel.LoadPlan(fixture.GetProperty(plan).GetRawText()));
                var evt = new RuntimeEvent(plan + "-event", Trigger, 1, 0, "test-scope",
                    RuntimeJson.From(new { target = new EntityReference(Provider + ":1", 1, 1) }));
                Suite.Check(handle.Publish(evt).Status == "queued", plan + " could not queue.");
                var tick = kernel.Advance(0, true);
                Suite.Check(tick.CommandsExecuted == 1 && calls == 1, plan + " did not execute exactly once.");
                Suite.Check(tick.Commands[0].Result.Status == "succeeded", plan + " command failed.");
                Suite.Check(handle.Publish(evt).Status == "duplicate", "Duplicate event was admitted.");
                kernel.Advance(0, true);
                Suite.Check(calls == 1, "Same-tick repeat duplicated execution.");
            });
        Suite.Test("expanded-port-group-layout", () =>
        {
            var kernel = Suite.Kernel(); kernel.RegisterModule(Module(fixture.GetProperty("variableSeed")), RuntimeLogLevel.Off);
            var outputs = kernel.ResolveGraphContract(Provider + ".action", "1.0.0", RuntimeJson.From(new { output_count = 3 }))
                .GetProperty("outputs").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToArray();
            Suite.Check(outputs.SequenceEqual(new[] { "value_1", "value_2", "value_3", "result" }),
                "Port group did not expand inside its declared block.");
            var layout = fixture.GetProperty("variablePlan").GetProperty("entrypoints")[0].GetProperty("steps")[0]
                .GetProperty("layout").GetProperty("outputs");
            Suite.Check(layout.GetArrayLength() == outputs.Length, "Compiled layout does not carry the expanded ports.");
        });
        Suite.Test("unexpanded-layout-refused", () =>
        {
            calls = 0;
            Refused("variableSeed", fixture.GetProperty("fixedPlan").GetRawText(), "unexpanded");
            Refused("fixedSeed", fixture.GetProperty("variablePlan").GetRawText(), "phantom-expansion");
        });
        Suite.Test("expansion-follows-constant", () =>
        {
            calls = 0;
            foreach (var count in new JsonNode?[] { JsonValue.Create(4), null })
            {
                var plan = JsonNode.Parse(fixture.GetProperty("variablePlan").GetRawText())!;
                plan["entrypoints"]![0]!["steps"]![0]!["layout"]!["constants"]![0] = count;
                Refused("variableSeed", plan.ToJsonString(), "count " + (count?.ToJsonString() ?? "default"));
            }
        });
    }
}
