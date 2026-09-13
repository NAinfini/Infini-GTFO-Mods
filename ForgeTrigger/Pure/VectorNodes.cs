using System;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>Double-precision metre vectors, in one caller-specified coordinate space. No implicit unit or space conversion.</summary>
public static class VectorNodes
{
    public static double[] ComposeMetres(double x, double y, double z)
        => new[] { ScalarNodes.Constant(x), ScalarNodes.Constant(y), ScalarNodes.Constant(z) };

    public static double[] AddMetres(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        Validate(a); Validate(b);
        return new[] { PureNumbers.Result(a[0] + b[0]), PureNumbers.Result(a[1] + b[1]), PureNumbers.Result(a[2] + b[2]) };
    }

    public static double[] ScaleMetres(ReadOnlySpan<double> vector, double factor)
    {
        Validate(vector); PureNumbers.Input(factor);
        return new[] { PureNumbers.Result(vector[0] * factor), PureNumbers.Result(vector[1] * factor), PureNumbers.Result(vector[2] * factor) };
    }

    private static void Validate(ReadOnlySpan<double> value)
    {
        if (value.Length != 3)
            throw new RuntimeContractException("pure-vector-shape", "A metre vector has exactly three components.");
        foreach (var component in value) PureNumbers.Input(component);
    }
}
