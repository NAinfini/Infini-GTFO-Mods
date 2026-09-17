using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

// Real website manifest and production SDK, with the trigger module's own registered surface; only the native
// action/resolver are managed test doubles.
internal sealed class WorkFixture
{
    internal const string StateId = "test.lifecycle.state.contribution";
    private const string DamageBinding = "forge.module.gtfo.enemy.binding.damage_applied";
    private const string HealBinding = "forge.module.gtfo.enemy.binding.heal";
    internal readonly RuntimeKernel Kernel;
    internal readonly RuntimeModuleHandle Owner;
    internal readonly string Plan;
    internal readonly string Trigger = DamageBinding;
    internal int Commits;
    internal Action<CommandContext>? OnCommit;
    internal EntityReference Target => new("gtfo.enemy:7", Kernel.WorldEpoch, 1);
    internal static string FixtureRoot = "";
    internal WorkFixture(bool ready = true)
    {
        var cases = Read("cases.json");
        var manifest = Read(cases.GetProperty("manifest").GetString()!);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Kernel = new(JsonSerializer.Deserialize<RuntimeIdentity>(manifest.GetProperty("runtime"), options)!);
        Kernel.BeginWorld(1);
        Kernel.RegisterModule(LevelMount(), RuntimeLogLevel.Off);
        // The website manifest owns the pins and the permission declarations; the shared contract capabilities and the
        // trigger evaluator come from the SDK, and the native enemy module is registered from the manifest's own rows
        // with managed doubles, so the fixture exercises the declared contracts rather than a copy of them.
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        // The trigger-contract rows are the kernel's own builtin module in production, so this fixture registers the
        // same module: the manifest's enemy bindings pin capabilities such as `forge.trigger.combat.damage_applied`,
        // and a registration that omits them is refused with `binding-capability`.
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(ForgeTrigger.ModuleDefinition.Create(), RuntimeLogLevel.Off);
        var declared = manifest.GetProperty("registry");
        var enemyProvider = declared.GetProperty("providers").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "forge.module.gtfo.enemy");
        // The manifest is generated from that module's own export, so the rows it owns under this provider are the
        // module's declared surface. A module registers its capabilities and its bindings together - a binding whose
        // capability is not registered is refused as `missing-capability` - so every capability row the enemy
        // provider owns is part of the registration, exactly as it is in the production module.
        var enemyCapabilities = declared.GetProperty("capabilities").EnumerateArray()
            .Where(c => c.GetProperty("owner").GetString() == enemyProvider.GetProperty("id").GetString()).ToArray();
        var enemyBindings = declared.GetProperty("bindings").EnumerateArray()
            .Where(b => b.GetProperty("providerId").GetString() == enemyProvider.GetProperty("id").GetString()).ToArray();
        var enemySupport = manifest.GetProperty("bindingSupport").EnumerateArray()
            .Where(s => enemyBindings.Any(b => b.GetProperty("id").GetString() == s.GetProperty("bindingId").GetString()))
            .Select(s => JsonSerializer.Deserialize<BindingSupport>(s, options)!).ToArray();
        var enemyHandlers = enemyBindings.Where(b => b.GetProperty("role").GetString() == "execute")
            .ToDictionary(b => b.GetProperty("handler").GetString()!, _ => (CommandHandler)(ctx =>
            { Commits++; OnCommit?.Invoke(ctx); return CommandResult.Succeeded(RuntimeJson.EmptyObject); }));
        // The doubles stand in for the native module, so their shapes name every value port the real handler reads:
        // the shape is derived from the same declared rows the plan was compiled against, never restated here.
        var enemyShapes = enemyHandlers.Keys.ToDictionary(handler => handler, handler =>
        {
            var binding = enemyBindings.Single(b => b.GetProperty("handler").GetString() == handler);
            var graph = declared.GetProperty("capabilities").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == binding.GetProperty("capabilityId").GetString()).GetProperty("graph");
            string[] ValuePorts(string side) => graph.GetProperty(side).EnumerateArray()
                .Where(p => p.GetProperty("type").GetString() != "execution").Select(p => p.GetProperty("id").GetString()!).ToArray();
            return new HandlerShape().Inputs(ValuePorts("inputs")).Outputs(ValuePorts("outputs"))
                .Parameters(graph.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToArray());
        });
        Owner = Kernel.RegisterModule(new(RuntimeKernel.ApiVersion,
            RuntimeJson.From(new { providers = new[] { enemyProvider }, capabilities = enemyCapabilities, bindings = enemyBindings }).GetRawText(),
            enemyHandlers, enemySupport, new Dictionary<string, Func<EntityReference, bool>> { ["gtfo.enemy"] = r => r == Target })
        { Shapes = enemyShapes }, RuntimeLogLevel.Off);
        Plan = LocalPlan();
        RegisterState();
        if (ready) { Kernel.StartRuntime(() => Kernel.LoadPlan(Plan)); Kernel.Advance(0, true); }
    }
    private static JsonElement Read(string name) => RuntimeJson.Parse(File.ReadAllText(Path.Combine(FixtureRoot, name)));
    /// <summary>One observed damage loss wired into the native heal action. Pins, slot frames and positional
    /// constants all come from the kernel's own live registry and graph contracts, so the fixture cannot drift from
    /// the modules it claims to exercise.</summary>
    private string LocalPlan()
    {
        var manifest = RuntimeJson.Parse(Kernel.ExportManifest()); var registry = manifest.GetProperty("registry");
        JsonElement Row(string list, string id) => registry.GetProperty(list).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        var ids = new[] { DamageBinding, HealBinding }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var pins = ids.Select(id =>
        {
            var binding = Row("bindings", id);
            string capabilityId = binding.GetProperty("capabilityId").GetString()!, providerId = binding.GetProperty("providerId").GetString()!;
            return new { bindingId = id, capabilityId, capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
                providerId, providerVersion = Row("providers", providerId).GetProperty("version").GetString()!, handler = binding.GetProperty("handler").GetString()! };
        }).ToArray();
        var permissions = manifest.GetProperty("bindingSupport").EnumerateArray().Where(s => ids.Contains(s.GetProperty("bindingId").GetString()!))
            .SelectMany(s => s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        // heal's structural parameter is required and has no default, so the fixture supplies its first member.
        JsonElement Parameters(string id) => id == HealBinding ? RuntimeJson.From(new { overheal_policy = 0 }) : RuntimeJson.EmptyObject;
        var contracts = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [DamageBinding] = Kernel.ResolveGraphContract("forge.trigger.combat.damage_applied", "1.0.0", Parameters(DamageBinding)),
            [HealBinding] = Kernel.ResolveGraphContract("forge.action.combat.heal", "1.0.0", Parameters(HealBinding))
        };
        object Layout(string id) => new { inputs = Slots(contracts[id].GetProperty("inputs")), outputs = Slots(contracts[id].GetProperty("outputs")),
            constants = Row("capabilities", pins.Single(p => p.bindingId == id).capabilityId).GetProperty("graph").GetProperty("parameters")
                .EnumerateArray().Select(definition => Parameters(id).TryGetProperty(definition.GetProperty("id").GetString()!, out var value) ? (object?)value : null).ToArray(),
            promoted = Array.Empty<int>() };
        int Slot(string id, string side, string port) => contracts[id].GetProperty(side).EnumerateArray()
            .Select((p, index) => (p, index)).Single(x => x.p.GetProperty("id").GetString() == port).index;
        var wires = new[] {
            (Slot: Slot(HealBinding, "inputs", "targets"), Row: (object)new { slot = Slot(HealBinding, "inputs", "targets"), fromEventSlot = Slot(DamageBinding, "outputs", "target") }),
            (Slot: Slot(HealBinding, "inputs", "source"), Row: (object)new { slot = Slot(HealBinding, "inputs", "source"), fromEventSlot = Slot(DamageBinding, "outputs", "target") }),
            (Slot: Slot(HealBinding, "inputs", "amount"), Row: (object)new { slot = Slot(HealBinding, "inputs", "amount"), value = 10.0 })
        };
        var inputs = wires.OrderBy(x => x.Slot).Select(x => x.Row).ToArray();
        return RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId = "test.lifecycle.plan", resource = new { id = "test.lifecycle.plan", revision = "1" },
            runtime = Kernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions, dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth }, bindings = pins,
            attachments = new[] { new { kind = "level", reference = "test.level" } },
            entrypoints = new[] { new { nodeId = "Fact", binding = Array.IndexOf(ids, DamageBinding), layout = Layout(DamageBinding), start = 0,
                steps = new[] { new { nodeId = "Heal", nodeKind = "action", binding = Array.IndexOf(ids, HealBinding), layout = Layout(HealBinding),
                    inputs, successors = new int?[] { null } } } } }
        }).GetRawText();
    }
    /// <summary>Every plan here mounts the whole level, and no mount kind belongs to the kernel any more: the
    /// provider that owns the kind has to be registered before the plan loads. No domain package is loaded in
    /// this fixture, so this double owns the kind and answers the one reference the fixture plan carries.</summary>
    private static RuntimeModule LevelMount() => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = "test.lifecycle.level", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == "test.level")
        }
    };
    private static readonly string[] WirePortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };
    private static readonly Type GraphContracts = typeof(RuntimeKernel).Assembly.GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!;
    private static readonly MethodInfo LayoutOf = GraphContracts.GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new
    {
        index, type = Array.IndexOf(WirePortTypes, p.GetProperty("type").GetString()),
        cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0,
        valueSet = p.GetProperty("type").GetString() == "enum" ? EnumIndex(p.GetProperty("schema").GetString()!) : -1, lifetime = -1,
        optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
    }).ToArray();
    private static int EnumIndex(string schema)
    {
        var sets = (System.Collections.IEnumerable)GraphContracts.GetField("EnumSets", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        int index = 0;
        foreach (var entry in sets)
        {
            if ((string)entry.GetType().GetProperty("Key")!.GetValue(entry)! == schema) return index;
            index++;
        }
        throw new InvalidDataException("Unknown enum set " + schema);
    }
    private void RegisterState()
    {
        const string id = "test.lifecycle.state";
        Kernel.RegisterModule(new(RuntimeKernel.ApiVersion, RuntimeJson.From(new {
            providers = new[] { new { id, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new[] { new { id = StateId, owner = id, kind = "state", version = "1.0.0",
                label = "Lifecycle cleanup test contribution", parameters = new { valueType = "numeric-contribution" } } },
            bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>()), RuntimeLogLevel.Off);
    }
    internal RuntimeEvent Event(string id, long tick = 10) => new(id, Trigger, Kernel.WorldEpoch, tick,
        "test.lifecycle.scope", RuntimeJson.From(new { source = (EntityReference?)null, target = Target, amount = 10,
            damage_kind = (int?)null, limb = (int?)null }));
    internal RuntimeScheduleHandle Schedule(string id = "test.timer")
    {
        var result = Owner.Schedule(Event(id, Math.Max(0, Kernel.CurrentTick)),
            new(10, FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 3));
        return result.Handle ?? throw new Exception("Schedule setup failed: " + result.Code);
    }
    internal NumericLeaseRequest LeaseRequest(string id = "test.lease") => new(id, StateId, "1.0.0",
        Target, "test.lifecycle.scope", "test.stack", Target, 100, Additive: 5);
    internal RuntimeStateLeaseHandle Lease(string id = "test.lease")
    {
        var result = Owner.AcquireNumericLease(LeaseRequest(id));
        return result.Handle ?? throw new Exception("Lease setup failed: " + result.Code);
    }
}
