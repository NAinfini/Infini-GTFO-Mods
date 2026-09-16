using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using BepInEx.Logging;
using ForgeRuntime.Framework;

namespace ForgeRuntime.Logging;

/// <summary>Constructor inputs only so tests can inject small values; the host always passes <see cref="Default"/>.</summary>
internal sealed record RuntimeLogLimits(int PlayerLinesPerTick, int PlayerQueue, int ElevatedLinesPerTick, int ElevatedQueue,
    long MaximumFileBytes, int RetainedFiles)
{
    internal static readonly RuntimeLogLimits Default = new(256, 8192, 4096, 65536, 64L * 1024 * 1024, 10);
}

/// <summary>forge.log JSONL writer. BepInEx logging never happens on the background thread: <see cref="Write"/> and
/// <see cref="Dispose"/> run on the kernel thread and mirror the console, while the one background thread created by the
/// first record only opens the file, serializes, writes and prunes. What that thread could not write is reported once by
/// <see cref="Dispose"/>, because a session's loss is only final once the writer has drained its queue and closed its
/// file.</summary>
internal sealed class RuntimeLogWriter : IRuntimeLogSink, IDisposable
{
    internal const string Schema = "forge.log";
    internal const string DirectoryName = "forge-logs";
    // Stop runs from Unity quit/destroy on the main thread: a longer wait visibly hangs shutdown, while a working disk
    // drains even a full elevated queue far sooner. The thread is a background thread, so a stuck disk cannot keep the process alive.
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    // Keeps room for the final log.dropped line under the file cap.
    private const int DroppedLineReserve = 512;

    private readonly string directory;
    private readonly ManualLogSource console;
    private readonly RuntimeLogLimits limits;
    private BlockingCollection<Entry>? queue;
    private Thread? thread;
    private volatile string? filePath;
    private RuntimeLogLevels? announced;
    private RuntimeLogTier? announcedTier;
    private readonly string runtimeProvider;
    private long seq, tick = long.MinValue, epoch = long.MinValue, dropped, droppedTick, droppedEpoch;
    // What the writer thread could not write: the records a file failure cost and the reason. It publishes both so the
    // kernel thread can report them, and reads them nowhere itself. BepInEx listeners must not run on the writer thread —
    // a listener that touches IL2CPP objects there takes the process down — so the one report happens in Dispose, after
    // the join that both publishes the fields and ends the run whose loss they describe.
    private long unwritten;
    private volatile string? unwrittenReason;
    private int linesThisTick;
    private bool disposed;

    internal RuntimeLogWriter(string directory, string runtimeProvider, ManualLogSource console, RuntimeLogLimits limits)
    {
        this.directory = Path.GetFullPath(directory);
        this.runtimeProvider = runtimeProvider;
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    internal bool Started => thread != null;
    /// <summary>Set by the background thread once the session file exists; read it after Dispose.</summary>
    internal string? FilePath { get => filePath; private set => filePath = value; }

    public void Write(in RuntimeLogRecord record, RuntimeLogLevels levels)
    {
        if (disposed) throw new ObjectDisposedException(nameof(RuntimeLogWriter));
        if (queue == null) Start();
        var elevated = levels.Tier == RuntimeLogTier.Elevated;
        if (record.Tick != tick || record.WorldEpoch != epoch) { tick = record.Tick; epoch = record.WorldEpoch; linesThisTick = 0; }
        if (linesThisTick >= (elevated ? limits.ElevatedLinesPerTick : limits.PlayerLinesPerTick)
            || queue!.Count >= (elevated ? limits.ElevatedQueue : limits.PlayerQueue))
        { dropped++; droppedTick = record.Tick; droppedEpoch = record.WorldEpoch; return; }
        linesThisTick++;
        // The contract allows at most two log.level lines in a file: the first real record opens the file with the current
        // table, and elevation is the one later change that is announced. Registration adds providers to the table without
        // a line of its own, so a file with N registrations still carries at most two.
        if (announcedTier != levels.Tier)
        {
            announcedTier = levels.Tier;
            announced = levels;
            var level = new Entry(++seq, new RuntimeLogRecord { Level = RuntimeLogLevel.Info, Code = RuntimeLogCodes.LogLevel,
                Provider = levels.RuntimeProvider, Tick = record.Tick, WorldEpoch = record.WorldEpoch }, levels, 0);
            Mirror(level, Message(level));
            queue.Add(level);
        }
        if (dropped != 0) AddDropped();
        var entry = new Entry(++seq, record, null, 0);
        Mirror(entry, Message(entry));
        queue.Add(entry);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (queue == null) return;
        if (dropped != 0) AddDropped();
        queue.CompleteAdding();
        bool finished = thread!.Join(StopTimeout);
        // The session's file loss is reported here, exactly once: the join above both ends the run that counts it and
        // publishes the count, and this is the kernel thread, the only place BepInEx may be called from. The queue is
        // closed by now, so this report reaches the console and no file.
        var lost = Interlocked.Exchange(ref unwritten, 0);
        if (lost != 0 || unwrittenReason != null)
        {
            var entry = DroppedEntry(lost, tick, epoch, "Records were not written to " + (filePath ?? directory) + ": "
                + (unwrittenReason ?? "the session file reached its size limit"));
            Mirror(entry, Message(entry));
        }
        if (!finished)
            console.LogWarning("Forge log writer did not finish within " + StopTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                + " s; " + queue.Count.ToString(CultureInfo.InvariantCulture) + " queued records were not written.");
    }

    private void Start()
    {
        queue = new BlockingCollection<Entry>(new ConcurrentQueue<Entry>());
        thread = new Thread(Run) { IsBackground = true, Name = "Forge log writer" };
        thread.Start();
    }

    // log.level and log.dropped bypass the per-tick and queue limits; drops coalesce, so at most one extra entry is pending.
    private void AddDropped()
    {
        var entry = DroppedEntry(dropped, droppedTick, droppedEpoch, null);
        dropped = 0;
        // This runs on the kernel thread like every other mirror, and the count is consumed exactly once, so the report
        // reaches the console exactly once and never from the writer thread.
        Mirror(entry, Message(entry));
        queue!.Add(entry);
    }

    /// <summary>One `log.dropped` report: how many records a provider logged and the writer did not carry, with the reason
    /// when a file failure caused it. A report is never one of the records it accounts for.</summary>
    private Entry DroppedEntry(long count, long atTick, long atEpoch, string? detail) => new(++seq,
        // A session whose very first record was already dropped has no announced table yet, so the Runtime provider is
        // carried from construction rather than from the level line.
        new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.LogDropped,
            Provider = announced?.RuntimeProvider ?? runtimeProvider, Tick = atTick, WorldEpoch = atEpoch },
        null, count, detail, isReport: true);

    // Runs on the kernel thread only: BepInEx dispatches synchronously to every ILogListener in the calling thread.
    private void Mirror(in Entry entry, string message)
    {
        var level = entry.Record.Level;
        if (level == RuntimeLogLevel.Error) console.LogError(message);
        else if (level == RuntimeLogLevel.Info) console.LogInfo(message);
    }

    // Runs on the background thread: no BepInEx call, no Unity call, no console mirror. It counts what the file did not
    // receive and records why, for the one report Dispose writes on the kernel thread once this run has ended.
    private void Run()
    {
        FileStream? file = null;
        long lost = 0;
        try
        {
            var buffer = new ArrayBufferWriter<byte>(1024);
            using var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            file = Open();
            long written = 0, room = limits.MaximumFileBytes - DroppedLineReserve;
            bool capped = false;
            Entry last = default;
            foreach (var entry in queue!.GetConsumingEnumerable())
            {
                last = entry;
                Serialize(json, buffer, entry, Message(entry));
                if (file == null) { if (IsRecord(entry)) lost++; continue; }
                if (capped || written + buffer.WrittenCount > room)
                {
                    capped = true;
                    unwrittenReason ??= "the session file reached its size limit";
                    if (IsRecord(entry)) lost++;
                    continue;
                }
                // Append answers with the file that survived the write, so a null here is the write-failure path that
                // follows the never-opened one above: either way this record and every later one has nowhere to go.
                file = Append(file, buffer, ref written);
                if (file == null) { if (IsRecord(entry)) lost++; continue; }
                if (queue.Count == 0) file.Flush();
            }
            // The run closes the file with one footer holding every record it never received, so a capped session cannot
            // be mistaken for a complete one. The footer is no record of its own: if it cannot be written either, the
            // reason the report carries is the write failure that stopped it.
            if (lost != 0 && file != null)
            {
                var footer = new Entry(last.Seq + 1, new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.LogDropped,
                    Provider = last.Record.Provider, Tick = last.Record.Tick, WorldEpoch = last.Record.WorldEpoch }, null, lost, null, isReport: true);
                Serialize(json, buffer, footer, Message(footer));
                file = Append(file, buffer, ref written);
            }
            file?.Flush();
        }
        catch (Exception error)
        {
            // An unhandled exception on this thread would terminate the game process. The entry it was holding is lost
            // with every record already skipped, and the reason names the crash rather than a file that may be fine.
            unwrittenReason = error.GetType().Name + ": " + error.Message;
            lost++;
        }
        finally
        {
            file?.Dispose();
            if (lost != 0) Interlocked.Add(ref unwritten, lost);
        }
    }

    private FileStream? Open()
    {
        try
        {
            Directory.CreateDirectory(directory);
            var name = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-"
                + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant() + ".jsonl";
            var path = Path.Combine(directory, name);
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            FilePath = path;
            Retain(path);
            return file;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Reason only: the records this costs are counted by the run and reported with it at Dispose.
            unwrittenReason = "no file could be created in " + directory + ": " + error.Message;
            return null;
        }
    }

    private void Retain(string current)
    {
        var stale = new DirectoryInfo(directory).GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(f => string.Equals(f.Extension, ".jsonl", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(f.FullName, current, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(limits.RetainedFiles - 1).ToArray();
        int failures = 0; string? first = null;
        foreach (var file in stale)
        {
            try { file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { failures++; first ??= file.Name + ": " + error.Message; }
        }
        // Only when a deletion actually failed, and only until the loss of a record gives the report a better reason:
        // retention runs on the writer thread every session, while a disk refuses rarely.
        if (failures != 0) unwrittenReason ??= "retention could not delete " + failures.ToString(CultureInfo.InvariantCulture)
            + " of " + stale.Length.ToString(CultureInfo.InvariantCulture) + " old session files (" + first + ")";
    }

    /// <summary>A record a provider logged, as opposed to the `log.level` table or a `log.dropped` report: the writer's own
    /// lines are how a session describes itself, so nothing counts them as records a provider lost.</summary>
    private static bool IsRecord(in Entry entry) => entry.Levels == null && !entry.IsReport;

    /// <summary>Writes the serialized record and answers with the file that survived it. A write that fails closes and
    /// drops the file here, so the run says the rest has nowhere to write and this reason covers every record it costs:
    /// the file is never handed back, and a failed write is never retried.</summary>
    private FileStream? Append(FileStream file, ArrayBufferWriter<byte> buffer, ref long written)
    {
        try { file.Write(buffer.WrittenSpan); written += buffer.WrittenCount; return file; }
        catch (IOException error)
        {
            unwrittenReason = "the session file write failed: " + error.Message;
            file.Dispose();
            return null;
        }
    }

    private static void Serialize(Utf8JsonWriter json, ArrayBufferWriter<byte> buffer, in Entry entry, string message)
    {
        buffer.Clear(); json.Reset();
        var r = entry.Record;
        json.WriteStartObject();
        json.WriteString("schema", Schema);
        json.WriteString("origin", "game");
        json.WriteNumber("seq", entry.Seq);
        json.WriteNumber("tick", r.Tick);
        json.WriteNumber("worldEpoch", r.WorldEpoch);
        if (r.Frame is long frame) json.WriteNumber("frame", frame);
        json.WriteString("level", Name(r.Level));
        json.WriteString("code", r.Code);
        json.WriteString("provider", r.Provider);
        Optional(json, "subjectProvider", r.SubjectProvider);
        Optional(json, "commandId", r.CommandId);
        Optional(json, "eventId", r.EventId);
        Optional(json, "causeId", r.CauseId);
        Optional(json, "rootEventId", r.RootEventId);
        if (r.Plan is RuntimeLogPlan plan)
        {
            json.WriteStartObject("plan");
            json.WriteString("planId", plan.PlanId);
            json.WriteStartObject("resource");
            json.WriteString("id", plan.ResourceId);
            json.WriteString("revision", plan.ResourceRevision);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        Optional(json, "path", r.Path);
        if (r.Permissions is { } permissions)
        {
            json.WriteStartArray("permissions");
            foreach (var permission in permissions) json.WriteStringValue(permission);
            json.WriteEndArray();
        }
        Optional(json, "entry", r.Entry);
        Optional(json, "step", r.Step);
        Optional(json, "binding", r.Binding);
        if (r.Result is RuntimeLogResult result)
        {
            json.WriteStartObject("result");
            json.WriteString("status", result.Status);
            Optional(json, "commit", result.Commit);
            json.WriteString("reason", result.Reason);
            json.WriteEndObject();
        }
        if (r.Layout is RuntimeLogLayout layout) WriteLayout(json, in layout);
        if (r.Code == RuntimeLogCodes.LogDropped) json.WriteNumber("count", entry.Count);
        if (entry.Levels is { } levels)
        {
            json.WriteStartArray("levels");
            foreach (var provider in levels.Providers)
            {
                json.WriteStartObject();
                json.WriteString("provider", provider.Provider);
                json.WriteString("level", Name(provider.Level));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteBoolean("elevated", levels.Tier == RuntimeLogTier.Elevated);
        }
        json.WriteString("message", message);
        json.WriteEndObject();
        json.Flush();
        buffer.Write("\n"u8);
    }

    private static void Optional(Utf8JsonWriter json, string name, string? value)
    { if (value != null) json.WriteString(name, value); }

    /// <summary>The layout object of `map.layout-generated`. Every array is written even when empty: the website
    /// reader requires the four keys, and an attempt that built nothing says so with `complete:false` rather than
    /// with a missing field.</summary>
    private static void WriteLayout(Utf8JsonWriter json, in RuntimeLogLayout layout)
    {
        json.WriteStartObject("layout");
        json.WriteBoolean("complete", layout.Complete);
        if (layout.ElevatorLandedTick is long landed) json.WriteNumber("elevatorLandedTick", landed);
        else json.WriteNull("elevatorLandedTick");
        json.WriteStartArray("zones");
        foreach (var zone in layout.Zones ?? Array.Empty<RuntimeLogLayoutZone>())
        {
            json.WriteStartObject();
            json.WriteString("zone", zone.Zone);
            json.WriteNumber("dimension", zone.Dimension);
            json.WriteNumber("layer", zone.Layer);
            json.WriteNumber("localIndex", zone.LocalIndex);
            json.WriteNumber("floor", zone.Floor);
            json.WriteStartArray("tiles");
            foreach (var tile in zone.Tiles ?? Array.Empty<string>()) json.WriteStringValue(tile);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("connections");
        foreach (var connection in layout.Connections ?? Array.Empty<RuntimeLogLayoutConnection>())
        {
            json.WriteStartObject();
            json.WriteString("from", connection.From);
            json.WriteString("to", connection.To);
            json.WriteString("direction", connection.Direction);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static string Message(in Entry entry)
    {
        var r = entry.Record;
        if (r.Code == RuntimeLogCodes.LogLevel)
            return "Forge log levels: " + string.Join(", ", entry.Levels!.Providers.Select(p => p.Provider + "=" + Name(p.Level)))
                + (entry.Levels.Tier == RuntimeLogTier.Elevated ? " (elevated)" : "");
        if (r.Code == RuntimeLogCodes.LogDropped)
            return "Forge log dropped " + entry.Count.ToString(CultureInfo.InvariantCulture) + " records."
                + (entry.Detail == null ? "" : " " + entry.Detail);
        var text = new StringBuilder("Forge ").Append(r.Code).Append(" provider=").Append(r.Provider);
        Field(text, "subject", r.SubjectProvider);
        if (r.Plan is RuntimeLogPlan plan) text.Append(" plan=").Append(plan.PlanId).Append(" resource=").Append(plan.ResourceId).Append('@').Append(plan.ResourceRevision);
        Field(text, "path", r.Path);
        Field(text, "detail", r.Detail);
        Field(text, "entry", r.Entry);
        Field(text, "step", r.Step);
        Field(text, "binding", r.Binding);
        Field(text, "event", r.EventId);
        Field(text, "command", r.CommandId);
        if (r.Result is RuntimeLogResult result)
        {
            text.Append(" result=").Append(result.Status);
            if (result.Commit != null) text.Append('/').Append(result.Commit);
            text.Append(':').Append(result.Reason);
        }
        if (r.Layout is RuntimeLogLayout layout)
            text.Append(" layout=").Append(layout.Complete ? "complete" : "started")
                .Append(" zones=").Append((layout.Zones?.Count ?? 0).ToString(CultureInfo.InvariantCulture))
                .Append(" connections=").Append((layout.Connections?.Count ?? 0).ToString(CultureInfo.InvariantCulture))
                .Append(" elevator=").Append(layout.ElevatorLandedTick?.ToString(CultureInfo.InvariantCulture) ?? "not-observed");
        return text.ToString();
    }

    private static void Field(StringBuilder text, string name, string? value)
    { if (value != null) text.Append(' ').Append(name).Append('=').Append(value); }

    private static string Name(RuntimeLogLevel level) => level switch
    {
        RuntimeLogLevel.Off => "off",
        RuntimeLogLevel.Error => "error",
        RuntimeLogLevel.Info => "info",
        RuntimeLogLevel.Trace => "trace",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown log level.")
    };

    private readonly struct Entry
    {
        internal Entry(long seq, in RuntimeLogRecord record, RuntimeLogLevels? levels, long count, string? detail = null, bool isReport = false)
        { Seq = seq; Record = record; Levels = levels; Count = count; Detail = detail; IsReport = isReport; }
        internal long Seq { get; }
        internal RuntimeLogRecord Record { get; }
        internal RuntimeLogLevels? Levels { get; }
        internal long Count { get; }
        /// <summary>Extra human-readable text for log.dropped when the cause was the file, not rate limiting.</summary>
        internal string? Detail { get; }
        /// <summary>True for a `log.dropped` report about records the writer did not carry, false for a logged record. A
        /// report accounts for lost records without being one of them, so it never counts towards its own total, neither in
        /// the console report nor in the footer that closes the file.</summary>
        internal bool IsReport { get; }
    }
}
