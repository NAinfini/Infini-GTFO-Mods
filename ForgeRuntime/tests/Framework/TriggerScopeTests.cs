using ForgeRuntime.Framework;

/// <summary>
/// The one optional structural parameter a trigger row may now declare: the map object address it is attached to
/// (ruling 143.11). What is under test is the whole path — the loader resolves the constant and refuses an address
/// that is not the canonical five-segment spelling or that a row declares twice, and dispatch hands the entry only
/// the events whose own subject is that object, while a row that declares no address keeps receiving every event of
/// its binding.
///
/// The address is compared with the subject entity namespace the event carries, so an event about another object
/// of the same kind is not merely filtered later: the entry is never claimed, and the plan's own mount targets are
/// never asked about it.
/// </summary>
internal static class TriggerScopeTests
{
    private const string Id = "example.scope";
    /// <summary>The address an entrypoint is attached to, in the one shape the website's address module renders
    /// and the provider's own address record parses.</summary>
    private const string Door = "door/0/0/3/security";

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

        // The fixture trigger gains the one parameter this batch allows: `address` is optional, so the same row
        // also serves the unattached case, and the plan's constant frame carries either the address or null. The
        // row is rewritten as JSON rather than as text, so the fixture's own spelling is not what this test reads.
        const string ObjectAddress = "{\"id\":\"address\",\"type\":\"object-address\",\"role\":\"structural\",\"required\":false}";
        RuntimeModule Module(string declared)
        {
            var module = Fixture.Module(Id);
            var registry = System.Text.Json.Nodes.JsonNode.Parse(module.RegistryJson)!;
            var capability = registry["capabilities"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == Fixture.TriggerCapability(Id))!;
            capability["graph"]!["parameters"] = System.Text.Json.Nodes.JsonNode.Parse("[" + declared + "]");
            // The namespace the events below are about: the provider that owns it is what makes a subject readable
            // as an address, and the same registration answers whether a reference still exists.
            return module with
            {
                RegistryJson = registry.ToJsonString(),
                EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [Id] = _ => true }
            };
        }

        var kernel = new RuntimeKernel(Fixture.Identity);
        kernel.BeginWorld(1);
        // The fixture's registry text is the one the plan's pin table is read from, so the rewrite has to land:
        // a capability that declares the parameter while the plan's constant frame carries one is the plan's own
        // `constant-frame` refusal, and that is not what this file is testing.
        Check(Module(ObjectAddress).RegistryJson.Contains("object-address"), "the fixture trigger row declares the address parameter");
        // The fixture plans mount on the fixture's own level kind, so its owner is registered first: what this file
        // tests is the trigger's address, not mount ownership.
        kernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
        var provider = kernel.RegisterModule(Module(ObjectAddress), RuntimeLogLevel.Off);
        Check(provider.IsRegistered, "a trigger row declaring one map object address parameter registers");

        // A plan of this provider, written from the fixture's own plan — its step layout and pin order are the
        // suite's, so only what this file is about is changed: the trigger's constant frame (the address, or null,
        // and the second address of the two-address case) and the pin whose capability row now declares that
        // parameter. The kernel is a parameter because a case of its own boots its own kernel, and a plan is
        // written from the registry it will load into.
        string Plan(RuntimeKernel target, string planId, object? address, object? second = null)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(Fixture.Plan(target, planId, Id))!.AsObject();
            var pins = node["bindings"]!.AsArray();
            pins.Clear();
            pins.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["bindingId"] = Id + ".binding.apply", ["capabilityId"] = Fixture.ActionCapability(Id), ["capabilityVersion"] = "1.0.0",
                ["providerId"] = Id, ["providerVersion"] = "1.0.0", ["handler"] = Fixture.Handler(Id)
            });
            pins.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["bindingId"] = Fixture.Trigger(Id), ["capabilityId"] = Fixture.TriggerCapability(Id), ["capabilityVersion"] = "1.0.0",
                ["providerId"] = Id, ["providerVersion"] = "1.0.0", ["handler"] = Id + ".handler.trigger"
            });
            var entry = node["entrypoints"]!.AsArray()[0]!.AsObject();
            entry["binding"] = 1;
            var frame = new List<System.Text.Json.Nodes.JsonNode?>
            {
                address == null ? null : System.Text.Json.Nodes.JsonValue.Create(address.ToString())
            };
            if (second != null) frame.Add(System.Text.Json.Nodes.JsonValue.Create(second.ToString()));
            entry["layout"]!["constants"] = new System.Text.Json.Nodes.JsonArray(frame.ToArray());
            return node.ToJsonString();
        }
        // One advance first: a dispatch is inside a tick, and a world that has just begun is at no tick at all.
        kernel.Advance(1, true);
        RuntimeEvent Event(string id, string subject) => new(id, Fixture.Trigger(Id), kernel.WorldEpoch, kernel.CurrentTick,
            "scope", RuntimeJson.From(new { target = new { id = subject, worldEpoch = kernel.WorldEpoch, lifeEpoch = 1L } }));

        kernel.LoadPlan(Plan(kernel, "scope-bound", Door));
        Check(true, "an entrypoint carrying an address loads");

        // An event about the object the entry named is claimed; an event about another object of the same category
        // is not, which is the whole point of the parameter: the trigger stops following every door of the level.
        var boundHit = provider.Publish(Event("bound-hit", Id + ":" + Door));
        Check(boundHit.Status == "queued", "the bound entry is claimed by its own object (" + boundHit.Status + "/" + boundHit.Code + ")");
        Check(provider.Publish(Event("bound-other", Id + ":door/0/0/4/security")).Status == "ignored", "another object of the same category is not claimed");

        // A trigger that declares no address keeps receiving every event of its binding, whatever object the event
        // is about: the parameter narrows a trigger, it never becomes a requirement to name an object.
        var open = new RuntimeKernel(Fixture.Identity);
        open.BeginWorld(2);
        open.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
        var openProvider = open.RegisterModule(Module(ObjectAddress), RuntimeLogLevel.Off);
        open.Advance(1, true);
        RuntimeEvent Open(string id, string subject) => new(id, Fixture.Trigger(Id), open.WorldEpoch, open.CurrentTick,
            "scope", RuntimeJson.From(new { target = new { id = subject, worldEpoch = open.WorldEpoch, lifeEpoch = 1L } }));
        open.LoadPlan(Plan(open, "scope-open", null));
        Check(openProvider.Publish(Open("open-door", Id + ":" + Door)).Status == "queued", "an entry with no address is claimed by the object it names");
        Check(openProvider.Publish(Open("open-other", Id + ":door/0/0/9/security")).Status == "queued", "an entry with no address is claimed by any object");
        openProvider.Dispose();

        // The grammar is the loader's: five non-empty segments, and the address a row may declare only once.
        RejectCode(() => kernel.LoadPlan(Plan(kernel, "scope-short", "door/0/0/3")), "trigger-address", "an address that is not five segments is refused at load");
        RejectCode(() => kernel.LoadPlan(Plan(kernel, "scope-empty", "door/0/0/3/")), "trigger-address", "an address with an empty segment is refused at load");

        // Two addresses on one trigger is not a filter this kernel can evaluate, so it is refused by name rather
        // than silently mounted on the first one. The two parameters carry different names because one row cannot
        // declare one id twice — that is the registry's own `duplicate-parameter` refusal, a different fact.
        const string SpareAddress = "{\"id\":\"address_spare\",\"type\":\"object-address\",\"role\":\"structural\",\"required\":false}";
        var twice = new RuntimeKernel(Fixture.Identity);
        twice.BeginWorld(1);
        // The fixture plan mounts on the fixture's own level kind, so its owner is registered here too: the fact
        // under test is the trigger's address count, not the plan's mount.
        twice.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
        twice.RegisterModule(Module(ObjectAddress + "," + SpareAddress), RuntimeLogLevel.Off);
        RejectCode(() => twice.LoadPlan(Plan(twice, "scope-twice", Door, "door/0/0/4/security")), "trigger-address-count", "two address parameters on one trigger are refused");

        provider.Dispose();
        return checks;
    }
}
