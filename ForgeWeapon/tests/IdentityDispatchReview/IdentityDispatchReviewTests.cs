using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon;

namespace ForgeWeapon.Tests.IdentityDispatchReview;

// Compiled public SDK dispatcher and actual EquipmentIdentitySession; NO native game call.
// The action below is deliberately a fixture binding, not a Forge gameplay capability.
public sealed class IdentityDispatchReviewTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }

    private static void Rejected(string code, Action action)
    {
        try { action(); }
        catch (RuntimeContractException error) { Check(error.Code == code, "Expected " + code + "; got " + error.Code); return; }
        throw new Exception("Expected rejection: " + code);
    }

    private static void NoInvocation(Fixture f, string code)
    {
        var tick = f.Kernel.Advance(1, true);
        Check(tick.Commands.Count == 1, "Expected a terminal command receipt");
        Check(tick.Commands[0].Result.Code == code, "Unexpected rejection: " + tick.Commands[0].Result.Code);
        Check(tick.Commands[0].Result.CommitState == CommitStates.None, "Precondition failure claimed commitment");
        Check(f.HandlerEntries == 0 && f.FixtureEffects == 0, "Stale command reached the fixture action");
    }

    [Fact]
    public void live_public_dispatch_executes_once()
    {
        using var f = new Fixture(); Check(f.Queue().Status == "queued", "not admitted");
        var tick = f.Kernel.Advance(1, true);
        Check(tick.Commands.Count == 1 && tick.Commands[0].Result.Status == CommandStatuses.Succeeded, "current command failed");
        Check(f.HandlerEntries == 1 && f.FixtureEffects == 1, "wrong fixture count");
        f.Kernel.Advance(1, true); Check(f.FixtureEffects == 1, "same tick replayed work");
    }

    [Fact]
    public void duplicate_publish_does_not_double_invoke()
    {
        using var f = new Fixture(); Check(f.Queue().Status == "queued", "not admitted");
        Check(f.Publish().Status == "duplicate", "same event not deduplicated");
        f.Kernel.Advance(1, true); Check(f.FixtureEffects == 1, "duplicate event produced another action");
    }

    [Fact]
    public void unknown_equipment_is_rejected_before_queue()
    {
        using var f = new Fixture(); var result = f.Publish(target: f.Item.Entity with { Id = "gtfo.equipment:7.99" });
        Check(result.Status == "rejected" && result.Code == "stale-entity", "unknown recipient accepted");
        Check(f.Kernel.QueuedEvents == 0 && f.FixtureEffects == 0, "unknown entity started work");
    }

    [Fact]
    public void native_disappearance_after_queue_prevents_invocation()
    {
        using var f = new Fixture(); f.Queue(); f.NativeLive = false; NoInvocation(f, "stale-entity");
    }

    [Fact]
    public void owner_disappearance_after_queue_prevents_invocation()
    {
        using var f = new Fixture(); f.Queue(); f.OwnerLive = false; NoInvocation(f, "stale-entity");
    }

    [Fact]
    public void removed_equipment_after_queue_prevents_invocation()
    {
        using var f = new Fixture(); f.Queue(); f.Session.Remove(f.Item.Entity); NoInvocation(f, "stale-entity");
    }

    [Fact]
    public void same_native_key_new_life_does_not_receive_old_command()
    {
        using var f = new Fixture(); f.Queue(); f.Session.Remove(f.Item.Entity);
        f.Session.Record(f.Item with { Entity = f.Item.Entity with { LifeEpoch = 2 } }); NoInvocation(f, "stale-entity");
    }

    [Fact]
    public void world_reset_drops_old_queue()
    {
        using var f = new Fixture(); f.Queue(); f.Kernel.BeginWorld(8); var tick = f.Kernel.Advance(0, true);
        Check(tick.Commands.Count == 0 && f.Kernel.QueuedEvents == 0 && f.FixtureEffects == 0, "old world work survived");
    }

    [Fact]
    public void unregistered_resolver_prevents_queued_invocation()
    {
        using var f = new Fixture(); f.Queue(); f.Session.Dispose(); NoInvocation(f, "entity-resolver");
    }

    [Fact]
    public void queued_ownership_ABA_rejected_by_process_local_ticket()
    {
        using var f = new Fixture(); f.Queue();
        f.Session.Record(f.Item with { Owner = f.Owner with { Id = "fixture.player:b" } }); f.Session.Record(f.Item);
        var tick = f.Kernel.Advance(1, true); var result = tick.Commands.Single().Result;
        Check(f.HandlerEntries == 1, "Physical instance unexpectedly ceased to exist");
        Check(result.Status == CommandStatuses.Rejected && result.Code == "equipment.observation-changed", "Old ownership ticket accepted");
        Check(result.CommitState == CommitStates.None && f.FixtureEffects == 0, "Rejected ticket committed a fixture effect");
    }

    [Fact]
    public void queued_unwield_rejects_old_use_but_preserves_physical_instance()
    {
        using var f = new Fixture(); f.Queue(); f.Session.Record(f.Item with { IsWielded = false });
        var tick = f.Kernel.Advance(1, true);
        Check(f.Session.IsCurrent(f.Item.Entity), "Unwield destroyed physical identity");
        Check(tick.Commands.Single().Result.Code == "equipment.observation-changed" && f.FixtureEffects == 0, "Old wielded use survived");
    }

    [Fact]
    public void owner_life_change_rejects_queued_use()
    {
        using var f = new Fixture(); f.Queue(); f.Session.Record(f.Item with { Owner = f.Owner with { LifeEpoch = 2 } });
        var tick = f.Kernel.Advance(1, true);
        Check(tick.Commands.Single().Result.Code == "equipment.observation-changed" && f.FixtureEffects == 0, "Old owner life used equipment");
    }

    [Fact]
    public void client_tick_rejects_queued_host_work()
    {
        using var f = new Fixture(); f.Queue(); var tick = f.Kernel.Advance(1, false);
        Check(tick.CommandsExecuted == 0 && f.FixtureEffects == 0 && f.Session.Count == 0, "client executed host work");
        Check(tick.Events.Any(e => e.Code == "not-host"), "authority failure lost its reason");
    }

    [Fact]
    public void scope_cancel_rejects_queued_work()
    {
        using var f = new Fixture(); f.Queue(); f.Dispatch.CancelScope("fixture.scope");
        var tick = f.Kernel.Advance(1, true);
        Check(f.FixtureEffects == 0 && tick.Events.Any(e => e.Status == "cancelled"), "cancelled scope executed");
    }

    [Fact]
    public void native_reader_exception_is_precondition_rejection()
    {
        using var f = new Fixture(); f.Queue(); f.ThrowNativeRead = true; NoInvocation(f, "stale-entity");
    }

    [Fact]
    public void source_reference_is_revalidated_independently_of_recipient()
    {
        using var f = new Fixture(); var source = f.Item with { Entity = f.Item.Entity with { Id = "gtfo.equipment:7.98" }, Slot = "GearSpecial" };
        f.Session.Record(source); f.Queue(source.Entity); f.Session.Remove(source.Entity); NoInvocation(f, "stale-entity");
    }

    [Fact]
    public void post_commit_source_cleanup_does_not_undo_fixture_effect()
    {
        using var f = new Fixture(); f.Queue(); f.Kernel.Advance(1, true); f.Session.Remove(f.Item.Entity); f.Kernel.Advance(2, true);
        Check(f.FixtureEffects == 1, "lifetime cleanup reversed a committed effect");
    }

    [Fact]
    public void missing_host_permission_rejects_plan_before_dispatch()
    {
        using var f = new Fixture(loadPlan: false);
        // The plan's own permissions omit the action binding's required permission; permission-lock demands an exact
        // match, so this can never load without it.
        Rejected("permission-lock", () => f.Kernel.LoadPlan(f.Plan(Array.Empty<string>())));
        Check(f.Kernel.LoadedPlans == 0 && f.Kernel.QueuedEvents == 0, "denied plan was installed");
    }

    [Fact]
    public void duplicate_equipment_registration_does_not_damage_live_session()
    {
        var kernel = new RuntimeKernel(new("fixture.dispatch.registration", "1.0.0", RuntimeKernel.ApiVersion, "synthetic"));
        kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        using var session = new EquipmentIdentitySession(kernel, RuntimeLogLevel.Off, _ => true, _ => true); string before = kernel.ExportManifest();
        Rejected("provider-conflict", () => new EquipmentIdentitySession(kernel, RuntimeLogLevel.Off, _ => true, _ => true));
        Check(before == kernel.ExportManifest(), "failed registration altered registry"); kernel.StopRuntime();
    }

    [Fact]
    public void startup_failure_disposal_does_not_throw()
    {
        var kernel = new RuntimeKernel(new("fixture.dispatch.startup", "1.0.0", RuntimeKernel.ApiVersion, "synthetic"));
        kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        var session = new EquipmentIdentitySession(kernel, RuntimeLogLevel.Off, _ => true, _ => true); kernel.BeginWorld(7);
        try { kernel.StartRuntime(() => throw new InvalidOperationException("fixture-startup-failure")); }
        catch (InvalidOperationException) { }
        Check(kernel.StartupState == RuntimeStartupState.Failed, "startup failure did not latch");
        session.Dispose(); session.Dispose(); Check(session.Count == 0, "failed startup retained entities");
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Provider = "fixture.dispatch";
        internal const string Trigger = Provider + ".binding.trigger", Action = Provider + ".binding.action";
        /// <summary>The one level the fixture plan is mounted on. No mount kind belongs to the kernel any more,
        /// so this fixture owns the `level` kind itself and answers one identity for it.</summary>
        internal const string LevelReference = "31:A:0";
        private static RuntimeModule LevelMount() => new(RuntimeKernel.ApiVersion, """
        {"providers":[{"id":"fixture.dispatch.level","kind":"extension","version":"1.0.0","dependencies":[]}],
        "capabilities":[],"bindings":[]}
        """, new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
            {
                // A level names no event subject, so the kind is judged from the mount target alone.
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                    category == null && reference == LevelReference)
            }
        };
        internal RuntimeKernel Kernel { get; } = new(new("fixture.dispatch.runtime", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        internal EquipmentIdentitySession Session { get; }
        internal RuntimeModuleHandle Dispatch { get; }
        internal EntityReference Owner { get; } = new("fixture.player:a", 7, 1);
        internal EquipmentObservation Item { get; }
        internal bool NativeLive = true, OwnerLive = true, ThrowNativeRead;
        internal int HandlerEntries, FixtureEffects;
        private EquipmentUseTicket? ticket;
        internal Fixture(bool loadPlan = true)
        {
            Kernel.BeginWorld(7);
            Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
            Kernel.RegisterModule(LevelMount(), RuntimeLogLevel.Off);
            Session = new(Kernel, RuntimeLogLevel.Off, _ => ThrowNativeRead ? throw new InvalidOperationException("fixture-reader") : NativeLive, _ => OwnerLive);
            Item = new(new(EquipmentIdentityId.Life(7, 1), 7, 1), "fixture.rifle", "r1", Owner, "GearStandard", EquipmentLocation.Inventory, true, true);
            Dispatch = Kernel.RegisterModule(Module(), RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { if (loadPlan) Kernel.LoadPlan(Plan()); });
            Kernel.Advance(0, true); Session.Record(Item);
        }
        internal DispatchResult Queue(EntityReference? source = null)
        { ticket = Session.CaptureOwnedUse(Item.Entity, Owner, true); return Publish(source: source); }
        internal DispatchResult Publish(EntityReference? target = null, EntityReference? source = null)
            => Dispatch.Publish(new("fixture.event.1", Trigger, Kernel.WorldEpoch, 1, "fixture.scope",
                RuntimeJson.From(new { target = target ?? Item.Entity, source = source ?? Item.Entity })));
        private CommandResult Apply(CommandContext context)
        {
            HandlerEntries++;
            try
            {
                if (ticket == null) return CommandResult.Rejected("fixture.missing-ticket");
                var observed = Session.RequireCurrent(ticket);
                if (observed.Entity != context.GetEntityInput("target")) return CommandResult.Rejected("fixture.target-mismatch");
                // The source is a payload port like any other entity, so it is revalidated on its own: a source
                // that stopped being current must not borrow the recipient's liveness.
                if (!Session.IsCurrent(context.GetEntityInput("source"))) return CommandResult.Rejected("stale-entity");
            }
            catch (RuntimeContractException error) { return CommandResult.Rejected(error.Code); }
            FixtureEffects++; // Test counter only: NOT native damage, inventory, money or a resource transaction.
            return CommandResult.Succeeded(RuntimeJson.From(new { fixtureOnly = true }));
        }
        private object Capability(string kind)
        {
            object graph = kind == "trigger" ? new
            {
                domains = new[] { "weapon" }, execution = "host", inputs = Array.Empty<object>(),
                outputs = new object[] { new { id = "next", type = "execution" }, new { id = "target", type = "entity" }, new { id = "source", type = "entity" } }, parameters = Array.Empty<object>()
            } : new
            {
                domains = new[] { "weapon" }, execution = "host",
                inputs = new object[] { new { id = "enter", type = "execution" }, new { id = "target", type = "entity" }, new { id = "source", type = "entity" } },
                outputs = new[] { new { id = "result", type = "result", schema = "fixture.dispatch.result", fields = IdentityRow } }, parameters = Array.Empty<object>(),
                recipients = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" }
            };
            return new { id = Provider + "." + kind, owner = Provider, kind, label = "Synthetic dispatch review", version = "1.0.0", parameters = new { }, graph };
        }
        private RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = Provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new[] { Capability("trigger"), Capability("action") },
            bindings = new[] { "action", "trigger" }.Select(kind => new
            {
                id = Provider + ".binding." + kind, capabilityId = Provider + "." + kind, providerId = Provider,
                handler = Provider + "." + kind, role = kind == "action" ? "execute" : "observe", status = "implemented",
                dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
            }).ToArray()
        }).GetRawText(), new Dictionary<string, CommandHandler> { [Provider + ".action"] = Apply },
            new[] { new BindingSupport(Trigger, "implementation-only", Array.Empty<string>()),
                new BindingSupport(Action, "implementation-only", new[] { "fixture.dispatch.use" }) })
        {
            // The dispatch double reads the wired target and source and fills the action's result row.
            Shapes = new Dictionary<string, HandlerShape> { [Provider + ".action"] = new HandlerShape().Inputs("target", "source").Outputs("result") }
        };
        private static readonly string[] PortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };
        // The fixture action declares the framework's shared result row, so its contract is well-formed.
        private static readonly object[] IdentityRow =
        {
            new { id = "target", type = "entity" }, new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" }, new { id = "code", type = "string" }
        };
        private JsonElement Graph(string kind) => RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry").GetProperty("capabilities")
            .EnumerateArray().Single(c => c.GetProperty("id").GetString() == Provider + "." + kind).GetProperty("graph");
        // Dense slot frame written independently of the SDK; these ports carry no value set or lifetime.
        private static object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new
        {
            index, type = Array.IndexOf(PortTypes, p.GetProperty("type").GetString()!), cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0, valueSet = -1, lifetime = -1,
            optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
        }).ToArray();
        private static object Layout(JsonElement graph) => new { inputs = Slots(graph.GetProperty("inputs")), outputs = Slots(graph.GetProperty("outputs")), constants = Array.Empty<object>(), promoted = Array.Empty<int>() };
        private static int Slot(JsonElement ports, string name) => ports.EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == name).i;
        internal string Plan(string[]? permissions = null)
        {
            var kinds = new[] { "action", "trigger" }; var trigger = Graph("trigger"); var action = Graph("action");
            return RuntimeJson.From(new
            {
                schemaVersion = 1, kind = "forge-runtime-plan", planId = "fixture.plan", resource = new { id = "fixture.resource", revision = "r1" },
                runtime = Kernel.Identity, domain = "weapon", authority = "host", failurePolicy = "stop-entrypoint",
                permissions = permissions ?? new[] { "fixture.dispatch.use" }, dependencies = Array.Empty<string>(),
                // The fixture plan mounts on the one level this fixture owns the mount kind for.
                attachments = new[] { new { kind = "level", reference = LevelReference } },
                limits = new { maxEventsPerTick = 8, maxCommandsPerTick = 8, maxQueuedEvents = 8, maxCausalDepth = 4 },
                bindings = kinds.Select(kind => new
                {
                    bindingId = Provider + ".binding." + kind, capabilityId = Provider + "." + kind,
                    capabilityVersion = "1.0.0", providerId = Provider, providerVersion = "1.0.0", handler = Provider + "." + kind
                }).ToArray(),
                entrypoints = new[] { new { nodeId = "entry", binding = Array.IndexOf(kinds, "trigger"), layout = Layout(trigger), start = 0,
                    steps = new[] { new { nodeId = "apply", nodeKind = "action", binding = Array.IndexOf(kinds, "action"), layout = Layout(action),
                        inputs = new[] { new { slot = Slot(action.GetProperty("inputs"), "target"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "target") },
                            new { slot = Slot(action.GetProperty("inputs"), "source"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "source") } },
                        successors = Array.Empty<int?>() } } } }
            }).GetRawText();
        }
        public void Dispose() { Dispatch.Dispose(); Session.Dispose(); Kernel.StopRuntime(); }
    }
}
