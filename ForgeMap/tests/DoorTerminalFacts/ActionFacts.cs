using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMapTests.DoorTerminalFacts;

/// <summary>The two door lock rows: which native setup each policy reaches, what is refused before anything is
/// written, the identity checks, the authority gate and the one policy the runtime cannot keep. Every case calls
/// the production `DoorTerminalActions` through the kernel, so the row's own declared ports are the ones the
/// handler reads.</summary>
public sealed class ActionFacts
{
    [Fact]
    public void TheNoKeyPolicyReachesTheDoorsOwnNoKeySetup()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        var result = Lock(world, world.ReferenceOf(door), "none");

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(1, World.Locks(door).NoKeyLockCalls);
        Assert.Equal(0, World.Locks(door).SimpleLockCalls);
        // The prompt the game shows at the door is the row's own structural choice with no port, so the setup is
        // asked with the game's own empty text rather than a text this layer invented.
        Assert.False(World.Locks(door).LastNoKeyText!.Value.HasValue);
    }

    [Fact]
    public void TheAnyPolicyReachesTheDoorsOwnSimpleSetup()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        var result = Lock(world, world.ReferenceOf(door), "any");

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(1, World.Locks(door).SimpleLockCalls);
        Assert.Equal(0, World.Locks(door).NoKeyLockCalls);
    }

    [Fact]
    public void TheSpecificPolicyIsRefusedBecauseNoItemProviderCanResolveAKey()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        var result = Lock(world, world.ReferenceOf(door), "specific");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.KeyItemUnavailableCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, World.Locks(door).NoKeyLockCalls);
        Assert.Equal(0, World.Locks(door).SimpleLockCalls);
    }

    [Fact]
    public void AnUnknownPolicyIsRefusedBeforeTheDoorIsEvenRead()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        var result = Lock(world, world.ReferenceOf(door), "whatever");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.PolicyCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, World.Locks(door).NoKeyLockCalls);
    }

    [Fact]
    public void ADoorAlreadyInTheRequestedLockReportsTheExistingState()
    {
        using var world = new World();
        world.Start();
        var door = world.Door(status: eDoorStatus.Closed_LockedWithNoKey);

        var result = Lock(world, world.ReferenceOf(door), "none");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.AlreadyLockedCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, World.Locks(door).NoKeyLockCalls);
    }

    [Fact]
    public void ARequestWithNoRecipientsIsRefusedRatherThanReportedDone()
    {
        using var world = new World();
        world.Start();

        var result = world.Actions.Execute(RuntimeJson.From(new { doors = Array.Empty<object>() }),
            Parameters("key_policy", "none"), unlock: false);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(DoorTerminalActions.NoTargetsCode, result.Code);
    }

    [Fact]
    public void AnotherCategoryOfTheSameKindIsRefusedByTheDoorRow()
    {
        using var world = new World();
        world.Start();

        var result = Lock(world, world.TerminalReference(), "none");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.KindCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AForeignKindIsRefusedByTheDoorRow()
    {
        using var world = new World();
        world.Start();

        var result = Lock(world, world.ForeignReference(), "none");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.KindCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceOfAWorldThatEndedIsRefused()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();

        var reference = world.StaleWorldReference(door);
        var current = world.Kernel.IsEntityCurrent(reference);
        var result = Lock(world, reference, "none");

        Assert.False(current);
        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.StaleCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, World.Locks(door).NoKeyLockCalls);
    }

    [Fact]
    public void ADoorThatMovedOffItsReferenceIsRefused()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();
        // The instance still resolves, but it no longer reads as the address the reference was built from.
        door.AddressNow = MapObjectDoorAddress.Create(World.Dimension, World.Layer, World.Zone + 1);

        var result = Lock(world, world.ReferenceOf(door), "none");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.StaleCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, World.Locks(door).NoKeyLockCalls);
    }

    [Fact]
    public void AClientCannotWriteDoorState()
    {
        using var world = new World();
        world.Start();
        var door = world.Door();
        world.LoseAuthority();

        var result = Lock(world, world.ReferenceOf(door), "none");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(DoorTerminalActions.AuthorityCode, result.Code);
        Assert.Equal(0, World.Locks(door).NoKeyLockCalls);
    }

    [Fact]
    public void UnlockAsksTheDoorsOwnInteractionEntryForUnlock()
    {
        using var world = new World();
        world.Start();
        var door = world.Door(status: eDoorStatus.Closed_LockedWithNoKey);

        var result = Unlock(world, world.ReferenceOf(door), "no");

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(1, World.Sync(door).InteractionCalls);
        Assert.Equal(eDoorInteractionType.Unlock, World.Sync(door).LastInteraction);
        // An unlock requested by a plan has no player behind it, so the source agent is absent rather than
        // invented.
        Assert.Null(World.Sync(door).LastAgent);
    }

    [Fact]
    public void UnlockRefusesToSpendAKey()
    {
        using var world = new World();
        world.Start();
        var door = world.Door(status: eDoorStatus.Closed_LockedWithKeyItem);

        var result = Unlock(world, world.ReferenceOf(door), "yes");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.ConsumeKeyUnsupportedCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, World.Sync(door).InteractionCalls);
    }

    [Fact]
    public void UnlockReportsADoorThatIsAlreadyUnlockedOrOpen()
    {
        using var world = new World();
        world.Start();
        var door = world.Door(status: eDoorStatus.Open);

        var result = Unlock(world, world.ReferenceOf(door), "no");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.NotLockedCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, World.Sync(door).InteractionCalls);
    }

    [Fact]
    public void UnlockWithoutASyncComponentIsRefused()
    {
        using var world = new World();
        world.Start();
        var door = world.Door(status: eDoorStatus.Closed_LockedWithNoKey);
        door.m_sync = null;

        var result = Unlock(world, world.ReferenceOf(door), "no");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.NoSyncComponentCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownConsumeKeyPolicyIsRefused()
    {
        using var world = new World();
        world.Start();
        var door = world.Door(status: eDoorStatus.Closed_LockedWithNoKey);

        var result = Unlock(world, world.ReferenceOf(door), "maybe");

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Contains(DoorTerminalActions.PolicyCode, result.Outputs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, World.Sync(door).InteractionCalls);
    }

    private static CommandResult Lock(World world, EntityReference door, string policy)
        => world.Actions.Execute(Inputs(door), Parameters("key_policy", policy), unlock: false);

    private static CommandResult Unlock(World world, EntityReference door, string consumeKey)
        => world.Actions.Execute(Inputs(door), Parameters("consume_key", consumeKey), unlock: true);

    /// <summary>The row's own `doors` input bag, carrying the reference exactly as it was built — including the
    /// world it belongs to, which is what makes a reference from a world that ended stay stale.</summary>
    private static JsonElement Inputs(EntityReference door)
        => RuntimeJson.From(new
        {
            doors = new[] { new { id = door.Id, worldEpoch = door.WorldEpoch, lifeEpoch = door.LifeEpoch } }
        });

    /// <summary>The row's own structural-parameter bag, with the one member the case names.</summary>
    private static JsonElement Parameters(string id, string value)
        => RuntimeJson.From(new Dictionary<string, string>(StringComparer.Ordinal) { [id] = value });
}
