using System;
using System.Diagnostics;
using UnityEngine.Profiling;

namespace InfiniTweaks;

// Optional diagnostics consumers own collection. With no subscriber, scopes do no sampling.
public static class Telemetry
{
    public static event Action<string, long, long>? Measurement;
    public static event Action<string, (string Key, string Value)[]>? RecordWritten;
    public static event Action? SnapshotRequested;
    public static void CollectSnapshot() => SnapshotRequested?.Invoke();
    internal static Scope Measure(string name) => new(name, Measurement);
    internal static void Write(string type, params (string Key, string Value)[] fields) => RecordWritten?.Invoke(type, fields);

    internal readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly Action<string, long, long>? _sink;
        private readonly long _ticks, _allocated;
        private readonly bool _profiled;

        internal Scope(string name, Action<string, long, long>? sink)
        {
            _name = name;
            _sink = sink;
            _ticks = sink == null ? 0 : Stopwatch.GetTimestamp();
            _allocated = sink == null ? 0 : GC.GetAllocatedBytesForCurrentThread();
            _profiled = sink != null && Profiler.enabled;
            if (_profiled) Profiler.BeginSample("InfiniTweaks." + name);
        }

        public void Dispose()
        {
            if (_sink == null) return;
            if (_profiled) Profiler.EndSample();
            _sink(_name, Stopwatch.GetTimestamp() - _ticks, GC.GetAllocatedBytesForCurrentThread() - _allocated);
        }
    }
}
