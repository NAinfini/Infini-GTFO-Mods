using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.MapObjects;

/// <summary>
/// The session trigger `forge.trigger.session.expedition_ended`: the one row this provider publishes about the
/// expedition itself. It carries no subject, so a plan is claimed by its mount alone — the `level` matcher, which
/// is the same path the per-map-object facts do not use. What is asserted here is the event's own identity (one
/// world, one end state, one event), the outcome the game's end state maps to, and the two gates that keep a
/// client and an unreadable end state out of the queue.
/// </summary>
public sealed class ExpeditionTriggerTests
{
    private const string Ended = ExpeditionContract.EndedCapability;
    private static readonly MapLevelReference ThisLevel = MapLevelReference.TryParse("31:A:0")!.Value;

    [Fact]
    public void AnEndOfThisLevelsExpeditionIsClaimed()
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        Assert.True(fixture.LoadLevel("expedition-here", Ended, "31:A:0").Loaded);
        var result = fixture.Module.Expeditions.Ended(ExpeditionContract.Success);
        Assert.NotNull(result);
        Assert.Equal("queued", result!.Status);
        Assert.Equal(1, fixture.Module.Expeditions.PublishedFacts);
    }

    /// <summary>A plan mounted on another level of the same package is a plan for that level: the event is
    /// dropped before it is queued, and the answer says which rule dropped it.</summary>
    [Theory]
    [InlineData("31:A:1")]
    [InlineData("31:B:0")]
    [InlineData("35:A:0")]
    public void AnEndOfAnotherLevelsExpeditionIsNeverClaimed(string reference)
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        Assert.True(fixture.LoadLevel("expedition-elsewhere", Ended, reference).Loaded);
        var result = fixture.Module.Expeditions.Ended(ExpeditionContract.Success);
        Assert.NotNull(result);
        Assert.Equal("ignored", result!.Status);
        Assert.Equal("attachment-mismatch", result.Code);
        Assert.Equal(0, fixture.Module.Expeditions.PublishedFacts);
        // The reference is legal for another level, which is silence rather than a diagnostic.
        Assert.DoesNotContain(fixture.Reported, message => message.Contains("level attachment", StringComparison.Ordinal));
    }

    /// <summary>A world whose level cannot be read claims nothing: the mount is answered false, not "every
    /// level", and the one bounded diagnostic says which of the two answers this was.</summary>
    [Fact]
    public void AnUnreadableLevelClaimsNothing()
    {
        using var fixture = new MapObjectFixture(currentLevel: () => null);
        Assert.True(fixture.LoadLevel("expedition-unreadable", Ended, "31:A:0").Loaded);
        var result = fixture.Module.Expeditions.Ended(ExpeditionContract.Success);
        Assert.Equal("ignored", result!.Status);
        Assert.Contains(fixture.Reported, message => message.Contains("reports no readable expedition", StringComparison.Ordinal));
    }

    /// <summary>One end reported twice is one event: the second answer is the kernel's own `duplicate`, which is
    /// also what keeps a native callback the game repeats from running a plan's steps twice.</summary>
    [Fact]
    public void ARepeatedEndIsOneEvent()
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        Assert.True(fixture.LoadLevel("expedition-repeat", Ended, "31:A:0").Loaded);
        Assert.Equal("queued", fixture.Module.Expeditions.Ended(ExpeditionContract.Success)!.Status);
        var again = fixture.Module.Expeditions.Ended(ExpeditionContract.Success);
        Assert.Equal("duplicate", again!.Status);
        Assert.Equal(1, fixture.Module.Expeditions.PublishedFacts);
    }

    /// <summary>The event id names the end state as well as the world, so a world whose expedition ends twice
    /// with different answers carries both facts instead of the second being refused as an id conflict. Which of
    /// the two a game reports for one expedition is an in-game check; the identity rule is what is frozen here.
    /// </summary>
    [Fact]
    public void EachEndStateOfOneWorldIsItsOwnEvent()
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        Assert.True(fixture.LoadLevel("expedition-two-ends", Ended, "31:A:0").Loaded);
        Assert.Equal("queued", fixture.Module.Expeditions.Ended(ExpeditionContract.Success)!.Status);
        Assert.Equal("queued", fixture.Module.Expeditions.Ended(ExpeditionContract.Abort)!.Status);
        Assert.Equal(2, fixture.Module.Expeditions.PublishedFacts);
    }

    /// <summary>The same end in the next world is a new event: the world epoch is part of the id, and the
    /// kernel's replay ledger that would answer `duplicate` is per world.</summary>
    [Fact]
    public void TheSameEndInTheNextWorldIsANewEvent()
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        Assert.True(fixture.LoadLevel("expedition-world-two", Ended, "31:A:0").Loaded);
        Assert.Equal("queued", fixture.Module.Expeditions.Ended(ExpeditionContract.Fail)!.Status);
        fixture.ForgeWorld(2);
        Assert.Equal("queued", fixture.Module.Expeditions.Ended(ExpeditionContract.Fail)!.Status);
        Assert.Equal(2, fixture.Module.Expeditions.PublishedFacts);
    }

    /// <summary>The three end states are the catalog's three outcomes: clearing is the one success, a wipe the
    /// one failure, and leaving a cancellation. An end state outside the set is refused instead of being folded
    /// into one of the three.</summary>
    [Theory]
    [InlineData(ExpeditionContract.Success, 0)]
    [InlineData(ExpeditionContract.Fail, 3)]
    [InlineData(ExpeditionContract.Abort, 4)]
    public void EveryEndStateCarriesItsCatalogOutcome(int endState, int outcome)
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        Assert.True(fixture.LoadLevel("expedition-outcome", Ended, "31:A:0").Loaded);
        Assert.Equal(outcome, ExpeditionContract.Outcome(endState));
        Assert.Equal("queued", fixture.Module.Expeditions.Ended(endState)!.Status);
    }

    [Fact]
    public void AnUnknownEndStatePublishesNothing()
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        Assert.True(fixture.LoadLevel("expedition-unknown", Ended, "31:A:0").Loaded);
        Assert.Null(fixture.Module.Expeditions.Ended(9));
        Assert.Null(fixture.Module.Expeditions.Ended(9));
        Assert.Equal(0, fixture.Module.Expeditions.PublishedFacts);
        Assert.Equal(1, fixture.Reported.Count(message => message.Contains("names no execution outcome", StringComparison.Ordinal)));
    }

    /// <summary>Only the host publishes. A client that ran the same native callback answers without reaching the
    /// kernel, and says so once.</summary>
    [Fact]
    public void AClientDoesNotPublish()
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel) { Authority = false };
        Assert.True(fixture.LoadLevel("expedition-client", Ended, "31:A:0").Loaded);
        Assert.Null(fixture.Module.Expeditions.Ended(ExpeditionContract.Success));
        Assert.Equal(0, fixture.Module.Expeditions.PublishedFacts);
        Assert.Equal(1, fixture.Reported.Count(message => message.Contains("non-authoritative peer", StringComparison.Ordinal)));
    }

    /// <summary>The catalog shape of this row, declared once by the runtime's trigger contract: `next` plus the
    /// outcome enum, no resource port, and no input. The row is what the website compiles against, so a port
    /// added or lost there changes every plan; this provider only binds the id.</summary>
    [Fact]
    public void TheDeclaredShapeIsTheCatalogRow()
    {
        using var manifest = System.Text.Json.JsonDocument.Parse(TriggerContracts.Module().RegistryJson);
        var capability = manifest.RootElement.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == Ended);
        Assert.Equal("trigger", capability.GetProperty("kind").GetString());
        var graph = capability.GetProperty("graph");
        Assert.Equal("host", graph.GetProperty("execution").GetString());
        Assert.Empty(graph.GetProperty("inputs").EnumerateArray());
        Assert.Empty(graph.GetProperty("parameters").EnumerateArray());
        Assert.Equal(new[] { "map", "session", "logic" },
            graph.GetProperty("domains").EnumerateArray().Select(d => d.GetString()).ToArray());
        var outputs = graph.GetProperty("outputs").EnumerateArray().ToArray();
        Assert.Equal(new[] { "next", "outcome" }, outputs.Select(p => p.GetProperty("id").GetString()).ToArray());
        Assert.Equal("execution", outputs[0].GetProperty("type").GetString());
        Assert.Equal("enum", outputs[1].GetProperty("type").GetString());
        Assert.Equal("execution_outcome", outputs[1].GetProperty("schema").GetString());
    }
}
