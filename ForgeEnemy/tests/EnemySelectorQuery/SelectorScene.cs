using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using SNetwork;

namespace ForgeEnemy.Tests.EnemySelectorQuery;

/// <summary>
/// The one dispatch scene of this suite: one kernel holding the production Enemy module — its own `gtfo.enemy`
/// candidate source, its own selector evaluator and the binding it registers for them — plus a fixture provider
/// that contributes only what the plan needs around the selector (an entry trigger and a recording action).
///
/// Every selector row the kernel serves is the production declaration: the capability, the binding, the handler
/// shape and the evaluator all come from `EnemyModule`'s own registration, so the answer travels the whole
/// production path — candidate source, kernel enumeration and budget, evaluator, port-shape validation, action
/// frame — and no second provider restates the selector's rows.
/// </summary>
internal sealed class SelectorScene : IDisposable
{
    private const string Provider = "gtfo.selector.fixture";
    private const string TriggerCapability = Provider + ".trigger.pulse";
    private const string TriggerBinding = Provider + ".binding.pulse";
    private const string ActionCapability = Provider + ".action.record";
    private const string ActionBinding = Provider + ".binding.record";
    private const string ActionHandler = Provider + ".handler.record";
    private const string LevelReference = "31:A:0";
    private const string PlanId = "test.enemy.selector.query";
    /// <summary>The reviewed roster has no structural parameters; extra constants are invalid.</summary>
    internal static readonly object[] RosterConstants = Array.Empty<object>();
    private static readonly MethodInfo LayoutFrame = typeof(RuntimeKernel).Assembly
        .GetType("ForgeRuntime.Framework.RuntimeGraphContracts", true)!
        .GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly RuntimeModuleHandle fixture;
    private readonly EntityReference firstLife;
    /// <summary>Every input frame the recording action received, in dispatch order.</summary>
    internal readonly List<JsonElement> Recorded = new();
    internal readonly RuntimeKernel Kernel;
    internal readonly EnemyModule Module;
    internal CommandReceipt? LastStep;
    private long tick;
    private ushort nextId = 1000;

    /// <summary>One scene over the production module's own candidates. <paramref name="extra"/> spawns that many
    /// further lives of the same kind, for the cases that need a set larger than the kernel's per-query
    /// ceiling.</summary>
    internal SelectorScene(int extra = 0)
    {
        Kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        Kernel.BeginWorld(1);
        // The module's own trigger rows name canonical capabilities the builtin contract providers own, and the
        // plan's mount needs a level owner: these are the providers the host registers, so this suite registers
        // them instead of a stand-in with shapes of its own.
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(LocalPlan.LevelMount(), RuntimeLogLevel.Off);
        fixture = Kernel.RegisterModule(Fixture(), RuntimeLogLevel.Off);
        // The production module registers the `gtfo.enemy` resolver, the candidate source only that kind's owner
        // may expose, the selector's evaluator and the binding that serves them.
        Module = new EnemyModule(Kernel, RuntimeLogLevel.Off, () => true, _ => { });
        firstLife = Spawn();
        for (var index = 0; index < extra; index++) Spawn();
        Kernel.StartRuntime(() => { });
        Load(RosterConstants);
    }

    /// <summary>The provider this suite stands up around the selector: the trigger the plan's entrypoint
    /// subscribes to, and the action that records what the selector answered. It declares no selector row — the
    /// module under test is the only owner of those.</summary>
    private RuntimeModule Fixture()
    {
        var capabilities = new JsonArray
        {
            JsonNode.Parse(RuntimeJson.From(new
            {
                id = TriggerCapability, owner = Provider, kind = "trigger", label = "Fixture pulse", version = "1.0.0",
                parameters = new { }, graph = new
                {
                    domains = new[] { "enemy" }, execution = "host", inputs = Array.Empty<object>(),
                    outputs = new object[] { new { id = "next", type = "execution" }, new { id = "target", type = "entity" } },
                    parameters = Array.Empty<object>()
                }
            }).GetRawText())!,
            JsonNode.Parse(RuntimeJson.From(new
            {
                id = ActionCapability, owner = Provider, kind = "action", label = "Record", version = "1.0.0",
                parameters = new { }, graph = new
                {
                    domains = new[] { "enemy" }, execution = "host",
                    inputs = new object[] { new { id = "in", type = "execution" }, new { id = "targets", type = "entity", cardinality = "many" } },
                    outputs = new object[] { new { id = "next", type = "execution" }, new { id = "result", type = "result", schema = "test.enemy.selector.result", fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" }
                    } } },
                    parameters = Array.Empty<object>(),
                    recipients = new { input = "targets", target = "entity", cardinality = "many", requires = Array.Empty<string>(), result = "result" }
                }
            }).GetRawText())!
        };
        var registry = RuntimeJson.From(new
        {
            providers = new[] { new { id = Provider, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities,
            bindings = new object[]
            {
                new { id = TriggerBinding, capabilityId = TriggerCapability, providerId = Provider, handler = Provider + ".handler.pulse",
                    role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
                new { id = ActionBinding, capabilityId = ActionCapability, providerId = Provider, handler = ActionHandler,
                    role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
            }
        });
        return new RuntimeModule(RuntimeKernel.ApiVersion, registry.GetRawText(),
            new Dictionary<string, CommandHandler> { [ActionHandler] = Record },
            new[]
            {
                new BindingSupport(TriggerBinding, "implementation-only", Array.Empty<string>()),
                new BindingSupport(ActionBinding, "implementation-only", Array.Empty<string>())
            })
        {
            Shapes = new Dictionary<string, HandlerShape> { [ActionHandler] = new HandlerShape().Inputs("targets").Outputs("result") }
        };
    }

    private CommandResult Record(CommandContext context)
    {
        Recorded.Add(context.Inputs);
        return CommandResult.Succeeded(RuntimeJson.EmptyObject);
    }

    /// <summary>Reload with an exact constant frame, including intentionally invalid test frames.</summary>
    internal void Load(object[] selectorConstants)
    {
        var outcome = TryLoad(selectorConstants);
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code ?? "plan-rejected", outcome.Code ?? "Plan rejected.");
    }

    /// <summary>The loader's own outcome for a constant frame that may not compile, such as one that omits a
    /// required parameter. Every load carries the same plan id, so the frame this scene dispatches is the one
    /// loaded last.</summary>
    internal PlanLoadOutcome TryLoad(object[] selectorConstants)
    {
        Kernel.UnloadPlan(PlanId);
        return Kernel.LoadPlans(new[] { PlanCandidate.Loaded(PlanId + ".json", Build(selectorConstants)) })[0];
    }

    private string Build(object[] selectorConstants)
    {
        var manifest = RuntimeJson.Parse(Kernel.ExportManifest());
        var registry = manifest.GetProperty("registry");
        JsonElement Row(string list, string id) => registry.GetProperty(list).EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == id);
        var ids = new[] { TriggerBinding, ActionBinding, EnemySelector.BindingId }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var pins = ids.Select(id =>
        {
            var binding = Row("bindings", id);
            var capabilityId = binding.GetProperty("capabilityId").GetString()!;
            return (object)new
            {
                bindingId = id, capabilityId,
                capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
                providerId = binding.GetProperty("providerId").GetString()!,
                providerVersion = Row("providers", binding.GetProperty("providerId").GetString()!).GetProperty("version").GetString()!,
                handler = binding.GetProperty("handler").GetString()!
            };
        }).ToArray();
        var permissions = manifest.GetProperty("bindingSupport").EnumerateArray()
            .Where(support => ids.Contains(support.GetProperty("bindingId").GetString()!))
            .SelectMany(support => support.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var action = Kernel.ResolveGraphContract(ActionCapability, "1.0.0", RuntimeJson.EmptyObject);
        var trigger = Kernel.ResolveGraphContract(TriggerCapability, "1.0.0", RuntimeJson.EmptyObject);
        var selector = Kernel.ResolveGraphContract(EnemySelector.CapabilityId, "1.0.0", RuntimeJson.EmptyObject);
        object Layout(JsonElement contract, object[] constants) => new
        {
            inputs = LayoutFrame.Invoke(null, new object[] { contract, "inputs" })!,
            outputs = LayoutFrame.Invoke(null, new object[] { contract, "outputs" })!,
            constants, promoted = Array.Empty<int>()
        };
        int Port(JsonElement contract, string side, string id) => contract.GetProperty(side).EnumerateArray()
            .Select((port, index) => (port, index)).Single(x => x.port.GetProperty("id").GetString() == id).index;
        return RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId = PlanId, resource = new { id = PlanId, revision = "1" },
            runtime = Kernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
            permissions, dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
            bindings = pins, attachments = new object[] { new { kind = "level", reference = LevelReference } },
            // The entrypoint is the fixture's trigger; the query step is the module's own selector binding and the
            // action is the one step reachable from `start`, reading the query's first output slot.
            entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(ids, TriggerBinding),
                layout = Layout(trigger, Array.Empty<object>()), start = 1, steps = new object[]
                {
                    new { nodeId = "Select", nodeKind = "query", binding = Array.IndexOf(ids, EnemySelector.BindingId),
                        layout = Layout(selector, selectorConstants), inputs = Array.Empty<object>(), successors = Array.Empty<int?>() },
                    new { nodeId = "Record", nodeKind = "action", binding = Array.IndexOf(ids, ActionBinding),
                        layout = Layout(action, Array.Empty<object>()),
                        inputs = new object[] { new { slot = Port(action, "inputs", "targets"), fromStepSlot = new { step = 0, port = 0 } } },
                        successors = new int?[] { null } }
                } } }
        }).GetRawText();
    }

    /// <summary>Publishes one event through the fixture's own trigger. A released provider takes its plans with
    /// it, so an event no loaded plan subscribes to is ignored rather than dispatched.</summary>
    internal DispatchResult Publish(string eventId)
        => fixture.Publish(new RuntimeEvent(eventId, TriggerBinding, 1, 1, "selector-scope",
            RuntimeJson.From(new { target = firstLife })));

    /// <summary>Publishes one event and advances the tick that dispatches it, keeping the receipt of the step that
    /// ran: a refused selector is reported on the action that waited for it.</summary>
    internal TickResult Dispatch(string eventId)
    {
        var queued = Publish(eventId);
        if (queued.Status != "queued") throw new InvalidOperationException(eventId + " was not queued: " + queued.Status + "/" + queued.Code);
        var result = Kernel.Advance(++tick, true);
        LastStep = result.Commands.Count > 0 ? result.Commands[^1] : null;
        return result;
    }

    /// <summary>The entity frames the recording action received, in the order the selector answered them.</summary>
    internal string[] Targets(int index = 0) => Recorded[index].GetProperty("targets").EnumerateArray()
        .Select(item => item.GetProperty("id").GetString()!).ToArray();

    /// <summary>The production source's own answer, in its own order: what the selector must have returned.</summary>
    internal string[] CandidateIds() => Module.CurrentCandidates().Select(item => item.Id).ToArray();

    /// <summary>Releases the module, so the next read refuses the way a gone provider's table refuses. The kind it
    /// served loses its resolver, its candidate source and its selector binding in the same unregistration.</summary>
    internal void ReleaseModule() => Module.Dispose();

    internal EntityReference Spawn(ushort id, long pointer)
    {
        var actor = new EnemyAgent { GlobalID = id, Pointer = new IntPtr(pointer) };
        actor.Damage = new Dam_EnemyDamageBase { Owner = actor, Pointer = new IntPtr(pointer + 100) };
        return Module.TrackSpawn(actor);
    }

    /// <summary>One more tracked life with a fresh id and pointer, for the count cases.</summary>
    internal EntityReference Spawn()
    {
        nextId++;
        return Spawn(nextId, 100 + nextId);
    }

    public void Dispose()
    {
        fixture.Dispose();
        Module.Dispose();
        if (Kernel.StartupState != RuntimeStartupState.Stopped) Kernel.StopRuntime();
    }
}

/// <summary>The production module on its own kernel: real native-instance tracking, real entity table, real
/// candidate source. The kernel exists so the module has one to register with; no plan is loaded into it.</summary>
internal sealed class CandidateWorld : IDisposable
{
    internal readonly RuntimeKernel Kernel = new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly EnemyModule Module;
    private ushort nextId = 1000;

    internal CandidateWorld()
    {
        SNet.IsMaster = true;
        Kernel.BeginWorld(1);
        // The module's own trigger and execute rows name canonical capabilities, and a plan's mounts need a level
        // owner; the two builtin contract providers are the ones the host registers, so this suite registers them
        // instead of a stand-in with shapes of its own.
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(LocalPlan.LevelMount(), RuntimeLogLevel.Off);
        Module = new EnemyModule(Kernel, RuntimeLogLevel.Off, () => true, _ => { });
    }

    internal void Start() => Kernel.StartRuntime(() => { });

    internal EntityReference Spawn(ushort id, long pointer)
    {
        var actor = new EnemyAgent { GlobalID = id, Pointer = new IntPtr(pointer) };
        actor.Damage = new Dam_EnemyDamageBase { Owner = actor, Pointer = new IntPtr(pointer + 100) };
        return Module.TrackSpawn(actor);
    }

    /// <summary>One more tracked life with a fresh id and pointer, for the count cases.</summary>
    internal EntityReference Spawn()
    {
        nextId++;
        return Spawn(nextId, 100 + nextId);
    }

    public void Dispose()
    {
        Module.Dispose();
        if (Kernel.StartupState != RuntimeStartupState.Stopped) Kernel.StopRuntime();
    }
}
