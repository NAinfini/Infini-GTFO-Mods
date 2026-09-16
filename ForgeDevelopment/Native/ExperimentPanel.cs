using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The F5 experiment panel. IMGUI rather than a cloned TMP hierarchy: the panel is a development tool that
/// must work on whichever UI prefab the running build happens to have, it owns no game object, and it is
/// removed in one line when this package is not installed. The TMP route would have to clone a live HUD
/// element, re-parent it and survive level cleanup, resolution changes and focus states for no benefit here.
/// </summary>
internal sealed class ExperimentPanel : MonoBehaviour
{
    private const int MaximumRows = 400;
    private const float PanelWidth = 720f;
    private const float PanelHeight = 520f;

    private static readonly Rect Window = new(24f, 24f, PanelWidth, PanelHeight);

    private static ExperimentPanel? _instance;
    private static bool _open;
    private static int _selected;
    private static Vector2 _listScroll;
    private static Vector2 _detailScroll;

    public ExperimentPanel(IntPtr pointer) : base(pointer) { }

    private void Awake()
    {
        _instance = this;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    /// <summary>Called by the core's F5 binding.</summary>
    internal static void Toggle()
    {
        _open = !_open;
        if (_open && _instance == null) Plugin.PluginLog.LogWarning("experiment panel: the runner component is not attached");
    }

    internal static bool IsOpen => _open;

    /// <summary>Reloads the command files from the config directory, so a new experiment needs no restart. The
    /// authoring branch calls it once the component is attached; the panel's own button calls it again.</summary>
    internal static void Load()
    {
        try
        {
            var seed = ExperimentRunner.Load(ShippedCommandsPath);
            foreach (var problem in ExperimentCatalog.LoadProblems) Plugin.PluginLog.LogWarning("experiment: " + problem);
            if (ExperimentCatalog.All.Count == 0) Plugin.PluginLog.LogWarning("experiment: no usable command in " + ExperimentCatalog.DirectoryPath + " (" + seed + ")");
            RecSession.Write("session", "experiments_loaded", json =>
            {
                json.WriteString("directory", ExperimentCatalog.DirectoryPath);
                json.WriteNumber("commands", ExperimentCatalog.All.Count);
                json.WriteNumber("problems", ExperimentCatalog.LoadProblems.Count);
            });
        }
        catch (Exception e)
        {
            Plugin.PluginLog.LogError("experiment: command loading failed: " + e);
        }
    }

    private static string ShippedCommandsPath
    {
        get
        {
            try
            {
                var plugin = System.IO.Path.GetDirectoryName(typeof(ExperimentPanel).Assembly.Location) ?? "";
                var profiles = new System.IO.DirectoryInfo(plugin).Parent?.Parent;
                var candidate = profiles == null ? null : System.IO.Path.Combine(profiles.FullName, "probes", "commands");
                return candidate ?? System.IO.Path.Combine(plugin, "probes", "commands");
            }
            catch (Exception)
            {
                return "probes/commands";
            }
        }
    }

    private void OnGUI()
    {
        if (!_open) return;
        var previous = GUI.skin;
        try
        {
            GUI.skin = null;
            Draw();
        }
        catch (Exception e)
        {
            Plugin.PluginLog.LogError("experiment panel draw failed: " + e.Message);
            _open = false;
        }
        finally
        {
            GUI.skin = previous;
        }
    }

    private static void Draw()
    {
        GUILayout.BeginArea(Window, GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label("<b>Forge Development experiments (F5)</b>", new GUIStyle(GUI.skin.label) { richText = true });
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("reload files", GUILayout.Width(110f))) Load();
        if (GUILayout.Button("close", GUILayout.Width(70f))) _open = false;
        GUILayout.EndHorizontal();

        var commands = ExperimentCatalog.All;
        GUILayout.Label(commands.Count.ToString(CultureInfo.InvariantCulture) + " commands from " + ExperimentCatalog.DirectoryPath);
        GUILayout.Label("crosshair: " + ExperimentRunner.Describe("lookedAtEnemy") + " | " + ExperimentRunner.Describe("lookedAtDoor") + " | " + ExperimentRunner.Describe("lookedAtTerminal"));
        GUILayout.Label("nearest: " + ExperimentRunner.Describe("nearestGenerator") + " | " + ExperimentRunner.Describe("nearestTerminal"));
        GUILayout.Label("experiment objects alive: " + ExperimentObjects.Count.ToString(CultureInfo.InvariantCulture) + "   [R] destroy them");
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.R)
        {
            Plugin.PluginLog.LogInfo("experiment: " + ExperimentRunner.CleanUp());
            Event.current.Use();
        }
        if (ExperimentRunner.Running)
        {
            GUILayout.Label("running: " + ExperimentRunner.CurrentCommand + "   [Esc] cancel");
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                ExperimentRunner.Cancel();
                Event.current.Use();
            }
        }

        var selected = Selection(commands);
        _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Height(220f));
        var group = "";
        var index = 0;
        foreach (var command in commands)
        {
            var points = command.Points.Count == 0 ? "unassigned" : string.Join(",", command.Points);
            if (!string.Equals(points, group, StringComparison.Ordinal))
            {
                group = points;
                GUILayout.Label("<b>" + group + "</b>", new GUIStyle(GUI.skin.label) { richText = true });
            }
            var record = ExperimentRunner.Record(command.Id);
            var line = (index == selected ? "> " : "  ") + Status(record) + "  " + command.Id + "   [" + command.Host.ToString().ToLowerInvariant() + "]";
            var style = new GUIStyle(GUI.skin.label) { richText = false };
            if (record.Status == ExperimentRunStatus.Failed || record.Status == ExperimentRunStatus.Rejected) style.normal.textColor = new Color(1f, 0.55f, 0.4f);
            else if (record.Status == ExperimentRunStatus.Passed) style.normal.textColor = new Color(0.5f, 1f, 0.6f);
            if (GUILayout.Button(line, style)) _selected = index;
            if (!string.IsNullOrEmpty(record.Message) && index == selected)
                GUILayout.Label("      " + Truncate(record.Message, 160));
            index++;
            if (index >= MaximumRows) break;
        }
        GUILayout.EndScrollView();

        var current = selected >= 0 && selected < commands.Count ? commands[selected] : null;
        var chosen = current;
        if (chosen != null)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                Start(selected);
                Event.current.Use();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("run selected [Enter]", GUILayout.Width(160f))) Start(selected);
            if (GUILayout.Button("copy id", GUILayout.Width(80f))) GUIUtility.systemCopyBuffer = chosen.Id;
            GUILayout.EndHorizontal();
            _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUILayout.Height(150f));
            GUILayout.Label("<b>" + chosen.Title + "</b>", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label("source: " + chosen.Source);
            GUILayout.Label("target: " + chosen.Target + "   -> " + Describe(chosen.Target));
            GUILayout.Label("points: " + (chosen.Points.Count == 0 ? "none" : string.Join(", ", chosen.Points)));
            if (chosen.Requires.Count > 0) GUILayout.Label("requires: " + string.Join("; ", chosen.Requires));
            foreach (var step in chosen.Steps.Take(24)) GUILayout.Label("  " + Describe(step));
            if (chosen.Cleanup.Count > 0) GUILayout.Label("cleanup: " + chosen.Cleanup.Count + " step(s)");
            var record = ExperimentRunner.Record(chosen.Id);
            if (record.When.Length > 0)
                GUILayout.Label("last: " + record.Status.ToString().ToLowerInvariant() + " " + record.Done + "/" + record.Total + " at " + record.When + " (" + record.Milliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms)");
            GUILayout.EndScrollView();
        }

        GUILayout.Label("<b>recent</b>", new GUIStyle(GUI.skin.label) { richText = true });
        foreach (var entry in ExperimentRunner.Recent.TakeLast(6))
            GUILayout.Label("  [" + entry.Type + "] " + Truncate(entry.Message, 150));
        GUILayout.EndArea();
    }

    private static int Selection(IReadOnlyList<ExperimentCommand> commands)
    {
        if (commands.Count == 0) { _selected = -1; return -1; }
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode is KeyCode.UpArrow or KeyCode.DownArrow)
        {
            var delta = Event.current.keyCode == KeyCode.UpArrow ? -1 : 1;
            _selected = Math.Clamp((_selected < 0 ? 0 : _selected) + delta, 0, commands.Count - 1);
            Event.current.Use();
        }
        if (_selected < 0 || _selected >= commands.Count) _selected = 0;
        return _selected;
    }

    private static void Start(int index)
    {
        if (index < 0) return;
        var error = ExperimentRunner.Run(index);
        if (error != null) Plugin.PluginLog.LogWarning("experiment: " + error);
    }

    private static string Status(ExperimentRecord record) => record.Status switch
    {
        ExperimentRunStatus.Passed => "[done]",
        ExperimentRunStatus.Failed => "[fail]",
        ExperimentRunStatus.Rejected => "[skip]",
        ExperimentRunStatus.Aborted => "[stop]",
        _ => "[    ]"
    };

    private static string Describe(string target) =>
        ExperimentTargetSpec.TryParse(target, out var spec) ? spec.Describe() : "unknown selector";

    private static string Describe(ExperimentStep step) => step.Kind switch
    {
        ExperimentStepKind.Call => "call " + step.Name + "(" + step.Arguments.Count + ")",
        ExperimentStepKind.Set => "set " + step.Path,
        ExperimentStepKind.Read => "read " + string.Join(", ", step.Paths) + (step.Screenshot ? " + screenshot" : ""),
        ExperimentStepKind.Wait => "wait " + DescribeWait(step.Wait),
        ExperimentStepKind.Screenshot => "screenshot" + (step.Reason.Length > 0 ? " " + step.Reason : ""),
        ExperimentStepKind.WorldEvent => "worldEvent",
        ExperimentStepKind.Repeat => "repeat x" + step.Times + " (" + step.Body.Count + " steps)",
        _ => step.Kind.ToString()
    };

    private static string DescribeWait(ExperimentWait wait)
    {
        if (wait.Frames is { } frames) return frames + " frames";
        if (wait.Seconds is { } seconds) return seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        if (wait.TraceType.Length > 0) return "trace " + wait.TraceType + "." + wait.TraceMethod + " within " + wait.TimeoutFrames + " frames";
        if (wait.Path.Length > 0) return (wait.Changed ? "change of " : "presence of ") + wait.Path + " within " + wait.TimeoutFrames + " frames";
        return "nothing";
    }

    private static string Truncate(string text, int maximum) => text.Length <= maximum ? text : text[..maximum] + "…";
}
