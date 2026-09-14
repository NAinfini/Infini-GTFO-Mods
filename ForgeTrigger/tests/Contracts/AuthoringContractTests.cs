using System.Text.Json;
using ForgeRuntime.Framework;

internal static class AuthoringContractTests
{
    internal static void Run(string path, Action<bool, string> check)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var report = document.RootElement;
        check(report.GetProperty("kind").GetString() == "actual-authoring-registration-audit", "authoring audit provenance");
        var canonical = RuntimeJson.Parse(CombatContracts.Module().RegistryJson).GetProperty("capabilities");
        var accepted = 0; var rejected = 0; var shared = 0;
        var unsupported = new List<object>();
        foreach (var row in report.GetProperty("cases").EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!;
            var seed = row.GetProperty("seed");
            var definition = seed.GetProperty("capabilities")[0];
            check(definition.GetProperty("version").GetString() == row.GetProperty("version").GetString(), "exact audited version " + id);
            if (row.GetProperty("shared").GetBoolean())
            {
                var expected = canonical.EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
                check(Same(definition, expected), "shared definition equals actual SDK " + id);
                shared++;
            }
            var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "authoring-audit-no-game"));
            var before = kernel.ExportManifest();
            var module = new RuntimeModule(RuntimeKernel.ApiVersion, seed.GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
            if (row.GetProperty("accepted").GetBoolean())
            {
                using (kernel.RegisterModule(module, RuntimeLogLevel.Off))
                {
                    var snapshot = RuntimeJson.Parse(kernel.ExportManifest());
                    check(snapshot.GetProperty("registry").GetProperty("capabilities").GetArrayLength() == 1, "single-owner metadata registered " + id);
                    check(snapshot.GetProperty("bindingSupport").GetArrayLength() == 0, "metadata adds no executable binding " + id);
                    GraphResolutionChecks.Run(kernel, definition, row, check);
                }
                check(kernel.ExportManifest() == before, "metadata cleanly unregisters " + id); accepted++;
            }
            else
            {
                try { kernel.RegisterModule(module, RuntimeLogLevel.Off); check(false, "unsupported metadata accepted " + id); }
                catch (RuntimeContractException error)
                {
                    check(error.Code == row.GetProperty("expectedCode").GetString()
                        && (!row.TryGetProperty("expectedDetail", out var detail) || error.Message == detail.GetString()), "precise invalid metadata rejection " + id);
                }
                check(kernel.ExportManifest() == before, "unsupported metadata rejection is atomic " + id); rejected++;
                unsupported.Add(new { id, version=row.GetProperty("version").GetString(), code=row.GetProperty("expectedCode").GetString(), caseName=row.GetProperty("caseId").GetString() });
            }
            check(kernel.LoadedPlans == 0 && kernel.QueuedEvents == 0, "audit creates no game work " + id);
        }
        var result = new {status="audited", acceptedMetadata=accepted,
            sharedCanonical=shared, invalidMetadataRejected=rejected, invalidDefinitions=unsupported, unsupportedMetadata=0,
            metadataReady=accepted==report.GetProperty("definitionCount").GetInt32(), runtimeReady=false, gameVerified=false};
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "authoring-compatibility-result.json"), JsonSerializer.Serialize(result));
    }
    internal static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        if (a.ValueKind == JsonValueKind.Object)
        {
            var left = a.EnumerateObject().ToArray();
            return left.Length == b.EnumerateObject().Count()
                && left.All(p => b.TryGetProperty(p.Name, out var value) && Same(p.Value, value));
        }
        if (a.ValueKind == JsonValueKind.Array)
            return a.GetArrayLength() == b.GetArrayLength()
                && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Same(p.First, p.Second));
        if (a.ValueKind == JsonValueKind.Number) return a.GetDouble() == b.GetDouble();
        if (a.ValueKind == JsonValueKind.String) return a.GetString() == b.GetString();
        return a.GetRawText() == b.GetRawText();
    }
}
