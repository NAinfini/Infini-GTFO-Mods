using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>One wielded weapon's ammunition, in rounds.</summary>
internal readonly record struct PlayerAmmo(int Clip, int ClipMaximum, int Reserve);

/// <summary>What the tool row reads: the held item the two ammunition numbers belong to, the class-ammunition
/// pool the game itself spends as tool energy, and the backpack's own consumable stacks.</summary>
internal readonly record struct PlayerTool(EntityReference? Item, int Ammo, int AmmoMaximum, int Count, int Stacks);

/// <summary>What the value rows need from the native side beyond the shared entity snapshot: the receiver's
/// infection, the item the player holds, the rounds that item carries, and the expedition item in the backpack.
/// A read that cannot be made answers a code instead of a value, because a value row that answered a zero for an
/// unreadable field would read exactly like an observed zero.</summary>
internal interface IPlayerValueSource
{
    bool TryInfection(EntityReference reference, out double value, out string code);

    /// <summary>Whether the player is holding an item at all, and which equipment entity it is. A player holding
    /// nothing answers true with a null entity: "holding nothing" is observed, not unknown.</summary>
    bool TryWieldedGear(EntityReference reference, out EntityReference? equipment, out string code);

    bool TryAmmo(EntityReference reference, out PlayerAmmo ammo, out string code);

    /// <summary>Whether the player carries an expedition item, and which equipment entity it is. True with a null
    /// entity means a read that found nobody carrying one.</summary>
    bool TryCarriedItem(EntityReference reference, out EntityReference? item, out string code);

    /// <summary>The held item's class-ammunition pool and the backpack's consumable stacks. A player holding
    /// nothing has no pool, which is a refusal and not a zero.</summary>
    bool TryTool(EntityReference reference, out PlayerTool tool, out string code);
}

/// <summary>The eight read-only value rows of the player domain.
///
/// Every one of them is a `query` step, and every one of them reads the world exactly once through the kernel's
/// budgeted session: the player it is about is resolved through `TrySnapshot`, which is what charges the query
/// budget, proves the reference still names a life and carries the fields the shared snapshot owns (health, its
/// maximum and the position). The fields the shared snapshot does not carry — infection, the wielded gear, its
/// ammunition and the carried item — are read by this provider's own native half from the same life the snapshot
/// just verified; that read is not a second world query and adds no second identity check, which is why this file
/// takes the reference from the snapshot and never from the frame a second time.
///
/// A value that cannot be read is a refusal with a code, never a zero and never an empty answer: a snapshot
/// without health means the provider does not publish health for that life (`health-unavailable`), a player
/// holding no weapon has no clip (`no-wielded-gear`), and a source that is not attached at all refuses by name
/// instead of answering from a table that was never filled.</summary>
internal sealed class PlayerValueReads
{
    /// <summary>The code a value read answers when the provider publishes no source. It is a refusal and not an
    /// empty value: the row exists, this process just cannot read it.</summary>
    internal const string SourceUnavailableCode = "player-value-source-unavailable";
    internal const string HealthUnavailableCode = "health-unavailable";
    internal const string PositionUnavailableCode = "position-unavailable";
    internal const string NoWieldedGearCode = "no-wielded-gear";
    internal const string MissingFieldCode = "missing-field";

    internal static PlayerValueReads? Installed { get; private set; }

    private readonly IPlayerValueSource _source;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    internal PlayerValueReads(IPlayerValueSource source) => _source = source ?? throw new ArgumentNullException(nameof(source));

    internal static PlayerValueReads Attach(IPlayerValueSource source)
    {
        Installed = new PlayerValueReads(source);
        return Installed;
    }

    internal static void Detach(PlayerValueReads reads)
    {
        if (ReferenceEquals(Installed, reads)) Installed = null;
    }

    /// <summary>The evaluator table this provider's registration composes: one entry per value row, each name
    /// declared with a shape in `PlayerStateContract.ValueShapes`.</summary>
    internal static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [PlayerStateContract.HealthValueHandler] = Evaluate(Health),
        [PlayerStateContract.InfectionValueHandler] = Evaluate(Infection),
        [PlayerStateContract.DownedValueHandler] = Evaluate(Downed),
        [PlayerStateContract.PositionValueHandler] = Evaluate(Position),
        [PlayerStateContract.WieldedGearValueHandler] = Evaluate(WieldedGear),
        [PlayerStateContract.AmmoValueHandler] = Evaluate(Ammo),
        [PlayerStateContract.CarriedItemValueHandler] = Evaluate(CarriedItem),
        [PlayerStateContract.ToolValueHandler] = Evaluate(Tool)
    };

    private static EvaluatorHandler Evaluate(Func<PlayerValueReads, EntityReference, RuntimeEntitySnapshot, JsonElement> answer)
        => context =>
        {
            if (Installed is not { } reads) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
            reads.CheckThread();
            var (reference, snapshot) = Read(context);
            return answer(reads, reference, snapshot);
        };

    /// <summary>The one budgeted read every value row starts with: the player the row is about, resolved through
    /// the session's own snapshot read. The row's `player` port is required, so a frame without one is refused by
    /// name; the entity is the row's, never the event's or the mount's.</summary>
    private static (EntityReference Reference, RuntimeEntitySnapshot Snapshot) Read(EvaluationContext context)
    {
        if (!context.Inputs.TryGetProperty("player", out var value) || value.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException(MissingFieldCode, "player");
        var reference = RuntimeJson.Entity(value);
        if (!context.Query.TrySnapshot(reference, out var snapshot, out var code))
            throw new RuntimeContractException(code, "Player read failed: " + code);
        return (reference, snapshot!);
    }

    // The seven answers, each over the one budgeted read the evaluator already made. They are internal rather than
    // private so this package's focused tests can drive a row with a written snapshot and a written source: a
    // value row's whole contract is which ports it carries and which refusal it raises, and neither needs a
    // dispatch to be exercised.

    internal static JsonElement AnswerHealth(RuntimeEntitySnapshot snapshot)
    {
        if (snapshot.Health is not { } current || snapshot.HealthMaximum is not { } maximum)
            throw new RuntimeContractException(HealthUnavailableCode, "This provider publishes no health for the life.");
        return PlayerStateContract.HealthAnswer(current, maximum);
    }

    internal static JsonElement AnswerInfection(IPlayerValueSource? source, EntityReference reference, RuntimeEntitySnapshot snapshot)
    {
        if (source == null) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
        if (!source.TryInfection(reference, out var value, out var code))
            throw new RuntimeContractException(code, "Infection read failed: " + code);
        return PlayerStateContract.InfectionAnswer(value);
    }

    /// <summary>The downed state comes from the shared snapshot's own life state, which is the one place the
    /// provider publishes it: a life that is neither `alive` nor `downed` is dead, and that is what the `alive`
    /// port answers.</summary>
    internal static JsonElement AnswerDowned(RuntimeEntitySnapshot snapshot)
        => PlayerStateContract.DownedAnswer(snapshot.LifeState == "downed", snapshot.LifeState != "dead");

    internal static JsonElement AnswerPosition(RuntimeEntitySnapshot snapshot)
        => snapshot.Position.Count == 3
            ? PlayerStateContract.PositionAnswer(new[] { snapshot.Position[0], snapshot.Position[1], snapshot.Position[2] })
            : throw new RuntimeContractException(PositionUnavailableCode, "This provider publishes no position for the life.");

    internal static JsonElement AnswerWieldedGear(IPlayerValueSource? source, EntityReference reference, RuntimeEntitySnapshot snapshot)
    {
        if (source == null) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
        if (!source.TryWieldedGear(reference, out var equipment, out var code))
            throw new RuntimeContractException(code, "Wielded gear read failed: " + code);
        return PlayerStateContract.WieldedGearAnswer(equipment);
    }

    internal static JsonElement AnswerAmmo(IPlayerValueSource? source, EntityReference reference, RuntimeEntitySnapshot snapshot)
    {
        if (source == null) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
        if (!source.TryAmmo(reference, out var ammo, out var code))
            throw new RuntimeContractException(code, "Ammunition read failed: " + code);
        return PlayerStateContract.AmmoAnswer(ammo.Clip, ammo.ClipMaximum, ammo.Reserve);
    }

    internal static JsonElement AnswerCarriedItem(IPlayerValueSource? source, EntityReference reference, RuntimeEntitySnapshot snapshot)
    {
        if (source == null) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
        if (!source.TryCarriedItem(reference, out var item, out var code))
            throw new RuntimeContractException(code, "Carried item read failed: " + code);
        return PlayerStateContract.CarriedItemAnswer(item);
    }

    /// <summary>The tool row's answer: the class-ammunition pool of the item the player holds and the backpack's
    /// consumable stacks, read by the source in one call so the five ports describe one moment. Neither half is
    /// derived from the other: a player holding a weapon that is not a tool still has its class pool, and a
    /// backpack with no pocket item answers zero rather than a missing port.</summary>
    internal static JsonElement AnswerTool(IPlayerValueSource? source, EntityReference reference, RuntimeEntitySnapshot snapshot)
    {
        if (source == null) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
        if (!source.TryTool(reference, out var tool, out var code))
            throw new RuntimeContractException(code, "Tool read failed: " + code);
        return PlayerStateContract.ToolAnswer(tool.Item, tool.Ammo, tool.AmmoMaximum, tool.Count, tool.Stacks);
    }

    private static JsonElement Health(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerHealth(snapshot);

    private static JsonElement Infection(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerInfection(reads._source, reference, snapshot);

    private static JsonElement Downed(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerDowned(snapshot);

    private static JsonElement Position(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerPosition(snapshot);

    private static JsonElement WieldedGear(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerWieldedGear(reads._source, reference, snapshot);

    private static JsonElement Ammo(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerAmmo(reads._source, reference, snapshot);

    private static JsonElement CarriedItem(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerCarriedItem(reads._source, reference, snapshot);

    private static JsonElement Tool(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerTool(reads._source, reference, snapshot);

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player value reads require the runtime's own simulation thread.");
    }
}
