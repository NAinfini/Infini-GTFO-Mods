using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.WorldEventFacts;

/// <summary>
/// The focused cases for the objective-event family: the two rows the contract declares, the ids and ports a plan
/// names them by, the decisions the two handlers make and every code they refuse with, and the result a caller
/// reads back. The decisions are the game-independent half of the native handlers, so a case that drives them
/// drives everything except the one call to the engine's event executor.
/// </summary>
public sealed class ObjectiveEventTests
{
    // ---- the declaration ----------------------------------------------------------------------------

    [Fact]
    public void TheContractDeclaresTheProvidersOwnId()
        => Assert.Equal(ModuleDefinition.ProviderId, ObjectiveEventContract.ProviderId);

    [Fact]
    public void TheKernelAcceptsTheTwoRowsAndTheirBodies()
    {
        // A registration an actual kernel refuses is the one failure a row cannot survive, so the rows are
        // installed rather than only inspected: the kernel resolves every handler shape against the capability its
        // binding implements, which makes this the exactness case for both rows at once.
        using var world = ObjectiveEventWorld.Start();
        Assert.NotEqual(0, world.Kernel.WorldEpoch);
    }

    [Fact]
    public void EveryCapabilityHasExactlyOneBindingAndOneSupportRow()
    {
        var capabilities = ObjectiveEventContract.CapabilityRows();
        var bindings = ObjectiveEventContract.BindingRows();
        var support = ObjectiveEventContract.Supports();
        Assert.Equal(2, capabilities.Length);
        Assert.Equal(capabilities.Length, bindings.Length);
        Assert.Equal(bindings.Length, support.Length);

        var capabilityIds = capabilities.Select(row => Id(RuntimeJson.From(row))).ToArray();
        Assert.Equal(capabilityIds.Length, capabilityIds.Distinct(StringComparer.Ordinal).Count());
        var bindingIds = bindings.Select(row => Id(RuntimeJson.From(row))).ToArray();
        Assert.Equal(bindingIds.Length, bindingIds.Distinct(StringComparer.Ordinal).Count());

        for (int index = 0; index < bindings.Length; index++)
        {
            var declared = Text(RuntimeJson.From(bindings[index]), "capabilityId");
            Assert.Contains(declared, capabilityIds);
            Assert.Equal(bindingIds[index], ObjectiveEventContract.Binding(declared));
            Assert.Equal(bindingIds[index], support[index].BindingId);
        }
    }

    [Fact]
    public void TheTwoRowsAreNewCanonicalIdsAndTheirEndsFailClosed()
    {
        // The site's catalog declares no node for either native event type (`catalog/native-event-types.json`
        // records both with `canonical: null`), so these are the first declarations of their ids. The row the
        // catalog *does* declare — `forge.action.map.spawn_commit` for `SpawnEnemyOnPoint` — is deliberately not
        // declared here, because its contract is a solved spawn plan rather than an event-table point.
        Assert.Equal("forge.action.presentation.objective_display", ObjectiveEventContract.DisplayCapability);
        Assert.Equal("forge.action.map.objective_progress", ObjectiveEventContract.ProgressCapability);
        Assert.Equal("objective.text", ObjectiveEventContract.DisplayPermission);
        Assert.Equal("objective.progress", ObjectiveEventContract.ProgressPermission);
        Assert.Throws<RuntimeContractException>(() => ObjectiveEventContract.Binding("gtfo.action.map.something"));
    }

    [Fact]
    public void TheRowsDeclareThePortsTheNativeHalfReads()
    {
        var rows = ObjectiveEventContract.CapabilityRows().Select(RuntimeJson.From).ToArray();

        // The display row: the two texts are plan values, the layer is the structural choice, and the result
        // reports what was written.
        var display = rows[0].GetProperty("graph");
        Assert.Equal(new[] { "in", "objectives", "header", "body" },
            Inputs(display).Select(port => Id(port)).ToArray());
        Assert.Equal("objective", Inputs(display).Single(port => Id(port) == "objectives")
            .GetProperty("resourceKind").GetString());
        Assert.Equal("forge.resource.objective", Inputs(display).Single(port => Id(port) == "objectives")
            .GetProperty("schema").GetString());
        Assert.Equal(new[] { "layer" }, display.GetProperty("parameters").EnumerateArray()
            .Select(row => Id(row)).ToArray());
        Assert.True(display.GetProperty("parameters").EnumerateArray().Single().GetProperty("required").GetBoolean());
        Assert.Equal(new[] { "main", "secondary", "third" }, display.GetProperty("parameters").EnumerateArray()
            .Single().GetProperty("values").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal("host", display.GetProperty("execution").GetString());
        Assert.Equal("objectives", display.GetProperty("recipients").GetProperty("input").GetString());

        // The progression row: the step count is a plan value, so it is an input rather than a parameter.
        var progress = rows[1].GetProperty("graph");
        Assert.Equal(new[] { "in", "objectives", "steps" }, Inputs(progress).Select(port => Id(port)).ToArray());
        Assert.Equal("integer", Inputs(progress).Single(port => Id(port) == "steps").GetProperty("type").GetString());

        Assert.Equal(new[] { "target", "status", "committed", "code", "layer", "header", "body" },
            Result(rows[0]).GetProperty("fields").EnumerateArray().Select(field => Id(field)).ToArray());
        Assert.Equal(new[] { "target", "status", "committed", "code", "layer", "steps" },
            Result(rows[1]).GetProperty("fields").EnumerateArray().Select(field => Id(field)).ToArray());
        Assert.Equal("forge.result.presentation.objective_display", Result(rows[0]).GetProperty("schema").GetString());
        Assert.Equal("forge.result.map.objective_progress", Result(rows[1]).GetProperty("schema").GetString());
    }

    [Fact]
    public void TheShapeTableNamesOneShapePerHandler()
        => Assert.Equal(
            new[] { ObjectiveEventContract.DisplayHandlerName, ObjectiveEventContract.ProgressHandlerName }
                .OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            ObjectiveEventContract.Shapes().Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());

    // ---- the display decision -----------------------------------------------------------------------

    [Fact]
    public void TheDisplayActionRefusesAClient()
    {
        using var world = ObjectiveEventWorld.Start();
        var result = world.Display(world.Context(
            new { header = "Sub", body = "Body" }, new { layer = "main" }, isHost: false));
        Assert.Equal("rejected", result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal(ObjectiveEventContract.AuthorityCode, result.Code);
    }

    [Fact]
    public void TheDisplayActionRefusesARecipientAndAnUnknownLayer()
    {
        using var world = ObjectiveEventWorld.Start();
        var recipient = world.Display(world.Context(
            new { objectives = new { id = "objective:1", revision = 1 }, header = "Sub", body = "Body" },
            new { layer = "main" }));
        Assert.Equal(ObjectiveEventContract.TargetCode, recipient.Code);

        var layer = world.Display(world.Context(new { header = "Sub", body = "Body" }, new { layer = "side" }));
        Assert.Equal(ObjectiveEventContract.LayerCode, layer.Code);

        var ordinal = world.Display(world.Context(new { header = "Sub", body = "Body" }, new { layer = 9 }));
        Assert.Equal(ObjectiveEventContract.LayerCode, ordinal.Code);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Sub", null)]
    [InlineData(null, "Body")]
    [InlineData("", "Body")]
    [InlineData("Sub", "")]
    public void TheDisplayActionRefusesATextItCannotWrite(string? header, string? body)
    {
        using var world = ObjectiveEventWorld.Start();
        var result = world.Display(world.Context(new { header, body }, new { layer = "main" }));
        Assert.Equal("rejected", result.Status);
        Assert.Equal(ObjectiveEventContract.TextCode, result.Code);
    }

    [Fact]
    public void TheDisplayActionReportsTheTextItWrote()
    {
        using var world = ObjectiveEventWorld.Start();
        var result = world.Display(world.Context(
            new { header = "Door opened", body = "Find the HSU" }, new { layer = "secondary" }));
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal("secondary", result.Outputs.GetProperty("layer").GetString());
        Assert.Equal("Door opened", result.Outputs.GetProperty("header").GetString());
        Assert.Equal("Find the HSU", result.Outputs.GetProperty("body").GetString());
    }

    // ---- the progression decision -------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void TheProgressActionRefusesANonPositiveStep(int steps)
    {
        using var world = ObjectiveEventWorld.Start();
        var result = world.Progress(world.Context(new { steps }, new { layer = "main" }));
        Assert.Equal("rejected", result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal(ObjectiveEventContract.StepsCode, result.Code);
    }

    [Fact]
    public void TheProgressActionRefusesAMissingStepAndAClient()
    {
        using var world = ObjectiveEventWorld.Start();
        Assert.Equal(ObjectiveEventContract.StepsCode,
            world.Progress(world.Context(new { }, new { layer = "main" })).Code);
        Assert.Equal(ObjectiveEventContract.AuthorityCode,
            world.Progress(world.Context(new { steps = 1 }, new { layer = "main" }, isHost: false)).Code);
        Assert.Equal(ObjectiveEventContract.TargetCode, world.Progress(world.Context(
            new { objectives = new { id = "objective:1", revision = 1 }, steps = 1 }, new { layer = "main" })).Code);
    }

    [Fact]
    public void TheProgressActionReportsTheLayerAndTheStep()
    {
        using var world = ObjectiveEventWorld.Start();
        var result = world.Progress(world.Context(new { steps = 5 }, new { layer = 1 }));
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal("secondary", result.Outputs.GetProperty("layer").GetString());
        Assert.Equal(5, result.Outputs.GetProperty("steps").GetInt32());
    }

    // ---- the shape every row's graph is read with ---------------------------------------------------

    private static string? Id(JsonElement row) => row.GetProperty("id").GetString();

    private static string? Text(JsonElement row, string name) => row.GetProperty(name).GetString();

    private static JsonElement[] Inputs(JsonElement graph)
        => graph.GetProperty("inputs").EnumerateArray().ToArray();

    private static JsonElement Result(JsonElement row)
        => row.GetProperty("graph").GetProperty("outputs").EnumerateArray()
            .Single(port => Id(port) == "result");
}
