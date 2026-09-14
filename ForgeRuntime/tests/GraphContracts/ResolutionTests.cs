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
                var kernel = Suite.Kernel(); kernel.RegisterModule(Suite.Module(row.GetProperty("seed")), RuntimeLogLevel.Off);
                var before = kernel.ExportManifest();
                JsonElement Resolve() => kernel.ResolveGraphContract(row.GetProperty("id").GetString()!,
                    row.GetProperty("version").GetString()!, row.GetProperty("parameters"));
                if (row.GetProperty("accepted").GetBoolean())
                {
                    var actual = Resolve();
                    Suite.Check(Suite.Equal(actual, row.GetProperty("expected")), "C#/TypeScript port layout differs.");
                    Suite.Check(Suite.Equal(actual, Resolve()), "Repeated resolution changed its output.");
                    Suite.Check(actual.TryGetProperty("variadic", out _) || actual.TryGetProperty("portGroups", out _),
                        "Variable metadata was silently stripped.");
                }
                else Suite.Reject(() => Resolve(), "invalid-integer");
                Suite.Check(kernel.ExportManifest() == before, "Resolution mutated the registered definition.");
                Suite.Check(kernel.LoadedPlans == 0 && kernel.QueuedEvents == 0 && kernel.CurrentTick == -1,
                    "Port metadata resolution created executable work.");
            });
        }
        // A numeric whole-side input variadic from the website seed (numbers may carry units); id, version and bounds come from the vectors.
        var sample = vectors.GetProperty("resolutions").EnumerateArray().First(r =>
            r.GetProperty("seed").GetProperty("capabilities")[0].GetProperty("graph").TryGetProperty("variadic", out var v)
            && v.GetProperty("side").GetString() == "inputs" && v.GetProperty("port").GetProperty("type").GetString() == "number");
        var seed = sample.GetProperty("seed"); var id = sample.GetProperty("id").GetString()!;
        var version = sample.GetProperty("version").GetString()!;
        var graph = seed.GetProperty("capabilities")[0].GetProperty("graph");
        var countName = graph.GetProperty("variadic").GetProperty("parameter").GetString()!;
        var count = graph.GetProperty("parameters").EnumerateArray().First(p => p.GetProperty("id").GetString() == countName);
        int minimum = count.GetProperty("minimum").GetInt32(), maximum = count.GetProperty("maximum").GetInt32();
        JsonElement Count(int value) => RuntimeJson.Parse("{\"" + countName + "\":" + value + "}");
        var parts = version.Split('.').Select(int.Parse).ToArray();
        var neighbours = new[] { $"{parts[0]}.{parts[1]}.{parts[2] + 1}", $"{parts[0]}.{parts[1] + 1}.0", $"{parts[0] + 1}.0.0" };
        Suite.Test("exact-lock-lifecycle-and-thread", () =>
        {
            var kernel = Suite.Kernel(); var handle = kernel.RegisterModule(Suite.Module(seed), RuntimeLogLevel.Off);
            var empty = RuntimeJson.EmptyObject;
            foreach (var neighbour in neighbours)
                Suite.Reject(() => kernel.ResolveGraphContract(id, neighbour, empty), "capability-version");
            Suite.Reject(() => kernel.ResolveGraphContract("test.missing", version, empty), "capability-unavailable");
            Suite.Reject(() => kernel.ResolveGraphContract(id, version, RuntimeJson.Parse("{\"typo_count\":3}")), "unknown-field");
            Suite.Reject(() => kernel.ResolveGraphContract(id, version, RuntimeJson.Parse("[]")), "object-required");
            Suite.Reject(() => kernel.ResolveGraphContract(id, version, RuntimeJson.Parse("null")), "object-required");
            Task.Run(() => Suite.Reject(() => kernel.ResolveGraphContract(id, version, empty), "wrong-thread"))
                .GetAwaiter().GetResult();
            var retained = kernel.ResolveGraphContract(id, version, Count(maximum));
            var before = kernel.ExportManifest();
            Suite.Reject(() => kernel.RegisterModule(Suite.Module(seed), RuntimeLogLevel.Off), "provider-conflict");
            Suite.Check(before == kernel.ExportManifest(), "Duplicate revision modified registry.");
            handle.Dispose();
            Suite.Reject(() => kernel.ResolveGraphContract(id, version, empty), "capability-unavailable");
            Suite.Check(retained.GetProperty("inputs").GetArrayLength() == maximum, "Returned metadata depended on live registration.");
            kernel.RegisterModule(Suite.Module(seed), RuntimeLogLevel.Off);
            Suite.Check(Suite.Equal(retained, kernel.ResolveGraphContract(id, version, Count(maximum))),
                "Reregistering changed definition semantics.");
        });
        Suite.Test("read-only-lifecycle-observation", () =>
        {
            var kernel = Suite.Kernel(); var handle = kernel.RegisterModule(Suite.Module(seed), RuntimeLogLevel.Off);
            var observations = 0;
            using var subscription = handle.ObserveLifecycle(_ =>
            {
                var ports = kernel.ResolveGraphContract(id, version, RuntimeJson.EmptyObject);
                Suite.Check(ports.GetProperty("inputs").GetArrayLength() == minimum, "Default count did not preserve base ports.");
                observations++;
            });
            kernel.BeginWorld(1); kernel.StartRuntime(() => { }); kernel.Advance(0, true);
            var expected = observations; kernel.Advance(0, true);
            Suite.Check(observations == expected && subscription.IsActive && kernel.LifecycleFaultCount == 0,
                "Read-only resolution disrupted lifecycle delivery.");
        });
        Suite.Test("generated-name-bounds", () =>
        {
            const int nameBudget = 256;
            var candidate = JsonNode.Parse(seed.GetRawText())!;
            var template = candidate["capabilities"]![0]!["graph"]!["variadic"]!["port"]!;
            var fitting = nameBudget - ("_" + maximum).Length;
            template["id"] = new string('a', fitting);
            var kernel = Suite.Kernel(); kernel.RegisterModule(Suite.Module(RuntimeJson.Parse(candidate.ToJsonString())), RuntimeLogLevel.Off);
            var resolved = kernel.ResolveGraphContract(id, version, Count(maximum));
            Suite.Check(resolved.GetProperty("inputs")[maximum - 1].GetProperty("id").GetString()!.Length == nameBudget,
                "The bounded name boundary was rejected or shortened.");
            template["id"] = new string('a', fitting + 1);
            Suite.Reject(() => Suite.Kernel().RegisterModule(Suite.Module(RuntimeJson.Parse(candidate.ToJsonString())), RuntimeLogLevel.Off),
                "variadic-port-name-budget");
        });
        Suite.Test("flags-units-schema-and-order", () =>
        {
            var candidate = JsonNode.Parse(seed.GetRawText())!;
            var node = candidate["capabilities"]![0]!["graph"]!;
            foreach (var port in node["inputs"]!.AsArray().Append(node["variadic"]!["port"]))
            {
                port!["unit"] = "m"; port["schema"] = "test.measurement";
                port["optional"] = false; port["nullable"] = false;
            }
            var kernel = Suite.Kernel(); kernel.RegisterModule(Suite.Module(RuntimeJson.Parse(candidate.ToJsonString())), RuntimeLogLevel.Off);
            var resolved = kernel.ResolveGraphContract(id, version, Count(maximum));
            var inputs = resolved.GetProperty("inputs");
            var template = graph.GetProperty("variadic").GetProperty("port").GetProperty("id").GetString()!;
            var expectedIds = graph.GetProperty("inputs").EnumerateArray().Select(p => p.GetProperty("id").GetString()!)
                .Concat(Enumerable.Range(minimum + 1, maximum - minimum).Select(i => template + "_" + i));
            Suite.Check(inputs.EnumerateArray().Select(p => p.GetProperty("id").GetString()!).SequenceEqual(expectedIds),
                "Ports were sorted or renamed.");
            foreach (var port in inputs.EnumerateArray())
                Suite.Check(port.GetProperty("unit").GetString() == "m" && port.GetProperty("schema").GetString() == "test.measurement"
                    && !port.GetProperty("optional").GetBoolean() && !port.GetProperty("nullable").GetBoolean(),
                    "Port contract flags or units were dropped.");
        });
        Suite.Test("fixed-shape-preserved", () =>
        {
            var row = vectors.GetProperty("registrations").EnumerateArray().First(r => r.GetProperty("name").GetString() == "actual:forge.modifier.value.constant");
            var capability = row.GetProperty("seed").GetProperty("capabilities")[0];
            var kernel = Suite.Kernel(); kernel.RegisterModule(Suite.Module(row.GetProperty("seed")), RuntimeLogLevel.Off);
            Suite.Check(Suite.Equal(kernel.ResolveGraphContract("forge.modifier.value.constant", capability.GetProperty("version").GetString()!,
                    RuntimeJson.From(new { value = 5 })), capability.GetProperty("graph")), "Fixed metadata changed.");
        });
    }
}
