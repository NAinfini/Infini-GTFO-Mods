using System;

namespace ForgeDevelopment.Native;

internal readonly record struct FrameSummary(
    int Samples,
    int DroppedSamples,
    int SlowFrames,
    double AverageMilliseconds,
    double MinimumMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaximumMilliseconds);

internal sealed class PerformanceSampleWindow
{
    private readonly double[] _samples;
    private readonly double[] _sorted;
    private int _count;
    private int _dropped;
    private int _slow;
    private double _sum;
    private double _minimum = double.PositiveInfinity, _maximum;

    internal PerformanceSampleWindow(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _samples = new double[capacity];
        _sorted = new double[capacity];
    }

    internal void Add(double milliseconds, double slowThresholdMilliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        _sum += milliseconds;
        _minimum = Math.Min(_minimum, milliseconds);
        _maximum = Math.Max(_maximum, milliseconds);
        if (milliseconds >= slowThresholdMilliseconds) _slow++;
        if (_count < _samples.Length) _samples[_count++] = milliseconds;
        else _dropped++;
    }

    internal bool TryTake(out FrameSummary summary)
    {
        if (_count == 0)
        {
            summary = default;
            return false;
        }

        Array.Copy(_samples, _sorted, _count);
        Array.Sort(_sorted, 0, _count);
        var observed = _count + _dropped;
        summary = new FrameSummary(
            observed,
            _dropped,
            _slow,
            _sum / observed,
            _minimum,
            Percentile(0.50),
            Percentile(0.95),
            Percentile(0.99),
            _maximum);
        _count = _dropped = _slow = 0;
        _sum = 0;
        _minimum = double.PositiveInfinity; _maximum = 0;
        return true;
    }

    private double Percentile(double percentile) =>
        _sorted[Math.Clamp((int)Math.Ceiling(percentile * _count) - 1, 0, _count - 1)];
}
