using System.Text.Json;
using ForgeRuntime.Framework;

Cases.Run();
Console.WriteLine($"Variable checks: {Suite.Passed} passed; {Suite.Failed} failed.");
return Suite.Failed == 0 ? 0 : 1;

internal static class Suite
{
    internal static int Passed, Failed;
    internal static void Test(string name, Action action)
    {
        try { action(); }
        catch (Exception error) { Failed++; Console.WriteLine($"FAIL {name}: {error}"); }
    }
    internal static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Passed++;
    }
    internal static string Reject(Action action, string? expectedCode = null)
    {
        try { action(); }
        catch (RuntimeContractException error)
        {
            Check(expectedCode == null || error.Code == expectedCode, $"Expected {expectedCode}, got {error.Code}.");
            return error.Code;
        }
        throw new InvalidOperationException("Expected contract rejection.");
    }
}

/// <summary>
/// Every case drives the kernel the way the runtime does and reads its public state back: the exported snapshot,
/// the level-object table and the event receipts. What a control step produced is observed by writing it into
/// another variable, so the suite asserts the production rows rather than a test-only capability's copy of them.
/// </summary>
internal static class Cases
{
    private const int Number = 0, Text = 2, Entity = 3;
    private const int Received = 0, TimedOut = 1;

    internal static void Run()
    {
        Suite.Test("a variable nobody wrote reads its declared initial value", LevelInitial);
        Suite.Test("a write is what the next read sees", LevelRoundTrip);
        Suite.Test("each scope stores its own subject and slot", ScopePartition);
        Suite.Test("a player who joined late reads the initial value", LateJoiner);
        Suite.Test("once fires per mount point", OncePerMount);
        Suite.Test("a wait resumes on the message it named, with its payload", WaitReceived);
        Suite.Test("a wait leaves by timeout when nothing arrives", WaitTimeout);
        Suite.Test("a level object holds a handle and an entity", NamedObjects);
        Suite.Test("a checkpoint restores values and latches over reloaded plans", CheckpointRestore);
        Suite.Test("a released subject loses only its own variables", ScopeRelease);
        Suite.Test("the host snapshot reproduces a world on another host", HostSnapshot);
        Suite.Test("end stops the region it is in", EndStopsTheRegion);
        Suite.Test("an undeclared variable is refused by name", UndeclaredRefusal);
        Suite.Test("a node whose type disagrees with the declaration is refused", TypeMismatch);
        Suite.Test("a named variable declared in variables[] is refused", NamedScopeRefusal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // One variable, one dispatch
    // ---------------------------------------------------------------------------------------------------------
    private static void LevelInitial()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.initial", ("test.shield", "level", "number"), ("test.seen", "level", "number"))
            .Initial("test.shield", 7).Initial("test.seen", 0);
        var read = ReadStep(builder, "test.shield", "ARead", 1);
        WriteFrom(builder, "test.seen", read, ReadValue(builder), "BWriteSeen", null);
        world.Load(builder.Build());
        world.Ping("p1");
        world.Advance(10);
        Suite.Check(Value(world, "test.seen|level||-1") == 7, "a read nobody wrote answers the declared initial value");
        Suite.Check(!Entries(world.Kernel).ContainsKey("test.shield|level||-1"), "a read does not create an entry");
    }

    private static void LevelRoundTrip()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.roundtrip", ("test.shield", "level", "number"), ("test.seen", "level", "number"), ("test.seen2", "level", "number"))
            .Initial("test.shield", 7).Initial("test.seen", 0).Initial("test.seen2", 0);
        var first = ReadStep(builder, "test.shield", "ARead", 1);
        var writeSeen = WriteFrom(builder, "test.seen", first, ReadValue(builder), "BWriteSeen", 2);
        var writeShield = WriteStep(builder, "test.shield", "CWriteShield", new[] { PlanBuilder.Literal(ValueSlot(builder, "test.shield"), 42) }, 3);
        var second = ReadStep(builder, "test.shield", "DRead", 4);
        WriteFrom(builder, "test.seen2", second, ReadValue(builder), "EWriteSeen", null);
        Suite.Check(new[] { first, writeSeen, writeShield, second }.SequenceEqual(new[] { 0, 1, 2, 3 }), "the chain is the walk order");
        world.Load(builder.Build());
        world.Ping("p1");
        world.Advance(10);
        Suite.Check(Value(world, "test.seen|level||-1") == 7, "the first read saw the initial value");
        Suite.Check(Value(world, "test.seen2|level||-1") == 42, "the second read saw what the write committed");
        Suite.Check(Value(world, "test.shield|level||-1") == 42, "the host snapshot carries the written value");
        // The delta is the advance's own writes: a client is handed what changed under the snapshot it holds.
        Suite.Check(RuntimeJson.Parse(world.Kernel.ExportVariableDelta()).GetProperty("entries").GetArrayLength() == 3,
            "the delta carries the entries this advance wrote");
        world.Ping("p2", tick: 11);
        world.Advance(11);
        var repeat = world.Kernel.ExportVariableDelta();
        // The second dispatch reads 42 where the first read 7, so it changes exactly `test.seen` and nothing else:
        // a write of the value an address already holds is not a change and is not replicated.
        Suite.Check(RuntimeJson.Parse(repeat).GetProperty("entries").GetArrayLength() == 1
            && RuntimeJson.Text(RuntimeJson.Parse(repeat).GetProperty("entries")[0], "name") == "test.seen",
            "the second advance's delta carries exactly the address whose value changed: " + repeat);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Scopes
    // ---------------------------------------------------------------------------------------------------------
    private static void ScopePartition()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.scopes",
            ("test.hp", "player", "number"), ("test.slot", "weapon", "number"), ("test.obj", "object", "number"),
            ("test.foe", "enemy", "number"), ("test.tick", "module", "number"))
            .Initial("test.hp", 100).Initial("test.slot", 0).Initial("test.obj", 5).Initial("test.foe", 1).Initial("test.tick", 0);
        SubjectWrite(builder, "test.hp", 55, "AWritePlayer", null, 1);
        SubjectWrite(builder, "test.slot", 3, "BWriteWeapon", 2, 2);
        SubjectWrite(builder, "test.obj", 6, "CWriteObject", null, 3);
        SubjectWrite(builder, "test.foe", 7, "DWriteEnemy", null, 4);
        SubjectWrite(builder, "test.tick", 1, "EWriteModule", null, null);
        world.Load(builder.Build());
        world.Ping("p1");
        world.Advance(10);
        Suite.Check(Value(world, "test.hp|player|test.entity:1|-1") == 55, "the player scope is keyed by the entity the mount accepted");
        Suite.Check(Value(world, "test.slot|weapon|test.entity:1|2") == 3, "the weapon scope is keyed by the entity and the slot");
        Suite.Check(Value(world, "test.obj|object|test.entity:1|-1") == 6, "the object scope is keyed by the object's entity");
        Suite.Check(Value(world, "test.foe|enemy|test.entity:1|-1") == 7, "the enemy scope is keyed by the enemy's entity");
        Suite.Check(Value(world, "test.tick|module|test.vars.scopes/Entry|-1") == 1, "the module scope is keyed by the mount point");
        Suite.Check(Entries(world.Kernel).Count == 5, "each scope holds exactly its own entry");

        world.Ping("p2", world.SecondTarget, tick: 11);
        world.Advance(11);
        Suite.Check(Value(world, "test.hp|player|test.entity:1|-1") == 55, "another player's dispatch leaves the first player's value alone");
        Suite.Check(Value(world, "test.hp|player|test.entity:2|-1") == 55, "the second player has its own entry");
        Suite.Check(Value(world, "test.slot|weapon|test.entity:1|2") == 3, "the weapon entry is per player and slot");
    }

    private static void LateJoiner()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.late", ("test.hp", "player", "number"), ("test.seen", "level", "number"))
            .Initial("test.hp", 100).Initial("test.seen", 0);
        var parameters = new Dictionary<string, object?> { ["name"] = "test.hp", ["value_type"] = Number };
        var reader = builder.Step("AReadPlayer", "control", VariableContracts.ReadBinding, parameters,
            new[] { PlanBuilder.FromEvent(builder.Port("inputs", VariableContracts.ReadBinding, parameters, "subject"), Event(builder, "target")) }, new int?[] { 1 });
        WriteFrom(builder, "test.seen", reader, ReadValue(builder), "BWriteSeen", null);
        world.Load(builder.Build());
        world.Ping("p1", world.SecondTarget);
        world.Advance(10);
        Suite.Check(Value(world, "test.seen|level||-1") == 100, "a player who joined late reads the declared initial value");
        Suite.Check(Entries(world.Kernel).Count == 1, "the read created no player entry of its own");
    }

    // ---------------------------------------------------------------------------------------------------------
    // g-once, g-wait, g-end, g-message
    // ---------------------------------------------------------------------------------------------------------
    private static void OncePerMount()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.once", ("test.branch", "level", "number")).Initial("test.branch", 9);
        builder.Step("AOnce", "control", VariableContracts.OnceBinding, new Dictionary<string, object?>(), Array.Empty<object>(), new int?[] { 1, 2 });
        WriteStep(builder, "test.branch", "BFirst", new[] { PlanBuilder.Literal(ValueSlot(builder, "test.branch"), 1) }, null);
        WriteStep(builder, "test.branch", "CLater", new[] { PlanBuilder.Literal(ValueSlot(builder, "test.branch"), 0) }, null);
        world.Load(builder.Build());
        world.Ping("p1");
        world.Advance(10);
        Suite.Check(Branch(world) == 1, "the first dispatch takes the first exit");
        world.Ping("p2", tick: 11);
        world.Advance(11);
        Suite.Check(Branch(world) == 0, "the second dispatch takes the later exit");
    }

    private static void WaitReceived()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.wait.recv", ("test.branch", "level", "number"), ("test.payload", "level", "number"))
            .Initial("test.branch", 9).Initial("test.payload", 0);
        var wait = new Dictionary<string, object?> { ["target"] = VariableContracts.MessageReceivedBinding, ["message"] = "test.power_on" };
        builder.Step("AOnce", "control", VariableContracts.OnceBinding, new Dictionary<string, object?>(), Array.Empty<object>(), new int?[] { 1, 2 });
        var waiting = builder.Step("BWait", "control", VariableContracts.WaitBinding, wait, Array.Empty<object>(), new int?[] { 3, 4 });
        builder.Step("CEnd", "control", VariableContracts.EndBinding, new Dictionary<string, object?>(), Array.Empty<object>(), Array.Empty<int?>());
        var received = builder.Port("outputs", VariableContracts.WaitBinding, wait, "received");
        var timeout = builder.Port("outputs", VariableContracts.WaitBinding, wait, "timeout");
        Suite.Check(received == Received && timeout == TimedOut, "the wait's two exits are received then timeout");
        WriteStep(builder, "test.branch", "DReceived", new[] { PlanBuilder.Literal(ValueSlot(builder, "test.branch"), 1) }, 5);
        WriteStep(builder, "test.branch", "ETimeout", new[] { PlanBuilder.Literal(ValueSlot(builder, "test.branch"), 0) }, null);
        var payload = builder.Port("outputs", VariableContracts.WaitBinding, wait, "payload");
        WriteStep(builder, "test.payload", "FWritePayload",
            new[] { PlanBuilder.FromStep(ValueSlot(builder, "test.payload"), waiting, payload) }, null);
        world.Load(builder.Build());
        world.Ping("p1");
        world.Advance(10);
        Suite.Check(!Entries(world.Kernel).ContainsKey("test.branch|level||-1"), "the wait suspends the flow instead of running past it");
        Suite.Check(world.Kernel.QueuedEvents == 0, "a wait without a deadline queues no timer");

        var emit = new Dictionary<string, object?> { ["message"] = "test.power_on" };
        var sender = new PlanBuilder(world, VariableFixture.PingBinding).Named("test.vars.wait.send");
        sender.Step("AEmit", "control", VariableContracts.EmitBinding, emit,
            new[] { PlanBuilder.Literal(sender.Port("inputs", VariableContracts.EmitBinding, emit, "value"), 9) }, new int?[] { null });
        world.Load(sender.Build());
        world.Ping("p2", tick: 11);
        world.Advance(11);
        Suite.Check(Branch(world) == 1, "the message resumes the waiting flow by the received exit");
        Suite.Check(Value(world, "test.payload|level||-1") == 9, "the resumed step reads the payload the sender passed");
    }

    private static void WaitTimeout()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.wait.timeout", ("test.branch", "level", "number")).Initial("test.branch", 9);
        var wait = new Dictionary<string, object?> { ["target"] = VariableContracts.MessageReceivedBinding, ["message"] = "test.never", ["timeout"] = 3 };
        builder.Step("AWait", "control", VariableContracts.WaitBinding, wait, Array.Empty<object>(), new int?[] { 1, 2 });
        WriteStep(builder, "test.branch", "BReceived", new[] { PlanBuilder.Literal(ValueSlot(builder, "test.branch"), 1) }, null);
        WriteStep(builder, "test.branch", "CTimeout", new[] { PlanBuilder.Literal(ValueSlot(builder, "test.branch"), 0) }, null);
        world.Load(builder.Build());
        world.Ping("p1");
        world.Advance(10);
        Suite.Check(!Entries(world.Kernel).ContainsKey("test.branch|level||-1"), "the wait is still suspended before its deadline");
        world.Advance(13);
        Suite.Check(Branch(world) == 0, "the deadline leaves by the timeout exit");
        Suite.Check(Entries(world.Kernel).Count == 1, "the timeout wrote once");
    }

    private static void EndStopsTheRegion()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.end", ("test.stopped", "level", "number")).Initial("test.stopped", 0);
        // Two regions of one sequence: the first ends the flow, so the second never runs. Without the `end` step the
        // walk would advance to the next region and write, which is what the control plan below shows.
        builder.Step("ASequence", "control", "forge.contract.control.binding.sequence", new Dictionary<string, object?>(), Array.Empty<object>(), new int?[] { 1, 2 });
        builder.Step("BEnd", "control", VariableContracts.EndBinding, new Dictionary<string, object?>(), Array.Empty<object>(), Array.Empty<int?>());
        WriteStep(builder, "test.stopped", "CWriteStopped", new[] { PlanBuilder.Literal(ValueSlot(builder, "test.stopped"), 1) }, null);
        world.Load(builder.Build());
        world.Ping("p1");
        world.Advance(10);
        Suite.Check(!Entries(world.Kernel).ContainsKey("test.stopped|level||-1"), "the region after the end step never runs");

        var control = new VariableFixture();
        var second = Variables(control, "test.vars.end.control", ("test.continued", "level", "number")).Initial("test.continued", 0);
        second.Step("ASequence", "control", "forge.contract.control.binding.sequence", new Dictionary<string, object?>(), Array.Empty<object>(), new int?[] { 1, 2 });
        second.Step("BOnce", "control", VariableContracts.OnceBinding, new Dictionary<string, object?>(), Array.Empty<object>(), new int?[] { null, null });
        WriteStep(second, "test.continued", "CWriteContinued", new[] { PlanBuilder.Literal(ValueSlot(second, "test.continued"), 1) }, null);
        control.Load(second.Build());
        control.Ping("p1");
        control.Advance(10);
        Suite.Check(Value(control, "test.continued|level||-1") == 1, "the same shape without the end step does run the next region");
    }

    // ---------------------------------------------------------------------------------------------------------
    // g-object
    // ---------------------------------------------------------------------------------------------------------
    private static void NamedObjects()
    {
        var world = new VariableFixture();
        var wave = world.Owner.CreateEffectHandle("encounter");
        var builder = new PlanBuilder(world, VariableFixture.PingBinding).Named("test.vars.objects")
            .Object("test.alarm_a", "handle")
            .Object("test.door_a", "entity");
        var waveWrite = new Dictionary<string, object?> { ["name"] = "test.alarm_a" };
        var doorWrite = new Dictionary<string, object?> { ["name"] = "test.door_a" };
        builder.Step("AWriteAlarm", "control", VariableContracts.NamedWriteBinding, waveWrite,
            new[] { PlanBuilder.FromEvent(builder.Port("inputs", VariableContracts.NamedWriteBinding, waveWrite, "wave"), Event(builder, "wave")) }, new int?[] { 1 });
        builder.Step("BWriteDoor", "control", VariableContracts.NamedWriteBinding, doorWrite,
            new[] { PlanBuilder.FromEvent(builder.Port("inputs", VariableContracts.NamedWriteBinding, doorWrite, "value"), Event(builder, "target")) }, new int?[] { null });
        world.Load(builder.Build());
        world.Ping("p1", wave: wave);
        world.Advance(10);
        Suite.Check(RuntimeNamedObjects.Names(world.Kernel).SequenceEqual(new[] { "test.alarm_a", "test.door_a" }), "both objects are declared");
        var stored = RuntimeNamedObjects.Read(world.Kernel, "test.alarm_a");
        Suite.Check(stored is { } handle && handle.GetProperty("local").GetInt32() == wave.GetProperty("local").GetInt32(),
            "the wave handle the action produced is stored under the object's name");
        var door = RuntimeNamedObjects.Read(world.Kernel, "test.door_a");
        Suite.Check(door is { } entity && entity.GetProperty("id").GetString() == world.Target.Id, "the entity half stores the dispatched entity");
        Suite.Check(!RuntimeNamedObjects.Bind(world.Kernel, "test.alarm_a", stored!.Value), "binding the value already stored reports no change");
        Suite.Check(RuntimeNamedObjects.Bind(world.Kernel, "test.door_a",
            RuntimeJson.From(new { id = world.SecondTarget.Id, worldEpoch = world.Kernel.WorldEpoch, lifeEpoch = 1 })), "binding another entity is a change");
        Suite.Check(RuntimeNamedObjects.Read(world.Kernel, "test.alarm_a")!.Value.GetProperty("local").GetInt32() == wave.GetProperty("local").GetInt32(),
            "the other name kept its value");
        Suite.Reject(() => RuntimeNamedObjects.Bind(world.Kernel, "test.missing",
            RuntimeJson.From(new { id = "test.entity:9", worldEpoch = world.Kernel.WorldEpoch, lifeEpoch = 1 })), "variable-undeclared");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Checkpoints, snapshots and scope release
    // ---------------------------------------------------------------------------------------------------------
    private static void CheckpointRestore()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.checkpoint", ("test.counter", "level", "number"), ("test.foe", "enemy", "number"))
            .Initial("test.counter", 0).Initial("test.foe", 1);
        var counter = new Dictionary<string, object?> { ["name"] = "test.counter", ["value_type"] = Number };
        builder.Step("AOnce", "control", VariableContracts.OnceBinding, new Dictionary<string, object?>(), Array.Empty<object>(), new int?[] { 1, 2 });
        builder.Step("BFirst", "control", VariableContracts.WriteBinding, counter,
            new[] { PlanBuilder.Literal(ValueSlot(builder, "test.counter"), 1) }, new int?[] { 3 });
        builder.Step("CLater", "control", VariableContracts.WriteBinding, counter,
            new[] { PlanBuilder.Literal(ValueSlot(builder, "test.counter"), 2) }, new int?[] { 3 });
        // One enemy-scoped value, written by whatever the dispatch is about: a checkpoint carries it, and a reload
        // re-creates the level's enemies, so the enemy half is the one scope a restore does not put back.
        var foe = new Dictionary<string, object?> { ["name"] = "test.foe", ["value_type"] = Number };
        builder.Step("DWriteEnemy", "control", VariableContracts.WriteBinding, foe,
            new[]
            {
                PlanBuilder.FromEvent(builder.Port("inputs", VariableContracts.WriteBinding, foe, "subject"), Event(builder, "target")),
                PlanBuilder.Literal(builder.Port("inputs", VariableContracts.WriteBinding, foe, "value"), 1)
            }, new int?[] { null });
        world.Load(builder.Build());
        world.Ping("p1");
        world.Advance(10);
        Suite.Check(Value(world, "test.counter|level||-1") == 1, "the first dispatch wrote 1");
        Suite.Check(Entries(world.Kernel).ContainsKey("test.foe|enemy|test.entity:1|-1"), "the enemy-scoped value was written");

        var saved = world.Kernel.CaptureCheckpoint();
        world.Ping("p2", tick: 11);
        world.Advance(11);
        Suite.Check(Value(world, "test.counter|level||-1") == 2, "the second dispatch took the later exit and wrote 2");
        world.Kernel.RestoreCheckpoint(saved, world.Candidates());
        Suite.Check(Value(world, "test.counter|level||-1") == 1, "the checkpoint put the saved value back");
        Suite.Check(!Entries(world.Kernel).ContainsKey("test.foe|enemy|test.entity:1|-1"), "a per-enemy value does not come back with a reloaded checkpoint");
        world.Ping("p3", tick: 12);
        world.Advance(12);
        Suite.Check(Value(world, "test.counter|level||-1") == 2, "the restored once latch still reads as already fired");
    }

    private static void ScopeRelease()
    {
        var world = new VariableFixture();
        var builder = Variables(world, "test.vars.release", ("test.hp", "player", "number")).Initial("test.hp", 100);
        SubjectWrite(builder, "test.hp", 55, "AWritePlayer", null, null);
        world.Load(builder.Build());
        world.Ping("p1");
        world.Advance(10);
        Suite.Check(Entries(world.Kernel).Count == 1, "the value is stored");
        Suite.Check(world.Kernel.ReleaseVariableScope("player", world.Target) == 1, "releasing the subject drops its entry");
        Suite.Check(Entries(world.Kernel).Count == 0, "the entry is gone");
        Suite.Check(world.Kernel.ReleaseVariableScope("player", world.Target) == 0, "releasing it twice is not a second removal");
    }

    private static void HostSnapshot()
    {
        var host = new VariableFixture();
        host.Load(RoundTrip(host, "test.vars.snapshot"));
        host.Ping("p1");
        host.Advance(10);
        var snapshot = host.Kernel.ExportVariables();

        var migrated = new VariableFixture();
        migrated.Load(RoundTrip(migrated, "test.vars.snapshot"));
        migrated.Kernel.ApplyVariables(snapshot);
        Suite.Check(migrated.Kernel.ExportVariables() == snapshot, "the snapshot reproduces the host's variable state exactly");
        migrated.Ping("p1");
        migrated.Advance(10);
        Suite.Check(Value(migrated, "test.seen2|level||-1") == 42, "the migrated value is what the next dispatch reads");
        var elsewhere = RuntimeJson.StableText(RuntimeJson.From(new
        {
            worldEpoch = migrated.Kernel.WorldEpoch + 1, entries = Array.Empty<object>()
        }));
        Suite.Reject(() => migrated.Kernel.ApplyVariables(elsewhere), "variable-snapshot");
    }

    private static string RoundTrip(VariableFixture world, string planId)
    {
        var builder = Variables(world, planId, ("test.shield", "level", "number"), ("test.seen", "level", "number"), ("test.seen2", "level", "number"))
            .Initial("test.shield", 7).Initial("test.seen", 0).Initial("test.seen2", 0);
        var first = ReadStep(builder, "test.shield", "ARead", 1);
        WriteFrom(builder, "test.seen", first, ReadValue(builder), "BWriteSeen", 2);
        WriteStep(builder, "test.shield", "CWriteShield", new[] { PlanBuilder.Literal(ValueSlot(builder, "test.shield"), 42) }, 3);
        var second = ReadStep(builder, "test.shield", "DRead", 4);
        WriteFrom(builder, "test.seen2", second, ReadValue(builder), "EWriteSeen", null);
        return builder.Build();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Refusals
    // ---------------------------------------------------------------------------------------------------------
    private static void UndeclaredRefusal()
    {
        var world = new VariableFixture();
        var builder = new PlanBuilder(world, VariableFixture.PingBinding).Named("test.vars.undeclared");
        ReadStep(builder, "test.nothing", "ARead", null);
        world.Load(builder.Build());
        world.Ping("p1");
        var tick = world.Advance(10);
        Suite.Check(tick.Events.Count == 1 && tick.Events[0].Code == "variable-undeclared", "a name no plan declares is refused by name");
    }

    private static void TypeMismatch()
    {
        var world = new VariableFixture();
        var read = new Dictionary<string, object?> { ["name"] = "test.shield", ["value_type"] = Text };
        var builder = new PlanBuilder(world, VariableFixture.PingBinding).Named("test.vars.typemismatch")
            .Variable("test.shield", "level", "number", 1);
        builder.Step("ARead", "control", VariableContracts.ReadBinding, read, Array.Empty<object>(), new int?[] { null });
        world.Load(builder.Build());
        world.Ping("p1");
        var tick = world.Advance(10);
        Suite.Check(tick.Events.Count == 1 && tick.Events[0].Code == "variable-type", "a node that moves another type than the declaration is refused");
    }

    private static void NamedScopeRefusal()
    {
        var world = new VariableFixture();
        var builder = new PlanBuilder(world, VariableFixture.PingBinding).Named("test.vars.namedscope")
            .Variable("test.alarm_a", "named", "entity", null);
        ReadStep(builder, "test.alarm_a", "ARead", null, Entity);
        var outcome = world.Kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/plan.plan.json", builder.Build()) })[0];
        Suite.Check(!outcome.Loaded && outcome.Code == "variable-scope", "a named variable belongs in objects[], not variables[]");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Plan pieces
    // ---------------------------------------------------------------------------------------------------------
    private static PlanBuilder Variables(VariableFixture world, string planId, params (string Name, string Scope, string Type)[] variables)
    {
        var builder = new PlanBuilder(world, VariableFixture.PingBinding).Named(planId);
        foreach (var (name, scope, type) in variables) builder.Variable(name, scope, type == "number" ? "number" : type, 0);
        return builder;
    }

    private static int ReadStep(PlanBuilder builder, string name, string nodeId, int? next, int portType = Number)
    {
        var parameters = new Dictionary<string, object?> { ["name"] = name, ["value_type"] = portType };
        return builder.Step(nodeId, "control", VariableContracts.ReadBinding, parameters, Array.Empty<object>(), new int?[] { next });
    }

    private static int WriteStep(PlanBuilder builder, string name, string nodeId, object[] inputs, int? next)
    {
        var parameters = new Dictionary<string, object?> { ["name"] = name, ["value_type"] = Number };
        return builder.Step(nodeId, "control", VariableContracts.WriteBinding, parameters, inputs, new int?[] { next });
    }

    /// <summary>A write whose subject comes from the trigger's own entity: the scope's own partition.</summary>
    private static int SubjectWrite(PlanBuilder builder, string name, double value, string nodeId, int? slot, int? next)
    {
        var parameters = new Dictionary<string, object?> { ["name"] = name, ["value_type"] = Number };
        var subject = builder.Port("inputs", VariableContracts.WriteBinding, parameters, "subject");
        var slotPort = builder.Port("inputs", VariableContracts.WriteBinding, parameters, "slot");
        var valuePort = builder.Port("inputs", VariableContracts.WriteBinding, parameters, "value");
        var inputs = new List<object> { PlanBuilder.FromEvent(subject, Event(builder, "target")), PlanBuilder.Literal(valuePort, value) };
        if (slot is { } weaponSlot) inputs.Add(PlanBuilder.Literal(slotPort, weaponSlot));
        return builder.Step(nodeId, "control", VariableContracts.WriteBinding, parameters, inputs.OrderBy(Slot).ToArray(), new int?[] { next });
    }

    private static int WriteFrom(PlanBuilder builder, string name, int step, int producerPort, string nodeId, int? next)
        => WriteStep(builder, name, nodeId, new[] { PlanBuilder.FromStep(ValueSlot(builder, name), step, producerPort) }, next);

    /// <summary>The index of one payload port of the fixture trigger, so a case reads an event port by name.</summary>
    private static int Event(PlanBuilder builder, string port)
        => builder.Port("outputs", VariableFixture.PingBinding, new Dictionary<string, object?>(), port);

    private static int ValueSlot(PlanBuilder builder, string name)
    {
        var parameters = new Dictionary<string, object?> { ["name"] = name, ["value_type"] = Number };
        return builder.Port("inputs", VariableContracts.WriteBinding, parameters, "value");
    }

    /// <summary>The `value` output of a read row: a read's port list does not depend on which variable it names.</summary>
    private static int ReadValue(PlanBuilder builder)
        => builder.Port("outputs", VariableContracts.ReadBinding, new Dictionary<string, object?> { ["name"] = "test.shield", ["value_type"] = Number }, "value");

    private static int Slot(object row) => (int)row.GetType().GetProperty("slot")!.GetValue(row)!;

    private static double Branch(VariableFixture world) => Value(world, "test.branch|level||-1");

    private static double Value(VariableFixture world, string key) => Entries(world.Kernel)[key].GetProperty("value").GetDouble();

    /// <summary>Every variable the host holds, keyed the way the snapshot writes it: name, scope, subject, slot.</summary>
    private static Dictionary<string, JsonElement> Entries(RuntimeKernel kernel)
        => RuntimeJson.Parse(kernel.ExportVariables()).GetProperty("entries").EnumerateArray()
            .ToDictionary(entry => RuntimeJson.Text(entry, "name") + "|" + RuntimeJson.Text(entry, "scope") + "|"
                + entry.GetProperty("subject").GetString() + "|" + entry.GetProperty("slot").GetInt32());
}
