using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The `gtfo.enemy` candidate source: every enemy life this module currently tracks and can still
/// resolve, and nothing else. The kernel never discovers entities, so this table is the one answer to "which
/// enemies exist" — it is registered on the same module that already owns the `gtfo.enemy` resolver, which is
/// the only ownership the kernel accepts for a candidate source, and the selector binding reads it through the
/// step's query session rather than through a second way to the module.
///
/// The list is derived, never cached: a life the module retired, a native instance that was replaced, and a
/// world epoch that has moved on are all dropped by the same `Resolve` check every other module read uses, so
/// a world change (`ClearWorld`) and a despawn (which removes its own entry) clear the answer through the one
/// table rather than through a second bookkeeping copy. The order is `id` ordinal, exactly as the Map
/// provider's player source orders its lives, so two reads of an unchanged world answer identically.
///
/// The source never truncates: it names every life it holds, and a set larger than the kernel's per-query
/// ceiling is refused by the kernel with `entity-query-budget` — the evaluator rethrows that code, so an
/// overflowing set reaches the step as a refusal instead of a short list that looks like a small world.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The selector binding's support row: this provider implements it and needs no permission beyond
    /// the `gtfo.enemy` kind it already owns.</summary>
    internal static readonly BindingSupport EnemySelectorSupport =
        new(EnemySelector.BindingId, "implementation-only", Array.Empty<string>());

    internal IReadOnlyList<EntityReference> CurrentCandidates()
    {
        CheckThread();
        // A released module owns no table: answering an empty list here would claim the world holds no enemy,
        // which is exactly the answer the kernel must never be given by a source that is simply gone.
        if (!IsRegistered)
            throw new RuntimeContractException("enemy-module-unavailable", "No enemy module is registered.");
        var ordered = new List<EntityReference>();
        foreach (var entry in _entities.Values)
            if (Resolve(entry.Reference) != null) ordered.Add(entry.Reference);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return ordered;
    }
}
