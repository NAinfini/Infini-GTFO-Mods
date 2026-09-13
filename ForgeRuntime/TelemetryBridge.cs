using System;
using System.Reflection;

namespace ForgeRuntime;

// Resolve only the optional metrics contract; neither plugin references the other's assembly.
internal static class TelemetryBridge
{
    private static EventInfo? _measurement, _record;
    private static Action? _snapshot;
    private static readonly Action<string, long, long> OnMeasurement = PerformanceDiagnostics.Record;
    private static readonly Action<string, (string Key, string Value)[]> OnRecord = PerformanceDiagnostics.Write;

    internal static void Attach(Type? telemetry)
    {
        Detach();
        if (telemetry == null) return;
        try
        {
            _measurement = telemetry.GetEvent("Measurement") ?? throw new MissingMemberException(telemetry.FullName, "Measurement");
            _record = telemetry.GetEvent("RecordWritten") ?? throw new MissingMemberException(telemetry.FullName, "RecordWritten");
            _snapshot = (Action)(telemetry.GetMethod("CollectSnapshot") ?? throw new MissingMethodException(telemetry.FullName, "CollectSnapshot")).CreateDelegate(typeof(Action));
            _measurement.AddEventHandler(null, OnMeasurement);
            _record.AddEventHandler(null, OnRecord);
        }
        catch
        {
            Detach();
            throw;
        }
    }

    internal static void Snapshot() => _snapshot?.Invoke();

    internal static void Detach()
    {
        _measurement?.RemoveEventHandler(null, OnMeasurement);
        _record?.RemoveEventHandler(null, OnRecord);
        _measurement = _record = null;
        _snapshot = null;
    }
}
