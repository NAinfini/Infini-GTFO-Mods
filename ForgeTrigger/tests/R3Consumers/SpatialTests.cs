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
        // Rows the website records apart from `cases` are produced cases too: they are evaluated like any other row, never listed as a gap.
        var recorded = data.GetProperty("unimplemented").EnumerateArray().ToArray();
        check(recorded.All(row => !string.IsNullOrEmpty(row.GetProperty("reason").GetString())), "recorded spatial rows carry their reason");
        var rows = cases.EnumerateArray().Concat(recorded).ToArray();
        var keys = rows.Select(row => row.GetProperty("name").GetString() + "|" + row.GetProperty("parameters").GetRawText()
            + "|" + row.GetProperty("inputs").GetRawText()).ToArray();
        check(keys.Distinct(StringComparer.Ordinal).Count() == keys.Length, "every produced spatial row is evaluated exactly once");
        var index = 0;
        foreach (var row in rows)
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
            var shape = name == "shape_overlap" ? p.GetProperty("shape").GetString() : null;
            // capsule and box take radius and height; the produced half sizes must be exactly those values, as the box reads `extents`.
            if (shape is "capsule" or "box")
            {
                var extents = Vector("extents");
                var radius = input.GetProperty("radius").GetDouble(); var height = input.GetProperty("height").GetDouble();
                check(extents.SequenceEqual(new[] { radius, height / 2d, radius }), "produced half sizes match the observed volume " + index);
            }
            check(p.GetProperty("empty").GetString() == "emit-empty", "spatial fixture uses the identity empty policy " + index);
            IReadOnlyList<EntityReference> Evaluate() => name switch
            {
                "shape_overlap" => ObservedSpatialNodes.Overlap(world.Kernel, candidates, Vector("center"),
                    shape switch
                    {
                        "sphere" => ObservedVolumeShape.Sphere, "cylinder" => ObservedVolumeShape.Cylinder,
                        "capsule" => ObservedVolumeShape.Capsule, "box" => ObservedVolumeShape.Box,
                        var other => throw new InvalidOperationException("Unknown website spatial shape " + other)
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
        VolumeShapes(check);
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
    // capsule and box need samples the shared world does not carry: one past the sphere radius along the axis and one box corner.
    private static void VolumeShapes(Action<bool, string> check)
    {
        var axis = ObservationWorld.At("test.spatial:axis", 0, 3.5, 0);
        var cap = ObservationWorld.At("test.spatial:cap", 0, 3, 0);
        var corner = ObservationWorld.At("test.spatial:corner", 3, 4, 0);
        var beyond = ObservationWorld.At("test.spatial:beyond", 3.001, 4, 0);
        var zero = new double[3];
        using var world = new ObservationWorld(new[] { axis, cap, corner, beyond });
        var k = world.Kernel;
        var candidates = world.Entities.Values.Select(row => row.Ref).ToArray();
        bool Inside(ObservedVolumeShape shape, double radius, double height, EntityReference target)
            => ObservedSpatialNodes.Overlap(k, candidates, zero, shape, radius, height, true).Contains(target);
        check(!Inside(ObservedVolumeShape.Sphere, 3, 8, axis.Ref), "sphere stops at its radius along the axis");
        check(Inside(ObservedVolumeShape.Capsule, 3, 8, axis.Ref), "capsule reaches half its axis beyond the radius");
        check(Inside(ObservedVolumeShape.Capsule, 3, 4, cap.Ref) && !Inside(ObservedVolumeShape.Capsule, 3, 4, axis.Ref),
            "a capsule shorter than its diameter is the sphere of its radius");
        check(Inside(ObservedVolumeShape.Box, 3, 8, corner.Ref) && !Inside(ObservedVolumeShape.Box, 3, 8, beyond.Ref),
            "box half sizes are inclusive per world axis");
        check(!Inside(ObservedVolumeShape.Sphere, 3, 8, corner.Ref) && !Inside(ObservedVolumeShape.Capsule, 3, 8, corner.Ref),
            "box corner lies outside the sphere and the capsule");
        check(Inside(ObservedVolumeShape.Cylinder, 3, 8, axis.Ref), "cylinder covers the axial sample inside its height");
        void Reject(string name, Action action)
        {
            try { action(); check(false, name + " was accepted"); }
            catch (RuntimeContractException error) { check(error.Code == "spatial-parameter", name + ": " + error.Code); }
        }
        Reject("capsule zero radius", () => ObservedSpatialNodes.Overlap(k, candidates, zero, ObservedVolumeShape.Capsule, 0, 8, true));
        Reject("capsule negative height", () => ObservedSpatialNodes.Overlap(k, candidates, zero, ObservedVolumeShape.Capsule, 3, -1, true));
        Reject("box zero radius", () => ObservedSpatialNodes.Overlap(k, candidates, zero, ObservedVolumeShape.Box, 0, 8, true));
        Reject("box non-finite height", () => ObservedSpatialNodes.Overlap(k, candidates, zero, ObservedVolumeShape.Box, 3, double.NaN, true));
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
