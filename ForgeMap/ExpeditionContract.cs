using System;
using System.Globalization;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The one session-scope trigger this half implements: `forge.trigger.session.expedition_ended`, the
/// catalog's row for the end of an expedition. The capability id, its domains, its execution tier and its output
/// ports belong to the runtime's `TriggerContracts` provider, which declares the catalog row field for field; this
/// provider registers only the observation that publishes the fact.
///
/// The row's sibling `forge.trigger.session.expedition_started` is declared by `LevelEventContract`, which
/// publishes it from the level-event half.</summary>
public static class ExpeditionContract
{
    public const string EndedCapability = "forge.trigger.session.expedition_ended";

    /// <summary>The binding a native end-of-expedition reading publishes through: the capability's own suffix
    /// under the Map provider, so either side names the counterpart of a row.</summary>
    public static string EndedBinding => ModuleDefinition.ProviderId + ".binding.session.expedition_ended";

    /// <summary>The game's own `ExpeditionEndState` members as the native callback reports them
    /// (`RundownManager.OnExpeditionEnded(ExpeditionEndState)`, build 20403457: Success=0, Fail=1, Abort=2).
    /// The values are read from the game's enum, never guessed from the callback order, and an end state outside
    /// this set publishes nothing rather than being folded into one of the three.</summary>
    public const int Success = 0, Fail = 1, Abort = 2;

    /// <summary>The `execution_outcome` member an end state is, as the wire index the port carries, or null for a
    /// state this provider has no outcome for. Clearing the expedition is the one success, a wipe is the one
    /// failure, and leaving it is a cancellation — the three answers the catalog's own description names.</summary>
    public static int? Outcome(int endState) => endState switch
    {
        Success => MapObjectOutcomes.Succeeded,
        Fail => MapObjectOutcomes.Failed,
        Abort => MapObjectOutcomes.Cancelled,
        _ => null
    };

    /// <summary>The same three states in this provider's own vocabulary, for one bounded diagnostic.</summary>
    public static string Name(int endState) => endState switch
    {
        Success => "success",
        Fail => "fail",
        Abort => "abort",
        _ => "unknown"
    };

    internal const string EndedFact = "session.expedition_ended";

    /// <summary>The event id of one end of one world: the trigger's own id, the world epoch and the state the
    /// game reported. The epoch is what makes an id unique across worlds — the kernel's replay ledger is per
    /// world — and the state is part of the fact, so the same end reported twice is one event while a different
    /// end state in the same world is its own answer rather than a rejected id conflict.</summary>
    internal static string EventId(long worldEpoch, int endState)
        => EndedCapability + ":" + worldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + endState.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static object BindingRow() => new
    {
        id = EndedBinding,
        capabilityId = EndedCapability,
        providerId = ModuleDefinition.ProviderId,
        handler = "gtfo.map.session.expedition_ended",
        role = "observe",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };
}
