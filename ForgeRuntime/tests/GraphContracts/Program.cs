using System.Text.Json;
using ForgeRuntime.Framework;

if (args.Length != 1) throw new ArgumentException("Expected generated vector JSON path.");
using var vectors = JsonDocument.Parse(File.ReadAllText(args[0]));
foreach (var row in vectors.RootElement.GetProperty("registrations").EnumerateArray())
{
    Suite.Test(row.GetProperty("name").GetString()!, () =>
    {
        var kernel = Suite.Kernel(); var before = kernel.ExportManifest();
        if (row.GetProperty("accepted").GetBoolean())
        {
            kernel.RegisterModule(Suite.Module(row.GetProperty("seed")));
            using var manifest = JsonDocument.Parse(kernel.ExportManifest());
            Suite.Check(Suite.Equal(manifest.RootElement.GetProperty("registry").GetProperty("capabilities")[0],
                row.GetProperty("seed").GetProperty("capabilities")[0]), "Metadata changed on registration.");
            Suite.Check(kernel.LoadedPlans == 0 && kernel.QueuedEvents == 0, "Metadata created work.");
        }
        else
        {
            Suite.Reject(() => kernel.RegisterModule(Suite.Module(row.GetProperty("seed"))));
            Suite.Check(kernel.ExportManifest() == before, "Failed registration was not atomic.");
        }
    });
}
ResolutionTests.Run(vectors.RootElement);
PlanBoundaryTests.Run(vectors.RootElement);
Console.WriteLine($"Graph contract checks: {Suite.Passed} passed; {Suite.Failed} failed.");
return Suite.Failed == 0 ? 0 : 1;
