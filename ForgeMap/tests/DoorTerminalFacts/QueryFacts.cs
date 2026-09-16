using System;
using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMapTests.DoorTerminalFacts;

/// <summary>The `v-door` value row: the five states derived from a door's native status, the detailed readings
/// that travel beside them, and every refusal path. The evaluator is the production one; only the world read is
/// the fixture's, which is what makes "which member was read" and "which reference was refused" assertions.
///
/// The evaluation context has an assembly-internal constructor, so a case that is not running the whole dispatch
/// walk builds the same object through it. The kernel builds the identical object for a real query step, and the
/// two inputs the row has — the door reference — are what that step would carry.</summary>
public sealed class QueryFacts
{
    private static readonly ConstructorInfo EvaluationConstructor = typeof(EvaluationContext)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[]
        {
            typeof(string), typeof(JsonElement), typeof(JsonElement), typeof(RuntimeQuerySession),
            typeof(RuntimeActorContext), typeof(RuntimeFactionRelations)
        }, null) ?? throw new InvalidOperationException("EvaluationContext's own constructor was not found.");

    [Fact]
    public void ADoorReadsAsTheStateItsStatusIs()
    {
        Assert.Equal("needs_scan", Evaluate(eDoorStatus.Closed_LockedWithChainedPuzzle).State);
        Assert.Equal("broken", Evaluate(eDoorStatus.Destroyed).State);
        Assert.Equal("open", Evaluate(eDoorStatus.Open).State);
        Assert.Equal("locked", Evaluate(eDoorStatus.Closed_LockedWithNoKey).State);
        Assert.Equal("closed", Evaluate(eDoorStatus.Closed).State);
        // The exact native status and the lock reading travel beside the coarse state.
        var state = Evaluate(eDoorStatus.Closed_LockedWithKeyItem, locked: true, key: "KEY_A");
        Assert.Equal("locked", state.State);
        Assert.Equal("closed_locked_with_key_item", state.Detail);
        Assert.True(state.Locked);
        Assert.Equal("KEY_A", state.Key);
    }

    [Fact]
    public void AKeyPortIsAbsentWhenNoKeyIsWanted()
    {
        var answer = Answer(eDoorStatus.Closed);
        Assert.False(answer.TryGetProperty("key", out _));
        Assert.False(answer.GetProperty("locked").GetBoolean());
    }

    [Fact]
    public void AReferenceOfAnotherKindIsRefusedByName()
    {
        var refused = Refusal(new EntityReference("gtfo.player:steam-1", World.WorldEpoch, 1));
        Assert.Equal(DoorQueryContract.InputKindCode, refused);
    }

    [Fact]
    public void AReferenceOfTheRightKindThatIsNotADoorAddressIsRefused()
    {
        // A terminal is a map object too, and reading it as a door would answer with a state the terminal never
        // had. The address grammar is the door category's own, so a terminal address fails it.
        var refused = Refusal(new EntityReference(World.Kind + ":terminal/0/0/3/0", World.WorldEpoch, 1));
        Assert.Equal(DoorQueryContract.InputAddressCode, refused);
    }

    [Fact]
    public void ADoorTheWorldNoLongerAnswersForIsRefused()
    {
        var refused = Refusal(DoorReference(), read: _ => null);
        Assert.Equal(DoorQueryContract.InputStaleCode, refused);
    }

    [Fact]
    public void AMissingDoorInputIsRefusedByPortName()
    {
        var error = Assert.Throws<RuntimeContractException>(() => Evaluate(RuntimeJson.EmptyObject,
            _ => new DoorQueryContract.DoorSample((int)eDoorStatus.Closed, false, null)));
        Assert.Equal("missing-field", error.Code);
    }

    [Fact]
    public void AStatusOutsideTheEnumIsRefusedRatherThanMapped()
    {
        // The sample reaches the derivation as a number, and a number no `eDoorStatus` member has is refused
        // instead of being reported as the nearest state it is not.
        var refused = Refusal(DoorReference(), read: _ => new DoorQueryContract.DoorSample(200, false, null));
        Assert.Equal(DoorQueryContract.StatusCode, refused);
    }

    private static (string State, string Detail, bool Locked, string? Key) Evaluate(eDoorStatus status,
        bool locked = false, string? key = null)
    {
        var answer = Evaluate(new { door = DoorReference() },
            reference => reference.Id == DoorReference().Id
                ? new DoorQueryContract.DoorSample((int)status, locked, key) : null);
        // An enum port carries the member index of its own set, never the name, so the answer is read back
        // through the row's own vocabulary.
        return (DoorQueryContract.StateName(answer.GetProperty("state").GetInt32()),
            answer.GetProperty("detail").GetString()!,
            answer.GetProperty("locked").GetBoolean(),
            answer.TryGetProperty("key", out var declared) ? declared.GetString() : null);
    }

    private static JsonElement Answer(eDoorStatus status)
        => Evaluate(new { door = DoorReference() },
            reference => new DoorQueryContract.DoorSample((int)status, false, null));

    private static JsonElement Evaluate(object inputs, Func<EntityReference, DoorQueryContract.DoorSample?> read)
        => DoorQueryContract.Evaluator(new DoorQueryContract.DoorReaders(read))(Context(inputs));

    private static string Refusal(EntityReference? door, Func<EntityReference, DoorQueryContract.DoorSample?>? read = null)
    {
        var inputs = door == null ? RuntimeJson.EmptyObject : RuntimeJson.From(new { door });
        var error = Assert.Throws<RuntimeContractException>(() => Evaluate(inputs!,
            read ?? (_ => new DoorQueryContract.DoorSample((int)eDoorStatus.Closed, false, null))));
        return error.Code;
    }

    /// <summary>One door reference of this provider's own kind, spelled the way the trigger rows publish one.</summary>
    private static EntityReference DoorReference()
        => new(World.Kind + ":" + MapObjectDoorAddress.Create(World.Dimension, World.Layer, World.Zone), World.WorldEpoch, 1);

    /// <summary>The evaluation context the kernel builds for a real query step: the row's own input bag and no
    /// session, actors or relations, because this row reads none of the three.</summary>
    private static EvaluationContext Context(object inputs)
        => (EvaluationContext)EvaluationConstructor.Invoke(new object?[]
        {
            "A_row", RuntimeJson.EmptyObject, RuntimeJson.From(inputs), null!, null!, null!
        })!;
}
