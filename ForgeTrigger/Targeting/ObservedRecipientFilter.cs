using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

/// <summary>One relation selection over explicit current references. This is a read-only answer, not permission,
/// cost reservation or a committed effect: the references it returns are re-checked by the kernel before a
/// consumer sees them, exactly like the candidates it was handed.</summary>
public sealed class RecipientFilterSelection
{
    internal RecipientFilterSelection(EntityReference[] selected, int requested, int distinct)
    {
        Selected = Array.AsReadOnly(selected); Requested = requested; Distinct = distinct;
    }
    public IReadOnlyList<EntityReference> Selected { get; }
    public int Requested { get; }
    public int Distinct { get; }
    public int Matched => Selected.Count;
}

/// <summary>One filter row's structural parameters: the relation the candidates must have to the row's own
/// `anchor` input, the entity states and object categories the row accepts, and whether the anchor itself may be
/// one of the targets. There is no policy object here — the shape a plan compiles is the shape the capability
/// declares, and a field no row declares cannot be read by one. Every member is validated where it is read, so a
/// member the row does not declare answers the same refusal whether it arrived from a parameter bag or from a
/// caller.</summary>
public sealed record RecipientFilterRequest(string Relation, string State, int? Dimension,
    bool DamageableDoors, bool IncludeSelf)
{
    /// <summary>Every member of the catalog's `recipient_relation` set, in its declared order.</summary>
    public static readonly string[] Relations = { "self", "ally", "hostile", "neutral", "unknown" };
    /// <summary>The row's `state` members, in the catalog's own order: `any` accepts every candidate, `sleeping`
    /// the ones whose own behaviour state is the sleeping one, `awake` every candidate whose state was read and is
    /// not it.</summary>
    public static readonly string[] States = { "any", "sleeping", "awake" };

    /// <summary>The `ai_state` member a sleeping candidate is in — the state an enemy group publishes while it is
    /// asleep (`RuntimeGraphContracts.EnumSets["ai_state"]`). The one member this row names by hand, because
    /// "asleep" is a reading of the shared set and not a set of its own.</summary>
    internal const string SleepingState = "hibernating";

    /// <summary>The label a map object's own provider publishes its category under, which is how a candidate says
    /// it is a door or a terminal without the filter knowing any category by name.</summary>
    internal const string CategoryTag = "map-object.category=";

    /// <summary>A candidate whose own behaviour state this runtime cannot read, which the `state` members refuse
    /// rather than answer as awake.</summary>
    public const string StateUnknownCode = "recipient-state-unknown";
    /// <summary>A candidate whose own dimension cannot be read from the zone its provider places it in.</summary>
    public const string DimensionCode = "recipient-dimension";

    /// <summary>One request read from a row's resolved parameter frame, whose enums arrive as member names and
    /// whose numbers and flags arrive as the values the author wrote. A parameter the plan left out filters
    /// nothing — `any` for the relation and the state, no dimension, doors and the source kept — and a name outside
    /// its list is refused rather than answered with an empty selection, because "no candidate has a relation
    /// nobody declared" would look exactly like a world in which none of them matched.</summary>
    public static RecipientFilterRequest Read(string relation, string? state = null,
        int? dimension = null, bool damageableDoors = true, bool includeSelf = true)
        => new(Member(relation, Relations, "recipient-relation", -1), Member(state, States, "recipient-state", 0),
            dimension, damageableDoors, includeSelf);

    /// <summary>Refuses a request whose own members are not the ones the row declares, before any world read: a
    /// request assembled by hand carries the same refusal a compiled frame does.</summary>
    internal void Validate()
    {
        foreach (var (member, members, code) in new[]
                 {
                     (Relation, Relations, "recipient-relation"), (State, States, "recipient-state")
                 })
            if (member == null || !members.Contains(member, StringComparer.Ordinal))
                throw new RuntimeContractException(code, "Unknown member: " + member);
    }

    /// <summary>Whether one observed candidate is of the state the row asked for. A candidate whose behaviour
    /// state no observer publishes is refused: "asleep" and "awake" are readings, and a row that answered an
    /// unreadable candidate as either of them would be filtering on a value nobody read.</summary>
    internal bool AcceptsState(RuntimeEntitySnapshot snapshot)
    {
        if (State == "any") return true;
        if (snapshot.AiState == null)
            throw new RuntimeContractException(StateUnknownCode, "The behaviour state of " + snapshot.Ref.Id + " could not be read.");
        return State == "sleeping" ? snapshot.AiState == SleepingState : snapshot.AiState != SleepingState;
    }

    /// <summary>Whether one observed candidate survives the door rule: a map object is a door or a terminal the
    /// level placed, which its own provider says by publishing the category label, and a candidate that carries no
    /// such label is not one.</summary>
    internal bool AcceptsDoors(RuntimeEntitySnapshot snapshot)
        => DamageableDoors || !snapshot.Tags.Any(tag => tag.StartsWith(CategoryTag, StringComparison.Ordinal));

    private static string Member(string? member, string[] members, string code, int whenAbsent)
    {
        if (member == null)
        {
            if (whenAbsent >= 0) return members[whenAbsent];
            throw new RuntimeContractException(code, "A member is required.");
        }
        if (!members.Contains(member, StringComparer.Ordinal))
            throw new RuntimeContractException(code, "Unknown member: " + member);
        return member;
    }
}

/// <summary>Keeps the candidates whose current relation to the row's own anchor is the requested one, using the
/// world's own faction relations and the step's own observation session.
///
/// The anchor is the explicit `anchor` input of the filter row, never a role looked up again: a deployable whose
/// owner is the thing its targets must relate to wires `owner` into that port, and the row measures the relation
/// against exactly what it was handed. A pair the world says nothing about resolves to `unknown`, which is a
/// relation like any other and therefore matches only when `unknown` was asked for; it is never treated as "no
/// match" or as a guess at neutrality. The answer is ordered by stable identity, so the same world and the same
/// candidates always produce the same set.</summary>
public static class ObservedRecipientFilter
{
    public static RecipientFilterSelection Select(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference anchor, RuntimeFactionRelations relations, RecipientFilterRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(request);
        // A request the row cannot answer is refused before the world is read: a member the catalog does not
        // declare and a candidate list past the read budget are decidable from the step's own inputs, and a step
        // that could never have answered leaves no observation behind.
        var captured = candidates.ToArray();
        RuntimeEntityReferences.Validate(anchor);
        if (captured.Length > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Candidate input exceeds the public query limit.");
        request.Validate();
        var requested = captured.Contains(anchor) ? captured : captured.Append(anchor).ToArray();
        // One read for the candidates and their anchor together: the anchor's own current faction is part of the
        // answer, so observing it separately would be a second chance at a different world.
        var observed = ObservedSpaceNodes.Observe(session, requested);
        var anchorSnapshot = observed.Single(row => row.Ref == anchor);
        var candidatesSet = new HashSet<EntityReference>(captured);
        // Every member is measured on the one observation the row already made: the anchor's own snapshot is the
        // relation basis, the candidate's own snapshot is its state and category, and its dimension — the one
        // reading a snapshot does not carry — is the zone its own provider places it in.
        var matched = new List<EntityReference>();
        foreach (var row in observed)
        {
            if (!candidatesSet.Contains(row.Ref)) continue;
            if (!request.IncludeSelf && row.Ref == anchor) continue;
            if (!request.AcceptsDoors(row) || !request.AcceptsState(row)) continue;
            if (request.Dimension is { } dimension && !InDimension(session, row.Ref, dimension)) continue;
            if (!string.Equals(relations.Resolve(anchorSnapshot, row), request.Relation, StringComparison.Ordinal)) continue;
            matched.Add(row.Ref);
        }
        return new RecipientFilterSelection(
            matched.OrderBy(row => ReferenceCollections.OrderKey(row), StringComparer.Ordinal).ToArray(),
            captured.Length, candidatesSet.Count);
    }

    /// <summary>Whether one candidate stands in the dimension the row named. A zone is the only place an entity's
    /// dimension is published, and its id is the coordinates the runtime's own <see cref="RuntimeZones"/> formats,
    /// so the zone is read through the step's session like every other fact. A candidate no provider can place, or
    /// whose zone id is not those coordinates, is refused rather than answered as "another dimension".</summary>
    private static bool InDimension(RuntimeQuerySession session, EntityReference candidate, int dimension)
    {
        if (!session.TryZone(candidate, out var zone, out var code) || zone == null)
            throw new RuntimeContractException(code, "The zone of " + candidate.Id + " could not be read: " + code);
        var prefix = RuntimeZones.EntityKind + ":";
        var segments = zone.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? zone.Id.Substring(prefix.Length).Split(':') : Array.Empty<string>();
        if (segments.Length != 3
            || !int.TryParse(segments[0], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var found))
            throw new RuntimeContractException(RecipientFilterRequest.DimensionCode,
                "The zone of " + candidate.Id + " does not carry the runtime's own coordinates.");
        return found == dimension;
    }
}
