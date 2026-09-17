using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// The entity-reference-to-native-object table (R7 2): the one door a handler reaches a module's own entities
/// through. What is under test is ownership and currency, the two things the table promises: only the provider
/// that owns a kind may register its lookup, and only a reference the owner still calls live is answered with an
/// object — a reference of another life, another kind or another namespace is nothing rather than a stale object.
/// </summary>
internal static class EntityInstanceTests
{
    private const string Id = "example.instance";

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void RejectCode(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }

        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        kernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
        // The provider owns its kind, so the resolver, the instance lookup and the native object are all its own:
        // the lookup answers with the object the provider itself would have reached for.
        var module = Fixture.Module(Id) with
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
            {
                [Id] = reference => reference.WorldEpoch == 1 && reference.LifeEpoch == 1
            },
            EntityInstances = new Dictionary<string, Func<EntityReference, object?>>
            {
                [Id] = reference => "native:" + reference.Id
            }
        };
        var handle = kernel.RegisterModule(module, RuntimeLogLevel.Off);
        Check(handle.IsRegistered, "a kind's instance lookup registers beside its resolver");

        // One owner per kind, and the owner is the provider that registered the kind's resolver: a second module
        // cannot answer for entities it does not own, which is what keeps two modules' tables from overlapping.
        // Asked before startup freezes registration, exactly as every other registration rule is.
        var foreign = Fixture.Module("example.foreign") with
        {
            EntityInstances = new Dictionary<string, Func<EntityReference, object?>> { [Id] = _ => "stolen" }
        };
        RejectCode(() => kernel.RegisterModule(foreign, RuntimeLogLevel.Off), "entity-instance-owner",
            "a module that does not own the kind cannot register its instance lookup");

        // A lookup is a world read: it answers only once the runtime is ready and a world has begun.
        kernel.StartRuntime(() => { });
        Check((string?)kernel.EntityInstance(new EntityReference(Id + ":1", 1, 1)) == "native:" + Id + ":1",
            "a live reference answers the owner's own object");
        Check(kernel.EntityInstance(new EntityReference(Id + ":1", 1, 2)) == null,
            "a reference of another life answers nothing");
        Check(kernel.EntityInstance(new EntityReference(Id + ":1", 2, 1)) == null,
            "a reference of another world answers nothing");
        Check(kernel.EntityInstance(new EntityReference("elsewhere:1", 1, 1)) == null,
            "a kind nobody registered answers nothing rather than an object of another kind");

        // Disposing the handle releases the module, and the lookup goes with it: the table is the module's, not the
        // kernel's, exactly as its resolver is.
        handle.Dispose();
        Check(kernel.EntityInstance(new EntityReference(Id + ":1", 1, 1)) == null,
            "a released module's instance lookup is gone with its resolver");

        return checks;
    }
}
