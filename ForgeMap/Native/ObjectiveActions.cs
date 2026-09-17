using System;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Which objective the request is about. The chain index is the one the native state carries
/// (`pWardenObjectiveState`'s chain byte), and the layer is the objective's own layer key: those two are what
/// `WardenObjectiveManager.TryGetWardenObjective` resolves a chain by, so an address is checked against the same
/// lookup the native objective machine itself uses instead of against a table of this layer's own.</summary>
internal readonly struct ObjectiveTarget
{
    internal ObjectiveTarget(LG_LayerType layer, int chainIndex)
    {
        Layer = layer;
        ChainIndex = chainIndex;
    }

    internal LG_LayerType Layer { get; }
    internal int ChainIndex { get; }

    internal static ObjectiveTarget LayerOnly(LG_LayerType layer) => new(layer, -1);
    internal bool WholeLayer => ChainIndex < 0;
}

/// <summary>The state interactions this row carries, and the native meaning each one has. The names are this
/// layer's own vocabulary — the catalog's `state` string is compared against them — and the member values are
/// the bytes `eWardenObjectiveInteractionType` declares, so a member's byte is the index it has there.</summary>
internal enum ObjectiveStateKind : byte
{
    Discover = 0,
    Start = 1,
    UpdateSubObjective = 5,
    CustomSubObjectiveUpdate = 8,
    SetItemSolved = 9,
    SetExtraTime = 11,
    SetSolveOnDeath = 12,
    SetExitWaveTriggered = 13
}

/// <summary>The phase interactions this row carries: the objective's own event chain step and the win-condition
/// member its sibling row also reaches. `CompleteChain` is not an interaction type at all — it is the
/// whole-layer force entry, which names no chain, so it is the one member with its own native call.</summary>
internal enum ObjectivePhaseKind : byte
{
    EventUpdate = 7,
    SolveWinCondition = 4,
    CompleteChain = 255
}

/// <summary>The interaction families this row does not carry. They reach the same native entry through the same
/// struct, but they are not this row's: the item-solve family has no row of its own in the catalog. A request for
/// one of them here is refused by name rather than carried out under a row that does not describe it.</summary>
internal enum ObjectiveForeignKind : byte
{
    SolveItem = 2,
    PartiallySolveItem = 3,
    AddRequiredItem = 10
}

/// <summary>One event step of the objective's own event chain, as `pWardenObjectiveInteraction` carries it: the
/// break index and the index inside it. Both are bytes, so a value outside one byte has nothing to write. The
/// two indices compare by value because every member but the event update is asked for with no step at all, and
/// that check is a comparison rather than a pair of field reads.</summary>
internal readonly struct ObjectiveEventStep : IEquatable<ObjectiveEventStep>
{
    internal ObjectiveEventStep(int breakIndex, int index)
    {
        BreakIndex = breakIndex;
        Index = index;
    }

    internal int BreakIndex { get; }
    internal int Index { get; }

    internal static ObjectiveEventStep None => new(0, 0);

    public bool Equals(ObjectiveEventStep other) => BreakIndex == other.BreakIndex && Index == other.Index;
    public override bool Equals(object? other) => other is ObjectiveEventStep step && Equals(step);
    public override int GetHashCode() => HashCode.Combine(BreakIndex, Index);
    public static bool operator ==(ObjectiveEventStep left, ObjectiveEventStep right) => left.Equals(right);
    public static bool operator !=(ObjectiveEventStep left, ObjectiveEventStep right) => !left.Equals(right);
}

/// <summary>
/// The objective half of the native execution layer: one method per objective action, each writing through the
/// game's own interaction entry or the manager member that owns the decision. Nothing here is a plan, a handle
/// or a result frame — a later binding resolves the target and the parameters and calls in, and this layer
/// answers with the one decision it made.
///
/// Two rules hold for every method. The target is resolved before anything is written: the layer must be this
/// build's own member and the addressed chain must be the one `WardenObjectiveManager.TryGetWardenObjective`
/// answers for, so an objective this level did not build — a wrong layer, a chain index past the end, a world
/// that was torn down — is refused rather than asked. And a request whose arguments no native entry can carry is
/// refused by name: the interaction struct carries a byte for the chain, a byte each for the two event indices,
/// and it carries no target status at all, so a request for a value it cannot express is never run in its plain
/// form.
///
/// What this layer deliberately does not do is replace native progress. Every member it reaches is the entry the
/// game's own objective machine calls, so the state a request asks for is the state the objective's own rules
/// can reach: forcing a member makes the objective's own machine act, it does not write a status directly. A
/// native read that throws is not caught here: an action runs inside the same session guard as the readbacks,
/// which disables the provider once instead of letting this layer swallow a failure the rest of the session
/// would keep acting on.
/// </summary>
internal static class ObjectiveActions
{
    /// <summary>The manager the objective interaction entry belongs to; without it nothing can be asked.</summary>
    internal const string Unavailable = "objective-unavailable";
    /// <summary>The address is not one this level built, or its chain could not be read.</summary>
    internal const string UnknownTarget = "objective-unknown-target";
    /// <summary>A whole-layer member was asked for with a chain, or a chain member without one.</summary>
    internal const string TargetShapeUnsupported = "objective-target-shape-unsupported";
    internal const string StateKindUnknown = "objective-state-unknown";
    internal const string PhaseKindUnknown = "objective-phase-unknown";
    /// <summary>The `expected_state`/`expected_phase` precondition has no native check to run against.</summary>
    internal const string ExpectedStateUnsupported = "objective-expected-state-unsupported";
    /// <summary>A sub-objective member outside the vocabulary the interaction struct carries.</summary>
    internal const string SubObjectiveUnknown = "objective-sub-objective-unknown";
    internal const string SubObjectiveUnsupported = "objective-sub-objective-unsupported";
    internal const string EventStepUnsupported = "objective-event-step-unsupported";
    internal const string ItemIdUnsupported = "objective-item-id-unsupported";
    internal const string ExtraTimeUnsupported = "objective-extra-time-unsupported";
    /// <summary>The objective's own layer is not one this process has objective data for.</summary>
    internal const string NoObjectiveData = "objective-no-data";

    internal const string Discovered = "objective-discover-issued";
    internal const string Started = "objective-start-issued";
    internal const string SubObjectiveUpdated = "objective-sub-objective-issued";
    internal const string ItemSolved = "objective-item-solved-issued";
    internal const string ExtraTimeSet = "objective-extra-time-issued";
    internal const string SolveOnDeathSet = "objective-solve-on-death-issued";
    internal const string ExitWaveTriggered = "objective-exit-wave-issued";
    internal const string ChainCompleted = "objective-chain-completed-issued";
    internal const string PhaseUpdated = "objective-phase-issued";
    internal const string WinConditionSolved = "objective-win-condition-issued";

    /// <summary>The landing's own win-condition item was armed, or disarmed. Both are the item's own members.</summary>
    internal const string ExtractionArmed = "extraction-win-condition-armed";
    internal const string ExtractionDisarmed = "extraction-win-condition-disarmed";

    /// <summary>The last sub-objective member the interaction struct carries: the vocabulary's own members run
    /// from 0 to `GoToWinConditionHelp`, and a value past it is not a member the native side can be asked for.</summary>
    private const int LastSubObjective = (int)eWardenSubObjectiveStatus.GoToWinConditionHelp;
    /// <summary>No item was named. The struct's item field is an int index, so "absent" is its own value rather
    /// than zero, which is a real item index.</summary>
    internal const int NoItem = -1;

    /// <summary>Asks the objective's own interaction entry for one state member. The member's own arguments are
    /// checked against the member that was asked for: only the sub-objective member carries a sub-objective, only
    /// the item member carries an item index, and only the extra-time member carries a time.</summary>
    internal static MapActionOutcome SetState(WardenObjectiveManager manager, ObjectiveTarget target,
        ObjectiveStateKind kind, string? expectedState, int subObjective, int itemId, float extraTime)
    {
        if (manager == null || manager.WasCollected) return MapActionOutcome.Refused(Unavailable);
        if (!Reachable(target)) return MapActionOutcome.Refused(UnknownTarget);
        if (expectedState != null) return MapActionOutcome.Refused(ExpectedStateUnsupported);
        if (kind == ObjectiveStateKind.UpdateSubObjective)
        {
            if (subObjective < 0 || subObjective > LastSubObjective) return MapActionOutcome.Refused(SubObjectiveUnknown);
        }
        else if (subObjective != 0) return MapActionOutcome.Refused(SubObjectiveUnsupported);
        if (kind == ObjectiveStateKind.SetItemSolved)
        {
            if (itemId == NoItem) return MapActionOutcome.Refused(ItemIdUnsupported);
        }
        else if (itemId != NoItem) return MapActionOutcome.Refused(ItemIdUnsupported);
        if (kind == ObjectiveStateKind.SetExtraTime)
        {
            if (!float.IsFinite(extraTime)) return MapActionOutcome.Refused(ExtraTimeUnsupported);
        }
        else if (extraTime != 0) return MapActionOutcome.Refused(ExtraTimeUnsupported);
        // Extra time is the one member with a dedicated entry of its own, and that entry names no layer and no
        // chain: the objective's own machine decides which layer's timer the value belongs to. The interaction
        // form carries the same float, so the dedicated entry is reached rather than the general one.
        if (kind == ObjectiveStateKind.SetExtraTime)
        {
            WardenObjectiveManager.SetExtraTime(extraTime);
            return MapActionOutcome.Issued(ExtraTimeSet);
        }

        var interaction = new pWardenObjectiveInteraction
        {
            type = (eWardenObjectiveInteractionType)(byte)kind,
            inLayer = target.Layer,
            newSubObj = (eWardenSubObjectiveStatus)(byte)subObjective,
            itemID = itemId == NoItem ? 0 : itemId,
            extraTime = extraTime
        };
        manager.AttemptInteract(interaction);
        return MapActionOutcome.Issued(StateCode(kind));
    }

    /// <summary>Asks the objective's own interaction entry for one phase member. The chain index is the byte the
    /// interaction struct carries and the layer is the request's own layer, so a request cannot silently move the
    /// interaction to another layer's objective.</summary>
    internal static MapActionOutcome SetPhase(WardenObjectiveManager manager, ObjectiveTarget target,
        ObjectivePhaseKind kind, string? expectedPhase, ObjectiveEventStep step)
    {
        if (manager == null || manager.WasCollected) return MapActionOutcome.Refused(Unavailable);
        if (!Reachable(target)) return MapActionOutcome.Refused(UnknownTarget);
        if (expectedPhase != null) return MapActionOutcome.Refused(ExpectedStateUnsupported);
        if (kind == ObjectivePhaseKind.CompleteChain)
        {
            // The whole-layer force entry names no chain, and a chain member cannot be served by it: one call
            // completes every chain the layer has.
            if (!target.WholeLayer) return MapActionOutcome.Refused(TargetShapeUnsupported);
            if (step != ObjectiveEventStep.None) return MapActionOutcome.Refused(EventStepUnsupported);
            WardenObjectiveManager.ForceCompleteObjectiveAll(target.Layer);
            return MapActionOutcome.Issued(ChainCompleted);
        }
        if (target.WholeLayer) return MapActionOutcome.Refused(TargetShapeUnsupported);
        if (kind == ObjectivePhaseKind.EventUpdate)
        {
            // Both indices are bytes on the wire, and the break index names a break the objective's own data
            // block declares: a value outside one byte has nothing to write.
            if (step.BreakIndex < 0 || step.BreakIndex > 255 || step.Index < 0 || step.Index > 255)
                return MapActionOutcome.Refused(EventStepUnsupported);
        }
        else if (step != ObjectiveEventStep.None) return MapActionOutcome.Refused(EventStepUnsupported);
        if (target.ChainIndex > 254) return MapActionOutcome.Refused(UnknownTarget);

        var interaction = new pWardenObjectiveInteraction
        {
            type = (eWardenObjectiveInteractionType)(byte)kind,
            inLayer = target.Layer,
            ownerChainIndexPlusOne = (byte)(target.ChainIndex + 1),
            newOnActivateEventBreakIndex = (byte)step.BreakIndex,
            newOnActivateEventIndex = (byte)step.Index
        };
        manager.AttemptInteract(interaction);
        return MapActionOutcome.Issued(kind == ObjectivePhaseKind.SolveWinCondition ? WinConditionSolved : PhaseUpdated);
    }

    /// <summary>Asks the level's own win-condition item to arm or disarm. The landing owns the exit and both
    /// members are its own, so neither invents an extraction the level did not build. The disarming member has no
    /// caller in this build, so what it produced is read back by the extraction observation rather than claimed
    /// here.</summary>
    internal static MapActionOutcome SetExtraction(ElevatorShaftLanding landing, bool enabled)
    {
        if (landing == null || landing.WasCollected) return MapActionOutcome.Refused(Unavailable);
        if (enabled) landing.ActivateWinCondition();
        else landing.DeactivateWinCondition();
        return MapActionOutcome.Issued(enabled ? ExtractionArmed : ExtractionDisarmed);
    }

    /// <summary>The one gate every objective action passes: the layer must be a member of the layer vocabulary,
    /// and the whole-layer form must be a layer this build has objective data for. A chain address is checked
    /// against the game's own lookup, so a chain the level never built is refused before any write.</summary>
    internal static bool Reachable(ObjectiveTarget target)
    {
        if (!Enum.IsDefined(typeof(LG_LayerType), target.Layer)) return false;
        if (target.WholeLayer) return WardenObjectiveManager.HasWardenObjectiveDataForLayer(target.Layer);
        return WardenObjectiveManager.TryGetWardenObjective(target.Layer, target.ChainIndex, out _);
    }

    private static string StateCode(ObjectiveStateKind kind) => kind switch
    {
        ObjectiveStateKind.Discover => Discovered,
        ObjectiveStateKind.Start => Started,
        ObjectiveStateKind.UpdateSubObjective => SubObjectiveUpdated,
        ObjectiveStateKind.CustomSubObjectiveUpdate => SubObjectiveUpdated,
        ObjectiveStateKind.SetItemSolved => ItemSolved,
        ObjectiveStateKind.SetExtraTime => ExtraTimeSet,
        ObjectiveStateKind.SetSolveOnDeath => SolveOnDeathSet,
        _ => ExitWaveTriggered
    };
}
