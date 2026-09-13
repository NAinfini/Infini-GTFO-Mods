using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

internal static class ResolutionTests
{
    internal static void Run(JsonElement vectors)
    {
        foreach (var row in vectors.GetProperty("resolutions").EnumerateArray())
        {
            Suite.Test("resolve:" + row.GetProperty("name").GetString(), () =>
            {
                var kernel = Suite.Kernel(); kernel.RegisterModule(Suite.Module(row.GetProperty("seed")));
                var before = kernel.ExportManifest();
                JsonElement Resolve() => kernel.ResolveGraphContract(row.GetProperty("id").GetString()!,
                    row.GetProperty("version").GetString()!, row.GetProperty("parameters"));
                if (row.GetProperty("accepted").GetBoolean())
                {
                    var actual = Resolve();
                    Suite.Check(Suite.Equal(actual, row.GetProperty("expected")), "C#/TypeScript port layout differs.");
                    Suite.Check(Suite.Equal(actual, Resolve()), "Repeated resolution changed its output.");
                    Suite.Check(actual.TryGetProperty("variadic", out _), "Variable metadata was silently stripped.");
                }
                else Suite.Reject(() => Resolve(), "invalid-integer");
                Suite.Check(kernel.ExportManifest() == before, "Resolution mutated the registered definition.");
                Suite.Check(kernel.LoadedPlans == 0 && kernel.QueuedEvents == 0 && kernel.CurrentTick == -1,
                    "Port metadata resolution created executable work.");
            });
        }
        var sample = vectors.GetProperty("resolutions").EnumerateArray().First(r =>
            r.GetProperty("id").GetString() == "forge.modifier.value.add");
        var seed = sample.GetProperty("seed"); var id = sample.GetProperty("id").GetString()!;
        Suite.Test("exact-lock-lifecycle-and-thread", () =>
        {
            var kernel = Suite.Kernel(); var handle = kernel.RegisterModule(Suite.Module(seed));
            var empty = RuntimeJson.EmptyObject;
            Suite.Reject(() => kernel.ResolveGraphContract(id, "1.0.0", empty), "capability-version");
            Suite.Reject(() => kernel.ResolveGraphContract(id, "1.2.0", empty), "capability-version");
            Suite.Reject(() => kernel.ResolveGraphContract("test.missing", "1.1.0", empty), "capability-unavailable");
            Suite.Reject(() => kernel.ResolveGraphContract(id, "1.1.0", RuntimeJson.Parse("{\"typo_count\":3}")), "unknown-field");
            Suite.Reject(() => kernel.ResolveGraphContract(id, "1.1.0", RuntimeJson.Parse("[]")), "object-required");
            Suite.Reject(() => kernel.ResolveGraphContract(id, "1.1.0", RuntimeJson.Parse("null")), "object-required");
            Task.Run(() => Suite.Reject(() => kernel.ResolveGraphContract(id, "1.1.0", empty), "wrong-thread"))
                .GetAwaiter().GetResult();
            var retained = kernel.ResolveGraphContract(id, "1.1.0", RuntimeJson.From(new { input_count = 32 }));
            var before = kernel.ExportManifest();
            Suite.Reject(() => kernel.RegisterModule(Suite.Module(seed)), "provider-conflict");
            Suite.Check(before == kernel.ExportManifest(), "Duplicate revision modified registry.");
            handle.Dispose();
            Suite.Reject(() => kernel.ResolveGraphContract(id, "1.1.0", empty), "capability-unavailable");
            Suite.Check(retained.GetProperty("inputs").GetArrayLength() == 32, "Returned metadata depended on live registration.");
            kernel.RegisterModule(Suite.Module(seed));
            Suite.Check(Suite.Equal(retained, kernel.ResolveGraphContract(id, "1.1.0", RuntimeJson.From(new { input_count = 32 }))),
                "Reregistering changed definition semantics.");
        });
        Suite.Test("read-only-lifecycle-observation", () =>
        {
            var kernel = Suite.Kernel(); var handle = kernel.RegisterModule(Suite.Module(seed));
            var observations = 0;
            using var subscription = handle.ObserveLifecycle(_ =>
            {
                var ports = kernel.ResolveGraphContract(id, "1.1.0", RuntimeJson.EmptyObject);
                Suite.Check(ports.GetProperty("inputs").GetArrayLength() == 2, "Default count did not preserve base ports.");
                observations++;
            });
            kernel.BeginWorld(1); kernel.StartRuntime(() => { }); kernel.Advance(0, true);
            var expected = observations; kernel.Advance(0, true);
            Suite.Check(observations == expected && subscription.IsActive && kernel.LifecycleFaultCount == 0,
                "Read-only resolution disrupted lifecycle delivery.");
        });
        Suite.Test("generated-name-bounds", () =>
        {
            var candidate = JsonNode.Parse(seed.GetRawText())!;
            var template = candidate["capabilities"]![0]!["graph"]!["variadic"]!["port"]!;
            template["id"] = new string('a', 253);
            var kernel = Suite.Kernel(); kernel.RegisterModule(Suite.Module(RuntimeJson.Parse(candidate.ToJsonString())));
            var resolved = kernel.ResolveGraphContract(id, "1.1.0", RuntimeJson.From(new { input_count = 32 }));
            Suite.Check(resolved.GetProperty("inputs")[31].GetProperty("id").GetString()!.Length == 256,
                "The bounded name boundary was rejected or shortened.");
            template["id"] = new string('a', 254);
            Suite.Reject(() => Suite.Kernel().RegisterModule(Suite.Module(RuntimeJson.Parse(candidate.ToJsonString()))),
                "variadic-port-name-budget");
        });
        Suite.Test("flags-units-schema-and-order", () =>
        {
            var candidate = JsonNode.Parse(seed.GetRawText())!;
            var graph = candidate["capabilities"]![0]!["graph"]!;
            foreach (var port in graph["inputs"]!.AsArray().Append(graph["variadic"]!["port"]))
            {
                port!["unit"] = "m"; port["schema"] = "test.measurement";
                port["optional"] = false; port["nullable"] = false;
            }
            var kernel = Suite.Kernel(); kernel.RegisterModule(Suite.Module(RuntimeJson.Parse(candidate.ToJsonString())));
            var resolved = kernel.ResolveGraphContract(id, "1.1.0", RuntimeJson.From(new { input_count = 12 }));
            var inputs = resolved.GetProperty("inputs");
            Suite.Check(inputs[0].GetProperty("id").GetString() == "a" && inputs[1].GetProperty("id").GetString() == "b"
                && inputs[9].GetProperty("id").GetString() == "input_10", "Ports were sorted or renamed.");
            foreach (var port in inputs.EnumerateArray())
                Suite.Check(port.GetProperty("unit").GetString() == "m" && port.GetProperty("schema").GetString() == "test.measurement"
                    && !port.GetProperty("optional").GetBoolean() && !port.GetProperty("nullable").GetBoolean(),
                    "Port contract flags or units were dropped.");
        });
        Suite.Test("fixed-shape-preserved", () =>
        {
            var row = vectors.GetProperty("registrations").EnumerateArray().First(r => r.GetProperty("name").GetString() == "actual:forge.modifier.value.constant");
            var kernel = Suite.Kernel(); kernel.RegisterModule(Suite.Module(row.GetProperty("seed")));
            Suite.Check(Suite.Equal(kernel.ResolveGraphContract("forge.modifier.value.constant", "1.0.0", RuntimeJson.From(new { value = 5 })),
                row.GetProperty("seed").GetProperty("capabilities")[0].GetProperty("graph")), "Fixed metadata changed.");
        });
    }
}
