using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

internal static class NumericBoundaryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var values = new[] { 0d, -0d, 0.1d, 0.3d, -0.1d, 1e-300, 1e100, -1e100, double.MaxValue, -double.MaxValue };
        foreach (var value in values)
        {
            foreach (var weight in new[] { 0d, 0.1d, 0.2d, 0.3d, 0.5d, 1d })
                check(ScalarNodes.Lerp(value, value, weight) == value, $"equal lerp endpoint {value:R}, {weight}");
            foreach (var seed in new long[] { 0, 1, 15, 23, 42, uint.MaxValue })
                check(SeededNodes.Uniform(value, value, seed) == value, $"degenerate random interval {value:R}, {seed}");
        }
        void Reject(string name, string code, Action action)
        {
            try { action(); check(false, name + " accepted"); }
            catch (RuntimeContractException e) { check(e.Code == code, name + ": " + e.Code); }
        }
        Reject("equal lerp invalid weight", "pure-weight-range", () => ScalarNodes.Lerp(1, 1, 2));
        Reject("equal lerp NaN weight", "pure-invalid-number", () => ScalarNodes.Lerp(1, 1, double.NaN));
        Reject("equal random invalid seed", "pure-seed", () => SeededNodes.Uniform(1, 1, -1));
        Reject("equal random infinite endpoints", "pure-invalid-number", () => SeededNodes.Uniform(double.PositiveInfinity, double.PositiveInfinity, 1));
        check(BitConverter.DoubleToInt64Bits(ScalarNodes.Lerp(-0d, -0d, 0.2)) == 0, "equal lerp normalizes zero");
        check(BitConverter.DoubleToInt64Bits(SeededNodes.Uniform(-0d, -0d, 23)) == 0, "equal random normalizes zero");
    }
}
