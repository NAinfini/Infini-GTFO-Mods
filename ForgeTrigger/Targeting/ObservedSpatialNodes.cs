using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

public enum ObservedVolumeShape { Sphere, Cylinder }

/// <summary>Current point-position selection via the public R3 query. No discovery, LOS or collision certificate.</summary>
public static class ObservedSpatialNodes
{
    public static IReadOnlyList<EntityReference> Overlap(RuntimeKernel runtime,
        IReadOnlyList<EntityReference> candidates, IReadOnlyList<double> center,
        ObservedVolumeShape shape, double radius, double height, bool candidatesComplete)
    {
        var point = Point(center); Bounds(radius, 0.000001, 1000); Bounds(height, 0.000001, 2000);
        if (shape is not (ObservedVolumeShape.Sphere or ObservedVolumeShape.Cylinder))
            throw new RuntimeContractException("spatial-shape", "Only point sphere/cylinder selection is implemented.");
        if (!candidatesComplete)
            throw new RuntimeContractException("spatial-candidates-incomplete", "An incomplete candidate set cannot establish all-target coverage.");
        var snapshots = Observe(runtime, candidates);
        return Array.AsReadOnly(snapshots.Where(row => shape == ObservedVolumeShape.Sphere
            ? Distance(row.Position, point) <= radius
            : Horizontal(row.Position, point) <= radius && Delta(row.Position[1], point[1]) <= height / 2d)
            .OrderBy(row => ReferenceCollections.OrderKey(row.Ref), StringComparer.Ordinal).Select(row => row.Ref).ToArray());
    }
    public static ReferenceSelection Nearest(RuntimeKernel runtime, IReadOnlyList<EntityReference> candidates,
        IReadOnlyList<double> center, int count) => OrderedDistance(runtime, candidates, center, count, false);
    public static ReferenceSelection Farthest(RuntimeKernel runtime, IReadOnlyList<EntityReference> candidates,
        IReadOnlyList<double> center, int count) => OrderedDistance(runtime, candidates, center, count, true);
    private static ReferenceSelection OrderedDistance(RuntimeKernel runtime,
        IReadOnlyList<EntityReference> candidates, IReadOnlyList<double> center, int count, bool farthest)
    {
        if (count < 1 || count > 256)
            throw new RuntimeContractException("pure-selection-count", "Selection count must be in [1, 256].");
        var point = Point(center); var snapshots = Observe(runtime, candidates);
        var ranked = snapshots.Select(row => (Row: row, Distance: Distance(row.Position, point))).ToArray();
        var ordered = farthest ? ranked.OrderByDescending(row => row.Distance) : ranked.OrderBy(row => row.Distance);
        var selected = ordered.ThenBy(row => ReferenceCollections.OrderKey(row.Row.Ref), StringComparer.Ordinal)
            .Take(count).Select(row => row.Row.Ref).ToArray();
        return new ReferenceSelection(selected, candidates.Count, snapshots.Count, count);
    }
    public static ReferenceSelection Chain(RuntimeKernel runtime, IReadOnlyList<EntityReference> candidates,
        EntityReference start, int maximumHops, double radius)
    {
        ArgumentNullException.ThrowIfNull(candidates); RuntimeEntityReferences.Validate(start);
        if (maximumHops < 1 || maximumHops > 64)
            throw new RuntimeContractException("spatial-hop-budget", "Chain maximum hops must be in [1, 64].");
        Bounds(radius, 0.000001, 1000);
        if (candidates.Count > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Explicit candidate query exceeds the Runtime limit.");
        var requested = candidates.ToList();
        if (!requested.Contains(start)) requested.Add(start);
        var snapshots = Observe(runtime, requested);
        var origin = snapshots.Single(row => row.Ref == start);
        var remaining = snapshots.Where(row => row.Ref != start).ToList();
        var available = remaining.Count; var selected = new List<EntityReference>();
        var from = origin.Position;
        while (remaining.Count > 0 && selected.Count < maximumHops)
        {
            var next = remaining.Select(row => (Row: row, Distance: Distance(row.Position, from)))
                .OrderBy(row => row.Distance).ThenBy(row => ReferenceCollections.OrderKey(row.Row.Ref), StringComparer.Ordinal).First();
            if (next.Distance > radius) break;
            selected.Add(next.Row.Ref); remaining.Remove(next.Row); from = next.Row.Position;
        }
        return new ReferenceSelection(selected.ToArray(), candidates.Count, available, maximumHops);
    }
    private static IReadOnlyList<RuntimeEntitySnapshot> Observe(RuntimeKernel runtime, IReadOnlyList<EntityReference> candidates)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        // Do not batch around the Runtime per-query or per-tick budget.
        return runtime.InspectEntities(candidates).RequireComplete();
    }
    private static double[] Point(IReadOnlyList<double> point)
    {
        if (point is null || point.Count != 3)
            throw new RuntimeContractException("spatial-point", "Center requires three finite metre coordinates.");
        var copy = new[] { point[0], point[1], point[2] };
        if (copy.Any(value => !double.IsFinite(value)))
            throw new RuntimeContractException("spatial-point", "Center contains a non-finite coordinate.");
        return copy;
    }
    private static void Bounds(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new RuntimeContractException("spatial-parameter", "Invalid or out-of-range spatial parameter.");
    }
    private static double Delta(double a, double b)
    {
        var value = a - b;
        if (!double.IsFinite(value)) throw new RuntimeContractException("spatial-overflow", "Position difference overflowed.");
        return Math.Abs(value);
    }
    internal static double Distance(IReadOnlyList<double> a, IReadOnlyList<double> b)
        => Magnitude(Delta(a[0], b[0]), Delta(a[1], b[1]), Delta(a[2], b[2]));
    private static double Horizontal(IReadOnlyList<double> a, IReadOnlyList<double> b)
        => Magnitude(Delta(a[0], b[0]), 0, Delta(a[2], b[2]));
    private static double Magnitude(double x, double y, double z)
    {
        var scale = Math.Max(x, Math.Max(y, z));
        if (scale == 0) return 0;
        x /= scale; y /= scale; z /= scale;
        var length = scale * Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(length)) throw new RuntimeContractException("spatial-overflow", "Distance overflowed.");
        return length;
    }
}
