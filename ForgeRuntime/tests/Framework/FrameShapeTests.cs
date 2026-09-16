using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// The physical frame shapes this batch adds: the appended value kinds, the widths a collection, a resource, an
/// event and a result row reserve, the constant pool's one compile-time reference, the budget split between
/// per-step value slots and whole-frame bytes, and the frozen enum wire order. Every assertion reads the
/// production path — <c>FramePort</c>, <c>Layout</c>, <c>PlanFrames.Build</c>, <c>FrameWriter</c>/<c>Frame</c> —
/// rather than a second copy of the tables, so a drift between the tables and the frames fails here.
/// </summary>
internal static class FrameShapeTests
{
    /// <summary>The fixture result row: the four shared columns, then a number column and a vector3 column, so a
    /// row's width is the sum of its field widths rather than the four fixed columns alone.</summary>
    private static readonly object[] RowFields = {
        new { id = "target", type = "entity" },
        new { id = "status", type = "enum", schema = "execution_outcome" },
        new { id = "committed", type = "enum", schema = "commit_state" },
        new { id = "code", type = "string" },
        new { id = "amount", type = "number", unit = "hp" },
        new { id = "point", type = "vector3" } };

    private const string ResultPort = "{\"id\":\"result\",\"type\":\"result\",\"schema\":\"example.result.zone\",\"fields\":";
    private static string RowFieldsJson => RuntimeJson.From(RowFields).GetRawText();
    private static string TriggerOutputs => "[{\"id\":\"next\",\"type\":\"execution\"}," +
        "{\"id\":\"place\",\"type\":\"resource\",\"schema\":\"forge.resource.zone\",\"resourceKind\":\"zone\"}," +
        ResultPort + RowFieldsJson + "}]";
    private static string StepPorts => "[{\"id\":\"in\",\"type\":\"execution\"}," +
        "{\"id\":\"target\",\"type\":\"entity\",\"cardinality\":\"many\",\"optional\":true}," +
        "{\"id\":\"place\",\"type\":\"resource\",\"schema\":\"forge.resource.zone\",\"resourceKind\":\"zone\"}," +
        "{\"id\":\"amount\",\"type\":\"number\",\"optional\":true}]";
    private static string StepOutputs => "[{\"id\":\"next\",\"type\":\"execution\"}," + ResultPort + RowFieldsJson + "}]";
    private static string ZoneLiteral => "place={\"id\":\"zone/alpha\",\"revision\":\"revision-1\"}";

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL frame: " + name); }
        void RejectCode(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL frame: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL frame: " + name + " did not reject");
        }
        static JsonElement Json(string text) => RuntimeJson.Parse(text);
        static (ValueKind Kind, int Width) Port(string json) => RuntimeGraphContracts.FramePort(Json(json));

        {
            // 1. Appending a value kind is appending at the tail: every kind a schemaVersion 4 plan could already
            // carry keeps the byte it had, and the three new ones follow it.
            Check((byte)ValueKind.Missing == 0 && (byte)ValueKind.Null == 1 && (byte)ValueKind.Boolean == 2
                && (byte)ValueKind.Integer == 3 && (byte)ValueKind.Number == 4 && (byte)ValueKind.String == 5
                && (byte)ValueKind.Vector3 == 6 && (byte)ValueKind.Entity == 7 && (byte)ValueKind.Enum == 8
                && (byte)ValueKind.Handle == 9, "every value kind a compiled plan could already carry keeps its index");
            Check((byte)ValueKind.Resource == 10 && (byte)ValueKind.Event == 11 && (byte)ValueKind.Result == 12,
                "resource, event and result are appended after every existing value kind");
            // The wire table and the kind table are one list: `ValueKind - 2` is the port type's dense index. A
            // resource, a handle and an event are now both, which is what lets a plan layout name them.
            RuntimeFrames.ValidateTables();
            foreach (var type in new[] { "handle", "resource", "event" })
                Check((int)RuntimeFrames.KindOf(type) - 2 == Array.IndexOf(RuntimeGraphContracts.RuntimeValueTypes, type),
                    "the wire index of " + type + " is its kind minus the two leading states");
            Check(RuntimeGraphContracts.RuntimeValueTypes.Contains("event") && !RuntimeGraphContracts.RuntimeValueTypes.Contains("result"),
                "an event is a value kind; a result row is a region, not a kind any slot carries");
        }
        {
            // 2. Port shapes: what each wire type reserves inside a frame.
            Check(Port("{\"id\":\"p\",\"type\":\"execution\"}") == (ValueKind.Missing, 0), "an execution port owns no slot");
            Check(Port("{\"id\":\"p\",\"type\":\"boolean\"}") == (ValueKind.Boolean, 1), "a boolean is one slot");
            Check(Port("{\"id\":\"p\",\"type\":\"vector3\"}") == (ValueKind.Vector3, 3), "a vector3 is three slots");
            Check(Port("{\"id\":\"p\",\"type\":\"handle\",\"handleKind\":\"timer\",\"lifetime\":\"invocation\",\"schema\":\"forge.handle.timer\"}")
                == (ValueKind.Handle, 1), "a handle is one slot: its identity bytes and its provider index");
            Check(Port("{\"id\":\"p\",\"type\":\"resource\",\"resourceKind\":\"zone\",\"schema\":\"forge.resource.zone\"}")
                == (ValueKind.Resource, 2), "a resource is its kind index plus the id's string slot");
            Check(Port("{\"id\":\"p\",\"type\":\"event\",\"schema\":\"forge.event.damage\"}") == (ValueKind.Event, 1),
                "an event is the row index of the dispatch's own event");
            Check(Port("{\"id\":\"p\",\"type\":\"policy\",\"schema\":\"forge.policy.recipient\"}") == (ValueKind.Missing, 0),
                "a policy port still has no frame form and reserves nothing");
            Check(Port("{\"id\":\"p\",\"type\":\"entity\",\"cardinality\":\"many\"}")
                == (ValueKind.Entity, 1 + RuntimeFrames.MaxSetWidth), "an entity collection is a head slot plus 256 elements");
            Check(Port("{\"id\":\"p\",\"type\":\"string\",\"cardinality\":\"many\"}")
                == (ValueKind.String, 1 + RuntimeFrames.MaxSetWidth), "a string collection reserves the same element count");
            Check(Port("{\"id\":\"p\",\"type\":\"vector3\",\"cardinality\":\"many\"}")
                == (ValueKind.Vector3, 1 + RuntimeFrames.MaxSetWidth * RuntimeFrames.VectorWidth),
                "a vector3 element is three slots, so its collection reserves the element stride");
            // The three that carry a reference or a row have no element form: a plan that declares one is refused
            // here, one step before the loader refuses it on the side of the boundary it knows about.
            RejectCode(() => Port("{\"id\":\"p\",\"type\":\"resource\",\"cardinality\":\"many\",\"resourceKind\":\"zone\",\"schema\":\"forge.resource.zone\"}"),
                "unsupported-port", "a resource collection is refused");
            RejectCode(() => Port("{\"id\":\"p\",\"type\":\"event\",\"cardinality\":\"many\",\"schema\":\"forge.event.damage\"}"),
                "unsupported-port", "an event collection is refused");
            RejectCode(() => Port("{\"id\":\"p\",\"type\":\"result\",\"cardinality\":\"many\",\"schema\":\"example.result.zone\",\"fields\":" + RowFieldsJson + "}"),
                "unsupported-port", "a result collection is refused");
        }
        {
            // 3. A result row: no head slot, and its width is the sum of its declared fields, offsets included.
            var port = Json(ResultPort + RowFieldsJson + "}");
            RuntimeGraphContracts.ValidatePort(port, "example.result.zone");
            var row = RuntimeGraphContracts.ResultRowWidth(port);
            var offsets = RuntimeGraphContracts.ResultFieldOffsets(port);
            Check(row == 1 + 1 + 1 + 1 + 1 + 3 && row == offsets[^1] + RuntimeFrames.ValueWidth("vector3"),
                "a result row is as wide as its fields: the fixed four, a number and a vector3's three");
            Check(offsets.SequenceEqual(new[] { 0, 1, 2, 3, 4, 5 }),
                "a row's field offsets accumulate the widths of the fields declared before them");
            Check(Port(ResultPort + RowFieldsJson + ",\"cardinality\":\"one\"}") == (ValueKind.Result, row),
                "the port's own frame form is the row itself");
        }
        {
            // 4. Collection round trip through the writer and the reader: every element kind the wire admits,
            // including the handle whose provider index shares the slot with its identity.
            var space = new FrameSpace(2048, 4096);
            var writer = new FrameWriter(space.Slots, 0, space.SlotCapacity, space.Strings);
            writer.SetSegment(0, ValueKind.Boolean, 2); writer.SetBoolean(0, 0, true); writer.SetBoolean(0, 1, false);
            writer.SetSegment(3, ValueKind.Integer, 2); writer.SetInteger(3, 0, 11); writer.SetInteger(3, 1, 12);
            writer.SetSegment(6, ValueKind.Number, 2); writer.SetNumber(6, 0, 1.5); writer.SetNumber(6, 1, 2.5);
            writer.SetSegment(9, ValueKind.String, 2); writer.SetString(9, 0, "alpha"); writer.SetString(9, 1, "beta");
            writer.SetSegment(12, ValueKind.Vector3, 2); writer.SetVector3(12, 0, 1, 2, 3); writer.SetVector3(12, 1, 4, 5, 6);
            var first = new FrameEntity(1, 1, 7); var second = new FrameEntity(1, 2, 8);
            writer.SetSegment(19, ValueKind.Entity, 2); writer.SetEntity(19, 0, first); writer.SetEntity(19, 1, second);
            var handle = new FrameHandle(1, 3, 4); var other = new FrameHandle(1, 5, 6);
            writer.SetSegment(22, ValueKind.Handle, 2); writer.SetHandle(22, 0, handle, 3); writer.SetHandle(22, 1, other, 7);
            var frame = new Frame(space.Slots, 0, space.Strings.Memory);
            Check(frame.Kind(0) == ValueKind.Boolean && frame.Count(0) == 2 && frame.Boolean(0, 0) && !frame.Boolean(0, 1),
                "a boolean collection keeps its count and every element");
            Check(frame.Integer(3, 1) == 12 && frame.Number(6, 0) == 1.5 && frame.String(9, 1) == "beta",
                "integer, number and string elements round trip");
            frame.Vector3(12, 1, out var x, out var y, out var z);
            Check(x == 4 && y == 5 && z == 6 && frame.Element(12, 1).Kind == ValueKind.Vector3,
                "a vector3 element is addressed by element index, not by slot");
            Check(frame.Entity(19, 1).ToString() == second.ToString() && frame.Element(19, 1).Kind == ValueKind.Entity,
                "an entity collection round trips through the generic element accessor");
            var read = frame.Handle(22, 1, out var provider);
            Check(read.ToString() == other.ToString() && provider == 7, "a handle element carries its creating provider");
            // One value of each of the three new kinds, and the null/missing distinction a reader must keep.
            writer.SetResource(30, Array.IndexOf(RuntimeGraphContracts.ResourceKinds, "zone"), "zone/alpha");
            writer.SetEvent(32, 9);
            writer.SetNull(33);
            var single = new Frame(space.Slots, 0, space.Strings.Memory);
            var resource = single.Resource(30);
            Check(resource.KindIndex == Array.IndexOf(RuntimeGraphContracts.ResourceKinds, "zone") && resource.Id == "zone/alpha"
                && single.Kind(31) == ValueKind.String, "a resource is its kind plus the id's own string slot");
            Check(single.EventRow(32) == 9, "an event slot carries the row index of the dispatch's event");
            Check(!single.TryResource(33, out _) && single.IsNull(33) && single.Kind(33) == ValueKind.Null,
                "an explicit null is not a resource and not missing");
            Check(single.Kind(500) is ValueKind.Missing && !single.TryResource(500, out _),
                "an unwritten slot reads back as missing, never as a resource");
            try { single.Resource(500); throw new Exception("FAIL frame: a missing slot was read as a resource"); }
            catch (InvalidOperationException) { checks++; }
        }
        {
            // 5. The frame's budget rule, through a real descriptor: a collection, a resource and a result row are
            // charged to the plan's frame bytes; only the scalars a step decodes are charged to its value slots.
            PlanFrames Build(RuntimeLimits limits, string ports, string[] literals)
            {
                var contract = Json("{\"inputs\":" + ports + ",\"outputs\":" + StepOutputs + "}");
                var graph = Json("{\"inputs\":" + ports + ",\"outputs\":" + StepOutputs + ",\"parameters\":[]}");
                var declared = RuntimeJson.Rows(contract, "inputs");
                StepInput Literal(string name, string value)
                    => new(name, null, Json(value), null, false, declared.Single(p => RuntimeJson.Text(p, "id") == name));
                var step = new StepContract("Guard", "action", graph, contract, Array.Empty<JsonElement>(),
                    new HashSet<string>(StringComparer.Ordinal), literals.Select(l => Literal(l.Split('=', 2)[0], l.Split('=', 2)[1])).ToArray(),
                    new int?[] { null }, 0, 0);
                return PlanFrames.Build(new[] { new EntryContract("Entry", Json("{\"outputs\":" + TriggerOutputs + "}"), new[] { step }) }, limits);
            }
            var frames = Build(new RuntimeLimits { MaxValueSlotsPerStep = 1 }, StepPorts, new[] { ZoneLiteral });
            var root = frames.Entries[0]; var step = frames.Steps[0];
            Check(root.EventPorts.Length == 3 && root.EventPorts[0].Slot == -1 && root.EventPorts[1].Kind == ValueKind.Resource
                && root.EventPorts[1].Slot == root.Base && root.EventPorts[1].Width == 2,
                "the event payload frame reserves the trigger's resource reference after its execution port");
            Check(root.EventPorts[2].Kind == ValueKind.Result && root.EventPorts[2].Slot == -1 && root.EventPorts[2].Width == 0,
                "a trigger result row owns no slot in the event payload frame");
            Check(root.ResultPorts.Length == 1 && root.ResultPorts[0].Slot == root.Base + 2
                && root.ResultPorts[0].Width == RuntimeGraphContracts.ResultRowWidth(Json(ResultPort + RowFieldsJson + "}")),
                "the same result row is one region of its own, right after the payload frame");
            Check(step.Inputs[1] is { Kind: ValueKind.Entity, Width: 1 + RuntimeFrames.MaxSetWidth, Many: true, Slot: 10 },
                "an entity collection reserves its head slot plus its elements inside the step frame");
            Check(step.Inputs[2] is { Kind: ValueKind.Resource, Width: 2, Many: false, Slot: 10 + 1 + RuntimeFrames.MaxSetWidth },
                "the resource input follows the whole reserved collection, never inside it");
            Check(step.Outputs[1] is { Kind: ValueKind.Result, Width: 8 }
                && step.OutputBase == step.InputBase + RuntimeFrames.MaxSetWidth + 1 + 2 + 1,
                "the result row is the step's output region and every input before it keeps its own span");
            Check(step.Span == 268 && frames.StepSlots == 268, "one step's footprint is the whole span it reserves");
            Check(step.ValueSlots == 1 && step.Span > step.ValueSlots,
                "only the scalar input is charged to the per-step value budget; the collection, the resource and the row are not");
            Check(frames.ByteCount == (long)frames.Slots * RuntimeFrames.SlotBytes + frames.Constants.ByteCount && frames.Slots == 278,
                "the same ports are charged to the plan's frame bytes instead");
            Check(frames.Constants.SlotCount == RuntimeFrames.ResourceWidth, "a compiled resource reference takes two constant slots");
            var constant = frames.Constant(0).Resource(0);
            Check(constant.KindIndex == Array.IndexOf(RuntimeGraphContracts.ResourceKinds, "zone") && constant.Id == "zone/alpha",
                "the plan's only compiled reference is a resource, and its id survives the pool");
            // A compiled value of a segmented kind goes through the same three-slot form a written one does.
            var vector = Build(new RuntimeLimits(),
                "[{\"id\":\"in\",\"type\":\"execution\"},{\"id\":\"offset\",\"type\":\"vector3\",\"unit\":\"m\"}]",
                new[] { "offset=[1,2,3]" });
            Check(vector.Constants.SlotCount == RuntimeFrames.VectorWidth && vector.Constants.Slots[0] is { Kind: ValueKind.Vector3, Count: 3 },
                "a compiled vector3 is three constant slots");
            vector.Constant(0).Vector3(0, out var dx, out var dy, out var dz);
            Check(dx == 1 && dy == 2 && dz == 3, "a compiled vector3 keeps its three components in one segment");
            // A second scalar is over the budget the first one fits exactly; a byte budget smaller than the
            // reserved frame refuses the same plan, so both halves of the rule are actually enforced.
            RejectCode(() => Build(new RuntimeLimits { MaxValueSlotsPerStep = 1 }, StepPorts.Replace("\"optional\":true}]",
                "\"optional\":true},{\"id\":\"ratio\",\"type\":\"number\",\"optional\":true}]"), new[] { ZoneLiteral }),
                "frame-slot-budget", "a second scalar exceeds the per-step value slot budget");
            RejectCode(() => Build(new RuntimeLimits { MaxValueSlotsPerStep = 1, MaxDispatchFrameBytes = 1024 }, StepPorts,
                new[] { ZoneLiteral }), "frame-bytes-budget", "the reserved collection exceeds the plan's frame byte budget");
            Check(Build(new RuntimeLimits { MaxValueSlotsPerStep = 64, MaxDispatchFrameBytes = 4 * 1024 * 1024 }, StepPorts,
                new[] { ZoneLiteral }).Slots == 278, "the same plan loads unchanged once both budgets admit it");
            // The constant pool is not a second way to compile a reference the plan may not hold: a handle and an
            // event have no literal spelling at all, and a result row is defined by its schema, not by a value.
            const string HandlePort = "{\"id\":\"token\",\"type\":\"handle\",\"schema\":\"forge.handle.timer\",\"handleKind\":\"timer\",\"lifetime\":\"invocation\",\"optional\":true}]";
            RejectCode(() => Build(new RuntimeLimits(), "[{\"id\":\"in\",\"type\":\"execution\"}," + HandlePort,
                new[] { "token={\"worldEpoch\":1,\"lifeEpoch\":1,\"local\":1,\"provider\":1}" }), "handle-literal",
                "a handle literal never reaches the constant pool");
            RejectCode(() => Build(new RuntimeLimits(), "[{\"id\":\"in\",\"type\":\"execution\"}," +
                "{\"id\":\"observed\",\"type\":\"event\",\"schema\":\"forge.event.damage\",\"optional\":true}]",
                new[] { "observed=7" }), "unsupported-port", "an event literal never reaches the constant pool");
            RejectCode(() => Build(new RuntimeLimits(), "[{\"id\":\"in\",\"type\":\"execution\"}," + ResultPort + RowFieldsJson + ",\"optional\":true}]",
                new[] { "result={}" }), "unsupported-port", "a result row never reaches the constant pool");
        }
        {
            // 6. The enum wire order is frozen: a compiled valueSet index is a position in this table, so a set
            // keeps the index it was published with and a new set is appended after every set already there. The
            // website's `graphEnumSets` is the one source of that order and this table declares the same list in
            // the same order; the website's enum test reads this source back to prove it.
            int WireIndex(string name) => RuntimeGraphContracts
                .Layout(Json("{\"inputs\":[{\"id\":\"p\",\"type\":\"enum\",\"schema\":\"" + name + "\"}]}"), "inputs")[0]
                .GetProperty("valueSet").GetInt32();
            var table = new[] { "compare_operator", "boundary_mode", "rounding_mode", "command_phase", "execution_outcome",
                "interaction_phase", "damage_kind", "ai_state", "status_kind", "stack_policy", "query_shape", "empty_policy",
                "equipment_action", "lifetime_scope", "variable_value_type", "recipient_sort", "recipient_anchor",
                "recipient_relation", "recipient_life_state", "value_operation", "coordinate_space", "pulse_start",
                "commit_state", "agent_modifier", "rundown_tier", "door_state", "door_query_state", "door_phase",
                "equipment_kind", "supply_kind", "pickup_kind", "scan_state", "container_state", "door_lock_cause" };
            Check(table.Select(WireIndex).SequenceEqual(Enumerable.Range(0, table.Length)),
                "every set keeps the position the table declares and nothing is inserted before the end");
            Check(RuntimeGraphContracts.EnumSets.Count == table.Length, "no further enum set is declared beside these");
            // Members are appended inside a set too: an index already compiled against a set keeps its member.
            var ai = RuntimeGraphContracts.EnumSets["ai_state"];
            var committed = RuntimeGraphContracts.EnumSets["commit_state"];
            Check(ai.Length == 12 && ai[9] == "dead" && ai[10] == "patrolling" && ai[11] == "hibernating",
                "ai_state keeps its ten members and appends patrolling and hibernating");
            Check(committed.SequenceEqual(new[] { "none", "confirmed", "unknown", "partial" }),
                "commit_state is none, confirmed, unknown and the appended partial");
            // The two domain vocabularies keep every member a catalog row still spells, and append the new ones.
            Check(RuntimeGraphContracts.ResourceKinds.Contains("chained-puzzle") && RuntimeGraphContracts.ResourceKinds.Contains("zone")
                && RuntimeGraphContracts.ResourceKinds[^1] == "generator" && RuntimeGraphContracts.ResourceKinds.Contains("room"),
                "resource kinds append chained-puzzle, zone and generator, keeping room");
            Check(RuntimeGraphContracts.HandleKinds.TakeLast(2).SequenceEqual(new[] { "pool", "request" })
                && RuntimeGraphContracts.HandleKinds.Contains("pool_membership")
                && !RuntimeGraphContracts.HandleKinds.Contains("reservation") && !RuntimeGraphContracts.HandleKinds.Contains("lease")
                && !RuntimeGraphContracts.HandleKinds.Contains("transaction"),
                "handle kinds keep pool_membership, drop the three kinds with no native object behind them, and end pool, request");
            // Both vocabularies are mirrored wire tables too: a plan's layout carries the member's index, so the
            // appended members must leave every index an already-compiled plan could carry exactly where it was.
            int WireValueSet(string port) => RuntimeGraphContracts.Layout(Json("{\"inputs\":[" + port + "]}"), "inputs")[0]
                .GetProperty("valueSet").GetInt32();
            Check(WireValueSet("{\"id\":\"p\",\"type\":\"resource\",\"schema\":\"forge.resource.pool\",\"resourceKind\":\"pool\"}") == 21
                && WireValueSet("{\"id\":\"p\",\"type\":\"resource\",\"schema\":\"forge.resource.zone\",\"resourceKind\":\"zone\"}") == 23,
                "pool is still resource kind 21 and zone is appended after it");
            Check(WireValueSet("{\"id\":\"p\",\"type\":\"handle\",\"schema\":\"forge.handle.pool\",\"handleKind\":\"pool_membership\",\"lifetime\":\"invocation\"}") == 9
                && WireValueSet("{\"id\":\"p\",\"type\":\"handle\",\"schema\":\"forge.handle.request\",\"handleKind\":\"request\",\"lifetime\":\"invocation\"}") == 11,
                "the handle table renumbered with the three removed kinds: pool_membership is 9 and request 11");
        }
        return checks;
    }
}
