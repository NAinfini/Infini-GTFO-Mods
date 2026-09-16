using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using UnityEngine;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeDevelopment.Native;

internal sealed class ExperimentRecord
{
    internal ExperimentRunStatus Status { get; set; } = ExperimentRunStatus.NotRun;
    internal int Done { get; set; }
    internal int Total { get; set; }
    internal string Message { get; set; } = "";
    internal string When { get; set; } = "";
    internal double Milliseconds { get; set; }
}

internal sealed record ExperimentEvent(string Type, string Message);

/// <summary>
/// Runs one experiment command at a time on the main thread. The runner owns what the engine deliberately does
/// not know: the host/client gate, the session channel, the per-frame budget and the on-disk record of what
/// has already been executed.
/// </summary>
internal sealed class ExperimentRunner : MonoBehaviour
{
    private const string StateFolder = "experiments";
    private const int MaximumEvents = 512;

    private static readonly Dictionary<string, ExperimentRecord> Records = new(StringComparer.Ordinal);
    private static readonly List<ExperimentEvent> Events = new();

    private ExperimentCommand? _command;
    private ExperimentEngine? _engine;
    private ExperimentTargetSpec _spec = null!;
    private string _runId = "";
    private string _result = "";
    private long _startedTicks;
    private double _frameStart;
    private bool _sessionStarted;
    private bool _previousHooked;

    internal static ExperimentRunner? Instance { get; private set; }

    public ExperimentRunner(IntPtr pointer) : base(pointer) { }

    private void Awake() => Instance = this;

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    internal static ExperimentRecord Record(string id) => Records.TryGetValue(id, out var record) ? record : new ExperimentRecord();

    internal static IReadOnlyDictionary<string, ExperimentRecord> AllRecords => Records;

    internal static IReadOnlyList<ExperimentEvent> Recent => Events;

    internal static bool Running => Instance != null && Instance._engine is { Finished: false };

    internal static string CurrentCommand => Instance?._command?.Id ?? "";

    /// <summary>Loads the catalog into the config directory and restores what has already been executed.</summary>
    internal static string Load(string shippedDirectory)
    {
        var seed = ExperimentCatalog.Seed(shippedDirectory);
        ExperimentCatalog.Load();
        LoadState();
        return seed;
    }

    /// <summary>Runs a command by catalog index. Returns null when it started, otherwise the reason it did not.</summary>
    internal static string? Run(int index)
    {
        var runner = Instance;
        if (runner == null) return "the experiment runner component is not attached";
        if (ExperimentCatalog.All.Count == 0) return "no command file loaded; see " + ExperimentCatalog.DirectoryPath;
        if (index < 0 || index >= ExperimentCatalog.All.Count) return "no command at index " + index.ToString(CultureInfo.InvariantCulture);
        return runner.Begin(ExperimentCatalog.All[index]);
    }

    internal static string? Run(string id)
    {
        var runner = Instance;
        if (runner == null) return "the experiment runner component is not attached";
        var index = -1;
        for (var i = 0; i < ExperimentCatalog.All.Count; i++)
            if (ExperimentCatalog.All[i].Id == id) { index = i; break; }
        return index < 0 ? "no command with id '" + id + "'" : runner.Begin(ExperimentCatalog.All[index]);
    }

    internal static void Cancel() => Instance?._engine?.Cancel("cancelled from the panel");

    /// <summary>Destroys the markers and clones experiments created, without running a command.</summary>
    internal static string CleanUp() => ExperimentObjects.DestroyAll();

    /// <summary>The live resolution of a selector, for the panel's "what is the crosshair on" line.</summary>
    internal static string Describe(string target)
    {
        if (!ExperimentTargetSpec.TryParse(target, out var spec)) return "unknown selector";
        try
        {
            var resolved = ExperimentTargeting.Resolve(spec);
            return resolved.Ok ? resolved.Describe + " (" + resolved.Count + ")" : "unresolved: " + resolved.Error;
        }
        catch (Exception e)
        {
            return "resolution failed: " + e.Message;
        }
    }

    private string? Begin(ExperimentCommand command)
    {
        if (Running) return "an experiment is already running: " + _command?.Id;
        if (!ExperimentTargetSpec.TryParse(command.Target, out var spec)) return "target '" + command.Target + "' could not be parsed";
        var host = RecTime.Role;
        if (command.Host != ExperimentHost.Any)
        {
            var wantsHost = command.Host == ExperimentHost.Host;
            if ((host == "host") != wantsHost) return "the command requires " + command.Host + " but this instance is " + host;
        }

        _spec = spec;
        _command = command;
        _result = "";
        _runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + command.Id;
        _startedTicks = Stopwatch.GetTimestamp();
        _engine = new ExperimentEngine(command, spec, new ExperimentRuntime(), OnOutcome);
        _frameStart = Time.realtimeSinceStartup;

        if (!RecSession.Active)
        {
            _sessionStarted = RecSession.Start(Paths.BepInExRootPath, Plugin.PluginVersion, HostPlugin.PluginVersion);
            if (!_sessionStarted) return "the recorder session could not be started";
        }
        var annotation = Record(command.Id);
        annotation.Status = ExperimentRunStatus.Rejected;
        annotation.Total = command.Steps.Count;
        annotation.Message = "running";
        annotation.When = DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture);
        _previousHooked = ExperimentTrace.Capturing;
        if (UsesTraceWait(command)) ExperimentTrace.Start();
        Publish("command", command, null);
        Log($"started {command.Id} ({command.Steps.Count} steps) target={_spec.Describe()} host={host}");
        return null;
    }

    private void Update()
    {
        var engine = _engine;
        if (engine == null) return;
        if (engine.Finished) { Finish(); return; }
        _frameStart = Time.realtimeSinceStartup;
        var budget = Settings.InspectionBudget.Value;
        while (!engine.Finished)
        {
            var elapsed = (Time.realtimeSinceStartup - _frameStart) * 1000.0;
            var action = engine.Next(elapsed, budget);
            if (action == null) break;
            engine.Advance(action);
            if ((Time.realtimeSinceStartup - _frameStart) * 1000.0 >= budget) break;
        }
        if (engine.Finished) Finish();
    }

    /// <summary>Aborted runs still finish their cleanup stage, so the runner keeps ticking until then.</summary>
    internal void Tick() => Update();

    private void Finish()
    {
        var engine = _engine;
        var command = _command;
        _engine = null;
        _command = null;
        if (engine == null || command == null) return;
        var annotation = Record(command.Id);
        annotation.Status = engine.Failed ? ExperimentRunStatus.Failed : ExperimentRunStatus.Passed;
        annotation.Done = engine.Done;
        annotation.Total = command.Steps.Count;
        annotation.Message = engine.Failed ? engine.Blocked : _result.Length > 0 ? _result : "all steps reported";
        annotation.When = DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture);
        annotation.Milliseconds = (Stopwatch.GetTimestamp() - _startedTicks) * 1000.0 / Stopwatch.Frequency;
        Publish("result", command, annotation);
        SaveState();
        var cleaned = ExperimentObjects.DestroyAll();
        RecSession.Write("exp", "cleanup", json =>
        {
            json.WriteString("command", command.Id);
            json.WriteString("run", _runId);
            json.WriteString("objects", cleaned);
        });
        Add(new ExperimentEvent("cleanup", cleaned));
        if (!_previousHooked) ExperimentTrace.Stop();
        _previousHooked = false;
        if (_sessionStarted)
        {
            _sessionStarted = false;
            RecSession.Flush(TimeSpan.FromSeconds(2));
            RecSession.Stop();
        }
        Log($"{annotation.Status.ToString().ToLowerInvariant()} {command.Id}: {annotation.Done}/{annotation.Total} steps, {annotation.Message}");
    }

    private void OnOutcome(ExperimentOutcome outcome)
    {
        if (outcome.Kind == "read" && outcome.Ok)
        {
            var marker = outcome.Message.IndexOf(" :: ", StringComparison.Ordinal);
            if (marker >= 0) _result = outcome.Message[(marker + 4)..];
        }
        RecSession.Write("exp", "step", json =>
        {
            json.WriteString("command", _command?.Id ?? "");
            json.WriteString("run", _runId);
            json.WriteString("step", outcome.Kind);
            json.WriteBoolean("ok", outcome.Ok);
            json.WriteString("detail", RecReflect.Truncate(outcome.Message, 4096));
            json.WriteNumber("ms", Math.Round(outcome.Milliseconds, 4));
        });
        Add(new ExperimentEvent(outcome.Ok ? outcome.Kind : "error", outcome.Message));
    }

    private void Publish(string kind, ExperimentCommand command, ExperimentRecord? record)
    {
        RecSession.Write("exp", kind, json =>
        {
            json.WriteString("command", command.Id);
            json.WriteString("title", command.Title);
            json.WriteString("run", _runId);
            json.WriteString("target", command.Target);
            json.WriteString("host", command.Host.ToString().ToLowerInvariant());
            json.WriteString("source", command.Source);
            json.WriteStartArray("points");
            foreach (var point in command.Points) json.WriteStringValue(point);
            json.WriteEndArray();
            if (record == null) return;
            json.WriteString("status", record.Status.ToString().ToLowerInvariant());
            json.WriteNumber("done", record.Done);
            json.WriteNumber("total", record.Total);
            json.WriteString("message", RecReflect.Truncate(record.Message, 2048));
            json.WriteNumber("ms", Math.Round(record.Milliseconds, 4));
        });
    }

    private static bool UsesTraceWait(ExperimentCommand command)
    {
        static bool Scan(IReadOnlyList<ExperimentStep> steps)
        {
            foreach (var step in steps)
            {
                if (step.Kind == ExperimentStepKind.Wait && step.Wait.TraceType.Length > 0) return true;
                if (step.Kind == ExperimentStepKind.Repeat && Scan(step.Body)) return true;
            }
            return false;
        }
        return Scan(command.Steps) || Scan(command.Cleanup);
    }

    private static void Add(ExperimentEvent entry)
    {
        Events.Add(entry);
        if (Events.Count > MaximumEvents) Events.RemoveRange(0, Events.Count - MaximumEvents);
    }

    private static void Log(string message)
    {
        try { Plugin.PluginLog.LogInfo("experiment: " + message); }
        catch (Exception) { }
    }

    private static string StatePath => Path.Combine(Paths.BepInExRootPath, "config", "ForgeDevelopment", StateFolder, "state.json");

    private static void LoadState()
    {
        Records.Clear();
        try
        {
            if (!File.Exists(StatePath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(StatePath));
            if (!document.RootElement.TryGetProperty("commands", out var commands)) return;
            foreach (var entry in commands.EnumerateObject())
            {
                var value = entry.Value;
                Records[entry.Name] = new ExperimentRecord
                {
                    Status = Enum.TryParse<ExperimentRunStatus>(Text(value, "status"), true, out var status) ? status : ExperimentRunStatus.NotRun,
                    Done = Integer(value, "done"),
                    Total = Integer(value, "total"),
                    Message = Text(value, "message"),
                    When = Text(value, "when"),
                    Milliseconds = Number(value, "ms")
                };
            }
        }
        catch (Exception e)
        {
            Log("the previous experiment state could not be read: " + e.Message);
        }
    }

    private static void SaveState()
    {
        try
        {
            var path = StatePath;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("format", "forge-development-experiments");
            writer.WriteStartObject("commands");
            foreach (var (id, record) in Records.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(id);
                writer.WriteString("status", record.Status.ToString().ToLowerInvariant());
                writer.WriteNumber("done", record.Done);
                writer.WriteNumber("total", record.Total);
                writer.WriteString("message", record.Message);
                writer.WriteString("when", record.When);
                writer.WriteNumber("ms", Math.Round(record.Milliseconds, 4));
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        catch (Exception e)
        {
            Log("the experiment state could not be written: " + e.Message);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : "";

    private static int Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.TryGetInt32(out var value) ? value : 0;

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.TryGetDouble(out var value) ? value : 0;
}
