using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.MapObjects;

/// <summary>One object a hit was resolved against: the component a door or a terminal is hit through, which is
/// not a map object itself and is addressed only by the map object above it. A hit that belongs to no map
/// object is one of these with nothing above it.</summary>
internal sealed class StubHit
{
    internal StubHit(object? mapObject) => MapObject = mapObject;

    internal object? MapObject { get; }
}

/// <summary>One door as the native side would report it: the coordinates of the zone it is the entrance gate of
/// plus a status and the two lock members the door's own lock component carries. The stub addresses itself the
/// way the native reader does, so the module's address, state-key and identity rules are exercised without the
/// game.</summary>
internal sealed class StubDoor
{
    internal StubDoor(int status, bool lockedWithNoKey = false, string key = "",
        int dimension = 0, int layer = 2, int zone = 4)
    {
        Status = status; LockedWithNoKey = lockedWithNoKey; KeyName = key;
        Dimension = dimension; Layer = layer; Zone = zone;
    }

    internal int Status { get; set; }
    internal bool LockedWithNoKey { get; set; }
    internal string KeyName { get; set; }
    internal int? Dimension { get; set; }
    internal int? Layer { get; set; }
    internal int? Zone { get; set; }
    internal string? Broken { get; set; }

    internal MapObjectReference? Address() => MapObjectDoorAddress.Create(Dimension, Layer, Zone);

    internal bool Locked => MapObjectDoorStatus.IsLocked(Status) || LockedWithNoKey || KeyName.Length != 0;

    internal MapObjectObservation Read()
    {
        if (Broken == "unreadable") return new MapObjectObservation(false, null, null);
        return new MapObjectObservation(true, new MapObjectDoorSnapshot(
            MapObjectDoorStatus.Name(Status), Status, MapObjectDoorStatus.Phase(Status), Locked, KeyName), null);
    }
}

/// <summary>One terminal as the native side would report it, addressed by the zone it was spawned in and its
/// position in that zone's terminal list.</summary>
internal sealed class StubTerminal
{
    internal StubTerminal(int placementIndex = 0, int dimension = 0, int layer = 2, int zone = 4)
    {
        PlacementIndex = placementIndex; Dimension = dimension; Layer = layer; Zone = zone;
    }

    internal int? PlacementIndex { get; set; }
    internal int? Dimension { get; set; }
    internal int? Layer { get; set; }
    internal int? Zone { get; set; }
    internal int Status { get; set; } = 1;
    internal string? Broken { get; set; }

    internal MapObjectReference? Address()
        => MapObjectTerminalAddress.Create(Dimension, Layer, Zone, PlacementIndex);

    internal MapObjectObservation Read()
    {
        if (Broken == "unreadable") return new MapObjectObservation(false, null, null);
        return new MapObjectObservation(true, null, new MapObjectTerminalSnapshot(
            MapObjectTerminalState.Name(Status), Status, MapObjectTerminalState.SessionActive(Status),
            MapObjectTerminalState.Outcome(Status)));
    }
}

/// <summary>One power generator as the native side would report it: the zone it stands in, the level's own serial
/// for it, its own powered reading and the counts of the group it stands in. The stub addresses itself the way
/// the native reader does, so the module's address, state-key and identity rules are exercised without the
/// game.</summary>
internal sealed class StubGenerator
{
    internal StubGenerator(int serial = 7, bool powered = false, int connected = 0, int total = 0,
        int dimension = 0, int layer = 2, int zone = 4)
    {
        Serial = serial; Powered = powered; Connected = connected; Total = total;
        Dimension = dimension; Layer = layer; Zone = zone;
    }

    internal int? Serial { get; set; }
    internal bool Powered { get; set; }
    internal int Connected { get; set; }
    internal int Total { get; set; }
    internal int? Dimension { get; set; }
    internal int? Layer { get; set; }
    internal int? Zone { get; set; }
    internal string? Broken { get; set; }

    internal MapObjectReference? Address()
        => MapObjectGeneratorAddress.Create(Dimension, Layer, Zone, Serial);

    internal MapObjectObservation Read()
    {
        if (Broken == "unreadable") return new MapObjectObservation(false, null, null);
        return new MapObjectObservation(true, null, null, null,
            new MapObjectGeneratorSnapshot(Powered, Connected, Total));
    }
}

/// <summary>One generator group as the native side would report it: the zone its members stand in and its own
/// serial, carried under the same category's `group&lt;serial&gt;` key form so a group and a generator of one
/// serial stay two addresses. The counts are the group's own member readings.</summary>
internal sealed class StubGeneratorGroup
{
    internal StubGeneratorGroup(int serial = 1, int connected = 0, int total = 0,
        int dimension = 0, int layer = 2, int zone = 4)
    {
        Serial = serial; Connected = connected; Total = total;
        Dimension = dimension; Layer = layer; Zone = zone;
    }

    internal int? Serial { get; set; }
    internal int Connected { get; set; }
    internal int Total { get; set; }
    internal int? Dimension { get; set; }
    internal int? Layer { get; set; }
    internal int? Zone { get; set; }
    internal string? Broken { get; set; }

    internal MapObjectReference? Address()
        => MapObjectGeneratorAddress.CreateGroup(Dimension, Layer, Zone, Serial);

    internal MapObjectObservation Read()
    {
        if (Broken == "unreadable") return new MapObjectObservation(false, null, null);
        return new MapObjectObservation(true, null, null, null, null,
            new MapObjectGeneratorGroupSnapshot(Connected, Total));
    }
}

/// <summary>A category source over the stubs. It mirrors the native sources' rules exactly where they matter:
/// it answers only for its own category's instance type, it re-reads the address from the instance every time,
/// and a read whose subject was replaced under the same address reports the mismatch instead of the new
/// object's state.</summary>
internal sealed class StubSource : IMapObjectSource
{
    internal readonly List<string> Reports = new();

    internal StubSource(string category) => Category = category;

    public string Category { get; }
    internal bool ThrowOnRead { get; set; }

    private bool Mine(object instance) => Category == MapObjectCategories.Door ? instance is StubDoor
        : Category == MapObjectCategories.Terminal ? instance is StubTerminal
        : instance is StubGenerator or StubGeneratorGroup;

    /// <summary>The category's own key grammar: a door's key is the fixed token, a terminal's is a placement
    /// index, a generator's is its own serial or its group's. A stub that accepted any key would let a test pass
    /// on an address the real reader refuses.</summary>
    public MapObjectReference? Parse(string text) => Category == MapObjectCategories.Door
        ? MapObjectDoorAddress.TryParse(text)
        : Category == MapObjectCategories.Terminal ? MapObjectTerminalAddress.TryParse(text)
        : MapObjectGeneratorAddress.TryParse(text);

    public MapObjectReference? TryAddress(object instance) => !Mine(instance) ? null : instance switch
    {
        StubDoor door => door.Address(),
        StubTerminal terminal => terminal.Address(),
        StubGenerator generator => generator.Address(),
        StubGeneratorGroup group => group.Address(),
        _ => null
    };

    public MapObjectObservation? Read(object instance)
    {
        if (!Mine(instance)) return null;
        if (ThrowOnRead) throw new InvalidOperationException("synthetic read failure");
        return instance switch
        {
            StubDoor door => door.Read(),
            StubTerminal terminal => terminal.Read(),
            StubGenerator generator => generator.Read(),
            StubGeneratorGroup group => group.Read(),
            _ => null
        };
    }

    /// <summary>The position a stub reports, or null to stand in for a transform that did not read.</summary>
    internal double[]? PositionValue { get; set; } = new double[] { 1, 2, 3 };

    public double[]? Position(object instance) => Mine(instance) ? PositionValue : null;

    public bool IsCurrentAddress(object instance, MapObjectReference address) => Mine(instance) && instance switch
    {
        StubDoor door => door.Address() == address,
        StubTerminal terminal => terminal.Address() == address,
        StubGenerator generator => generator.Address() == address,
        StubGeneratorGroup group => group.Address() == address,
        _ => false
    };

    public void Report(string message) => Reports.Add(message);
}

internal sealed class MapObjectFixture : IDisposable
{
    private const string BranchBinding = "forge.contract.control.binding.branch";
    internal readonly RuntimeKernel Kernel = new(new RuntimeIdentity("map.object.tests", "1.0.0", RuntimeKernel.ApiVersion, "synthetic"));
    internal readonly StubSource Doors;
    internal readonly StubSource Terminals;
    internal readonly StubSource Generators;
    internal readonly MapObjectModule Module;
    internal readonly List<string> Reported = new();
    internal readonly Dictionary<string, object> Resolved = new();
    /// <summary>The climb a hit object goes through before it is addressed, as the game-bound half answers it: a
    /// component double names the map object it belongs to, and anything else names nothing.</summary>
    internal readonly Func<object, object?> Hit = instance => instance is StubHit hit ? hit.MapObject : null;
    internal bool Authority = true;
    internal long World = 1;

    internal MapObjectFixture(Func<RuntimeModule, RuntimeModule>? compose = null, Func<MapLevelReference?>? currentLevel = null)
    {
        Kernel.BeginWorld(World);
        // The control vocabulary is the kernel's own module, and the mounted plan in these cases needs one step
        // that actually executes; registering it is the same call the host makes for its built-in providers.
        Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        // The two interaction rows whose catalog shape this provider registers are bound to capabilities the
        // runtime's trigger contract owns, so the contract registers first exactly as the host registers it.
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Doors = new StubSource(MapObjectCategories.Door);
        Terminals = new StubSource(MapObjectCategories.Terminal);
        // The generator category is one more map-object category (ruling 148.4), so its reader is composed on
        // the same module exactly as the door and terminal readers are.
        Generators = new StubSource(MapObjectGeneratorAddress.Category);
        // The fixture stands in for the game-bound session: it composes the one provider definition this half
        // registers on. There is no player half here and no level-object half, so no player entity surface is
        // declared; the rows the definition binds to evaluators still need one each, and every one of them
        // refuses to answer: this fixture has no player table, no zone table and no level-object table, so a row
        // asked here is a question it cannot answer.
        Module = new MapObjectModule(Kernel, RuntimeLogLevel.Off, Doors, Terminals, Resolve, null,
            () => Authority, Reported.Add, _ => { }, currentLevel, Hit, generators: Generators);
        var definition = ModuleDefinition.Create() with
        {
            // The level-object value row and the generator value row the definition binds are their own
            // contracts' handler names, and the shapes they are resolved against live in those contracts beside
            // them, so the fixture adds them the way the sessions that implement them do instead of restating
            // either.
            Shapes = new Dictionary<string, HandlerShape>(ModuleDefinition.FormShapes, StringComparer.Ordinal)
                .Concat(LevelObjectContract.Shapes())
                .Concat(GeneratorContract.Shapes())
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            Evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
            {
                [PlayerSelectorContract.HandlerName] = _ => throw new InvalidOperationException("This fixture has no player selector."),
                [ZoneSelectorContract.HandlerName] = _ => throw new InvalidOperationException("This fixture has no zone selector."),
                [LevelObjectContract.ScanStateHandler] = _ => throw new InvalidOperationException("This fixture has no level-object reader."),
                // The generator value row is answered by the module itself: the resource table it fills is what
                // says whether a key names a generator this world reported, so the row's refusals are exercised
                // through the same evaluator the session registers.
                [GeneratorContract.GeneratorStateHandler] = context => Module.ReadGeneratorState(context)
            }
        };
        Module.Register(compose == null ? definition : compose(definition));
        if (!Kernel.StartRuntime(() => { })) throw new InvalidOperationException("Test startup failed.");
    }

    /// <summary>How the module re-resolves an address on its own, for reads that do not arrive with a callback
    /// instance. The fixture keeps the same lookup the native terminal source uses.</summary>
    private object? Resolve(string category, string address)
        => Resolved.TryGetValue(address, out var instance) ? instance : null;

    /// <summary>The door a lock callback would carry, plus the address the module must resolve it to.</summary>
    internal StubDoor Track(StubDoor door)
    {
        if (door.Address() is { } address) Resolved[address.ToString()] = door;
        return door;
    }

    internal StubTerminal Track(StubTerminal terminal)
    {
        if (terminal.Address() is { } address) Resolved[address.ToString()] = terminal;
        return terminal;
    }

    /// <summary>One generator or group a callback would carry, plus the address the module must resolve it to.
    /// A group is tracked under its own `group&lt;serial&gt;` address, which is what keeps it a second address
    /// beside the generator of the same serial.</summary>
    internal StubGenerator Track(StubGenerator generator)
    {
        if (generator.Address() is { } address) Resolved[address.ToString()] = generator;
        return generator;
    }

    internal StubGeneratorGroup Track(StubGeneratorGroup group)
    {
        if (group.Address() is { } address) Resolved[address.ToString()] = group;
        return group;
    }

    /// <summary>One entity id of the provider's single namespace, as a hook's event subject would carry it.</summary>
    internal EntityReference Reference(MapObjectReference address, long? world = null)
        => new(MapObjectModule.EntityKind + ":" + address, world ?? World, 1);

    internal void ForgeWorld(long epoch)
    {
        World = epoch;
        Kernel.BeginWorld(epoch);
    }

    /// <summary>One plan whose only entrypoint runs the kernel's branch step, mounted on `map-object` for one
    /// category and address. A trigger binding here has no handler and publishes no value, so the plan's own step
    /// is the control vocabulary and a case observes dispatch through the mount — the part of plan handling this
    /// provider owns — through the tick's own command count. `triggerCapability` is the catalog capability the
    /// plan's entrypoint triggers on, so a terminal case subscribes to a terminal fact.</summary>
    internal PlanLoadOutcome Load(string planId, string triggerCapability, string category, string reference)
        => LoadMounted(planId, triggerCapability, new { kind = "map-object", category, reference });

    /// <summary>One plan mounted on the whole level, the kind whose matcher is judged from the mount target
    /// alone: a level reference is compared against the identity the fixture's level reader answers.</summary>
    internal PlanLoadOutcome LoadLevel(string planId, string triggerCapability, string reference)
        => LoadMounted(planId, triggerCapability, new { kind = "level", reference });

    private PlanLoadOutcome LoadMounted(string planId, string triggerCapability, object attachment)
    {
        var (pins, capabilities, providers, support) = PinTable();
        string trigger = MapObjectContract.Binding(triggerCapability);
        var used = new[] { BranchBinding, trigger }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var pinRows = used.Select(id => (object)new
        {
            bindingId = id, capabilityId = pins[id].Capability, capabilityVersion = capabilities[pins[id].Capability],
            providerId = pins[id].Provider, providerVersion = providers[pins[id].Provider], handler = pins[id].Handler
        }).ToArray();
        // The contract is resolved at the revision the registry really carries: a canonical Trigger row is
        // declared once by the trigger contract and keeps that contract's own version, not this provider's.
        var triggerContract = Kernel.ResolveGraphContract(triggerCapability, capabilities[triggerCapability], RuntimeJson.EmptyObject);
        var branchContract = Kernel.ResolveGraphContract("forge.control.flow.branch", capabilities["forge.control.flow.branch"], RuntimeJson.EmptyObject);
        string json = RuntimeJson.From(new
        {
            schemaVersion = 4, kind = "forge-runtime-plan", planId,
            resource = new { id = "author.resource", revision = "revision-1" },
            runtime = Kernel.Identity, domain = "map", authority = "host", failurePolicy = "stop-entrypoint",
            // The plan's permissions are exactly the binding closure's own union (permission-lock), so they are
            // read from the manifest instead of being restated: a plan whose trigger binding carries no
            // permission, like the expedition one, locks an empty set and would be refused by a fixed literal.
            permissions = used.SelectMany(id => support[id]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
            bindings = pinRows,
            attachments = new[] { attachment },
            entrypoints = new[]
            {
                new
                {
                    nodeId = "Entry", binding = Array.IndexOf(used, trigger),
                    layout = Layout(triggerContract), start = 0,
                    steps = new object[]
                    {
                        new
                        {
                            nodeId = "S0_branch", nodeKind = "control", binding = Array.IndexOf(used, BranchBinding),
                            layout = Layout(branchContract),
                            // The branch is a structural step: its condition is a compile-time constant, and the
                            // case only needs a step the kernel actually runs when the mount lets the event in.
                            inputs = new object[] { new { slot = Port(branchContract, "inputs", "condition"), value = true } },
                            successors = new int?[] { null, null }
                        }
                    }
                }
            }
        }).GetRawText();
        var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded(planId + ".plan.json", json) })[0];
        // A plan that does not load names the rule it broke rather than leaving the case to fail on a count.
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Code + ": " + outcome.Detail);
        return outcome;
    }

    private static object Layout(JsonElement contract) => new
    {
        inputs = Sides(contract, "inputs"),
        outputs = Sides(contract, "outputs"),
        constants = Array.Empty<object>(),
        promoted = Array.Empty<int>()
    };

    /// <summary>The slot index of one declared port of a resolved contract, as a plan's own wire rows name it.</summary>
    private static int Port(JsonElement contract, string side, string id)
        => contract.GetProperty(side).EnumerateArray()
            .Select((port, index) => (port, index))
            .Single(x => x.port.GetProperty("id").GetString() == id).index;

    /// <summary>The dense slot layout the plan loader re-derives from the registered contract. It is
    /// assembly-internal and the fixture needs the same rule the compiler publishes, so it is read here rather
    /// than restated — a restated layout would only prove the test agrees with itself.</summary>
    private static object Sides(JsonElement contract, string side)
        => typeof(RuntimeKernel).Assembly.GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!
            .GetMethod("Layout", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { contract, side })!;

    /// <summary>Every registered binding as a pin row, keyed by binding id: the plan loader locks a plan's pin
    /// table against the closure it actually uses and against the registry's own versions, so every row is
    /// taken from the manifest rather than restated. The support rows come from the same manifest, because the
    /// plan's permission lock is that closure's own union.</summary>
    private (Dictionary<string, (string Capability, string Provider, string Handler)> Pins,
        Dictionary<string, string> Capabilities, Dictionary<string, string> Providers,
        Dictionary<string, string[]> Support) PinTable()
    {
        var manifest = RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry");
        var capabilities = manifest.GetProperty("capabilities").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("version").GetString()!, StringComparer.Ordinal);
        var providers = manifest.GetProperty("providers").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("version").GetString()!, StringComparer.Ordinal);
        var pins = manifest.GetProperty("bindings").EnumerateArray().ToDictionary(
            b => b.GetProperty("id").GetString()!,
            b => (Capability: b.GetProperty("capabilityId").GetString()!,
                Provider: b.GetProperty("providerId").GetString()!,
                Handler: b.TryGetProperty("handler", out var handler) && handler.ValueKind == JsonValueKind.String
                    ? handler.GetString()! : ""), StringComparer.Ordinal);
        var support = RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("bindingSupport").EnumerateArray().ToDictionary(
            s => s.GetProperty("bindingId").GetString()!,
            s => s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!).ToArray(),
            StringComparer.Ordinal);
        return (pins, capabilities, providers, support);
    }

    public void Dispose()
    {
        Module.Dispose();
        Kernel.StopRuntime();
    }
}
