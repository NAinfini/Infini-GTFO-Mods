using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// The two readings batch D adds to an entity observation and the event value that crosses the same boundaries:
/// a snapshot carrying health, orientation, state, speed and a parent round-trips with every unknown left unknown
/// (<see cref="RuntimeEntitySnapshot.FromJson"/>), the kernel indexes the reverse parent direction the moment it
/// observes a child, and an event value is one dispatch's event row — written into a slot, carried through the
/// dispatch, read back by the step that consumes it, and refused with `event-port-kind` when the row names no
/// event of that dispatch or names an event the port did not declare.
///
/// This file uses the published surface only, like the rest of this project: the plan it loads is written the way
/// the compiler writes one, with literal frames, through <see cref="PlanCandidate.Loaded"/>.
/// </summary>
internal static class SnapshotFieldTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void Reject(string code, Action action, string name)
        {
            checks++;
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception("FAIL: " + name + " [" + error.Code + " expected " + code + "]");
                return;
            }
            throw new Exception("FAIL: accepted " + name);
        }

        SnapshotWire(Check, Reject);
        EventValues(Check, Reject);
        Children(Check);
        return checks;
    }

    /// <summary>Every added reading is optional in both directions: a snapshot that publishes none is the snapshot
    /// it always was, and one that publishes some keeps them exactly.</summary>
    private static void SnapshotWire(Action<bool, string> Check, Action<string, Action, string> Reject)
    {
        var parent = new EntityReference("rabid.snapshot:9", 1, 1);
        var full = new RuntimeEntitySnapshot(new EntityReference("rabid.snapshot:1", 1, 1), "enemy", "blue", "alive",
            new[] { "tag" }, new[] { "combat.health" }, new[] { 1d, 2d, 3d },
            40d, 100d, new[] { 0d, 0d, 1d }, "alerted", 3.5d, parent);
        var parsed = RuntimeEntitySnapshot.FromJson(RuntimeJson.From(full));
        Check(parsed.Health == 40 && parsed.HealthMaximum == 100 && parsed.Speed == 3.5
            && parsed.AiState == "alerted" && parsed.Parent == parent
            && parsed.Rotation!.SequenceEqual(new[] { 0d, 0d, 1d }),
            "a snapshot's health, health maximum, speed, ai state, rotation and parent round trip");
        // The wire form names the six readings in one order; the round trip above would pass under another
        // spelling too, so the names are asserted rather than assumed.
        var keys = RuntimeJson.From(full).EnumerateObject().Select(p => p.Name).ToArray();
        Check(keys.SequenceEqual(new[] { "ref", "kind", "faction", "lifeState", "tags", "receives", "position",
                "health", "healthMaximum", "rotation", "aiState", "speed", "parent" }),
            "the snapshot's own field order is the contract's [" + string.Join(",", keys) + "]");
        // What an observer that reads none of them publishes stays exactly as unknown as it was.
        var bare = RuntimeEntitySnapshot.FromJson(RuntimeJson.From(new RuntimeEntitySnapshot(
            new EntityReference("rabid.snapshot:2", 1, 1), "enemy", null, "alive",
            Array.Empty<string>(), Array.Empty<string>(), new[] { 0d, 0d, 0d })));
        Check(bare.Health == null && bare.HealthMaximum == null && bare.Rotation == null
            && bare.AiState == null && bare.Speed == null && bare.Parent == null,
            "a snapshot that publishes no reading keeps all six unknown");
        // Each reading may be absent or explicit null: the runtime's own writer always emits the key, a domain
        // observer's hand-written document need not, and neither form may be read as a value.
        var written = RuntimeJson.From(full);
        foreach (var key in new[] { "health", "healthMaximum", "rotation", "aiState", "speed", "parent" })
        {
            var withoutKey = RuntimeJson.Parse("{" + string.Join(",", written.EnumerateObject()
                .Where(p => p.Name != key).Select(p => "\"" + p.Name + "\":" + p.Value.GetRawText())) + "}");
            Check(Without(RuntimeEntitySnapshot.FromJson(withoutKey), key) == null,
                "an absent " + key + " stays unknown, not zero or false");
            var explicitNull = RuntimeJson.Parse("{" + string.Join(",", written.EnumerateObject()
                .Select(p => "\"" + p.Name + "\":" + (p.Name == key ? "null" : p.Value.GetRawText()))) + "}");
            Check(Without(RuntimeEntitySnapshot.FromJson(explicitNull), key) == null,
                "an explicit null " + key + " stays unknown, not zero or false");
        }
        // A reading that is present is checked, so an unknown is never smuggled in as a number that means one.
        Entity(health: 0);
        Reject("entity-health", () => Entity(health: double.NaN), "a non-finite health reading");
        Reject("entity-health-range", () => Entity(health: 101, healthMaximum: 100), "health above its own maximum");
        Reject("entity-health", () => Entity(health: -1), "a negative health reading");
        Reject("entity-speed", () => Entity(speed: -0.5), "a negative speed reading");
        Reject("entity-rotation", () => Entity(rotation: new[] { 0d, 0d }), "a rotation of two components");
        Reject("entity-ai-state", () => Entity(aiState: "sleeping-ish"), "an ai state outside the shared set");
        foreach (var state in new[] { "sleeping", "alerted", "dead", "hibernating" }) Entity(aiState: state);

        static object? Without(RuntimeEntitySnapshot snapshot, string key) => key switch
        {
            "health" => snapshot.Health, "healthMaximum" => snapshot.HealthMaximum,
            "rotation" => snapshot.Rotation, "aiState" => snapshot.AiState,
            "speed" => snapshot.Speed, _ => snapshot.Parent
        };
        static void Entity(double? health = null, double? healthMaximum = null, IReadOnlyList<double>? rotation = null,
            string? aiState = null, double? speed = null)
            => new RuntimeEntitySnapshot(new EntityReference("rabid.snapshot:3", 1, 1), "enemy", null, "alive",
                Array.Empty<string>(), Array.Empty<string>(), new[] { 0d, 0d, 0d }, health, healthMaximum, rotation, aiState, speed);
    }

    /// <summary>The event value at both of its boundaries: the row table a dispatch carries, the envelope that row
    /// reports, and which carries a port accepts.</summary>
    private static void EventValues(Action<bool, string> Check, Action<string, Action, string> Reject)
    {
        using var world = new World();
        Check(RuntimeEventRows.EnvelopeFields.SequenceEqual(new[]
            {
                "eventId", "eventType", "schemaVersion", "simulationTick", "sequence", "sourceRef", "instigatorRef",
                "targetRefs", "scopeRef", "worldEpoch", "lifeEpoch", "correlationId", "causationId", "payload"
            }),
            "the envelope declares its fourteen fields in their fixed order");
        world.Advance();
        // The dispatch's own event is row 0 of its own table, and the envelope is built from what was published.
        var rows = world.Rows;
        Check(rows != null && rows.Count == 1, "a dispatch holds exactly the event it is running for ["
            + (rows == null ? "no dispatch ran" : rows.Count.ToString()) + "]");
        var row = rows!.Row(0)!;
        Check(row.EventId == "rabid.event:1" && row.EventType == World.TriggerBinding && row.SchemaVersion == 1
            && row.SimulationTick == 1 && row.WorldEpoch == 1 && row.ScopeRef == "rabid.scope"
            && row.CausationId == null && row.CorrelationId == null && row.LifeEpoch == null,
            "row 0 is the dispatched event's own envelope, with the fields it has no source for left absent");
        Check(rows.Row(1) == null, "a dispatch holds no row it never published");
        var envelope = RuntimeKernel.EventRowJson(row);
        Check(envelope.EnumerateObject().Select(p => p.Name).SequenceEqual(RuntimeEventRows.EnvelopeFields),
            "the envelope writes the shared fields in their declared order");
        Check(envelope.GetProperty("payload").GetProperty("amount").GetDouble() == 7,
            "the payload is the event's own payload frame, unchanged");
        // The trigger answered its event port with an explicit null, so the step that reads it is handed that null:
        // an absent event is the absence its publisher chose, never row 0 standing in for one.
        Check(world.Values.SequenceEqual(new[] { "null" }),
            "an explicit null on an event port reaches the step as a null [" + string.Join("|", world.Values) + "]");

        // A port accepts the row only when this dispatch holds it and its type is the port's own schema. One port
        // declares the dispatched event's own binding and one declares another event; the row table is the
        // dispatch's, so what it holds is what may be carried.
        var port = RuntimeJson.Parse("{\"id\":\"value\",\"type\":\"event\",\"schema\":\"" + World.TriggerBinding + "\"}");
        var other = RuntimeJson.Parse("{\"id\":\"value\",\"type\":\"event\",\"schema\":\"rabid.other\"}");
        world.OnDispatch = rows =>
        {
            Check(Try(port, 0, rows) == null, "a row of this dispatch whose event is the port's schema is carried");
            Reject(RuntimeAbiCodes.EventPortKind, () => Try(port, 1, rows), "a row index past the dispatch's own table");
            Reject(RuntimeAbiCodes.EventPortKind, () => Try(port, -1, rows), "a negative event row index");
            Reject(RuntimeAbiCodes.EventPortKind, () => RuntimeJson.ValidateValue(RuntimeJson.From("0"), port, rows),
                "an event value that is not a row index");
            Reject(RuntimeAbiCodes.EventPortKind, () => Try(other, 0, rows),
                "a row whose event is not the event the port declares");
            Check(rows.Count == 1, "reading an event value never appends a row [" + rows.Count + "]");
        };
        world.Advance("rabid.event:2");

        static string? Try(JsonElement port, double value, RuntimeEventRows rows)
        {
            try { RuntimeJson.ValidateValue(RuntimeJson.From(value), port, rows); }
            catch (RuntimeContractException error) { return error.Code; }
            return null;
        }
    }

    /// <summary>The parent an observer publishes is what the kernel's children index is built from.</summary>
    private static void Children(Action<bool, string> Check)
    {
        using var world = new World(children: true);
        var child = new EntityReference(World.Kind + ":2", 1, 1);
        var parent = new EntityReference(World.Kind + ":1", 1, 1);
        Check(world.Kernel.InspectEntities(new[] { child }).IsComplete, "the child is observed once so its parent edge is known");
        var children = world.Kernel.InspectChildren(parent);
        Check(children.IsComplete && children.Items.Count == 1 && children.Items[0].Reference == child,
            "the observed parent's child is answered by the index, with no children field on any snapshot");
        var none = world.Kernel.InspectChildren(child);
        Check(!none.IsComplete && none.Code == "entity-children-unavailable",
            "an entity nothing was observed under has no children, which is a refusal and not an empty set");
    }

    /// <summary>
    /// A small world of one provider: a trigger whose event is the fixture's own, an action that reports the event
    /// value it was handed, and one observed entity (plus its child) for the parent index. The plan's frames are
    /// literal, written the way the compiler writes them, and its port-type indexes are the shared table's own
    /// order — so a reordered table fails this fixture instead of passing with a stale index. Nothing here touches
    /// Unity or a native assembly.
    /// </summary>
    private sealed class World : IDisposable
    {
        internal const string Kind = "rabid.snapshot";
        internal const string TriggerBinding = Kind + ".bind.trigger";
        internal const string ActionBinding = Kind + ".bind.action";
        /// <summary>The shared port-type order a compiled layout indexes: the wire contract, restated the way every
        /// fixture restates it, so this file needs no internal surface to write a plan.</summary>
        private const int Execution = 0, Number = 3, Entity = 7, Event = 10, Result = 11;
        private readonly RuntimeModuleHandle handle;
        internal readonly RuntimeKernel Kernel = new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        internal readonly List<string> Values = new();
        internal RuntimeEventRows? Rows { get; private set; }
        internal Action<RuntimeEventRows>? OnDispatch;

        internal World(bool children = false)
        {
            var registry = RuntimeJson.From(new
            {
                providers = new[] { new { id = Kind, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
                capabilities = new object[]
                {
                    new { id = Kind + ".trigger", owner = Kind, kind = "trigger", label = "Fixture event", version = "1.0.0", parameters = new { },
                        graph = new { domains = new[] { "logic" }, execution = "host", inputs = Array.Empty<object>(),
                            outputs = new object[] { new { id = "next", type = "execution" },
                                new { id = "target", type = "entity" },
                                new { id = "value", type = "event", schema = TriggerBinding, nullable = true },
                                new { id = "amount", type = "number" } },
                            parameters = Array.Empty<object>() } },
                    new { id = Kind + ".action", owner = Kind, kind = "action", label = "Read the event", version = "1.0.0",
                        parameters = new { },
                        graph = new { domains = new[] { "logic" }, execution = "host",
                            inputs = new object[] { new { id = "in", type = "execution" },
                                new { id = "value", type = "event", schema = TriggerBinding, nullable = true },
                                new { id = "self", type = "entity" } },
                            outputs = new object[] { new { id = "next", type = "execution" },
                                new { id = "result", type = "result", schema = "rabid.result", fields = ResultFields } },
                            parameters = Array.Empty<object>(),
                            recipients = new { input = "self", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" } } }
                },
                bindings = new object[]
                {
                    new { id = ActionBinding, capabilityId = "rabid.snapshot.action", providerId = Kind, handler = "rabid.snapshot.handler.action",
                        role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
                    new { id = TriggerBinding, capabilityId = Kind + ".trigger", providerId = Kind, handler = Kind + ".handler.trigger",
                        role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
                }
            });
            handle = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, registry.GetRawText(),
                new Dictionary<string, CommandHandler> { [Kind + ".handler.action"] = Act }, new[]
                {
                    new BindingSupport(TriggerBinding, "implementation-only", Array.Empty<string>()),
                    new BindingSupport(ActionBinding, "implementation-only", Array.Empty<string>())
                })
            {
                Shapes = new Dictionary<string, HandlerShape> { [Kind + ".handler.action"] = new HandlerShape().Inputs("value") },
                AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
                {
                    ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == "rabid-level")
                },
                EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
                {
                    [Kind] = reference => reference.Id.StartsWith(Kind + ":", StringComparison.Ordinal) && reference.WorldEpoch == 1
                },
                EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                {
                    [Kind] = reference => new RuntimeEntitySnapshot(reference, "enemy", null, "alive", Array.Empty<string>(),
                        Array.Empty<string>(), new[] { 0d, 0d, 0d }, 10d, 20d, new[] { 0d, 0d, 1d }, "idle", 1.5d,
                        children && reference.Id == Kind + ":2" ? new EntityReference(Kind + ":1", 1, 1) : null)
                }
            }, RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { });
            Kernel.BeginWorld(1);
            var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded("rabid/plan.plan.json", Plan()) })[0];
            if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
        }

        private static readonly object[] ResultFields =
        {
            new { id = "target", type = "entity" },
            new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" },
            new { id = "code", type = "string" }
        };
        /// <summary>The fixture's two contracts as the wire frame they compile to: one entry per port, in
        /// declaration order, with the port type's shared index. The trigger's event output and the action's event
        /// input are the ports this batch is about; the rest are what a loadable plan needs around them.</summary>
        private string Plan() => "{" +
            "\"schemaVersion\":4,\"kind\":\"forge-runtime-plan\",\"planId\":\"rabid.plan\"," +
            "\"resource\":{\"id\":\"author.resource\",\"revision\":\"revision-1\"}," +
            "\"runtime\":" + RuntimeJson.From(Kernel.Identity).GetRawText() + "," +
            "\"domain\":\"logic\",\"authority\":\"host\",\"failurePolicy\":\"stop-entrypoint\"," +
            "\"permissions\":[],\"dependencies\":[]," +
            "\"limits\":{\"maxEventsPerTick\":" + Kernel.Limits.MaxEventsPerTick + ",\"maxCommandsPerTick\":" + Kernel.Limits.MaxCommandsPerTick +
            ",\"maxQueuedEvents\":" + Kernel.Limits.MaxQueuedEvents + ",\"maxCausalDepth\":" + Kernel.Limits.MaxCausalDepth + "}," +
            "\"bindings\":[" +
            "{\"bindingId\":\"" + ActionBinding + "\",\"capabilityId\":\"" + Kind + ".action\",\"capabilityVersion\":\"1.0.0\",\"providerId\":\"" + Kind + "\",\"providerVersion\":\"1.0.0\",\"handler\":\"" + Kind + ".handler.action\"}," +
            "{\"bindingId\":\"" + TriggerBinding + "\",\"capabilityId\":\"" + Kind + ".trigger\",\"capabilityVersion\":\"1.0.0\",\"providerId\":\"" + Kind + "\",\"providerVersion\":\"1.0.0\",\"handler\":\"" + Kind + ".handler.trigger\"}]," +
            "\"attachments\":[{\"kind\":\"level\",\"reference\":\"rabid-level\"}]," +
            "\"entrypoints\":[{\"nodeId\":\"Entry\",\"binding\":1," +
            "\"layout\":{\"inputs\":[],\"outputs\":[" + Port(0, Execution) + "," + Port(1, Entity) + "," + Port(2, Event, true) + "," + Port(3, Number) + "],\"constants\":[],\"promoted\":[]}," +
            "\"start\":0,\"steps\":[{\"nodeId\":\"Action\",\"nodeKind\":\"action\",\"binding\":0," +
            "\"layout\":{\"inputs\":[" + Port(0, Execution) + "," + Port(1, Event, true) + "," + Port(2, Entity) + "]," +
            "\"outputs\":[" + Port(0, Execution) + "," + Port(1, Result) + "],\"constants\":[],\"promoted\":[]}," +
            "\"inputs\":[{\"slot\":1,\"fromEventSlot\":2},{\"slot\":2,\"fromEventSlot\":1}]," +
            "\"successors\":[null]}]}]}";
        /// <summary>One port of a compiled layout: its position in the side it belongs to, its type's index in the
        /// shared table, one cardinality, no value set and no lifetime. A layout is compared to the re-derived one
        /// as stable text, so every field the runtime writes has to be here with the same value.</summary>
        private static string Port(int index, int type, bool nullable = false) => "{\"index\":" + index + ",\"type\":" + type
            + ",\"cardinality\":0,\"valueSet\":-1,\"lifetime\":-1,\"optional\":false,\"nullable\":" + (nullable ? "true" : "false") + "}";

        /// <summary>Publishes one event and dispatches it, keeping the row table the dispatch ran with: the table
        /// is only reachable inside a dispatch, so what a case asserts about it is captured here. Anything but a
        /// queued event and a dispatched command is reported rather than swallowed — a case that silently ran
        /// nothing would otherwise look like a case about an empty table.</summary>
        internal void Advance(string eventId = "rabid.event:1")
        {
            var published = Kernel.Publish(handle, new RuntimeEvent(eventId, TriggerBinding, 1, 1, "rabid.scope",
                RuntimeJson.From(new { target = new EntityReference(Kind + ":1", 1, 1), value = (object?)null, amount = 7 })));
            Rows = null;
            Kernel.Advance(1, true);
            if (published.Status != "queued")
                throw new RuntimeContractException(published.Code, "The fixture event was not queued: " + published.Status);
        }
        private CommandResult Act(CommandContext context)
        {
            // The trigger published an explicit null for its event port, so the step is handed the null it
            // published: the event value itself is the row table's, and row 0 is the dispatch's own event.
            var value = context.Inputs.GetProperty("value");
            Values.Add(value.ValueKind == JsonValueKind.Number ? ((long)value.GetDouble()).ToString() : "null");
            Rows = Kernel.EventRows;
            if (Rows != null) OnDispatch?.Invoke(Rows);
            return CommandResult.Succeeded(RuntimeJson.EmptyObject);
        }
        public void Dispose() { Kernel.StopRuntime(); handle.Dispose(); }
    }
}
