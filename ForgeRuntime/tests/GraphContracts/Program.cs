using System.Text.Json;
using ForgeRuntime.Framework;

if (args.Length != 1) throw new ArgumentException("Expected generated vector JSON path.");
using var vectors = JsonDocument.Parse(File.ReadAllText(args[0]));
// Coverage: a row is asserted one by one only while this runtime's own registry knows the shape it declares. A
// catalog definition the registry refuses — an unknown port, parameter or capability shape — is a capability this
// SDK does not register, so it is counted as uncovered instead of failing the suite forever; the total is printed.
// A negative row is judged against the definition it mutates: a mutation of a shape this runtime does not know
// proves nothing, and counts as uncovered as well.
foreach (var row in vectors.RootElement.GetProperty("registrations").EnumerateArray())
{
    var name = row.GetProperty("name").GetString()!;
    var seed = row.GetProperty("seed");
    var basis = row.TryGetProperty("baseSeed", out var parent) && parent.ValueKind == JsonValueKind.Object ? parent : seed;
    if (!Suite.Registrable(basis)) { Suite.Uncover(name); continue; }
    Suite.Test(name, () =>
    {
        var kernel = Suite.Kernel(); var before = kernel.ExportManifest();
        if (row.GetProperty("accepted").GetBoolean())
        {
            kernel.RegisterModule(Suite.Module(seed), RuntimeLogLevel.Off);
            using var manifest = JsonDocument.Parse(kernel.ExportManifest());
            Suite.Check(Suite.Equal(manifest.RootElement.GetProperty("registry").GetProperty("capabilities")[0],
                seed.GetProperty("capabilities")[0]), "Metadata changed on registration.");
            Suite.Check(kernel.LoadedPlans == 0 && kernel.QueuedEvents == 0, "Metadata created work.");
        }
        else
        {
            Suite.Reject(() => kernel.RegisterModule(Suite.Module(seed), RuntimeLogLevel.Off));
            Suite.Check(kernel.ExportManifest() == before, "Failed registration was not atomic.");
        }
    });
}
ResolutionTests.Run(vectors.RootElement);
PlanBoundaryTests.Run(vectors.RootElement);
Suite.ReportCoverage();
Console.WriteLine($"Graph contract checks: {Suite.Passed} passed; {Suite.Failed} failed; {Suite.Unregistered} unregistered.");
return Suite.Failed == 0 ? 0 : 1;
