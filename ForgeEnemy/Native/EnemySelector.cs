using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The `forge.selector.target.enemies` query binding: the enemy entity set a selector step reads.
///
/// The set is the Enemy provider's own `gtfo.enemy` candidate source, read through the step's query session, so
/// the one enumeration of the module's tracked lives is the one the kernel charges to the query budget; the
/// source re-resolves every reference against its native instance as it lists it, and the kernel checks every
/// reference again before a consumer sees it. This binding keeps no second way to the module and no world read
/// of its own: a source that refuses or is unavailable is the step's own refusal, never an empty set.
///
/// The capability has no `subject` or `anchor` input, so `relation` cannot be evaluated against an anchor — the
/// site's targeting rule derives `self` from reference equality with the anchor and every other relation from
/// the anchor's faction, neither of which this node carries. This provider's kind is the game's enemy kind, and
/// a plan's executing side is the player team, so `hostile` is the relation every candidate really has to the
/// executor and it is the one relation the binding answers; `self`, `ally`, `neutral` and `unknown` are refused
/// with `relation-unsupported` instead of being answered with every enemy or with none. That is the same
/// criterion the already-implemented `forge.selector.target.players` binding applies to `ally`.
///
/// `empty` is a caller-side policy in this runtime — the kernel has no `skip`/`fail` path that an answer could
/// hand an incomplete set to — so the binding always answers its complete set and leaves the policy to the
/// step that consumes the collection.
///
/// The candidate set is not filtered by life state or by enemy type. The catalog row declares no parameter
/// that could carry either, the candidate read answers references and never snapshots, and the sibling player
/// selector returns every recorded life for the same reason. A binding that silently dropped candidates would
/// answer a shorter set than its provider's own table, which is the one thing this shape cannot report.</summary>
internal static class EnemySelector
{
    /// <summary>The `forge.selector.target.enemies` capability and the binding that implements it: declared in
    /// the game-independent Enemy assembly, implemented here. The port shape the handler is resolved against is
    /// that declaration's own `EnemySelectorContract.Shape`, and the kind it reads is the one that declaration
    /// names; this half supplies the evaluator only.</summary>
    internal const string CapabilityId = ForgeEnemy.EnemySelectorContract.CapabilityId;
    internal const string BindingId = ForgeEnemy.EnemySelectorContract.BindingId;
    internal const string HandlerName = ForgeEnemy.EnemySelectorContract.HandlerName;
    internal const string EntityKind = ForgeEnemy.EnemySelectorContract.EntityKind;

    /// <summary>Runs as the `query` step's evaluator: a read-only answer, evaluated on demand and memoized per
    /// activation, with no command and no world write. Reading the module's own candidate source is the step's
    /// world read, so the session the kernel offers here is exactly what it is read through.</summary>
    internal static JsonElement Evaluate(EvaluationContext context)
    {
        string relation = Text(context.Parameters, "relation");
        _ = Text(context.Parameters, "empty"); // Read, not applied: `empty` is the consuming step's policy.
        if (relation != "hostile")
            throw new RuntimeContractException("relation-unsupported", "This selector has no anchor, so only `hostile` is evaluated.");
        // The provider's refusal is the step's refusal, code and all: an unregistered module, a missing kind and
        // an exhausted budget must not read as an empty set of enemies.
        if (!context.Query.TryCandidates(EntityKind, out var candidates, out var code))
            throw new RuntimeContractException(code, "The enemy candidate source refused the read: " + code);
        var targets = new List<EntityFrame>(candidates.Count);
        foreach (var reference in candidates) targets.Add(new EntityFrame(reference.Id, reference.WorldEpoch, reference.LifeEpoch));
        return RuntimeJson.From(new { targets });
    }

    /// <summary>One entity value in the frame the kernel validates: identity, never a native instance.</summary>
    private sealed record EntityFrame(string Id, long WorldEpoch, long LifeEpoch);

    private static string Text(JsonElement parameters, string parameter)
    {
        if (!parameters.TryGetProperty(parameter, out var value) || value.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("missing-parameter", parameter);
        return value.GetString()!;
    }
}
