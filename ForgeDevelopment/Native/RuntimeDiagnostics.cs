using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CullingSystem;
using LevelGeneration;
using UnityEngine;
using Il2CppInterop.Runtime;
using UnityEngine.SceneManagement;
using HostPlugin = ForgeRuntime.Plugin;
using Object = UnityEngine.Object;

namespace ForgeDevelopment.Native;

public sealed class AuthoringMonitor : MonoBehaviour
{
    public AuthoringMonitor(IntPtr pointer) : base(pointer) { }
    public void Update() => RuntimeDiagnostics.Safe(RuntimeDiagnostics.Tick);
    public void OnApplicationQuit() => RuntimeDiagnostics.Stop();
    public void OnDestroy() => RuntimeDiagnostics.Stop();
}

internal static class RuntimeDiagnostics
{
    internal sealed record JobTrace(string Subject, string Stage, string Before, Dictionary<string, string> Context, long Started, LG_FactoryJob Job, DiagnosticsReport Report, long WorldEpoch);
    private sealed class LogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.Error | LogLevel.Fatal | LogLevel.Warning;
        public void LogEvent(object sender, LogEventArgs args)
        {
            if (args.Source is BepInEx.Unity.IL2CPP.Logging.IL2CPPUnityLogSource) return;
            if (args.Source.SourceName == Plugin.PluginName || (args.Level & (LogLevel.Error | LogLevel.Fatal | LogLevel.Warning)) == 0) return;
            var text = args.Data?.ToString() ?? "";
            if (text.Length == 0) return;
            var (type, source) = DiagnosticLogClassifier.Classify(args.Level.ToString(), args.Source.SourceName, text);
            _report?.Issue(type, source, text, text);
        }
        public void Dispose() { }
    }
    private static readonly LogListener Listener = new();
    private static Application.LogCallback? _unityCallback;
    private static uint _unityCallbackRoot;
    private static readonly string Session = DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
    private static readonly Dictionary<string, int> JobCalls = new();
    private static readonly Dictionary<string, (int Calls, double Total, double Maximum)> StageCosts = new();
    private static readonly HashSet<string> CullingObservations = new();
    private static volatile DiagnosticsReport? _report;
    private static IEnumerator<byte>? _inspection;
    private static ProjectInspectionSession? _inspectionSession;
    private static bool _generationActive;
    private static AsyncReportWriter? _writer;
    private static bool _listening, _stopped, _structureComplete, _jobIdentityCaptured;
    private static int _run, _cleanupCount;
    private static string _path = "", _state = "", _outcome = "observing";
    private static double _nextExport;
    private static long _inspectionTicks, _maxInspectionTicks, _traceTicks;
    private static int _steps;

    internal static DiagnosticsReport? Report => _report;
    internal static long WorldEpoch => HostPlugin.Runtime?.WorldEpoch ?? 0;
    internal static long? SimulationTick => HostPlugin.Runtime is { CurrentTick: >= 0 } runtime ? runtime.CurrentTick : null;
    internal static string Number(double n) => n.ToString("R", CultureInfo.InvariantCulture);
    internal static string Vector(Vector3 p) => $"{Number(p.x)},{Number(p.y)},{Number(p.z)}";
    internal static string Rotation(Quaternion q) => $"{Number(q.x)},{Number(q.y)},{Number(q.z)},{Number(q.w)}";
    internal static string Zone(LG_Zone? zone) => zone == null ? "unavailable" : $"{zone.DimensionIndex}/{zone.Layer?.m_type.ToString() ?? "unavailable"}/{zone.LocalIndex}";
    internal static string PathOf(Transform? t)
    {
        if (t == null) return "unavailable";
        var names = new List<string>();
        for (var i = 0; t != null && i < 32; i++, t = t.parent) names.Add(t.name + "[" + t.GetSiblingIndex() + "]");
        names.Reverse(); return string.Join("/", names);
    }
    internal static void Safe(Action action)
    {
        using var scope = PerformanceDiagnostics.Measure("AuthoringObserver");
        try { action(); }
        catch (Exception e)
        {
            // A failed observer is reported, never substituted for the original game exception.
            _report?.Issue("diagnostic_error", "ForgeDevelopment", e.Message, e.ToString());
            if (_report == null) Plugin.PluginLog.LogError(e);
        }
    }
    internal static void Initialize()
    {
        if (_listening) return;
        _stopped = false;
        _writer = new AsyncReportWriter(error => Plugin.PluginLog.LogError("Forge report write failed: " + error));
        _unityCallback = (Application.LogCallback)(Action<string, string, LogType>)UnityLog;
        // Keep the native delegate alive, not just its managed wrapper.
        _unityCallbackRoot = IL2CPP.il2cpp_gchandle_new(_unityCallback.Pointer, false);
        Application.add_logMessageReceivedThreaded(_unityCallback);
        BepInEx.Logging.Logger.Listeners.Add(Listener);
        _listening = true;
        _report = new DiagnosticsReport(Session + "-startup");
        _path = System.IO.Path.Combine(Paths.BepInExRootPath, "ForgeReports", Session + "-startup.json");
        _report.SetMetadata("sessionId", Session);
        _report.SetMetadata("forgeVersion", HostPlugin.PluginVersion);
        _report.SetMetadata("developmentVersion", Plugin.PluginVersion);
        _report.Check("structure", "factory", "not_checked", "Waiting for generation.");
    }
    private static void UnityLog(string message, string stack, LogType level)
    {
        if (level is not (LogType.Exception or LogType.Error or LogType.Assert or LogType.Warning)) return;
        var report = _report;
        if (report == null || string.IsNullOrEmpty(message)) return;
        var (type, source) = DiagnosticLogClassifier.Classify(level.ToString(), "Unity", message + "\n" + stack);
        report.Issue(type, source, message, stack);
    }
    internal static void Begin()
    {
        CancelInspection("new_generation");
        ProjectChecks.CancelSourceSnapshot("New generation started.");
        Export(_run == 0 ? "startup" : _outcome);
        _run++;
        var id = Session + "-" + _run.ToString("D3", CultureInfo.InvariantCulture);
        _report = new DiagnosticsReport(id);
        _path = System.IO.Path.Combine(Paths.BepInExRootPath, "ForgeReports", id + ".json");
        _structureComplete = false; _jobIdentityCaptured = false; _outcome = "generating";
        _generationActive = true;
        _inspectionTicks = _maxInspectionTicks = _traceTicks = 0; _steps = 0;
        JobCalls.Clear(); StageCosts.Clear(); CullingObservations.Clear();
        _report.SetMetadata("sessionId", Session);
        _report.SetMetadata("attempt", _run.ToString(CultureInfo.InvariantCulture));
        _report.SetMetadata("cleanupCount", _cleanupCount.ToString(CultureInfo.InvariantCulture));
        _report.SetMetadata("forgeVersion", HostPlugin.PluginVersion);
        _report.SetMetadata("developmentVersion", Plugin.PluginVersion);
        _report.SetMetadata("unityVersion", Application.unityVersion);
        _report.SetMetadata("gameVersion", Application.version);
        _report.SetMetadata("bepInExAssemblyVersion", typeof(Paths).Assembly.GetName().Version?.ToString() ?? "unavailable");
        _report.SetMetadata("mode", HostPlugin.ConfiguredMode.ToString());
        _report.SetMetadata("role", SNetwork.SNet.IsMaster ? "host" : "client");
        CaptureGenerationIdentity("factory_setup_prefix");
        foreach (var info in IL2CPPChainloader.Instance.Plugins.Values.OrderBy(p => p.Metadata.GUID))
            _report.SetMetadata("plugin:" + info.Metadata.GUID, info.Metadata.Version.ToString());
        _report.Check("structure", "factory", "not_checked", "FactoryDone has not been observed.");
        _report.Check("objective_scripts", "expedition", "not_checked", "No general proof of objective or script completion is possible from object presence.");
        _report.Check("playability", "expedition", "not_checked", "Structure completion does not establish that the level is completable.");
        _report.Event("generation", "begin", id, new() { ["random"] = RandomState() });
        _nextExport = Time.realtimeSinceStartup + 30;
        var epoch = WorldEpoch;
        var tick = SimulationTick;
        var scan = ProjectChecks.Load(_report, epoch, tick);
        _inspectionSession = new ProjectInspectionSession(_report, scan, epoch);
        Export("generating");
    }
    private static void CaptureGenerationIdentity(string phase)
    {
        if (_report == null) return;
        _report.SetMetadata("seedCaptureStage", phase);
        _report.SetMetadata("seed", Builder.BuildSeed.ToString(CultureInfo.InvariantCulture));
        var expedition = Builder.LevelGenExpedition;
        if (expedition != null)
        {
            _report.SetMetadata("mainLayout", expedition.LevelLayoutData.ToString());
            _report.SetMetadata("secondaryLayout", expedition.SecondaryLayout.ToString());
            _report.SetMetadata("thirdLayout", expedition.ThirdLayout.ToString());
            _report.SetMetadata("hostSeed", expedition.HostIDSeed.ToString());
            _report.SetMetadata("sessionSeed", expedition.SessionSeed.ToString());
        }
    }
    private static string RandomState()
    {
        static string Read(SeedRandom? r) => r == null ? "unavailable" : $"{r.m_seed}:{r.m_step}:{r.m_randomRangeIntCount}:{r.m_randomRangeFloatCount}";
        return "build=" + Read(Builder.BuildSeedRandom) + ";host=" + Read(Builder.HostIDSeedRandom) + ";session=" + Read(Builder.SessionSeedRandom);
    }
    private static string LocalRandom(LG_FactoryJob job)
    {
        var geoJob = job.TryCast<LG_BuildGeomorphJob>();
        if (geoJob?.m_rnd?.m_random == null) return "not_exposed";
        var rnd = geoJob.m_rnd.m_random;
        // Reading the existing state must not draw or reseed the random generator.
        var state = new StringBuilder().Append(rnd.inext).Append(':').Append(rnd.inextp);
        if (rnd.SeedArray != null) foreach (var value in rnd.SeedArray) state.Append(':').Append(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString())));
    }
    internal static JobTrace? StartJob(LG_FactoryJob job, MethodBase method)
    {
        if (_report == null || _stopped || !_generationActive || _inspectionSession?.WorldEpoch != WorldEpoch) return null;
        var begin = Stopwatch.GetTimestamp();
        // Setup may update generation identity; capture again once jobs actually start.
        if (!_jobIdentityCaptured) { CaptureGenerationIdentity("first_job_prefix"); _jobIdentityCaptured = true; }
        var subject = method.DeclaringType!.FullName + ":" + job.Pointer.ToString("X");
        var stage = LG_Factory.Current?.GetCurrentBatchName.ToString() ?? "unavailable";
        var fields = new Dictionary<string, string> { ["jobType"] = method.DeclaringType!.FullName!, ["randomLocalBefore"] = LocalRandom(job) };
        var wrapper = method.DeclaringType.GetConstructor(new[] { typeof(IntPtr) })?.Invoke(new object[] { job.Pointer });
        var geo = method.DeclaringType.GetProperty("m_geomorph")?.GetValue(wrapper) as LG_Geomorph;
        var area = method.DeclaringType.GetProperty("m_area")?.GetValue(wrapper) as LG_Area;
        var zone = method.DeclaringType.GetProperty("m_zone")?.GetValue(wrapper) as LG_Zone ?? geo?.m_zone ?? area?.m_zone;
        if (zone != null) fields["zone"] = Zone(zone);
        if (area != null) fields["area"] = area.UID.ToString();
        var batch = LG_Factory.Current?.m_currentBatch;
        if (batch != null) { fields["batchSeed"] = batch.m_localSeed.ToString(); fields["batchSeedOffset"] = batch.m_seedOffset.ToString(); }
        if (geo != null)
        {
            fields["geomorph"] = geo.m_geoPrefab == null ? geo.name : geo.m_geoPrefab.name;
            fields["zone"] = Zone(geo.m_zone);
            fields["position"] = Vector(geo.transform.position);
        }
        var key = method.DeclaringType.FullName + ":" + job.Pointer;
        if (JobCalls.Count < 100000 || JobCalls.ContainsKey(key))
        { JobCalls.TryGetValue(key, out var count); JobCalls[key] = ++count; fields["invocation"] = count.ToString(); }
        fields["invocationMeaning"] = "Build calls; false may be continuation, not evidence of random retry";
        var before = RandomState();
        _traceTicks += Stopwatch.GetTimestamp() - begin;
        return new(subject, stage, before, fields, Stopwatch.GetTimestamp(), job, _report, WorldEpoch);
    }
    internal static void EndJob(JobTrace? trace, bool done, Exception? error)
    {
        if (trace == null || _report == null || !_generationActive ||
            !ReferenceEquals(trace.Report, _report) || trace.WorldEpoch != WorldEpoch) return;
        var end = Stopwatch.GetTimestamp();
        trace.Context["randomBefore"] = trace.Before;
        trace.Context["randomAfter"] = RandomState();
        trace.Context["randomLocalAfter"] = LocalRandom(trace.Job);
        trace.Context["returnedDone"] = done.ToString();
        var elapsed = (end - trace.Started) * 1000.0 / Stopwatch.Frequency;
        StageCosts.TryGetValue(trace.Stage, out var costs);
        StageCosts[trace.Stage] = (costs.Calls + 1, costs.Total + elapsed, Math.Max(costs.Maximum, elapsed));
        if (done || error != null || trace.Context.GetValueOrDefault("invocation") == "1")
            _report.Event("generation_job", trace.Stage, trace.Subject, trace.Context, elapsed);
        if (error != null)
            _report.Issue(error.GetType().FullName!, trace.Context["jobType"] + "/" + trace.Stage + "/" + trace.Context.GetValueOrDefault("zone", "unavailable") + "/" + trace.Context.GetValueOrDefault("geomorph", trace.Subject), error.Message, error.ToString());
        _traceTicks += Stopwatch.GetTimestamp() - end;
    }
    internal static void StructureFinished()
    {
        var session = _inspectionSession;
        if (!_generationActive || _structureComplete || session == null ||
            !session.AcceptWorld(WorldEpoch, SimulationTick)) return;
        _structureComplete = true;
        _report?.Check("structure", "factory", "observed_complete", "Native FactoryDone returned; objectives and scripts remain unverified.");
        _report?.Event("generation", "factory_done", "factory", new() { ["random"] = RandomState() });
        _inspection = WorldInspection.Capture(session).GetEnumerator();
        Export("structure_complete_inspection_pending");
    }
    internal static void Tick()
    {
        if (_stopped) return;
        if (_inspectionSession is { IsClosed: false } current && !current.AcceptWorld(WorldEpoch, SimulationTick))
        {
            CancelInspection("world_epoch_changed");
            ProjectChecks.CancelSourceSnapshot("World changed before diagnostics completed.");
            _generationActive = false;
            Export("inspection_cancelled");
        }
        var state = GameStateManager.CurrentStateName.ToString();
        if (state != _state)
        {
            _report?.Event("lifecycle", "game_state", state, new() { ["previous"] = _state, ["sceneCount"] = SceneManager.sceneCount.ToString() });
            _state = state;
            Export(_outcome);
        }
        if (_inspection != null)
        {
            var started = Stopwatch.GetTimestamp();
            var budget = Settings.InspectionBudget.Value * Stopwatch.Frequency / 1000.0;
            while (_inspection != null)
            {
                try
                {
                    if (_inspectionSession == null || !_inspectionSession.AcceptWorld(WorldEpoch, SimulationTick))
                    {
                        CancelInspection("world_epoch_changed");
                        ProjectChecks.CancelSourceSnapshot("World changed during inspection.");
                        Export("inspection_cancelled");
                        break;
                    }
                    if (!_inspection.MoveNext())
                    {
                        _inspection.Dispose(); _inspection = null;
                        Export(_inspectionSession.Outcome);
                        break;
                    }
                }
                catch (Exception e)
                {
                    _report?.Issue("diagnostic_error", "world_iterator", e.Message, e.ToString());
                    CancelInspection("iterator_failed");
                    ProjectChecks.CancelSourceSnapshot("World inspection failed.");
                    Export("inspection_failed");
                    break;
                }
                if (Stopwatch.GetTimestamp() - started >= budget) break;
            }
            var ticks = Stopwatch.GetTimestamp() - started;
            _inspectionTicks += ticks; _maxInspectionTicks = Math.Max(_maxInspectionTicks, ticks); _steps++;
        }
        if (Input.GetKeyDown(Settings.ExportKey.Value)) Export(_outcome);
        if (Input.GetKeyDown(KeyCode.F11)) CombatSampling.Toggle();
        if (Time.realtimeSinceStartup >= _nextExport) { Export(_outcome); _nextExport = Time.realtimeSinceStartup + 30; }
    }
    internal static void Cleanup(string phase, Exception? error = null)
    {
        _report?.Event("lifecycle", "level_cleanup_" + phase, "Builder", new() { ["sceneCount"] = SceneManager.sceneCount.ToString(), ["courseNodes"] = AIGraph.AIG_CourseNode.s_allNodes?.Count.ToString() ?? "unavailable" });
        if (phase == "before")
        {
            _generationActive = false;
            CancelInspection("level_cleanup");
            ProjectChecks.CancelSourceSnapshot("Level cleanup started.");
            PerformanceDiagnostics.CancelWorldInspection();
        }
        if (error != null) _report?.Issue(error.GetType().FullName!, "Builder.OnLevelCleanup", error.Message, error.ToString());
        if (phase != "after") return;
        _cleanupCount++; JobCalls.Clear(); CullingObservations.Clear(); CombatSampling.Active = false;
        _report?.SetMetadata("authoringIteratorActiveAfterCleanup", (_inspection != null).ToString());
        _report?.SetMetadata("performanceWorldScanActiveAfterCleanup", PerformanceDiagnostics.WorldInspectionActive.ToString());
        _report?.Check("retained_world_objects", "all_plugins", "not_checked", "Only this plugin's two scan ownership states are observed; no global retained-reference count is claimed.");
        Export(_structureComplete ? "level_left" : "generation_interrupted");
    }
    internal static void Marker(LG_MarkerSpawner marker, bool success)
    {
        var go = marker.m_spawnedGO;
        _report?.Event("placement", "marker_spawn", marker.m_markerDataBlockID.ToString(), new()
        {
            ["source"] = PathOf(marker.m_producerSource?.transform), ["zone"] = Zone(marker.m_zone),
            ["area"] = marker.m_area?.UID.ToString() ?? "unavailable", ["seed"] = marker.m_markerInstanceSeed.ToString(),
            ["localPosition"] = Vector(marker.m_localPosition), ["localRotation"] = Rotation(marker.m_localRotation),
            ["spawned"] = success.ToString(), ["object"] = go == null ? "none" : PathOf(go.transform),
            ["worldPosition"] = go == null ? "unavailable" : Vector(go.transform.position),
            ["worldRotation"] = go == null ? "unavailable" : Rotation(go.transform.rotation)
        });
    }
    internal static void Spawner(LG_PrefabSpawner spawner, GameObject? go)
    {
        _report?.Event("prefab_spawner", "build", PathOf(spawner.transform), new()
        {
            ["prefab"] = spawner.m_prefab == null ? "unavailable" : spawner.m_prefab.name,
            ["zone"] = Zone(spawner.m_zone), ["built"] = spawner.m_isBuilt.ToString(),
            ["object"] = go == null ? "none" : PathOf(go.transform), ["position"] = Vector(spawner.transform.position),
            ["rotation"] = Rotation(spawner.transform.rotation), ["collisionDisabled"] = spawner.m_disableCollision.ToString()
        });
    }
    internal static void Culling(C_Cullable culler, string method, string phase, Exception? error = null)
    {
        if (error != null) _report?.Issue(error.GetType().FullName!, "CullingSystem." + method, error.Message, error.ToString());
        var key = culler.Pointer.ToString("X") + ":" + method + ":" + phase;
        if (CullingObservations.Count >= 10000 || !CullingObservations.Add(key)) return;
        var fields = new Dictionary<string, string> { ["phase"] = phase, ["registered"] = culler.IsRegistered.ToString(), ["cleanupCount"] = _cleanupCount.ToString() };
        fields["identifierScope"] = "native pointer within this run only; not a stable cross-run identity";
        var cluster = culler.TryCast<C_CullingCluster>();
        if (cluster != null)
        {
            fields["renderers"] = cluster.Renderers?.Length.ToString() ?? "unavailable";
            if (cluster.Renderers != null) fields["destroyedRenderers"] = cluster.Renderers.Count(r => r == null).ToString();
        }
        _report?.Event("culling_lifecycle", method, culler.Pointer.ToString("X"), fields);
    }
    private static void CancelInspection(string why)
    {
        _inspectionSession?.Cancel(_inspectionSession.WorldEpoch == WorldEpoch ? SimulationTick : null, why);
        if (_inspection == null) return;
        var iterator = _inspection; _inspection = null;
        try { iterator.Dispose(); }
        catch (Exception e) { _report?.Issue("diagnostic_error", "world_iterator_dispose", e.Message, e.ToString()); }

    }
    internal static void Export(string outcome)
    {
        if (_report == null) return;
        _outcome = outcome;
        _report.SetMetadata("inspectionTotalMs", Number(_inspectionTicks * 1000.0 / Stopwatch.Frequency));
        _report.SetMetadata("inspectionMaximumStepMs", Number(_maxInspectionTicks * 1000.0 / Stopwatch.Frequency));
        _report.SetMetadata("inspectionSteps", _steps.ToString());
        _report.SetMetadata("generationTraceOverheadMs", Number(_traceTicks * 1000.0 / Stopwatch.Frequency));
        foreach (var (stage, cost) in StageCosts)
            _report.SetMetadata("stage:" + stage, $"calls={cost.Calls};totalMs={Number(cost.Total)};maxMs={Number(cost.Maximum)}");
        if (_writer?.Enqueue(_report, _path, outcome) != true)
        {
            _report.Issue("report_queue_full", "ForgeDevelopment", "Report was not queued; previous files do not include this requested snapshot.");
            Plugin.PluginLog.LogError("Forge report queue rejected a snapshot: " + _path);
        }
    }
    internal static void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _generationActive = false;
        // A failed native unsubscribe must not prevent exports or writer disposal.
        ShutdownSequence.Run(new (string, Action)[]
        {
            ("bepinex_listener", () => { if (_listening) BepInEx.Logging.Logger.Listeners.Remove(Listener); _listening = false; }),
            ("unity_listener", () =>
            {
                if (_unityCallback == null) return;
                Application.remove_logMessageReceivedThreaded(_unityCallback);
                _unityCallback = null;
                if (_unityCallbackRoot != 0) IL2CPP.il2cpp_gchandle_free(_unityCallbackRoot);
                _unityCallbackRoot = 0;
            }),
            ("world_inspection", () => CancelInspection("shutdown")),
            ("performance_scan", PerformanceDiagnostics.CancelWorldInspection),
            ("source_snapshot", () => ProjectChecks.CancelSourceSnapshot("Process shutdown.")),
            ("job_state", () => { JobCalls.Clear(); CullingObservations.Clear(); }),
            ("final_export", () => Export("process_exit")),
            ("report_writer", () => _writer?.Dispose())
        }, (stage, error) =>
        {
            _report?.Issue("shutdown_error", stage, error.Message, error.ToString());
            Plugin.PluginLog.LogError("Forge shutdown " + stage + " failed: " + error);
        });
        _writer = null;
        _inspectionSession = null;
        _report = null;
    }
}
