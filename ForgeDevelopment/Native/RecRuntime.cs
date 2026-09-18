using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using ForgeRuntime.Framework;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeDevelopment.Native;

/// <summary>
/// The recorder's startup and lifetime. The order is fixed and each stage is torn down in reverse when a later one
/// fails: the session must exist before anything can record, the log capture needs the session, the tracer needs the
/// log capture's failure reporting, and the kernel patch comes last because it is the piece owned by the host.
///
/// Nothing here is a second diagnostics pipeline: <see cref="RuntimeDiagnostics"/> keeps owning the report, and this
/// adds the session's own channels beside it.
/// </summary>
internal static class RecRuntime
{
    private static readonly object Gate = new();
    private static readonly List<string> Started = new();
    private static Harmony? _harmony;
    private static RuntimeBehaviorTraceSubscription? _behaviorTrace;
    private static long _nextKernelPoll;
    private static string _lastLevel = "";
    private static long _lastEpoch = long.MinValue;
    private static bool _ticking;

    internal static bool Active { get { lock (Gate) return Started.Count != 0 && RecSession.Active; } }
    internal static IReadOnlyList<string> Stages { get { lock (Gate) return Started.ToArray(); } }
    internal static string TraceReport => RecTracerRuntime.Report;

    /// <summary>Starts the recorder for this process. It is called from the authoring branch of Plugin.Load after
    /// RuntimeDiagnostics is listening, so a stage that fails is reported through the diagnostics that already work.
    /// A stage that fails records itself in <see cref="Failure"/> and the ones before it keep running: the recorder
    /// degrades to the channels it could open instead of taking the authoring session down with it.</summary>
    internal static void Start(string bepInExRootPath)
    {
        if (!Settings.RecorderEnabled.Value) return;
        lock (Gate)
        {
            if (Started.Count != 0) return;
        }
        RecUnity.Install();
        RecTime.Install();
        Root = bepInExRootPath;
        if (!RecSession.Start(Root, Plugin.PluginVersion, HostPlugin.PluginVersion))
        {
            Failure = "session: could not be opened under " + Root;
            return;
        }
        Stage("session");
        Try("log", RecLog.Start, RecLog.Stop);
        if (Settings.RecorderTrace.Value) Try("tracer", LoadTrace, RecTracerRuntime.UninstallAll);
        Try("forge_kernel", WireForge, UnwireForge);
        WriteStartup();
    }

    /// <summary>Why a stage is missing from the session, or null. A recorder that cannot start one channel says so
    /// once, in the session itself and in the plugin log, rather than throwing the whole authoring session away.</summary>
    internal static string? Failure { get; private set; }

    private static void Try(string stage, Action start, Action stop)
    {
        try
        {
            start();
            Stage(stage);
        }
        catch (Exception error)
        {
            Failure = stage + ": " + error.GetType().Name + ": " + error.Message;
            try { Plugin.PluginLog.LogError("Forge recorder stage " + stage + " failed: " + error); }
            catch (Exception) { }
            try { stop(); }
            catch (Exception cleanup)
            {
                Failure += " (cleanup failed: " + cleanup.GetType().Name + ")";
            }
            RecSession.Note("stage_failed", json =>
            {
                json.WriteString("stage", stage);
                json.WriteString("error", error.GetType().Name + ": " + error.Message);
            });
        }
    }

    /// <summary>Flushes what is queued and releases every stage in reverse. A level end, a process exit and a failed
    /// startup all call it, so there is one teardown and not two: every stage below is idempotent, and a second call
    /// after the first has run costs nothing.</summary>
    internal static void Stop()
    {
        lock (Gate)
        {
            if (Started.Count == 0 && !RecSession.Active)
            {
                RecSession.Stop();
                return;
            }
        }
        RecSession.Flush(TimeSpan.FromSeconds(5));
        for (var i = Started.Count - 1; i >= 0; i--)
        {
            try { StopStage(Started[i]); }
            catch (Exception error)
            {
                try { Plugin.PluginLog.LogError("Forge recorder stop stage " + Started[i] + " failed: " + error); }
                catch (Exception) { }
            }
        }
        lock (Gate)
        {
            Started.Clear();
            _harmony = null;
        }
        RecSession.Stop();
    }

    /// <summary>The per-frame part: the recorder's hotkeys, the kernel's own state, and the boundaries a snapshot is
    /// asked for at. It rides the authoring monitor's Update, which is the only per-frame callback this package owns.</summary>
    internal static void Tick()
    {
        if (!RecSession.Active) return;
        if (_ticking) return;
        _ticking = true;
        try
        {
            RecHotkeys.Poll();
            var now = Environment.TickCount64;
            if (now >= _nextKernelPoll)
            {
                _nextKernelPoll = now + 1000;
                RecForge.Poll();
            }
            var level = RecTime.Level;
            var epoch = RecTime.WorldEpoch;
            if (level != _lastLevel || epoch != _lastEpoch)
            {
                var previousLevel = _lastLevel;
                var previousEpoch = _lastEpoch;
                _lastLevel = level;
                _lastEpoch = epoch;
                if (previousLevel.Length != 0 || previousEpoch != long.MinValue)
                {
                    RecSession.Note("boundary", json =>
                    {
                        json.WriteString("from", previousLevel.Length == 0 ? "unavailable" : previousLevel);
                        json.WriteString("to", level);
                        json.WriteNumber("fromEpoch", previousEpoch == long.MinValue ? -1 : previousEpoch);
                        json.WriteNumber("toEpoch", epoch);
                    });
                    CaptureRegistry.SnapshotAll("level_boundary");
                }
            }
        }
        finally { _ticking = false; }
    }

    /// <summary>The level-cleanup path: the last snapshot of the level and the summary that says what the session
    /// holds, before the world it describes is gone.</summary>
    internal static void LevelEnded(string phase)
    {
        if (!RecSession.Active) return;
        CaptureRegistry.SnapshotAll("level_cleanup_" + phase);
        RecSession.Note("summary", json =>
        {
            json.WriteString("phase", phase);
            json.WriteNumber("bytes", RecSession.BytesWritten);
            json.WriteNumber("dropped", RecSession.Dropped);
            json.WriteBoolean("budgetReached", RecSession.BudgetReached);
            json.WriteNumber("tracedMethods", RecTracerRuntime.InstalledMethods.Count);
            json.WriteNumber("traceSkips", RecTracerRuntime.Skipped.Count);
            json.WriteNumber("kernelRecords", RecForge.Records);
            json.WriteString("kernelCodes", RecForge.CodeCounts());
            json.WriteString("level", RecTime.Level);
        });
        RecSession.Flush(TimeSpan.FromSeconds(2));
    }

    /// <summary>The BepInEx root this session writes under. It is read once at startup: a later read would be a
    /// second source of truth for where the session already wrote its files.</summary>
    private static string Root { get; set; } = "";

    private static void LoadTrace()
    {
        var configDirectory = Path.Combine(Root, "config", "ForgeDevelopment", RecTracer.ConfigFolder);
        var sourceDirectory = Path.Combine(Path.GetDirectoryName(typeof(RecRuntime).Assembly.Location) ?? ".", "probes", RecTracer.ConfigFolder);
        Directory.CreateDirectory(configDirectory);
        // The first run copies the package's own profiles into the config directory; from then on the config
        // directory is the only source, so an author's edit is never overwritten by a plugin update.
        foreach (var source in Directory.Exists(sourceDirectory) ? Directory.GetFiles(sourceDirectory, "*.json") : Array.Empty<string>())
        {
            var target = Path.Combine(configDirectory, Path.GetFileName(source));
            if (!File.Exists(target)) File.Copy(source, target);
        }
        var files = Directory.Exists(configDirectory)
            ? Directory.GetFiles(configDirectory, "*.json").OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        RecSession.Note("trace_config", json =>
        {
            json.WriteString("directory", configDirectory);
            json.WriteNumber("files", files.Length);
        });
        RecTracerRuntime.LoadProfiles(files);
        RecSession.WritePatches(new { report = RecTracerRuntime.Report });
    }

    private static void WireForge()
    {
        _harmony = new Harmony(Plugin.PluginGuid + ".RecForge");
        RecForge.Start(_harmony);
        var runtime = HostPlugin.Runtime ?? throw new InvalidOperationException("Forge Runtime is unavailable to the authoring recorder.");
        _behaviorTrace = runtime.ObserveBehaviorTrace(RecForge.RecordBehaviorTrace);
        var previous = DevelopmentModule.Sink;
        DevelopmentModule.Sink = record =>
        {
            previous(record);
            RecForge.RecordDiagnostic(record);
        };
    }

    private static void UnwireForge()
    {
        _behaviorTrace?.Dispose();
        _behaviorTrace = null;
        RecForge.Stop(_harmony);
    }

    private static void WriteStartup()
    {
        RecSession.Write("session", "startup", json =>
        {
            json.WriteString("plugin", Plugin.PluginName + " " + Plugin.PluginVersion);
            json.WriteString("forgeRuntime", HostPlugin.PluginVersion);
            json.WriteString("mode", HostPlugin.ConfiguredMode.ToString());
            json.WriteString("directory", RecSession.Directory);
            json.WriteString("session", RecSession.SessionId);
            json.WriteString("role", RecTime.Role);
            json.WriteString("slot", RecTime.Slot);
            json.WriteString("gameVersion", RecUnity.GameVersion);
            json.WriteString("unityVersion", RecUnity.UnityVersion);
            json.WriteNumber("budgetBytes", Settings.RecorderBudgetGiB.Value * 1024L * 1024 * 1024);
            json.WriteNumber("segmentBytes", Settings.RecorderSegmentMiB.Value * 1024L * 1024);
            json.WriteBoolean("gzip", Settings.RecorderGzip.Value);
            json.WriteString("trace", RecTracerRuntime.Report);
            json.WriteString("forgeKernel", RecForge.Installed ? "patched" : (RecForge.Failure ?? "absent"));
            json.WriteNumber("process", Environment.ProcessId);
            json.WriteString("startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        });
    }

    private static void Stage(string name)
    {
        lock (Gate) Started.Add(name);
    }

    /// <summary>Releases one started stage. The names are the ones <see cref="Try"/> records, and the order they are
    /// released in is the reverse of the order they started in.</summary>
    private static void StopStage(string name)
    {
        switch (name)
        {
            case "forge_kernel":
                UnwireForge();
                break;
            case "tracer":
                RecTracerRuntime.UninstallAll();
                break;
            case "log":
                RecLog.Stop();
                break;
            case "session":
                RecSession.Flush(TimeSpan.FromSeconds(5));
                break;
        }
    }
}
