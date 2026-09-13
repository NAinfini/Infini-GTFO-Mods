namespace ForgeTrigger.Pure;

/// <summary>Explicit one-sample seed inputs; no global stream or frame-time randomness.</summary>
public static class SeededNodes
{
    public static bool Chance(double probability, long seed)
    {
        PureNumbers.Probability(probability);
        return new SeededStream(seed).NextUnit() < probability;
    }

    public static double Uniform(double minimum, double maximum, long seed)
    {
        PureNumbers.Bounds(minimum, maximum);
        var stream = new SeededStream(seed);
        // Seed and endpoints are validated even when the interval is degenerate.
        if (minimum == maximum) return PureNumbers.Result(minimum);
        var weight = stream.NextUnit();
        return PureNumbers.Result(minimum * (1d - weight) + maximum * weight);
    }
}
