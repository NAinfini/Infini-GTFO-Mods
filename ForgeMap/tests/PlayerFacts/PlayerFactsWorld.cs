using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;

namespace ForgeMap.Tests.PlayerFacts;

/// <summary>One recorded life as the fixture hands it to a case: the entity reference and the native agent and
/// receiver behind it.</summary>
internal sealed record LifeFixture(EntityReference Reference, PlayerAgent Agent, Dam_PlayerDamageBase Damage);

/// <summary>A native object that stands for an enemy: the fixture's own registration for the `gtfo.enemy` kind
/// answers it, which is how the damage fact's attacker port is exercised without the enemy package.</summary>
internal sealed class EnemyTarget
{
    internal required EntityReference Reference { get; init; }
}

/// <summary>The player-state observation as the facts half reaches it: a table of lives the case writes, and the
/// authority gate. Every transition the production half publishes is decided from this table alone, so a case
/// states the world and asserts the facts.</summary>
internal sealed class TestVitalsSource : IPlayerVitalsSource
{
    internal readonly Dictionary<string, (EntityReference Reference, PlayerVitals Vitals, object Agent)> Lives = new(StringComparer.Ordinal);
    internal readonly Dictionary<IntPtr, string> ByAgent = new();
    internal bool CanPublish { get; set; } = true;
    internal int Reconciles { get; private set; }

    public bool Authoritative => CanPublish;
    public void Reconcile() => Reconciles++;

    internal void Record(LifeFixture life, float health, float maximum, float infection, bool alive = true)
    {
        Lives[life.Reference.Id] = (life.Reference, new PlayerVitals(life.Reference, health, maximum, infection, alive), life.Agent);
        ByAgent[life.Agent.Pointer] = life.Reference.Id;
    }

    public EntityReference? ReferenceOf(object? instance)
    {
        if (instance == null) return null;
        // The same mapping the native source performs: a damage receiver resolves through its own owner, so the
        // hooks that are handed the receiver and the hooks that are handed the agent reach one life.
        if (instance is Dam_PlayerDamageBase damage) return damage.Owner == null ? null : ReferenceOf(damage.Owner);
        if (instance is PlayerAgent agent && ByAgent.TryGetValue(agent.Pointer, out var id) && Lives.TryGetValue(id, out var recorded))
            return recorded.Reference;
        foreach (var candidate in Lives.Values)
            if (ReferenceEquals(candidate.Agent, instance)) return candidate.Reference;
        return null;
    }

    public PlayerVitals? Read(EntityReference reference)
        => Lives.TryGetValue(reference.Id, out var entry) ? entry.Vitals : null;
}

/// <summary>The player-value source as the value rows reach it: a table a case writes, and the four refusal
/// codes the rows have to carry through unchanged.</summary>
internal sealed class TestValueSource : IPlayerValueSource
{
    internal double Infection { get; set; }
    internal bool InfectionReadable { get; set; } = true;
    internal EntityReference? Wielded { get; set; }
    internal bool WieldedReadable { get; set; } = true;
    internal string WieldedCode { get; set; } = "equipment-unresolved";
    internal PlayerAmmo Ammo { get; set; } = new(7, 12, 30);
    internal bool AmmoReadable { get; set; } = true;
    internal string AmmoCode { get; set; } = "no-wielded-gear";
    internal EntityReference? Carried { get; set; }
    internal bool CarriedReadable { get; set; } = true;
    internal string CarriedCode { get; set; } = "backpack-unavailable";
    internal PlayerTool Tool { get; set; } = new(null, 0, 1, 0, 0);
    internal bool ToolReadable { get; set; } = true;
    internal string ToolCode { get; set; } = "no-wielded-gear";

    public bool TryInfection(EntityReference reference, out double value, out string code)
    {
        value = Infection; code = InfectionReadable ? "" : "readback-exception";
        return InfectionReadable;
    }

    public bool TryWieldedGear(EntityReference reference, out EntityReference? equipment, out string code)
    {
        equipment = Wielded; code = WieldedReadable ? "" : WieldedCode;
        return WieldedReadable;
    }

    public bool TryAmmo(EntityReference reference, out PlayerAmmo ammo, out string code)
    {
        ammo = Ammo; code = AmmoReadable ? "" : AmmoCode;
        return AmmoReadable;
    }

    public bool TryCarriedItem(EntityReference reference, out EntityReference? item, out string code)
    {
        item = Carried; code = CarriedReadable ? "" : CarriedCode;
        return CarriedReadable;
    }

    public bool TryTool(EntityReference reference, out PlayerTool tool, out string code)
    {
        tool = Tool; code = ToolReadable ? "" : ToolCode;
        return ToolReadable;
    }
}

/// <summary>The fixture: the real kernel, one real Map provider registration carrying this slice's rows, the two
/// observation halves attached to it, and a working player identity for the action layer.
///
/// The three observed capability rows are the shipped text with their ids moved under this fixture's own provider,
/// because a module may only declare capabilities of its own and the shipped ids belong to the runtime's trigger
/// contract. Ports, labels, parameter metadata and binding names are the shipped text, so a renamed row or a row
/// without a binding fails here. The value rows and the down action are this provider's own capabilities and are
/// registered verbatim.</summary>
internal sealed class PlayerFactsWorld : IDisposable
{
    private const string TestContractProvider = "forge.test.player_facts";
    private static long world = 500;
    private long life = 100;

    internal readonly RuntimeKernel Kernel = new(new RuntimeIdentity("player.facts.tests", "1.0.0", RuntimeKernel.ApiVersion, "synthetic"));
    internal readonly List<string> Reported = new();
    internal readonly List<string> Logged = new();
    internal readonly TestVitalsSource Source = new();
    internal readonly TestValueSource Values = new();
    internal readonly Dictionary<string, EnemyTarget> Enemies = new(StringComparer.Ordinal);
    internal readonly PlayerIdentityModule Identity = new();
    internal RuntimeModuleHandle Registration = null!;
    internal PlayerStateFacts State = null!;
    internal PlayerEventFacts Events = null!;
    internal PlayerValueReads Reads = null!;

    internal PlayerFactsWorld()
    {
        Kernel.BeginWorld(++world);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TestCapabilities(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(LevelMounts(), RuntimeLogLevel.Off);
        var module = new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
            DownHandlers(), Support()) with
        {
            Shapes = Shapes(),
            Evaluators = PlayerValueReads.Evaluators(),
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = reference => Source.Read(reference) != null,
                ["gtfo.enemy"] = reference => Enemies.ContainsKey(reference.Id)
            },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
            {
                ["gtfo.enemy"] = instance => instance is EnemyTarget target ? target.Reference : null
            }
        };
        var registration = Kernel.RegisterModule(module, RuntimeLogLevel.Off);
        Registration = registration;
        Identity.World = world;
        PlayerIdentityModule.Current = Identity;
        State = PlayerStateFacts.Attach(registration, Kernel, Source, Logged.Add, Reported.Add,
            fact => TestBindingOf("state", fact));
        Events = PlayerEventFacts.Attach(registration, Kernel, Source, Logged.Add, Reported.Add,
            fact => TestBindingOf("event", fact));
        Reads = PlayerValueReads.Attach(Values);
        if (!Kernel.StartRuntime(() => { })) throw new InvalidOperationException("Test startup failed.");
        MountObserved();
    }

    /// <summary>The one attachment kind this fixture answers: a `level` mount is judged from the mount target
    /// alone, so every one of this fixture's plans is attached.</summary>
    private static RuntimeModule LevelMounts() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = "forge.test.player_facts_mounts", kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(),
        bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((_, _) => true)
        }
    };

    /// <summary>One plan per observed fact, each mounting the level and claiming exactly that fact: the kernel
    /// only answers a publication with `queued` when a plan claims it, so a fact a case asserts on needs a claim.
    /// The plan's one step is the kernel's own branch, which needs no handler of this package.</summary>
    private void MountObserved()
    {
        int index = 0;
        foreach (var (fact, _) in PlayerStateContract.Facts) Mount("state." + fact, TestBindingOf("state", fact), TestCapabilityOf("state", fact), ++index);
        foreach (var (fact, _) in PlayerEventContract.Facts) Mount("event." + fact, TestBindingOf("event", fact), TestCapabilityOf("event", fact), ++index);
    }

    private void Mount(string name, string triggerBinding, string triggerCapability, int index)
    {
        const string BranchBinding = "forge.contract.control.binding.branch";
        var (pins, capabilities, providers, support) = PinTable();
        var used = new[] { BranchBinding, triggerBinding }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var pinRows = used.Select(id => (object)new
        {
            bindingId = id, capabilityId = pins[id].Capability, capabilityVersion = capabilities[pins[id].Capability],
            providerId = pins[id].Provider, providerVersion = providers[pins[id].Provider], handler = pins[id].Handler
        }).ToArray();
        var triggerContract = Kernel.ResolveGraphContract(triggerCapability, capabilities[triggerCapability], RuntimeJson.EmptyObject);
        var branchContract = Kernel.ResolveGraphContract("forge.control.flow.branch", capabilities["forge.control.flow.branch"], RuntimeJson.EmptyObject);
        string planId = "test.player-facts." + name;
        string json = RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId,
            resource = new { id = "author.resource", revision = "revision-1" },
            runtime = Kernel.Identity, domain = "player", authority = "host", failurePolicy = "stop-entrypoint",
            permissions = used.SelectMany(id => support[id]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
            bindings = pinRows,
            attachments = new object[] { new { kind = "level", reference = "31:A:0" } },
            entrypoints = new[]
            {
                new
                {
                    nodeId = "Entry", binding = Array.IndexOf(used, triggerBinding),
                    layout = Layout(triggerContract), start = 0,
                    steps = new object[]
                    {
                        new
                        {
                            nodeId = "S0_branch", nodeKind = "control", binding = Array.IndexOf(used, BranchBinding),
                            layout = Layout(branchContract),
                            inputs = new object[] { new { slot = Port(branchContract, "inputs", "condition"), value = true } },
                            successors = new int?[] { null, null }
                        }
                    }
                }
            }
        }).GetRawText();
        var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded(planId + ".plan.json", json) })[0];
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Code + ": " + outcome.Detail);
    }

    private static object Layout(System.Text.Json.JsonElement contract) => new
    {
        inputs = Sides(contract, "inputs"), outputs = Sides(contract, "outputs"),
        constants = Array.Empty<object>(), promoted = Array.Empty<int>()
    };

    private static int Port(System.Text.Json.JsonElement contract, string side, string id)
        => contract.GetProperty(side).EnumerateArray().Select((port, position) => (port, position))
            .Single(x => x.port.GetProperty("id").GetString() == id).position;

    /// <summary>The dense slot layout the plan loader re-derives from the registered contract. It is
    /// assembly-internal and this fixture needs the same rule the compiler publishes, so it is read here rather
    /// than restated.</summary>
    private static object Sides(System.Text.Json.JsonElement contract, string side)
        => typeof(RuntimeKernel).Assembly.GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!
            .GetMethod("Layout", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { contract, side })!;

    private (Dictionary<string, (string Capability, string Provider, string Handler)> Pins,
        Dictionary<string, string> Capabilities, Dictionary<string, string> Providers,
        Dictionary<string, string[]> Support) PinTable()
    {
        var manifest = RuntimeJson.Parse(Kernel.ExportManifest());
        var registry = manifest.GetProperty("registry");
        var capabilities = registry.GetProperty("capabilities").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("version").GetString()!, StringComparer.Ordinal);
        var providers = registry.GetProperty("providers").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("version").GetString()!, StringComparer.Ordinal);
        var pins = registry.GetProperty("bindings").EnumerateArray().ToDictionary(
            b => b.GetProperty("id").GetString()!,
            b => (Capability: b.GetProperty("capabilityId").GetString()!,
                Provider: b.GetProperty("providerId").GetString()!,
                Handler: b.TryGetProperty("handler", out var handler) && handler.ValueKind == JsonValueKind.String
                    ? handler.GetString()! : ""), StringComparer.Ordinal);
        var support = manifest.GetProperty("bindingSupport").EnumerateArray().ToDictionary(
            s => s.GetProperty("bindingId").GetString()!,
            s => s.GetProperty("requiredPermissions").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            StringComparer.Ordinal);
        return (pins, capabilities, providers, support);
    }

    /// <summary>One player life with a receiver, recorded in both halves: the observation source and the identity
    /// the action layer commits through.</summary>
    internal LifeFixture Spawn(float health = 100f, float maximum = 100f, float infection = 0f)
    {
        var reference = new EntityReference(PlayerIdentityModule.EntityKind + ":" + (++life), Kernel.WorldEpoch, life);
        var agent = new PlayerAgent { Pointer = (IntPtr)life, Position = new UnityEngine.Vector3(1, 2, 3) };
        var damage = new Dam_PlayerDamageBase { Pointer = (IntPtr)(life + 1000), Owner = agent, Health = health, HealthMax = maximum, Infection = infection };
        agent.Damage = damage;
        var fixture = new LifeFixture(reference, agent, damage);
        Source.Record(fixture, health, maximum, infection);
        Identity.Record(reference, agent);
        return fixture;
    }

    /// <summary>One enemy the kernel's own `gtfo.enemy` instance resolver answers, for the attacker port.</summary>
    internal EnemyTarget Enemy()
    {
        var reference = new EntityReference("gtfo.enemy:" + (++life), Kernel.WorldEpoch, life);
        var enemy = new EnemyTarget { Reference = reference };
        Enemies[reference.Id] = enemy;
        return enemy;
    }

    internal EntityReference Entity(EntityReference reference) => reference;

    /// <summary>The provider declaration this slice's registration composes: this provider, the value rows and the
    /// down row it owns, and one binding per row.</summary>
    private string Registry() => RuntimeJson.From(new
    {
        providers = new[] { new { id = PlayerStateContract.ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = PlayerStateContract.ValueRows().Append(PlayerCommandContract.DownRow()).ToArray(),
        bindings = TestObservedBindings().Concat(PlayerStateContract.ValueBindings())
            .Append(DownBinding()).ToArray()
    }).GetRawText();

    private object DownBinding() => PlayerCommandContract.Rows()[2];

    /// <summary>The one execute handler this registration declares: the down action, whose capability row the
    /// fixture owns. The damage and revive handlers implement capabilities the combat contract owns, so a test
    /// registration cannot declare them and the two handlers are driven directly instead.</summary>
    private static IReadOnlyDictionary<string, CommandHandler> DownHandlers()
        => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [PlayerCommandContract.DownHandlerName] = PlayerCommandActions.Down
        };

    private IEnumerable<object> TestObservedBindings()
        => PlayerStateContract.Facts.Select(entry => (object)new
        {
            id = TestBindingOf("state", entry.Fact),
            capabilityId = TestCapabilityOf("state", entry.Fact),
            providerId = PlayerStateContract.ProviderId,
            handler = PlayerStateContract.HandlerOf(entry.Fact),
            role = "observe",
            status = "implemented",
            dependencies = Array.Empty<string>(),
            requires = Array.Empty<string>()
        }).Concat(PlayerEventContract.Facts.Select(entry => (object)new
        {
            id = TestBindingOf("event", entry.Fact),
            capabilityId = TestCapabilityOf("event", entry.Fact),
            providerId = PlayerStateContract.ProviderId,
            handler = PlayerEventContract.HandlerOf(entry.Fact),
            role = "observe",
            status = "implemented",
            dependencies = Array.Empty<string>(),
            requires = Array.Empty<string>()
        }));

    private BindingSupport[] Support()
        => PlayerStateContract.Facts.Select(entry => new BindingSupport(TestBindingOf("state", entry.Fact), "implementation-only", Array.Empty<string>()))
            .Concat(PlayerEventContract.Facts.Select(entry => new BindingSupport(TestBindingOf("event", entry.Fact), "implementation-only", Array.Empty<string>())))
            .Concat(PlayerStateContract.ValueSupport())
            .Concat(PlayerCommandContract.Support().Skip(2))
            .ToArray();

    private static IReadOnlyDictionary<string, HandlerShape> Shapes()    {
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        foreach (var (name, shape) in PlayerStateContract.ValueShapes()) shapes[name] = shape;
        foreach (var (name, shape) in PlayerCommandContract.Shapes())
            if (name == PlayerCommandContract.DownHandlerName) shapes[name] = shape;
        return shapes;
    }

    /// <summary>Every observed capability row the fixture declares, with this fixture's ids and the runtime's
    /// own text: the rows are read by capability id from the trigger contract that ships them instead of restated
    /// here, so a port that contract changes fails this fixture's cases rather than drifting in a private copy.</summary>
    private RuntimeModule TestCapabilities()
    {
        var facts = PlayerStateContract.Facts.Concat(PlayerEventContract.Facts).ToArray();
        var shipped = TriggerContracts.Rows(facts.Select(entry => entry.Capability).ToArray());
        var rows = new List<string>(shipped.Count);
        foreach (var row in shipped)
            rows.Add(Rewrite(row.GetRawText(), FactOf(row.GetProperty("id").GetString()!)));
        string registry = "{\"providers\":[{\"id\":\"" + TestContractProvider + "\",\"kind\":\"native\",\"version\":\"1.0.0\",\"dependencies\":[]}],"
            + "\"capabilities\":[" + string.Join(",", rows) + "],\"bindings\":[]}";
        return new RuntimeModule(RuntimeKernel.ApiVersion, registry, new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
    }

    private static string FactOf(string capabilityId)
    {
        foreach (var family in new[] { PlayerStateContract.Facts, PlayerEventContract.Facts })
            foreach (var (fact, capability) in family)
                if (capability == capabilityId) return fact;
        throw new InvalidOperationException("Unknown capability: " + capabilityId);
    }

    private static string Rewrite(string row, string fact)
    {
        var parsed = RuntimeJson.Parse(row);
        bool state = PlayerStateContract.Facts.Any(entry => entry.Fact == fact);
        return "{ \"id\": " + Quote(TestCapabilityOf(state ? "state" : "event", fact))
            + ", \"owner\": \"" + TestContractProvider
            + "\", \"kind\": \"" + parsed.GetProperty("kind").GetString() + "\", \"label\": "
            + Quote(parsed.GetProperty("label").GetString()!) + ", \"version\": \"" + parsed.GetProperty("version").GetString()
            + "\", \"parameters\": " + parsed.GetProperty("parameters").GetRawText()
            + ", \"graph\": " + parsed.GetProperty("graph").GetRawText() + " }";
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    internal static string TestCapabilityOf(string family, string fact) => "forge.test.player_facts." + family + "." + fact;

    internal static string TestBindingOf(string family, string fact) => PlayerStateContract.ProviderId + ".binding.test." + family + "." + fact;

    public void Dispose()
    {
        PlayerIdentityModule.Current = null;
        State.Dispose();
        Events.Dispose();
        PlayerValueReads.Detach(Reads);
    }
}
