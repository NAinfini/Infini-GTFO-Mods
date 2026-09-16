using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;

namespace ForgeDevelopment.Native;

/// <summary>One patch the tracer installed, with everything the prefix and postfix need to decide what to write.
/// The counters are the ones a `maxPerSecond` gate and an overflow summary report.</summary>
internal sealed class RecPatch
{
    private static long _nextId;

    internal RecPatch(RecTraceEntry entry, MethodBase method)
    {
        Entry = entry;
        Method = method;
        Id = ++_nextId;
        Gate = new RecTracer.RateGate { MaxPerSecond = entry.MaxPerSecond };
        Changes = new RecTracer.ChangeTracker();
        Key = new RecJsonBuffer(RecSession.DefaultRecordBudget);
    }

    internal long Id { get; }
    internal RecTraceEntry Entry { get; }
    internal MethodBase Method { get; }
    /// <summary>True once Harmony has this method patched. The tracer owns the flag because a removed patch keeps its
    /// record and must never be installed a second time.</summary>
    internal bool Installed { get; set; }
    internal RecTracer.RateGate Gate { get; }
    internal RecTracer.ChangeTracker Changes { get; }
    internal RecJsonBuffer Key { get; }
    internal string Subject => Entry.Subject + "." + Method.Name;
    internal long Calls { get; set; }
    internal long Recorded { get; set; }
    internal long Failures { get; set; }
    internal long LastOverflowReport { get; set; }
    internal string? Removed { get; private set; }

    /// <summary>The patch is uninstalled the first time it throws. A configuration line that breaks the game is a
    /// bug in the configuration, and the tracer's contract is that it costs one record, not the session.</summary>
    internal void Fail(Exception error)
    {
        Failures++;
        if (Removed != null) return;
        Removed = error.GetType().Name + ": " + RecReflect.Truncate(error.Message, 200);
        try
        {
            RecTracerRuntime.Uninstall(this);
        }
        catch (Exception uninstall)
        {
            Removed += " (uninstall failed: " + uninstall.GetType().Name + ")";
        }
    }

    /// <summary>Writes one call. The instance fields, the arguments and the result each go through RecReflect, so
    /// depth, element and budget caps apply to a trace record exactly as they do to a snapshot.</summary>
    internal void Record(object? instance, object?[]? arguments, object? result, bool hasResult)
    {
        Calls++;
        var adepth = 0;
        RecSession.Write("tracer", Entry.RecordsChanges ? "change" : "call", json =>
        {
            json.WriteString("profile", Entry.Profile);
            json.WriteString("type", Entry.Type);
            json.WriteString("method", Method.Name);
            json.WriteNumber("patch", Id);
            json.WriteString("declaring", Method.DeclaringType?.FullName ?? "unknown");
            if (instance != null)
            {
                json.WriteString("instance", RecReflect.Describe(instance));
                if (Entry.InstanceFields.Count != 0)
                {
                    json.WritePropertyName("fields");
                    json.WriteStartObject();
                    foreach (var (path, value) in RecReflect.ReadPaths(instance, Entry.InstanceFields))
                    {
                        json.WritePropertyName(path);
                        RecReflect.WriteValue(json, value, adepth);
                    }
                    json.WriteEndObject();
                }
            }
            if (Entry.RecordsArgs && arguments is { Length: > 0 })
            {
                json.WritePropertyName("args");
                json.WriteStartArray();
                foreach (var argument in arguments) RecReflect.WriteValue(json, argument, adepth);
                json.WriteEndArray();
            }
            if (Entry.RecordsResult && hasResult)
            {
                json.WritePropertyName("result");
                RecReflect.WriteValue(json, result, adepth);
            }
            json.WriteNumber("call", Calls);
        });
        Recorded++;
    }

    /// <summary>Builds the `changes` key: the same text a record would carry for the fields and arguments that define
    /// the change. The key is built into this patch's own bounded buffer, so a `changes` entry on a hot method still
    /// costs a fixed amount of memory.</summary>
    internal string? ChangeKey(object? instance, object?[]? arguments)
    {
        try
        {
            Key.Reset();
            using var json = new Utf8JsonWriter(Key, new JsonWriterOptions { SkipValidation = true });
            json.WriteStartArray();
            if (instance != null)
            {
                foreach (var (_, value) in RecReflect.ReadPaths(instance, Entry.InstanceFields)) RecReflect.WriteValue(json, value, 0);
            }
            if (arguments != null) foreach (var argument in arguments) RecReflect.WriteValue(json, argument, 0);
            json.WriteEndArray();
            json.Flush();
            return System.Text.Encoding.UTF8.GetString(Key.WrittenSpan);
        }
        catch (RecValueTooLargeException) { return "(over-budget)"; }
        catch (Exception error) { return "(" + error.GetType().Name + ")"; }
    }

    /// <summary>The overflow summary: a call the rate refused is counted and the count is written on its own record,
    /// so a trace of a hot method says what it did not keep.</summary>
    internal void ReportOverflow(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - LastOverflowReport < RecTracer.OverflowSummarySeconds * 1000L) return;
        var dropped = Gate.TakeDropped();
        if (dropped == 0) return;
        LastOverflowReport = now;
        RecSession.Write("tracer", "overflow", json =>
        {
            json.WriteString("profile", Entry.Profile);
            json.WriteString("type", Entry.Type);
            json.WriteString("method", Method.Name);
            json.WriteNumber("patch", Id);
            json.WriteNumber("dropped", dropped);
            json.WriteNumber("maxPerSecond", Entry.MaxPerSecond);
            json.WriteString("onOverflow", Entry.OnOverflow);
            json.WriteNumber("calls", Calls);
        });
    }
}

/// <summary>
/// The Harmony side of the tracer: it finds the methods a profile names in the interop assemblies, installs one
/// generic prefix/postfix pair on each, and removes a patch that throws. Everything it decides with is in
/// <see cref="RecTracer"/>; everything here is the game's own reflection and HarmonyX.
/// </summary>
internal static class RecTracerRuntime
{
    /// <summary>The Harmony owner id every trace patch is installed under. It is here rather than read from the
    /// plugin so this file compiles and runs without the plugin beside it.</summary>
    internal const string RecorderGuid = "NAinfini.ForgeDevelopment.Recorder";

    private static readonly object Gate = new();
    private static readonly Dictionary<MethodBase, RecPatch> Patches = new();
    private static readonly Dictionary<string, bool> ProfileEnabled = new(StringComparer.Ordinal);
    private static readonly List<RecTraceSkip> Skips = new();
    private static readonly List<string> Installed = new();
    private static readonly List<string> Errors = new();
    private static readonly Dictionary<string, List<RecPatch>> Candidates = new(StringComparer.Ordinal);

    private static Harmony? _harmony;
    private static string _report = "{\"profiles\":[],\"matched\":0,\"skipped\":0,\"methods\":[],\"skip\":[],\"errors\":[]}";

    internal static IReadOnlyList<RecTraceSkip> Skipped => Skips;
    internal static IReadOnlyList<string> InstalledMethods => Installed;
    internal static string Report => _report;
    internal static bool Active => _harmony != null && Patches.Count != 0;

    /// <summary>The report the session's index.json carries, so a session says which patches it ran with.</summary>
    internal static string InstalledJson() => _report;

    /// <summary>Parses the configured profiles, matches them against the loaded assemblies and installs the patches.
    /// A profile that is disabled by default is still matched and reported, because a later
    /// <see cref="SetProfileEnabled"/> may turn it on without a restart.</summary>
    internal static void LoadProfiles(IEnumerable<string> files) => LoadProfiles(files, null);

    /// <summary>The same parse over an explicit type list. The game passes null and the loaded assemblies are
    /// searched; a test passes the stand-in types it wants matched, so the whole decision path — include, exclude,
    /// reject, match — runs without a game assembly present.</summary>
    internal static void LoadProfiles(IEnumerable<string> files, IReadOnlyList<Type>? types)
    {
        ArgumentNullException.ThrowIfNull(files);
        var profiles = RecTracer.Parse(files);
        lock (Gate)
        {
            foreach (var profile in profiles)
            {
                ProfileEnabled[profile.Name] = profile.EnabledByDefault;
                if (profile.Error != null) Errors.Add(profile.Error);
            }
        }
        EnsureHarmony();
        foreach (var profile in profiles)
        {
            if (profile.Error != null) continue;
            foreach (var entry in profile.Entries) Match(entry, types ?? FindTypes(entry.Type));
        }
        Publish(profiles);
    }

    /// <summary>The matching step with the type list handed in: the decision the tracer makes about one entry over
    /// one candidate type, which is the part a test can drive without a game assembly present.</summary>
    internal static void Match(RecTraceEntry entry, IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(types);
        foreach (var type in types)
        {
            if (type.ContainsGenericParameters || type.IsInterface) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!entry.MatchesMethod(method.Name)) continue;
                var reason = RejectionReason(entry, method);
                if (reason != null)
                {
                    lock (Gate) Skips.Add(new RecTraceSkip(entry.Profile, type.FullName ?? type.Name, method.Name, reason));
                    continue;
                }
                lock (Gate)
                {
                    // A second profile that names the same method keeps the first patch: two patches on one method
                    // would record the same call twice and rate-limit each other.
                    if (Patches.ContainsKey(method)) continue;
                    var patch = new RecPatch(entry, method);
                    Patches.Add(method, patch);
                    if (!Candidates.TryGetValue(entry.Profile, out var list)) Candidates.Add(entry.Profile, list = new List<RecPatch>());
                    list.Add(patch);
                }
            }
        }
        InstallProfile(entry.Profile, entry);
        Publish(null);
    }

    /// <summary>Every method the tracer matched for a profile, installed or not. The session's startup report and a
    /// test read the same list.</summary>
    internal static IReadOnlyList<RecPatch> Matched(string profile)
    {
        lock (Gate) return Candidates.TryGetValue(profile, out var list) ? list.ToArray() : Array.Empty<RecPatch>();
    }

    /// <summary>Turns one profile on or off at run time. Turning it on installs the patches it matched but never
    /// installed; turning it off uninstalls exactly that profile's patches.</summary>
    internal static void SetProfileEnabled(string profile, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (Gate) { ProfileEnabled[profile] = enabled; }
        if (enabled) InstallProfile(profile, null);
        else foreach (var patch in Patches.Values.Where(p => p.Entry.Profile == profile).ToArray()) Uninstall(patch);
        RecSession.Write("session", "profile", json =>
        {
            json.WriteString("profile", profile);
            json.WriteBoolean("enabled", enabled);
            json.WriteNumber("patches", Patches.Count(p => p.Value.Entry.Profile == profile));
        });
    }

    internal static bool IsProfileEnabled(string profile)
    {
        lock (Gate) return ProfileEnabled.TryGetValue(profile, out var enabled) && enabled;
    }

    /// <summary>Removes a patch. Called by the patch itself when it throws, and by <see cref="SetProfileEnabled"/>.</summary>
    internal static void Uninstall(RecPatch patch)
    {
        lock (Gate)
        {
            if (!Patches.Remove(patch.Method)) return;
        }
        try { _harmony?.Unpatch(patch.Method, HarmonyPatchType.All, _harmony.Id); }
        catch (Exception error) { lock (Gate) Errors.Add("unpatch " + patch.Subject + ": " + error.Message); }
        RecSession.Write("session", "patch_removed", json =>
        {
            json.WriteString("profile", patch.Entry.Profile);
            json.WriteString("type", patch.Entry.Type);
            json.WriteString("method", patch.Method.Name);
            json.WriteString("reason", patch.Removed ?? "disabled");
            json.WriteNumber("failures", patch.Failures);
        });
        RecDiag.Warn("Forge recorder removed trace patch " + patch.Subject + ": " + patch.Removed);
        Publish(null);
    }

    /// <summary>Releases every patch. The plugin's failure path calls this, and a process that is leaving does not
    /// need the patches removed; a failed startup does.</summary>
    internal static void UninstallAll()
    {
        foreach (var patch in Patches.Values.ToArray()) Uninstall(patch);
        lock (Gate) { Installed.Clear(); }
    }

    /// <summary>The patch a Harmony prefix or postfix belongs to. A method that is no longer patched answers null, so
    /// a call already inside the patched body when the patch is removed writes nothing.</summary>
    internal static RecPatch? Get(MethodBase method)
    {
        lock (Gate) return Patches.TryGetValue(method, out var patch) ? patch : null;
    }

    private static void Match(RecTraceEntry entry)
    {
        Match(entry, FindTypes(entry.Type));
    }

    /// <summary>The Harmony instance the patches are installed through, created on first use. It is resolved here
    /// rather than constructed in a field so a process without HarmonyX — a test — still reaches every decision this
    /// file makes and reports the reason it could not install.</summary>
    private static void EnsureHarmony()
    {
        if (_harmony != null) return;
        try { _harmony = new Harmony(RecorderGuid + ".RecTracer"); }
        catch (Exception error)
        {
            lock (Gate) _harmonyFailure = error.GetType().Name + ": " + error.Message;
        }
    }

    private static string? _harmonyFailure;

    /// <summary>Why nothing could be installed, when Harmony itself was missing.</summary>
    internal static string? HarmonyFailure { get { lock (Gate) return _harmonyFailure; } }

    private static void InstallProfile(string profile, RecTraceEntry? only)
    {
        var harmony = _harmony;
        if (harmony == null) return;
        var candidates = Patches.Values
            .Where(p => p.Entry.Profile == profile && (only == null || ReferenceEquals(p.Entry, only)) && !p.Method.IsPatched())
            .ToArray();
        foreach (var patch in candidates)
        {
            try
            {
                var processor = harmony.CreateProcessor(patch.Method);
                processor.AddPrefix(new HarmonyMethod(RecTracerPatches.PrefixMethod) { priority = Priority.First });
                processor.AddPostfix(new HarmonyMethod(RecTracerPatches.PostfixMethod) { priority = Priority.Last });
                processor.Patch();
                patch.Installed = true;
                lock (Gate) Installed.Add(patch.Subject);
            }
            catch (Exception error)
            {
                lock (Gate)
                {
                    Errors.Add("patch " + patch.Subject + ": " + error.GetType().Name + ": " + error.Message);
                    Skips.Add(new RecTraceSkip(patch.Entry.Profile, patch.Entry.Type, patch.Method.Name, "patch-failed"));
                    Patches.Remove(patch.Method);
                }
            }
        }
    }

    /// <summary>Methods the tracer refuses whatever a profile says, and the reason it reports. Generic methods and
    /// methods with a pointer, by-ref or open generic parameter are refused by signature: Harmony cannot bind them
    /// generically, and a tracer that half-installs one is worse than one that says it did not.</summary>
    internal static string? RejectionReason(RecTraceEntry entry, MethodInfo method)
    {
        if (method.IsGenericMethodDefinition || method.ContainsGenericParameters) return "generic";
        if (method.GetParameters().Any(p => p.ParameterType.IsPointer || p.ParameterType.IsByRef
            || p.ParameterType.IsGenericParameter || p.ParameterType.ContainsGenericParameters)) return "unsupported-parameter";
        // A property accessor is refused by its name: the interop assemblies project a property as a `get_` method
        // whose `IsSpecialName` is not set, so the name is the only thing that identifies one.
        var getter = method.Name.StartsWith("get_", StringComparison.Ordinal);
        return RecTracer.RejectionReason(entry, method.Name, getter);
    }

    private static IEnumerable<Type> FindTypes(string pattern)
    {
        var shortName = pattern;
        var dot = pattern.LastIndexOf('.');
        if (dot >= 0) shortName = pattern[(dot + 1)..];
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name ?? "";
            if (name is "ForgeDevelopment.Native" or "ForgeRuntime" or "ForgeRuntime.Framework" or "0Harmony" or "BepInEx.Core"
                || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name is "mscorlib" or "netstandard" or "Il2Cppmscorlib" or "Il2CppInterop.Runtime") continue;
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (Exception) { continue; }
            foreach (var type in types)
            {
                if (type.IsGenericTypeDefinition || type.IsInterface) continue;
                if (RecGlob.MatchesType(pattern, type.FullName ?? type.Name, type.Name) || RecGlob.MatchesType(pattern, type.FullName ?? type.Name, shortName)) yield return type;
            }
        }
    }

    private static bool IsPatched(this MethodBase method)
    {
        lock (Gate) return Patches.TryGetValue(method, out var patch) && patch.Installed;
    }

    private static void Publish(IEnumerable<RecTraceProfile>? profiles)
    {
        lock (Gate)
        {
            // The report is cumulative: a profile matched later never erases the one matched before it, so the
            // session's startup facts and index.json describe every configuration file that was read.
            if (profiles != null)
                foreach (var profile in profiles) Known[profile.Name] = profile;
            _report = RecTraceReport.ToJson(Known.Values, Skips, Installed, Errors, Candidates.Values.SelectMany(x => x).ToArray());
        }
    }

    private static readonly Dictionary<string, RecTraceProfile> Known = new(StringComparer.Ordinal);
}

/// <summary>
/// The one line the recorder core needs from the plugin's log. It is resolved by reflection because this file is
/// compiled on its own by the recorder's test project, where no BepInEx log source exists: the core's decisions are
/// testable, and the process that owns a log is the only one that pays for it.
/// </summary>
internal static class RecDiag
{
    private static bool _resolved;
    private static System.Reflection.MethodInfo? _warning;
    private static object? _source;

    internal static void Warn(string message) => Write("LogWarning", message);
    internal static void Error(string message) => Write("LogError", message);

    private static void Write(string method, string message)
    {
        try
        {
            if (!_resolved)
            {
                _resolved = true;
                var plugin = Type.GetType("ForgeDevelopment.Native.Plugin, ForgeDevelopment.Native", false);
                var log = plugin?.GetProperty("PluginLog", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
                if (log != null)
                {
                    _source = log;
                    _warning = log.GetType().GetMethod(method, new[] { typeof(object) });
                }
            }
            _warning?.Invoke(_source, new object[] { message });
        }
        catch (Exception)
        {
            // A recorder that cannot write its own log line has nothing left to report it with, and must not throw.
        }
    }
}
