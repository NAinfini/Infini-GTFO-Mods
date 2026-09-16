using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

/// <summary>The collection algebra the selector rows are built on, exercised directly. The cross-language vectors
/// that used to drive this file came from the website's selector preview; that preview is gone with the selector
/// rows' move to the `query` tier, so the pending cross-language half belongs to the batch that owns those rows and
/// this file keeps the C#-side coverage instead of reading a fixture nothing produces.</summary>
internal static class CollectionTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(string name, string code, Action action)
        {
            try { action(); check(false, name + " unexpectedly accepted"); }
            catch (RuntimeContractException e) { check(e.Code == code, name + ": " + e.Code + " expected " + code); }
            catch (Exception e) { check(false, name + ": unexpected " + e.GetType().Name); }
        }
        var a = new EntityReference("test.entity:a", 1, 1);
        var b = new EntityReference("test.entity:b", 1, 1);
        var sameIdNewLife = a with { LifeEpoch = 2 };
        var input = new[] { b, a, b, sameIdNewLife };
        var before = input.ToArray(); var all = ReferenceCollections.Distinct(input);
        check(input.SequenceEqual(before), "collection input is not mutated");
        check(all.Count == 3 && all.Contains(a) && all.Contains(sameIdNewLife), "full identity survives deduplication");
        var limited = ReferenceCollections.Limit(input, 2);
        check(limited.Selected.SequenceEqual(new[] { b, a }), "limit preserves first input occurrence");
        var shortfall = ReferenceCollections.Random(new[] { a, a }, 42, 5);
        check(shortfall.InputCount == 2 && shortfall.AvailableCount == 1 && shortfall.RequestedCount == 5
            && shortfall.SelectedCount == 1 && shortfall.UnfilledCount == 4, "shortfall is explicit and not full success");
        var empty = ReferenceCollections.Random(Array.Empty<EntityReference>(), 42, 5);
        check(empty.SelectedCount == 0 && empty.UnfilledCount == 5, "empty is explicit");
        input[0] = a with { Id = "test.entity:changed" };
        check(limited.Selected[0] == b && all.Contains(b), "output not backed by caller array");
        try { ((IList<EntityReference>)limited.Selected)[0] = a; check(false, "output was mutable"); }
        catch (NotSupportedException) { check(true, "selection output is read-only"); }
        Reject("null list", "pure-collection-null", () => ReferenceCollections.Distinct(null!));
        Reject("null entry", "pure-reference-null", () => ReferenceCollections.Distinct(new EntityReference[] { null! }));
        Reject("control ID follows SDK", "invalid-string", () => ReferenceCollections.Distinct(new[] { a with { Id = "test.entity:a\nb" } }));
        Reject("unpaired surrogate", "pure-reference-encoding", () => ReferenceCollections.Distinct(new[] { a with { Id = "test.entity:\ud800" } }));
        Reject("empty validates seed", "pure-seed", () => ReferenceCollections.Shuffle(Array.Empty<EntityReference>(), -1));
        Reject("singleton validates seed", "pure-seed", () => ReferenceCollections.Random(new[] { a }, long.MaxValue, 1));
        Reject("invalid trailing reference", "invalid-integer", () => ReferenceCollections.Limit(new[] { a, b with { LifeEpoch = -1 } }, 1));
        Reject("duplicate input budget", "pure-collection-budget", () => ReferenceCollections.Distinct(Enumerable.Repeat(a, 4097).ToArray()));
        var large = Enumerable.Range(0, 4096).Select(i => a with { Id = "test.entity:" + i }).ToArray();
        check(ReferenceCollections.Distinct(large).Count == 4096, "pure collection exact upper bound");
        Reject("union output budget", "pure-collection-output-budget", () => ReferenceCollections.Union(large, new[] { a }));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var refs = new[] { a, a with { LifeEpoch = 10 }, a with { LifeEpoch = 100 }, sameIdNewLife };
            check(ReferenceCollections.Distinct(refs).Select(x => x.LifeEpoch).SequenceEqual(new long[] { 100, 10, 1, 2 }), "JSON ordinal order independent of culture");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        for (var seed = 0; seed < 32; seed++)
        {
            var refs = new[] { a, b, sameIdNewLife }; var permutation = ReferenceCollections.Shuffle(refs, seed);
            check(permutation.SequenceEqual(ReferenceCollections.Shuffle(refs.AsEnumerable().Reverse().Concat(refs).ToArray(), seed)), "shuffle input order independent " + seed);
            check(ReferenceCollections.Random(refs, seed, 2).Selected.SequenceEqual(permutation.Take(2)), "random is full permutation prefix " + seed);
            check(permutation.Distinct().Count() == refs.Length, "shuffle is without replacement " + seed);
        }
    }
}
