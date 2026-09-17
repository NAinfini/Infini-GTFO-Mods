using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LevelGeneration;
using static ForgeMap.LevelObjectiveValueContract;

namespace ForgeMap.Tests.ObjectiveValueFacts;

/// <summary>
/// The `v-obj` row's cases: what it declares, what one read answers for each field, and every refusal path. The
/// evaluator and the read are the production ones; only the objective machine is the double, so a case asserts the
/// member the read reached and the value the contract published.
/// </summary>
public sealed class ObjectiveValueFactsTests
{
    // ---------------------------------------------------------------- declaration

    [Fact]
    public void TheContractDeclaresTheProvidersOwnId()
    {
        // The level-event contract of this same package spells the same id, and `ModuleDefinition.ProviderId` is
        // that same constant again: this is the cross-check that keeps the copy in step.
        Assert.Equal(LevelEventContract.ProviderId, ProviderId);
        Assert.Equal(ProviderId + ".binding.query.objective.state", BindingId);
        Assert.Equal("forge.query.objective.state", CapabilityId);
    }

    [Fact]
    public void TheDeclaredRowIsTheRowTheContractPublishes()
    {
        var capability = ObjectiveWorld.Capability;
        Assert.Equal(CapabilityId, capability.GetProperty("id").GetString());
        Assert.Equal(ProviderId, capability.GetProperty("owner").GetString());
        // A value row is `state` and runs as an on-demand `query`: the two fields the framework keys its
        // registration on.
        Assert.Equal("state", capability.GetProperty("kind").GetString());
        Assert.Equal("query", capability.GetProperty("graph").GetProperty("execution").GetString());

        var binding = ObjectiveWorld.Binding;
        Assert.Equal(BindingId, binding.GetProperty("id").GetString());
        Assert.Equal(CapabilityId, binding.GetProperty("capabilityId").GetString());
        Assert.Equal(HandlerName, binding.GetProperty("handler").GetString());
        Assert.Equal("observe", binding.GetProperty("role").GetString());
    }

    [Fact]
    public void TheInputPortIsTheObjectiveReferenceTheLevelEventRowsPublish()
    {
        var inputs = ObjectiveWorld.Capability.GetProperty("graph").GetProperty("inputs");
        var port = Assert.Single(inputs.EnumerateArray());
        Assert.Equal(InputPort, port.GetProperty("id").GetString());
        Assert.Equal("resource", port.GetProperty("type").GetString());
        Assert.Equal(ObjectiveResourceKind, port.GetProperty("resourceKind").GetString());
        Assert.Equal(ObjectiveResourceSchema, port.GetProperty("schema").GetString());
        // The kind and schema are the level-event rows' own — the HSU row carries the objective resource port —
        // asserted here so the two families cannot drift. The row itself is the runtime trigger contract's
        // declaration (ruling 148.3), which is the one the level event's binding is resolved against.
        var trigger = TriggerContracts.Rows(new[] { LevelEventContract.HsuSampledCapability })[0]
            .GetProperty("graph").GetProperty("outputs")
            .EnumerateArray()
            .First(output => output.GetProperty("id").GetString() == "objective");
        Assert.Equal(ObjectiveResourceKind, trigger.GetProperty("resourceKind").GetString());
        Assert.Equal(ObjectiveResourceSchema, trigger.GetProperty("schema").GetString());
    }

    [Fact]
    public void TheOutputPortsAreTheOnesTheHandlerDeclares()
    {
        var outputs = ObjectiveWorld.Capability.GetProperty("graph").GetProperty("outputs")
            .EnumerateArray().Select(port => port.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(Shape.OutputPorts, outputs);
        Assert.Equal(Layers, LevelEventContract.Layers);
    }

    [Fact]
    public void OneBindingCarriesTheRowsPermissionAndNoDependency()
    {
        var support = Support();
        Assert.Equal(BindingId, support.BindingId);
        Assert.Equal(new[] { LevelEventContract.ObjectiveReadPermission }, support.RequiredPermissions);
    }

    [Fact]
    public void TheDeclaredRowsRegisterUnderADefinitionTheKernelAccepts()
    {
        using var world = ObjectiveWorld.Start();
        var manifest = RuntimeJson.Parse(world.Kernel.ExportManifest());
        var registry = manifest.GetProperty("registry");
        Assert.Contains(registry.GetProperty("capabilities").EnumerateArray(),
            row => row.GetProperty("id").GetString() == CapabilityId);
        Assert.Contains(registry.GetProperty("bindings").EnumerateArray(),
            row => row.GetProperty("id").GetString() == BindingId);
    }

    // ---------------------------------------------------------------- one read

    [Fact]
    public void AReadAnswersEveryPortFromTheStateItRead()
    {
        using var machine = new Machine();
        // The chain index is written before the layer is filled, because the read resolves the objective instance
        // through the state's own chain — the same lookup the machine itself uses.
        machine.State.main_chainIndex = 3;
        machine.Layer("secondary", eWardenObjectiveType.Survival);
        machine.State.main_status = eWardenObjectiveStatus.Started;
        machine.State.main_subObj = eWardenSubObjectiveStatus.InZoneFindItem;
        machine.State.main_startTime = 123.5f;
        machine.State.forceWinOnDeath = true;
        machine.State.exitWaveTriggered = false;
        machine.State.ObjectiveItemStates = Bytes(1, 0, 1, 1);
        machine.State.RequiredObjectiveItems = Bytes(1, 1);

        var answer = Read("layer:secondary");
        Assert.Equal((int)eWardenObjectiveType.Survival, answer.GetProperty("kind").GetInt32());
        Assert.True(answer.GetProperty("timed").GetBoolean());
        Assert.Equal("started", answer.GetProperty("phase").GetString());
        Assert.Equal("in_zone_find_item", answer.GetProperty("sub_phase").GetString());
        Assert.Equal(3, answer.GetProperty("chain_index").GetInt32());
        Assert.Equal(7410, answer.GetProperty("start_time").GetDouble(), 3); // 123.5 s in ticks
        Assert.True(answer.GetProperty("solve_on_death").GetBoolean());
        Assert.False(answer.GetProperty("exit_wave_triggered").GetBoolean());
        Assert.Equal(3, answer.GetProperty("items_solved").GetInt32());
        Assert.Equal(2, answer.GetProperty("required_items").GetInt32());
    }

    [Fact]
    public void TheRowCarriesNoCountdownPortAndReadsNoCountdownMember()
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.TimedTerminalSequence);
        machine.State.main_startTime = 123.5f;
        machine.State.extraTime = 90.5f;

        // Ruling 124.2: the countdown member's wire meaning was never established, so the row names no port for
        // it. The declared shape, the declared capability row and the payload the evaluator answers must agree —
        // a port in one of the three and not the others is the drift this case exists for.
        Assert.DoesNotContain("time_left_seconds", Shape.OutputPorts);
        Assert.DoesNotContain(ObjectiveWorld.Capability.GetProperty("graph").GetProperty("outputs").EnumerateArray()
            .Select(port => port.GetProperty("id").GetString()!), name => name == "time_left_seconds");        // The member is not read at all: the machine holds 90.5 and the payload answers only the ports it
        // declares, of which the one time value is the state's own start time.
        var answer = Read("layer:main");
        Assert.False(answer.TryGetProperty("time_left_seconds", out _));
        Assert.Equal(7410, answer.GetProperty("start_time").GetDouble(), 3); // 123.5 s in ticks
    }

    [Fact]
    public void OnlyTheTwoTimerObjectivesReportTimed()
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.Survival);
        Assert.True(Read("layer:main").GetProperty("timed").GetBoolean());

        machine.Layer("main", eWardenObjectiveType.TimedTerminalSequence);
        Assert.True(Read("layer:main").GetProperty("timed").GetBoolean());

        machine.Layer("main", eWardenObjectiveType.Reactor_Startup);
        Assert.False(Read("layer:main").GetProperty("timed").GetBoolean());
        Assert.Equal((int)eWardenObjectiveType.Reactor_Startup, Read("layer:main").GetProperty("kind").GetInt32());
    }

    [Fact]
    public void ALayerWhoseObjectiveInstanceIsGoneReportsTheEmptyKind()
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.Survival);
        // The machine still has data for the layer but no instance at the state's chain: the read reports the
        // game's own `Empty` member rather than guessing the last type it saw.
        machine.State.main_chainIndex = 7;
        var answer = Read("layer:main");
        Assert.Equal(14, answer.GetProperty("kind").GetInt32());
        Assert.False(answer.GetProperty("timed").GetBoolean());
    }

    [Fact]
    public void TheThirdsLayerIsTheThirdSlotAndNotTheNearestOne()
    {
        using var machine = new Machine();
        machine.Layer("third", eWardenObjectiveType.CentralGeneratorCluster);
        Assert.Equal((int)eWardenObjectiveType.CentralGeneratorCluster,
            Read("layer:third").GetProperty("kind").GetInt32());
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void ALayerWithNoObjectiveDataIsRefused()
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.Survival);
        Assert.Equal(NoObjectiveCode, Refusal("layer:third"));
    }

    [Fact]
    public void AStateTheProcessCannotReadIsRefusedRatherThanAnsweredWithZeros()
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.Survival);
        WardenObjectiveManager.CurrentState = null;
        Assert.Equal(NoObjectiveCode, Refusal("layer:main"));
    }

    [Fact]
    public void AReferenceOfAnotherKindIsRefusedByName()
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.Survival);
        var error = Assert.Throws<RuntimeContractException>(() => Evaluate(new
        {
            objective = new { resourceKind = "zone", resourceId = "layer:main" }
        }));
        Assert.Equal(InputKindCode, error.Code);
    }

    [Theory]
    [InlineData("layer:fourth")]
    [InlineData("objective:main")]
    [InlineData("main")]
    [InlineData("layer:")]
    public void AnInputThatIsNotALayerReferenceIsRefusedByName(string referenceId)
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.Survival);
        var error = Assert.Throws<RuntimeContractException>(() => Evaluate(new { objective = new { id = referenceId } }));
        Assert.Equal(InputLayerCode, error.Code);
    }

    [Fact]
    public void AMissingObjectiveInputIsRefusedByPortName()
    {
        var error = Assert.Throws<RuntimeContractException>(() => Evaluate(RuntimeJson.EmptyObject));
        Assert.Equal("missing-field", error.Code);
        Assert.Equal(InputPort, error.Message);
    }

    [Fact]
    public void AStatusOutsideTheVocabularyIsRefusedRatherThanShipped()
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.Survival);
        machine.State.main_status = (eWardenObjectiveStatus)50;
        Assert.Equal("objective-status-unknown", Refusal("layer:main"));
    }

    [Fact]
    public void ASubStatusOutsideTheVocabularyIsRefusedRatherThanShipped()
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.Survival);
        machine.State.main_subObj = (eWardenSubObjectiveStatus)10;
        Assert.Equal("objective-sub-status-unknown", Refusal("layer:main"));
    }

    // ---------------------------------------------------------------- vocabulary

    [Fact]
    public void EveryLayerAndEveryStatusMemberHasItsOwnName()
    {
        Assert.Equal(new[] { "main", "secondary", "third" }, Layers);
        Assert.Equal(5, Statuses.Count);
        Assert.Equal(new[] { "not_discovered", "discovered", "started", "partially_solved", "item_solved" },
            Statuses.Select(member => member.Name).ToArray());
        Assert.Equal(new[] { 0, 10, 20, 30, 40 }, Statuses.Select(member => member.Value).ToArray());
        foreach (var (value, name) in Statuses) Assert.Equal(name, StatusName(value));
        Assert.Throws<RuntimeContractException>(() => StatusName(1));
    }

    [Fact]
    public void EverySubStatusMemberHasItsIndexName()
    {
        Assert.Equal(10, SubStatuses.Count);
        for (int index = 0; index < SubStatuses.Count; index++) Assert.Equal(SubStatuses[index], SubStatusName(index));
        Assert.Throws<RuntimeContractException>(() => SubStatusName(-1));
        Assert.Throws<RuntimeContractException>(() => SubStatusName(10));
    }

    [Fact]
    public void EveryObjectiveKindAndEveryLayerReferenceRoundTrips()
    {
        Assert.Equal(16, Kinds.Count);
        Assert.Equal("survival", Kinds[SurvivalKind]);
        Assert.Equal("timed_terminal_sequence", Kinds[TimedTerminalSequenceKind]);
        Assert.True(IsTimed(SurvivalKind));
        Assert.True(IsTimed(TimedTerminalSequenceKind));
        Assert.False(IsTimed(0));
        foreach (var layer in Layers) Assert.Equal(layer, LayerOf(LayerReferencePrefix + layer));
        Assert.Null(LayerOf("layer:fourth"));
        Assert.Null(LayerOf(null));
        Assert.Null(LayerOf("main"));
    }

    // ---------------------------------------------------------------- world lifetime

    [Fact]
    public void ANewWorldReadsTheStateOfItsOwnEpoch()
    {
        using var machine = new Machine();
        machine.Layer("main", eWardenObjectiveType.Survival);
        machine.State.main_startTime = 10f;
        using var first = ObjectiveWorld.Start(1);
        Assert.Equal(600, Read("layer:main").GetProperty("start_time").GetDouble(), 3); // 10 s in ticks

        // The same process, a second world: the read answers the state it finds now, because the row keeps no
        // value of its own between reads.
        machine.State.main_startTime = 5f;
        using var second = ObjectiveWorld.Start(2);
        Assert.Equal(300, Read("layer:main").GetProperty("start_time").GetDouble(), 3); // 5 s in ticks
        Assert.Equal(2, second.Kernel.WorldEpoch);
        Assert.Equal(1, first.Kernel.WorldEpoch);
    }

    // ---------------------------------------------------------------- plumbing

    private static JsonElement Read(string referenceId)
        => Evaluate(new { objective = new { id = referenceId } });

    private static string Refusal(string referenceId)
    {
        var error = Assert.Throws<RuntimeContractException>(() => Read(referenceId));
        return error.Code;
    }

    private static JsonElement Evaluate(object inputs)
        => Evaluator(new LayerReader(LevelObjectiveValueReader.Read))(ObjectiveWorld.Context(inputs));

    private static Il2CppStructArray<byte> Bytes(params byte[] values) => new(values);
}

