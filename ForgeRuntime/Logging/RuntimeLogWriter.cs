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

/// <summary>Constructor inputs only so tests can inject small values; the host always passes <see cref="Default"/> (I-DIAG §3.2).</summary>
internal sealed record RuntimeLogLimits(int PlayerLinesPerTick, int PlayerQueue, int ElevatedLinesPerTick, int ElevatedQueue,
    long MaximumFileBytes, int RetainedFiles)
{
    internal static readonly RuntimeLogLimits Default = new(256, 8192, 4096, 65536, 64L * 1024 * 1024, 10);
}

/// <summary>forge.log.v1 JSONL writer. Write runs on the kernel thread and only counts, copies and enqueues; directory,
/// file, retention, JSON and message text all happen on one background thread that exists only after the first record.</summary>
internal sealed class RuntimeLogWriter : IRuntimeLogSink, IDisposable
{
    internal const string Schema = "forge.log.v1";
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
    private RuntimeLogLevels? announced;
    private RuntimeLogTier? announcedTier;
    private readonly string runtimeProvider;
    private long seq, tick = long.MinValue, epoch = long.MinValue, dropped, droppedTick, droppedEpoch;
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
    internal string? FilePath { get; private set; }

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
            queue.Add(new Entry(++seq, new RuntimeLogRecord { Level = RuntimeLogLevel.Info, Code = RuntimeLogCodes.LogLevel,
                Provider = levels.RuntimeProvider, Tick = record.Tick, WorldEpoch = record.WorldEpoch }, levels, 0));
        }
        if (dropped != 0) AddDropped();
        queue.Add(new Entry(++seq, record, null, 0));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (queue == null) return;
        if (dropped != 0) AddDropped();
        queue.CompleteAdding();
        if (!thread!.Join(StopTimeout))
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
        // A session whose very first record was already dropped has no announced table yet, so the Runtime provider is
        // carried from construction rather than from the level line.
        queue!.Add(new Entry(++seq, new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.LogDropped,
            Provider = announced?.RuntimeProvider ?? runtimeProvider, Tick = droppedTick, WorldEpoch = droppedEpoch }, null, dropped));
        dropped = 0;
    }

    private void Run()
    {
        FileStream? file = null;
        try
        {
            var buffer = new ArrayBufferWriter<byte>(1024);
            using var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            file = Open();
            long written = 0, capped = 0;
            Entry last = default;
            foreach (var entry in queue!.GetConsumingEnumerable())
            {
                last = entry;
                var message = Message(entry);
                if (entry.Record.Level <= RuntimeLogLevel.Info)
                {
                    if (entry.Record.Level == RuntimeLogLevel.Error) console.LogError(message);
                    else console.LogInfo(message);
                }
                if (file == null) continue;
                Serialize(json, buffer, entry, message);
                if (capped != 0 || written + buffer.WrittenCount > limits.MaximumFileBytes - DroppedLineReserve)
                {
                    if (capped++ == 0) console.LogError("Forge log file reached its size limit; further records are dropped: " + FilePath);
                    continue;
                }
                file = Append(file, buffer, ref written);
                if (file != null && queue.Count == 0) file.Flush();
            }
            if (file != null && capped != 0)
            {
                var record = new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.LogDropped,
                    Provider = last.Record.Provider, Tick = last.Record.Tick, WorldEpoch = last.Record.WorldEpoch };
                var end = new Entry(last.Seq + 1, record, null, capped);
                Serialize(json, buffer, end, Message(end));
                file = Append(file, buffer, ref written);
            }
            file?.Flush();
        }
        catch (Exception error)
        {
            // An unhandled exception on this thread would terminate the game process.
            console.LogError("Forge log writer stopped: " + error);
        }
        finally { file?.Dispose(); }
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
            console.LogError("Forge log file could not be created in " + directory + "; no records are written to file this session: " + error.Message);
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
        foreach (var file in stale)
        {
            try { file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { console.LogWarning("Forge log retention could not delete " + file.Name + ": " + error.Message); }
        }
    }

    private FileStream? Append(FileStream file, ArrayBufferWriter<byte> buffer, ref long written)
    {
        try { file.Write(buffer.WrittenSpan); written += buffer.WrittenCount; return file; }
        catch (IOException error)
        {
            console.LogError("Forge log file write failed; no further records are written to file this session: " + error.Message);
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

    private static string Message(in Entry entry)
    {
        var r = entry.Record;
        if (r.Code == RuntimeLogCodes.LogLevel)
            return "Forge log levels: " + string.Join(", ", entry.Levels!.Providers.Select(p => p.Provider + "=" + Name(p.Level)))
                + (entry.Levels.Tier == RuntimeLogTier.Elevated ? " (elevated)" : "");
        if (r.Code == RuntimeLogCodes.LogDropped)
            return "Forge log dropped " + entry.Count.ToString(CultureInfo.InvariantCulture) + " records.";
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
        return text.ToString();
    }

    private static void Field(StringBuilder text, string name, string? value)
    { if (value != null) text.Append(' ').Append(name).Append('=').Append(value); }

    private static string Name(RuntimeLogLevel level) => level switch
    {
        RuntimeLogLevel.Error => "error",
        RuntimeLogLevel.Info => "info",
        RuntimeLogLevel.Trace => "trace",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Off is never written.")
    };

    private readonly struct Entry
    {
        internal Entry(long seq, in RuntimeLogRecord record, RuntimeLogLevels? levels, long count)
        { Seq = seq; Record = record; Levels = levels; Count = count; }
        internal long Seq { get; }
        internal RuntimeLogRecord Record { get; }
        internal RuntimeLogLevels? Levels { get; }
        internal long Count { get; }
    }
}
