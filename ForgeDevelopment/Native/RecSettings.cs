using BepInEx.Configuration;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The recorder's own configuration. It has a section of its own because every key here is about one session's cost
/// and size, not about a diagnosis: an author who runs out of disk turns gzip or the budget down without touching the
/// performance or inspection settings.
/// </summary>
internal static partial class Settings
{
    internal static ConfigEntry<bool> RecorderEnabled = null!;
    internal static ConfigEntry<bool> RecorderGzip = null!;
    internal static ConfigEntry<int> RecorderBudgetGiB = null!;
    internal static ConfigEntry<int> RecorderSegmentMiB = null!;
    internal static ConfigEntry<int> RecorderRecordKiB = null!;
    internal static ConfigEntry<int> RecorderQueue = null!;
    internal static ConfigEntry<bool> RecorderTrace = null!;
    internal static ConfigEntry<KeyCode> RecorderBookmarkKey = null!;
    internal static ConfigEntry<string> RecorderBookmarkLabel = null!;
    internal static ConfigEntry<KeyCode> RecorderSnapshotKey = null!;
    internal static ConfigEntry<KeyCode> RecorderScreenshotKey = null!;
    internal static ConfigEntry<KeyCode> RecorderPanelKey = null!;

    internal static void BindRecorder(ConfigFile config)
    {
        RecorderEnabled = config.Bind("Recorder", "Enabled", true,
            "Record the whole authoring session into BepInEx/ForgeReports/rec-*: JSONL per channel, with the trace, log, bookmark and snapshot channels a later query reads. Authoring mode only; the player package does not contain this plugin.");
        RecorderGzip = config.Bind("Recorder", "GzipSegments", false,
            "Compress each segment with gzip as it is written. Off by default: a session under investigation is read with tail and jq far more often than it is archived.");
        RecorderBudgetGiB = config.Bind("Recorder", "BudgetGiB", 2,
            new ConfigDescription("Total bytes one session may write. The session stops writing at this size, counts every record it dropped, and says so on the session channel and in the screen corner.", new AcceptableValueRange<int>(1, 64)));
        RecorderSegmentMiB = config.Bind("Recorder", "SegmentMiB", 8,
            new ConfigDescription("Bytes per JSONL segment before the writer opens the next one.", new AcceptableValueRange<int>(1, 256)));
        RecorderRecordKiB = config.Bind("Recorder", "RecordKiB", 64,
            new ConfigDescription("Largest single record. One value over this budget is dropped and counted rather than growing the writer's memory.", new AcceptableValueRange<int>(4, 1024)));
        RecorderQueue = config.Bind("Recorder", "QueueRecords", 16384,
            new ConfigDescription("Records that may wait for the writer. A queue this full drops the newest record and counts it, which is the recorder saying the disk cannot keep up.", new AcceptableValueRange<int>(256, 262144)));
        RecorderTrace = config.Bind("Recorder", "TraceEnabled", true,
            "Install the native method tracing from BepInEx/config/ForgeDevelopment/trace/*.json. The first run copies them from the package's probes/trace.");
        RecorderBookmarkKey = config.Bind("Recorder", "BookmarkKey", KeyCode.F6, "Mark \"it just happened here\" in the session. The bookmark carries the label below and where the camera was looking.");
        RecorderBookmarkLabel = config.Bind("Recorder", "BookmarkLabel", "", "Label written with the next bookmark; empty writes the default label.");
        RecorderSnapshotKey = config.Bind("Recorder", "SnapshotKey", KeyCode.F7, "Ask every capture provider for a full state snapshot.");
        RecorderScreenshotKey = config.Bind("Recorder", "ScreenshotKey", KeyCode.F8, "Capture the screen into the session directory and record its path.");
        RecorderPanelKey = config.Bind("Recorder", "ExperimentPanelKey", KeyCode.F5, "Toggle the experiment panel.");
    }
}
