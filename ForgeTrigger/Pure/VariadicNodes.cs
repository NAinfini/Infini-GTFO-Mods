using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>Ordered 2..32-input computations for the existing 1.1 authoring contracts; no graph executor.</summary>
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
    public static IReadOnlyList<EntityReference> Union(IReadOnlyList<IReadOnlyList<EntityReference>> inputs)
    {
        var snapshots = Snapshot(inputs);
        IReadOnlyList<EntityReference> result = snapshots[0];
        for (var i = 1; i < snapshots.Length; i++) result = ReferenceCollections.Union(result, snapshots[i]);
        return result;
    }
    public static IReadOnlyList<EntityReference> Intersection(IReadOnlyList<IReadOnlyList<EntityReference>> inputs)
    {
        var snapshots = Snapshot(inputs);
        IReadOnlyList<EntityReference> result = snapshots[0];
        for (var i = 1; i < snapshots.Length; i++) result = ReferenceCollections.Intersection(result, snapshots[i]);
        return result;
    }
    private static IReadOnlyList<EntityReference>[] Snapshot(IReadOnlyList<IReadOnlyList<EntityReference>> inputs)
    {
        if (inputs is null) throw new RuntimeContractException("pure-variadic-null", "Input lists are required.");
        Count(inputs.Count);
        var result = new IReadOnlyList<EntityReference>[inputs.Count];
        // Validate every list even when an earlier empty intersection determines the final result.
        for (var i = 0; i < result.Length; i++) result[i] = ReferenceCollections.Distinct(inputs[i]);
        return result;
    }
    private static void Count(int count)
    {
        if (count < MinimumInputs || count > MaximumInputs)
            throw new RuntimeContractException("pure-variadic-count", "Variadic computations require 2..32 ordered inputs.");
    }
}
