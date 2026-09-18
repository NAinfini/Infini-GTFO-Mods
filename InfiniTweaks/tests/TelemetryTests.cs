using InfiniTweaks;
using UnityEngine.Profiling;

// The collector half of the optional telemetry contract. ForgeDevelopment's bridge test covers the consumer half
// against the same public surface; NativeContracts pins that surface in the built assembly.
internal static class TelemetryTests
{
    internal static void Run(Action<bool, string> check)
    {
        Profiler.enabled = true;
        Profiler.Reads = Profiler.Begins = Profiler.Ends = 0;
        using (Telemetry.Measure("unobserved")) { }
        check(Profiler.Reads == 0 && Profiler.Begins == 0, "A scope with no subscriber neither samples nor queries the profiler.");

        var measured = new List<(string Name, long Ticks, long Allocated)>();
        var written = new List<(string Type, (string Key, string Value)[] Fields)>();
        var snapshots = 0;
        Action<string, long, long> onMeasure = (name, ticks, allocated) => measured.Add((name, ticks, allocated));
        Action<string, (string Key, string Value)[]> onWrite = (type, fields) => written.Add((type, fields));
        Action onSnapshot = () => { snapshots++; Telemetry.Write("marker_summary", ("active", "3")); };
        Telemetry.Measurement += onMeasure;
        Telemetry.RecordWritten += onWrite;
        Telemetry.SnapshotRequested += onSnapshot;
        try
        {
            using (Telemetry.Measure("ResourceHud")) { }
            check(measured.Count == 1 && measured[0].Name == "ResourceHud" && measured[0].Ticks >= 0 && measured[0].Allocated >= 0,
                "An observed scope reports its name, elapsed ticks and allocation once.");
            check(Profiler.Begins == 1 && Profiler.Ends == 1, "An observed scope opens and closes one profiler sample.");
            Telemetry.CollectSnapshot();
            check(snapshots == 1 && written.Count == 1 && written[0].Type == "marker_summary" && written[0].Fields[0] == ("active", "3"),
                "A snapshot request reaches the collector and its record reaches the consumer.");
        }
        finally
        {
            Telemetry.Measurement -= onMeasure;
            Telemetry.RecordWritten -= onWrite;
            Telemetry.SnapshotRequested -= onSnapshot;
            Profiler.enabled = false;
        }
    }
}
