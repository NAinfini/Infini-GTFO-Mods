using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>One trace configuration entry: which methods of one type to record, how much of each call to keep, and
/// which instance fields make two calls different. It is parsed from <c>probes/trace/*.json</c> and never holds a
/// MethodInfo, so it can be parsed and matched without a game assembly present.</summary>
internal sealed class RecTraceEntry
{
    internal RecTraceEntry(string profile, string type, IReadOnlyList<string> methods, IReadOnlyList<string> exclude,
        string record, IReadOnlyList<string> instanceFields, bool firstStack, int maxPerSecond, string onOverflow)
    {
        Profile = profile;
        Type = type;
        Methods = methods;
        Exclude = exclude;
        Record = record;
        InstanceFields = instanceFields;
        FirstStack = firstStack;
        MaxPerSecond = maxPerSecond;
        OnOverflow = onOverflow;
    }

    internal string Profile { get; }
    internal string Type { get; }
    internal IReadOnlyList<string> Methods { get; }
    internal IReadOnlyList<string> Exclude { get; }
    /// <summary><c>count</c>, <c>args</c>, <c>args+result</c> or <c>changes</c>.</summary>
    internal string Record { get; }
    internal IReadOnlyList<string> InstanceFields { get; }
    internal bool FirstStack { get; }
    internal int MaxPerSecond { get; }
    /// <summary><c>count</c> or <c>drop</c>: what a call beyond <see cref="MaxPerSecond"/> costs.</summary>
    internal string OnOverflow { get; }

    internal bool RecordsArgs => Record is "args" or "args+result" or "changes";
    internal bool RecordsResult => Record is "args+result" or "changes";
    internal bool RecordsChanges => Record == "changes";

    /// <summary>The name a record carries for this entry: the profile and the type, so a record can be attributed to
    /// the configuration line that asked for it.</summary>
    internal string Subject => Profile + ":" + Type;

    internal bool MatchesMethod(string method)
    {
        var included = Methods.Any(pattern => RecGlob.IsMatch(pattern, method));
        if (!included) return false;
        return !Exclude.Any(pattern => RecGlob.IsMatch(pattern, method));
    }
}

/// <summary>One profile as parsed from a trace file, with the outcome of its own parse kept so a rejected file is
/// reported rather than silently ignored.</summary>
internal sealed class RecTraceProfile
{
    internal RecTraceProfile(string name, bool enabledByDefault, string source, IReadOnlyList<RecTraceEntry> entries, string? error)
    {
        Name = name;
        EnabledByDefault = enabledByDefault;
        Source = source;
        Entries = entries;
        Error = error;
    }

    internal string Name { get; }
    internal bool EnabledByDefault { get; }
    internal string Source { get; }
    internal IReadOnlyList<RecTraceEntry> Entries { get; }
    internal string? Error { get; }
}

/// <summary>Why one configured method was not patched. The reason is a code so the startup report can be counted.</summary>
internal sealed record RecTraceSkip(string Profile, string Type, string Method, string Reason);

/// <summary>
/// The data-driven native tracer. Everything in this file is decided without Harmony and without a game assembly:
/// what a profile means, whether a method is refused, whether a call is over its rate and whether a `changes` entry
/// has anything new. <see cref="RecTracerRuntime"/> installs the patches this file decides on, which is what makes the
/// rules below testable in a plain process.
/// </summary>
internal static class RecTracer
{
    internal const string Section = "ForgeDevelopment";
    internal const string ConfigFolder = "trace";
    internal const int DefaultMaxPerSecond = 200;
    internal const int OverflowSummarySeconds = 5;

    /// <summary>Methods this tracer refuses whatever a profile says, and the reason it reports for each. The list is
    /// the per-frame and per-render hot path: recording them would measure the recorder, not the game. A profile that
    /// names one of these explicitly gets an override, because that is a deliberate authoring choice.</summary>
    private static readonly Dictionary<string, string> Rejected = new(StringComparer.Ordinal)
    {
        ["Update"] = "frame-loop",
        ["LateUpdate"] = "frame-loop",
        ["FixedUpdate"] = "frame-loop",
        ["OnGUI"] = "immediate-mode-ui",
        ["OnPreRender"] = "render-loop",
        ["OnPostRender"] = "render-loop",
        ["OnRenderObject"] = "render-loop",
        ["OnDrawGizmos"] = "editor-gizmo",
        ["OnDrawGizmosSelected"] = "editor-gizmo"
    };

    /// <summary>A property getter is refused unless the profile asks for it by name: they are called from everywhere,
    /// and a recorder inside a getter is how a game acquires a new stall.</summary>
    private const string GetterReason = "property-getter";

    /// <summary>Patterns that must be spelled out to be accepted. They are matched against the method name.</summary>
    internal static bool IsExplicitOverride(IReadOnlyList<string> patterns, string method)
        => patterns.Any(pattern => string.Equals(pattern, method, StringComparison.Ordinal));

    internal static string? RejectionReason(RecTraceEntry entry, string method, bool hasGetter)
    {
        if (Rejected.TryGetValue(method, out var reason) && !IsExplicitOverride(entry.Methods, method)) return reason;
        if (hasGetter && !IsExplicitOverride(entry.Methods, method)) return GetterReason;
        return null;
    }

    /// <summary>Parses every trace file. A file that is not valid JSON, or an entry missing its type, produces a
    /// profile with an error and no entries; it is never silently dropped.</summary>
    internal static List<RecTraceProfile> Parse(IEnumerable<string> files)
    {
        var profiles = new List<RecTraceProfile>();
        foreach (var file in files)
        {
            var source = System.IO.Path.GetFileName(file);
            try
            {
                using var document = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                var root = document.RootElement;
                var name = root.TryGetProperty("profile", out var profileName) && profileName.ValueKind == JsonValueKind.String
                    ? profileName.GetString()! : System.IO.Path.GetFileNameWithoutExtension(file);
                var enabled = !root.TryGetProperty("enabledByDefault", out var enabledValue) || enabledValue.ValueKind != JsonValueKind.False;
                var entries = new List<RecTraceEntry>();
                string? error = null;
                if (root.TryGetProperty("entries", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        var type = Text(row, "type");
                        if (string.IsNullOrEmpty(type))
                        {
                            // One unusable entry costs the whole file: a profile that silently drops a line would
                            // report coverage it does not have.
                            error = source + ": an entry has no type";
                            entries.Clear();
                            break;
                        }
                        entries.Add(new RecTraceEntry(name, type!, Strings(row, "methods", new[] { "*" }), Strings(row, "exclude", Array.Empty<string>()),
                            Record(row), Strings(row, "instance", Array.Empty<string>()), Flag(row, "firstStack"),
                            Integer(row, "maxPerSecond", DefaultMaxPerSecond), Text(row, "onOverflow") ?? "count"));
                    }
                }
                profiles.Add(new RecTraceProfile(name, enabled, source, entries, error));
            }
            catch (Exception error)
            {
                profiles.Add(new RecTraceProfile(System.IO.Path.GetFileNameWithoutExtension(file), false, source, Array.Empty<RecTraceEntry>(),
                    source + ": " + error.GetType().Name + ": " + error.Message));
            }
        }
        return profiles;
    }

    /// <summary>The per-second gate every patched method owns. A call beyond the rate is counted for the summary and
    /// then either dropped or recorded as a count, which is the entry's own `onOverflow`.</summary>
    internal sealed class RateGate
    {
        private long _second = -1;
        private int _count;
        private int _dropped;

        internal bool Admit(long nowMilliseconds)
        {
            var second = nowMilliseconds / 1000;
            if (second != _second) { _second = second; _count = 0; }
            if (_count < MaxPerSecond) { _count++; return true; }
            _dropped++;
            return false;
        }

        internal int MaxPerSecond { get; set; } = DefaultMaxPerSecond;

        /// <summary>Takes the overflow count, so exactly one summary reports it.</summary>
        internal int TakeDropped()
        {
            var dropped = _dropped;
            _dropped = 0;
            return dropped;
        }
    }

    /// <summary>The `changes` memory of one entry: the record it wrote last, as text. A call that produces the same
    /// text is not recorded at all, which is what makes a `changes` entry cheap on a method that runs every frame.</summary>
    internal sealed class ChangeTracker
    {
        private string? _last;

        internal bool Changed(string current)
        {
            if (string.Equals(_last, current, StringComparison.Ordinal)) return false;
            _last = current;
            return true;
        }

        internal void Reset() => _last = null;
    }

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Integer(JsonElement row, string name, int fallback)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    private static string Record(JsonElement row)
    {
        var record = Text(row, "record") ?? "count";
        return record is "count" or "args" or "args+result" or "changes" ? record : "count";
    }

    private static string[] Strings(JsonElement row, string name, string[] fallback)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : fallback;
}

/// <summary>Glob matching for type and method patterns: <c>*</c> for any run, <c>?</c> for one character. It is
/// iterative with backtracking rather than recursive, so a pathological pattern costs time, not stack.</summary>
internal static class RecGlob
{
    internal static bool IsMatch(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern)) return text.Length == 0;
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; continue; }
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; continue; }
            if (star < 0) return false;
            p = star + 1;
            t = ++mark;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>Anchored type matching: a profile may name the bare type, the namespace-qualified name, or a
    /// leading/trailing wildcard over either.</summary>
    internal static bool MatchesType(string pattern, string fullName, string shortName)
        => IsMatch(pattern, fullName) || IsMatch(pattern, shortName);
}

/// <summary>Formats the startup report the tracer writes into the `session` channel and into index.json.</summary>
internal static class RecTraceReport
{
    internal static string ToJson(IEnumerable<RecTraceProfile> profiles, IReadOnlyList<RecTraceSkip> skips, IReadOnlyList<string> installed, IReadOnlyList<string> errors,
        IReadOnlyList<RecPatch>? matched = null)
    {
        using var stream = new System.IO.MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartArray("profiles");
            foreach (var profile in profiles)
            {
                json.WriteStartObject();
                json.WriteString("profile", profile.Name);
                json.WriteString("source", profile.Source);
                json.WriteBoolean("enabledByDefault", profile.EnabledByDefault);
                json.WriteNumber("entries", profile.Entries.Count);
                if (profile.Error != null) json.WriteString("error", profile.Error);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            // `matched` counts every method a profile selected, installed or not: a process without HarmonyX still
            // made the decision, and the report has to say which decision it made.
            var methods = matched ?? Array.Empty<RecPatch>();
            json.WriteNumber("matched", methods.Count);
            json.WriteNumber("installed", installed.Count);
            json.WriteNumber("skipped", skips.Count);
            json.WriteStartArray("methods");
            foreach (var method in methods.Select(m => m.Subject).OrderBy(x => x, StringComparer.Ordinal)) json.WriteStringValue(method);
            json.WriteEndArray();
            json.WriteStartArray("skip");
            foreach (var skip in skips.OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Method, StringComparer.Ordinal))
            {
                json.WriteStartObject();
                json.WriteString("profile", skip.Profile);
                json.WriteString("type", skip.Type);
                json.WriteString("method", skip.Method);
                json.WriteString("reason", skip.Reason);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("errors");
            foreach (var error in errors) json.WriteStringValue(error);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string Counted(IEnumerable<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values) counts[value] = counts.TryGetValue(value, out var count) ? count + 1 : 1;
        return string.Join(", ", counts.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value.ToString(CultureInfo.InvariantCulture)));
    }
}
