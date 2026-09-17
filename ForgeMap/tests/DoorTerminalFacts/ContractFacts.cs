using System;
using System.Linq;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMapTests.DoorTerminalFacts;

/// <summary>What the three contract files declare: the three event rows, the two action rows and the one value
/// row, their bindings, their registration support and their handler port shapes. A case here is the one that
/// fails when a row is renamed on the production side, which is what keeps a binding id, a capability id and a
/// handler name from drifting apart between the declaration and the native half.</summary>
public sealed class ContractFacts
{
    [Fact]
    public void TheThreeEventRowsAreDeclaredWithTheirOwnFactsAndBindings()
    {
        var rows = DoorTerminalEventContract.Rows();
        Assert.Equal(3, rows.Length);
        Assert.Equal(3, DoorTerminalEventContract.Bindings().Length);
        Assert.Equal(3, DoorTerminalEventContract.Supports().Length);

        foreach (var fact in new[]
        {
            DoorTerminalEventContract.DoorScanFact, DoorTerminalEventContract.LockBrokenFact,
            DoorTerminalEventContract.DoorBrokenFact
        })
        {
            var capability = DoorTerminalEventContract.Capability(fact);
            var row = rows.Single(row => Id(row) == capability);
            var graph = Property(row, "graph");
            Assert.Equal("trigger", Property(row, "kind").GetString());
            Assert.Equal("host", Property(graph, "execution").GetString());
            Assert.Equal(ModuleDefinition.ProviderId, Property(row, "owner").GetString());

            // The binding id is derived from the provider and the fact, so it can never disagree with the
            // capability the row names.
            var binding = DoorTerminalEventContract.Bindings().Single(row => Property(row, "capabilityId").GetString() == capability);
            Assert.Equal(DoorTerminalEventContract.Binding(fact), Property(binding, "id").GetString());
            Assert.Equal("observe", Property(binding, "role").GetString());
            Assert.Contains(DoorTerminalEventContract.Supports(),
                support => support.BindingId == DoorTerminalEventContract.Binding(fact)
                    && support.RequiredPermissions.Contains(MapObjectContract.MapObjectReadPermission));
        }
    }

    [Fact]
    public void EveryEventRowPublishesThePortsItsFactCarries()
    {
        var scan = RowPorts(DoorTerminalEventContract.DoorScanCapability);
        Assert.Equal(new[] { "next", "door", "phase", "status" }, scan);
        var broken = RowPorts(DoorTerminalEventContract.LockBrokenCapability);
        Assert.Equal(new[] { "next", "door", "cause", "lock_kind" }, broken);
        var weak = RowPorts(DoorTerminalEventContract.DoorBrokenCapability);
        Assert.Equal(new[] { "next", "door", "phase", "zone", "position", "attacker" }, weak);
    }

    [Fact]
    public void TheWeakDoorRowCarriesTheZoneThePositionAndTheAttacker()
    {
        var row = DoorTerminalEventContract.Rows().Single(row => Id(row) == DoorTerminalEventContract.DoorBrokenCapability);
        var outputs = Property(row, "graph").GetProperty("outputs").EnumerateArray().ToArray();
        var zone = outputs.Single(port => port.GetProperty("id").GetString() == "zone");
        Assert.Equal("resource", zone.GetProperty("type").GetString());
        Assert.Equal("zone", zone.GetProperty("resourceKind").GetString());
        Assert.True(zone.GetProperty("optional").GetBoolean());
        var position = outputs.Single(port => port.GetProperty("id").GetString() == "position");
        Assert.Equal("vector3", position.GetProperty("type").GetString());
        Assert.Equal("m", position.GetProperty("unit").GetString());
        var attacker = outputs.Single(port => port.GetProperty("id").GetString() == "attacker");
        Assert.True(attacker.GetProperty("optional").GetBoolean());
        var phase = outputs.Single(port => port.GetProperty("id").GetString() == "phase");
        Assert.Equal("door_phase", phase.GetProperty("schema").GetString());
        Assert.Equal(new[] { "attacked", "broken" }, DoorTerminalEventContract.DoorPhases);
        Assert.Equal(0, DoorTerminalEventContract.DoorPhaseIndex("attacked"));
        Assert.Equal(1, DoorTerminalEventContract.DoorPhaseIndex("broken"));
        Assert.Equal(-1, DoorTerminalEventContract.DoorPhaseIndex("whatever"));
    }

    [Fact]
    public void TheScanPhaseIsAnInteractionPhase()
    {
        var scan = DoorTerminalEventContract.Rows().Single(row => Id(row) == DoorTerminalEventContract.DoorScanCapability);
        var phase = Property(scan, "graph").GetProperty("outputs").EnumerateArray()
            .Single(port => port.GetProperty("id").GetString() == "phase");
        Assert.Equal("enum", phase.GetProperty("type").GetString());
        Assert.Equal("interaction_phase", phase.GetProperty("schema").GetString());
    }

    [Fact]
    public void TheLockCauseVocabularyIsTheOneTheNativeMembersProduce()
    {
        Assert.Equal(new[] { "unlocked", "hacked", "smashed" }, DoorTerminalEventContract.LockCauses);
        Assert.Equal(0, DoorTerminalEventContract.LockCauseIndex("unlocked"));
        Assert.Equal(1, DoorTerminalEventContract.LockCauseIndex("hacked"));
        Assert.Equal(2, DoorTerminalEventContract.LockCauseIndex("smashed"));
        Assert.Equal(-1, DoorTerminalEventContract.LockCauseIndex("whatever"));
    }

    [Fact]
    public void TheTwoActionRowsCarryTheReshapedPortsAndRefuseNothingTheyCannotDeclare()
    {
        var rows = DoorTerminalActionContract.Rows();
        Assert.Equal(2, rows.Length);
        foreach (var row in rows)
        {
            var graph = Property(row, "graph");
            Assert.Equal("action", Property(row, "kind").GetString());
            Assert.Equal("host", Property(graph, "execution").GetString());
            // The recipient input is the doors themselves and never a lease handle: the row is an action on
            // doors, which is also what the action recipient validator requires.
            var recipients = Property(graph, "recipients");
            Assert.Equal("doors", recipients.GetProperty("input").GetString());
            Assert.Equal("entity", recipients.GetProperty("target").GetString());
            Assert.Equal("many", recipients.GetProperty("cardinality").GetString());
            Assert.Equal("result", recipients.GetProperty("result").GetString());
            Assert.False(recipients.TryGetProperty("handle", out _));
        }
    }

    [Fact]
    public void TheLockRowsPoliciesAreDeclaredAndTheUnlockRowsConsumeKeyIsRefusedByName()
    {
        var lockShape = DoorTerminalActionContract.LockShape;
        Assert.Equal(new[] { "doors" }, lockShape.InputPorts);
        Assert.Equal(new[] { "result" }, lockShape.OutputPorts);
        Assert.Equal(new[] { "key_policy" }, lockShape.ParameterIds);

        var unlockShape = DoorTerminalActionContract.UnlockShape;
        Assert.Equal(new[] { "doors" }, unlockShape.InputPorts);
        Assert.Equal(new[] { "consume_key" }, unlockShape.ParameterIds);

        var policies = Property(DoorTerminalActionContract.LockRow(), "graph").GetProperty("parameters")
            .EnumerateArray().Single(parameter => parameter.GetProperty("id").GetString() == "key_policy")
            .GetProperty("values").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(new[] { "none", "any", "specific" }, policies);
    }

    [Fact]
    public void TheActionBindingsAndShapesMatchTheirHandlerNames()
    {
        var bindings = DoorTerminalActionContract.Bindings();
        Assert.Equal(2, bindings.Length);
        Assert.Equal(DoorTerminalActionContract.Binding(DoorTerminalActionContract.LockCapability),
            Property(bindings[0], "id").GetString());
        Assert.Equal(DoorTerminalActionContract.LockHandlerName, Property(bindings[0], "handler").GetString());
        Assert.Equal(DoorTerminalActionContract.Binding(DoorTerminalActionContract.UnlockCapability),
            Property(bindings[1], "id").GetString());
        Assert.Equal(DoorTerminalActionContract.UnlockHandlerName, Property(bindings[1], "handler").GetString());
        Assert.All(bindings, binding => Assert.Equal("execute", Property(binding, "role").GetString()));
        Assert.Equal(2, DoorTerminalActionContract.Supports().Length);
    }

    [Fact]
    public void TheCommandFactIsTheTerminalCommandRowsOwnFactKind()
    {
        // The publisher names the fact kind itself rather than reading the module's `internal` field across the
        // assembly boundary, so the two spellings are pinned together here: the capability id's own suffix is the
        // fact kind. `RegistryStubs.MapObjectContract` carries the module's two members these sources read, and
        // `tests/MapContracts` asserts the same values against the real file.
        Assert.Equal("terminal_command", DoorTerminalPublisher.CommandFact);
        Assert.Equal("forge.trigger.interaction." + DoorTerminalPublisher.CommandFact,
            MapObjectContract.TerminalCommandCapability);
        Assert.Equal(MapObjectContract.TerminalCommandFact, DoorTerminalPublisher.CommandFact);
    }

    [Fact]
    public void TheDerivationNamesTheStatusesTheNativeEnumDeclares()
    {
        // The status numbers the derivation classifies, checked against the values the case doubles carry, which
        // mirror `LevelGeneration.eDoorStatus` in the interop assembly.
        Assert.Equal(DoorTerminalDoorEvent.Unlocked, DoorTerminalDerivations.EventOf(9));
        Assert.Equal(DoorTerminalDoorEvent.Opened, DoorTerminalDerivations.EventOf(10));
        Assert.Equal(DoorTerminalDoorEvent.Opened, DoorTerminalDerivations.EventOf(16));
        Assert.Equal(DoorTerminalDoorEvent.Broken, DoorTerminalDerivations.EventOf(11));
        Assert.Equal(DoorTerminalDoorEvent.None, DoorTerminalDerivations.EventOf(1));
        Assert.Equal(DoorTerminalDoorEvent.None, DoorTerminalDerivations.EventOf(5));
        // The scan phase comes from the puzzle's own solved reading when the lock could produce one, and falls
        // back to the callback's own stage only when it could not.
        Assert.Equal(MapObjectPhases.Completed, DoorTerminalDerivations.ScanPhase(DoorScanStage.Activated, true));
        Assert.Equal(MapObjectPhases.Started, DoorTerminalDerivations.ScanPhase(DoorScanStage.Solved, false));
        Assert.Equal(MapObjectPhases.Started, DoorTerminalDerivations.ScanPhase(DoorScanStage.Activated, null));
        Assert.Equal(MapObjectPhases.Completed, DoorTerminalDerivations.ScanPhase(DoorScanStage.Solved, null));
    }

    [Fact]
    public void TheDoorQueryStatesAreDerivedFromTheNativeStatus()
    {
        Assert.Equal(new[] { "closed", "open", "locked", "needs_scan", "broken" }, DoorQueryContract.States);
        Assert.Equal(DoorQueryContract.Broken, DoorQueryContract.StateOf(11));
        Assert.Equal(DoorQueryContract.NeedsScan, DoorQueryContract.StateOf(4));
        Assert.Equal(DoorQueryContract.NeedsScan, DoorQueryContract.StateOf(5));
        Assert.Equal(DoorQueryContract.NeedsScan, DoorQueryContract.StateOf(15));
        Assert.Equal(DoorQueryContract.Open, DoorQueryContract.StateOf(10));
        Assert.Equal(DoorQueryContract.Open, DoorQueryContract.StateOf(16));
        Assert.Equal(DoorQueryContract.Locked, DoorQueryContract.StateOf(3));
        Assert.Equal(DoorQueryContract.Locked, DoorQueryContract.StateOf(6));
        Assert.Equal(DoorQueryContract.Locked, DoorQueryContract.StateOf(7));
        // An unlocked but still shut door is closed for the author's five-state question, and its exact status
        // stays readable through `detail`.
        Assert.Equal(DoorQueryContract.Closed, DoorQueryContract.StateOf(9));
        Assert.Equal(DoorQueryContract.Closed, DoorQueryContract.StateOf(1));
        Assert.Throws<RuntimeContractException>(() => DoorQueryContract.StateOf(200));
        Assert.Equal("needs_scan", DoorQueryContract.StateName(DoorQueryContract.NeedsScan));
        Assert.Throws<RuntimeContractException>(() => DoorQueryContract.StateName(9));
    }

    [Fact]
    public void TheDoorQueryRowIsAQueryRowWithItsOwnHandlerBinding()
    {
        var row = RuntimeJson.Parse(DoorQueryContract.CapabilityRowJson);
        Assert.Equal(DoorQueryContract.CapabilityId, row.GetProperty("id").GetString());
        Assert.Equal("state", row.GetProperty("kind").GetString());
        var graph = row.GetProperty("graph");
        Assert.Equal("query", graph.GetProperty("execution").GetString());
        Assert.Equal(new[] { "door" }, graph.GetProperty("inputs").EnumerateArray()
            .Select(port => port.GetProperty("id").GetString()).ToArray());
        Assert.Equal(new[] { "state", "detail", "locked", "key", "puzzle", "glued", "stuck" }, graph.GetProperty("outputs").EnumerateArray()
            .Select(port => port.GetProperty("id").GetString()).ToArray());
        // The puzzle output is the chained-puzzle resource the scan row takes, so its kind is that row's kind.
        var puzzle = graph.GetProperty("outputs").EnumerateArray()
            .Single(port => port.GetProperty("id").GetString() == "puzzle");
        Assert.Equal("resource", puzzle.GetProperty("type").GetString());
        Assert.Equal(AlarmWaveContract.ChainedPuzzleKind, puzzle.GetProperty("resourceKind").GetString());
        Assert.Equal(new[] { "world" }, graph.GetProperty("reads").EnumerateArray()
            .Select(read => read.GetString()).ToArray());
        var binding = RuntimeJson.Parse(DoorQueryContract.BindingRowJson);
        Assert.Equal(DoorQueryContract.BindingId, binding.GetProperty("id").GetString());
        Assert.Equal(DoorQueryContract.HandlerName, binding.GetProperty("handler").GetString());
        Assert.Equal("observe", binding.GetProperty("role").GetString());
        Assert.Equal(new[] { "door" }, DoorQueryContract.Shape.InputPorts);
        Assert.Equal(new[] { "state", "detail", "locked", "key", "puzzle", "glued", "stuck" }, DoorQueryContract.Shape.OutputPorts);
    }

    [Fact]
    public void TheCommandRowCarriesTheSlotAndTheVisibilityRowTheSwitch()
    {
        // The command row and its shape are the terminal contract's own; this project compiles that contract
        // file so the row a plan pins and the row a handler resolves against cannot drift apart. The contract is
        // compiled here without `Native/TerminalObjectActions.cs`, so only its declaration is asserted.
        // The switch that shows or hides a command is the split-out visibility row's own: running a command and
        // showing one are two requests, so the run row carries the slot and no structural parameter at all.
        var shape = TerminalObjectContract.CommandShape;
        Assert.Equal(new[] { "terminals", "actor", "command", "slot", "arguments" }, shape.InputPorts);
        Assert.Empty(shape.ParameterIds);
        var row = TerminalObjectContract.CommandRow();
        var graph = Property(row, "graph");
        Assert.Equal(new[] { "in", "terminals", "actor", "command", "slot", "arguments" },
            graph.GetProperty("inputs").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray());
        Assert.Empty(graph.GetProperty("parameters").EnumerateArray());
        Assert.Equal(new[] { "visible" }, TerminalObjectContract.VisibilityShape.ParameterIds);
        var visible = Property(TerminalObjectContract.VisibilityRow(), "graph").GetProperty("parameters")
            .EnumerateArray().Single(parameter => parameter.GetProperty("id").GetString() == "visible");
        Assert.Equal("structural", visible.GetProperty("role").GetString());
        Assert.True(visible.GetProperty("required").GetBoolean());
        Assert.Equal(TerminalObjectContract.VisibilityModes, visible.GetProperty("values").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
    }

    [Fact]
    public void ACommandResolvesFromItsNameOrItsSlot()
    {
        Assert.Equal(TERM_Command.UniqueCommand3, DoorTerminalDerivations.ResolveCommand("unique3", null));
        Assert.Equal(TERM_Command.UniqueCommand3, DoorTerminalDerivations.ResolveCommand(null, 3));
        Assert.Equal(TERM_Command.UniqueCommand5, DoorTerminalDerivations.ResolveCommand("UNIQUE5", null));
        Assert.Equal(TERM_Command.Ping, DoorTerminalDerivations.ResolveCommand("ping", null));
        Assert.Equal(TERM_Command.Ping, DoorTerminalDerivations.ResolveCommand("Ping", null));
        Assert.Equal(TERM_Command.DisableAlarm, DoorTerminalDerivations.ResolveCommand("DisableAlarm", null));
        Assert.Equal(TERM_Command.UsedCommand, DoorTerminalDerivations.ResolveCommand("used", null));
        Assert.Null(DoorTerminalDerivations.ResolveCommand("not_a_command", null));
        Assert.Null(DoorTerminalDerivations.ResolveCommand("unique9", null));
        Assert.Null(DoorTerminalDerivations.ResolveCommand(null, 9));
        Assert.Null(DoorTerminalDerivations.ResolveCommand(null, 0));
        Assert.Equal(3, DoorTerminalDerivations.CommandSlot("unique3", null));
        Assert.Equal(0, DoorTerminalDerivations.CommandSlot("ping", null));
        Assert.Equal(CommandVisibility.Change, DoorTerminalDerivations.Visibility(hidden: false, wanted: true));
        Assert.Equal(CommandVisibility.Already, DoorTerminalDerivations.Visibility(hidden: true, wanted: true));
    }

    [Fact]
    public void AWeakDoorIsAddressedAsAMapObjectDoorOfItsZoneAndSerial()
    {
        // The weak door's own identity is the door category's: it is opened, closed and locked through the same
        // door actions as an entrance gate, so an author wires one identity and not a namespace of its own.
        var weak = MapObjectDoorAddress.CreateWeak(0, 0, 3, 77);
        Assert.NotNull(weak);
        Assert.Equal("door", weak!.Category);
        Assert.Equal("0", weak.Dimension);
        Assert.Equal("0", weak.Layer);
        Assert.Equal("3", weak.Zone);
        Assert.Equal("weak77", weak.Key);
        Assert.Equal("door/0/0/3/weak77", weak.ToString());
        Assert.Equal(weak, MapObjectDoorAddress.TryParse("door/0/0/3/weak77"));
        Assert.Equal(weak, MapObjectDoorAddress.TryParseWeak("door/0/0/3/weak77"));
        Assert.Null(MapObjectDoorAddress.TryParseWeak("door/0/0/3/security"));
        Assert.Null(MapObjectDoorAddress.CreateWeak(0, 0, 3, -1));
        Assert.Null(MapObjectDoorAddress.CreateWeak(0, 0, 3, null));
        Assert.Null(MapObjectDoorAddress.TryParse("door/0/0/3/weak"));
        Assert.Null(MapObjectDoorAddress.TryParse("door/0/0/3/weak-1"));
    }

    [Fact]
    public void TheTwoDoorKindsOfOneZoneAreDifferentAddresses()
    {
        var entrance = MapObjectDoorAddress.Create(1, 2, 3);
        var weak = MapObjectDoorAddress.CreateWeak(1, 2, 3, 4);
        Assert.NotNull(entrance);
        Assert.NotNull(weak);
        Assert.NotEqual(entrance, weak);
        Assert.Equal(MapObjectDoorAddress.Security, entrance!.Key);
        Assert.Equal("door/1/2/3/security", entrance.ToString());
        Assert.Equal("door/1/2/3/weak4", weak!.ToString());
        Assert.True(MapObjectDoorAddress.IsDoorKey(entrance.Key));
        Assert.True(MapObjectDoorAddress.IsDoorKey(weak.Key));
        Assert.False(MapObjectDoorAddress.IsDoorKey("door"));
    }

    [Fact]
    public void TheDerivationNamesTheCommandsTheNativeEnumDeclares()
    {
        Assert.Equal(1, DoorTerminalDerivations.UniqueSlot(38));
        Assert.Equal(5, DoorTerminalDerivations.UniqueSlot(42));
        Assert.Equal(0, DoorTerminalDerivations.UniqueSlot(37));
        Assert.Equal(0, DoorTerminalDerivations.UniqueSlot(43));
        Assert.Equal(5, DoorTerminalDerivations.UniqueCommandSlots);
        Assert.Null(DoorTerminalDerivations.InputLine(""));
        Assert.Equal("unlocked", DoorTerminalDerivations.LockCause(smashed: false, hacked: false));
        Assert.Equal("hacked", DoorTerminalDerivations.LockCause(smashed: false, hacked: true));
        Assert.Equal("smashed", DoorTerminalDerivations.LockCause(smashed: true, hacked: false));
    }

    [Fact]
    public void TheLedgerPublishesOneStateOnceAndCountsEveryNewOne()
    {
        var ledger = new DoorTerminalFactLedger();

        Assert.True(ledger.Observe("f|a", "one", out long first));
        Assert.Equal(1, first);
        Assert.False(ledger.Observe("f|a", "one", out long repeat));
        // A repeat reports the transition it was published with rather than consuming a new one.
        Assert.Equal(1, repeat);
        Assert.True(ledger.Observe("f|a", "two", out long second));
        Assert.Equal(2, second);
        Assert.True(ledger.Observe("f|b", "one", out long other));
        Assert.Equal(3, other);
        // The key is the fact and the address, so the two states of one address share one entry and the second
        // address is the second.
        Assert.Equal(2, ledger.Count);
        ledger.Clear();

        Assert.Equal(0, ledger.Count);
        Assert.True(ledger.Observe("f|a", "one", out long afterWorld));
        Assert.Equal(1, afterWorld);
    }

    private static string[] RowPorts(string capability)
        => Property(Row(capability), "graph").GetProperty("outputs").EnumerateArray()
            .Select(port => port.GetProperty("id").GetString()!).ToArray();

    private static object Row(string capability)
        => DoorTerminalEventContract.Rows().Single(row => Id(row) == capability);

    private static string Id(object row) => Property(row, "id").GetString()!;

    private static JsonElement Property(object row, string name)
        => JsonSerializer.SerializeToElement(row).GetProperty(name);
}
