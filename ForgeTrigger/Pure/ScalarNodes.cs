using System;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

public enum ScalarOperation { Add, Subtract, Multiply, Divide, Minimum, Maximum }
public enum ScalarComparison { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }
public enum ScalarRounding { Floor, Ceiling, Nearest, Truncate }
/// <summary>The website divide node's structural zero_policy.</summary>
public enum DivisionZeroPolicy { Reject, Zero, Passthrough }

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
            ScalarOperation.Divide => Divide(a, b, DivisionZeroPolicy.Reject),
            ScalarOperation.Minimum => Math.Min(a, b),
            ScalarOperation.Maximum => Math.Max(a, b),
            _ => throw new RuntimeContractException("pure-operation", "Unknown scalar operation.")
        };
        return PureNumbers.Result(value);
    }

    public static double Divide(double a, double b, DivisionZeroPolicy policy)
    {
        PureNumbers.Input(a); PureNumbers.Input(b);
        if (b != 0d) return PureNumbers.Result(a / b);
        return policy switch
        {
            DivisionZeroPolicy.Reject => throw new RuntimeContractException("pure-division-by-zero", "The divisor must not be zero."),
            DivisionZeroPolicy.Zero => 0d,
            // The dividend passes through unchanged.
            DivisionZeroPolicy.Passthrough => PureNumbers.Result(a),
            _ => throw new RuntimeContractException("pure-operation", "Unknown division zero policy.")
        };
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
}

/// <summary>Pure predicates; never reads an entity, infers a missing actor or mutates state.</summary>
public static class PureConditions
{
    /// <summary>The value classes the catalog's parameterized comparison orders, in the parameter's own member
    /// order (`value_type` in `Tools/Forge/capability-rules-query.ts`, whose inline list this repeats because the
    /// compiled constant indexes it).</summary>
    public static readonly string[] ValueTypes = { "number", "integer", "string", "entity" };

    /// <summary>The catalog's comparison member names, in the order the C# enum declares them, which is the order
    /// the `compare_operator` set carries.</summary>
    public static readonly string[] Operators = { "eq", "ne", "lt", "lte", "gt", "gte" };

    /// <summary>Website compare: tolerance widens equality and shifts the ordered comparisons towards acceptance.</summary>
    public static bool Compare(double left, double right, ScalarComparison operation, double tolerance)
    {
        PureNumbers.Input(left); PureNumbers.Input(right); PureNumbers.Input(tolerance);
        if (tolerance < 0d)
            throw new RuntimeContractException("pure-tolerance-range", "Comparison tolerance must not be negative.");
        return operation switch
        {
            ScalarComparison.Equal => Math.Abs(left - right) <= tolerance,
            ScalarComparison.NotEqual => Math.Abs(left - right) > tolerance,
            ScalarComparison.Less => left < right - tolerance,
            ScalarComparison.LessOrEqual => left <= right + tolerance,
            ScalarComparison.Greater => left > right + tolerance,
            ScalarComparison.GreaterOrEqual => left >= right - tolerance,
            _ => throw new RuntimeContractException("pure-operation", "Unknown comparison operation.")
        };
    }

    /// <summary>The enum comparison the catalog's `forge.condition.enum.compare` row performs: both operands are
    /// members of the one set the row's structural parameter chose, so the answer is equality between the member
    /// names the frame resolved the compiled values back to. A value the author left unwritten (<c>null</c>) equals
    /// nothing: an empty value is no member of the set, which is why the row's nullable port turns a missing value
    /// into `false` instead of comparing it against the set's first member.</summary>
    public static bool EnumEquals(string? value, string member)
        => value != null && string.Equals(value, member, StringComparison.Ordinal);

    /// <summary>The typed comparison the catalog's `forge.condition.predicate.compare` row performs: one row for
    /// the four classes its structural `value_type` parameter offers, so `g-compare` is no longer one row per
    /// type. `valueType` is the member the plan compiled, `<c>null</c>` when the author left the optional
    /// parameter unwritten, which resolves to the first member exactly as the contract does.
    ///
    /// The numbers use <see cref="Compare(double,double,ScalarComparison,double)"/>, so tolerance keeps its one
    /// definition. Text compares by ordinal rank, which is what makes `eq` byte equality and the ordering a total
    /// order; entities compare by identity (id, world epoch, life epoch), which is the identity every other row of
    /// this vocabulary reads them by. A tolerance a non-numeric member cannot honour, a value of another class, and
    /// an unknown operator or member are all refused rather than answered as `false`, because a comparison that
    /// could not be made must not read like a world in which the answer is no.</summary>
    public static bool Compare(JsonElement left, JsonElement right, ScalarComparison operation, double? tolerance,
        string? valueType)
    {
        var member = valueType ?? ValueTypes[0];
        if (!ValueTypes.Contains(member, StringComparer.Ordinal))
            throw new RuntimeContractException("pure-operation", "Unknown value type member: " + member);
        if (member == "entity")
        {
            RequireNoTolerance(tolerance);
            var first = RuntimeJson.Entity(left); var second = RuntimeJson.Entity(right);
            var order = string.Equals(first.Id, second.Id, StringComparison.Ordinal) ? first.WorldEpoch.CompareTo(second.WorldEpoch)
                : string.CompareOrdinal(first.Id, second.Id);
            if (order == 0) order = first.LifeEpoch.CompareTo(second.LifeEpoch);
            return Ordered(order, operation);
        }
        if (member == "string")
        {
            RequireNoTolerance(tolerance);
            return Ordered(string.CompareOrdinal(Text(left), Text(right)), operation);
        }
        var leftNumber = Number(left); var rightNumber = Number(right);
        // An integer comparison answers about counts, so a non-integral operand is refused rather than truncated:
        // the frame would otherwise accept a value its own contract says the port never carries.
        if (member == "integer" && (leftNumber != Math.Truncate(leftNumber) || rightNumber != Math.Truncate(rightNumber)))
            throw new RuntimeContractException("pure-operation", "An integer comparison requires integral operands.");
        return Compare(leftNumber, rightNumber, operation, tolerance ?? 0d);
    }

    private static bool Ordered(int order, ScalarComparison operation) => operation switch
    {
        ScalarComparison.Equal => order == 0,
        ScalarComparison.NotEqual => order != 0,
        ScalarComparison.Less => order < 0,
        ScalarComparison.LessOrEqual => order <= 0,
        ScalarComparison.Greater => order > 0,
        ScalarComparison.GreaterOrEqual => order >= 0,
        _ => throw new RuntimeContractException("pure-operation", "Unknown comparison operation.")
    };

    private static void RequireNoTolerance(double? tolerance)
    {
        if (tolerance != null)
            throw new RuntimeContractException("pure-operation", "Only a numeric comparison carries a tolerance.");
    }

    private static double Number(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? PureNumbers.Input(number)
            : throw new RuntimeContractException("pure-operation", "A numeric comparison requires numeric operands.");

    private static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new RuntimeContractException("pure-operation", "A text comparison requires text operands.");

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
}
