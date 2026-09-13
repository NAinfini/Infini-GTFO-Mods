using ForgeRuntime.Framework;

static class RegistrationProbe
{
    // Expected to fail until the blocked Registry integration has actually landed.
    // This probe is never counted as a passed gameplay or query implementation.
    public static void Run()
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", "1.0.0", "synthetic"));
        var reference = ContractTests.Ref(); int observed = 0;
        const string registry = "{\"providers\":[{\"id\":\"test.entities\",\"kind\":\"extension\",\"version\":\"1.0.0\",\"dependencies\":[]}],\"capabilities\":[],\"bindings\":[]}";
        kernel.BeginWorld(1);
        kernel.RegisterModule(new RuntimeModule("1.0.0", registry, new Dictionary<string, CommandHandler>(),
            Array.Empty<BindingSupport>(), new Dictionary<string, Func<EntityReference, bool>> { ["test.entity"] = r => r == reference })
        {
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
            { ["test.entity"] = r => { observed++; return ContractTests.Entity(r); } }
        });
        kernel.StartRuntime(() => { }); kernel.Advance(0, true);
        var result = kernel.InspectEntities(new[] { reference });
        Console.WriteLine($"REGISTRATION PROBE: status={result.Status}, code={result.Code}, observerCalls={observed}, item={result.Items[0].Code}");
        Check.That(result.IsComplete && observed == 1, "registered observer must actually be called (known unfinished integration)");
        kernel.StopRuntime();
    }
}
