using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;
using SNetwork;

namespace ForgeMap.Tests.MapSelectorDispatch;

/// <summary>
/// End-to-end dispatch of the `forge.selector.target.players` query step through the real RuntimeKernel: the
/// production Map player identity module, the production selector binding, a test trigger and a test action,
/// and one schemaVersion 1 plan attached to `level`. The plan loader, the activation memo, the budgeted query
/// session and the dispatch walk are the kernel's own; only the native player objects are the managed doubles
/// the adapter suite already uses (source-linked, synthetic and NOT game-verified). No Harmony patch is
/// installed and the game is never started.
/// </summary>
public sealed class MapSelectorDispatchTests
{
    private const string Domain = "player";
    private const string Provider = "test.selq.dispatch";
    private const string TriggerBinding = Provider + ".binding.fired";
    private const string RecordBinding = Provider + ".binding.act";
    private const string SweepBinding = Provider + ".binding.sweep";
    private const string TriggerCapability = Provider + ".fired";
    private const string RecordCapability = Provider + ".record";
    private const string SweepCapability = Provider + ".sweep";
    /// <summary>The level the plan hangs on. The Map provider owns the `level` kind here — no kind belongs to
    /// the kernel — and the mount is judged from the target alone, so the plan is dispatched in this level.</summary>
    private const string PlanLevel = "31:A:0";

    // The doubles, the registered module and the kernel's per-tick query budget are process-wide static state.
    public MapSelectorDispatchTests()
    {
        PlayerManager.Reset();
        SNet.IsMaster = true;
    }

    [Fact]
    public void query_dispatch_selects_every_recorded_life_whatever_its_life_state()
    {
        using var s = Start("selq.select");
        Spawn(76561198000000001);
        var bot = Spawn(4242424242, bot: true);
        var client = Spawn(76561198000000002);
        // PlayerSelector answers with every life the module still resolves: life state is an observation, not a
        // selection input (PlayerSelector.cs:49-61 with PlayerIdentityModule.cs:194-202).
        bot.Agent.Locomotion = new PlayerLocomotion { m_currentStateEnum = PlayerLocomotion.PLOC_State.Downed };
        client.Agent.Alive = false;
        s.Read();
        Require(s.Kernel.Advance(1, true).CommandsExecuted == 0, "An idle world dispatched a command.");

        Queue(s, "selq.select-1");
        var tick = s.Kernel.Advance(1, true);
        Require(tick.CommandsExecuted == 1 && tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "succeeded",
            "Expected one succeeded record command: " + Describe(tick));
        Require(tick.Commands[0].NodeId == "C_record", "The record action's own step was not the command's step.");
        var expected = new[] { Reference(1, 1, 1), Reference(2, 1, 2), Reference(3, 1, 3) };
        Require(s.Recorded.SequenceEqual(new[] { Spell(expected) }),
            "The record action did not receive the selector's own set: " + string.Join("|", s.Recorded));
        // The three lives really are alive/downed/dead, so the set above is not three identical players. The
        // observation is what tells them apart (PlayerObservation.cs:47-55).
        var observed = s.Kernel.InspectEntities(expected);
        Require(observed.IsComplete && observed.Items.Select(i => i.Snapshot!.LifeState).SequenceEqual(new[] { "alive", "downed", "dead" }),
            "Life states differ: " + string.Join(",", observed.Items.Select(i => i.Snapshot?.LifeState ?? i.Code)));
    }

    [Fact]
    public void query_selector_answers_empty_when_the_local_side_is_not_the_host()
    {
        using var s = Start("selq.client");
        Spawn(76561198000000001);
        s.Read();
        SNet.IsMaster = false;
        // The plan runs only on the host: a non-authoritative side records no life, so the query
        // answers an empty set instead of every player or a truncated one (PlayerIdentityModule.cs:84-85 gate,
        // :194-202 enumeration).
        Queue(s, "selq.client-1");
        var tick = s.Kernel.Advance(1, true);
        Require(tick.CommandsExecuted == 1 && tick.Commands[0].Result.Status == "succeeded" && s.Recorded.SequenceEqual(new[] { "" }),
            "A non-host side selected players: " + string.Join("|", s.Recorded));
    }

    [Fact]
    public void query_step_over_the_tick_budget_is_refused_explicitly_and_never_truncates()
    {
        // One world read per declared read: the 65th read of the tick is past `MaximumEntityQueriesPerTick`.
        using var s = Start("selq.tick-budget", reads: RuntimeKernel.MaximumEntityQueriesPerTick + 1);
        Spawn(76561198000000001);
        s.Read();
        Queue(s, "selq.tick-budget-1");
        var tick = s.Kernel.Advance(1, true);
        // The refusal is the step's own: the walk stops at the action that would have consumed it, so the action
        // is never invoked with the shorter set the sweep collected (RuntimeKernel.Control.cs:288-307 with
        // RuntimeKernel.cs:595-607).
        Require(s.Recorded.Count == 0, "The action ran on a set the read budget never admitted: " + string.Join("|", s.Recorded));
        Require(tick.CommandsExecuted == 0 && tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "rejected",
            "The refused query was not reported on the action it stopped: " + Describe(tick));
        Require(tick.Commands[0].Result.Code == RuntimeAbiCodes.QueryBudget,
            "Expected the stable query-budget code in the result: " + Describe(tick));
        // The result carries the stable ABI code; the step's own log record carries it as the reason.
        Require(s.Logged(RuntimeLogCodes.StepFinished, RuntimeAbiCodes.QueryBudget),
            "The refusal was not written to the log: " + s.Log());
    }

    [Fact]
    public void world_read_over_the_reference_budget_is_refused_explicitly_and_never_truncates()
    {
        using var s = Start("selq.query-budget");
        var (_, agent) = Spawn(76561198000000001);
        s.Read();
        var one = Reference(1, 1, 1);
        // `entity-query-budget` guards one read's width, so it is raised by the read API itself: a plan's query
        // session reads one reference per call and can only reach the per-tick budget asserted above.
        var wide = Enumerable.Repeat(one, RuntimeKernel.MaximumEntityReferencesPerQuery + 1).ToArray();
        var oversized = s.Kernel.InspectEntities(wide);
        Require(oversized.Status == "rejected" && oversized.Code == "entity-query-budget",
            "Expected an explicit entity-query-budget rejection: " + oversized.Status + ":" + oversized.Code);
        Require(oversized.Items.Count == 0, "A refused read still answered with " + oversized.Items.Count + " observations.");
        // The per-tick budget is the other half of the same guard, and it is counted per read, not per reference.
        for (var read = 0; read < RuntimeKernel.MaximumEntityQueriesPerTick; read++)
            Require(s.Kernel.InspectEntities(new[] { one }).Status == "complete", "Read " + read + " was refused before the budget ran out.");
        var exhausted = s.Kernel.InspectEntities(new[] { one });
        Require(exhausted.Status == "rejected" && exhausted.Code == "entity-query-tick-budget",
            "Expected an explicit entity-query-tick-budget rejection: " + exhausted.Status + ":" + exhausted.Code);
        Require(exhausted.Items.Count == 0, "A refused read still answered with " + exhausted.Items.Count + " observations.");
        Require(agent.Alive, "The reads changed the observed player.");
    }

    [Fact]
    public void event_published_for_a_previous_world_is_refused_as_stale_world()
    {
        using var s = Start("selq.world");
        Spawn(76561198000000001);
        s.Read();
        var published = Event("selq.world-1", s.Kernel.WorldEpoch);
        var accepted = s.Trigger.Publish(published);
        Require(accepted.Status == "queued", "Publish was refused: " + accepted.Status + ":" + accepted.Code);
        s.Kernel.BeginWorld(2);
        var stale = s.Trigger.Publish(published);
        Require(stale.Status == "rejected" && stale.Code == "stale-world",
            "An event of the old world was accepted: " + stale.Status + ":" + stale.Code);
        var tick = s.Kernel.Advance(1, true);
        Require(tick.CommandsExecuted == 0 && tick.Commands.Count == 0 && s.Recorded.Count == 0,
            "The old world's event reached the action: " + Describe(tick));
        Require(s.Codes().Contains("stale-world"), "The stale-world refusal was not written to the log: " + Describe(tick));
    }

    [Fact]
    public void advance_without_host_authority_never_runs_the_action()
    {
        using var s = Start("selq.not-host");
        Spawn(76561198000000001);
        s.Read();
        Queue(s, "selq.not-host-1");
        var tick = s.Kernel.Advance(1, isHost: false);
        Require(tick.CommandsExecuted == 0 && s.Recorded.Count == 0, "A non-host advance executed the action: " + Describe(tick));
        Require(tick.Events.Count == 1 && tick.Events[0].Status == "rejected" && tick.Events[0].Code == "not-host",
            "The dropped event was not reported by name: " + Describe(tick));
    }

    [Fact]
    public void query_dispatch_preserves_the_recorded_target_order_across_runs()
    {
        var first = Run("selq.order", 1);
        var second = Run("selq.order", 2);
        Require(first == second && first == "gtfo.player:1@1,gtfo.player:2@2,gtfo.player:3@3",
            "Two identical runs differed: " + first + " | " + second);
    }

    [Fact]
    public void query_dispatch_answers_the_candidate_set_in_id_order()
    {
        using var s = Start("selq.ids");
        // Eleven lives, so the ordinal order of `gtfo.player:<n>` is not the order they were recorded in.
        for (int i = 0; i < 11; i++) Spawn(76561198000000001 + (ulong)i);
        s.Read();
        Queue(s, "selq.ids-1");
        var tick = s.Kernel.Advance(1, true);
        Require(tick.CommandsExecuted == 1 && tick.Commands[0].Result.Status == "succeeded", "Expected one succeeded record command: " + Describe(tick));
        Require(s.Recorded.SequenceEqual(new[] { "gtfo.player:1@1,gtfo.player:10@10,gtfo.player:11@11,gtfo.player:2@2,gtfo.player:3@3,"
            + "gtfo.player:4@4,gtfo.player:5@5,gtfo.player:6@6,gtfo.player:7@7,gtfo.player:8@8,gtfo.player:9@9" }),
            "The selector did not hand the action the candidate set in id order: " + string.Join("|", s.Recorded));
    }

    [Fact]
    public void query_dispatch_answers_an_empty_player_set_after_the_world_is_cleared()
    {
        using var s = Start("selq.cleared");
        Spawn(76561198000000001);
        s.Read();
        // A world change clears the module's table; the kind is still exposed by its provider, so the selector
        // answers an empty set and the action still runs on it. Empty is an answer, not a failed read.
        s.Kernel.BeginWorld(2);
        Queue(s, "selq.cleared-2");
        var tick = s.Kernel.Advance(1, true);
        Require(tick.CommandsExecuted == 1 && tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "succeeded",
            "A cleared world was not dispatched as an empty player set: " + Describe(tick));
        Require(s.Recorded.SequenceEqual(new[] { "" }), "The action did not receive the empty set: " + string.Join("|", s.Recorded));
    }

    [Fact]
    public void query_dispatch_reports_a_missing_player_module_as_a_failed_read()
    {
        // The Map registration is in place but the player half never attached to it: the candidate source has no
        // module to list, so the read fails by name instead of answering with an empty world.
        using var s = Start("selq.no-module", attach: false);
        Queue(s, "selq.no-module-1");
        var tick = s.Kernel.Advance(1, true);
        Require(s.Recorded.Count == 0, "The action ran on a player set no module could answer: " + string.Join("|", s.Recorded));
        Require(tick.CommandsExecuted == 0 && tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "rejected",
            "The missing module was not reported on the action it stopped: " + Describe(tick));
        Require(tick.Commands[0].Result.Code == "entity-candidates-failed",
            "Expected the kernel's entity-candidates-failed: " + Describe(tick));
        Require(s.Logged(RuntimeLogCodes.StepFinished, "entity-candidates-failed"),
            "The refusal was not written to the log: " + s.Log());
    }

    [Fact]
    public void query_dispatch_refuses_a_relation_the_selector_cannot_anchor()
    {
        // `recipient_relation` is `self, ally, hostile, neutral, unknown`; only `ally` has no anchor to read.
        using var s = Start("selq.relation", relation: 0);
        Spawn(76561198000000001);
        s.Read();
        Queue(s, "selq.relation-1");
        var tick = s.Kernel.Advance(1, true);
        Require(s.Recorded.Count == 0, "The action ran on a relation the selector refuses: " + string.Join("|", s.Recorded));
        Require(tick.CommandsExecuted == 0 && tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "rejected",
            "The refused relation was not reported on the action it stopped: " + Describe(tick));
        // The selector's own refusal is the reason the framework carries through a step that never read the world.
        Require(tick.Commands[0].Result.Code == "pure-evaluation-failed"
            && tick.Commands[0].Result.Detail.Contains("relation-unsupported", StringComparison.Ordinal),
            "Expected the selector's own relation-unsupported refusal: " + Describe(tick) + " detail=" + tick.Commands[0].Result.Detail);
    }

    [Fact]
    public void query_selector_spends_one_read_of_the_tick_budget()
    {
        // The sweep is the only other reader, and it is given the whole per-tick allowance: its last read fits
        // only if the selector's own candidate read was never charged to the same budget.
        using var s = Start("selq.selector-budget", reads: RuntimeKernel.MaximumEntityQueriesPerTick);
        Spawn(76561198000000001);
        s.Read();
        Queue(s, "selq.selector-budget-1");
        var tick = s.Kernel.Advance(1, true);
        Require(s.Recorded.Count == 0, "The action ran on a set the read budget never admitted: " + string.Join("|", s.Recorded));
        Require(tick.CommandsExecuted == 0 && tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "rejected"
            && tick.Commands[0].Result.Code == RuntimeAbiCodes.QueryBudget,
            "The selector's candidate read was not charged to the query budget: " + Describe(tick));
    }

    private static string Run(string planId, long world)
    {
        using var s = Start(planId, world: world);
        // The same three players in the same order, so only the world epoch differs between the two runs.
        PlayerManager.Reset();
        Spawn(76561198000000001);
        Spawn(76561198000000002);
        Spawn(76561198000000003);
        s.Read();
        Queue(s, planId + "-1");
        s.Kernel.Advance(1, true);
        return s.Recorded.Single();
    }

    /// <summary>Publishes through the provider that owns the trigger binding, and requires the queue to take it.</summary>
    private static void Queue(Session session, string eventId)
    {
        var result = session.Trigger.Publish(Event(eventId, session.Kernel.WorldEpoch));
        Require(result.Status == "queued", "Publish was refused: " + result.Status + ":" + result.Code);
    }

    // ---- The fixture -----------------------------------------------------------------------------------------

    private static (SNet_Player Player, PlayerAgent Agent) Spawn(ulong lookup, bool bot = false)
    {
        int slot = PlayerManager.PlayerAgentsInLevel.Count;
        var player = new SNet_Player { Lookup = lookup, IsBot = bot, SlotIndex = slot };
        var agent = new PlayerAgent { Owner = player, PlayerSlotIndex = slot };
        player.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
        PlayerManager.PlayerAgentsInLevel.Add(agent);
        return (player, agent);
    }

    private static EntityReference Reference(int number, long world, long life) => new("gtfo.player:" + number, world, life);

    private static RuntimeEvent Event(string eventId, long world)
        => new(eventId, TriggerBinding, world, 1, "selq.scope", RuntimeJson.EmptyObject);

    private static string Spell(IEnumerable<EntityReference> references)
        => string.Join(",", references.Select(r => r.Id + "@" + r.LifeEpoch));

    private static string Describe(TickResult tick)
        => "commands=" + string.Join("|", tick.Commands.Select(c => c.NodeId + ":" + c.Result.Status + ":" + c.Result.Code))
            + " events=" + string.Join("|", tick.Events.Select(e => e.Status + ":" + e.Code));

    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    /// <summary>One kernel world with the real Map registration, the test provider and the plan already loaded.
    /// <see cref="Players"/> is set only when the fixture attached the player half: a registration without it is
    /// exactly what a provider whose player module never mounted looks like to a candidate read.</summary>
    private sealed class Session : IDisposable
    {
        internal readonly RuntimeKernel Kernel;
        internal readonly RuntimeModuleHandle Map;
        /// <summary>The test provider's registration: the trigger binding this fixture publishes through belongs to it.</summary>
        internal readonly RuntimeModuleHandle Trigger;
        internal readonly PlayerIdentityModule? Players;
        internal readonly CaptureSink Sink;
        internal readonly List<string> Recorded;

        internal Session(RuntimeKernel kernel, RuntimeModuleHandle map, RuntimeModuleHandle trigger, PlayerIdentityModule? players,
            CaptureSink sink, List<string> recorded)
        { Kernel = kernel; Map = map; Trigger = trigger; Players = players; Sink = sink; Recorded = recorded; }

        /// <summary>Drives the spawn readback the production hook drives, so the module records the lives.</summary>
        internal void Read() => Players!.Reconcile();

        internal IEnumerable<string> Codes() => Sink.Records.Select(r => r.Result?.Reason).Where(c => c != null)!;

        internal bool Logged(string code, string reason) => Sink.Records.Any(r => r.Code == code && r.Result?.Reason == reason);

        internal string Log() => string.Join("|", Sink.Records.Select(r => r.Level + ":" + r.Code + ":" + r.Result?.Reason));

        public void Dispose()
        {
            Players?.Dispose();
            Map.Dispose();
            Trigger.Dispose();
        }
    }

    private static Session Start(string planId, long world = 1, int reads = 0, bool attach = true, int relation = 1)
    {
        var sink = new CaptureSink();
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"),
            new RuntimeLimits(), sink, RuntimeLogLevel.Error);
        var recorded = new List<string>();
        // Real registration order: the Map module (which owns the one registration and carries the selector
        // evaluator), the world, then the frozen runtime with the plan file.
        kernel.BeginWorld(world);
        var map = MapPlayers(kernel);
        var mapHandle = kernel.RegisterModule(map.Module, RuntimeLogLevel.Error);
        var players = attach ? map.Attach(mapHandle) : null;
        RuntimeModuleHandle? worldHandle = null;
        try
        {
            worldHandle = kernel.RegisterModule(WorldModule(kernel, recorded), RuntimeLogLevel.Info);
            kernel.StartRuntime(() =>
            {
                var json = Plan(planId, kernel, reads, relation);
                var outcome = kernel.LoadPlans(new[] { PlanCandidate.Loaded("pack/plans/" + planId + ".plan.json", json) })[0];
                if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Code + ": " + outcome.Detail + " :: " + json);
            });
            return new Session(kernel, mapHandle, worldHandle, players, sink, recorded);
        }
        catch
        {
            worldHandle?.Dispose();
            players?.Dispose();
            mapHandle.Dispose();
            throw;
        }
    }

    /// <summary>The Map provider's player surface only: the one provider, the `forge.selector.target.players`
    /// declaration taken verbatim from the game-independent package, and the production selector evaluator with
    /// the `gtfo.player` resolver, instance lookup, observer and candidate source attached to that one
    /// registration. The package's other module owns the map-object half and no plan here attaches to a map
    /// object, so nothing is declared for it. The player module answers through delegates that read it late,
    /// because the registration it is handed is the one this module is built from.</summary>
    private static (RuntimeModule Module, Func<RuntimeModuleHandle, PlayerIdentityModule> Attach) MapPlayers(RuntimeKernel kernel)
    {
        PlayerIdentityModule? players = null;
        var declarations = RuntimeJson.From(new
        {
            providers = new[] { new { id = ModuleDefinition.ProviderId, kind = "native", version = ModuleDefinition.Version, dependencies = Array.Empty<string>() } },
            capabilities = new object[]
            {
                new { id = PlayerSelectorContract.CapabilityId, owner = ModuleDefinition.ProviderId, kind = "selector", label = "select players",
                    version = "1.0.0", parameters = new { description = "Select players by relation and life state." },
                    graph = new { domains = new[] { Domain }, execution = "query", inputs = Array.Empty<object>(),
                        outputs = new object[] { new { id = "targets", type = "entity", cardinality = "many" } },
                        parameters = new object[]
                        {
                            new { id = "relation", type = "enum", role = "structural", required = true, set = "recipient_relation" },
                            new { id = "empty", type = "enum", role = "structural", required = true, set = "empty_policy" }
                        } } }
            },
            bindings = new object[]
            {
                new { id = PlayerSelectorContract.BindingId, capabilityId = PlayerSelectorContract.CapabilityId, providerId = ModuleDefinition.ProviderId,
                    handler = PlayerSelectorContract.HandlerName, role = "observe", status = "implemented",
                    dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
            }
        }).GetRawText();
        var module = new RuntimeModule(RuntimeKernel.ApiVersion, declarations, new Dictionary<string, CommandHandler>(), new[]
        {
            new BindingSupport(PlayerSelectorContract.BindingId, "implementation-only", Array.Empty<string>())
        })
        {
            Evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
            { [PlayerSelectorContract.HandlerName] = PlayerSelector.Evaluate },
            Shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
            { [PlayerSelectorContract.HandlerName] = PlayerSelectorContract.Shape },
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            { [PlayerIdentityModule.EntityKind] = reference => players!.IsCurrent(reference) },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
            { [PlayerIdentityModule.EntityKind] = instance => players!.ResolveInstance(instance) },
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>(StringComparer.Ordinal)
            { [PlayerIdentityModule.EntityKind] = reference => players!.Observe(reference) },
            // The candidate source `MapPluginSession` registers for the same kind: the module's current lives in
            // `id` ordinal order, and a half that was never attached or has been released refused by name — the
            // kernel turns that into `entity-candidates-failed`. The production source itself is driven through
            // the real session in `MapNativeAdapter`.
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = () => players is { IsRegistered: true } half
                    ? Candidates(half)
                    : throw new RuntimeContractException("player-module-unavailable", "No Map player identity is registered.")
            },
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
            {
                // The one kind no event subject can be read for: the plan hangs on the whole level, so the
                // matcher answers the mount target itself.
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                    category == null && MapLevelReference.TryParse(reference)?.ToString() == PlanLevel)
            }
        };
        return (module, handle => players = new PlayerIdentityModule(handle, kernel, () => true, _ => { }, _ => { }));
    }

    /// <summary>The current lives in `id` ordinal order, the order the production candidate source publishes.</summary>
    private static IReadOnlyList<EntityReference> Candidates(PlayerIdentityModule module)
    {
        var ordered = new List<EntityReference>(module.CurrentPlayers());
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return ordered;
    }

    /// <summary>The plan file, pinned by index the way the compiler pins it: the closure this entry uses, ordinal sorted.
    /// <paramref name="reads"/> adds the sweep selector as a second `query` step between the player selector and the
    /// recording action, so an exhausted read budget is raised by a step of the plan rather than by the test.
    /// <paramref name="relation"/> is the compiled index into `recipient_relation` the selector reads.</summary>
    private static string Plan(string planId, RuntimeKernel kernel, int reads, int relation)
    {
        var manifest = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry");
        JsonElement Row(string list, string id) => manifest.GetProperty(list).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        object Pin(string bindingId)
        {
            var binding = Row("bindings", bindingId);
            var capabilityId = binding.GetProperty("capabilityId").GetString()!;
            var providerId = binding.GetProperty("providerId").GetString()!;
            return new { bindingId, capabilityId, capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
                providerId, providerVersion = Row("providers", providerId).GetProperty("version").GetString()!, handler = binding.GetProperty("handler").GetString()! };
        }
        // The plan's pin table is exactly the closure of the bindings its nodes and entry use, ordinal sorted; a
        // pin no node names is not a wider plan but a rejected one.
        var sweep = reads > 0;
        var order = new[] { PlayerSelectorContract.BindingId, RecordBinding, TriggerBinding, sweep ? SweepBinding : null }
            .Where(id => id != null).Select(id => id!).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var pins = order.Select(Pin).ToArray();
        int Index(string bindingId) => Array.IndexOf(order, bindingId);
        var select = new
        {
            nodeId = "A_players", nodeKind = "query", binding = Index(PlayerSelectorContract.BindingId),
            // The selector's two structural enums are compiled as set indices: `empty` is `emit-empty` (0) and
            // `relation` is the index of the relation this plan asks for into `recipient_relation`.
            layout = Layout(kernel, PlayerSelectorContract.CapabilityId, new object[] { relation, 0 }),
            inputs = Array.Empty<object>(), successors = Array.Empty<int?>()
        };
        // The sweep reads the selector's own set, once per declared read, so `targets` reaching the action is exactly
        // what the budget admitted — never a shorter frame the kernel assembled on the way.
        var readings = new
        {
            nodeId = "B_sweep", nodeKind = "query", binding = Index(SweepBinding),
            layout = Layout(kernel, SweepCapability, new object[] { reads }),
            inputs = new object[] { new { slot = 0, fromStepSlot = new { step = 0, port = 0 } } }, successors = Array.Empty<int?>()
        };
        var record = new
        {
            nodeId = "C_record", nodeKind = "action", binding = Index(RecordBinding),
            layout = Layout(kernel, RecordCapability, Array.Empty<object>()),
            inputs = new object[] { new { slot = 1, fromStepSlot = new { step = sweep ? 1 : 0, port = 0 } } }, successors = new int?[] { null }
        };
        return RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" },
            runtime = kernel.Identity, domain = Domain, authority = "host", failurePolicy = "stop-entrypoint",
            permissions = Array.Empty<string>(), dependencies = Array.Empty<string>(),
            limits = new { maxEventsPerTick = 8, maxCommandsPerTick = 8, maxQueuedEvents = 8, maxCausalDepth = 4 },
            bindings = pins,
            attachments = new object[] { new { kind = "level", reference = PlanLevel } },
            entrypoints = new object[]
            {
                new { nodeId = "Entry", binding = Index(TriggerBinding), layout = Layout(kernel, TriggerCapability, Array.Empty<object>()), start = sweep ? 2 : 1,
                    steps = sweep ? new object[] { select, readings, record } : new object[] { select, record } }
            }
        }).GetRawText();
    }

    private static object Layout(RuntimeKernel kernel, string capabilityId, object[] constants)
    {
        var contract = kernel.ResolveGraphContract(capabilityId, "1.0.0", RuntimeJson.EmptyObject);
        return new { inputs = Sides(contract, "inputs"), outputs = Sides(contract, "outputs"), constants, promoted = Array.Empty<int>() };
    }

    /// <summary>The dense slot layout the loader re-derives from the registered contract. It is assembly-internal,
    /// and the fixture needs the same rule the compiler's `compiledNodeLayout` publishes, so it is read here rather
    /// than restated.</summary>
    private static object Sides(JsonElement contract, string side)
        => typeof(RuntimeKernel).Assembly.GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!
            .GetMethod("Layout", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { contract, side })!;

    /// <summary>One extension provider: a parameterless trigger, a recording action, and a selector that spends the
    /// kernel's own read budget. The action carries the shared result row columns.</summary>
    private static RuntimeModule WorldModule(RuntimeKernel kernel, List<string> recorded) => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = Provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = new object[]
        {
            new { id = TriggerCapability, owner = Provider, kind = "trigger", label = "test trigger", version = "1.0.0", parameters = new { },
                graph = new { domains = new[] { Domain }, execution = "host", inputs = Array.Empty<object>(),
                    outputs = new object[] { new { id = "next", type = "execution" } }, parameters = Array.Empty<object>() } },
            new { id = RecordCapability, owner = Provider, kind = "action", label = "test record", version = "1.0.0", parameters = new { },
                graph = ActionGraph("test.selq.record", Array.Empty<object>()) },
            new { id = SweepCapability, owner = Provider, kind = "selector", label = "test read sweep", version = "1.0.0", parameters = new { },
                graph = new { domains = new[] { Domain }, execution = "query",
                    inputs = new object[] { new { id = "targets", type = "entity", cardinality = "many" } },
                    outputs = new object[] { new { id = "targets", type = "entity", cardinality = "many" } },
                    parameters = new object[] { new { id = "reads", type = "integer", role = "structural", required = true, minimum = 1 } } } }
        },
        bindings = new object[]
        {
            new { id = RecordBinding, capabilityId = RecordCapability, providerId = Provider, handler = RecordCapability, role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
            new { id = TriggerBinding, capabilityId = TriggerCapability, providerId = Provider, handler = TriggerCapability, role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
            new { id = SweepBinding, capabilityId = SweepCapability, providerId = Provider, handler = SweepCapability, role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
        }
    }).GetRawText(), new Dictionary<string, CommandHandler>
    {
        // The test double: it records the target set the query step handed it and reports how many it saw.
        [RecordCapability] = context =>
        {
            var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
            recorded.Add(Spell(targets));
            return CommandResult.Succeeded(RuntimeJson.From(new { target_count = targets.Length }));
        }
    }, new[]
    {
        new BindingSupport(RecordBinding, "implementation-only", Array.Empty<string>()),
        new BindingSupport(TriggerBinding, "implementation-only", Array.Empty<string>()),
        new BindingSupport(SweepBinding, "implementation-only", Array.Empty<string>())
    })
    {
        Evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            // One world read per declared read, through the budgeted session the kernel hands a `query` step. The
            // sweep stops at the first refusal and still answers with the set it was given, so a plan that kept
            // walking would visibly hand the action a frame the budget never admitted.
            [SweepCapability] = context =>
            {
                var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
                for (long read = 0; read < context.Parameters.GetProperty("reads").GetInt64(); read++)
                    if (!context.Query.TrySnapshot(targets[read % targets.Length], out _, out _)) break;
                return RuntimeJson.From(new { targets });
            }
        },
        Shapes = new Dictionary<string, HandlerShape>
        { [RecordCapability] = new HandlerShape(), [SweepCapability] = new HandlerShape().Inputs("targets").Outputs("targets").Parameters("reads") }
    };

    private static object ActionGraph(string schema, object[] parameters) => new
    {
        domains = new[] { Domain }, execution = "host",
        inputs = new object[] { new { id = "in", type = "execution" }, new { id = "targets", type = "entity", cardinality = "many" } },
        outputs = new object[] { new { id = "next", type = "execution" },
            new { id = "outcome", type = "result", schema, fields = ResultFields } },
        parameters,
        recipients = new { input = "targets", target = "entity", cardinality = "many", requires = Array.Empty<string>(), result = "outcome" }
    };

    private static readonly object[] ResultFields =
    {
        new { id = "target", type = "entity" },
        new { id = "status", type = "enum", schema = "execution_outcome" },
        new { id = "committed", type = "enum", schema = "commit_state" },
        new { id = "code", type = "string" },
        new { id = "target_count", type = "integer" }
    };

    private sealed class CaptureSink : IRuntimeLogSink
    {
        internal readonly List<RuntimeLogRecord> Records = new();
        public void Write(in RuntimeLogRecord record, RuntimeLogLevels levels) => Records.Add(record);
    }
}
