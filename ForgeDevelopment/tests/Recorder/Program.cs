using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ForgeDevelopment.Native;

// The recorder core's focused suite. It runs in a plain process: the session writes real JSONL to a temporary
// directory, the reader reflects over managed stand-ins, and the tracer reaches its matching, rejection, rate and
// change decisions over stand-in types. No game, no IL2CPP, no Unity.

var runner = new Runner();
string root = Path.Combine(Path.GetTempPath(), "forge-rec-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
try
{
    runner.Case("session writes one JSONL line per record with the fixed context", () =>
    {
        var directory = Path.Combine(root, "basic");
        var context = new TestContext();
        RecSession.UseContext(context);
        Probe.That(RecSession.Open(directory, "rec-basic", 1024 * 1024, 1024 * 1024, false, 64 * 1024,
            _ => { }, new[] { ("plugin", "test") }, "session.start"), "the session did not open");
        RecSession.Write("tracer", "call", json => { json.WriteString("method", "Open"); json.WriteNumber("call", 1); });
        RecSession.Write("log", "line", json => json.WriteString("message", "hello"));
        RecSession.Stop();
        Probe.That(!RecSession.Active, "the session stayed active after Stop");

        var lines = ReadLines(Path.Combine(directory, "rec-rec-basic-000.jsonl"));
        Probe.That(lines.Count == 3, "expected three records, got " + lines.Count);
        using var first = JsonDocument.Parse(lines[0]);
        Probe.That(first.RootElement.GetProperty("channel").GetString() == "session" && first.RootElement.GetProperty("kind").GetString() == "session.start",
            "the start fact is not the first record");
        Probe.That(first.RootElement.GetProperty("v").GetString() == RecSession.SchemaVersion, "the schema version is missing");
        Probe.That(first.RootElement.GetProperty("role").GetString() == "host" && first.RootElement.GetProperty("slot").GetString() == "2",
            "the record does not carry the session role and slot");
        Probe.That(first.RootElement.GetProperty("level").GetString() == "Rundown/Test", "the record does not carry the level");
        using var third = JsonDocument.Parse(lines[2]);
        Probe.That(third.RootElement.GetProperty("seq").GetInt64() == 3, "the sequence number is not per record");
        Probe.That(third.RootElement.GetProperty("body").GetProperty("message").GetString() == "hello", "the body is not the caller's object");

        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "index.json")));
        Probe.That(index.RootElement.GetProperty("channels").GetProperty("tracer").GetInt64() == 1, "the index does not count the tracer channel");
        Probe.That(index.RootElement.GetProperty("channels").GetProperty("log").GetInt64() == 1, "the index does not count the log channel");
        Probe.That(index.RootElement.GetProperty("segments").GetArrayLength() == 1, "the index does not list one segment");
        Probe.That(index.RootElement.GetProperty("budgetReached").GetBoolean() == false, "the index claims the budget was reached");
    });

    runner.Case("segments roll at the segment size and the index lists every one", () =>
    {
        var directory = Path.Combine(root, "segments");
        RecSession.UseContext(new TestContext());
        RecSession.Open(directory, "rec-seg", 1024 * 1024, 600, false, 64 * 1024, _ => { }, null, "session.start");
        for (var i = 0; i < 30; i++) RecSession.Write("tracer", "call", json => json.WriteNumber("n", i));
        RecSession.Stop();
        var segments = Directory.GetFiles(directory, "*.jsonl");
        Probe.That(segments.Length >= 3, "30 records at 600 bytes per segment produced " + segments.Length + " segments");
        var total = segments.Sum(file => ReadLines(file).Count);
        Probe.That(total == 30, "the segments lost records: " + total + " of 30");
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "index.json")));
        Probe.That(index.RootElement.GetProperty("segments").GetArrayLength() == segments.Length,
            "the index lists " + index.RootElement.GetProperty("segments").GetArrayLength() + " segments for " + segments.Length + " files");
    });

    runner.Case("the total budget stops the session and counts every dropped record", () =>
    {
        var directory = Path.Combine(root, "budget");
        var notices = new List<string>();
        RecSession.UseContext(new TestContext());
        RecSession.Open(directory, "rec-budget", 4000, 1024 * 1024, false, 64 * 1024, notices.Add, null, "session.start");
        for (var i = 0; i < 400; i++) RecSession.Write("tracer", "call", json => json.WriteString("payload", new string('x', 80)));
        RecSession.Stop();
        Probe.That(RecSession.BudgetReached, "the session never reported reaching its budget");
        Probe.That(RecSession.Dropped > 0, "records were dropped past the budget but none was counted");
        Probe.That(RecSession.BytesWritten <= 4000, "the session wrote " + RecSession.BytesWritten + " bytes past its 4000 byte budget");
        Probe.That(notices.Any(n => n.Contains("dropped")), "the drop was not announced");
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "index.json")));
        Probe.That(index.RootElement.GetProperty("budgetReached").GetBoolean(), "the index does not say the budget was reached");
        Probe.That(index.RootElement.GetProperty("dropped").GetInt64() == RecSession.Dropped, "the index drop count differs from the session's");
    });

    runner.Case("gzip segments are readable and hold the same records", () =>
    {
        var directory = Path.Combine(root, "gzip");
        RecSession.UseContext(new TestContext());
        RecSession.Open(directory, "rec-gzip", 1024 * 1024, 1024 * 1024, true, 64 * 1024, _ => { }, null, "session.start");
        for (var i = 0; i < 5; i++) RecSession.Write("state", "snapshot", json => json.WriteNumber("n", i));
        RecSession.Stop();
        var file = Directory.GetFiles(directory, "*.gz").Single();
        var lines = ReadGzipLines(file);
        Probe.That(lines.Count == 5, "the gzip segment holds " + lines.Count + " records");
        using var last = JsonDocument.Parse(lines[4]);
        Probe.That(last.RootElement.GetProperty("body").GetProperty("n").GetInt32() == 4, "the gzip segment's last record is wrong");
    });

    runner.Case("the reader caps depth, elements, cycles and throws nothing", () =>
    {
        RecReflect.Probe = new TestProbe();
        RecReflect.MaxDepth = 3;
        RecReflect.MaxElements = 3;
        RecReflect.BudgetMilliseconds = 1000;
        try
        {
            var shallow = new Node("root", new Node("child", new Node("grandchild", new Node("deeper", null))));
            var json = Write(writer => RecReflect.WriteValue(writer, shallow, 0));
            Probe.That(json.Contains("\"$depthCapped\":true"), "the depth cap did not fire: " + json);
            Probe.That(json.Contains("grandchild"), "the value was not read to the cap: " + json);

            var cycle = new Node("a", null);
            cycle.Next = cycle;
            json = Write(writer => RecReflect.WriteValue(writer, cycle, 0));
            Probe.That(json.Contains("\"$ref\""), "the cycle was not detected: " + json);

            var many = new Node("list", null) { Items = Enumerable.Range(0, 20).Select(i => i).ToList() };
            json = Write(writer => RecReflect.WriteValue(writer, many, 0));
            Probe.That(json.Contains("\"$truncatedAt\""), "the element cap did not fire: " + json);
            Probe.That(json.Contains("\"$type\""), "the reader did not describe the type: " + json);

            var failing = new Throwing();
            json = Write(writer => RecReflect.WriteValue(writer, failing, 0));
            Probe.That(json.Contains("$error"), "a throwing member was not reported as an error: " + json);
            Probe.That(json.Contains("\"$type\""), "the failing object lost its type: " + json);
        }
        finally
        {
            RecReflect.Probe = RecInertProbe.Instance;
            RecReflect.MaxDepth = RecReflect.DefaultMaxDepth;
            RecReflect.MaxElements = RecReflect.DefaultMaxElements;
            RecReflect.BudgetMilliseconds = RecReflect.DefaultBudgetMilliseconds;
        }
    });

    runner.Case("the reader resolves a dotted path, private fields and a probe's native member", () =>
    {
        var probe = new TestProbe();
        RecReflect.Probe = probe;
        try
        {
            var outer = new Outer { Inner = new Node("inner", null) { Label = "leaf" } };
            Probe.That((string?)RecReflect.ReadPath(outer, "Inner.Label") == "leaf", "a nested property path did not resolve");
            Probe.That((string?)RecReflect.ReadPath(outer, "Inner.m_name") == "inner", "a private field did not resolve");
            Probe.That(RecReflect.ReadPath(outer, "Inner.Missing") == null, "a missing member answered a value");
            Probe.That(RecReflect.ReadPath(null, "Inner") == null, "a null root answered a value");

            var handle = new TestProbe.Handle(9);
            probe.NativeMembers[handle] = new Dictionary<string, object?> { ["m_nativeState"] = 5 };
            Probe.That(Convert.ToInt32(RecReflect.ReadPath(handle, "m_nativeState")) == 5, "a probe native member did not resolve");

            probe.Ids[handle] = 4242;
            Probe.That(RecReflect.Describe(handle).Contains("4242"), "the description does not carry the probe's identity: " + RecReflect.Describe(handle));
            probe.Destroyed.Add(handle);
            var json = Write(writer => RecReflect.WriteValue(writer, handle, 0));
            Probe.That(json.Contains("\"$destroyed\":true"), "a destroyed probe object was not reported: " + json);

            var vector = new TestProbe.Vector3Like(1.5f, 2.5f, 3.5f);
            json = Write(writer => RecReflect.WriteValue(writer, vector, 0));
            Probe.That(json.Contains("\"x\":1.5") && json.Contains("\"z\":3.5"), "the probe's own value type was not written by the probe: " + json);
            Probe.That(RecReflect.IsScalar(vector), "the probe's value type was not treated as a scalar");
            Probe.That(RecReflect.IsScalar(TimeSpan.FromSeconds(1)) && !RecReflect.IsScalar(new Node("x", null)), "the scalar test is wrong");
        }
        finally { RecReflect.Probe = RecInertProbe.Instance; }
    });

    runner.Case("glob matching covers wildcards, namespaces and non-matches", () =>
    {
        Probe.That(RecGlob.IsMatch("*", "anything"), "* did not match");
        Probe.That(RecGlob.IsMatch("get_*", "get_State"), "a prefix glob did not match");
        Probe.That(!RecGlob.IsMatch("get_*", "Set_State"), "a prefix glob matched the wrong name");
        Probe.That(RecGlob.IsMatch("*Door*", "LG_SecurityDoor_A"), "an infix glob did not match");
        Probe.That(RecGlob.IsMatch("On?GUI", "OnGUI".Replace("GUI", "XGUI")), "a ? glob did not match one character");
        Probe.That(RecGlob.IsMatch("Send*", "Send"), "a trailing glob did not match the empty rest");
        Probe.That(!RecGlob.IsMatch("Send*Command", "Send"), "a glob matched a shorter name");
        Probe.That(RecGlob.MatchesType("LevelGeneration.*Door", "LevelGeneration.LG_SecurityDoor", "LG_SecurityDoor"), "a namespace glob did not match the full name");
        Probe.That(RecGlob.MatchesType("LG_*Door", "LevelGeneration.LG_SecurityDoor", "LG_SecurityDoor"), "a wildcard did not match the short name");
        Probe.That(!RecGlob.MatchesType("LG_*Door", "GameData.LG_ComputerTerminal", "LG_ComputerTerminal"), "a wildcard matched the wrong type");
    });

    runner.Case("the tracer parses profiles and refuses what the reject table names", () =>
    {
        var path = Path.Combine(root, "trace-door.json");
        File.WriteAllText(path, """
        { "profile": "door-terminal", "enabledByDefault": true, "entries": [
          { "type": "*FakeSecurityDoor", "methods": ["*"], "exclude": ["Update", "get_*"],
            "record": "args+result", "instance": ["m_state", "Label"], "firstStack": true, "maxPerSecond": 2, "onOverflow": "count" }
        ]}
        """);
        var profiles = RecTracer.Parse(new[] { path });
        Probe.That(profiles.Count == 1 && profiles[0].Error == null, "the profile did not parse: " + profiles[0].Error);
        Probe.That(profiles[0].Name == "door-terminal" && profiles[0].EnabledByDefault, "the profile name or default is wrong");
        var entry = profiles[0].Entries.Single();
        Probe.That(entry.MatchesMethod("Open") && !entry.MatchesMethod("Update") && !entry.MatchesMethod("get_State"),
            "the include/exclude patterns are wrong");
        Probe.That(entry.RecordsArgs && entry.RecordsResult && !entry.RecordsChanges, "the record mode was not read");
        Probe.That(entry.InstanceFields.SequenceEqual(new[] { "m_state", "Label" }), "the instance paths were not read");
        Probe.That(entry.MaxPerSecond == 2 && entry.OnOverflow == "count", "the rate settings were not read");

        var rejects = Path.Combine(root, "trace-rejects.json");
        File.WriteAllText(rejects, """
        { "profile": "rejects", "entries": [
          { "type": "*FakeSecurityDoor", "methods": ["*"], "record": "count" },
          { "type": "*FakeTerminal", "methods": ["*"], "record": "count" }
        ]}
        """);
        var skipProfile = RecTracer.Parse(new[] { rejects }).Single();
        RecTracerRuntime.Match(skipProfile.Entries[0], new[] { typeof(StandIns.FakeSecurityDoor) });
        RecTracerRuntime.Match(skipProfile.Entries[1], new[] { typeof(StandIns.FakeTerminal) });
        var skips = RecTracerRuntime.Skipped;
        Probe.That(skips.Any(s => s.Method == "Update" && s.Reason == "frame-loop"), "Update was not refused as a frame loop");
        Probe.That(skips.Any(s => s.Method == "LateUpdate" && s.Reason == "frame-loop"), "LateUpdate was not refused");
        Probe.That(skips.Any(s => s.Method == "OnGUI" && s.Reason == "immediate-mode-ui"), "OnGUI was not refused");
        Probe.That(skips.Any(s => s.Method == "get_Login" && s.Reason == "property-getter"), "a getter was not refused");
        Probe.That(skips.Any(s => s.Method == "Echo" && s.Reason == "generic"), "a generic method was not refused");
        Probe.That(skips.Any(s => s.Method == "Configure" && s.Reason == "unsupported-parameter"), "a by-ref parameter was not refused");
        var matched = RecTracerRuntime.Matched("rejects").Select(p => p.Method.Name).ToArray();
        Probe.That(matched.Contains("Open") && matched.Contains("Close") && matched.Contains("SendCommand") && matched.Contains("Login"),
            "the methods that should be patched were not matched: " + string.Join(",", matched));
        Probe.That(!matched.Contains("Update") && !matched.Contains("Echo"), "a refused method was matched anyway");
    });

    runner.Case("an entry may name a hot method explicitly and the rejection is overridden", () =>
    {
        var path = Path.Combine(root, "trace-explicit.json");
        File.WriteAllText(path, """
        { "profile": "explicit", "entries": [
          { "type": "*FakeSecurityDoor", "methods": ["Update"], "record": "count" },
          { "type": "*FakeTerminal", "methods": ["get_Login"], "record": "count" }
        ]}
        """);
        var profile = RecTracer.Parse(new[] { path }).Single();
        RecTracerRuntime.Match(profile.Entries[0], new[] { typeof(StandIns.FakeSecurityDoor) });
        RecTracerRuntime.Match(profile.Entries[1], new[] { typeof(StandIns.FakeTerminal) });
        var matched = RecTracerRuntime.Matched("explicit").Select(p => p.Method.Name).ToArray();
        Probe.That(matched.Contains("Update"), "an explicitly named Update was still refused");
        Probe.That(matched.Contains("get_Login"), "an explicitly named getter was still refused");
    });

    runner.Case("a malformed profile is reported and never installs anything", () =>
    {
        var path = Path.Combine(root, "trace-broken.json");
        File.WriteAllText(path, "{ \"profile\": \"broken\", \"entries\": [ { \"methods\": [\"*\"] } ] }");
        var profile = RecTracer.Parse(new[] { path }).Single();
        Probe.That(profile.Error != null, "a typeless entry was accepted");
        Probe.That(profile.Entries.Count == 0, "a typeless entry produced a candidate");
        var notJson = Path.Combine(root, "trace-notjson.json");
        File.WriteAllText(notJson, "{ this is not json");
        var broken = RecTracer.Parse(new[] { notJson }).Single();
        Probe.That(broken.Error != null && broken.Error.Contains("Json"), "invalid JSON was not reported: " + broken.Error);
        Probe.That(RecTracerRuntime.HarmonyFailure == null, "the tracer reported a Harmony failure before any install");
    });

    runner.Case("the rate gate admits maxPerSecond and counts the overflow", () =>
    {
        var gate = new RecTracer.RateGate { MaxPerSecond = 3 };
        Probe.That(gate.Admit(0) && gate.Admit(10) && gate.Admit(20), "the gate refused inside its rate");
        Probe.That(!gate.Admit(30) && !gate.Admit(40), "the gate admitted past its rate");
        Probe.That(gate.TakeDropped() == 2, "the overflow count is wrong");
        Probe.That(gate.TakeDropped() == 0, "the overflow count was not consumed exactly once");
        Probe.That(gate.Admit(1000), "the gate did not reset on the next second");
    });

    runner.Case("changes records only when the compared values differ", () =>
    {
        var tracker = new RecTracer.ChangeTracker();
        Probe.That(tracker.Changed("a"), "the first value was not a change");
        Probe.That(!tracker.Changed("a"), "the same value was reported as a change");
        Probe.That(tracker.Changed("b"), "a different value was not a change");
        tracker.Reset();
        Probe.That(tracker.Changed("a"), "a reset tracker did not report a change");
    });

    runner.Case("a patch writes its record, applies the entry's record mode and reports overflow", () =>
    {
        var directory = Path.Combine(root, "patch");
        RecSession.UseContext(new TestContext());
        RecSession.Open(directory, "rec-patch", 1024 * 1024, 1024 * 1024, false, 64 * 1024, _ => { }, null, "session.start");
        var entry = new RecTraceEntry("p", "StandIns.FakeSecurityDoor", new[] { "Open" }, Array.Empty<string>(),
            "args+result", new[] { "m_state", "Label" }, false, 1, "count");
        var method = typeof(StandIns.FakeSecurityDoor).GetMethod("Open")!;
        var patch = new RecPatch(entry, method);
        var door = new StandIns.FakeSecurityDoor { m_state = 3 };
        patch.Record(door, new object?[] { 1, "two" }, "done", true);
        Probe.That(patch.Gate.Admit(0), "the patch gate refused its first call");
        Probe.That(!patch.Gate.Admit(1), "the patch gate admitted past maxPerSecond");
        patch.ReportOverflow(true);
        RecSession.Stop();

        var lines = ReadAllLines(directory);
        Probe.That(lines.Count == 2, "expected a call record and an overflow record, got " + lines.Count);
        using var call = JsonDocument.Parse(lines[0]);
        Probe.That(call.RootElement.GetProperty("kind").GetString() == "call", "the call record has the wrong kind");
        var body = call.RootElement.GetProperty("body");
        Probe.That(body.GetProperty("method").GetString() == "Open", "the method name is missing");
        Probe.That(body.GetProperty("args").GetArrayLength() == 2, "the arguments were not recorded");
        Probe.That(body.GetProperty("args")[0].GetInt32() == 1 && body.GetProperty("args")[1].GetString() == "two", "the argument values are wrong");
        Probe.That(body.GetProperty("result").GetString() == "done", "the result was not recorded");
        Probe.That(body.GetProperty("fields").GetProperty("m_state").GetInt32() == 3, "the instance field was not read");
        Probe.That(body.GetProperty("fields").GetProperty("Label").GetString() == "door", "the instance property was not read");
        using var overflow = JsonDocument.Parse(lines[1]);
        Probe.That(overflow.RootElement.GetProperty("kind").GetString() == "overflow", "the overflow summary has the wrong kind");
        Probe.That(overflow.RootElement.GetProperty("body").GetProperty("dropped").GetInt32() == 1, "the overflow count is wrong");
    });

    runner.Case("a changes patch keeps its key bounded and a count patch records neither args nor result", () =>
    {
        var entry = new RecTraceEntry("p", "T", new[] { "M" }, Array.Empty<string>(), "changes",
            new[] { "m_state" }, false, 100, "count");
        var method = typeof(StandIns.FakeSecurityDoor).GetMethod("Open")!;
        var patch = new RecPatch(entry, method);
        var door = new StandIns.FakeSecurityDoor { m_state = 1 };
        var first = patch.ChangeKey(door, new object?[] { 1 });
        Probe.That(first != null && first.Contains("1"), "the change key did not read the fields and args: " + first);
        door.m_state = 2;
        Probe.That(patch.ChangeKey(door, new object?[] { 1 }) != first, "a changed field produced the same key");

        var counting = new RecTraceEntry("p", "T", new[] { "M" }, Array.Empty<string>(), "count",
            Array.Empty<string>(), false, 100, "count");
        Probe.That(!counting.RecordsArgs && !counting.RecordsResult && !counting.RecordsChanges, "a count entry records more than a count");
    });

    runner.Case("the startup report names the profiles, the methods and every skip", () =>
    {
        var report = RecTracerRuntime.Report;
        using var document = JsonDocument.Parse(report);
        Probe.That(document.RootElement.GetProperty("matched").GetInt32() > 0, "the report claims nothing was matched");
        Probe.That(document.RootElement.GetProperty("skip").GetArrayLength() > 0, "the report carries no skips");
        var reasons = document.RootElement.GetProperty("skip").EnumerateArray().Select(s => s.GetProperty("reason").GetString()).ToHashSet();
        Probe.That(reasons.Contains("frame-loop") && reasons.Contains("property-getter") && reasons.Contains("generic"),
            "the report does not carry the skip reasons: " + string.Join(",", reasons));
        Probe.That(RecTraceReport.Counted(new[] { "a", "a", "b" }) == "a=2, b=1", "the counter text is wrong");
    });

    runner.Case("an unreadable directory is reported and the session never opens", () =>
    {
        RecSession.UseContext(new TestContext());
        var notices = new List<string>();
        var file = Path.Combine(root, "not-a-directory");
        File.WriteAllText(file, "x");
        Probe.That(!RecSession.Open(Path.Combine(file, "under"), "rec-bad", 1024, 1024, false, 4096, notices.Add, null, "session.start"),
            "the session opened inside a file");
        Probe.That(!RecSession.Active, "the failed session stayed active");
        Probe.That(notices.Count == 1, "the failure was not reported once");
    });

    runner.Case("a session cannot be opened twice and a second Open is answered false", () =>
    {
        RecSession.UseContext(new TestContext());
        var directory = Path.Combine(root, "twice");
        Probe.That(RecSession.Open(directory, "rec-twice", 1024 * 1024, 1024 * 1024, false, 64 * 1024, _ => { }, null, "session.start"),
            "the first Open failed");
        Probe.That(!RecSession.Open(Path.Combine(root, "twice-again"), "rec-other", 1024, 1024, false, 4096, _ => { }, null, "session.start"),
            "a second session was opened beside a live one");
        RecSession.Stop();
    });

    runner.Case("a bookmark and a screenshot use the context and say what happened", () =>
    {
        var directory = Path.Combine(root, "marks");
        var context = new TestContext { AimValue = true, ScreenshotResult = "shots/1.png" };
        RecSession.UseContext(context);
        RecSession.Open(directory, "rec-marks", 1024 * 1024, 1024 * 1024, false, 64 * 1024, _ => { }, null, "session.start");
        RecSession.Bookmark("door opened");
        Probe.That(RecSession.Screenshot("hotkey") == "shots/1.png", "the screenshot path was not returned");
        Probe.That(context.Screenshots == 1, "the context was not asked for the screenshot");
        RecSession.Stop();
        var lines = ReadAllLines(directory);
        using var mark = JsonDocument.Parse(lines[0]);
        Probe.That(mark.RootElement.GetProperty("body").GetProperty("label").GetString() == "door opened", "the bookmark label is wrong");
        Probe.That(mark.RootElement.GetProperty("body").GetProperty("aim").GetProperty("camera").GetString() == "test", "the bookmark lost the aim");
        using var shot = JsonDocument.Parse(lines[1]);
        Probe.That(shot.RootElement.GetProperty("body").GetProperty("path").GetString() == "shots/1.png", "the screenshot path is missing from the record");
    });

    runner.Case("a failing screenshot is a record with an error, not a false success", () =>
    {
        var directory = Path.Combine(root, "shot-fail");
        RecSession.UseContext(new TestContext { ScreenshotResult = "" });
        RecSession.Open(directory, "rec-shot-fail", 1024 * 1024, 1024 * 1024, false, 64 * 1024, _ => { }, null, "session.start");
        Probe.That(RecSession.Screenshot("hotkey").Length == 0, "a failed screenshot answered a path");
        RecSession.Stop();
        using var shot = JsonDocument.Parse(ReadAllLines(directory)[0]);
        Probe.That(shot.RootElement.GetProperty("body").TryGetProperty("error", out var error) && error.GetString()!.Length > 0,
            "the failed screenshot was not recorded as an error");
        Probe.That(!shot.RootElement.GetProperty("body").TryGetProperty("path", out _), "the failed screenshot recorded a path");
    });

    runner.Case("a body that throws becomes an error record and the session keeps writing", () =>
    {
        var directory = Path.Combine(root, "throwing");
        RecSession.UseContext(new TestContext());
        RecSession.Open(directory, "rec-throw", 1024 * 1024, 1024 * 1024, false, 64 * 1024, _ => { }, null, "session.start");
        RecSession.Write("tracer", "call", _ => throw new InvalidOperationException("body failed"));
        RecSession.Write("tracer", "call", json => json.WriteString("ok", "yes"));
        RecSession.Stop();
        var lines = ReadAllLines(directory);
        Probe.That(lines.Count == 2, "the session lost a record after a throwing body: " + lines.Count);
        using var error = JsonDocument.Parse(lines[0]);
        Probe.That(error.RootElement.GetProperty("body").GetProperty("$error").GetString()!.Contains("body failed"), "the body failure was not recorded");
        using var next = JsonDocument.Parse(lines[1]);
        Probe.That(next.RootElement.GetProperty("body").GetProperty("ok").GetString() == "yes", "the session stopped after a throwing body");
    });
}
finally
{
    RecSession.Stop();
    try { Directory.Delete(root, true); } catch (IOException) { }
}
Console.WriteLine($"Recorder core: {Probe.Checks} assertions passed; {Probe.Failures} scenarios failed.");
Console.WriteLine("Production RecSession/RecReflect/RecTracer with managed doubles; no game, IL2CPP or Unity.");
Environment.ExitCode = Probe.Failures == 0 ? 0 : 1;

static string Write(Action<Utf8JsonWriter> body)
{
    var buffer = new RecJsonBuffer(64 * 1024);
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = false }))
    {
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        body(writer);
        writer.WriteEndObject();
    }
    return Encoding.UTF8.GetString(buffer.WrittenSpan);
}

static List<string> ReadLines(string path)
    => File.Exists(path) ? File.ReadAllLines(path).Where(line => line.Length != 0).ToList() : new List<string>();

static List<string> ReadAllLines(string directory)
{
    var lines = new List<string>();
    foreach (var file in Directory.GetFiles(directory, "*.jsonl").OrderBy(x => x, StringComparer.Ordinal)) lines.AddRange(ReadLines(file));
    return lines;
}

static List<string> ReadGzipLines(string path)
{
    using var file = File.OpenRead(path);
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var reader = new StreamReader(gzip);
    var lines = new List<string>();
    while (reader.ReadLine() is { } line) if (line.Length != 0) lines.Add(line);
    return lines;
}

internal sealed class Runner
{
    internal void Case(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Probe.Failures++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
    }
}

internal static class Probe
{
    internal static int Checks, Failures;
    internal static void That(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Checks++;
    }
}

// Stand-in object graphs for the reader's cycle, depth, element and error cases.
internal sealed class Node
{
    internal Node(string name, Node? next) { m_name = name; Next = next; }
    private readonly string m_name;
    internal Node? Next { get; set; }
    internal string Label { get; set; } = "";
    internal List<int> Items { get; set; } = new();
}

internal sealed class Outer
{
    internal Node? Inner { get; set; }
}

internal sealed class Throwing
{
    internal string Fine => "fine";
    internal string Broken => throw new InvalidOperationException("this member refuses to read");
}
