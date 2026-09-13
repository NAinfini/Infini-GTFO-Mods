using ForgeDevelopment.Native;
using InfiniTweaks;

int checks = 0, snapshots = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
Telemetry.SnapshotRequested += Snapshot;
void Snapshot() { snapshots++; Telemetry.Write("marker_summary", ("active", "3")); }
TelemetryBridge.Attach(null);
using (Telemetry.Measure("disabled")) { }
TelemetryBridge.Snapshot();
Check(PerformanceDiagnostics.Measurements == 0 && snapshots == 0, "Standalone collector must not require QOL.");
Check(UnityEngine.Profiling.Profiler.Reads == 0, "Absent consumer must not query native profiler.");

for (var i = 0; i < 25; i++)
{
    TelemetryBridge.Attach(typeof(Telemetry));
    TelemetryBridge.Attach(typeof(Telemetry));
    using (Telemetry.Measure("ResourceHud")) { }
    TelemetryBridge.Snapshot();
    Check(PerformanceDiagnostics.Measurements == i + 1 && snapshots == i + 1 && PerformanceDiagnostics.Records == i + 1, "Repeated attach must keep one subscription.");
    TelemetryBridge.Detach();
    using (Telemetry.Measure("detached")) { }
    TelemetryBridge.Snapshot();
    Check(PerformanceDiagnostics.Measurements == i + 1 && snapshots == i + 1, "Detach must release callbacks.");
}
Check(PerformanceDiagnostics.LastName == "ResourceHud" && PerformanceDiagnostics.Ticks >= 0 && PerformanceDiagnostics.Allocated >= 0, "Measurement values must be forwarded unchanged.");
Check(PerformanceDiagnostics.LastType == "marker_summary" && PerformanceDiagnostics.LastValue == "3", "Snapshot fields must cross optional contract.");
TelemetryBridge.Attach(typeof(Telemetry));
UnityEngine.Profiling.Profiler.enabled = true;
using (Telemetry.Measure("profile")) { }
Check(UnityEngine.Profiling.Profiler.Begins == 1 && UnityEngine.Profiling.Profiler.Ends == 1, "Native profiling scopes must balance.");
TelemetryBridge.Detach();
bool failed = false;
try { TelemetryBridge.Attach(typeof(string)); } catch (MissingMemberException) { failed = true; }
Check(failed, "Invalid contract must fail explicitly.");
TelemetryBridge.Snapshot();
Telemetry.SnapshotRequested -= Snapshot;
Console.WriteLine($"PASS: {checks} optional telemetry lifecycle checks.");

namespace ForgeDevelopment.Native
{
    internal static class PerformanceDiagnostics
    {
        internal static int Measurements, Records;
        internal static long Ticks, Allocated;
        internal static string LastName = "", LastType = "", LastValue = "";
        internal static void Record(string name, long ticks, long allocated)
        { Measurements++; LastName = name; Ticks = ticks; Allocated = allocated; }
        internal static void Write(string type, params (string Key, string Value)[] fields)
        { Records++; LastType = type; LastValue = fields[0].Value; }
    }
}
namespace UnityEngine.Profiling
{
    public static class Profiler
    {
        private static bool _enabled;
        public static int Reads, Begins, Ends;
        public static bool enabled { get { Reads++; return _enabled; } set => _enabled = value; }
        public static void BeginSample(string name) => Begins++;
        public static void EndSample() => Ends++;
    }
}
