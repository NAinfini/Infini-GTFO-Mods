using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Process = System.Diagnostics.Process;
using Object = UnityEngine.Object;

namespace ForgeDevelopment.Native;

internal static partial class Settings
{
    internal static ConfigEntry<bool> PerformanceLogging = null!, AutoProfilerCapture = null!, SnapshotAssetsOnLevelStart = null!;
    internal static ConfigEntry<float> PerformanceSummarySeconds = null!, HitchThresholdMilliseconds = null!, ProfilerCaptureSeconds = null!;
    internal static ConfigEntry<int> MaxLoggedAssets = null!;
    internal static ConfigEntry<KeyCode> PerformanceSnapshotKey = null!;

    internal static void Bind(ConfigFile config)
    {
        PerformanceLogging = config.Bind("Performance Diagnostics", "EnablePerformanceLogging", true,
            "Continuously collect low-overhead frame, CPU, GPU-timing, memory, GC and Unity sampler summaries. Writes structured logs under BepInEx/PerformanceLogs. Restart required.");
        PerformanceSummarySeconds = config.Bind("Performance Diagnostics", "SummaryIntervalSeconds", 1f,
            new ConfigDescription("How often frame and resource summaries are written. Frames are sampled in memory; the file is written by a background thread.", new AcceptableValueRange<float>(0.25f, 10f)));
        HitchThresholdMilliseconds = config.Bind("Performance Diagnostics", "HitchThresholdMilliseconds", 20f,
            new ConfigDescription("A frame at or above this duration is counted as slow. 20 ms is 50 FPS; a sustained drop from 120 FPS to 40 FPS produces about 25 ms frames.", new AcceptableValueRange<float>(8f, 200f)));
        AutoProfilerCapture = config.Bind("Performance Diagnostics", "AutoCaptureUnityProfilerOnHitch", true,
            "After three consecutive slow gameplay frames, record a short native Unity .raw profiler trace when the retail player supports it. Allocation call stacks stay disabled to limit profiler overhead.");
        ProfilerCaptureSeconds = config.Bind("Performance Diagnostics", "UnityProfilerCaptureSeconds", 10f,
            new ConfigDescription("Length of automatic or manual native Unity profiler captures.", new AcceptableValueRange<float>(2f, 60f)));
        SnapshotAssetsOnLevelStart = config.Bind("Performance Diagnostics", "SnapshotAssetsOnLevelStart", true,
            "Five seconds after entering a level, inventory loaded textures/meshes/materials/audio and components in loaded scenes including the diagnostics object's persistent scene. Scene objects are walked cooperatively rather than globally enumerating components repeatedly; unattached prefab components are excluded. Cooperative budget is 2 ms, not a hard cap: native asset/root enumeration cannot be interrupted. Atomic calls and slow stages are logged.");
        MaxLoggedAssets = config.Bind("Performance Diagnostics", "MaxAssetsPerType", 20,
            new ConfigDescription("Number of largest textures, meshes, materials, audio clips and skinned meshes written per snapshot.", new AcceptableValueRange<int>(5, 100)));
        PerformanceSnapshotKey = config.Bind("Performance Diagnostics", "ManualSnapshotKey", KeyCode.F9,
            "Take an asset snapshot and begin a native profiler capture. Use this immediately before reproducing a slowdown.");
        foreach (var entry in new[] { PerformanceSummarySeconds, HitchThresholdMilliseconds, ProfilerCaptureSeconds })
        {
            void Validate()
            {
                if (float.IsFinite(entry.Value)) return;
                Plugin.PluginLog.LogWarning($"{entry.Definition.Key} must be finite; restoring its default.");
                entry.Value = (float)entry.DefaultValue;
            }
            entry.SettingChanged += (_, _) => Validate();
            Validate();
        }
    }
}

internal readonly struct PerformanceScope : IDisposable
{
    private readonly string _section;
    private readonly long _started;
    private readonly long _allocated;
    private readonly bool _active;
    private readonly bool _profiled;

    internal PerformanceScope(string section)
    {
        _section = section;
        _active = PerformanceDiagnostics.Enabled;
        _started = _active ? Stopwatch.GetTimestamp() : 0;
        _allocated = _active ? GC.GetAllocatedBytesForCurrentThread() : 0;
        _profiled = _active && PerformanceDiagnostics.ProfilerCaptureActive;
        if (_profiled)
            Profiler.BeginSample("ForgeDevelopment." + section);
    }

    public void Dispose()
    {
        if (!_active) return;
        if (_profiled) Profiler.EndSample();
        PerformanceDiagnostics.Record(_section, Stopwatch.GetTimestamp() - _started, GC.GetAllocatedBytesForCurrentThread() - _allocated);
    }
}

public sealed class PerformanceMonitor : MonoBehaviour
{
    public PerformanceMonitor(IntPtr pointer) : base(pointer) { }
    public void Start()
    {
        try { PerformanceDiagnostics.Start(gameObject.scene); }
        catch (Exception error)
        {
            PerformanceDiagnostics.Stop();
            Plugin.PluginLog.LogError($"Diagnostics startup failed: {error}");
            enabled = false;
        }
    }
    public void Update()
    {
        using var _ = PerformanceDiagnostics.Measure("Diagnostics");
        PerformanceDiagnostics.Tick();
    }
    public void OnApplicationQuit() => PerformanceDiagnostics.Stop();
    public void OnDestroy() => PerformanceDiagnostics.Stop();
}

internal static class PerformanceDiagnostics
{
    private sealed class NativeSampler
    {
        internal readonly string Name;
        internal readonly Recorder Recorder;
        internal long SumNanoseconds, MaxNanoseconds, Blocks;
        internal int Reads;
        internal bool Failed;
        internal NativeSampler(string name, Recorder recorder) { Name = name; Recorder = recorder; }
    }

    private readonly record struct AssetCost(string Name, long Bytes, string Detail);
    private readonly record struct RendererCost(string Name, string Kind, int Vertices, ulong Indices, int Materials, string Detail);
    private readonly record struct SceneDetail(string Name, long Weight, string Detail);
    private readonly record struct SkinnedCost(string Name, int Vertices, string Detail);
    private readonly record struct MemorySnapshot(long Allocated, long Reserved, long Unused, long ManagedHeap, long ManagedUsed, long GraphicsDriver, long DotNetManaged);
    private readonly record struct TextureStreamingSnapshot(ulong Total, ulong Desired, ulong Target, ulong Current, ulong NonStreaming, ulong Uploads, ulong Renderers, ulong Streaming, ulong NonStreamingCount, ulong Pending, ulong Loading);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly PerformanceSampleWindow Frames = new(8192);
    private static readonly PerformanceSampleWindow CpuFrames = new(8192);
    private static readonly PerformanceSampleWindow GpuFrames = new(8192);
    private static readonly List<NativeSampler> NativeSamplers = new();
    private sealed class SectionSample
    {
        internal long Total, Maximum, Allocated, MaxAllocated;
        internal int Calls;
    }
    private static readonly Dictionary<string, SectionSample> Sections = new();
    private static readonly string[] SamplerKeywords =
    {
        "PlayerLoop", "Behaviour", "Script", "Scripting", "Update", "Coroutine", "Job", "Thread", "Input",
        "Physics", "Animation", "Animator", "Skin", "Render", "Camera", "Cull", "Batch", "Gfx", "Present",
        "Shadow", "Light", "Shader", "Texture", "Mesh", "Compute", "PostProcess", "Particle", "VFX", "Audio",
        "Canvas", "UI.", "GC.", "Garbage", "Loading", "Asset", "Network", "NavMesh", "AI.", "Occlusion",
        "Director", "Video", "WaitFor"
    };
    private const int ContinuousSamplerLimit = 192;

    private static AsyncLogWriter? _writer;
    private static Process? _process;
    private static readonly Stopwatch Clock = new();
    private static Il2CppStructArray<FrameTiming>? _frameTimings;
    private static bool _started, _stopping, _hasState, _captureActive, _captureUnavailableLogged, _frameTimingAvailable;
    private static bool _previousProfilerEnabled, _previousBinaryLog, _previousAllocationStacks, _profilerStateSaved;
    private static bool[] _previousProfilerAreas = Array.Empty<bool>();
    private static string _previousProfilerFile = "", _state = "Unknown";
    private static (double Cpu, double Gpu, double WidthScale, double HeightScale) _latestFrameTiming = (-1, -1, -1, -1);
    private static double _nextSummary, _nextSamplerRead, _assetSnapshotDue = -1, _captureEnd, _nextCaptureAllowed, _nextHitchLog;
    private static IEnumerator<byte>? _assetScan;
    private static SceneInventory? _sceneInventory;
    private static Scene _diagnosticsScene;
    private static string _scanStage = "";
    private static string _snapshotReason = "";
    private static double _snapshotStarted, _snapshotCpuMs, _snapshotMaxStepMs;
    private static ulong _lastTimingTimestamp;
    private static int _timingEmptyReads, _timingDuplicateReads, _timingUntimestampedReads;
    private static TimeSpan _lastProcessCpu;
    private static double _lastProcessSample;
    private static bool _textureStreamingAvailable;
    private static int _slowStreak, _lastGc0, _lastGc1, _lastGc2;

    internal static bool Enabled => _started && !_stopping;
    internal static bool NativeProfilerSupported { get; private set; }
    internal static bool ProfilerCaptureActive => _captureActive;
    internal static string LogPath => _writer?.Path ?? "";

    internal static PerformanceScope Measure(string section) => new(section);

    internal static void Record(string section, long ticks, long allocated)
    {
        if (!Enabled || ticks < 0) return;
        if (!Sections.TryGetValue(section, out var sample)) Sections[section] = sample = new();
        sample.Total += ticks;
        sample.Maximum = Math.Max(sample.Maximum, ticks);
        sample.Calls++;
        if (allocated >= 0)
        {
            sample.Allocated += allocated;
            sample.MaxAllocated = Math.Max(sample.MaxAllocated, allocated);
        }
    }

    internal static void Start(Scene diagnosticsScene)
    {
        if (_started || !Settings.PerformanceLogging.Value) return;
        _started = true;
        _diagnosticsScene = diagnosticsScene;
        var tweaks = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "InfiniTweaks");
        TelemetryBridge.Attach(tweaks?.GetType("InfiniTweaks.Telemetry"));
        Clock.Restart();
        var directory = Path.Combine(Paths.BepInExRootPath, "PerformanceLogs");
        var path = Path.Combine(directory, $"ForgeDevelopment_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
        _writer = new AsyncLogWriter(path);
        _process = Process.GetCurrentProcess();
        _lastProcessCpu = _process.TotalProcessorTime;
        _lastGc0 = GC.CollectionCount(0); _lastGc1 = GC.CollectionCount(1); _lastGc2 = GC.CollectionCount(2);
        NativeProfilerSupported = Profiler.supported;
        _nextSummary = Settings.PerformanceSummarySeconds.Value;
        _nextSamplerRead = 0.1;
        InitializeFrameTiming();

        Write("session",
            ("plugin_version", Plugin.PluginVersion),
            ("unity_version", Application.unityVersion),
            ("os", SystemInfo.operatingSystem),
            ("cpu", SystemInfo.processorType),
            ("cpu_cores", SystemInfo.processorCount.ToString(Invariant)),
            ("system_memory_mb", SystemInfo.systemMemorySize.ToString(Invariant)),
            ("gpu", SystemInfo.graphicsDeviceName),
            ("gpu_api", SystemInfo.graphicsDeviceVersion),
            ("gpu_device_type", SystemInfo.graphicsDeviceType.ToString()),
            ("gpu_memory_mb", SystemInfo.graphicsMemorySize.ToString(Invariant)),
            ("graphics_multithreaded", SystemInfo.graphicsMultiThreaded.ToString()),
            ("render_threading_mode", SystemInfo.renderingThreadingMode.ToString()),
            ("async_compute", SystemInfo.supportsAsyncCompute.ToString()),
            ("resolution", $"{Screen.width}x{Screen.height}"),
            ("quality_level", QualitySettings.GetQualityLevel().ToString(Invariant)),
            ("anti_aliasing", QualitySettings.antiAliasing.ToString(Invariant)),
            ("shadow_quality", QualitySettings.shadows.ToString()),
            ("shadow_resolution", QualitySettings.shadowResolution.ToString()),
            ("shadow_distance", F(QualitySettings.shadowDistance)),
            ("shadow_cascades", QualitySettings.shadowCascades.ToString(Invariant)),
            ("lod_bias", F(QualitySettings.lodBias)),
            ("texture_streaming", QualitySettings.streamingMipmapsActive.ToString()),
            ("texture_streaming_budget_mb", F(QualitySettings.streamingMipmapsMemoryBudget)),
            ("srp_batching", GraphicsSettings.useScriptableRenderPipelineBatching.ToString()),
            ("vsync", QualitySettings.vSyncCount.ToString(Invariant)),
            ("target_fps", Application.targetFrameRate.ToString(Invariant)),
            ("native_profiler_supported", NativeProfilerSupported.ToString()));
        LogPluginsAndPatches();
        InitializeNativeSamplers();
        Plugin.PluginLog.LogInfo($"Performance diagnostics log: {path}");
    }

    internal static void Tick()
    {
        if (!Enabled) return;
        var now = Clock.Elapsed.TotalSeconds;
        var frameMilliseconds = Time.unscaledDeltaTime * 1000.0;
        var threshold = Settings.HitchThresholdMilliseconds.Value;
        var currentState = GetState();
        if (!_hasState || currentState != _state)
        {
            // Close the old interval before changing its state label. The first
            // frame after a transition straddles states and is explicitly excluded.
            if (_hasState) WriteSummary(now);
            CancelAssetSnapshot("state_change");
            _assetSnapshotDue = -1;
            _slowStreak = 0;
            _hasState = true;
            _state = currentState;
            Write("game_state", ("state", _state), ("frame", Time.frameCount.ToString(Invariant)));
            // Some player builds expose no sampler names until a scene has run.
            // Retry only at this lifecycle boundary, not in the per-frame loop.
            if (_state == nameof(eGameStateName.InLevel) && NativeSamplers.Count == 0) InitializeNativeSamplers();
            if (_state == nameof(eGameStateName.InLevel) && Settings.SnapshotAssetsOnLevelStart.Value)
                _assetSnapshotDue = now + 5;
        }
        else Frames.Add(frameMilliseconds, threshold);

        if (_state == nameof(eGameStateName.InLevel) && frameMilliseconds >= threshold)
        {
            _slowStreak++;
            if (now >= _nextHitchLog)
            {
                _nextHitchLog = now + 1;
                Write("hitch", ("frame_ms", F(frameMilliseconds)), ("slow_streak", _slowStreak.ToString(Invariant)), ("frame", Time.frameCount.ToString(Invariant)));
            }
            if (_slowStreak >= 3) StartProfilerCapture("automatic_hitch", now);
        }
        else _slowStreak = 0;

        SampleFrameTiming(threshold);
        if (now >= _nextSamplerRead)
        {
            SampleNativeSamplers();
            _nextSamplerRead = now + 0.1;
        }

        if (Input.GetKeyDown(Settings.PerformanceSnapshotKey.Value))
        {
            // Keep inventory work out of the native timing capture itself.
            CancelAssetSnapshot("manual_capture");
            StartProfilerCapture("manual", now);
            RequestAssetSnapshot("manual");
        }
        if (_assetSnapshotDue >= 0 && now >= _assetSnapshotDue)
        {
            _assetSnapshotDue = -1;
            RequestAssetSnapshot("level_start");
        }
        if (_captureActive && now >= _captureEnd) StopProfilerCapture("duration_complete");
        if (now >= _nextSummary)
        {
            WriteSummary(now);
            _nextSummary = now + Settings.PerformanceSummarySeconds.Value;
        }
        if (!_captureActive) StepAssetSnapshot();
        if (_writer?.Failure is { Length: > 0 } failure)
        {
            Plugin.PluginLog.LogError($"Performance log writer stopped: {failure}");
            Stop();
        }
    }

    internal static void Stop()
    {
        if (!_started || _stopping) return;
        WriteSummary(Clock.Elapsed.TotalSeconds);
        CancelAssetSnapshot("shutdown");
        _stopping = true;
        TelemetryBridge.Detach();
        if (_captureActive) StopProfilerCapture("shutdown");
        foreach (var sampler in NativeSamplers)
            try { sampler.Recorder.enabled = false; } catch { }
        Write("session_end", ("elapsed_seconds", F(Clock.Elapsed.TotalSeconds)), ("dropped_log_lines", (_writer?.Dropped ?? 0).ToString(Invariant)));
        _writer?.Dispose();
        _writer = null;
        _process?.Dispose();
        _process = null;
        _frameTimings = null;
        _frameTimingAvailable = false;
        NativeSamplers.Clear();
        Sections.Clear();
        _diagnosticsScene = default;
        _started = false;
        _stopping = false;
    }

    private static void WriteSummary(double now)
    {
        if (!Frames.TryTake(out var frame)) return;
        var hasCpuFrames = CpuFrames.TryTake(out var cpuFrames);
        var hasGpuFrames = GpuFrames.TryTake(out var gpuFrames);
        var texture = CaptureTextureStreaming();
        var memory = CaptureMemory();
        var processCpu = -1.0;
        var processCpuCores = -1.0;
        long workingSet = -1, privateBytes = -1;
        int threads = -1;
        if (_process != null)
        {
            try
            {
                _process.Refresh();
                var cpu = _process.TotalProcessorTime;
                var wall = now - _lastProcessSample;
                if (wall > 0)
                {
                    processCpuCores = (cpu - _lastProcessCpu).TotalSeconds / wall;
                    processCpu = processCpuCores / Math.Max(1, Environment.ProcessorCount) * 100;
                }
                _lastProcessCpu = cpu; _lastProcessSample = now;
                workingSet = _process.WorkingSet64; privateBytes = _process.PrivateMemorySize64; threads = _process.Threads.Count;
            }
            catch { }
        }

        var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
        Write("frame_summary",
            ("state", _state),
            ("frame", Time.frameCount.ToString(Invariant)),
            ("asset_scan_active", (_assetScan != null).ToString()),
            ("native_capture_active", _captureActive.ToString()),
            ("frame_timing_status", !_frameTimingAvailable ? "unavailable" : hasCpuFrames || hasGpuFrames ? "samples" : "no_samples"),
            ("frame_timing_untimestamped_reads", _timingUntimestampedReads.ToString(Invariant)),
            ("frame_timing_empty_reads", _timingEmptyReads.ToString(Invariant)),
            ("frame_timing_duplicate_reads", _timingDuplicateReads.ToString(Invariant)),
            ("sampler_poll_interval_ms", "100"),
            ("samples", frame.Samples.ToString(Invariant)),
            ("dropped_samples", frame.DroppedSamples.ToString(Invariant)),
            ("average_fps", frame.AverageMilliseconds > 0 ? F(1000 / frame.AverageMilliseconds) : "-1"),
            ("average_ms", F(frame.AverageMilliseconds)),
            ("minimum_ms", F(frame.MinimumMilliseconds)),
            ("p50_ms", F(frame.P50Milliseconds)),
            ("p95_ms", F(frame.P95Milliseconds)),
            ("p99_ms", F(frame.P99Milliseconds)),
            ("maximum_ms", F(frame.MaximumMilliseconds)),
            ("slow_frames", frame.SlowFrames.ToString(Invariant)),
            ("cpu_frame_latest_ms", F(_latestFrameTiming.Cpu)),
            ("cpu_frame_samples", hasCpuFrames ? cpuFrames.Samples.ToString(Invariant) : "0"),
            ("cpu_frame_average_ms", hasCpuFrames ? F(cpuFrames.AverageMilliseconds) : "-1"),
            ("cpu_frame_p95_ms", hasCpuFrames ? F(cpuFrames.P95Milliseconds) : "-1"),
            ("cpu_frame_p99_ms", hasCpuFrames ? F(cpuFrames.P99Milliseconds) : "-1"),
            ("cpu_frame_maximum_ms", hasCpuFrames ? F(cpuFrames.MaximumMilliseconds) : "-1"),
            ("gpu_frame_latest_ms", F(_latestFrameTiming.Gpu)),
            ("gpu_frame_samples", hasGpuFrames ? gpuFrames.Samples.ToString(Invariant) : "0"),
            ("gpu_frame_average_ms", hasGpuFrames ? F(gpuFrames.AverageMilliseconds) : "-1"),
            ("gpu_frame_p95_ms", hasGpuFrames ? F(gpuFrames.P95Milliseconds) : "-1"),
            ("gpu_frame_p99_ms", hasGpuFrames ? F(gpuFrames.P99Milliseconds) : "-1"),
            ("gpu_frame_maximum_ms", hasGpuFrames ? F(gpuFrames.MaximumMilliseconds) : "-1"),
            ("render_width_scale", F(_latestFrameTiming.WidthScale)),
            ("render_height_scale", F(_latestFrameTiming.HeightScale)),
            ("texture_streaming_available", _textureStreamingAvailable.ToString()),
            ("texture_total_mb", _textureStreamingAvailable ? Mb(texture.Total) : "-1"),
            ("texture_desired_mb", _textureStreamingAvailable ? Mb(texture.Desired) : "-1"),
            ("texture_target_mb", _textureStreamingAvailable ? Mb(texture.Target) : "-1"),
            ("texture_current_mb", _textureStreamingAvailable ? Mb(texture.Current) : "-1"),
            ("texture_non_streaming_mb", _textureStreamingAvailable ? Mb(texture.NonStreaming) : "-1"),
            ("texture_streaming_count", _textureStreamingAvailable ? texture.Streaming.ToString(Invariant) : "-1"),
            ("texture_non_streaming_count", _textureStreamingAvailable ? texture.NonStreamingCount.ToString(Invariant) : "-1"),
            ("texture_streaming_renderers", _textureStreamingAvailable ? texture.Renderers.ToString(Invariant) : "-1"),
            ("texture_pending_loads", _textureStreamingAvailable ? texture.Pending.ToString(Invariant) : "-1"),
            ("texture_loading", _textureStreamingAvailable ? texture.Loading.ToString(Invariant) : "-1"),
            ("texture_mipmap_uploads", _textureStreamingAvailable ? texture.Uploads.ToString(Invariant) : "-1"),
            ("process_cpu_percent", F(processCpu)),
            ("process_cpu_core_equivalents", F(processCpuCores)),
            ("working_set_mb", Mb(workingSet)),
            ("private_memory_mb", Mb(privateBytes)),
            ("threads", threads.ToString(Invariant)),
            ("unity_allocated_mb", Mb(memory.Allocated)),
            ("unity_reserved_mb", Mb(memory.Reserved)),
            ("unity_unused_reserved_mb", Mb(memory.Unused)),
            ("mono_heap_mb", Mb(memory.ManagedHeap)),
            ("mono_used_mb", Mb(memory.ManagedUsed)),
            ("graphics_driver_mb", Mb(memory.GraphicsDriver)),
            ("dotnet_managed_mb", Mb(memory.DotNetManaged)),
            ("log_queue_depth", (_writer?.Pending ?? 0).ToString(Invariant)),
            ("dropped_log_lines", (_writer?.Dropped ?? 0).ToString(Invariant)),
            ("gc0", (gc0 - _lastGc0).ToString(Invariant)),
            ("gc1", (gc1 - _lastGc1).ToString(Invariant)),
            ("gc2", (gc2 - _lastGc2).ToString(Invariant)));
        _timingEmptyReads = _timingDuplicateReads = _timingUntimestampedReads = 0;
        _lastGc0 = gc0; _lastGc1 = gc1; _lastGc2 = gc2;
        LogSections();
        TelemetryBridge.Snapshot();
        LogNativeSamplerValues();
    }

    private static void InitializeFrameTiming()
    {
        try
        {
            var klass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "FrameTiming");
            if (klass == IntPtr.Zero) throw new InvalidOperationException("UnityEngine.FrameTiming IL2CPP class was not found");
            Il2CppClassPointerStore<FrameTiming>.NativeClassPtr = klass;
            _frameTimings = new Il2CppStructArray<FrameTiming>(1);
            _frameTimingAvailable = true;
            Write("frame_timing_status", ("available", "True"));
        }
        catch (Exception error)
        {
            _frameTimings = null;
            _frameTimingAvailable = false;
            Write("frame_timing_status", ("available", "False"), ("error", error.GetType().Name + ": " + error.Message));
        }
    }

    private static void SampleFrameTiming(double threshold)
    {
        if (!_frameTimingAvailable) return;
        try
        {
            FrameTimingManager.CaptureFrameTimings();
            _latestFrameTiming = (-1, -1, -1, -1);
            if (_frameTimings == null || FrameTimingManager.GetLatestTimings(1, _frameTimings) == 0)
            { _timingEmptyReads++; return; }
            var timing = _frameTimings[0];
            if (timing.cpuTimeFrameComplete != 0 && timing.cpuTimeFrameComplete == _lastTimingTimestamp)
            { _timingDuplicateReads++; return; }
            if (timing.cpuTimeFrameComplete == 0) _timingUntimestampedReads++;
            _lastTimingTimestamp = timing.cpuTimeFrameComplete;
            _latestFrameTiming = (timing.cpuFrameTime, timing.gpuFrameTime, timing.widthScale, timing.heightScale);
            if (_latestFrameTiming.Cpu > 0) CpuFrames.Add(_latestFrameTiming.Cpu, threshold);
            if (_latestFrameTiming.Gpu > 0) GpuFrames.Add(_latestFrameTiming.Gpu, threshold);
        }
        catch (Exception error)
        {
            _frameTimingAvailable = false;
            _frameTimings = null;
            _latestFrameTiming = (-1, -1, -1, -1);
            Write("frame_timing_status", ("available", "False"), ("error", error.GetType().Name + ": " + error.Message));
        }
    }

    private static MemorySnapshot CaptureMemory()
    {
        long allocated = -1, reserved = -1, unused = -1, monoHeap = -1, monoUsed = -1, graphics = -1;
        try
        {
            allocated = Profiler.GetTotalAllocatedMemoryLong();
            reserved = Profiler.GetTotalReservedMemoryLong();
            unused = Profiler.GetTotalUnusedReservedMemoryLong();
            monoHeap = Profiler.GetMonoHeapSizeLong();
            monoUsed = Profiler.GetMonoUsedSizeLong();
            graphics = Profiler.GetAllocatedMemoryForGraphicsDriver();
        }
        catch { }
        return new MemorySnapshot(allocated, reserved, unused, monoHeap, monoUsed, graphics, GC.GetTotalMemory(false));
    }

    private static TextureStreamingSnapshot CaptureTextureStreaming()
    {
        try
        {
            _textureStreamingAvailable = true;
            return new TextureStreamingSnapshot(
                Texture.totalTextureMemory,
                Texture.desiredTextureMemory,
                Texture.targetTextureMemory,
                Texture.currentTextureMemory,
                Texture.nonStreamingTextureMemory,
                Texture.streamingMipmapUploadCount,
                Texture.streamingRendererCount,
                Texture.streamingTextureCount,
                Texture.nonStreamingTextureCount,
                Texture.streamingTexturePendingLoadCount,
                Texture.streamingTextureLoadingCount);
        }
        catch { _textureStreamingAvailable = false; return default; }
    }

    private static void LogSections()
    {
        foreach (var (name, sample) in Sections)
        {
            if (sample.Calls == 0) continue;
            var totalMilliseconds = sample.Total * 1000.0 / Stopwatch.Frequency;
            Write("infini_section",
                ("name", name), ("calls", sample.Calls.ToString(Invariant)),
                ("total_ms", F(totalMilliseconds)), ("average_ms", F(totalMilliseconds / sample.Calls)),
                ("maximum_ms", F(sample.Maximum * 1000.0 / Stopwatch.Frequency)),
                ("managed_allocated_bytes", sample.Allocated.ToString(Invariant)),
                ("maximum_managed_allocated_bytes", sample.MaxAllocated.ToString(Invariant)));
            sample.Total = sample.Maximum = sample.Allocated = sample.MaxAllocated = 0;
            sample.Calls = 0;
        }
    }

    private static void InitializeNativeSamplers()
    {
        try
        {
            var names = new Il2CppSystem.Collections.Generic.List<string>();
            Sampler.GetNames(names);
            var available = new List<string>(names.Count);
            for (var index = 0; index < names.Count; index++)
            {
                var name = names[index];
                if (string.IsNullOrWhiteSpace(name)) continue;
                available.Add(name);
                Write("unity_sampler_available", ("name", name));
            }
            var matched = available.Distinct(StringComparer.Ordinal).Where(IsInterestingSampler).OrderBy(SamplerPriority).ThenBy(value => value.Length).ToArray();
            foreach (var name in matched.Take(ContinuousSamplerLimit))
            {
                var recorder = Recorder.Get(name);
                if (recorder == null || !recorder.isValid) continue;
                recorder.CollectFromAllThreads();
                recorder.enabled = true;
                NativeSamplers.Add(new NativeSampler(name, recorder));
                Write("unity_sampler_selected", ("name", name));
            }
            Write("unity_sampler_summary",
                ("available", available.Count.ToString(Invariant)),
                ("matched_performance_categories", matched.Length.ToString(Invariant)),
                ("selected_continuous", NativeSamplers.Count.ToString(Invariant)),
                ("continuous_limit", ContinuousSamplerLimit.ToString(Invariant)),
                ("full_catalog_logged", "True"),
                ("all_profiler_areas_in_raw_capture", "True"));
        }
        catch (Exception error)
        {
            Write("unity_sampler_error", ("error", error.GetType().Name + ": " + error.Message));
        }
    }

    private static bool IsInterestingSampler(string name) => SamplerKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    private static int SamplerPriority(string name)
    {
        if (name.Equals("PlayerLoop", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("WaitForTargetFPS", StringComparison.OrdinalIgnoreCase) || name.Contains("Present", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("Physics", StringComparison.OrdinalIgnoreCase) || name.Contains("Render", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static void SampleNativeSamplers()
    {
        foreach (var sampler in NativeSamplers)
        {
            if (sampler.Failed) continue;
            try
            {
                var elapsed = Math.Max(0, sampler.Recorder.elapsedNanoseconds);
                sampler.SumNanoseconds += elapsed;
                sampler.MaxNanoseconds = Math.Max(sampler.MaxNanoseconds, elapsed);
                sampler.Blocks += Math.Max(0, sampler.Recorder.sampleBlockCount);
                sampler.Reads++;
            }
            catch { sampler.Failed = true; }
        }
    }

    private static void LogNativeSamplerValues()
    {
        foreach (var sampler in NativeSamplers.Where(value => value.Reads > 0 && (value.MaxNanoseconds > 0 || value.Blocks > 0)).OrderByDescending(value => value.MaxNanoseconds))
        {
            Write("unity_sampler",
                ("name", sampler.Name),
                ("reads", sampler.Reads.ToString(Invariant)),
                ("average_ms", F(sampler.SumNanoseconds / (double)sampler.Reads / 1_000_000)),
                ("maximum_ms", F(sampler.MaxNanoseconds / 1_000_000.0)),
                ("sample_blocks", sampler.Blocks.ToString(Invariant)));
        }
        foreach (var sampler in NativeSamplers)
        {
            sampler.SumNanoseconds = sampler.MaxNanoseconds = sampler.Blocks = 0;
            sampler.Reads = 0;
        }
    }

    private static void StartProfilerCapture(string reason, double now)
    {
        if ((!Settings.AutoProfilerCapture.Value && reason != "manual") || _captureActive || now < _nextCaptureAllowed) return;
        if (!NativeProfilerSupported)
        {
            if (!_captureUnavailableLogged)
            {
                _captureUnavailableLogged = true;
                _nextCaptureAllowed = double.PositiveInfinity;
                Write("unity_capture_unavailable", ("reason", "Profiler.supported is false in this game build"));
            }
            return;
        }
        try
        {
            if (Profiler.enabled)
            {
                Write("unity_capture_skipped", ("reason", "Profiler.enabled is already true; owner is unknown, existing state left untouched"),
                    ("binary_log", Profiler.enableBinaryLog.ToString()), ("log_file", Profiler.logFile),
                    ("allocation_callstacks", Profiler.enableAllocationCallstacks.ToString()));
                _nextCaptureAllowed = now + 60;
                return;
            }
            _previousProfilerEnabled = Profiler.enabled;
            _previousBinaryLog = Profiler.enableBinaryLog;
            _previousAllocationStacks = Profiler.enableAllocationCallstacks;
            _previousProfilerFile = Profiler.logFile;
            _previousProfilerAreas = new bool[Math.Max(0, Profiler.areaCount)];
            for (var index = 0; index < _previousProfilerAreas.Length; index++)
                _previousProfilerAreas[index] = Profiler.GetAreaEnabled((ProfilerArea)index);
            _profilerStateSaved = true;
            for (var index = 0; index < _previousProfilerAreas.Length; index++)
                Profiler.SetAreaEnabled((ProfilerArea)index, true);
            var file = Path.Combine(Path.GetDirectoryName(LogPath)!, $"ForgeDevelopment_UnityProfile_{DateTime.UtcNow:yyyyMMdd_HHmmss}.raw");
            Profiler.logFile = file;
            Profiler.enableBinaryLog = true;
            Profiler.enableAllocationCallstacks = false;
            Profiler.enabled = true;
            _captureActive = true;
            _captureEnd = now + Settings.ProfilerCaptureSeconds.Value;
            _nextCaptureAllowed = _captureEnd + 60;
            Write("unity_capture_start",
                ("reason", reason),
                ("seconds", F(Settings.ProfilerCaptureSeconds.Value)),
                ("profiler_areas", _previousProfilerAreas.Length.ToString(Invariant)),
                ("allocation_callstacks", "False"),
                ("file", Path.GetFileName(file)));
        }
        catch (Exception error)
        {
            Write("unity_capture_error", ("error", error.GetType().Name + ": " + error.Message));
            RestoreProfilerState();
            _nextCaptureAllowed = now + 60;
        }
    }

    private static void StopProfilerCapture(string reason)
    {
        if (!_captureActive) return;
        try
        {
            RestoreProfilerState();
            Write("unity_capture_stop", ("reason", reason));
        }
        catch (Exception error) { Write("unity_capture_error", ("error", error.GetType().Name + ": " + error.Message)); }
        finally { _captureActive = false; }
    }

    private static void RestoreProfilerState()
    {
        if (!_profilerStateSaved) return;
        try { Profiler.enabled = _previousProfilerEnabled; } catch { }
        try { Profiler.enableBinaryLog = _previousBinaryLog; } catch { }
        try { Profiler.enableAllocationCallstacks = _previousAllocationStacks; } catch { }
        try { Profiler.logFile = _previousProfilerFile; } catch { }
        for (var index = 0; index < _previousProfilerAreas.Length; index++)
            try { Profiler.SetAreaEnabled((ProfilerArea)index, _previousProfilerAreas[index]); } catch { }
        _previousProfilerAreas = Array.Empty<bool>();
        _profilerStateSaved = false;
    }

    private static void RequestAssetSnapshot(string reason)
    {
        if (!Enabled || _assetScan != null) return;
        _snapshotReason = reason;
        _snapshotStarted = Clock.Elapsed.TotalSeconds;
        _snapshotCpuMs = _snapshotMaxStepMs = 0;
        _assetScan = EnumerateAssetSnapshot().GetEnumerator();
        Write("asset_snapshot_start", ("reason", reason), ("state", _state),
            ("mode", "incremental"), ("budget_ms", "2"), ("atomic_scene_snapshot", "False"));
    }

    private static IEnumerable<byte> EnumerateAssetSnapshot()
    {
        _sceneInventory = new SceneInventory();
        _scanStage = "scene_inventory";
        Write("asset_stage_start", ("stage", _scanStage), ("scope", "loaded_scenes_and_diagnostics_persistent_scene; excludes unattached prefab components"));
        foreach (var item in _sceneInventory.Capture(_diagnosticsScene)) yield return item;
        Write("asset_stage_end", ("stage", _scanStage));
        var stages = new[]
        {
            LogComponents<GameObject>("game_object", value => value.activeInHierarchy),
            LogComponentTypes(),
            LogComponents<NavMeshAgent>("navmesh_agent", value => value.enabled && value.gameObject.activeInHierarchy),
            LogRenderingState(), LogCameras(), LogLights(), LogParticles(), LogAudioSources(),
            LogAnimators(), LogPhysics(), LogUi(), LogTopTextures(), LogTopMeshes(),
            LogTopMaterials(), LogTopAudioClips(), LogTopSkinnedMeshes()
        };
        string[] names = { "game_objects", "component_types", "navigation", "renderers", "cameras", "lights", "particles", "audio_sources", "animators", "physics", "ui", "textures", "meshes", "materials", "audio_clips", "skinned_meshes" };
        for (int i = 0; i < stages.Length; i++)
        {
            _scanStage = names[i];
            Write("asset_stage_start", ("stage", _scanStage));
            foreach (var item in stages[i]) yield return item;
            Write("asset_stage_end", ("stage", _scanStage));
            yield return 0;
        }
    }

    private static IEnumerable<Object> SnapshotObjects<T>() where T : Object
    {
        if (_sceneInventory != null && _sceneInventory.TryGet(typeof(T), out var values))
        {
            foreach (var value in values) yield return value;
            yield break;
        }
        // Texture/mesh/material/audio asset registries are native atomic queries.
        // Measure them explicitly; do not misrepresent the cooperative budget as a hard cap.
        long started = Stopwatch.GetTimestamp();
        var objects = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
        Write("asset_enumeration", ("kind", typeof(T).Name), ("objects", objects.Length.ToString(Invariant)),
            ("atomic_ms", F((Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency)));
        foreach (var value in objects) yield return value;
    }

    private static void StepAssetSnapshot()
    {
        if (_assetScan == null) return;
        using var scope = Measure("AssetSnapshot");
        var started = Stopwatch.GetTimestamp();
        string? end = null;
        try
        {
            do
            {
                long stepStart = Stopwatch.GetTimestamp();
                bool more = _assetScan.MoveNext();
                double atomicMs = (Stopwatch.GetTimestamp() - stepStart) * 1000d / Stopwatch.Frequency;
                if (atomicMs >= 2) Write("asset_slow_step", ("stage", _scanStage), ("atomic_ms", F(atomicMs)));
                if (!more) { end = "complete"; break; }
            } while ((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency < 2);
        }
        catch (Exception error)
        {
            Write("asset_snapshot_error", ("error", error.GetType().Name + ": " + error.Message));
            end = "error";
        }
        var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        _snapshotCpuMs += ms;
        _snapshotMaxStepMs = Math.Max(_snapshotMaxStepMs, ms);
        if (end != null) CancelAssetSnapshot(end);
    }

    internal static bool WorldInspectionActive => _assetScan != null || _sceneInventory != null;
    internal static void CancelWorldInspection()
    {
        CancelAssetSnapshot("level_cleanup");
        _assetSnapshotDue = -1;
    }
    private static void CancelAssetSnapshot(string status)
    {
        var scan = _assetScan;
        _assetScan = null;
        _sceneInventory = null;
        if (scan == null) return;
        try { scan.Dispose(); }
        catch (Exception error) { Write("asset_snapshot_dispose_error", ("error", error.ToString())); }
        Write("asset_snapshot_end", ("reason", _snapshotReason), ("status", status),
            ("scanner_ms", F(_snapshotCpuMs)), ("maximum_step_ms", F(_snapshotMaxStepMs)),
            ("elapsed_ms", F((Clock.Elapsed.TotalSeconds - _snapshotStarted) * 1000)));
    }

    private static IEnumerable<byte> LogComponents<T>(string kind, Func<T, bool> active) where T : Object
    {
        var objects = SnapshotObjects<T>();
        var loaded = 0; var activeCount = 0;
        foreach (var value in objects)
        {
            yield return 0;
            var typed = value?.TryCast<T>();
            if (typed == null) continue;
            loaded++;
            try { if (active(typed)) activeCount++; } catch { }
        }
        Write("component_summary", ("kind", kind), ("loaded", loaded.ToString(Invariant)), ("active", activeCount.ToString(Invariant)));
    }

    private static IEnumerable<byte> LogComponentTypes()
    {
        var counts = new Dictionary<string, (int Loaded, int Active)>(StringComparer.Ordinal);
        var typeNames = new Dictionary<IntPtr, string>();
        foreach (var value in SnapshotObjects<Component>())
        {
            yield return 0;
            var component = value?.TryCast<Component>();
            if (component == null) continue;
            try
            {
                var klass = IL2CPP.il2cpp_object_get_class(component.Pointer);
                if (!typeNames.TryGetValue(klass, out var name))
                {
                    name = Il2CppTypeName(component);
                    typeNames.Add(klass, name);
                }
                counts.TryGetValue(name, out var count);
                count.Loaded++;
                if (component.gameObject.activeInHierarchy) count.Active++;
                counts[name] = count;
            }
            catch { }
        }
        Write("component_type_summary", ("types", counts.Count.ToString(Invariant)), ("instances", counts.Sum(value => value.Value.Loaded).ToString(Invariant)));
        foreach (var entry in counts.OrderByDescending(value => value.Value.Active).ThenByDescending(value => value.Value.Loaded).ThenBy(value => value.Key, StringComparer.Ordinal))
            Write("component_type", ("name", entry.Key), ("loaded", entry.Value.Loaded.ToString(Invariant)), ("active", entry.Value.Active.ToString(Invariant)));
    }

    private static string Il2CppTypeName(Object value)
    {
        var klass = IL2CPP.il2cpp_object_get_class(value.Pointer);
        var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "unknown";
        var space = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
        return string.IsNullOrEmpty(space) ? name : space + "." + name;
    }

    private static IEnumerable<byte> LogRenderingState()
    {
        var costs = new List<RendererCost>();
        var materials = new HashSet<int>();
        var meshes = new HashSet<int>();
        var loaded = 0; var active = 0; var visible = 0; var shadowCasters = 0; var staticBatches = 0; var materialSlots = 0;
        long visibleVertices = 0; ulong visibleIndices = 0;
        foreach (var value in SnapshotObjects<Renderer>())
        {
            yield return 0;
            var renderer = value?.TryCast<Renderer>();
            if (renderer == null) continue;
            loaded++;
            try
            {
                var isActive = renderer.enabled && !renderer.forceRenderingOff && renderer.gameObject.activeInHierarchy;
                var isVisible = isActive && renderer.isVisible;
                if (isActive) active++;
                if (isVisible) visible++;
                if (isActive && renderer.shadowCastingMode != ShadowCastingMode.Off) shadowCasters++;
                if (renderer.isPartOfStaticBatch) staticBatches++;
                var sharedMaterials = renderer.sharedMaterials;
                materialSlots += sharedMaterials.Length;
                foreach (var material in sharedMaterials)
                    if (material != null) materials.Add(material.GetInstanceID());
                var mesh = RendererMesh(renderer, out var kind);
                var vertices = mesh?.vertexCount ?? 0;
                var indices = mesh == null ? 0 : MeshIndices(mesh);
                if (mesh != null) meshes.Add(mesh.GetInstanceID());
                if (isVisible) { visibleVertices += vertices; visibleIndices += indices; }
                costs.Add(new RendererCost(
                    SafeName(renderer), kind, vertices, indices, sharedMaterials.Length,
                    $"active={isActive};visible={isVisible};shadows={renderer.shadowCastingMode};receive_shadows={renderer.receiveShadows};static_batch={renderer.isPartOfStaticBatch};motion_vectors={renderer.motionVectorGenerationMode};occlusion={renderer.allowOcclusionWhenDynamic};mesh={SafeName(mesh)}"));
            }
            catch { }
        }
        Write("rendering_summary",
            ("renderers_loaded", loaded.ToString(Invariant)),
            ("renderers_active", active.ToString(Invariant)),
            ("renderers_visible", visible.ToString(Invariant)),
            ("shadow_casters_active", shadowCasters.ToString(Invariant)),
            ("static_batched_renderers", staticBatches.ToString(Invariant)),
            ("material_slots", materialSlots.ToString(Invariant)),
            ("unique_materials", materials.Count.ToString(Invariant)),
            ("unique_meshes", meshes.Count.ToString(Invariant)),
            ("visible_vertices_proxy", visibleVertices.ToString(Invariant)),
            ("visible_indices_proxy", visibleIndices.ToString(Invariant)));
        var rank = 0;
        foreach (var cost in costs.OrderByDescending(value => value.Indices).ThenByDescending(value => value.Vertices).Take(Settings.MaxLoggedAssets.Value))
            Write("renderer",
                ("rank", (++rank).ToString(Invariant)), ("name", cost.Name), ("kind", cost.Kind),
                ("vertices", cost.Vertices.ToString(Invariant)), ("indices", cost.Indices.ToString(Invariant)),
                ("material_slots", cost.Materials.ToString(Invariant)), ("detail", cost.Detail));
    }

    private static Mesh? RendererMesh(Renderer renderer, out string kind)
    {
        var skinned = renderer.TryCast<SkinnedMeshRenderer>();
        if (skinned != null) { kind = "skinned"; return skinned.sharedMesh; }
        var filter = renderer.GetComponent<MeshFilter>();
        if (filter != null) { kind = "mesh"; return filter.sharedMesh; }
        kind = "other";
        return null;
    }

    private static ulong MeshIndices(Mesh mesh)
    {
        ulong indices = 0;
        for (var subMesh = 0; subMesh < mesh.subMeshCount; subMesh++) indices += mesh.GetIndexCount(subMesh);
        return indices;
    }

    private static IEnumerable<byte> LogCameras()
    {
        var details = new List<SceneDetail>();
        var loaded = 0; var active = 0; var commandBuffers = 0;
        foreach (var value in SnapshotObjects<Camera>())
        {
            yield return 0;
            var camera = value?.TryCast<Camera>();
            if (camera == null) continue;
            loaded++;
            try
            {
                var enabled = camera.enabled && camera.gameObject.activeInHierarchy;
                if (enabled) active++;
                commandBuffers += camera.commandBufferCount;
                var pixels = (long)camera.scaledPixelWidth * camera.scaledPixelHeight;
                details.Add(new SceneDetail(SafeName(camera), enabled ? pixels : 0,
                    $"active={enabled};pixels={camera.scaledPixelWidth}x{camera.scaledPixelHeight};path={camera.actualRenderingPath};hdr={camera.allowHDR};msaa={camera.allowMSAA};dynamic_resolution={camera.allowDynamicResolution};occlusion={camera.useOcclusionCulling};depth_texture={camera.depthTextureMode};command_buffers={camera.commandBufferCount};target_texture={SafeName(camera.targetTexture)}"));
            }
            catch { }
        }
        Write("camera_summary", ("loaded", loaded.ToString(Invariant)), ("active", active.ToString(Invariant)), ("command_buffers", commandBuffers.ToString(Invariant)));
        LogSceneDetails("camera", details);
    }

    private static IEnumerable<byte> LogLights()
    {
        var details = new List<SceneDetail>();
        var loaded = 0; var active = 0; var shadowed = 0; var commandBuffers = 0;
        foreach (var value in SnapshotObjects<Light>())
        {
            yield return 0;
            var light = value?.TryCast<Light>();
            if (light == null) continue;
            loaded++;
            try
            {
                var enabled = light.enabled && light.gameObject.activeInHierarchy;
                if (enabled) active++;
                if (enabled && light.shadows != LightShadows.None) shadowed++;
                commandBuffers += light.commandBufferCount;
                details.Add(new SceneDetail(SafeName(light), enabled && light.shadows != LightShadows.None ? 2 : enabled ? 1 : 0,
                    $"active={enabled};type={light.type};shadows={light.shadows};shadow_resolution={light.shadowResolution};range={F(light.range)};intensity={F(light.intensity)};culling_mask={light.cullingMask};command_buffers={light.commandBufferCount}"));
            }
            catch { }
        }
        Write("light_summary", ("loaded", loaded.ToString(Invariant)), ("active", active.ToString(Invariant)), ("shadowed_active", shadowed.ToString(Invariant)), ("command_buffers", commandBuffers.ToString(Invariant)));
        LogSceneDetails("light", details);
    }

    private static IEnumerable<byte> LogParticles()
    {
        var details = new List<SceneDetail>();
        var loaded = 0; var playing = 0; long particles = 0; long capacity = 0;
        foreach (var value in SnapshotObjects<ParticleSystem>())
        {
            yield return 0;
            var particle = value?.TryCast<ParticleSystem>();
            if (particle == null) continue;
            loaded++;
            try
            {
                var active = particle.isPlaying && particle.gameObject.activeInHierarchy;
                if (active) playing++;
                particles += particle.particleCount;
                capacity += particle.maxParticles;
                details.Add(new SceneDetail(SafeName(particle), active ? particle.particleCount : 0,
                    $"active={active};particles={particle.particleCount};max_particles={particle.maxParticles};emitting={particle.isEmitting};loop={particle.loop};simulation={particle.simulationSpace};automatic_culling={particle.automaticCullingEnabled}"));
            }
            catch { }
        }
        Write("particle_summary", ("loaded", loaded.ToString(Invariant)), ("playing", playing.ToString(Invariant)), ("current_particles", particles.ToString(Invariant)), ("max_particles", capacity.ToString(Invariant)));
        LogSceneDetails("particle_system", details);
    }

    private static IEnumerable<byte> LogAudioSources()
    {
        var details = new List<SceneDetail>();
        var loaded = 0; var playing = 0; var spatialized = 0;
        foreach (var value in SnapshotObjects<AudioSource>())
        {
            yield return 0;
            var source = value?.TryCast<AudioSource>();
            if (source == null) continue;
            loaded++;
            try
            {
                var active = source.isPlaying && source.gameObject.activeInHierarchy;
                if (active) playing++;
                if (active && (source.spatialize || source.spatialBlend > 0)) spatialized++;
                details.Add(new SceneDetail(SafeName(source), active ? 256 - source.priority : 0,
                    $"active={active};clip={SafeName(source.clip)};priority={source.priority};volume={F(source.volume)};pitch={F(source.pitch)};loop={source.loop};spatial_blend={F(source.spatialBlend)};spatialize={source.spatialize};doppler={F(source.dopplerLevel)}"));
            }
            catch { }
        }
        Write("audio_source_summary", ("loaded", loaded.ToString(Invariant)), ("playing", playing.ToString(Invariant)), ("spatialized_playing", spatialized.ToString(Invariant)));
        LogSceneDetails("audio_source", details);
    }

    private static IEnumerable<byte> LogAnimators()
    {
        var details = new List<SceneDetail>();
        var loaded = 0; var active = 0; var alwaysAnimate = 0;
        foreach (var value in SnapshotObjects<Animator>())
        {
            yield return 0;
            var animator = value?.TryCast<Animator>();
            if (animator == null) continue;
            loaded++;
            try
            {
                var enabled = animator.enabled && animator.gameObject.activeInHierarchy;
                if (enabled) active++;
                if (enabled && animator.cullingMode == AnimatorCullingMode.AlwaysAnimate) alwaysAnimate++;
                details.Add(new SceneDetail(SafeName(animator), enabled ? animator.layerCount + animator.parameterCount : 0,
                    $"active={enabled};controller={SafeName(animator.runtimeAnimatorController)};culling={animator.cullingMode};update={animator.updateMode};layers={animator.layerCount};parameters={animator.parameterCount};human={animator.isHuman}"));
            }
            catch { }
        }
        Write("animator_summary", ("loaded", loaded.ToString(Invariant)), ("active", active.ToString(Invariant)), ("always_animate", alwaysAnimate.ToString(Invariant)));
        LogSceneDetails("animator", details);
    }

    private static IEnumerable<byte> LogPhysics()
    {
        var colliders = SnapshotObjects<Collider>();
        var colliderLoaded = 0; var colliderActive = 0; var triggers = 0;
        foreach (var value in colliders)
        {
            yield return 0;
            var collider = value?.TryCast<Collider>();
            if (collider == null) continue;
            colliderLoaded++;
            try
            {
                if (!collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                colliderActive++;
                if (collider.isTrigger) triggers++;
            }
            catch { }
        }
        var bodies = SnapshotObjects<Rigidbody>();
        var bodyLoaded = 0; var bodyActive = 0; var kinematic = 0; var sleeping = 0;
        foreach (var value in bodies)
        {
            yield return 0;
            var body = value?.TryCast<Rigidbody>();
            if (body == null) continue;
            bodyLoaded++;
            try
            {
                if (!body.gameObject.activeInHierarchy) continue;
                bodyActive++;
                if (body.isKinematic) kinematic++;
                if (body.IsSleeping()) sleeping++;
            }
            catch { }
        }
        Write("physics_summary",
            ("colliders_loaded", colliderLoaded.ToString(Invariant)), ("colliders_active", colliderActive.ToString(Invariant)),
            ("triggers_active", triggers.ToString(Invariant)), ("rigidbodies_loaded", bodyLoaded.ToString(Invariant)),
            ("rigidbodies_active", bodyActive.ToString(Invariant)), ("kinematic_active", kinematic.ToString(Invariant)),
            ("sleeping_active", sleeping.ToString(Invariant)));
    }

    private static IEnumerable<byte> LogUi()
    {
        var canvases = SnapshotObjects<Canvas>();
        var canvasLoaded = 0; var canvasActive = 0;
        foreach (var value in canvases)
        {
            yield return 0;
            var canvas = value?.TryCast<Canvas>();
            if (canvas == null) continue;
            canvasLoaded++;
            try { if (canvas.enabled && canvas.gameObject.activeInHierarchy) canvasActive++; } catch { }
        }
        var renderers = SnapshotObjects<CanvasRenderer>();
        var rendererLoaded = 0; var rendererActive = 0; var materialSlots = 0;
        foreach (var value in renderers)
        {
            yield return 0;
            var renderer = value?.TryCast<CanvasRenderer>();
            if (renderer == null) continue;
            rendererLoaded++;
            try
            {
                if (renderer.gameObject.activeInHierarchy) rendererActive++;
                materialSlots += renderer.materialCount;
            }
            catch { }
        }
        Write("ui_summary",
            ("canvases_loaded", canvasLoaded.ToString(Invariant)), ("canvases_active", canvasActive.ToString(Invariant)),
            ("canvas_renderers_loaded", rendererLoaded.ToString(Invariant)), ("canvas_renderers_active", rendererActive.ToString(Invariant)),
            ("material_slots", materialSlots.ToString(Invariant)));
    }

    private static void LogSceneDetails(string kind, List<SceneDetail> details)
    {
        var rank = 0;
        foreach (var detail in details.OrderByDescending(value => value.Weight).Take(Settings.MaxLoggedAssets.Value))
            Write("scene_object", ("kind", kind), ("rank", (++rank).ToString(Invariant)), ("name", detail.Name), ("weight_proxy", detail.Weight.ToString(Invariant)), ("detail", detail.Detail));
    }

    private static IEnumerable<byte> LogTopTextures()
    {
        var costs = new List<AssetCost>();
        foreach (var value in SnapshotObjects<Texture>())
        {
            yield return 0;
            var texture = value?.TryCast<Texture>();
            if (texture == null) continue;
            try { costs.Add(new AssetCost(SafeName(texture), RuntimeBytes(texture), $"{texture.width}x{texture.height}")); } catch { }
        }
        LogAssets("texture", costs);
    }

    private static IEnumerable<byte> LogTopMeshes()
    {
        var costs = new List<AssetCost>();
        foreach (var value in SnapshotObjects<Mesh>())
        {
            yield return 0;
            var mesh = value?.TryCast<Mesh>();
            if (mesh == null) continue;
            try
            {
                ulong indices = 0;
                for (var subMesh = 0; subMesh < mesh.subMeshCount; subMesh++) indices += mesh.GetIndexCount(subMesh);
                costs.Add(new AssetCost(SafeName(mesh), RuntimeBytes(mesh), $"vertices={mesh.vertexCount};indices={indices};submeshes={mesh.subMeshCount}"));
            }
            catch { }
        }
        LogAssets("mesh", costs);
    }

    private static IEnumerable<byte> LogTopMaterials()
    {
        var costs = new List<AssetCost>();
        foreach (var value in SnapshotObjects<Material>())
        {
            yield return 0;
            var material = value?.TryCast<Material>();
            if (material == null) continue;
            try { costs.Add(new AssetCost(SafeName(material), RuntimeBytes(material), "shader=" + SafeName(material.shader))); } catch { }
        }
        LogAssets("material", costs);
    }

    private static IEnumerable<byte> LogTopAudioClips()
    {
        var costs = new List<AssetCost>();
        foreach (var value in SnapshotObjects<AudioClip>())
        {
            yield return 0;
            var clip = value?.TryCast<AudioClip>();
            if (clip == null) continue;
            try { costs.Add(new AssetCost(SafeName(clip), RuntimeBytes(clip), $"seconds={F(clip.length)};channels={clip.channels};frequency={clip.frequency};state={clip.loadState}")); } catch { }
        }
        LogAssets("audio_clip", costs);
    }

    private static IEnumerable<byte> LogTopSkinnedMeshes()
    {
        var costs = new List<SkinnedCost>();
        long totalVertices = 0;
        foreach (var value in SnapshotObjects<SkinnedMeshRenderer>())
        {
            yield return 0;
            var renderer = value?.TryCast<SkinnedMeshRenderer>();
            if (renderer == null) continue;
            try
            {
                var vertices = renderer.sharedMesh?.vertexCount ?? 0;
                totalVertices += vertices;
                costs.Add(new SkinnedCost(SafeName(renderer), vertices, $"mesh={SafeName(renderer.sharedMesh)};materials={renderer.sharedMaterials.Length};update_offscreen={renderer.updateWhenOffscreen};visible={renderer.isVisible}"));
            }
            catch { }
        }
        Write("skinned_mesh_summary", ("loaded", costs.Count.ToString(Invariant)), ("total_vertices", totalVertices.ToString(Invariant)));
        var rank = 0;
        foreach (var cost in costs.OrderByDescending(value => value.Vertices).Take(Settings.MaxLoggedAssets.Value))
            Write("skinned_mesh", ("rank", (++rank).ToString(Invariant)), ("name", cost.Name), ("vertices", cost.Vertices.ToString(Invariant)), ("detail", cost.Detail));
    }

    private static void LogAssets(string kind, List<AssetCost> costs)
    {
        long bytes = 0;
        foreach (var cost in costs) bytes += Math.Max(0, cost.Bytes);
        Write("asset_summary", ("kind", kind), ("loaded", costs.Count.ToString(Invariant)), ("runtime_memory_mb", Mb(bytes)));
        var rank = 0;
        foreach (var cost in costs.OrderByDescending(value => value.Bytes).Take(Settings.MaxLoggedAssets.Value))
            Write("asset", ("kind", kind), ("rank", (++rank).ToString(Invariant)), ("name", cost.Name), ("runtime_memory_mb", Mb(cost.Bytes)), ("detail", cost.Detail));
    }

    private static long RuntimeBytes(Object value)
    {
        try { return Math.Max(0, Profiler.GetRuntimeMemorySizeLong(value)); }
        catch { return -1; }
    }

    private static void LogPluginsAndPatches()
    {
        try
        {
            foreach (var plugin in IL2CPPChainloader.Instance.Plugins.Values.OrderBy(value => value.Metadata.GUID, StringComparer.OrdinalIgnoreCase))
            {
                long bytes = -1;
                try { if (File.Exists(plugin.Location)) bytes = new FileInfo(plugin.Location).Length; } catch { }
                Write("plugin", ("guid", plugin.Metadata.GUID), ("name", plugin.Metadata.Name), ("version", plugin.Metadata.Version.ToString()), ("assembly", Path.GetFileName(plugin.Location)), ("assembly_bytes", bytes.ToString(Invariant)));
            }
            var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var patchedMethods = 0;
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                patchedMethods++;
                AddPatchOwners(info.Prefixes, owners); AddPatchOwners(info.Postfixes, owners); AddPatchOwners(info.Transpilers, owners); AddPatchOwners(info.Finalizers, owners);
            }
            Write("harmony_summary", ("patched_methods", patchedMethods.ToString(Invariant)), ("owners", owners.Count.ToString(Invariant)));
            foreach (var owner in owners.OrderByDescending(value => value.Value).ThenBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
                Write("harmony_owner", ("owner", owner.Key), ("patches", owner.Value.ToString(Invariant)));
        }
        catch (Exception error) { Write("plugin_inventory_error", ("error", error.GetType().Name + ": " + error.Message)); }
    }

    private static void AddPatchOwners(IEnumerable<Patch> patches, Dictionary<string, int> owners)
    {
        foreach (var patch in patches)
        {
            var owner = string.IsNullOrWhiteSpace(patch.owner) ? "unknown" : patch.owner;
            owners.TryGetValue(owner, out var count);
            owners[owner] = count + 1;
        }
    }

    private static string GetState()
    {
        try { return GameStateManager.CurrentStateName.ToString(); }
        catch { return "Unavailable"; }
    }

    private static string SafeName(Object? value)
    {
        if (value == null) return "null";
        try { return Clean(value.name); }
        catch { return "unavailable"; }
    }

    private static string Clean(string? value) => string.IsNullOrEmpty(value) ? "" : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    private static string F(double value) => double.IsFinite(value) ? value.ToString("0.###", Invariant) : "-1";
    private static string Mb(long bytes) => bytes < 0 ? "-1" : (bytes / 1048576.0).ToString("0.###", Invariant);
    private static string Mb(ulong bytes) => (bytes / 1048576.0).ToString("0.###", Invariant);

    internal static void Write(string type, params (string Key, string Value)[] fields)
    {
        var line = new StringBuilder(256).Append(DateTime.UtcNow.ToString("O", Invariant)).Append('\t').Append("type=").Append(type);
        foreach (var field in fields) line.Append('\t').Append(field.Key).Append('=').Append(Clean(field.Value));
        _writer?.Write(line.ToString());
    }

    private sealed class AsyncLogWriter : IDisposable
    {
        private readonly BlockingCollection<string> _lines = new(4096);
        private readonly Thread _thread;
        private volatile string _failure = "";
        internal string Path { get; }
        private int _dropped;
        internal int Dropped => _dropped;
        internal int Pending => _lines.Count;
        internal string Failure => _failure;

        internal AsyncLogWriter(string path)
        {
            Path = path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            _thread = new Thread(Run) { IsBackground = true, Name = "Forge Runtime Log" };
            _thread.Start();
        }

        internal void Write(string line)
        {
            if (!_lines.IsAddingCompleted && !_lines.TryAdd(line)) Interlocked.Increment(ref _dropped);
        }

        private void Run()
        {
            try
            {
                using var stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536);
                var nextFlush = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                while (!_lines.IsCompleted)
                {
                    if (_lines.TryTake(out var line, 250)) writer.WriteLine(line);
                    var now = Stopwatch.GetTimestamp();
                    if (now < nextFlush && _lines.Count != 0) continue;
                    writer.Flush();
                    nextFlush = now + Stopwatch.Frequency;
                }
                writer.Flush();
            }
            catch (Exception error) { _failure = error.GetType().Name + ": " + error.Message; }
        }

        public void Dispose()
        {
            if (!_lines.IsAddingCompleted) _lines.CompleteAdding();
            if (_thread.Join(2000)) _lines.Dispose();
        }
    }
}
