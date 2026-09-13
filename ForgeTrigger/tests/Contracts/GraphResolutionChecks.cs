using System.Text.Json;
using ForgeRuntime.Framework;

// Both T1 and independent acceptance consume the actual public metadata API.
internal static class GraphResolutionChecks
{
    internal static void Run(RuntimeKernel kernel, JsonElement definition, JsonElement row, Action<bool,string> check)
    {
        var id = definition.GetProperty("id").GetString()!;
        var version = definition.GetProperty("version").GetString()!;
        var before = kernel.ExportManifest();
        var original = definition.GetProperty("graph");
        check(AuthoringContractTests.Same(kernel.ResolveGraphContract(id,version,RuntimeJson.EmptyObject), original),
            "public default graph contract preserves metadata " + id);
        if (row.TryGetProperty("resolutions",out var resolutions))
            foreach (var example in resolutions.EnumerateArray())
            {
                var actual = kernel.ResolveGraphContract(id,version,example.GetProperty("parameters"));
                check(AuthoringContractTests.Same(actual,example.GetProperty("expected")),
                    "public variadic ports match website order/types " + id);
            }
        void Reject(string code, Action action)
        {
            try { action(); check(false,"public graph contract accepted " + code + " " + id); }
            catch (RuntimeContractException error) { check(error.Code==code,"public graph contract rejects " + code + " " + id); }
        }
        if (row.TryGetProperty("invalidResolutions",out var invalid))
            foreach (var example in invalid.EnumerateArray())
                Reject(example.GetProperty("expectedCode").GetString()!,
                    () => kernel.ResolveGraphContract(id,version,example.GetProperty("parameters")));
        Reject("capability-version", () => kernel.ResolveGraphContract(id,"9.9.9",RuntimeJson.EmptyObject));
        Reject("capability-unavailable", () => kernel.ResolveGraphContract("test.absent.capability",version,RuntimeJson.EmptyObject));
        Reject("unknown-field", () => kernel.ResolveGraphContract(id,version,RuntimeJson.From(new {unreviewed=true})));
        check(kernel.ExportManifest()==before, "graph contract resolution is read-only " + id);
        check(kernel.LoadedPlans==0 && kernel.QueuedEvents==0, "port expansion is not executable work " + id);
    }
}
