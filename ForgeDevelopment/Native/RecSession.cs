using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace ForgeDevelopment.Native;

/// <summary>One record waiting for the writer thread. The body is already serialized and complete, so nothing on the
/// writer thread ever touches a game object, a managed graph or a Unity API.</summary>
internal sealed class RecQueuedRecord
{
    internal RecQueuedRecord(string channel, string kind, byte[] payload)
    {
        Channel = channel;
        Kind = kind;
        Payload = payload;
    }

    internal string Channel { get; }
    internal string Kind { get; }
    internal byte[] Payload { get; }
    internal int ByteCount => Payload.Length;
}

/// <summary>What one session needs from the process it records: the identity every record carries, the two version
/// strings the startup facts report, and the three reads only the game can answer. The recorder core calls this
/// interface and nothing else that belongs to Unity or to the game, which is what lets a plain test process open a
/// session and exercise the whole writer.</summary>
internal interface IRecSessionContext
{
    string Role { get; }
    string Slot { get; }
    string Level { get; }
    string GameVersion { get; }
    string UnityVersion { get; }
    long WorldEpoch { get; }
    long Tick { get; }
    int Frame { get; }
    float? SNetTime { get; }
    void WriteContext(Utf8JsonWriter json, long sequence, string sessionId, string channel, string kind);
    string WriteScreenshot(string directory, string relative);
    (string Name, string Value)[]? Aim();
}

/// <summary>The context a session has before the game's own reads are installed, and the one a test keeps: a record
/// still carries its channel, kind and sequence, and claims no frame, network time or aim it does not have.</summary>
internal sealed class RecNullContext : IRecSessionContext
{
    internal static readonly RecNullContext Instance = new();
    public string Role => "unavailable";
    public string Slot => "unavailable";
    public string Level => "unavailable";
    public string GameVersion => "unavailable";
    public string UnityVersion => "unavailable";
    public long WorldEpoch => -1;
    public long Tick => -1;
    public int Frame => -1;
    public float? SNetTime => null;
    public void WriteContext(Utf8JsonWriter json, long sequence, string sessionId, string channel, string kind)
    {
        json.WriteString("v", RecSession.SchemaVersion);
        json.WriteNumber("seq", sequence);
        json.WriteString("session", sessionId);
        json.WriteString("channel", channel);
        json.WriteString("kind", kind);
    }
    public string WriteScreenshot(string directory, string relative) => "";
    public (string Name, string Value)[]? Aim() => null;
}

/// <summary>
/// The recorder session: one directory per process, one JSONL record per line, written by one background thread.
/// The game thread only composes a record into a bounded buffer and enqueues the bytes; segmentation, gzip, the total
/// budget and the index file all belong to the writer. A session that reaches its budget stops writing and counts what
/// it dropped, and says so on the `session` channel and in the screen corner rather than silently truncating.
/// </summary>
internal static class RecSession
{
    internal const string SchemaVersion = "forge.rec";
    internal const int DefaultRecordBudget = 64 * 1024;
    internal const int DefaultSegmentBytes = 8 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly object DiskGate = new();
    private static readonly Queue<RecQueuedRecord> Pending = new();
    private static readonly ManualResetEventSlim Signal = new(false);

    private static Thread? _writer;
    private static Dictionary<string, long> _channels = new(StringComparer.Ordinal);
    private static List<RecSegmentInfo> _segments = new();
    private static bool _running, _stop, _capped;
    private static long _sequence, _dropped, _bytes, _budget, _segmentBytes, _segmentIndex;
    private static int _maxQueue = 65536;
    private static bool _gzip;
    private static string _directory = "", _sessionId = "", _role = "", _slot = "unavailable", _level = "unavailable";
    private static long _lastCapNotice;
    private static Action<string> _notice = _ => { };
    private static RecJsonBuffer _buffer = new(DefaultRecordBudget);
    private static IRecSessionContext _context = RecNullContext.Instance;

    internal static bool Active { get { lock (Gate) return _running; } }
    internal static string SessionId { get { lock (Gate) return _sessionId; } }
    internal static string Directory { get { lock (Gate) return _directory; } }
    internal static long Dropped { get { lock (Gate) return _dropped; } }
    internal static long BytesWritten { get { lock (Gate) return _bytes; } }
    internal static bool BudgetReached { get { lock (Gate) return _capped; } }
    internal static long PendingRecords { get { lock (Gate) return Pending.Count; } }

    /// <summary>Installs the process reads every record carries. The game calls this with <see cref="RecTime"/>;
    /// a test leaves the null context, whose records carry no frame, network time or aim.</summary>
    internal static void UseContext(IRecSessionContext context) => _context = context ?? RecNullContext.Instance;

    /// <summary>Starts the session for this process. Called once from the authoring startup path; every later call is
    /// answered with false rather than opening a second writer.</summary>
    internal static bool Start(string bepInExRoot, string pluginVersion, string forgeVersion)
    {
        var context = _context;
        var root = System.IO.Path.Combine(bepInExRoot, "ForgeReports");
        var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + context.Role + "-" + context.Slot;
        var directory = System.IO.Path.Combine(root, "rec-" + id);
        return Open(directory, id,
            Settings.RecorderBudgetGiB.Value * 1024L * 1024 * 1024,
            Settings.RecorderSegmentMiB.Value * 1024L * 1024,
            Settings.RecorderGzip.Value,
            Math.Max(1024, Settings.RecorderRecordKiB.Value * 1024),
            message => { try { Plugin.PluginLog.LogWarning(message); } catch (Exception) { } },
            new[]
            {
                ("plugin", Plugin.PluginName + " " + pluginVersion),
                ("forgeRuntime", forgeVersion),
                ("runtimeMode", ForgeRuntime.Plugin.ConfiguredMode.ToString()),
                ("gameVersion", context.GameVersion),
                ("unityVersion", context.UnityVersion),
                ("bepInEx", typeof(BepInEx.Paths).Assembly.GetName().Version?.ToString() ?? "unavailable"),
                ("host", Environment.MachineName),
                ("process", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)),
                ("startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            },
            "session.start");
    }

    /// <summary>Opens a session at an explicit directory with explicit limits. The game path passes its configured
    /// values; a test passes a temporary directory and small numbers, which is how segmentation, the budget and the
    /// drop accounting are exercised without a game.</summary>
    internal static bool Open(string directory, string sessionId, long budgetBytes, long segmentBytes, bool gzip,
        int recordBudget, Action<string> notice, IEnumerable<(string Key, string Value)>? startFacts, string startKind)
    {
        lock (Gate)
        {
            if (_running) return false;
            _running = true;
            _stop = false;
            _capped = false;
            _directory = System.IO.Path.GetFullPath(directory);
            _sessionId = sessionId;
            _budget = Math.Max(recordBudget, budgetBytes);
            _segmentBytes = Math.Max(1, segmentBytes);
            _gzip = gzip;
            _buffer = new RecJsonBuffer(recordBudget);
            _notice = notice ?? (_ => { });
            _sequence = _dropped = _bytes = 0;
            _segmentIndex = 0;
            _channels = new Dictionary<string, long>(StringComparer.Ordinal);
            _segments = new List<RecSegmentInfo>();
            _role = _context.Role;
            _slot = _context.Slot;
            _level = _context.Level;
            _maxQueue = Math.Max(64, Settings.RecorderQueue.Value);
            Pending.Clear();
            Signal.Reset();
        }
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
        }
        catch (Exception error)
        {
            lock (Gate) { _running = false; }
            _notice("Forge recorder could not create " + _directory + ": " + error.Message);
            return false;
        }
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Forge Development Recorder" };
        _writer.Start();
        if (startFacts != null)
        {
            Write("session", startKind, json =>
            {
                foreach (var fact in startFacts) json.WriteString(fact.Key, fact.Value);
            });
        }
        return true;
    }

    /// <summary>The one entry point every channel uses. The body runs on the calling (game) thread and composes into
    /// the session's bounded buffer; the bytes are copied into the queue and nothing else happens here.</summary>
    internal static void Write(string channel, string kind, Action<Utf8JsonWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Active) return;
        byte[] payload;
        long sequence;
        lock (Gate)
        {
            if (!_running) return;
            if (_capped || Pending.Count >= _maxQueue)
            {
                CountDrop();
                return;
            }
            sequence = ++_sequence;
            try
            {
                _buffer.Reset();
                using var json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true });
                json.WriteStartObject();
                _context.WriteContext(json, sequence, _sessionId, channel, kind);
                json.WritePropertyName("body");
                json.WriteStartObject();
                body(json);
                json.WriteEndObject();
                json.WriteEndObject();
                json.Flush();
            }
            catch (RecValueTooLargeException)
            {
                // One record that does not fit its own budget is that record's problem, not the session's.
                CountDrop();
                return;
            }
            catch (Exception error)
            {
                // A body that throws must not take the game thread with it; the failure is itself recorded.
                _dropped++;
                payload = ErrorRecord(sequence, channel, kind, error);
                Pending.Enqueue(new RecQueuedRecord(channel, kind, payload));
                Signal.Set();
                return;
            }
            payload = new byte[_buffer.Length + 1];
            _buffer.WrittenSpan.CopyTo(payload);
            payload[^1] = (byte)'\n';
            Pending.Enqueue(new RecQueuedRecord(channel, kind, payload));
        }
        Signal.Set();
    }

    internal static void Write(string channel, string kind, Action<Utf8JsonWriter> body, string sessionId)
    {
        // The session writes under its own id; the parameter exists so a caller that follows the shared interface
        // cannot accidentally open a second session by passing one.
        if (sessionId != SessionId) return;
        Write(channel, kind, body);
    }

    /// <summary>The player marked "it just happened". The bookmark is a record of its own so a later query can ask for
    /// records within N seconds of it.</summary>
    internal static void Bookmark(string label)
    {
        var text = string.IsNullOrWhiteSpace(label) ? "bookmark" : RecReflect.Truncate(label, 200);
        Write("mark", "bookmark", json =>
        {
            json.WriteString("label", text);
            if (_context.Aim() is { } row)
            {
                json.WritePropertyName("aim");
                json.WriteStartObject();
                foreach (var (name, value) in row) json.WriteString(name, value);
                json.WriteEndObject();
            }
        });
        _notice("Forge recorder bookmark: " + text);
    }

    /// <summary>Writes the screen to a PNG inside the session directory and records its relative path. A screenshot
    /// that cannot be taken is reported as a record with no path, never as a success with an empty file.</summary>
    internal static string Screenshot(string reason)
    {
        var relative = "shots/" + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + (++_shotIndex).ToString("D3", CultureInfo.InvariantCulture) + ".png";
        string? error = null;
        try
        {
            var written = _context.WriteScreenshot(_directory, relative);
            if (written.Length == 0) { error = "no screenshot was produced"; relative = ""; }
            else relative = written;
        }
        catch (Exception failure)
        {
            error = failure.GetType().Name + ": " + failure.Message;
            relative = "";
        }
        Write("shot", "screenshot", json =>
        {
            json.WriteString("reason", reason);
            if (error == null) json.WriteString("path", relative);
            else json.WriteString("error", error);
        });
        return relative;
    }

    private static long _shotIndex;

    /// <summary>Records what the tracer installed: the profiles it resolved, the methods it patched and everything it
    /// refused. The startup report is the same write, under a different kind.</summary>
    internal static void WritePatches(object report) => Write("session", "patches", json =>
    {
        json.WritePropertyName("report");
        RecReflect.WriteValue(json, report, 0);
    });

    /// <summary>Runs until the queue is empty. Called on level end and process exit, and once after the writer stops.</summary>
    internal static void Flush(TimeSpan timeout)
    {
        if (!Active) return;
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            lock (Gate) { if (Pending.Count == 0) return; }
            Signal.Set();
            Thread.Sleep(5);
        }
    }

    /// <summary>Stops the session: the writer drains what is queued, writes the index and closes the segment. Safe to
    /// call twice, which is what a level end followed by a process exit does.</summary>
    internal static void Stop()
    {
        lock (Gate)
        {
            if (!_running) return;
            _running = false;
            _stop = true;
        }
        Signal.Set();
        var writer = _writer;
        if (writer != null && writer.IsAlive && Thread.CurrentThread != writer)
        {
            if (!writer.Join(TimeSpan.FromSeconds(10)))
                _notice("Forge recorder writer did not stop within 10 s; queued records were not written.");
        }
        _writer = null;
    }

    /// <summary>Records one line on the `session` channel that a summary was produced. Used by the level-end path.</summary>
    internal static void Note(string kind, Action<Utf8JsonWriter> body) => Write("session", kind, body);

    private static byte[] ErrorRecord(long sequence, string channel, string kind, Exception error)
    {
        var text = "{\"v\":\"" + SchemaVersion + "\",\"seq\":" + sequence.ToString(CultureInfo.InvariantCulture)
            + ",\"session\":\"" + JsonEscape(_sessionId) + "\",\"channel\":\"" + JsonEscape(channel) + "\",\"kind\":\"" + JsonEscape(kind)
            + "\",\"role\":\"" + JsonEscape(_role) + "\",\"slot\":\"" + JsonEscape(_slot) + "\",\"level\":\"" + JsonEscape(_level)
            + "\",\"body\":{\"$error\":\"" + JsonEscape(error.GetType().Name + ": " + RecReflect.Truncate(error.Message, 200)) + "\"}}\n";
        return Encoding.UTF8.GetBytes(text);
    }

    private static string JsonEscape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    private static void CountDrop()
    {
        _dropped++;
        if (!_capped || Environment.TickCount64 - _lastCapNotice < 1000) return;
        _lastCapNotice = Environment.TickCount64;
        _notice("Forge recorder dropped " + _dropped.ToString(CultureInfo.InvariantCulture) + " records.");
    }

    private static T? Safe<T>(Func<T> read, T? fallback = default)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }

    private static void WriteLoop()
    {
        SegmentWriter? segment = null;
        try
        {
            while (true)
            {
                RecQueuedRecord? record = null;
                lock (Gate)
                {
                    if (Pending.Count != 0) record = Pending.Dequeue();
                    else if (_stop) break;
                }
                if (record == null)
                {
                    Signal.Wait(50);
                    Signal.Reset();
                    continue;
                }
                if (_capped) { lock (Gate) CountDrop(); continue; }
                segment ??= new SegmentWriter();
                if (!segment.Write(record))
                {
                    lock (Gate)
                    {
                        _capped = true;
                        CountDrop();
                    }
                }
                lock (Gate) _channels[record.Channel] = _channels.TryGetValue(record.Channel, out var count) ? count + 1 : 1;
            }
        }
        catch (Exception error)
        {
            _notice("Forge recorder writer failed: " + error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            segment?.Dispose();
            WriteIndex();
            Signal.Reset();
        }
    }

    private static void WriteIndex()
    {
        try
        {
            var payload = new StringBuilder();
            payload.Append("{\"v\":\"").Append(SchemaVersion).Append("\",\"session\":\"").Append(JsonEscape(_sessionId)).Append("\"");
            payload.Append(",\"role\":\"").Append(JsonEscape(_role)).Append("\",\"slot\":\"").Append(JsonEscape(_slot)).Append("\"");
            payload.Append(",\"level\":\"").Append(JsonEscape(_level)).Append("\"");
            payload.Append(",\"budgetBytes\":").Append(_budget.ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"bytesWritten\":").Append(_bytes.ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"gzip\":").Append(_gzip ? "true" : "false");
            payload.Append(",\"budgetReached\":").Append(_capped ? "true" : "false");
            payload.Append(",\"dropped\":").Append(_dropped.ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"records\":").Append(_sequence.ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"endedUtc\":\"").Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append("\"");
            payload.Append(",\"channels\":{");
            var first = true;
            foreach (var channel in _channels)
            {
                if (!first) payload.Append(',');
                first = false;
                payload.Append('"').Append(JsonEscape(channel.Key)).Append("\":").Append(channel.Value.ToString(CultureInfo.InvariantCulture));
            }
            payload.Append("},\"segments\":[");
            first = true;
            foreach (var info in _segments)
            {
                if (!first) payload.Append(',');
                first = false;
                payload.Append("{\"file\":\"").Append(JsonEscape(info.File)).Append("\",\"bytes\":").Append(info.Bytes.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"records\":").Append(info.Records.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            payload.Append("],\"patches\":");
            payload.Append(RecTracerRuntime.InstalledJson());
            payload.Append('}');
            System.IO.File.WriteAllText(System.IO.Path.Combine(_directory, "index.json"), payload.ToString());
        }
        catch (Exception error)
        {
            _notice("Forge recorder could not write index.json: " + error.Message);
        }
    }

    internal sealed record RecSegmentInfo(string File, long Bytes, long Records);

    /// <summary>
    /// One open segment. The writer thread owns it exclusively, so the session's counters are reached without a lock
    /// here; the file is opened lazily so a session that records nothing leaves no empty file behind.
    /// </summary>
    private sealed class SegmentWriter : IDisposable
    {
        private FileStream? _file;
        private Stream? _stream;
        private string _name = "";
        private long _bytes, _records;
        private bool _disposed;

        internal bool Write(RecQueuedRecord record)
        {
            lock (Gate)
            {
                // The budget is checked before the bytes reach the disk, so the session stops exactly at its cap
                // instead of writing over it and reporting afterwards.
                if (_capped || _bytes + record.ByteCount > _budget) return false;
            }
            if (_file == null && !Open()) return false;
            if (_bytes + record.ByteCount > _segmentBytes && _bytes > 0)
            {
                Close();
                _segmentIndex++;
                if (!Open()) return false;
            }
            try
            {
                _stream!.Write(record.Payload, 0, record.Payload.Length);
                _bytes += record.ByteCount;
                _records++;
                lock (Gate) _bytes += record.ByteCount;
                return true;
            }
            catch (Exception error)
            {
                _notice("Forge recorder write failed in " + _name + ": " + error.Message);
                return false;
            }
        }

        private bool Open()
        {
            try
            {
                var name = "rec-" + _sessionId + "-" + _segmentIndex.ToString("D3", CultureInfo.InvariantCulture) + ".jsonl" + (_gzip ? ".gz" : "");
                var path = System.IO.Path.Combine(_directory, name);
                _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _stream = _gzip ? new GZipStream(_file, CompressionLevel.Fastest) : _file;
                _name = name;
                _bytes = 0;
                _records = 0;
                _segments.Add(new RecSegmentInfo(name, 0, 0));
                return true;
            }
            catch (Exception error)
            {
                _notice("Forge recorder could not open a segment in " + _directory + ": " + error.Message);
                return false;
            }
        }

        private void Close()
        {
            try
            {
                _stream?.Flush();
                _stream?.Dispose();
                if (_file != null) _bytes = _file.Length;
            }
            catch (Exception error)
            {
                _notice("Forge recorder could not close " + _name + ": " + error.Message);
            }
            finally
            {
                _stream = null;
                _file = null;
                UpdateInfo();
            }
        }

        private void UpdateInfo()
        {
            for (var i = 0; i < _segments.Count; i++)
                if (_segments[i].File == _name) _segments[i] = new RecSegmentInfo(_name, _bytes, _records);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
    }
}
