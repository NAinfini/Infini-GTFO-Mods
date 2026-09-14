using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>Ordered 2..32-input computations for the website's whole-side variadic pure contracts; no graph executor.
/// Set union and intersection are binary contracts and live in <see cref="ReferenceCollections"/>.</summary>
public static class VariadicNodes
{
    public const int MinimumInputs = 2;
    public const int MaximumInputs = 32;

    public static double Reduce(ScalarOperation operation, ReadOnlySpan<double> values)
    {
        Count(values.Length);
        if (operation is not (ScalarOperation.Add or ScalarOperation.Multiply or ScalarOperation.Minimum or ScalarOperation.Maximum))
            throw new RuntimeContractException("pure-variadic-operation", "This operation has no variadic contract.");
        foreach (var value in values) PureNumbers.Input(value);
        var result = operation == ScalarOperation.Add ? 0d : operation == ScalarOperation.Multiply ? 1d : values[0];
        foreach (var value in values)
            result = operation switch
            {
                ScalarOperation.Add => result + value,
                ScalarOperation.Multiply => result * value,
                ScalarOperation.Minimum => Math.Min(result, value),
                ScalarOperation.Maximum => Math.Max(result, value),
                _ => throw new InvalidOperationException()
            };
        return PureNumbers.Result(result);
    }
    public static bool All(ReadOnlySpan<bool> values)
    {
        Count(values.Length);
        foreach (var value in values) if (!value) return false;
        return true;
    }
    public static bool Any(ReadOnlySpan<bool> values)
    {
        Count(values.Length);
        foreach (var value in values) if (value) return true;
        return false;
    }
    private static void Count(int count)
    {
        if (count < MinimumInputs || count > MaximumInputs)
            throw new RuntimeContractException("pure-variadic-count", "Variadic computations require 2..32 ordered inputs.");
    }
}
