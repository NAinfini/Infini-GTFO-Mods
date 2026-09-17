using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using ForgeRuntime.Framework;
using ForgeRuntime.Logging;
using LogLevel = BepInEx.Logging.LogLevel;

if (args.Length != 2 || args[0] != "--root")
{
    Console.Error.WriteLine("Usage: RuntimeLog --root <new or empty directory for this run's log files>");
    return 2;
}
string root = Path.GetFullPath(args[1]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
{
    Console.Error.WriteLine("--root must be new or empty so stale log files cannot satisfy a check: " + root);
    return 2;
}
Directory.CreateDirectory(root);

const string Runtime = "forge.runtime";
int checks = 0, failures = 0, sequence = 0;
var console = new List<string>();
var strict = new UTF8Encoding(false, true);
// The kernel thread in this suite. Every BepInEx listener must be invoked on a thread that owns the calling frame.
int mainThread = Environment.CurrentManagedThreadId;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); Interlocked.Increment(ref checks); }
void Case(string name, Action test)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
void RejectCode(string code, Action action, string message)
{
    try { action(); }
    catch (RuntimeContractException error) when (error.Code == code) { Interlocked.Increment(ref checks); return; }
    catch (Exception error) { throw new Exception(message + ": threw " + error.GetType().Name + " " + (error as RuntimeContractException)?.Code, error); }
    throw new Exception(message + ": accepted");
}
string NewDirectory() => Path.Combine(root, (++sequence).ToString("D2"));
RuntimeKernel Kernel(IRuntimeLogSink sink, RuntimeLogLevel level)
    => new(new RuntimeIdentity(Runtime, "1.0.0", RuntimeKernel.ApiVersion, "managed-test"), new RuntimeLimits(), sink, level);
RuntimeLogRecord Record(RuntimeLogLevel level, string code, long tick)
    => new() { Level = level, Code = code, Provider = Runtime, Tick = tick, WorldEpoch = 1 };
RuntimeModule Module(string id) => new(RuntimeKernel.ApiVersion, JsonSerializer.Serialize(new {
    providers = new[] { new { id, kind = "native", version = "1.0.0", dependencies = Array.Empty<object>() } },
    capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
}), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
(RuntimeLogWriter Writer, ManualLogSource Log, List<LogEventArgs> Events) Writer(string directory, RuntimeLogLimits? limits = null)
{
    var log = new ManualLogSource("Forge"); var events = new List<LogEventArgs>();
    log.LogEvent += (_, e) => { lock (events) events.Add(e); lock (console) console.Add(e.Data?.ToString() ?? ""); };
    return (new RuntimeLogWriter(directory, Runtime, log, limits ?? RuntimeLogLimits.Default), log, events);
}
JsonElement[] Lines(RuntimeLogWriter writer)
{
    Check(writer.FilePath != null && File.Exists(writer.FilePath), "writer did not create its session file");
    var text = strict.GetString(File.ReadAllBytes(writer.FilePath!));
    Check(text.EndsWith("\n", StringComparison.Ordinal), "file does not end with a newline");
    return text[..^1].Split('\n').Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
}
string? Text(JsonElement line, string name) => line.TryGetProperty(name, out var value) ? value.GetString() : null;
string[] Codes(JsonElement[] lines) => lines.Select(line => Text(line, "code")!).ToArray();

Case("lazy start", () => {
    var directory = NewDirectory(); var (writer, _, events) = Writer(directory);
    // Off keeps every kernel record point closed, including the `world.began` an Info-level gate would write: the writer
    // may only be started by a record that actually reaches it.
    var kernel = Kernel(writer, RuntimeLogLevel.Off);
    kernel.BeginWorld(1); kernel.ElevateLogging(); kernel.StartRuntime(() => { }); kernel.StopRuntime();
    writer.Dispose();
    Check(!writer.Started && writer.FilePath == null && !Directory.Exists(directory) && events.Count == 0,
        "writer created a thread, directory, file or console line before any record");
});

Case("first line is log.level", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory);
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    kernel.WriteLog(Record(RuntimeLogLevel.Error, "test.first", 7));
    Check(writer.Started, "first record did not start the writer");
    writer.Dispose(); var lines = Lines(writer);
    Check(Path.GetDirectoryName(writer.FilePath) == Path.GetFullPath(directory)
        && Regex.IsMatch(Path.GetFileName(writer.FilePath!), "^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{4}\\.jsonl$"), "session file name or location");
    Check(Codes(lines).SequenceEqual(new[] { RuntimeLogCodes.LogLevel, "test.first" }), "log.level is not the first line");
    var levels = lines[0].GetProperty("levels");
    Check(levels.GetArrayLength() == 1 && Text(levels[0], "provider") == Runtime && Text(levels[0], "level") == "info"
        && !lines[0].GetProperty("elevated").GetBoolean(), "log.level does not carry the player level table");
    Check(Text(lines[0], "provider") == Runtime && Text(lines[0], "level") == "info" && lines[0].GetProperty("tick").GetInt64() == 7,
        "log.level owner, level or tick");
});

Case("log.level renders a disabled provider", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory);
    // A package that registers at Off is in the published table, and the first record's own log.level line is where
    // that table becomes console text: the name lookup has to spell every configured level, not only the written ones.
    var kernel = Kernel(writer, RuntimeLogLevel.Error);
    kernel.RegisterModule(Module("test.disabled"), RuntimeLogLevel.Off);
    kernel.WriteLog(Record(RuntimeLogLevel.Error, "test.first", 7));
    writer.Dispose(); var lines = Lines(writer);
    Check(Codes(lines).SequenceEqual(new[] { RuntimeLogCodes.LogLevel, "test.first" }), "log.level is not the first line");
    var levels = lines[0].GetProperty("levels").EnumerateArray()
        .ToDictionary(row => Text(row, "provider")!, row => Text(row, "level")!);
    Check(levels.Count == 2 && levels[Runtime] == "error" && levels["test.disabled"] == "off",
        "log.level did not render the disabled provider: " + string.Join(",", levels.Select(x => x.Key + "=" + x.Value)));
});

Case("seq increments by one", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory);
    var kernel = Kernel(writer, RuntimeLogLevel.Error);
    for (int tick = 1; tick <= 5; tick++) kernel.WriteLog(Record(RuntimeLogLevel.Error, "test.seq", tick));
    writer.Dispose(); var lines = Lines(writer);
    Check(lines.Length == 6 && lines.Select((line, index) => line.GetProperty("seq").GetInt64() == index + 1).All(x => x), "seq is not 1..n without gaps");
    var record = Record(RuntimeLogLevel.Error, "test.late", 6);
    Check(Throws<ObjectDisposedException>(() => writer.Write(in record, null!)), "stopped writer accepted a record");
});

Case("per-tick limit drops and reports counts", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory, RuntimeLogLimits.Default with { PlayerLinesPerTick = 3 });
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    for (int i = 0; i < 5; i++) kernel.WriteLog(Record(RuntimeLogLevel.Info, "tick.1", 1));
    kernel.WriteLog(Record(RuntimeLogLevel.Info, "tick.2", 2));
    for (int i = 0; i < 4; i++) kernel.WriteLog(Record(RuntimeLogLevel.Info, "tick.3", 3));
    writer.Dispose(); var lines = Lines(writer);
    Check(Codes(lines).SequenceEqual(new[] { "log.level", "tick.1", "tick.1", "tick.1", "log.dropped", "tick.2", "tick.3", "tick.3", "tick.3", "log.dropped" }),
        "per-tick drops were not reported before the next accepted record and at stop: " + string.Join(",", Codes(lines)));
    Check(lines[4].GetProperty("count").GetInt64() == 2 && lines[4].GetProperty("tick").GetInt64() == 1 && Text(lines[4], "level") == "error"
        && lines[9].GetProperty("count").GetInt64() == 1 && lines[9].GetProperty("tick").GetInt64() == 3, "log.dropped count, tick or level");
});

Case("queue full drops and reports counts", () => {
    var directory = NewDirectory(); var (writer, log, _) = Writer(directory, RuntimeLogLimits.Default with { PlayerQueue = 4 });
    using var release = new ManualResetEventSlim(); bool mirroredOnCaller = false;
    // The console mirror runs on the calling thread, and its listeners are synchronous, so holding one here freezes the
    // queue exactly while the caller keeps writing. That also proves BepInEx never runs on the writer thread.
    log.LogEvent += (_, e) =>
    {
        if (e.Data is not string text || !text.StartsWith("Forge log levels", StringComparison.Ordinal)) return;
        mirroredOnCaller = Environment.CurrentManagedThreadId == mainThread;
        release.Wait(TimeSpan.FromSeconds(30));
    };
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    kernel.WriteLog(Record(RuntimeLogLevel.Info, "queue.1", 1));
    Check(mirroredOnCaller, "the log.level line was not mirrored on the calling thread");
    for (int i = 2; i <= 10; i++) kernel.WriteLog(Record(RuntimeLogLevel.Info, "queue." + i, i));
    release.Set(); writer.Dispose(); var lines = Lines(writer);
    Check(Codes(lines).SequenceEqual(new[] { "log.level", "queue.1", "queue.2", "queue.3", "queue.4", "log.dropped" }),
        "queue limit did not keep the oldest records: " + string.Join(",", Codes(lines)));
    Check(lines[5].GetProperty("count").GetInt64() == 6 && lines[5].GetProperty("tick").GetInt64() == 10, "queue drop count or tick");
});

Case("background file failures are reported as log.dropped by a later write", () => {
    var directory = NewDirectory();
    // A file at the directory's own path makes Directory.CreateDirectory and the session file fail on the writer thread.
    File.WriteAllText(directory, "not a directory");
    var (writer, _, _) = Writer(directory);
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    kernel.WriteLog(Record(RuntimeLogLevel.Info, "file.1", 1));
    kernel.WriteLog(Record(RuntimeLogLevel.Info, "file.2", 2));
    writer.Dispose();
    Check(!writer.Started || writer.FilePath == null, "the writer opened a session file under a blocked path");
    lock (console)
    {
        var reported = console.Where(line => line.Contains("Forge log dropped", StringComparison.Ordinal)
            && line.Contains("Records were not written to", StringComparison.Ordinal)).ToArray();
        Check(reported.Length == 1 && reported[0].Contains("dropped 2 records.", StringComparison.Ordinal),
            "the kernel thread did not report the failed file exactly once with the lost record count: " + string.Join(" | ", reported));
    }
});

Case("file size cap stops writing and ends with log.dropped", () => {
    var directory = NewDirectory(); var (writer, _, events) = Writer(directory, RuntimeLogLimits.Default with { MaximumFileBytes = 4096 });
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    for (int tick = 1; tick <= 100; tick++) kernel.WriteLog(Record(RuntimeLogLevel.Info, "cap.record", tick));
    writer.Dispose(); var lines = Lines(writer);
    int written = lines.Count(line => Text(line, "code") == "cap.record");
    Check(new FileInfo(writer.FilePath!).Length <= 4096, "file exceeded injected cap");
    Check(Text(lines[^1], "code") == "log.dropped" && lines[^1].GetProperty("count").GetInt64() == 100 - written && written is > 0 and < 100,
        "capped file does not account for every unwritten record");
    Check(events.Count(e => e.Level == LogLevel.Error && e.Data is string text && text.Contains("reached its size limit", StringComparison.Ordinal)) == 1,
        "size cap was not reported to the console exactly once");
});

Case("retention keeps ten jsonl files and nothing else is touched", () => {
    var directory = NewDirectory(); Directory.CreateDirectory(Path.Combine(directory, "nested"));
    var old = DateTime.UtcNow.AddDays(-2);
    for (int i = 0; i < 12; i++)
    {
        var path = Path.Combine(directory, "old-" + i.ToString("D2") + ".jsonl"); File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, old.AddHours(i));
    }
    foreach (var name in new[] { "notes.txt", "keep.jsonl.bak", Path.Combine("nested", "old.jsonl") })
    { var path = Path.Combine(directory, name); File.WriteAllText(path, "keep"); File.SetLastWriteTimeUtc(path, old.AddDays(-30)); }
    var (writer, _, _) = Writer(directory); var kernel = Kernel(writer, RuntimeLogLevel.Error);
    kernel.WriteLog(Record(RuntimeLogLevel.Error, "retain", 1)); writer.Dispose();
    var remaining = Directory.GetFiles(directory).Select(Path.GetFileName).Where(name => name!.EndsWith(".jsonl", StringComparison.Ordinal)).ToHashSet();
    Check(remaining.Count == 10 && remaining.Contains(Path.GetFileName(writer.FilePath)), "retention did not keep exactly ten session files including the current one");
    Check(Enumerable.Range(0, 3).All(i => !remaining.Contains("old-" + i.ToString("D2") + ".jsonl"))
        && Enumerable.Range(3, 9).All(i => remaining.Contains("old-" + i.ToString("D2") + ".jsonl")), "retention deleted by something other than age");
    Check(File.Exists(Path.Combine(directory, "notes.txt")) && File.Exists(Path.Combine(directory, "keep.jsonl.bak"))
        && File.Exists(Path.Combine(directory, "nested", "old.jsonl")), "retention deleted a non-jsonl or nested file");
});

Case("console mirrors error and info only", () => {
    var directory = NewDirectory(); var (writer, log, events) = Writer(directory);
    var threads = new List<int>();
    log.LogEvent += (_, _) => { lock (threads) threads.Add(Environment.CurrentManagedThreadId); };
    var kernel = Kernel(writer, RuntimeLogLevel.Info); kernel.ElevateLogging();
    kernel.WriteLog(Record(RuntimeLogLevel.Error, "mirror.error", 1));
    kernel.WriteLog(Record(RuntimeLogLevel.Info, "mirror.info", 1));
    kernel.WriteLog(Record(RuntimeLogLevel.Trace, "mirror.trace", 1));
    writer.Dispose(); var lines = Lines(writer);
    bool Mirrored(LogLevel level, string code) => events.Any(e => e.Level == level && e.Data is string text && text.Contains(code, StringComparison.Ordinal));
    Check(Mirrored(LogLevel.Error, "mirror.error") && Mirrored(LogLevel.Info, "mirror.info") && Mirrored(LogLevel.Info, "(elevated)"), "error, info or log.level was not mirrored at its level");
    Check(!events.Any(e => e.Data is string text && text.Contains("mirror.trace", StringComparison.Ordinal)), "trace reached the console");
    Check(lines.Any(line => Text(line, "code") == "mirror.trace" && Text(line, "level") == "trace"), "trace record missing from file");
    lock (threads) Check(threads.Count > 0 && threads.All(id => id == mainThread),
        "a BepInEx listener ran off the calling thread: " + string.Join(",", threads.Distinct()));
});

Case("provider level gates and fixed rejection codes", () => {
    foreach (var configured in new[] { RuntimeLogLevel.Off, RuntimeLogLevel.Error, RuntimeLogLevel.Info })
    {
        var sink = new CaptureSink(); var kernel = Kernel(sink, configured); var gate = kernel.LogGate(Runtime);
        foreach (var level in new[] { RuntimeLogLevel.Error, RuntimeLogLevel.Info, RuntimeLogLevel.Trace })
        {
            Check(gate.IsEnabled(level) == (level <= configured), configured + " gate answered " + level + " wrongly");
            if (level <= configured) kernel.WriteLog(Record(level, "gate", 1));
            else RejectCode("log-level-disabled", () => kernel.WriteLog(Record(level, "gate", 1)), configured + " accepted " + level);
        }
        RejectCode("log-level-disabled", () => kernel.WriteLog(Record(RuntimeLogLevel.Off, "gate", 1)), "Off record accepted");
        Check(sink.Records.Count == (int)configured && sink.Levels.All(l => l.Tier == RuntimeLogTier.Player
            && l.Providers.SequenceEqual(new[] { new RuntimeLogProviderLevel(Runtime, configured) })), "sink received wrong records or level table");
        RejectCode("log-provider-unregistered", () => kernel.LogGate("test.domain"), "unregistered provider received a level");
        RejectCode("log-provider-unregistered", () => kernel.WriteLog(Record(RuntimeLogLevel.Error, "gate", 1) with { Provider = "test.domain" }),
            "domain record passed without a level entry");
        RejectCode("log-level", () => kernel.RegisterModule(Module("test.trace"), RuntimeLogLevel.Trace), "trace accepted as a registration level");
        RejectCode("log-provider-unregistered", () => kernel.LogGate("test.trace"), "rejected registration left a level entry");
        // The level arrives with the registration, and one package registering several providers passes the same one to each.
        var domain = kernel.RegisterModule(Module("test.domain"), RuntimeLogLevel.Info);
        using var second = kernel.RegisterModule(Module("test.domain.second"), RuntimeLogLevel.Info);
        Check(kernel.LogGate("test.domain").Level == RuntimeLogLevel.Info && kernel.LogGate("test.domain.second").Level == RuntimeLogLevel.Info,
            "a registered provider did not receive the level it was handed");
        kernel.WriteLog(Record(RuntimeLogLevel.Info, "gate", 1) with { Provider = "test.domain" });
        Check(sink.Records.Count == (int)configured + 1 && sink.Records[^1].Provider == "test.domain", "registered domain record did not reach the sink");
        Check(sink.Levels[^1].Providers.SequenceEqual(new[] {
            new RuntimeLogProviderLevel(Runtime, configured), new RuntimeLogProviderLevel("test.domain", RuntimeLogLevel.Info),
            new RuntimeLogProviderLevel("test.domain.second", RuntimeLogLevel.Info) }), "registration did not republish the level table");
        domain.Dispose();
        RejectCode("log-provider-unregistered", () => kernel.LogGate("test.domain"), "unregistered provider kept its level");
        RejectCode("log-provider-unregistered", () => kernel.WriteLog(Record(RuntimeLogLevel.Error, "gate", 1) with { Provider = "test.domain" }),
            "domain record passed after unregistration");
        // The Runtime's own entry belongs to the host constructor: a module claiming that provider id can neither replace nor drop it.
        using (var reserved = kernel.RegisterModule(Module(Runtime), RuntimeLogLevel.Info))
            Check(kernel.LogGate(Runtime).Level == configured, "a module replaced the Runtime's own level");
        Check(kernel.LogGate(Runtime).Level == configured, "unregistering a module dropped the Runtime's own level");
    }
    var checkedSink = new CaptureSink(); var validating = Kernel(checkedSink, RuntimeLogLevel.Info);
    var plan = new RuntimeLogPlan { PlanId = "plan", ResourceId = "resource", ResourceRevision = "1" };
    var result = new RuntimeLogResult { Status = "failed", Commit = "unknown", Reason = "reason" };
    validating.WriteLog(Record(RuntimeLogLevel.Error, "valid", 1) with { Plan = plan, Result = result });
    RejectCode("log-record", () => validating.WriteLog(Record(RuntimeLogLevel.Error, null!, 1)), "record without code accepted");
    RejectCode("log-record", () => validating.WriteLog(Record(RuntimeLogLevel.Error, "x", 1) with { Provider = null! }), "record without provider accepted");
    RejectCode("log-record", () => validating.WriteLog(Record(RuntimeLogLevel.Error, "x", 1) with { Plan = plan with { ResourceRevision = null! } }), "incomplete plan accepted");
    RejectCode("log-record", () => validating.WriteLog(Record(RuntimeLogLevel.Error, "x", 1) with { Result = result with { Reason = null! } }), "incomplete result accepted");
    Check(checkedSink.Records.Count == 1, "rejected records reached the sink");
    RejectCode("log-level", () => Kernel(new CaptureSink(), RuntimeLogLevel.Trace), "trace accepted as a configured level");
    Check(Throws<ArgumentNullException>(() => Kernel(null!, RuntimeLogLevel.Error)), "null sink accepted");
    var unconfigured = new RuntimeKernel(new RuntimeIdentity(Runtime, "1.0.0", RuntimeKernel.ApiVersion, "managed-test"));
    RejectCode("log-unconfigured", () => unconfigured.WriteLog(Record(RuntimeLogLevel.Error, "x", 1)), "kernel without sink wrote");
    RejectCode("log-unconfigured", unconfigured.ElevateLogging, "kernel without sink elevated");
    RejectCode("log-provider-unregistered", () => unconfigured.LogGate(Runtime), "kernel without sink has a level table");
    using var tableless = unconfigured.RegisterModule(Module("test.domain"), RuntimeLogLevel.Info);
    RejectCode("log-provider-unregistered", () => unconfigured.LogGate("test.domain"), "kernel without sink published a level entry");
});

Case("elevation is once, registration-only and irreversible", () => {
    var directory = NewDirectory();
    var (writer, _, _) = Writer(directory, RuntimeLogLimits.Default with { PlayerLinesPerTick = 2, ElevatedLinesPerTick = 5 });
    var kernel = Kernel(writer, RuntimeLogLevel.Error); var gate = kernel.LogGate(Runtime);
    kernel.WriteLog(Record(RuntimeLogLevel.Error, "before", 1));
    RejectCode("log-level-disabled", () => kernel.WriteLog(Record(RuntimeLogLevel.Trace, "before.trace", 1)), "player tier accepted trace");
    kernel.ElevateLogging();
    Check(gate.Level == RuntimeLogLevel.Trace, "gate obtained before elevation did not see it");
    RejectCode("log-elevation-rejected", kernel.ElevateLogging, "second elevation accepted");
    for (int i = 0; i < 5; i++) kernel.WriteLog(Record(RuntimeLogLevel.Trace, "after", 2));
    kernel.StartRuntime(() => { });
    RejectCode("log-elevation-rejected", kernel.ElevateLogging, "elevation accepted after StartRuntime");
    Check(gate.Level == RuntimeLogLevel.Trace, "elevation was undone");
    writer.Dispose(); var lines = Lines(writer);
    Check(Codes(lines).SequenceEqual(new[] { "log.level", "before", "log.level", "after", "after", "after", "after", "after" }),
        "elevated tier limits or level re-announcement wrong: " + string.Join(",", Codes(lines)));
    Check(!lines[0].GetProperty("elevated").GetBoolean() && lines[2].GetProperty("elevated").GetBoolean()
        && Text(lines[2].GetProperty("levels")[0], "level") == "trace", "log.level lines do not show the tier switch");
    foreach (Action<RuntimeKernel> close in new Action<RuntimeKernel>[] { k => k.StartRuntime(() => throw new InvalidOperationException("startup")), k => k.StopRuntime() })
    {
        var closed = Kernel(new CaptureSink(), RuntimeLogLevel.Error);
        try { close(closed); } catch (InvalidOperationException) { }
        RejectCode("log-elevation-rejected", closed.ElevateLogging, "elevation accepted after registration closed by " + closed.StartupState);
    }
});

Case("stop flushes a full elevated session", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory);
    var kernel = Kernel(writer, RuntimeLogLevel.Error); kernel.ElevateLogging();
    var clock = Stopwatch.StartNew();
    for (int tick = 0; tick < 15; tick++)
        for (int i = 0; i < 4000; i++) kernel.WriteLog(Record(RuntimeLogLevel.Trace, "flush", tick));
    var enqueued = clock.Elapsed; writer.Dispose(); var stopped = clock.Elapsed;
    var lines = Lines(writer);
    Check(lines.Length == 60001 && !lines.Any(line => Text(line, "code") == "log.dropped") && lines[^1].GetProperty("seq").GetInt64() == 60001,
        "stop lost or dropped records: " + lines.Length);
    Check(stopped - enqueued < RuntimeLogWriter.StopTimeout, "stop exceeded its bounded wait");
    Console.WriteLine($"  60000 elevated records: enqueue {enqueued.TotalMilliseconds:F0} ms, stop and flush {(stopped - enqueued).TotalMilliseconds:F0} ms");
});

Case("valid UTF-8 JSONL without BOM or CR", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory);
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    var full = new RuntimeLogRecord {
        Level = RuntimeLogLevel.Error, Code = "test.full", Provider = Runtime, SubjectProvider = "test.subject", Tick = 42, WorldEpoch = 3, Frame = 9001,
        CommandId = "command-1", EventId = "event-2", CauseId = "event-1", RootEventId = "event-0",
        Plan = new RuntimeLogPlan { PlanId = "plan-α", ResourceId = "资源/一", ResourceRevision = "rev \"7\"" },
        Entry = "入口", Step = "步骤\\1", Binding = "gtfo.enemy.health",
        Result = new RuntimeLogResult { Status = "failed", Commit = "unknown", Reason = "原因 é\ttab" }
    };
    kernel.WriteLog(in full); kernel.WriteLog(Record(RuntimeLogLevel.Info, "test.minimal", 42) with { WorldEpoch = 3 });
    writer.Dispose();
    var bytes = File.ReadAllBytes(writer.FilePath!);
    Check(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) && !bytes.Contains((byte)'\r'), "file has a BOM or CR");
    Check(strict.GetString(bytes).Contains("资源/一", StringComparison.Ordinal), "non-ASCII text was escaped instead of written as UTF-8");
    var lines = Lines(writer);
    Check(lines.Length == 3 && lines.All(line => Text(line, "schema") == RuntimeLogWriter.Schema && Text(line, "origin") == "game"), "schema or origin");
    string[] Names(JsonElement line) => line.EnumerateObject().Select(p => p.Name).ToArray();
    Check(Names(lines[0]).SequenceEqual(new[] { "schema", "origin", "seq", "tick", "worldEpoch", "level", "code", "provider", "levels", "elevated", "message" }), "log.level field order");
    Check(Names(lines[1]).SequenceEqual(new[] { "schema", "origin", "seq", "tick", "worldEpoch", "frame", "level", "code", "provider", "subjectProvider",
        "commandId", "eventId", "causeId", "rootEventId", "plan", "entry", "step", "binding", "result", "message" }), "full record field order: " + string.Join(",", Names(lines[1])));
    Check(Names(lines[2]).SequenceEqual(new[] { "schema", "origin", "seq", "tick", "worldEpoch", "level", "code", "provider", "message" }), "optional fields were written when absent");
    var plan = lines[1].GetProperty("plan"); var result = lines[1].GetProperty("result");
    Check(Text(plan, "planId") == "plan-α" && Text(plan.GetProperty("resource"), "id") == "资源/一" && Text(plan.GetProperty("resource"), "revision") == "rev \"7\""
        && Text(lines[1], "step") == "步骤\\1" && Text(result, "status") == "failed" && Text(result, "commit") == "unknown" && Text(result, "reason") == "原因 é\ttab"
        && lines[1].GetProperty("frame").GetInt64() == 9001 && lines[1].GetProperty("worldEpoch").GetInt64() == 3 && Text(lines[1], "subjectProvider") == "test.subject",
        "field values did not roundtrip");
    Check(Text(lines[1], "message")!.Contains("test.full", StringComparison.Ordinal) && Text(lines[1], "level") == "error", "message or level text");
});

Case("map.layout-generated carries the generated layout and refuses an unreadable one", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory);
    var zones = new[] { new RuntimeLogLayoutZone { Zone = "0/1/1", Dimension = 0, Layer = 1, LocalIndex = 1, Floor = 2, Tiles = new[] { "geo_a" } } };
    var connections = new[] { new RuntimeLogLayoutConnection { From = "0/1/1", To = "0/1/2", Direction = RuntimeLogLayoutDirections.North } };
    // A provider level below info writes nothing and throws nothing: a generated level is not an error.
    Kernel(writer, RuntimeLogLevel.Error).ReportGeneratedLayout(new RuntimeLogLayout { Complete = true, ElevatorLandedTick = 7, Zones = zones, Connections = connections });
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    RejectCode("log-record", () => kernel.ReportGeneratedLayout(new RuntimeLogLayout { Complete = true, ElevatorLandedTick = null, Zones = zones,
        Connections = new[] { new RuntimeLogLayoutConnection { From = "0/1/1", To = "0/1/2", Direction = "sideways" } } }), "unknown direction accepted");
    RejectCode("log-record", () => kernel.ReportGeneratedLayout(new RuntimeLogLayout { Complete = false, ElevatorLandedTick = null, Zones = zones,
        Connections = Array.Empty<RuntimeLogLayoutConnection>() }), "an incomplete observation carried a zone");
    RejectCode("log-record", () => kernel.ReportGeneratedLayout(new RuntimeLogLayout { Complete = true, ElevatorLandedTick = -1, Zones = zones,
        Connections = Array.Empty<RuntimeLogLayoutConnection>() }), "a negative landing tick accepted");
    kernel.ReportGeneratedLayout(new RuntimeLogLayout { Complete = true, ElevatorLandedTick = 7, Zones = zones, Connections = connections });
    writer.Dispose();
    var lines = Lines(writer);
    var layout = lines.Single(line => Text(line, "code") == RuntimeLogCodes.MapLayoutGenerated);
    string[] Names(JsonElement line) => line.EnumerateObject().Select(p => p.Name).ToArray();
    Check(Names(layout).SequenceEqual(new[] { "schema", "origin", "seq", "tick", "worldEpoch", "level", "code", "provider", "layout", "message" }),
        "map.layout-generated field order: " + string.Join(",", Names(layout)));
    var body = layout.GetProperty("layout");
    Check(body.GetProperty("complete").GetBoolean() && body.GetProperty("elevatorLandedTick").GetInt64() == 7
        && Text(body.GetProperty("zones")[0], "zone") == "0/1/1" && body.GetProperty("zones")[0].GetProperty("tiles")[0].GetString() == "geo_a"
        && body.GetProperty("zones")[0].GetProperty("floor").GetInt32() == 2
        && Text(body.GetProperty("connections")[0], "direction") == "north" && Text(lines[^1], "code") == RuntimeLogCodes.MapLayoutGenerated,
        "layout values did not round-trip");
    var started = new RuntimeLogLayout { Complete = false, ElevatorLandedTick = null, Zones = Array.Empty<RuntimeLogLayoutZone>(), Connections = Array.Empty<RuntimeLogLayoutConnection>() };
    Check(RuntimeLogLayoutDirections.IsKnown(started.Connections is null ? null : "same") && !RuntimeLogLayoutDirections.IsKnown("sideways"), "direction vocabulary");
});

Case("plan.loaded and plan.rejected carry path and permissions", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory);
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    var plan = new RuntimeLogPlan { PlanId = "plan-a", ResourceId = "resource-a", ResourceRevision = "1" };
    kernel.WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Info, Code = RuntimeLogCodes.PlanLoaded, Provider = Runtime, Tick = 1, WorldEpoch = 1,
        Plan = plan, Path = "Team-Pack/forge/plans/plan-a.plan.json", Permissions = new[] { "gtfo.enemy.health.read", "gtfo.enemy.health.write" } });
    kernel.WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.PlanRejected, Provider = Runtime, Tick = 2, WorldEpoch = 1,
        Path = "Team-Pack/forge/plans/plan-b.plan.json", Result = new RuntimeLogResult { Status = "rejected", Commit = null, Reason = "plan-conflict" } });
    writer.Dispose(); var lines = Lines(writer);
    var loaded = lines.Single(line => Text(line, "code") == RuntimeLogCodes.PlanLoaded);
    var rejected = lines.Single(line => Text(line, "code") == RuntimeLogCodes.PlanRejected);
    string[] Names(JsonElement line) => line.EnumerateObject().Select(p => p.Name).ToArray();
    Check(Names(loaded).SequenceEqual(new[] { "schema", "origin", "seq", "tick", "worldEpoch", "level", "code", "provider", "plan", "path", "permissions", "message" }),
        "plan.loaded field order: " + string.Join(",", Names(loaded)));
    Check(Names(rejected).SequenceEqual(new[] { "schema", "origin", "seq", "tick", "worldEpoch", "level", "code", "provider", "path", "result", "message" }),
        "plan.rejected field order without a resolved identity: " + string.Join(",", Names(rejected)));
    Check(Text(loaded, "path") == "Team-Pack/forge/plans/plan-a.plan.json", "plan.loaded path did not round-trip");
    Check(loaded.GetProperty("permissions").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "gtfo.enemy.health.read", "gtfo.enemy.health.write" }),
        "plan.loaded permissions did not round-trip in order");
    Check(Text(rejected, "path") == "Team-Pack/forge/plans/plan-b.plan.json" && Text(rejected.GetProperty("result"), "reason") == "plan-conflict",
        "plan.rejected path or result.reason did not round-trip");
    Check(!rejected.TryGetProperty("permissions", out _) && !rejected.TryGetProperty("plan", out _), "plan.rejected without a resolved identity leaked plan or permissions");
});

Case("plan-conflict Detail is folded into the message for every path in the group, never its own JSON field", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory);
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    string conflictDetail = string.Join(", ", "Team-Pack/forge/plans/a.plan.json", "Team-Pack/forge/plans/b.plan.json", "Other-Pack/forge/plans/c.plan.json");
    kernel.WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.PlanRejected, Provider = Runtime, Tick = 1, WorldEpoch = 1,
        Path = "Team-Pack/forge/plans/a.plan.json", Detail = conflictDetail, Result = new RuntimeLogResult { Status = "rejected", Commit = null, Reason = "plan-conflict" } });
    writer.Dispose(); var lines = Lines(writer);
    var rejected = lines.Single(line => Text(line, "code") == RuntimeLogCodes.PlanRejected);
    Check(!rejected.TryGetProperty("detail", out _), "Detail leaked as its own JSON field");
    string message = Text(rejected, "message")!;
    Check(message.Contains("Team-Pack/forge/plans/a.plan.json", StringComparison.Ordinal)
        && message.Contains("Team-Pack/forge/plans/b.plan.json", StringComparison.Ordinal)
        && message.Contains("Other-Pack/forge/plans/c.plan.json", StringComparison.Ordinal),
        "plan-conflict message did not carry every path in the conflict group: " + message);
});

Case("kernel records registration.rejected with the rejected provider as subject", () => {
    var sink = new CaptureSink(); var kernel = Kernel(sink, RuntimeLogLevel.Info);
    // A valid provider id with a capability owned by somebody else: the reason is the registry's own code.
    var broken = new RuntimeModule(RuntimeKernel.ApiVersion, JsonSerializer.Serialize(new {
        providers = new[] { new { id = "test.records.broken", kind = "native", version = "1.0.0", dependencies = Array.Empty<object>() } },
        capabilities = new[] { new { id = "test.records.broken.capability", owner = "test.records.other", kind = "trigger",
            version = "1.0.0", label = "broken", parameters = new { }, graph = new { domains = new[] { "logic" }, execution = "host",
                inputs = Array.Empty<object>(), outputs = new[] { new { id = "next", type = "execution" } }, parameters = Array.Empty<object>() } } },
        bindings = Array.Empty<object>()
    }), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
    RejectCode("capability-owner", () => kernel.RegisterModule(broken, RuntimeLogLevel.Info), "broken registration accepted");
    var record = sink.Records.Single(r => r.Code == RuntimeLogCodes.RegistrationRejected);
    Check(record.Level == RuntimeLogLevel.Error && record.Provider == Runtime && record.SubjectProvider == "test.records.broken",
        "registration.rejected level, owner or subject: " + record.Provider + "/" + record.SubjectProvider);
    Check(record.Result is { Status: "rejected", Commit: null, Reason: "capability-owner" },
        "registration.rejected must carry status and reason but no commit: " + record.Result?.Commit);
    Check(record.Tick == -1 && record.WorldEpoch == 0, "registration.rejected tick or epoch before the world began");
    Check(kernel.LogGate(Runtime).Level == RuntimeLogLevel.Info, "a rejected registration changed the level table");
});

Case("kernel records binding.registered per binding under the binding's provider", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Info);
    var records = fixture.Sink.Records.Where(r => r.Code == RuntimeLogCodes.BindingRegistered).ToArray();
    Check(records.Length == 5, "expected one binding.registered per registered binding, got " + records.Length);
    Check(records.All(r => r.Level == RuntimeLogLevel.Info && r.Tick == -1 && r.WorldEpoch == 0), "binding.registered level or tick");
    // The provider of a record is the binding's own provider, so the two packages show up separately.
    Check(records.Select(r => r.Provider + " " + r.Binding).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] {
        RecordFixture.ActionProvider + " " + RecordFixture.ActionBinding,
        RecordFixture.ActionProvider + " " + RecordFixture.FailBinding,
        RecordFixture.SecondActionProvider + " " + RecordFixture.SecondActionBinding,
        RecordFixture.SecondActionProvider + " " + RecordFixture.SecondTriggerBinding,
        RecordFixture.TriggerProvider + " " + RecordFixture.TriggerBinding }), "binding.registered attribution or binding id");
    using var extra = fixture.AddTriggerBinding("test.records.third", "test.records.third.binding.fired");
    Check(fixture.Sink.Records.Count(r => r.Code == RuntimeLogCodes.BindingRegistered) == 6, "a later registration added no binding record");
    Check(fixture.Sink.Levels[^1].Providers.Select(p => p.Provider).Contains("test.records.third"), "level table did not gain the new provider");
    // Registration publishes one table snapshot and writes its binding records against it, so every record sees a table
    // that already lists the provider it came from.
    Check(fixture.Sink.Records.Count == fixture.Sink.Levels.Count, "each record must carry the table of its own registration");
    foreach (var (record, levels) in fixture.Sink.Records.Zip(fixture.Sink.Levels))
        Check(levels.Providers.Any(p => p.Provider == record.Provider), "record provider missing from its level table: " + record.Provider);
});

Case("kernel records world.began, plan.loaded and plan.rejected under Runtime", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Info);
    fixture.Start();
    var began = fixture.Records(RuntimeLogCodes.WorldBegan).Single();
    Check(began.Level == RuntimeLogLevel.Info && began.Provider == Runtime && began.Tick == -1 && began.WorldEpoch == 1, "world.began level, owner or tick");
    var loaded = fixture.Records(RuntimeLogCodes.PlanLoaded).Single();
    // The fixture names its plan file after the plan id, which already ends in ".plan": the record carries the
    // candidate path verbatim, so the doubled suffix is the real discovered name.
    Check(loaded.Level == RuntimeLogLevel.Info && loaded.Provider == Runtime && loaded.Path == "pack/plans/test.records.plan.plan.json"
        && loaded.Plan is { PlanId: "test.records.plan", ResourceId: "test.records.plan", ResourceRevision: "1" }
        && loaded.Permissions is { Count: 0 } && loaded.Result == null, "plan.loaded required fields");
    var rejected = fixture.Kernel.LoadPlans(new[] { PlanCandidate.Loaded("pack/plans/bad.plan.json", "{\"schemaVersion\":4}") })[0];
    Check(!rejected.Loaded && rejected.Code == "missing-field", "a malformed plan was accepted: " + rejected.Code);
    var record = fixture.Records(RuntimeLogCodes.PlanRejected).Single();
    Check(record.Level == RuntimeLogLevel.Error && record.Provider == Runtime && record.Path == "pack/plans/bad.plan.json"
        && record.Result is { Status: "rejected", Reason: "missing-field" } && record.Plan == null, "plan.rejected required fields");
});

Case("kernel records trigger.fired with the binding provider and the root event", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Info);
    fixture.Start();
    var dispatch = fixture.Trigger.Publish(fixture.Event("evt-root"));
    Check(dispatch.Status == "queued", "publish refused: " + dispatch.Code);
    var fired = fixture.Records(RuntimeLogCodes.TriggerFired, RecordFixture.TriggerProvider).Single();
    Check(fired.Level == RuntimeLogLevel.Info && fired.Binding == RecordFixture.TriggerBinding && fired.EventId == "evt-root"
        && fired.RootEventId == "evt-root" && fired.CauseId == null && fired.Tick == 0 && fired.WorldEpoch == 1 && fired.Result == null,
        "trigger.fired required fields or attribution");
});

Case("kernel records step.started and step.finished under the executing binding's provider", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Info);
    fixture.Kernel.ElevateLogging();
    fixture.Start();
    fixture.AdvanceAndCollect("evt-steps");
    // The entry hangs off the trigger package's binding, but the step runs in another package: attribution follows the
    // binding that executes the step.
    var started = fixture.Records(RuntimeLogCodes.StepStarted).Single();
    var finished = fixture.Records(RuntimeLogCodes.StepFinished).Single();
    Check(started.Level == RuntimeLogLevel.Trace && started.Provider == RecordFixture.ActionProvider && started.Step == "B"
        && started.Entry == "A" && started.Binding == RecordFixture.ActionBinding && started.EventId == "evt-steps"
        && started.RootEventId == "evt-steps" && started.CommandId == finished.CommandId
        && started.Plan is { PlanId: "test.records.plan", ResourceRevision: "1" }, "step.started required fields or attribution");
    Check(finished.Level == RuntimeLogLevel.Info && finished.Provider == RecordFixture.ActionProvider && finished.Step == "B"
        && finished.Result is { Status: "succeeded", Commit: "confirmed", Reason: "committed" }, "step.finished level or result");
    Check(fixture.Records(RuntimeLogCodes.EntryStopped).Length == 0, "a succeeded entry recorded a stop");
});

Case("kernel records a failed step and entry.stopped one step before the entry's end", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Info);
    fixture.Start();
    // The second entry answers its own trigger binding, so only its two steps are involved.
    fixture.AdvanceAndCollect("evt-stopped", binding: RecordFixture.SecondTriggerBinding);
    var failure = fixture.Records(RuntimeLogCodes.StepFinished).Single();
    Check(failure.Level == RuntimeLogLevel.Error && failure.Provider == RecordFixture.ActionProvider
        && failure.Result is { Status: "failed", Commit: "unknown", Reason: "handler-exception" },
        "a failed/unknown step is not recorded as an error: " + failure.Level + "/" + failure.Result?.Reason);
    var stopped = fixture.Records(RuntimeLogCodes.EntryStopped).Single();
    Check(stopped.Level == RuntimeLogLevel.Info && stopped.Provider == Runtime && stopped.Entry == "C" && stopped.Step == "D"
        && stopped.CommandId == failure.CommandId && stopped.EventId == "evt-stopped" && stopped.Result == null,
        "entry.stopped required fields or attribution");
    // The step after D belongs to a third package, and neither record of it was written.
    Check(fixture.Records(RuntimeLogCodes.StepStarted).Count(r => r.Step == "E") == 0
        && fixture.Records(RuntimeLogCodes.StepFinished).All(r => r.Provider != RecordFixture.SecondActionProvider),
        "a stopped entry executed or recorded its successor");
    // The other entry's single step succeeds, so the stop count stays where the failed step left it.
    fixture.Kernel.BeginWorld(2);
    fixture.Kernel.Advance(1, true);
    fixture.AdvanceAndCollect("evt-last", binding: RecordFixture.TriggerBinding, tick: 2);
    Check(fixture.Records(RuntimeLogCodes.StepFinished).Any(r => r.Step == "B")
        && fixture.Records(RuntimeLogCodes.EntryStopped).Length == 1, "a succeeding step changed the stop count");
});

Case("kernel records event.deferred once per tick at trace", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Info);
    fixture.Kernel.ElevateLogging();
    fixture.Start(maxEventsPerTick: 1);
    foreach (var id in new[] { "evt-one", "evt-two" }) Check(fixture.Trigger.Publish(fixture.Event(id)).Status == "queued", "publish refused");
    fixture.Kernel.Advance(1, true);
    var deferred = fixture.Records(RuntimeLogCodes.EventDeferred).Single();
    Check(deferred.Level == RuntimeLogLevel.Trace && deferred.Provider == Runtime && deferred.Binding == RecordFixture.TriggerBinding
        && deferred.Result is { Status: "deferred", Reason: "plan-tick-event-budget" }, "event.deferred required fields");
    Check(fixture.Records(RuntimeLogCodes.EventRejected).Length == 0 && fixture.Records(RuntimeLogCodes.BudgetExceeded).Length == 0,
        "a deferred event was also recorded as rejected");
    fixture.Kernel.Advance(2, true);
    Check(fixture.Records(RuntimeLogCodes.EventDeferred).Length == 1, "a caught-up tick recorded a second deferral");
    Check(fixture.Records(RuntimeLogCodes.TriggerFired).Count(r => r.EventId == "evt-two") == 1,
        "the deferred event did not dispatch on the next tick");
});

Case("kernel records event.cancelled at trace when the scope is gone", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Info);
    fixture.Kernel.ElevateLogging();
    fixture.Start();
    Check(fixture.Trigger.Publish(fixture.Event("evt-cancelled")).Status == "queued", "publish refused");
    Check(fixture.Trigger.CancelScope("test.records.scope") == 1, "scope cancel did not find the queued event");
    fixture.Kernel.Advance(1, true);
    var cancelled = fixture.Records(RuntimeLogCodes.EventCancelled).Single();
    Check(cancelled.Level == RuntimeLogLevel.Trace && cancelled.Provider == Runtime && cancelled.Binding == RecordFixture.TriggerBinding
        && cancelled.EventId == "evt-cancelled" && cancelled.SubjectProvider == RecordFixture.TriggerProvider
        && cancelled.Result is { Status: "cancelled", Reason: "source-lifecycle" },
        "event.cancelled required fields");
});

Case("kernel splits a refused event between event.rejected and budget.exceeded", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Info);
    fixture.Start();
    // An event from an older world is refused with a non-budget reason.
    var rejected = fixture.Trigger.Publish(fixture.Event("evt-stale") with { WorldEpoch = 0 });
    Check(rejected.Status == "rejected" && rejected.Code == "stale-world", "stale publish accepted: " + rejected.Status + "/" + rejected.Code);
    var record = fixture.Records(RuntimeLogCodes.EventRejected).Single();
    Check(record.Level == RuntimeLogLevel.Error && record.Provider == Runtime && record.Binding == RecordFixture.TriggerBinding
        && record.EventId == "evt-stale" && record.SubjectProvider == RecordFixture.TriggerProvider
        && record.Result is { Status: "rejected", Reason: "stale-world" },
        "event.rejected required fields");
    Check(fixture.Records(RuntimeLogCodes.BudgetExceeded).Length == 0, "a non-budget refusal was also recorded as budget.exceeded");
    // A queue at its budget is a budget reason, so the same refusal is recorded as budget.exceeded instead. The plan
    // may not declare a queue budget above the kernel ceiling, so both are one here and the plan's own budget refuses.
    var full = new RecordFixture(RuntimeLogLevel.Info, new RuntimeLimits { MaxQueuedEvents = 1 });
    full.Start(maxQueuedEvents: 1);
    Check(full.Trigger.Publish(full.Event("evt-first")).Status == "queued", "first publish refused");
    Check(full.Trigger.Publish(full.Event("evt-budget")).Status == "rejected", "a full queue accepted a second event");
    var budget = full.Records(RuntimeLogCodes.BudgetExceeded).Single();
    Check(budget.Level == RuntimeLogLevel.Error && budget.Provider == Runtime && budget.Binding == RecordFixture.TriggerBinding
        && budget.EventId == "evt-budget" && budget.Result is { Status: "rejected", Reason: "plan-queue-budget" },
        "budget.exceeded required fields");
    Check(full.Records(RuntimeLogCodes.EventRejected).Length == 0, "a budget refusal was also recorded as event.rejected");
    Check(fixture.Records(RuntimeLogCodes.BudgetExceeded).Length == 0, "an unrelated kernel recorded the refusal");
});

Case("kernel records observer.failed under the observer's provider and suspends once", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Info);
    using var bad = fixture.Action.ObserveLifecycle(_ => throw new InvalidOperationException("observer boom"), false);
    fixture.Kernel.StartRuntime(() => { });
    var failed = fixture.Records(RuntimeLogCodes.ObserverFailed).Single();
    Check(failed.Level == RuntimeLogLevel.Error && failed.Provider == RecordFixture.ActionProvider
        && failed.Result is { Status: "failed", Reason: "lifecycle-observer-failed" } && failed.Detail != null,
        "observer.failed level, attribution or result");
    fixture.Kernel.LogSuspended("bridge-exception", "host callback threw");
    var suspended = fixture.Records(RuntimeLogCodes.RuntimeSuspended).Single();
    Check(suspended.Level == RuntimeLogLevel.Error && suspended.Provider == Runtime
        && suspended.Result is { Status: "failed", Reason: "bridge-exception" }, "runtime.suspended level, owner or result");
    Exception? threadFailure = null;
    var thread = new Thread(() => { try { fixture.Kernel.LogSuspended("bridge-exception"); } catch (Exception error) { threadFailure = error; } });
    thread.Start(); thread.Join();
    Check(threadFailure is RuntimeContractException { Code: "wrong-thread" }, "a suspend record was accepted off the simulation thread");
    fixture.Kernel.StopRuntime();
    Check(fixture.Records(RuntimeLogCodes.RuntimeSuspended).Length == 1, "a normal stop recorded a suspension");
});

Case("a disabled level keeps every kernel record point out of the sink", () => {
    var fixture = new RecordFixture(RuntimeLogLevel.Off);
    Check(fixture.Sink.Records.Count == 0, "registration wrote at Off");
    Check(fixture.Kernel.IsRegistrationOpen, "the fixture could not register at Off");
    fixture.Kernel.BeginWorld(1);
    using (var observer = fixture.Action.ObserveLifecycle(_ => throw new InvalidOperationException("off boom"), true)) { }
    Check(fixture.Sink.Records.Count == 0, "registration, world or observer wrote at Off");
    fixture.Kernel.LoadPlans(new[] { PlanCandidate.Loaded("pack/plans/bad.plan.json", "{\"schemaVersion\":4}") });
    fixture.Kernel.LogSuspended("checkpoint-restore", "test restore");
    Check(fixture.Sink.Records.Count == 0, "a disabled runtime level still reached the sink: "
        + string.Join(",", fixture.Sink.Records.Select(r => r.Code)));
    // Elevation is the only way in, and it reaches a gate obtained before it.
    var gate = fixture.Kernel.LogGate(RecordFixture.TriggerProvider);
    fixture.Kernel.ElevateLogging();
    Check(gate.Level == RuntimeLogLevel.Trace, "elevation did not reach a gate obtained earlier");
    fixture.Kernel.BeginWorld(2);
    Check(fixture.Records(RuntimeLogCodes.WorldBegan, Runtime).Length == 1, "an elevated provider wrote nothing");
});

Case("kernel records reach the session file with at most two log.level lines", () => {
    var directory = NewDirectory(); var (writer, _, _) = Writer(directory);
    var kernel = Kernel(writer, RuntimeLogLevel.Info);
    using var trigger = kernel.RegisterModule(RecordFixture.PublicTriggerModule(), RuntimeLogLevel.Info);
    kernel.BeginWorld(1);
    kernel.StartRuntime(() => { });
    kernel.Advance(0, true);
    kernel.LogSuspended("checkpoint-restore", "test restore");
    writer.Dispose(); var lines = Lines(writer);
    // The first real record opens the file with the current table; registration adds providers without a line of its own.
    Check(Codes(lines).Count(code => code == RuntimeLogCodes.LogLevel) == 1, "a registration added a log.level line of its own");
    Check(Codes(lines)[0] == RuntimeLogCodes.LogLevel, "log.level is not the first line of the session file");
    var suspended = lines.Single(line => Text(line, "code") == RuntimeLogCodes.RuntimeSuspended);
    Check(Text(suspended, "level") == "error" && Text(suspended, "provider") == Runtime
        && Text(suspended.GetProperty("result"), "reason") == "checkpoint-restore", "runtime.suspended did not round-trip to JSONL");
    Check(lines.Any(line => Text(line, "code") == RuntimeLogCodes.WorldBegan && Text(line, "level") == "info"), "world.began missing from the file");
    Check(Codes(lines).Count(code => code == RuntimeLogCodes.BindingRegistered) == 1, "binding.registered missing from the file");
});

Case("privacy: no Steam64-shaped numbers", () => {
    var steam = new Regex("(?<![0-9])[0-9]{17}(?![0-9])");
    Check(steam.IsMatch("id 76561198000000000.") && !steam.IsMatch("765611980000000001"), "privacy pattern control");
    var files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
    Check(files.Length >= 10, "privacy scan found too few session files");
    foreach (var file in files) Check(!steam.IsMatch(File.ReadAllText(file)), "Steam64-shaped number in " + file);
    lock (console) Check(console.Count > 0 && !console.Any(steam.IsMatch), "Steam64-shaped number in console mirror");
    Check(typeof(RuntimeLogRecord).GetProperties().All(p => p.PropertyType == typeof(string) || p.PropertyType == typeof(long) || p.PropertyType == typeof(long?)
        || p.PropertyType == typeof(RuntimeLogLevel) || p.PropertyType == typeof(RuntimeLogPlan?) || p.PropertyType == typeof(RuntimeLogResult?)
        || p.PropertyType == typeof(RuntimeLogLayout?) || p.PropertyType == typeof(IReadOnlyList<string>))
        && !typeof(RuntimeLogRecord).GetProperties().Any(p => p.Name.Contains("Player", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Steam", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Name", StringComparison.OrdinalIgnoreCase)),
        "log record gained a player identity field");
});

Case("log APIs reject the wrong thread", () => {
    var sink = new CaptureSink(); var kernel = Kernel(sink, RuntimeLogLevel.Info); Exception? failure = null;
    var thread = new Thread(() => {
        try
        {
            RejectCode("wrong-thread", () => kernel.WriteLog(Record(RuntimeLogLevel.Error, "thread", 1)), "WriteLog accepted another thread");
            RejectCode("wrong-thread", kernel.ElevateLogging, "ElevateLogging accepted another thread");
            RejectCode("wrong-thread", () => kernel.LogGate(Runtime), "LogGate accepted another thread");
        }
        catch (Exception error) { failure = error; }
    });
    thread.Start(); thread.Join();
    if (failure != null) throw failure;
    Check(sink.Records.Count == 0 && kernel.LogGate(Runtime).Level == RuntimeLogLevel.Info, "wrong-thread call changed state");
});

Console.WriteLine($"Runtime log: {checks} assertions passed; {failures} scenarios failed.");
Console.WriteLine("Real BepInEx ManualLogSource and files under " + root + " only; no game, profile or kernel/domain record points.");
return failures == 0 ? 0 : 1;

static bool Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return true; }
    return false;
}

internal sealed class CaptureSink : IRuntimeLogSink
{
    internal readonly List<RuntimeLogRecord> Records = new();
    internal readonly List<RuntimeLogLevels> Levels = new();
    public void Write(in RuntimeLogRecord record, RuntimeLogLevels levels) { Records.Add(record); Levels.Add(levels); }
}
