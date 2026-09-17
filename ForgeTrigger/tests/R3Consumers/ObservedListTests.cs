using System;
using System.Linq;
using ForgeRuntime.Framework;
using ForgeTrigger.Targeting;

/// <summary>The list rows the batch registers, exercised through the exact public entry points their handlers
/// call. What the handlers add on top — the port ids, the one output and the declared world read — is checked
/// against the catalog by the contract suite; what is checked here is the answer itself: an empty list is empty,
/// contains nothing and counts zero, membership is the full identity rather than an id, the count is the list's
/// own length, and no answer depends on the list being ordered or deduplicated.</summary>
internal static class ObservedListTests
{
    internal static void Run(Action<bool, string> check)
    {
        var first = new EntityReference("test.list:a", 1, 1);
        var second = new EntityReference("test.list:b", 1, 1);
        var sameIdOtherLife = first with { LifeEpoch = 2 };
        var empty = Array.Empty<EntityReference>();

        // The answers an empty list has are fixed by the row and never by the world: an empty list can only mean
        // empty, so a gate over an empty selection is decided by the list the step was handed.
        check(ObservedListDeclarations.IsEmpty(empty), "is_empty: an empty list is empty");
        check(!ObservedListDeclarations.Contains(empty, first), "contains: an empty list contains nothing");
        check(ObservedListDeclarations.Count(empty) == 0, "count: an empty list counts zero");

        var items = new[] { first, second };
        check(!ObservedListDeclarations.IsEmpty(items), "is_empty: a list with members is not empty");
        check(ObservedListDeclarations.Contains(items, first) && ObservedListDeclarations.Contains(items, second),
            "contains: every member the list holds is found");
        check(!ObservedListDeclarations.Contains(items, new EntityReference("test.list:c", 1, 1)),
            "contains: a reference the list does not hold is not found");
        check(ObservedListDeclarations.Count(items) == 2 && ObservedListDeclarations.Count(new[] { first }) == 1,
            "count: the answer is the list's own length");

        // Membership is the identity the kernel itself compares — the same id from another life is another member —
        // so a list can never answer for an entity it does not hold.
        check(!ObservedListDeclarations.Contains(items, sameIdOtherLife),
            "contains: the same id from another life is not the member the list holds");
        check(ObservedListDeclarations.Contains(new[] { sameIdOtherLife, second }, sameIdOtherLife),
            "contains: a reference is found when the list holds that same life");
        check(!ObservedListDeclarations.IsEmpty(new[] { first, first })
                && ObservedListDeclarations.Contains(new[] { first, first }, first)
                && ObservedListDeclarations.Count(new[] { first, first }) == 2,
            "the answers do not depend on the list being deduplicated or in any order");

        // The family's own table is what the module registers: the two list predicates and the count, all three
        // reading the world their references come from, so a rename, a kind or a role change cannot pass as "some
        // row is published".
        var rows = ObservedListDeclarations.Family.Nodes;
        check(rows.Count == 3
                && rows.Select(row => row.CapabilityId).OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "forge.condition.list.contains", "forge.condition.list.is_empty", "forge.modifier.list.count" })
                && rows.All(row => row.Role == "observe" && row.Execution == "query"
                    && row.Graph.GetProperty("reads")[0].GetString() == "world")
                && rows.Where(row => row.Kind == "condition").Select(row => row.CapabilityId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "forge.condition.list.contains", "forge.condition.list.is_empty" })
                && rows.Single(row => row.Kind == "modifier").CapabilityId == "forge.modifier.list.count",
            "the family publishes the two list predicates and the count as world-reading rows");
    }
}
