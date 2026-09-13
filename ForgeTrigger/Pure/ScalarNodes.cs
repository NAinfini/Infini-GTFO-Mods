using System;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

public enum ScalarOperation { Add, Subtract, Multiply, Divide, Minimum, Maximum, Power }
public enum ScalarComparison { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }
public enum IntervalBoundary { Inclusive, Exclusive }
public enum ScalarRounding { Floor, Ceiling, Nearest, Truncate }

/// <summary>Stateless, dimensionless arithmetic. Graph units, domains and bindings belong to the public Runtime boundary.</summary>
public static class ScalarNodes
{
    public static double Constant(double value) => PureNumbers.Result(PureNumbers.Input(value));

    public static double Binary(ScalarOperation operation, double a, double b)
    {
        PureNumbers.Input(a); PureNumbers.Input(b);
        var value = operation switch
        {
            ScalarOperation.Add => a + b,
            ScalarOperation.Subtract => a - b,
            ScalarOperation.Multiply => a * b,
            ScalarOperation.Divide => b == 0d
                ? throw new RuntimeContractException("pure-division-by-zero", "The divisor must not be zero.") : a / b,
            ScalarOperation.Minimum => Math.Min(a, b),
            ScalarOperation.Maximum => Math.Max(a, b),
            ScalarOperation.Power => Math.Pow(a, b),
            _ => throw new RuntimeContractException("pure-operation", "Unknown scalar operation.")
        };
        return PureNumbers.Result(value);
    }

    public static double Clamp(double value, double minimum, double maximum)
    {
        PureNumbers.Input(value); PureNumbers.Bounds(minimum, maximum);
        return PureNumbers.Result(Math.Max(minimum, Math.Min(maximum, value)));
    }

    public static double Absolute(double value) => PureNumbers.Result(Math.Abs(PureNumbers.Input(value)));

    public static double Round(double value, ScalarRounding mode)
    {
        PureNumbers.Input(value);
        // Website Math.round ties towards +infinity, unlike .NET's default ties-to-even.
        // Do not use Floor(value + 0.5): addition can round large integral inputs up.
        var lower = Math.Floor(value);
        var rounded = mode switch
        {
            ScalarRounding.Floor => lower,
            ScalarRounding.Ceiling => Math.Ceiling(value),
            ScalarRounding.Nearest => value - lower < 0.5d ? lower : lower + 1d,
            ScalarRounding.Truncate => Math.Truncate(value),
            _ => throw new RuntimeContractException("pure-operation", "Unknown rounding mode.")
        };
        return PureNumbers.Result(rounded);
    }

    public static double Lerp(double a, double b, double weight)
    {
        PureNumbers.Input(a); PureNumbers.Input(b); PureNumbers.Probability(weight);
        // All inputs were validated before preserving an identical endpoint.
        if (a == b) return PureNumbers.Result(a);
        // Same weighted-sum order as the authoring preview. No silent weight clamp.
        return PureNumbers.Result(a * (1d - weight) + b * weight);
    }

    public static double SelectValue(bool condition, double whenTrue, double whenFalse)
    {
        // An unselected input must still satisfy its typed finite-number contract.
        PureNumbers.Input(whenTrue); PureNumbers.Input(whenFalse);
        return PureNumbers.Result(condition ? whenTrue : whenFalse);
    }
}

/// <summary>Pure predicates; never reads an entity, infers a missing actor or mutates state.</summary>
public static class PureConditions
{
    public static bool Compare(double a, double b, ScalarComparison operation)
    {
        PureNumbers.Input(a); PureNumbers.Input(b);
        return operation switch
        {
            ScalarComparison.Equal => a == b,
            ScalarComparison.NotEqual => a != b,
            ScalarComparison.Less => a < b,
            ScalarComparison.LessOrEqual => a <= b,
            ScalarComparison.Greater => a > b,
            ScalarComparison.GreaterOrEqual => a >= b,
            _ => throw new RuntimeContractException("pure-operation", "Unknown comparison operation.")
        };
    }

    public static bool InRange(double value, double minimum, double maximum, IntervalBoundary boundary)
    {
        PureNumbers.Input(value); PureNumbers.Bounds(minimum, maximum);
        return boundary switch
        {
            IntervalBoundary.Inclusive => value >= minimum && value <= maximum,
            IntervalBoundary.Exclusive => value > minimum && value < maximum,
            _ => throw new RuntimeContractException("pure-operation", "Unknown interval boundary.")
        };
    }

    public static bool All(bool a, bool b) => a && b;
    public static bool Any(bool a, bool b) => a || b;
    public static bool Not(bool value) => !value;
}

internal static class PureNumbers
{
    internal static double Input(double value)
    {
        if (!double.IsFinite(value))
            throw new RuntimeContractException("pure-invalid-number", "All inputs must be finite numbers.");
        return value;
    }

    internal static double Result(double value)
    {
        if (!double.IsFinite(value))
            throw new RuntimeContractException("pure-nonfinite-result", "Calculation overflowed or has no finite real result.");
        // Match JSON authoring output; negative zero is not a distinct persisted value.
        return value == 0d ? 0d : value;
    }

    internal static void Bounds(double minimum, double maximum)
    {
        Input(minimum); Input(maximum);
        if (minimum > maximum)
            throw new RuntimeContractException("pure-reversed-range", "Minimum must not exceed maximum.");
    }

    internal static void Probability(double value)
    {
        Input(value);
        if (value < 0d || value > 1d)
            throw new RuntimeContractException("pure-weight-range", "Probability/interpolation weight must be in [0, 1].");
    }
}
