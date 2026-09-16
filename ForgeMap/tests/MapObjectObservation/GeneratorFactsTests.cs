using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.MapObjects;

/// <summary>
/// The generator category of the map-object provider (ruling 148.4): a power generator is one more kind of
/// object the level places, so it is addressed in the one map-object grammar, published through the one
/// `gtfo.map_object` namespace and read back through the one resource table. These are the cases the
/// level-object suite carried while the generators were half of `LevelObjectModule`; they moved here with the
/// category, and the module's own rules — the mount, the authority gate, the repeated reading and the world
/// change — are asserted against the same one publisher the doors and terminals go through.
/// </summary>
public sealed class GeneratorFactsTests
{
    private const string GeneratorCell = "forge.trigger.map.generator_cell";
    private const string GeneratorCluster = "forge.trigger.map.generator_cluster";
    private static readonly string Category = MapObjectGeneratorAddress.Category;

    /// <summary>One plan mounted on one generator address, so the kernel claims the fact this case publishes:
    /// a fact no plan claims is `no-consumer` and never reaches the module's own count.</summary>
    private static void Mount(MapObjectFixture fixture, string planId, string capability, MapObjectReference address)
        => Assert.True(fixture.Load(planId, capability, Category, address.ToString()).Loaded);

    // ---- address ------------------------------------------------------------------------------------

    [Fact]
    public void AGeneratorAddressIsTheOneMapObjectGrammar()
    {
        // Ruling 130.1: the generator's id is `<category>/<dimension>/<layer>/<zone>/<serial>` — the same five
        // segments a door and a terminal are addressed by, so the website can offer it as a host.
        var address = MapObjectGeneratorAddress.TryParse("generator/0/0/1/7");

        Assert.NotNull(address);
        Assert.Equal(Category, address!.Category);
        Assert.Equal("0", address.Dimension);
        Assert.Equal("0", address.Layer);
        Assert.Equal("1", address.Zone);
        Assert.Equal("7", address.Key);
        Assert.Equal("generator/0/0/1/7", address.ToString());
        // Another category, another segment count and a coordinate with a leading zero are not this category's
        // address: the key grammar is the category's own and nothing else is reinterpreted.
        Assert.Null(MapObjectGeneratorAddress.TryParse("cluster/0/0/1/7"));
        Assert.Null(MapObjectGeneratorAddress.TryParse("generator/0/0/1"));
        Assert.Null(MapObjectGeneratorAddress.TryParse("generator/0/0/01/7"));
        Assert.Null(MapObjectGeneratorAddress.TryParse("generator/0/0/1/07"));
        Assert.Null(MapObjectGeneratorAddress.TryParse(""));
    }

    [Fact]
    public void AGeneratorGroupIsTheSameCategorysSecondKeyForm()
    {
        // A group is not a second category: it is the same category's `group<serial>` key, exactly as a weak
        // door is the door category's second key form. A group and a generator of one serial therefore stay two
        // addresses.
        var group = MapObjectGeneratorAddress.TryParse("generator/0/2/4/group1");

        Assert.NotNull(group);
        Assert.Equal(Category, group!.Category);
        Assert.True(MapObjectGeneratorAddress.IsGroupKey(group.Key));
        Assert.Equal(1, MapObjectGeneratorAddress.ParseGroupSerial(group.Key));
        Assert.Equal("generator/0/2/4/group1", group.ToString());
        Assert.False(MapObjectGeneratorAddress.IsGroupKey("7"));
        Assert.Null(MapObjectGeneratorAddress.ParseGroupSerial("group01"));
        Assert.Null(MapObjectGeneratorAddress.TryParse("generator/0/2/4/group01"));

        var single = MapObjectGeneratorAddress.Create(0, 2, 4, 1);
        Assert.NotNull(single);
        Assert.NotEqual(single!.ToString(), group.ToString());
        Assert.Null(MapObjectGeneratorAddress.Create(0, 2, 4, -1));
        Assert.Null(MapObjectGeneratorAddress.CreateGroup(0, 2, null, 1));
    }

    [Fact]
    public void AGeneratorTheInstanceCannotAddressPublishesNothing()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator { Zone = null });
        Assert.Null(generator.Address());

        // A generator outside any zone this level knows carries none of the address's keys, so there is no
        // address to publish under and no guessed one.
        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);

        Assert.Equal(0, fixture.Module.PublishedFacts);
        Assert.Empty(fixture.Module.EnumerateGenerators());
    }

    // ---- publication --------------------------------------------------------------------------------

    [Fact]
    public void ACellChangePublishesTheRowsOwnFactOncePerState()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true, connected: 1, total: 3));
        Mount(fixture, "generator-cell", GeneratorCell, generator.Address()!);

        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        // The generator's own sync callback fires again while it still reads powered: the state key is the
        // reading, so the second callback is not a second event.
        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        generator.Powered = false;
        fixture.Module.GeneratorCellChanged(generator, null, null);
        Assert.Equal(2, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void ACellChangeNeverPublishesTheGroupRow()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true, connected: 3, total: 3));
        // Only the group row is subscribed: the cell callback reports a cell, and the group row is the group's
        // own callback's fact, so a completed group is not published out of the cell change.
        Mount(fixture, "generator-group-only", GeneratorCluster, generator.Address()!);

        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);

        Assert.Equal(0, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void AGroupWhoseEveryMemberIsPoweredPublishesTheGroupRow()
    {
        using var fixture = new MapObjectFixture();
        var group = fixture.Track(new StubGeneratorGroup(serial: 1, connected: 3, total: 3));
        Mount(fixture, "generator-group", GeneratorCluster, group.Address()!);

        fixture.Module.GeneratorGroupConnected(group);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        fixture.Module.GeneratorGroupConnected(group);
        Assert.Equal(1, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void AGroupWithNoMembersNeverPublishesItselfConnected()
    {
        using var fixture = new MapObjectFixture();
        var group = fixture.Track(new StubGeneratorGroup(serial: 1, connected: 0, total: 0));
        Mount(fixture, "generator-empty-group", GeneratorCluster, group.Address()!);

        fixture.Module.GeneratorGroupConnected(group);

        // The module re-checks the counts rather than trusting the callback: an empty group is not a group whose
        // every member is powered.
        Assert.Equal(0, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void APartlyPoweredGroupNeverPublishesItselfConnected()
    {
        using var fixture = new MapObjectFixture();
        var group = fixture.Track(new StubGeneratorGroup(serial: 1, connected: 2, total: 3));
        Mount(fixture, "generator-partly-powered", GeneratorCluster, group.Address()!);

        fixture.Module.GeneratorGroupConnected(group);

        Assert.Equal(0, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void ANonAuthoritativePeerPublishesNoGeneratorFact()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true));
        Mount(fixture, "generator-client", GeneratorCell, generator.Address()!);

        fixture.Authority = false;
        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);

        Assert.Equal(0, fixture.Module.PublishedFacts);
        Assert.Contains(fixture.Reported, message => message.Contains("non-authoritative", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherCategorysInstanceIsNeverReadAsAGenerator()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true));
        var terminal = fixture.Track(new StubTerminal());
        Mount(fixture, "generator-wrong-instance", GeneratorCell, generator.Address()!);

        // The generator source answers only for its own native types, exactly as the door and terminal sources
        // do: a terminal reported through the generator callback addresses nothing.
        fixture.Module.GeneratorCellChanged(terminal, "00ff0011", null);
        Assert.Equal(0, fixture.Module.PublishedFacts);

        // And the other way round: a generator reported through the door callback is not a door.
        fixture.Module.DoorStateChanged(generator);
        Assert.Equal(0, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void AGeneratorThatNoLongerReadsPublishesNothingAndSaysSo()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true));
        Mount(fixture, "generator-replaced", GeneratorCell, generator.Address()!);

        // The instance under the address no longer reads: the module refuses the report instead of publishing a
        // state it could not read.
        generator.Broken = "unreadable";
        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);

        Assert.Equal(0, fixture.Module.PublishedFacts);
        Assert.Contains(fixture.Generators.Reports,
            message => message.Contains("no longer reads as it was addressed", StringComparison.Ordinal));
    }

    // ---- resource table and value row ---------------------------------------------------------------

    [Fact]
    public void AGeneratorTheFactsNamedIsAResourceTheValueRowCanReadBack()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true, connected: 1, total: 3));
        var address = generator.Address()!.ToString();
        Mount(fixture, "generator-resource", GeneratorCell, generator.Address()!);
        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);

        var generators = fixture.Module.EnumerateGenerators();

        Assert.Equal(new[] { address }, generators.Select(reference => reference.ResourceId));
        Assert.NotNull(fixture.Module.ResolveGenerator(address));
        Assert.Null(fixture.Module.ResolveGenerator("generator/0/2/4/9"));
        Assert.Null(fixture.Module.ResolveGenerator(""));
    }

    [Fact]
    public void AGroupAddressIsAFactSubjectAndNeverAResource()
    {
        using var fixture = new MapObjectFixture();
        var group = fixture.Track(new StubGeneratorGroup(serial: 1, connected: 1, total: 1));
        var address = group.Address()!.ToString();
        Mount(fixture, "generator-group-resource", GeneratorCluster, group.Address()!);
        fixture.Module.GeneratorGroupConnected(group);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        // A group is a fact subject: the resource table answers for generators, and the `cluster` port's own key
        // form resolves nothing rather than naming a resource the game has no instance of.
        Assert.Empty(fixture.Module.EnumerateGenerators());
        Assert.Null(fixture.Module.ResolveGenerator(address));
    }

    [Fact]
    public void AGeneratorTheWorldNeverReportedIsRefusedByName()
    {
        using var fixture = new MapObjectFixture();

        var error = Assert.Throws<RuntimeContractException>(() =>
            fixture.Module.ReadGeneratorState(GeneratorInput("generator/0/2/4/7")));

        Assert.Equal("generator-not-found", error.Code);
    }

    [Fact]
    public void AGeneratorReadingTheNativeHalfCannotAnswerIsRefusedByName()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true, connected: 1, total: 1));
        var address = generator.Address()!.ToString();
        Mount(fixture, "generator-unavailable", GeneratorCell, generator.Address()!);
        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);

        var unreadable = Assert.Throws<RuntimeContractException>(() =>
            fixture.Module.ReadGeneratorState(GeneratorInput(address)));
        Assert.Equal("generator-unavailable", unreadable.Code);

        // A reader that answers nothing is the same refusal: a row that cannot be read must not look like a row
        // that read nothing.
        fixture.Module.GeneratorStateReader = key => null;
        var empty = Assert.Throws<RuntimeContractException>(() =>
            fixture.Module.ReadGeneratorState(GeneratorInput(address)));
        Assert.Equal("generator-unavailable", empty.Code);
    }

    [Fact]
    public void TheGeneratorValueRowAnswersTheGroupCountsTheReaderRead()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true, connected: 2, total: 3));
        var address = generator.Address()!.ToString();
        Mount(fixture, "generator-value", GeneratorCell, generator.Address()!);
        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);
        fixture.Module.GeneratorStateReader = key => RuntimeJson.From(new { value = true, connected = 2, total = 3 });

        var state = fixture.Module.ReadGeneratorState(GeneratorInput(address));

        Assert.True(state.GetProperty("value").GetBoolean());
        Assert.Equal(2, state.GetProperty("connected").GetInt32());
        Assert.Equal(3, state.GetProperty("total").GetInt32());
    }

    // ---- world and identity -------------------------------------------------------------------------

    [Fact]
    public void TheWorldChangeDropsEveryGeneratorTheLastWorldReported()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true, connected: 1, total: 1));
        var address = generator.Address()!.ToString();
        Mount(fixture, "generator-world", GeneratorCell, generator.Address()!);
        fixture.Module.GeneratorCellChanged(generator, "00ff0011", null);
        Assert.Single(fixture.Module.EnumerateGenerators());

        fixture.ForgeWorld(fixture.World + 1);

        // A new level reuses the same zone and serial for its own generator: the table is dropped with the world
        // it was read from, so the old address resolves nothing rather than a same-serial generator of the new
        // level.
        Assert.Empty(fixture.Module.EnumerateGenerators());
        Assert.Null(fixture.Module.ResolveGenerator(address));
    }

    [Fact]
    public void AGeneratorEntityIsObservedWithItsOwnPositionAndPoweredTag()
    {
        using var fixture = new MapObjectFixture();
        var generator = fixture.Track(new StubGenerator(serial: 7, powered: true, connected: 1, total: 3));

        var inspected = fixture.Kernel.InspectEntities(new[] { fixture.Reference(generator.Address()!) });
        Assert.Equal("entity-observed", inspected.Items[0].Code);
        Assert.Equal(MapObjectModule.EntityKind, inspected.Items[0].Snapshot!.Kind);
        Assert.Contains("map-object.category=" + Category, inspected.Items[0].Snapshot!.Tags);
        Assert.Contains("map-object.generator.state=powered", inspected.Items[0].Snapshot!.Tags);
        Assert.Equal(new double[] { 1, 2, 3 }, inspected.Items[0].Snapshot!.Position);
    }

    /// <summary>One evaluation context the way the kernel builds it for the value row: the row's own handler
    /// name and the resource input it declares. The context's constructor is internal to the SDK, so it is
    /// reached through its non-public signature, exactly as the level-object suite does.</summary>
    private static EvaluationContext GeneratorInput(string id)
    {
        var query = default(RuntimeQuerySession);
        var actors = new RuntimeActorContext(new Dictionary<string, EntityReference>(StringComparer.Ordinal));
        var relations = new RuntimeFactionRelations(Array.Empty<RuntimeFactionRelation>());
        return (EvaluationContext)Activator.CreateInstance(typeof(EvaluationContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object?[]
            {
                GeneratorContract.GeneratorStateHandler, RuntimeJson.EmptyObject,
                RuntimeJson.From(new Dictionary<string, object>
                {
                    ["generator"] = new { resourceKind = GeneratorContract.GeneratorResourceKind, resourceId = id }
                }),
                query, actors, relations
            }, null)!;
    }
}
