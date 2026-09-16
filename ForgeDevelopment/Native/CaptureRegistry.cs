using System;
using System.Globalization;
using HarmonyLib;
using LevelGeneration;
using SNetwork;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The capture side's entry point. It owns the snapshot schedule, the change pass and the Harmony patches that say
/// when the game changed something worth a snapshot. The core calls <see cref="EnsureStarted"/> once after the
/// session opens and <see cref="SnapshotAll"/> on the snapshot hotkey; everything else here is driven by the game's
/// own calls, so a reason is never invented by a timer that guessed.
/// </summary>
internal static class CaptureRegistry
{
    private const string Where = "ForgeDevelopment.Native.CaptureRegistry";

    private static CaptureMonitor? _monitor;
    private static CaptureChangeDetector? _changes;
    private static long _snapshots;
    private static long _changed;
    private static long _lastPeriodicTick;
    private static long _lastChangeTick;
    private static float _periodicAccumulator;
    private static long _lastSnapshotAt = -MinSnapshotIntervalMs;
    private static long _coalesced;
    private static string _lastCoalescedReason = "";
    private static bool _snapshotting;
    private static string _lastReason = CaptureReason.Manual;
    private static int _periodicSeconds = DefaultPeriodicSeconds;
    private static bool _started;

    /// <summary>The low-frequency snapshot cadence. Thirty seconds is the default the task names; the value is
    /// clamped so a configuration mistake cannot turn the snapshot into a frame cost.</summary>
    internal const int DefaultPeriodicSeconds = 30;
    internal const int MinPeriodicSeconds = 5;
    internal const int MaxPeriodicSeconds = 600;

    /// <summary>The shortest gap between two full snapshots. Triggers are postfixes inside the game's own calls
    /// (`Dimension.NotifyWarpableObjects` runs per player per warp), so a burst of events must cost one walk, not one
    /// walk each.</summary>
    internal const int MinSnapshotIntervalMs = 500;

    /// <summary>Rows and milliseconds one per-frame change pass may spend. They are the budget the change record
    /// reports, so the number in the record and the number enforced are the same one.</summary>
    internal const int ChangeRowsPerFrame = 4096;
    internal const int ChangeMillisecondsPerFrame = 2;

    internal static bool Active => _started && RecSession.Active;
    internal static long Snapshots => _snapshots;
    internal static long Changes => _changed;

    /// <summary>The reasons the capture answers, in the order the game produces them in one expedition. The probe
    /// index publishes this so a reader knows which snapshot reasons a session should contain.</summary>
    internal static readonly string[] Reasons = CaptureReason.All;

    internal static void EnsureStarted(UnityEngine.MonoBehaviour host, int periodicSeconds)
    {
        ArgumentNullException.ThrowIfNull(host);
        _periodicSeconds = Math.Clamp(periodicSeconds, MinPeriodicSeconds, MaxPeriodicSeconds);
        if (_started) return;
        _changes = new CaptureChangeDetector(ChangeRowsPerFrame, ChangeMillisecondsPerFrame);
        _monitor = host.gameObject.GetComponent<CaptureMonitor>();
        if (_monitor == null) _monitor = host.gameObject.AddComponent<CaptureMonitor>();
        _monitor.IntervalSeconds = _periodicSeconds;
        _started = true;
        RecSession.Note("capture_started", json =>
        {
            json.WriteString("periodicSeconds", _periodicSeconds.ToString(CultureInfo.InvariantCulture));
            json.WriteNumber("changeRowsPerFrame", ChangeRowsPerFrame);
            json.WriteNumber("changeMsPerFrame", ChangeMillisecondsPerFrame);
            json.WritePropertyName("reasons");
            json.WriteStartArray();
            foreach (var reason in Reasons) json.WriteStringValue(reason);
            json.WriteEndArray();
        });
    }

    /// <summary>Releases the capture after a startup that unwound. The snapshot and change state is dropped with the
    /// monitor component, so a later <see cref="EnsureStarted"/> starts from the level it is actually in.</summary>
    internal static void Stop()
    {
        if (_monitor != null) UnityEngine.Object.Destroy(_monitor);
        _monitor = null;
        _changes = null;
        _started = false;
        _snapshotting = false;
        _periodicAccumulator = 0f;
        _lastSnapshotAt = -MinSnapshotIntervalMs;
    }

    /// <summary>The full snapshot. <paramref name="reason"/> is what asked for it, and it decides the sections: the
    /// periodic and hotkey snapshots take everything, a level change takes the world, and the data block export
    /// happens only for the reasons that start or end a level.
    /// A trigger that arrives while a snapshot is running, or within <see cref="MinSnapshotIntervalMs"/> of one, is
    /// counted and dropped: the triggers are postfixes inside the game's own calls, and a full walk per event would
    /// be the recorder measuring itself. The dropped reason is written so the record says what it coalesced.</summary>
    internal static CaptureSnapshot? SnapshotAll(string reason)
    {
        if (!Active) return null;
        if (_snapshotting) { _coalesced++; return null; }
        var now = Environment.TickCount64;
        if (now - _lastSnapshotAt < MinSnapshotIntervalMs)
        {
            _coalesced++;
            _lastCoalescedReason = reason;
            return null;
        }
        _snapshotting = true;
        try
        {
            var tick = Now();
            var snapshot = CaptureWorld.Collect(new CaptureOptions(reason, tick)
            {
                IncludeText = reason is CaptureReason.Manual or CaptureReason.Periodic or CaptureReason.LevelGenerated,
                IncludeEnvironment = reason is CaptureReason.Manual or CaptureReason.Periodic,
                IncludeNavMarkers = reason is not CaptureReason.Change
            });
            CaptureStateWriter.Write(snapshot);
            _snapshots++;
            _lastReason = reason;
            _lastSnapshotAt = Environment.TickCount64;
            if (CaptureReason.CapturesDataBlocks(reason)) SnapshotDataBlocks(reason, tick);
            if (reason == CaptureReason.LevelEntered || reason == CaptureReason.CheckpointRestored) _changes?.Reset();
            SeedChanges(snapshot, reason);
            if (_coalesced != 0)
            {
                var coalesced = _coalesced;
                var last = _lastCoalescedReason;
                _coalesced = 0;
                snapshot.Notes.Add(coalesced.ToString(CultureInfo.InvariantCulture) + " earlier reasons coalesced into this snapshot, last was " + last);
            }
            return snapshot;
        }
        catch (Exception error)
        {
            Log("snapshot:" + reason, error);
            return null;
        }
        finally { _snapshotting = false; }
    }

    /// <summary>The data block export. It runs once per level start and once per level end, which is what "the blocks
    /// this level used" means; running it every snapshot would repeat the same table all expedition.</summary>
    internal static void SnapshotDataBlocks(string reason, long tick)
    {
        if (!Active) return;
        try
        {
            var snapshot = CaptureWorld.CollectDataBlocks(CaptureReason.DataBlocks, tick, 512);
            RecSession.Write("state", "datablocks", json =>
            {
                json.WriteString("reason", reason);
                json.WriteNumber("tick", tick);
                json.WriteNumber("rows", snapshot.RowCount);
                json.WritePropertyName("types");
                json.WriteStartArray();
                foreach (var section in snapshot.Sections)
                    foreach (var row in section.Rows)
                    {
                        if (!string.Equals(row.Kind, "dataBlockType", StringComparison.Ordinal)) continue;
                        json.WriteStartObject();
                        json.WriteString("type", row.Name);
                        foreach (var field in row.Fields) json.WriteString(field.Key, field.Value);
                        json.WriteEndObject();
                    }
                json.WriteEndArray();
                json.WritePropertyName("blocks");
                json.WriteStartArray();
                foreach (var section in snapshot.Sections)
                    foreach (var row in section.Rows)
                    {
                        if (!string.Equals(row.Kind, "dataBlock", StringComparison.Ordinal)) continue;
                        json.WriteStartObject();
                        json.WriteString("type", row.Name);
                        foreach (var field in row.Fields) json.WriteString(field.Key, field.Value);
                        json.WriteEndObject();
                    }
                json.WriteEndArray();
                if (snapshot.Notes.Count != 0)
                {
                    json.WritePropertyName("notes");
                    json.WriteStartArray();
                    foreach (var note in snapshot.Notes) json.WriteStringValue(note);
                    json.WriteEndArray();
                }
            });
        }
        catch (Exception error) { Log("datablocks", error); }
    }

    /// <summary>
    /// The per-frame change pass. It is deliberately not a snapshot: it compares a few fields of the objects a change
    /// record is defined over and writes only the rows that moved, with the cost it spent. A frame that finds nothing
    /// writes nothing beyond its periodic counters.
    /// </summary>
    internal static void Tick()
    {
        if (!Active || _changes == null) return;
        try
        {
            var tick = Now();
            var snapshot = CaptureWorld.Collect(new CaptureOptions(CaptureReason.Change, tick)
            {
                IncludeText = false,
                IncludeEnvironment = false,
                IncludeNavMarkers = false
            });
            var result = _changes.Compare(CaptureStateWriter.Select(snapshot, CaptureStateWriter.ChangeSections));
            _lastChangeTick = tick;
            if (result.Changed == 0 && result.Added == 0 && result.Removed == 0 && !result.BudgetExhausted) return;
            _changed++;
            CaptureStateWriter.WriteChanges(result, tick, "world");
        }
        catch (Exception error) { Log("changes", error); }
    }

    /// <summary>The periodic cadence. It is a tick counter rather than a timer so a paused or unfocused game does not
    /// accumulate missed snapshots and then take several at once.</summary>
    internal static bool DueForPeriodic(float deltaSeconds)
    {
        if (!Active) return false;
        _periodicAccumulator += deltaSeconds;
        if (_periodicAccumulator < _periodicSeconds) return false;
        _periodicAccumulator = 0f;
        _lastPeriodicTick = Now();
        return true;
    }

    private static void SeedChanges(CaptureSnapshot snapshot, string reason)
    {
        if (_changes == null) return;
        // A full snapshot already read every row; handing them to the detector means the first change pass after it
        // compares against the snapshot rather than reporting the whole level as new.
        _changes.Compare(CaptureStateWriter.Select(snapshot, CaptureStateWriter.ChangeSections));
    }

    internal static string Describe()
        => "reason=" + _lastReason + " snapshots=" + _snapshots.ToString(CultureInfo.InvariantCulture)
            + " changes=" + _changed.ToString(CultureInfo.InvariantCulture)
            + " coalesced=" + _coalesced.ToString(CultureInfo.InvariantCulture)
            + " changeTick=" + _lastChangeTick.ToString(CultureInfo.InvariantCulture)
            + " periodicTick=" + _lastPeriodicTick.ToString(CultureInfo.InvariantCulture)
            + " " + (_changes?.Describe() ?? "changes=off");

    /// <summary>The frame number a snapshot and its change record are stamped with. It is read here rather than from
    /// the session context so a capture error cannot come from the thing that labels the capture.</summary>
    private static long Now()
    {
        try { return Time.frameCount; }
        catch (Exception) { return -1; }
    }

    internal static void Log(string what, Exception error)
    {
        try
        {
            RecSession.Write("log", "capture_error", json =>
            {
                json.WriteString("where", Where + ":" + what);
                json.WriteString("error", error.GetType().Name);
                json.WriteString("message", RecReflect.Truncate(error.Message, 200));
            });
        }
        catch (Exception) { }
    }
}

/// <summary>The snapshot clock. It lives on the plugin object and does nothing but count seconds and drive the change
/// pass, so the capture has exactly one place per frame where it spends time.</summary>
internal sealed class CaptureMonitor : MonoBehaviour
{
    internal float IntervalSeconds { get; set; } = CaptureRegistry.DefaultPeriodicSeconds;

    private void Update()
    {
        try
        {
            if (IntervalSeconds > 0f && CaptureRegistry.DueForPeriodic(Time.unscaledDeltaTime)) CaptureRegistry.SnapshotAll(CaptureReason.Periodic);
            CaptureRegistry.Tick();
        }
        catch (Exception error) { CaptureRegistry.Log("monitor", error); }
    }
}

/// <summary>One snapshot reason, declared on the game method that produces it. Every patch answers the same question
/// "did the game just cross a boundary a snapshot is defined at", and every one of them defers the work to the
/// scheduler instead of recording inside the native call.</summary>
internal static class CaptureTriggers
{
    private static void Fire(string reason)
    {
        try { CaptureRegistry.SnapshotAll(reason); }
        catch (Exception error) { CaptureRegistry.Log("trigger:" + reason, error); }
    }

    [HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.DoChangeState))]
    internal static class StateChanged
    {
        private static void Postfix() => Fire(CaptureReason.LevelEntered);
    }

    [HarmonyPatch(typeof(LG_Factory), nameof(LG_Factory.FactoryDone))]
    internal static class FactoryFinished
    {
        private static void Postfix() => Fire(CaptureReason.LevelGenerated);
    }

    [HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.StoreCheckpoint))]
    internal static class CheckpointStored
    {
        private static void Postfix() => Fire(CaptureReason.CheckpointStored);
    }

    [HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.ReloadCheckpoint))]
    internal static class CheckpointRestored
    {
        private static void Postfix() => Fire(CaptureReason.CheckpointRestored);
    }

    [HarmonyPatch(typeof(Dimension), nameof(Dimension.NotifyWarpableObjects))]
    internal static class DimensionWarped
    {
        private static void Postfix() => Fire(CaptureReason.DimensionChanged);
    }

    [HarmonyPatch(typeof(SNet_SyncManager), nameof(SNet_SyncManager.OnFoundNewMasterDuringMigration))]
    internal static class HostMigrated
    {
        private static void Postfix() => Fire(CaptureReason.HostMigrated);
    }

    [HarmonyPatch(typeof(SNet_SyncManager), nameof(SNet_SyncManager.OnPlayerJoinedSessionHub))]
    internal static class PlayerJoined
    {
        private static void Postfix() => Fire(CaptureReason.PlayerJoined);
    }

    [HarmonyPatch(typeof(SNet_SyncManager), nameof(SNet_SyncManager.OnPlayerLeftSessionHub))]
    internal static class PlayerLeft
    {
        private static void Postfix() => Fire(CaptureReason.PlayerLeft);
    }

    [HarmonyPatch(typeof(RundownManager), nameof(RundownManager.OnExpeditionEnded))]
    internal static class LevelEnded
    {
        private static void Postfix() => Fire(CaptureReason.LevelEnded);
    }
}


