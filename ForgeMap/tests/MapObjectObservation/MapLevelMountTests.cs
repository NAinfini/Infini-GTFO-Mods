using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.MapObjects;

/// <summary>
/// The `level` mount: the one kind whose matcher is judged from the mount target alone. A plan mounted on one
/// level must be dispatched in that level and in no other, and a reference the vocabulary cannot even spell
/// must never be read as "every level" — the two failure modes that made the mount meaningless before the
/// identity existed. The reader is a stub here, so what is under test is the comparison and its refusals; the
/// game-bound reader that feeds the same comparison is the native adapter suite's subject.
/// </summary>
public sealed class MapLevelMountTests
{
    private const string DoorState = "forge.trigger.interaction.door_state";
    private static readonly MapLevelReference ThisLevel = MapLevelReference.TryParse("31:A:0")!.Value;

    /// <summary>One published door fact, and whether a plan claimed it: the kernel answers `queued` only when
    /// some plan's mount targets matched.</summary>
    private static bool Claimed(MapObjectFixture fixture, StubDoor door)
    {
        var before = fixture.Module.PublishedFacts;
        fixture.Module.DoorStateChanged(door);
        return fixture.Module.PublishedFacts > before;
    }

    [Fact]
    public void APlanMountedOnThisLevelIsClaimed()
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        var door = fixture.Track(new StubDoor(status: 1));
        Assert.True(fixture.LoadLevel("level-here", DoorState, "31:A:0").Loaded);
        Assert.True(Claimed(fixture, door));
        Assert.DoesNotContain(fixture.Reported, message => message.Contains("level", StringComparison.Ordinal));
    }

    /// <summary>Every other identity is a different level: another index in the same tier, another tier of the
    /// same rundown block, and the same tier and index of another rundown block.</summary>
    [Theory]
    [InlineData("31:A:1")]
    [InlineData("31:B:0")]
    [InlineData("35:A:0")]
    public void APlanMountedOnAnotherLevelIsNeverClaimed(string reference)
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        var door = fixture.Track(new StubDoor(status: 1));
        Assert.True(fixture.LoadLevel("level-elsewhere", DoorState, reference).Loaded);
        Assert.False(Claimed(fixture, door));
        // The reference is legal and simply not this level, which is silence rather than a diagnostic.
        Assert.DoesNotContain(fixture.Reported, message => message.Contains("level attachment", StringComparison.Ordinal));
    }

    /// <summary>A reference outside the grammar names no level. It is refused and reported once, however many
    /// times the mount is asked, and never turns into a match. An empty reference is not one of these cases: the
    /// plan loader refuses an empty attachment reference before any matcher sees it.</summary>
    [Theory]
    [InlineData("expedition:7f3a")]
    [InlineData("31:a:0")]
    [InlineData("31:A:01")]
    [InlineData("31:A:-1")]
    [InlineData("31:A")]
    [InlineData("31:A:0:9")]
    [InlineData("0031:A:0")]
    [InlineData("31:F:0")]
    [InlineData("31:A:4294967296")]
    public void AnUnspellableReferenceIsRefusedOnceAndNeverMatches(string reference)
    {
        using var fixture = new MapObjectFixture(currentLevel: () => ThisLevel);
        var door = fixture.Track(new StubDoor(status: 1));
        Assert.True(fixture.LoadLevel("level-unspellable", DoorState, reference).Loaded);
        Assert.False(Claimed(fixture, door));
        door.Status = 16;
        Assert.False(Claimed(fixture, door));
        Assert.Equal(1, fixture.Reported.Count(message =>
            message.Contains("is not `<rundown block id>:<tier A-E>:<tier index>`", StringComparison.Ordinal)));
    }

    /// <summary>A world whose level identity cannot be read has no level any mount can equal. That is reported
    /// once too, and it is not the same answer as "the reference is illegal".</summary>
    [Fact]
    public void AnUnreadableLevelIdentityMatchesNothing()
    {
        using var fixture = new MapObjectFixture(currentLevel: () => null);
        var door = fixture.Track(new StubDoor(status: 1));
        Assert.True(fixture.LoadLevel("level-unreadable", DoorState, "31:A:0").Loaded);
        Assert.False(Claimed(fixture, door));
        door.Status = 16;
        Assert.False(Claimed(fixture, door));
        Assert.Contains(fixture.Reported, message => message.Contains("reports no readable expedition", StringComparison.Ordinal));
    }

    /// <summary>The reader is asked once per world: a level identity is a property of the world, and a mount
    /// comparison on every published fact must not become a game read on every fact.</summary>
    [Fact]
    public void TheLevelIdentityIsReadOncePerWorld()
    {
        var reads = 0;
        using var fixture = new MapObjectFixture(currentLevel: () => { reads++; return ThisLevel; });
        var door = fixture.Track(new StubDoor(status: 1));
        fixture.LoadLevel("level-once", DoorState, "31:A:0");
        for (var status = 1; status <= 3; status++) { door.Status = status; Claimed(fixture, door); }
        Assert.Equal(1, reads);

        // A new world is a new level, so the identity is read again there.
        fixture.ForgeWorld(2);
        fixture.LoadLevel("level-next-world", DoorState, "31:A:0");
        door.Status = 4;
        Claimed(fixture, door);
        Assert.Equal(2, reads);
    }
}
