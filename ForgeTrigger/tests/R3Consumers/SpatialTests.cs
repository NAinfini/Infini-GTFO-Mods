using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Targeting;

internal static class SpatialTests
{
    internal static void Run(string referencePath, Action<bool, string> check)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(referencePath));
        var data = document.RootElement;
        check(data.GetProperty("kind").GetString() == "test-only-spatial-vectors", "spatial fixture provenance");
        check(!data.GetProperty("gameVerified").GetBoolean(), "spatial fixtures are not game evidence");
        var cases = data.GetProperty("cases");
        check(cases.GetArrayLength() > 0 && cases.GetArrayLength() == data.GetProperty("caseCount").GetInt32(), "every website spatial case is exercised");
        // Website shapes the C# observed volume does not implement are listed, never evaluated as passing.
        foreach (var gap in data.GetProperty("unimplemented").EnumerateArray())
        {
            var shape = gap.GetProperty("parameters").GetProperty("shape").GetString()!;
            check(!Enum.GetNames<ObservedVolumeShape>().Any(name => string.Equals(name, shape, StringComparison.OrdinalIgnoreCase)),
                "listed spatial gap is still unimplemented: " + shape);
        }
        var index = 0;
        foreach (var row in cases.EnumerateArray())
        {
            using var world = ObservationWorld.FromJson(data.GetProperty("world"));
            var input = row.GetProperty("inputs"); var p = row.GetProperty("parameters");
            var name = row.GetProperty("name").GetString();
            // shape_overlap queries the whole world sample; C# needs that set supplied explicitly and marked complete.
            var candidates = (name == "shape_overlap" ? data.GetProperty("world").GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("ref"))
                : input.GetProperty("candidates").EnumerateArray()).Select(RuntimeJson.Entity).ToArray();
            var original = candidates.ToArray();
            var expected = row.GetProperty("expected").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
            double[] Vector(string key) => input.GetProperty(key).EnumerateArray().Select(v => v.GetDouble()).ToArray();
            // C# ranks around a point; the website anchor entity is resolved through the same R3 observation first.
            double[] Anchor() => ObservedEntityNodes.Entity(world.Kernel, RuntimeJson.Entity(input.GetProperty("anchor"))).Position.ToArray();
            int MaxTargets() => input.GetProperty("max_targets").GetInt32();
            check(p.GetProperty("empty").GetString() == "emit-empty", "spatial fixture uses the identity empty policy " + index);
            IReadOnlyList<EntityReference> Evaluate() => name switch
            {
                "shape_overlap" => ObservedSpatialNodes.Overlap(world.Kernel, candidates, Vector("center"),
                    p.GetProperty("shape").GetString() switch
                    {
                        "sphere" => ObservedVolumeShape.Sphere, "cylinder" => ObservedVolumeShape.Cylinder,
                        var shape => throw new InvalidOperationException("Unimplemented spatial shape " + shape)
                    },
                    input.GetProperty("radius").GetDouble(), input.GetProperty("height").GetDouble(), true),
                "nearest" => ObservedSpatialNodes.Nearest(world.Kernel, candidates, Anchor(), MaxTargets()).Selected,
                "farthest" => ObservedSpatialNodes.Farthest(world.Kernel, candidates, Anchor(), MaxTargets()).Selected,
                "chain" => ObservedSpatialNodes.Chain(world.Kernel, candidates, RuntimeJson.Entity(input.GetProperty("origin")),
                    input.GetProperty("hops").GetInt32(), input.GetProperty("radius").GetDouble()).Selected,
                _ => throw new InvalidOperationException("Unknown spatial fixture")
            };
            try
            {
                var actual = Evaluate();
                check(actual.SequenceEqual(expected), "spatial cross-language case " + index);
                check(Evaluate().SequenceEqual(actual), "spatial duplicate evaluation " + index);
                check(candidates.SequenceEqual(original), "spatial inputs unchanged " + index);
                check(world.Kernel.QueuedEvents == 0 && world.Kernel.LoadedPlans == 0, "spatial selection is not a side effect " + index);
            }
            catch (Exception error) { check(false, "spatial case " + index + ": " + error.Message); }
            index++;
        }
        Boundaries(check);
    }
    private static void Boundaries(Action<bool, string> check)
    {
        void Reject(string name, string code, Action action)
        {
            try { action(); check(false, name + " was accepted"); }
            catch (RuntimeContractException error) { check(error.Code == code, name + ": " + error.Code); }
        }
        var origin = ObservationWorld.At("test.spatial:origin");
        var edge = ObservationWorld.At("test.spatial:edge", 3, 4, 0);
        var zero = new double[3];
        using var world = new ObservationWorld(new[] { origin, edge });
        var k = world.Kernel; var refs = new[] { edge.Ref };
        Reject("incomplete candidates", "spatial-candidates-incomplete", () => ObservedSpatialNodes.Overlap(k, refs, zero, ObservedVolumeShape.Sphere, 5, 2, false));
        Reject("unknown shape", "spatial-shape", () => ObservedSpatialNodes.Overlap(k, refs, zero, (ObservedVolumeShape)999, 5, 2, true));
        foreach (var radius in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, 1001d })
            Reject("invalid radius", "spatial-parameter", () => ObservedSpatialNodes.Overlap(k, refs, zero, ObservedVolumeShape.Sphere, radius, 2, true));
        foreach (var center in new[] { Array.Empty<double>(), new[] { 0d, 0d }, new[] { double.NaN, 0d, 0d } })
            Reject("invalid center", "spatial-point", () => ObservedSpatialNodes.Nearest(k, refs, center, 1));
        Reject("nearest count", "pure-selection-count", () => ObservedSpatialNodes.Nearest(k, refs, zero, 0));
        Reject("chain hops", "spatial-hop-budget", () => ObservedSpatialNodes.Chain(k, refs, origin.Ref, 65, 1));
        check(world.Observations == 0, "invalid spatial parameters never query native observers");
        var selection = ObservedSpatialNodes.Nearest(k, new[] { edge.Ref, edge.Ref }, zero, 5);
        check(selection.SelectedCount == 1 && selection.UnfilledCount == 4 && selection.InputCount == 2, "spatial shortfall receipt");
        check(selection.AvailableCount == 1, "spatial receiver identity deduplicated once");
        check(ObservedSpatialNodes.Overlap(k, refs, zero, ObservedVolumeShape.Sphere, 5, 2, true).Count == 1, "inclusive sphere boundary");
        check(ObservedSpatialNodes.Overlap(k, refs, zero, ObservedVolumeShape.Cylinder, 3, 8, true).Count == 1, "inclusive cylinder height boundary");
        check(ObservedSpatialNodes.Overlap(k, refs, zero, ObservedVolumeShape.Cylinder, 3, 7.999, true).Count == 0, "outside cylinder height");
        var oldResult = selection.Selected.ToArray();
        world.Entities[edge.Ref.Id] = ObservationWorld.At(edge.Ref.Id, 10, 0, 0);
        check(ObservedSpatialNodes.Overlap(k, refs, zero, ObservedVolumeShape.Sphere, 5, 2, true).Count == 0, "position is freshly observed");
        check(selection.Selected.SequenceEqual(oldResult), "earlier selection is an immutable snapshot");
        world.Entities[edge.Ref.Id] = ObservationWorld.At(edge.Ref.Id, life: 2);
        Reject("stale life", "entity-query-incomplete", () => ObservedSpatialNodes.Nearest(k, refs, zero, 1));
        Reject("incomplete query cannot silently filter", "entity-query-incomplete", () => ObservedSpatialNodes.Overlap(k, new[] { origin.Ref, edge.Ref }, zero, ObservedVolumeShape.Sphere, 5, 2, true));
        Reject("raw input budget", "entity-query-budget", () => ObservedSpatialNodes.Nearest(k, Enumerable.Repeat(origin.Ref, 257).ToArray(), zero, 1));
        Reject("chain includes start in shared query budget", "entity-query-budget", () => ObservedSpatialNodes.Chain(k, Enumerable.Repeat(edge.Ref, 256).ToArray(), origin.Ref, 1, 1));
        var chain = ObservedSpatialNodes.Chain(k, new[] { origin.Ref, origin.Ref }, origin.Ref, 4, 5);
        check(chain.SelectedCount == 0 && chain.UnfilledCount == 4, "chain never selects its own start");
        OverflowAndBudget(check);
    }
    private static void OverflowAndBudget(Action<bool, string> check)
    {
        var far = ObservationWorld.At("test.spatial:far", 1e200, 1e200, 0);
        using var world = new ObservationWorld(new[] { far });
        var k = world.Kernel; var refs = new[] { far.Ref }; var zero = new double[3];
        check(ObservedSpatialNodes.Nearest(k, refs, zero, 1).SelectedCount == 1, "distance avoids squaring overflow");
        world.Entities[far.Ref.Id] = ObservationWorld.At(far.Ref.Id, double.MaxValue, 0, 0);
        try
        {
            ObservedSpatialNodes.Nearest(k, refs, new[] { -double.MaxValue, 0d, 0d }, 1);
            check(false, "position difference overflow accepted");
        }
        catch (RuntimeContractException error) { check(error.Code == "spatial-overflow", "position difference overflow rejects"); }
        k.Advance(1, true);
        for (var i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick; i++)
            ObservedSpatialNodes.Nearest(k, refs, zero, 1);
        k.Advance(1, true);
        try { ObservedSpatialNodes.Nearest(k, refs, zero, 1); check(false, "same-tick spatial budget reset"); }
        catch (RuntimeContractException error) { check(error.Code == "entity-query-tick-budget", "spatial nodes share the Runtime budget"); }
        k.Advance(2, true);
        check(ObservedSpatialNodes.Nearest(k, refs, zero, 1).SelectedCount == 1, "new tick resumes spatial queries");
        k.BeginWorld(2); k.Advance(0, true);
        try { ObservedSpatialNodes.Nearest(k, refs, zero, 1); check(false, "old-world spatial reference accepted"); }
        catch (RuntimeContractException error) { check(error.Code == "entity-query-incomplete", "old-world references rejected"); }
        check(k.QueuedEvents == 0, "spatial queries never create scheduled work");
    }
}
