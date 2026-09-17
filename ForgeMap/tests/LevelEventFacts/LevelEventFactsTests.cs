using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.LevelEventFacts;

/// <summary>
/// The focused cases for the level-event family: the declaration a registration installs, what each of the six
/// observation rows publishes and when, the absence a row with no reading produces, the refusal path of each of
/// the three actions, and the world-epoch cleanup. Nothing here touches the game: the module under test is the
/// game-independent half, so every port is asserted against the value a case supplied.
/// </summary>
public sealed class LevelEventFactsTests
{
    // ---- the declaration ----------------------------------------------------------------------------

    [Fact]
    public void TheContractDeclaresTheProvidersOwnId()
    {
        Assert.Equal(ModuleDefinition.ProviderId, LevelEventContract.ProviderId);
        Assert.Equal(6, LevelEventContract.Triggers.Count);
        Assert.Equal(3, LevelEventContract.Actions.Count);
    }

    [Fact]
    public void EveryCapabilityHasExactlyOneBindingAndOneSupportRow()
    {
        // The eight trigger rows are the runtime trigger contract's own declaration, so this file declares the
        // three action capabilities and binds all eleven rows.
        var capabilities = LevelEventContract.CapabilityRows();
        var bindings = LevelEventContract.BindingRows();
        var support = LevelEventContract.Supports();
        Assert.Equal(LevelEventContract.Actions.Count, capabilities.Length);
        Assert.Equal(LevelEventContract.Triggers.Count + LevelEventContract.Actions.Count, bindings.Length);
        Assert.Equal(bindings.Length, support.Length);

        var capabilityIds = capabilities.Select(row => RuntimeJson.Text(RuntimeJson.From(row), "id")).ToArray();
        Assert.Equal(capabilityIds.Length, capabilityIds.Distinct(StringComparer.Ordinal).Count());
        var bindingIds = bindings.Select(row => RuntimeJson.Text(RuntimeJson.From(row), "id")).ToArray();
        Assert.Equal(bindingIds.Length, bindingIds.Distinct(StringComparer.Ordinal).Count());

        for (int index = 0; index < bindings.Length; index++)
        {
            var declared = RuntimeJson.Text(RuntimeJson.From(bindings[index]), "capabilityId");
            // A trigger binding names a capability the trigger contract declares; an action binding names one of
            // this file's own rows. Either way the binding is the capability's own suffix under this provider.
            Assert.Equal(bindingIds[index], LevelEventContract.Binding(declared));
            Assert.Equal(bindingIds[index], support[index].BindingId);
        }
        for (int index = 0; index < LevelEventContract.Actions.Count; index++)
            Assert.Contains(RuntimeJson.Text(RuntimeJson.From(bindings[index + LevelEventContract.Triggers.Count]),
                "capabilityId"), capabilityIds);
    }

    [Fact]
    public void TheTriggerRowsAreTheRuntimeContractsOwnDeclarations()
    {
        // Ruling 148.3: a `forge.trigger.*` capability row is declared once, by `forge.contract.trigger`. This
        // family binds those rows and declares no copy of any of them, so the ids it publishes under resolve
        // through the contract module and nowhere else.
        var ids = LevelEventContract.Triggers.Select(entry => entry.Capability).ToArray();
        var rows = TriggerContracts.Rows(ids);
        for (int index = 0; index < ids.Length; index++)
        {
            Assert.Equal(ids[index], RuntimeJson.Text(rows[index], "id"));
            Assert.Equal("forge.contract.trigger", RuntimeJson.Text(rows[index], "owner"));
            Assert.Equal("trigger", RuntimeJson.Text(rows[index], "kind"));
        }
        var declared = LevelEventContract.CapabilityRows()
            .Select(row => RuntimeJson.Text(RuntimeJson.From(row), "id")).ToArray();
        foreach (var id in ids) Assert.DoesNotContain(id, declared);
        // The ports the facts publish are the canonical row's own, so the binding and the payload cannot drift
        // from the declaration a plan was written against.
        var expedition = Ports(rows[Array.IndexOf(ids, LevelEventContract.ExpeditionStartedCapability)]);
        Assert.Equal("map", Assert.Single(expedition, port => port.GetProperty("type").GetString() == "resource")
            .GetProperty("resourceKind").GetString());
        var hsu = Ports(rows[Array.IndexOf(ids, LevelEventContract.HsuSampledCapability)]);
        Assert.Equal(new[] { "next", "objective", "container" },
            hsu.Select(port => port.GetProperty("id").GetString()).ToArray());
    }

    private static JsonElement[] Ports(JsonElement row)
        => row.GetProperty("graph").GetProperty("outputs").EnumerateArray().ToArray();

    [Fact]
    public void EveryActionHandlerHasAShapeAndEveryTriggerNamesItsBinding()
    {
        var shapes = LevelEventContract.Shapes();
        Assert.Equal(LevelEventContract.Actions.Count, shapes.Count);
        foreach (var (_, handler) in LevelEventContract.Actions) Assert.True(shapes.ContainsKey(handler), handler);
        // A trigger row is an observation: it has no handler table entry, so it declares no shape either, which
        // is what keeps the shape table an exact set of the handlers a registration supplies.
        foreach (var (_, capability) in LevelEventContract.Triggers)
        {
            Assert.False(shapes.ContainsKey(LevelEventContract.HandlerName(capability)));
            Assert.Equal("gtfo.map." + LevelEventContract.Suffix(capability), LevelEventContract.HandlerName(capability));
        }
    }

    [Fact]
    public void TheDeclaredRowsRegisteredUnderADefinitionTheKernelAccepts()
    {
        using var world = LevelEventWorld.Start();
        Assert.True(world.Module.IsRegistered);
        // The kernel resolves every binding against the capability it implements and every handler against its
        // shape; a definition it refused would have thrown in `Start`, so reaching here is the assertion.
        Assert.Equal(0, world.Published.Count);
    }

    // ---- the rows -----------------------------------------------------------------------------------

    [Fact]
    public void ExpeditionStartPublishesTheLevelReferenceAndNothingWithoutOne()
    {
        using var world = LevelEventWorld.Start();
        string capability = LevelEventWorld.Capability(LevelEventContract.ExpeditionStartedFact);

        world.Module.ExpeditionStarted(null);
        world.Module.ExpeditionStarted("");
        Assert.Null(world.Last(capability));

        world.Module.ExpeditionStarted("31:A:0");
        var published = world.Last(capability);
        Assert.NotNull(published);
        var map = published!.Outputs.GetProperty("map");
        Assert.Equal("map", map.GetProperty("resourceKind").GetString());
        Assert.Equal("31:A:0", map.GetProperty("resourceId").GetString());
        Assert.StartsWith("gtfo.level.session.expedition_started:1:session:1", published.EventId);
        Assert.Equal(1, world.Count(capability));

        // The same expedition starting twice is one fact: the second report repeats the state it already carried.
        world.Module.ExpeditionStarted("31:A:0");
        Assert.Equal(1, world.Count(capability));
    }

    [Fact]
    public void AReactorWaveIsTheChainStepItAdvancedTo()
    {
        using var world = LevelEventWorld.Start();
        string capability = LevelEventWorld.Capability(LevelEventContract.ReactorWaveFact);

        // A wave that did not advance is no fact.
        world.Module.ReactorWaveAdvanced("main", 3, 3);
        world.Module.ReactorWaveAdvanced("main", 2, 3);
        Assert.Null(world.Last(capability));

        world.Module.ReactorWaveAdvanced("main", 1, 0);
        world.Module.ReactorWaveAdvanced("main", 2, 1);
        Assert.Equal(2, world.Count(capability));
        var second = world.Last(capability)!;
        Assert.Equal(2, second.Outputs.GetProperty("wave").GetInt32());
        Assert.Equal(1, second.Outputs.GetProperty("previous").GetInt32());

        // The same step twice in one world is one fact; the event id carries the transition number.
        world.Module.ReactorWaveAdvanced("main", 2, 1);
        Assert.Equal(2, world.Count(capability));
    }

    [Fact]
    public void TheHsuRowCarriesItsContainerOnlyWhenOneWasRead()
    {
        using var world = LevelEventWorld.Start();
        string capability = LevelEventWorld.Capability(LevelEventContract.HsuSampledFact);

        world.Module.HsuSampled("main", null);
        var without = world.Last(capability)!;
        Assert.Equal("layer:main", without.Outputs.GetProperty("objective").GetProperty("resourceId").GetString());
        Assert.False(without.Outputs.TryGetProperty("container", out _));

        world.Module.HsuSampled("secondary", "hsu/17");
        var with = world.Last(capability)!;
        Assert.Equal("hsu/17", with.Outputs.GetProperty("container").GetProperty("resourceId").GetString());
        Assert.Equal("item", with.Outputs.GetProperty("container").GetProperty("resourceKind").GetString());
    }

    [Fact]
    public void CheckpointRestoreIsOneFactPerWorld()
    {
        using var world = LevelEventWorld.Start();
        string capability = LevelEventWorld.Capability(LevelEventContract.CheckpointRestoredFact);

        world.Module.CheckpointRestored();
        world.Module.CheckpointRestored();
        Assert.Equal(1, world.Count(capability));
        Assert.Equal(LevelEventContract.Binding(capability), world.Last(capability)!.BindingId);
    }

    [Fact]
    public void ZoneEntryIsPerPlayerAndPerZone()
    {
        using var world = LevelEventWorld.Start();
        string capability = LevelEventWorld.Capability(LevelEventContract.ZoneEnteredFact);
        var player = new EntityReference("gtfo.player:1", world.Kernel.WorldEpoch, 1);
        var other = new EntityReference("gtfo.player:2", world.Kernel.WorldEpoch, 1);

        world.Module.ZoneEntered(player, null);
        Assert.Null(world.Last(capability));

        world.Module.ZoneEntered(player, "dimension:0/layer:main/zone:3");
        Assert.Equal(1, world.Count(capability));
        world.Module.ZoneEntered(player, "dimension:0/layer:main/zone:3");
        Assert.Equal(1, world.Count(capability));

        world.Module.ZoneEntered(player, "dimension:0/layer:main/zone:4");
        world.Module.ZoneEntered(other, "dimension:0/layer:main/zone:3");
        Assert.Equal(3, world.Count(capability));
        var last = world.Last(capability)!;
        Assert.Equal("gtfo.player:2", last.Outputs.GetProperty("player").GetProperty("id").GetString());
        Assert.Equal("dimension:0/layer:main/zone:3", last.Outputs.GetProperty("zone").GetString());

        // A reference of a world this session no longer holds publishes nothing rather than a stale fact.
        world.Module.ZoneEntered(new EntityReference("gtfo.player:1", world.Kernel.WorldEpoch + 1, 1), "dimension:0/layer:main/zone:9");
        Assert.Equal(3, world.Count(capability));
    }

    [Fact]
    public void PortalWarpIsPerPortalAndPerDestination()
    {
        using var world = LevelEventWorld.Start();
        string capability = LevelEventWorld.Capability(LevelEventContract.PortalWarpedFact);

        world.Module.PortalWarped("", 1, 0);
        Assert.Null(world.Last(capability));

        world.Module.PortalWarped("portal/9", 1, 0);
        Assert.Equal(1, world.Count(capability));
        world.Module.PortalWarped("portal/9", 1, 0);
        Assert.Equal(1, world.Count(capability));
        world.Module.PortalWarped("portal/9", 2, 1);
        Assert.Equal(2, world.Count(capability));
        var published = world.Last(capability)!;
        Assert.Equal("portal/9", published.Outputs.GetProperty("portal").GetProperty("id").GetString());
        Assert.Equal(2, published.Outputs.GetProperty("dimension").GetInt32());
        Assert.Equal(1, published.Outputs.GetProperty("previous").GetInt32());
    }

    // ---- the actions --------------------------------------------------------------------------------

    [Fact]
    public void TheCountdownActionAddsOnlyWithSecondsAndResetsWithout()
    {
        using var world = LevelEventWorld.Start();
        var added = new List<float>();
        int resets = 0;

        var missing = world.Module.ExecuteTimer(world.Context(new { }, new { operation = "add" }), added.Add, () => resets++);
        Assert.Equal(LevelEventModule.SecondsInvalidCode, missing.Code);
        Assert.Empty(added);

        var zero = world.Module.ExecuteTimer(world.Context(new { seconds = 0 }, new { operation = "add" }), added.Add, () => resets++);
        Assert.Equal(LevelEventModule.SecondsInvalidCode, zero.Code);

        var unknown = world.Module.ExecuteTimer(world.Context(new { seconds = 30 }, new { operation = "poke" }), added.Add, () => resets++);
        Assert.Equal(LevelEventModule.OperationUnknownCode, unknown.Code);

        var add = world.Module.ExecuteTimer(world.Context(new { seconds = 30 }, new { operation = "add" }), added.Add, () => resets++);
        Assert.Equal(CommandStatuses.Succeeded, add.Status);
        Assert.Equal(CommitStates.Confirmed, add.CommitState);
        Assert.Equal(new[] { 30f }, added);
        Assert.Equal(1, Row(add).GetProperty("target_count").GetInt32());

        // `reset` carries no seconds: the countdown goes back to the value the objective's data block set.
        var reset = world.Module.ExecuteTimer(world.Context(new { }, new { operation = "reset" }), added.Add, () => resets++);
        Assert.Equal(CommandStatuses.Succeeded, reset.Status);
        Assert.Equal(1, resets);
        Assert.Equal(1, added.Count);
    }

    [Fact]
    public void TheCountdownActionRefusesAClient()
    {
        using var world = LevelEventWorld.Start();
        int calls = 0;
        var refused = world.Module.ExecuteTimer(
            world.Context(new { seconds = 10 }, new { operation = "add" }, isHost: false), _ => calls++, () => calls++);
        Assert.Equal(CommandStatuses.Rejected, refused.Status);
        Assert.Equal(LevelEventModule.AuthorityCode, refused.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TheCountdownActionReportsAFailedCommit()
    {
        using var world = LevelEventWorld.Start();
        var failed = world.Module.ExecuteTimer(world.Context(new { seconds = 5 }, new { operation = "add" }),
            _ => throw new InvalidOperationException("native"), () => { });
        Assert.Equal(CommandStatuses.Failed, failed.Status);
        Assert.Equal(CommitStates.Unknown, failed.CommitState);
        Assert.Equal(LevelEventModule.CommitExceptionCode, failed.Code);
    }

    [Fact]
    public void TheDimensionActionPassesTheModeAndTheIndex()
    {
        using var world = LevelEventWorld.Start();
        var moves = new List<(string Mode, int Dimension)>();

        // The dimension command's two shapes: these cases carry no locations table, so the per-recipient

        // body is the other shape and must not be reached.

        Func<CommandResult> unreachable = () => throw new InvalidOperationException(

            "fixture: this case carries no locations table, so the per-recipient shape is not reached");

        var unknown = world.Module.ExecuteDimension(world.Context(new { dimension = 1 }, new { mode = "hop" }),
            (mode, dimension) => moves.Add((mode, dimension)), unreachable);
        Assert.Equal(LevelEventModule.ModeUnknownCode, unknown.Code);

        var missingIndex = world.Module.ExecuteDimension(world.Context(new { }, new { mode = "warp" }),
            (mode, dimension) => moves.Add((mode, dimension)), unreachable);
        Assert.Equal(LevelEventModule.DimensionInvalidCode, missingIndex.Code);

        var flash = world.Module.ExecuteDimension(world.Context(new { dimension = 2 }, new { mode = "flash" }),
            (mode, dimension) => moves.Add((mode, dimension)), unreachable);
        Assert.Equal(CommandStatuses.Succeeded, flash.Status);

        // `clear` empties a dimension and names no destination: the event type carries that, which is why the
        // request has no clear flag of its own.
        var clear = world.Module.ExecuteDimension(world.Context(new { }, new { mode = "clear" }),
            (mode, dimension) => moves.Add((mode, dimension)), unreachable);
        Assert.Equal(CommandStatuses.Succeeded, clear.Status);

        Assert.Equal(new[] { ("flash", 2), ("clear", 0) }, moves.ToArray());
    }

    [Fact]
    public void TheDimensionDestinationTableSelectsThePerRecipientShape()
    {
        using var world = LevelEventWorld.Start();
        var moved = 0;

        // The table is the row's two collections, zipped by index. A request that carries them is the
        // per-recipient warp, so the team event must not be reached.
        var table = world.Module.ExecuteDimension(
            world.Context(new
            {
                positions = new[] { new[] { 1.0, 2.0, 3.0 } },
                look_dirs = new[] { new[] { 0.0, 0.0, 1.0 } }
            }, new { mode = "warp" }),
            (mode, dimension) => throw new InvalidOperationException("fixture: the team event must not run"),
            () => { moved++; return CommandResult.Succeeded(RuntimeJson.EmptyObject); });
        Assert.Equal(CommandStatuses.Succeeded, table.Status);
        Assert.Equal(1, moved);

        // The table is warp-only, and a request that names recipients without one is the author error the ruling
        // names: the team events move everyone and take no subset.
        var wrongMode = world.Module.ExecuteDimension(
            world.Context(new { positions = new[] { new[] { 1.0, 2.0, 3.0 } } }, new { mode = "flash" }),
            (mode, dimension) => { }, () => CommandResult.Succeeded(RuntimeJson.EmptyObject));
        Assert.Equal(LevelEventModule.ModeUnknownCode, wrongMode.Code);

        var noTable = world.Module.ExecuteDimension(
            world.Context(new { players = new[] { "gtfo.player:1" } }, new { mode = "flash" }),
            (mode, dimension) => { }, () => CommandResult.Succeeded(RuntimeJson.EmptyObject));
        Assert.Equal(LevelEventModule.PlayersNeedLocationsCode, noTable.Code);
    }

    [Fact]
    public void TheDimensionRowDeclaresTheTableAsCollectionsAndNoFollowPolicyOrClearFlag()
    {
        // The row's ports are the ones the handler resolves against, and the deleted `policy` and `clear`
        // parameters are gone: an author cannot pin a follow choice this build has no carry for, and the event
        // type already carries the clear.
        var row = LevelEventContract.ActionRows()
            .Select(candidate => RuntimeJson.From(candidate))
            .Single(candidate => RuntimeJson.Text(candidate, "id") == LevelEventContract.DimensionCapability);
        var graph = row.GetProperty("graph");
        Assert.Equal(new[] { "in", "players", "dimension", "positions", "look_dirs" },
            graph.GetProperty("inputs").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray());
        var positions = graph.GetProperty("inputs").EnumerateArray()
            .Single(port => port.GetProperty("id").GetString() == "positions");
        Assert.Equal("vector3", positions.GetProperty("type").GetString());
        Assert.Equal("many", positions.GetProperty("cardinality").GetString());
        Assert.Equal("m", positions.GetProperty("unit").GetString());
        var lookDirs = graph.GetProperty("inputs").EnumerateArray()
            .Single(port => port.GetProperty("id").GetString() == "look_dirs");
        Assert.False(lookDirs.TryGetProperty("unit", out _),
            "A facing direction is not a length, so `look_dirs` carries no unit.");
        Assert.Equal(new[] { "mode" },
            graph.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        Assert.Equal(new[] { "players", "dimension", "positions", "look_dirs" },
            LevelEventContract.DimensionShape.InputPorts);
    }

    [Fact]
    public void TheExpeditionEndActionAcceptsTheTwoNativeEndings()
    {
        using var world = LevelEventWorld.Start();
        var endings = new List<string>();

        var unknown = world.Module.ExecuteExpeditionEnd(world.Context(null, new { ending = "draw" }), endings.Add);
        Assert.Equal(LevelEventModule.EndingUnknownCode, unknown.Code);

        var instant = world.Module.ExecuteExpeditionEnd(world.Context(null, new { ending = "instant_win" }), endings.Add);
        var death = world.Module.ExecuteExpeditionEnd(world.Context(null, new { ending = "win_on_death" }), endings.Add);
        Assert.Equal(CommandStatuses.Succeeded, instant.Status);
        Assert.Equal(CommandStatuses.Succeeded, death.Status);
        Assert.Equal(new[] { "instant_win", "win_on_death" }, endings.ToArray());
    }

    // ---- world and lifetime -------------------------------------------------------------------------

    [Fact]
    public void ANewWorldPublishesTheSameFactAgainUnderItsOwnEpoch()
    {
        using var world = LevelEventWorld.Start();
        string capability = LevelEventWorld.Capability(LevelEventContract.CheckpointRestoredFact);

        world.Module.CheckpointRestored();
        Assert.Equal(1, world.Count(capability));
        world.Module.CheckpointRestored();
        Assert.Equal(1, world.Count(capability));

        // The world the kernel moves to is a new world: the same transition is its own fact and its id carries
        // the new epoch, which is what makes the kernel's replay ledger per world.
        world.Kernel.BeginWorld(2);
        world.Kernel.StartRuntime(static () => { });
        world.Kernel.Advance(0, true);
        world.Module.CheckpointRestored();
        Assert.Equal(2, world.Count(capability));
        Assert.Contains(":2:", world.Last(capability)!.EventId);
    }

    [Fact]
    public void ClearWorldDropsTheTablesSoTheNextWorldReadsItsOwn()
    {
        using var world = LevelEventWorld.Start();
        string capability = LevelEventWorld.Capability(LevelEventContract.CheckpointRestoredFact);

        world.Module.CheckpointRestored();
        world.Module.ClearWorld();
        world.Module.CheckpointRestored();
        Assert.Equal(2, world.Count(capability));
        Assert.Equal(LevelEventContract.Binding(capability), world.Last(capability)!.BindingId);
    }

    [Fact]
    public void ADisposedModulePublishesNothing()
    {
        var world = LevelEventWorld.Start();
        var module = world.Module;
        world.Dispose();
        module.ExpeditionStarted("31:A:0");
        module.CheckpointRestored();
        Assert.Equal(0, world.Published.Count);
    }

    /// <summary>The one row of a command result, which is what every handler of this family answers with.</summary>
    private static JsonElement Row(CommandResult result) => result.Outputs.GetProperty("results")[0];
}
