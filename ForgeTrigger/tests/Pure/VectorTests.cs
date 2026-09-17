using System;
using System.Linq;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

/// <summary>The two two-point rows the batch registers, exercised through the exact public helpers their handlers
/// call. The cross-language vectors that used to drive a file like this came from the website's pure preview; that
/// preview answers no distance or direction today, so this file keeps the C#-side coverage instead of reading a
/// fixture nothing produces. Measured answers, scaling, the direction's own orientation and both refusals are the
/// row's whole behavior; the ports, units and catalog alignment are checked by the contract suite.</summary>
internal static class VectorTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(string code, Action action)
        {
            try { action(); check(false, "rejected " + code); }
            catch (RuntimeContractException error) { check(error.Code == code, "refused by name: " + code + " [" + error.Code + "]"); }
        }

        var origin = new double[] { 0, 0, 0 };
        check(VectorNodes.Distance(origin, new double[] { 3, 4, 0 }) == 5 && VectorNodes.Distance(origin, new double[] { 0, 0, 2.5 }) == 2.5,
            "distance: the answer is the metre distance between the two positions");
        check(VectorNodes.Distance(new double[] { 1, 1, 1 }, new double[] { 1, 1, 1 }) == 0,
            "distance: one position from itself is zero");
        check(VectorNodes.Distance(origin, new double[] { -3, -4, 0 }) == VectorNodes.Distance(new double[] { -3, -4, 0 }, origin),
            "distance: the answer does not depend on which port is the starting point");

        // The length is scaled before the squares, so a pair of positions near the edge of the range keeps its
        // answer while a pair whose difference has no finite value is refused rather than answered as infinity.
        check(VectorNodes.Distance(origin, new double[] { 1e200, 0, 0 }) == 1e200,
            "distance: a far-apart pair keeps the answer its components still have");
        Reject("pure-nonfinite-result", () => VectorNodes.Distance(origin, new double[] { 1e308, 1e308, 1e308 }));
        Reject("pure-nonfinite-result", () => VectorNodes.Distance(new double[] { 1e308, 0, 0 }, new double[] { -1e308, 0, 0 }));

        var up = VectorNodes.Direction(origin, new double[] { 0, 5, 0 });
        check(up.SequenceEqual(new[] { 0d, 1d, 0d }), "direction: the unit vector points from the start to the end port");
        var diagonal = VectorNodes.Direction(origin, new double[] { 3, 4, 0 });
        check(Math.Abs(diagonal[0] - 0.6) < 1e-12 && Math.Abs(diagonal[1] - 0.8) < 1e-12 && diagonal[2] == 0,
            "direction: the answer is scaled to the unit length of the same point");
        check(Math.Abs(Math.Sqrt(diagonal.Sum(component => component * component)) - 1) < 1e-12,
            "direction: every answer is one unit long");
        var backwards = VectorNodes.Direction(new double[] { 3, 4, 0 }, origin);
        check(backwards[0] == -diagonal[0] && backwards[1] == -diagonal[1],
            "direction: swapping the two ports turns the answer around");

        // Two positions that coincide have no direction: answering a zero vector would hand a consumer a value it
        // would divide by, so the row refuses the step by name instead.
        Reject("pure-zero-vector", () => VectorNodes.Direction(new double[] { 2, 2, 2 }, new double[] { 2, 2, 2 }));
        Reject("pure-nonfinite-result", () => VectorNodes.Direction(new double[] { 1e308, 0, 0 }, new double[] { -1e308, 0, 0 }));

        // The declared rows are the ones the helpers above belong to: both are stateless modifiers of the plan's
        // own ports, so a rename or a role change cannot pass as "some pure row is published".
        var rows = PureModule.Nodes.Where(node => node.CapabilityId.StartsWith("forge.modifier.value.", StringComparison.Ordinal)
            && node.CapabilityId is "forge.modifier.value.distance" or "forge.modifier.value.direction").ToArray();
        check(rows.Length == 2
                && rows.All(node => node.Kind == "modifier"
                    && node.Graph.GetProperty("execution").GetString() == "pure")
                && rows.Select(node => node.HandlerName).OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "trigger.modifier.direction", "trigger.modifier.distance" }),
            "the family publishes the two two-point rows as pure modifiers");
    }
}
