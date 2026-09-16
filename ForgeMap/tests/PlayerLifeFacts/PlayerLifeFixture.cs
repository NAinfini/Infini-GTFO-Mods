using System.Globalization;
using System.Text.Json;
using System.Reflection;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeMap.Tests.PlayerLifeObs;

/// <summary>One synthetic player life: the native objects the production readers reach, plus the recorded entity
/// the identity gives that life. Every field is driven by the case, so a transition is produced by writing the
/// same native state the game writes and nothing else.</summary>
internal sealed class Life
{
    internal required SNet_Player Player { get; init; }
    internal required PlayerAgent Agent { get; init; }
    internal required EntityReference Reference { get; init; }
    internal PlayerLocomotion Locomotion => Agent.Locomotion!;
    internal PLOC_Downed Downed => (PLOC_Downed)Locomotion.CurrentStateInstance!;

    /// <summary>The native write a downing performs: the locomotion machine enters the downed state.</summary>
    internal void Fall()
    {
        Locomotion.m_currentStateEnum = PlayerLocomotion.PLOC_State.Downed;
        Downed.m_isRevived = false;
    }

    /// <summary>What the game does on a revive: the downed state records it, and the machine leaves the state.</summary>
    internal void Revive(PlayerLocomotion.PLOC_State state = PlayerLocomotion.PLOC_State.Stand)
    {
        Downed.m_isRevived = true;
        Locomotion.m_currentStateEnum = state;
    }

    /// <summary>The write that ends a life: the agent's own alive flag.</summary>
    internal void Kill() => Agent.Alive = false;

    internal void Move(float x, float y, float z) => Agent.Position = new Vector3(x, y, z);
}

/// <summary>The player-life fixture. The kernel is the real one and the registration is a real Map provider
/// registration, so a fact published here passes the same registry checks, the same binding ownership and the
/// same event ledger the game's own host applies. The game half is synthetic, but it is reached through the same
/// door production uses: the registered `PlayerIdentityModule`, which is the one thing the life half reads a
/// life through.
///
/// The seven catalog rows are the `PlayerLifeContract` text with its ids moved under this fixture's own
/// provider, because a module owns the capabilities it declares and the shipped ids belong to the runtime's
/// trigger contract. Ports, labels, parameter metadata and handler names are the shipped text, and production
/// finds its binding ids through the same mapping, so a renamed row or a row without a binding fails here. The
/// one field a fixture cannot make real is the provider id itself: the registration that owns the Map provider
/// belongs to the game-bound session.</summary>
internal sealed class PlayerLifeFixture : IDisposable
{
    internal readonly RuntimeKernel Kernel = new(new RuntimeIdentity("player.life.tests", "1.0.0", RuntimeKernel.ApiVersion, "synthetic"));
    internal readonly List<string> Reported = new();
    internal readonly List<string> Logged = new();
    internal readonly LifeHost Host = new();
    internal readonly PlayerIdentityModule Identity = new();
    internal bool Authority = true;
    internal long World = 1;
    internal PlayerLifeFacts Facts = null!;

    internal PlayerLifeFixture()
    {
        Kernel.BeginWorld(World);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(LevelMounts(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(PlayerLifeContracts(), RuntimeLogLevel.Off);
        var registration = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = PlayerLifeContract.ProviderId, kind = "native", version = "0.1.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(),
            bindings = PlayerLifeRows()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), PlayerLifeSupport(), EntityResolvers()), RuntimeLogLevel.Off);
        Identity.Attach(World);
        PlayerIdentityModule.Current = Identity;
        Facts = new PlayerLifeFacts(registration, Kernel, () => Authority, Reported.Add, Logged.Add)
        {
            World = Identity,
            BindingOverride = BindingOf
        };
        Host.Facts = Facts;
        Plugin.Session = Host;
        if (!Kernel.StartRuntime(() => { })) throw new InvalidOperationException("Test startup failed.");
    }

    /// <summary>One binding row per fact. The id is read from the shipped contract through the row the fixture
    /// declared, so a row whose capability id changed without its binding id changing is refused here and not in
    /// the game.</summary>
    /// <summary>The one attachment kind this fixture answers: a `level` mount is judged from the mount target
    /// alone, which is the scope kind, and every level is this fixture's.</summary>
    private static RuntimeModule LevelMounts() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = "forge.test.player_life_mounts", kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(),
        bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((_, _) => true)
        }
    };

    private object[] PlayerLifeRows()
        => PlayerLifeContract.Facts.Select(entry => (object)new
        {
            id = BindingOf(entry.Fact),
            capabilityId = CapabilityOf(entry.Fact),
            providerId = PlayerLifeContract.ProviderId,
            handler = PlayerLifeContract.HandlerOf(entry.Fact),
            role = "observe",
            status = "implemented",
            dependencies = Array.Empty<string>(),
            requires = Array.Empty<string>()
        }).ToArray();

    private BindingSupport[] PlayerLifeSupport()
        => PlayerLifeContract.Facts.Select(entry => new BindingSupport(BindingOf(entry.Fact), "implementation-only", Array.Empty<string>())).ToArray();

    /// <summary>The one thing a published fact cannot do without: the kernel checks every entity port of every
    /// event against the resolver that owns the kind, and an event whose kind no registered provider resolves is
    /// refused with `entity-resolver` before any plan is asked. Production's Map provider registers exactly this
    /// owner for `gtfo.player` on its own registration; the fixture registers the same predicate against its
    /// synthetic identity, so a fact about a life the identity no longer holds is refused by the kernel's own
    /// check and never by the publishing half.</summary>
    private IReadOnlyDictionary<string, Func<EntityReference, bool>> EntityResolvers()
        => new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
        {
            [PlayerIdentityModule.EntityKind] = Identity.IsCurrent
        };

    /// <summary>The provider the fixture's own capability rows and bindings belong to. The shipped ids
    /// belong to the runtime's trigger contract, which a module cannot declare for itself.</summary>
    internal const string ContractProvider = "forge.test.player_life";

    internal static string CapabilityOf(string fact) => "forge.test.player_life." + fact;

    internal static string BindingOf(string fact) => PlayerLifeContract.ProviderId + ".binding.test." + fact;

    /// <summary>The shipped rows of the runtime's own trigger contract with the provider that owns them replaced
    /// by this fixture's, so the declared shapes are the shipped ones and the ids are the fixture's. Every other
    /// field is copied verbatim.</summary>
    private RuntimeModule PlayerLifeContracts()
    {
        var shipped = TriggerContracts.Rows(PlayerLifeContract.Facts.Select(entry => entry.Capability).ToArray());
        var rows = shipped.Select(row =>
        {
            var capability = row.GetProperty("id").GetString()!;
            var fact = PlayerLifeContract.Facts.Single(entry => entry.Capability == capability).Fact;
            return "{ \"id\": \"" + CapabilityOf(fact) + "\", \"owner\": \"" + "forge.test.player_life"
                + "\", \"kind\": \"" + row.GetProperty("kind").GetString() + "\", \"label\": "
                + Quote(row.GetProperty("label").GetString()!) + ", \"version\": \"" + row.GetProperty("version").GetString()
                + "\", \"parameters\": " + row.GetProperty("parameters").GetRawText()
                + ", \"graph\": " + row.GetProperty("graph").GetRawText() + " }";
        });
        string registry = "{\"providers\":[{\"id\":\"forge.test.player_life\",\"kind\":\"native\",\"version\":\"1.0.0\",\"dependencies\":[]}],"
            + "\"capabilities\":[" + string.Join(",", rows) + "],\"bindings\":[]}";
        return new RuntimeModule(RuntimeKernel.ApiVersion, registry, new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
    }

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>One life the identity records, exactly as the spawn readback would: the agent, the player that
    /// owns it, and the entity number the module assigns in first-recorded order.</summary>
    internal Life Spawn(ulong lookup, float x = 0, float y = 0, float z = 0, bool bot = false)
    {
        int number = Identity.LifeCount + 1;
        var player = new SNet_Player { Lookup = lookup, IsBot = bot, SlotIndex = number - 1 };
        var agent = new PlayerAgent { Owner = player, PlayerSlotIndex = number - 1, Position = new Vector3(x, y, z) };
        var downed = new PLOC_Downed { m_owner = agent };
        var locomotion = new PlayerLocomotion
        {
            m_currentStateEnum = PlayerLocomotion.PLOC_State.Stand, CurrentStateInstance = downed
        };
        agent.Locomotion = locomotion;
        agent.ReviveInteraction = new Interact_Revive { m_owner = agent, Agent = null };
        player.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
        PlayerManager.PlayerAgentsInLevel.Add(agent);
        var life = new Life
        {
            Player = player, Agent = agent,
            Reference = new EntityReference(PlayerIdentityModule.EntityKind + ":" + number.ToString(CultureInfo.InvariantCulture), World, number)
        };
        Identity.Record(life.Reference, agent, player);
        return life;
    }

    internal void Forget(Life life) => Identity.Forget(life.Reference);

    internal void ForgeWorld(long epoch)
    {
        World = epoch;
        Identity.Attach(epoch);
        Kernel.BeginWorld(epoch);
    }

    internal void Advance(long tick, bool host = true) => Kernel.Advance(tick, host);

    /// <summary>Registers one plan whose entrypoint triggers on one player-life fact and whose only step is the
    /// kernel's own branch, attached to the level this fixture's plans mount on, so the mount target the payload
    /// carries is the one the matcher compares. A fact the runtime hands to the kernel is only dispatched when a
    /// plan claims it, so a case that wants to observe a publication mounts one for every fact it asserts on: the
    /// answer a publish gives is the kernel's own `queued` only when a claim took it, and `ignored` otherwise.
    /// Two facts of one case are two mounts, because a plan's entrypoint answers for exactly one binding.</summary>
    internal void Mount(string planId, string fact)
    {
        string trigger = BindingOf(fact);
        const string BranchBinding = "forge.contract.control.binding.branch";
        var (pins, capabilities, providers, support) = PinTable();
        var used = new[] { BranchBinding, trigger }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var pinRows = used.Select(id => (object)new
        {
            bindingId = id, capabilityId = pins[id].Capability, capabilityVersion = capabilities[pins[id].Capability],
            providerId = pins[id].Provider, providerVersion = providers[pins[id].Provider], handler = pins[id].Handler
        }).ToArray();
        var triggerContract = Kernel.ResolveGraphContract(CapabilityOf(fact), capabilities[CapabilityOf(fact)], RuntimeJson.EmptyObject);
        var branchContract = Kernel.ResolveGraphContract("forge.control.flow.branch", capabilities["forge.control.flow.branch"], RuntimeJson.EmptyObject);
        string json = RuntimeJson.From(new
        {
            schemaVersion = 4, kind = "forge-runtime-plan", planId,
            resource = new { id = "author.resource", revision = "revision-1" },
            runtime = Kernel.Identity, domain = "map", authority = "host", failurePolicy = "stop-entrypoint",
            permissions = used.SelectMany(id => support[id]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
            bindings = pinRows,
            attachments = new object[] { new { kind = "level", reference = "31:A:0" } },
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

    private static object Layout(JsonElement contract) => new
    {
        inputs = Sides(contract, "inputs"), outputs = Sides(contract, "outputs"),
        constants = Array.Empty<object>(), promoted = Array.Empty<int>()
    };

    private static int Port(JsonElement contract, string side, string id)
        => contract.GetProperty(side).EnumerateArray().Select((port, index) => (port, index))
            .Single(x => x.port.GetProperty("id").GetString() == id).index;

    /// <summary>The dense slot layout the plan loader re-derives from the registered contract. It is
    /// assembly-internal and this fixture needs the same rule the compiler publishes, so it is read here rather
    /// than restated: a restated layout would only prove the case agrees with itself.</summary>
    private static object Sides(JsonElement contract, string side)
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

    public void Dispose()
    {
        Plugin.Session = null;
        try { Facts.Dispose(); } catch (RuntimeContractException) { }
        PlayerIdentityModule.Current = null;
    }
}
