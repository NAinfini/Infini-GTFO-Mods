using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.MapObjects;

/// <summary>Publication rules: one fact per real state change, none for a repeated sync, none from a peer that
/// is not the host, and no fact at all for an instance that no longer reads as it was addressed.</summary>
public sealed class MapObjectPublicationTests
{
    private const string DoorState = "forge.trigger.interaction.door_state";
    private const string LockState = "forge.trigger.interaction.lock_state";
    private const string TerminalSession = "forge.trigger.interaction.terminal_session";
    private const string TerminalResult = "forge.trigger.interaction.terminal_result";
    private const string TerminalCommand = "forge.trigger.interaction.terminal_command";

    [Fact]
    public void OneTransitionPublishesOnceAndARepeatedSyncDoesNot()
    {
        using var fixture = new MapObjectFixture();
        var door = fixture.Track(new StubDoor(status: 1));
        Assert.True(fixture.Load("door-state", DoorState, MapObjectCategories.Door, door.Address()!.ToString()).Loaded);
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        // The door's own sync callback fires again while the status is unchanged: the state key is the state
        // the instance reports, so the second callback is not a second event.
        fixture.Module.DoorStateChanged(door);
        fixture.Module.DoorLockChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        door.Status = 16;
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(2, fixture.Module.PublishedFacts);

        door.Status = 10;
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(3, fixture.Module.PublishedFacts);

        fixture.Module.DoorStateChanged(door);
        Assert.Equal(3, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void ALockChangeUnderAnUnchangedStatusStillPublishes()
    {
        using var fixture = new MapObjectFixture();
        var door = fixture.Track(new StubDoor(status: 5));
        fixture.Load("lock-state", LockState, MapObjectCategories.Door, door.Address()!.ToString());
        fixture.Module.DoorLockChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        fixture.Module.DoorLockChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        // The door is unlocked but its status is still the one the lock held it in, as the native sync of a
        // separate lock member can report: the lock fact's own key changed, so it publishes.
        door.LockedWithNoKey = false;
        door.Status = 1;
        fixture.Module.DoorLockChanged(door);
        Assert.Equal(2, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void AReplacedInstanceNeverPublishesUnderTheOldAddress()
    {
        using var fixture = new MapObjectFixture();
        var door = fixture.Track(new StubDoor(status: 1));
        fixture.Load("replaced", DoorState, MapObjectCategories.Door, door.Address()!.ToString());
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        // The same address now reads a different instance, so the read disagrees with itself and nothing is
        // published: a level rebuild that reuses a zone's entrance must not extend the old door's event stream.
        door.Broken = "unreadable";
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);
        Assert.Contains(fixture.Doors.Reports, message => message.Contains("no longer reads as it was addressed", StringComparison.Ordinal));
    }

    [Fact]
    public void ADoorWhoseCoordinatesCannotBeReadIsNotAddressed()
    {
        using var fixture = new MapObjectFixture();
        var door = fixture.Track(new StubDoor(status: 1));
        Assert.True(fixture.Load("unaddressed", DoorState, MapObjectCategories.Door, door.Address()!.ToString()).Loaded);
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        // The door is not any zone's entrance gate, so it has no address at all: the status it reports now is
        // never published under a coordinate it was not read for.
        var unread = fixture.Track(new StubDoor(status: 1));
        unread.Zone = null;
        fixture.Module.DoorStateChanged(unread);
        Assert.Equal(1, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void ADoorWithoutCoordinatesIsNotAddressed()
    {
        using var fixture = new MapObjectFixture();
        var door = fixture.Track(new StubDoor(status: 1) { Layer = null });
        Assert.Null(door.Address());
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(0, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void TwoDoorsUnderOneParentZoneAreToldApartByTheZoneEachOneGuards()
    {
        using var fixture = new MapObjectFixture();
        // The same parent zone is entered once and expands into several children: each child has its own
        // entrance gate, and the address names the child, so the two doors are two objects.
        var first = fixture.Track(new StubDoor(status: 1, zone: 4));
        var second = fixture.Track(new StubDoor(status: 1, zone: 5));
        Assert.NotEqual(first.Address()!.ToString(), second.Address()!.ToString());
        fixture.Load("first-child", DoorState, MapObjectCategories.Door, first.Address()!.ToString());
        fixture.Load("second-child", DoorState, MapObjectCategories.Door, second.Address()!.ToString());
        fixture.Module.DoorStateChanged(first);
        fixture.Module.DoorStateChanged(second);
        // Two doors, two addresses, two mounts: neither door's state is published under the other's address.
        Assert.Equal(2, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void TwoTerminalsInOneZoneAreToldApartByTheirPlacementIndex()
    {
        using var fixture = new MapObjectFixture();
        var first = fixture.Track(new StubTerminal(placementIndex: 0));
        var second = fixture.Track(new StubTerminal(placementIndex: 1));
        Assert.NotEqual(first.Address()!.ToString(), second.Address()!.ToString());
        fixture.Load("first-placement", TerminalSession, MapObjectCategories.Terminal, first.Address()!.ToString());
        fixture.Load("second-placement", TerminalSession, MapObjectCategories.Terminal, second.Address()!.ToString());
        first.Status = 2;
        second.Status = 2;
        fixture.Module.TerminalStateChanged(first, null);
        fixture.Module.TerminalStateChanged(second, null);
        Assert.Equal(2, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void ATerminalWithoutAPlacementIndexIsNotAddressed()
    {
        using var fixture = new MapObjectFixture();
        var terminal = fixture.Track(new StubTerminal());
        terminal.PlacementIndex = null;
        Assert.Null(terminal.Address());
        fixture.Module.TerminalStateChanged(terminal, null);
        Assert.Equal(0, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void ANonAuthoritativePeerPublishesNothing()
    {
        using var fixture = new MapObjectFixture();
        var door = fixture.Track(new StubDoor(status: 1));
        fixture.Load("client", DoorState, MapObjectCategories.Door, door.Address()!.ToString());
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        fixture.Authority = false;
        door.Status = 10;
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);
        Assert.Contains(fixture.Reported, message => message.Contains("non-authoritative", StringComparison.Ordinal));
    }

    [Fact]
    public void ATerminalSessionAndItsOutcomeComeFromTheOneStateChange()
    {
        using var fixture = new MapObjectFixture();
        var terminal = fixture.Track(new StubTerminal { Status = 2 });
        // Session and result are two catalog rows, so a case that counts both subscribes both.
        fixture.Load("terminal-session", TerminalSession, MapObjectCategories.Terminal, terminal.Address()!.ToString());
        fixture.Load("terminal-session-result", TerminalResult, MapObjectCategories.Terminal, terminal.Address()!.ToString());
        fixture.Module.TerminalStateChanged(terminal, null);
        // A player sitting down at the terminal is a session, not an outcome: the interactive state carries no
        // execution outcome, so only the session fact is published.
        Assert.Equal(1, fixture.Module.PublishedFacts);

        fixture.Module.TerminalStateChanged(terminal, null);
        Assert.Equal(1, fixture.Module.PublishedFacts);

        terminal.Status = 4;
        fixture.Module.TerminalStateChanged(terminal, null);
        // Leaving the terminal ends the session and answers the command with its result in one state change.
        Assert.Equal(3, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void ATerminalCommandIsPublishedUnderItsOwnEnumName()
    {
        using var fixture = new MapObjectFixture();
        var terminal = fixture.Track(new StubTerminal());
        Assert.True(fixture.Load("terminal-command", TerminalCommand, MapObjectCategories.Terminal, terminal.Address()!.ToString()).Loaded);
        fixture.Module.TerminalCommandAccepted(terminal, 11, null);
        Assert.Equal(1, fixture.Module.PublishedFacts);
        fixture.Module.TerminalCommandAccepted(terminal, 11, null);
        Assert.Equal(1, fixture.Module.PublishedFacts);
        fixture.Module.TerminalCommandAccepted(terminal, 14, null);
        Assert.Equal(2, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void ATerminalResultReportsTheAcceptedCommand()
    {
        using var fixture = new MapObjectFixture();
        var terminal = fixture.Track(new StubTerminal { Status = 1 });
        // Both facts are subscribed, because a command that is accepted and a command that finishes are two
        // different catalog rows with their own plans.
        fixture.Load("terminal-result", TerminalResult, MapObjectCategories.Terminal, terminal.Address()!.ToString());
        fixture.Load("terminal-command-2", TerminalCommand, MapObjectCategories.Terminal, terminal.Address()!.ToString());
        fixture.Module.TerminalCommandAccepted(terminal, 11, null);
        Assert.Equal(1, fixture.Module.PublishedFacts);
        terminal.Status = 3;
        fixture.Module.TerminalStateChanged(terminal, null);
        Assert.Equal(2, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void AReadFailureOnOneCallbackDoesNotStopTheNextRealChange()
    {
        using var fixture = new MapObjectFixture();
        var door = fixture.Track(new StubDoor(status: 1));
        fixture.Load("throw", DoorState, MapObjectCategories.Door, door.Address()!.ToString());
        fixture.Doors.ThrowOnRead = true;
        Assert.Throws<InvalidOperationException>(() => fixture.Module.DoorStateChanged(door));
        fixture.Doors.ThrowOnRead = false;
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void FactsOfAnotherCategoryAreNeverPublishedThroughThisOne()
    {
        using var fixture = new MapObjectFixture();
        var terminal = fixture.Track(new StubTerminal());
        // A door callback carrying a terminal instance addresses nothing: the door source answers only for its
        // own native type, so the wrong instance cannot be observed under the wrong category.
        fixture.Module.DoorStateChanged(terminal);
        fixture.Module.DoorLockChanged(terminal);
        Assert.Equal(0, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void OnlyTheMountThatNamesThisAddressIsDelivered()
    {
        using var fixture = new MapObjectFixture();
        // One parent zone expands into two children, so two different doors guard two different zones. The plan
        // is mounted on the sibling's address: the fact is published for the door that changed, and the kernel's
        // attachment matcher is what keeps it out of this plan's mount.
        var mine = fixture.Track(new StubDoor(status: 1, zone: 4));
        var other = fixture.Track(new StubDoor(status: 1, zone: 5));
        fixture.Load("other-door", DoorState, MapObjectCategories.Door, other.Address()!.ToString());
        fixture.Module.DoorStateChanged(mine);
        fixture.Module.DoorStateChanged(other);
        Assert.Equal(1, fixture.Module.PublishedFacts);
    }

    [Fact]
    public void AnIdentityFromAnEarlierLevelIsRefusedAfterTheWorldChanged()
    {
        using var fixture = new MapObjectFixture();
        var door = fixture.Track(new StubDoor(status: 1));
        fixture.Load("world", DoorState, MapObjectCategories.Door, door.Address()!.ToString());
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(1, fixture.Module.PublishedFacts);
        var before = fixture.Reference(door.Address()!);
        Assert.True(fixture.Kernel.IsEntityCurrent(before));

        // A new level reuses the same coordinates for its own entrance: the old identity carries the old world
        // epoch, so it is refused, and the same door in the new world publishes as a new object.
        fixture.ForgeWorld(fixture.World + 1);
        Assert.False(fixture.Kernel.IsEntityCurrent(before));
        fixture.Module.DoorStateChanged(door);
        Assert.Equal(2, fixture.Module.PublishedFacts);
        Assert.True(fixture.Kernel.IsEntityCurrent(fixture.Reference(door.Address()!)));
    }

    /// <summary>The one Map registration has to carry the caller's entity surface beside this half's own: a
    /// definition that already declares another domain's kind keeps answering for it, and the map-object kind
    /// answers through this half's own resolver, instance lookup and observer.</summary>
    [Fact]
    public void TheOneRegistrationKeepsTheCallersEntitySurfaceBesideThisHalfs()
    {
        // The fixture's first world, named here because the probe's own resolvers are composed before the
        // fixture exists and have to answer in the world it starts in.
        long world = 1;
        using var fixture = new MapObjectFixture(definition => definition with
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                ["gtfo.probe"] = reference => reference.Id == "gtfo.probe:1"
            },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
            {
                ["gtfo.probe"] = instance => instance is int value && value == 1
                    ? new EntityReference("gtfo.probe:1", world, 1) : null
            },
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>(StringComparer.Ordinal)
            {
                ["gtfo.probe"] = reference => new RuntimeEntitySnapshot(reference, "probe", "probe",
                    "alive", new[] { "probe.tag" }, Array.Empty<string>(), new double[] { 0, 0, 0 })
            }
        });
        var probe = fixture.Kernel.InspectEntities(new[] { new EntityReference("gtfo.probe:1", fixture.World, 1) });
        Assert.Equal("entity-observed", probe.Items[0].Code);
        Assert.Equal("probe", probe.Items[0].Snapshot!.Kind);

        var door = fixture.Track(new StubDoor(status: 1));
        var inspected = fixture.Kernel.InspectEntities(new[] { fixture.Reference(door.Address()!) });
        Assert.Equal("entity-observed", inspected.Items[0].Code);
        Assert.Equal(MapObjectModule.EntityKind, inspected.Items[0].Snapshot!.Kind);
        Assert.Contains("map-object.door.status=closed", inspected.Items[0].Snapshot!.Tags);
        Assert.Equal(new double[] { 1, 2, 3 }, inspected.Items[0].Snapshot!.Position);

        // A position that did not read leaves the observation unavailable rather than publishing a zero one.
        fixture.Doors.PositionValue = null;
        Assert.Equal("entity-observation-unavailable",
            fixture.Kernel.InspectEntities(new[] { fixture.Reference(door.Address()!) }).Items[0].Code);
    }
}
