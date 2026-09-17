using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.WorldEventFacts;

/// <summary>
/// The focused cases for the world-event family: the three rows the contract declares, the ids a plan names them
/// by, the one decision the condition action makes and every code it refuses with, and the result a caller reads
/// back. Nothing here touches the game — the decision is the game-independent half of the native handler, so a
/// case that drives it drives everything except the one call to the engine's event executor.
/// </summary>
public sealed class WorldEventContractTests
{
    // ---- the declaration ----------------------------------------------------------------------------

    [Fact]
    public void TheContractDeclaresTheProvidersOwnId()
        => Assert.Equal(ModuleDefinition.ProviderId, WorldEventContract.ProviderId);

    [Fact]
    public void TheKernelAcceptsTheThreeRowsAndTheirBodies()
    {
        // A registration an actual kernel refuses is the one failure a row cannot survive, so the rows are
        // installed rather than only inspected. The kernel answers a declared `execute` row with no body and a
        // supplied body with no row on its own, which is what makes this the exactness case for both directions.
        using var world = WorldEventWorld.Start();
        Assert.NotEqual(0, world.Kernel.WorldEpoch);
    }

    [Fact]
    public void EveryCapabilityHasExactlyOneBindingAndOneSupportRow()
    {
        var capabilities = WorldEventContract.CapabilityRows();
        var bindings = WorldEventContract.BindingRows();
        var support = WorldEventContract.Supports();
        Assert.Equal(3, capabilities.Length);
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
            Assert.Equal(bindingIds[index], WorldEventContract.Binding(declared));
            Assert.Equal(bindingIds[index], support[index].BindingId);
        }
    }

    [Fact]
    public void TheTwoTriggerRowsAreNewCanonicalInteractionsAndTheActionIsItsOwnRow()
    {
        // The catalog's own families are the ones a row must not silently duplicate: a `forge.trigger.map.*` row
        // names a map object the level generated, and this family's triggers are the two components a world event
        // object carries, which is the `interaction` family the door and terminal rows already use. The action is
        // not `forge.action.map.objective_state`: a world event condition is the flag a world event object's own
        // condition list reads, not objective state.
        Assert.Equal("forge.trigger.interaction.world_event_interact", WorldEventContract.WorldEventInteractCapability);
        Assert.Equal("forge.trigger.interaction.world_event_lookat", WorldEventContract.WorldEventLookatCapability);
        Assert.Equal("forge.action.map.world_event_condition", WorldEventContract.WorldEventConditionCapability);
        Assert.Equal(new[] { WorldEventContract.WorldEventInteractCapability, WorldEventContract.WorldEventLookatCapability },
            WorldEventContract.TriggerTable.Select(entry => entry.Capability).ToArray());
    }

    [Fact]
    public void TheRowsCarryTheCatalogShapeAndNoBodylessHandler()
    {
        var rows = WorldEventContract.CapabilityRows().Select(RuntimeJson.From).ToArray();
        foreach (var row in rows)
        {
            Assert.Equal(ModuleDefinition.ProviderId, row.GetProperty("owner").GetString());
            Assert.Equal("1.0.0", row.GetProperty("version").GetString());
            Assert.Equal("host", row.GetProperty("graph").GetProperty("execution").GetString());
            Assert.NotEqual(0, row.GetProperty("graph").GetProperty("domains").GetArrayLength());
        }
        Assert.Equal(new[] { "trigger", "trigger", "action" },
            rows.Select(row => row.GetProperty("kind").GetString()).ToArray());

        // A trigger row is an observation: it carries no handler table entry, so it declares no shape either, and
        // the shape table stays the exact set of handlers a registration supplies.
        var shapes = WorldEventContract.Shapes();
        Assert.Equal(new[] { WorldEventContract.WorldEventConditionHandlerName }, shapes.Keys.ToArray());
        Assert.Equal(WorldEventContract.ConditionShape, shapes[WorldEventContract.WorldEventConditionHandlerName]);
        var bindings = WorldEventContract.BindingRows().Select(RuntimeJson.From).ToArray();
        Assert.Equal(new[] { "observe", "observe", "execute" },
            bindings.Select(row => row.GetProperty("role").GetString()).ToArray());
    }

    [Fact]
    public void TheTriggerRowsCarryTheirTwoMomentsAsTheNativeBoolean()
    {
        // The look-at row carries one port more than the interact row: the trigger's own maximum distance, which
        // is the field that component kind exists for. A row that omitted it would leave the two trigger kinds
        // indistinguishable in a plan. A moment travels as the native trigger state `true`/`false` rather than as
        // an enum index, so the port needs no vocabulary the catalog does not already carry.
        var rows = WorldEventContract.CapabilityRows().Select(RuntimeJson.From).ToArray();
        var interact = Outputs(rows[0]);
        Assert.Equal(new[] { "next", "key", "triggered", "source", "zone", "position" }, PortIds(interact));
        Assert.Equal(new[] { "next", "key", "triggered", "source", "zone", "position", "lookat_distance" },
            PortIds(Outputs(rows[1])));
        Assert.Equal("boolean", interact.Single(port => Id(port) == "triggered").GetProperty("type").GetString());
        Assert.True(WorldEventContract.IsTriggered(WorldEventContract.TriggeredMoment));
        Assert.False(WorldEventContract.IsTriggered(WorldEventContract.ResetMoment));
        Assert.Equal("zone", interact.Single(port => Id(port) == "zone")
            .GetProperty("resourceKind").GetString());

        var action = rows[2].GetProperty("graph");
        var parameters = action.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(new[] { "condition", "solved" }, parameters.Select(row => Id(row)).ToArray());
        Assert.All(parameters, row => Assert.True(row.GetProperty("required").GetBoolean()));
        Assert.Equal("boolean", parameters[1].GetProperty("type").GetString());
        var result = Outputs(rows[2]).Single(port => Id(port) == "result");
        Assert.Equal(new[] { "target", "status", "committed", "code", "condition", "solved", "target_count" },
            result.GetProperty("fields").EnumerateArray().Select(field => Id(field)).ToArray());
    }

    private static string? Id(JsonElement row) => row.GetProperty("id").GetString();

    private static string? Text(JsonElement row, string name) => row.GetProperty(name).GetString();

    private static JsonElement[] Outputs(JsonElement row)
        => row.GetProperty("graph").GetProperty("outputs").EnumerateArray().ToArray();

    private static string?[] PortIds(JsonElement[] outputs)
        => outputs.Select(port => port.GetProperty("id").GetString()).ToArray();

    // ---- the one decision ---------------------------------------------------------------------------

    [Fact]
    public void TheConditionActionRefusesAClient()
    {
        using var world = WorldEventWorld.Start();
        Assert.False(WorldEventContract.TryCondition(
            world.Context(null, new { condition = 0, solved = true }, isHost: false), out _, out string? code));
        Assert.Equal(WorldEventContract.AuthorityCode, code);
    }

    [Fact]
    public void TheConditionActionRefusesATargetTheNativeEventCannotName()
    {
        using var world = WorldEventWorld.Start();
        Assert.False(WorldEventContract.TryCondition(
            world.Context(new { targets = "gtfo.map_object:1:0:0:1" }, new { condition = 0, solved = true }),
            out _, out string? code));
        Assert.Equal(WorldEventContract.TargetCode, code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public void TheConditionActionRefusesASlotTheMachineHasNoByteFor(int index)
    {
        using var world = WorldEventWorld.Start();
        Assert.False(WorldEventContract.TryCondition(
            world.Context(null, new { condition = index, solved = true }), out _, out string? code));
        Assert.Equal(WorldEventContract.ConditionCode, code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("solved")]
    public void TheConditionActionRefusesAValueThatIsNotABoolean(string shape)
    {
        using var world = WorldEventWorld.Start();
        object parameters = shape.Length == 0 ? new { condition = 3 } : new { condition = 3, solved = "maybe" };
        Assert.False(WorldEventContract.TryCondition(
            world.Context(null, parameters), out _, out string? code));
        Assert.Equal(WorldEventContract.SolvedCode, code);
    }

    [Fact]
    public void TheConditionActionAcceptsBothValuesOfItsOwnSlot()
    {
        using var world = WorldEventWorld.Start();
        Assert.True(WorldEventContract.TryCondition(
            world.Context(null, new { condition = 0, solved = true }), out var set, out _));
        Assert.Equal(0, set.Index);
        Assert.True(set.Solved);
        // `false` is a legal write: a level clears a flag its own earlier step set, which is why the value is a
        // structural boolean and not a `true`-only constraint.
        Assert.True(WorldEventContract.TryCondition(
            world.Context(null, new { condition = WorldEventContract.ConditionSlots - 1, solved = false }),
            out var cleared, out _));
        Assert.Equal(WorldEventContract.ConditionSlots - 1, cleared.Index);
        Assert.False(cleared.Solved);
        Assert.Equal("false", WorldEventContract.Outputs(cleared).GetProperty("solved").GetBoolean().ToString().ToLowerInvariant());
    }

    [Fact]
    public void TheConditionResultReadsBackTheSlotAndValueTheRequestNamed()
    {
        using var world = WorldEventWorld.Start();
        var result = world.Run(world.Context(null, new { condition = 7, solved = true }));
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(7, result.Outputs.GetProperty("condition").GetInt32());
        Assert.True(result.Outputs.GetProperty("solved").GetBoolean());
    }

    [Fact]
    public void ARejectedConditionCommittedNothing()
    {
        using var world = WorldEventWorld.Start();
        var result = world.Run(world.Context(null, new { condition = 99, solved = true }));
        Assert.Equal("rejected", result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal(WorldEventContract.ConditionCode, result.Code);
    }

    [Fact]
    public void TheConditionRefusalCarriesNoOutputAndTheFailureIsUnknown()
    {
        // A refusal wrote nothing, so it carries no outputs; a native call that threw may have written the
        // replicated slot before it threw, which is why the one commit-exception code reports `unknown` rather
        // than claiming nothing happened.
        var refused = WorldEventContract.Refused(WorldEventContract.ConditionCode);
        Assert.Empty(refused.Outputs.EnumerateObject());
        Assert.Equal(CommitStates.None, refused.CommitState);
        var failed = WorldEventContract.Failed("native-commit-exception");
        Assert.Equal("failed", failed.Status);
        Assert.Equal(CommitStates.Unknown, failed.CommitState);
        Assert.Equal("native-commit-exception", failed.Code);
    }

    // ---- the binding ids a plan mounts on -----------------------------------------------------------

    [Fact]
    public void EveryBindingIdIsTheCapabilitysOwnSuffixUnderThisProvider()
    {
        Assert.Equal($"{WorldEventContract.ProviderId}.binding.trigger_interaction_world_event_interact",
            WorldEventContract.Binding(WorldEventContract.WorldEventInteractCapability));
        Assert.Equal($"{WorldEventContract.ProviderId}.binding.action_map_world_event_condition",
            WorldEventContract.Binding(WorldEventContract.WorldEventConditionCapability));
        Assert.Throws<RuntimeContractException>(() => WorldEventContract.Binding("gtfo.zone"));
    }

    [Fact]
    public void TheHandlerNameIsTheOneTheRegistrationAsksABodyFor()
    {
        var binding = WorldEventContract.BindingRows().Select(RuntimeJson.From)
            .Single(row => Text(row, "capabilityId") == WorldEventContract.WorldEventConditionCapability);
        Assert.Equal("gtfo.map.world_event_condition", Text(binding, "handler"));
        Assert.Equal(WorldEventContract.WorldEventConditionHandlerName, Text(binding, "handler"));
        Assert.Equal("implemented", Text(binding, "status"));
    }
}
