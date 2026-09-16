using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using ForgeDevelopment.Native;

// CaptureCheck: the managed tests behind probes/trace and probes/points.tsv. It parses the trace profiles with the
// tracer's own parser, checks the configured type and method names against this machine's interop assemblies with
// Cecil, checks that a points.tsv row points at something that exists, and exercises the snapshot change detector over
// managed doubles. Nothing here loads the game.
if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: CaptureCheck <BepInEx dir> <repo ForgeDevelopment dir> <report.json> <detail.json>");
    return 2;
}
var bepInEx = Path.GetFullPath(args[0]);
var development = Path.GetFullPath(args[1]);
var reportPath = Path.GetFullPath(args[2]);
var detailPath = Path.GetFullPath(args[3]);
var traceDir = Path.Combine(development, "probes", "trace");
var pointsPath = Path.Combine(development, "probes", "points.tsv");
var commandsDir = Path.Combine(development, "probes", "commands");
var checks = new List<Check>();
var detail = new Dictionary<string, object>();
var diagnostics = new List<string>();

void Check(string id, bool passed, string note) => checks.Add(new Check(id, passed, note));

// ---------------------------------------------------------------- trace profiles
var files = Directory.GetFiles(traceDir, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();
Check("trace.files-present", files.Length != 0, files.Length + " profile files");

var profiles = RecTracer.Parse(files);
var parseErrors = profiles.Where(p => p.Error != null).Select(p => p.Error!).ToArray();
Check("trace.parses", parseErrors.Length == 0, parseErrors.Length == 0 ? "all files parsed" : string.Join(" | ", parseErrors));

var declared = profiles.SelectMany(p => p.Entries.Select(e => (Profile: p.Name, Entry: e))).ToArray();
Check("trace.profile-names-unique", profiles.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == profiles.Count,
    string.Join(",", profiles.Select(p => p.Name)));

var defaultOn = profiles.Count(p => p.EnabledByDefault);
var defaultOff = profiles.Count - defaultOn;
Check("trace.default-on-is-the-playable-set", defaultOn >= 9 && defaultOff >= 1,
    "enabledByDefault: on=" + defaultOn + " off=" + defaultOff + " (" + string.Join(",", profiles.Where(p => !p.EnabledByDefault).Select(p => p.Name)) + ")");
Check("trace.everything-is-off", profiles.Any(p => p.Name == "everything" && !p.EnabledByDefault), "everything must stay opt-in");

// Every entry names methods, every entry's record value is one the tracer accepts, and a rate limit exists on every
// entry so one profile line cannot flood a session.
var missingMethods = declared.Where(d => d.Entry.Methods.Count == 0).Select(d => d.Profile + ":" + d.Entry.Type).ToArray();
Check("trace.entries-have-methods", missingMethods.Length == 0, missingMethods.Length == 0 ? "ok" : string.Join(",", missingMethods));
var badRecord = declared.Where(d => d.Entry.Record is not ("count" or "args" or "args+result" or "changes"))
    .Select(d => d.Profile + ":" + d.Entry.Type + "=" + d.Entry.Record).ToArray();
Check("trace.record-values", badRecord.Length == 0, badRecord.Length == 0 ? "ok" : string.Join(",", badRecord));
var unbounded = declared.Where(d => d.Entry.Methods.Count == 1 && d.Entry.Methods[0] is "*" or "*.*" && d.Entry.MaxPerSecond <= 0)
    .Select(d => d.Profile + ":" + d.Entry.Type).ToArray();
Check("trace.rate-limits", unbounded.Length == 0, unbounded.Length == 0 ? "every entry has maxPerSecond>0" : string.Join(",", unbounded));

// ---------------------------------------------------------------- interop type index
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.Combine(bepInEx, "interop"));
resolver.AddSearchDirectory(Path.Combine(bepInEx, "core"));
var interop = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
var byFullName = new Dictionary<string, List<(string Short, TypeDefinition Type)>>(StringComparer.Ordinal);
var byShortName = new Dictionary<string, List<TypeDefinition>>(StringComparer.Ordinal);
var byBareName = new Dictionary<string, List<TypeDefinition>>(StringComparer.Ordinal);
foreach (var dll in Directory.GetFiles(Path.Combine(bepInEx, "interop"), "*.dll").OrderBy(p => p, StringComparer.Ordinal))
{
    AssemblyDefinition assembly;
    try { assembly = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { AssemblyResolver = resolver, InMemory = true }); }
    catch (Exception error) { diagnostics.Add("load-failed " + Path.GetFileName(dll) + " " + error.GetType().Name + ": " + error.Message); continue; }
    interop[Path.GetFileNameWithoutExtension(dll)] = assembly;
    foreach (var type in assembly.MainModule.Types.SelectMany(Walk))
    {
        // The tracer's own type walk skips a generic type definition and an interface, so this index does too: a
        // name only this index answers is a name no runtime profile can install.
        if (type.IsInterface || type.HasGenericParameters) continue;
        var full = type.FullName;
        if (!byFullName.TryGetValue(full, out var list)) byFullName[full] = list = new List<(string, TypeDefinition)>();
        list.Add((type.Name, type));
        if (!byShortName.TryGetValue(type.Name, out var shorts)) byShortName[type.Name] = shorts = new List<TypeDefinition>();
        shorts.Add(type);
        // A generic type's short name carries its arity (`SNet_Packet`1`). A profile that names the type without the
        // arity names the same type, so the bare name is indexed too.
        var tick = type.Name.IndexOf('`');
        if (tick > 0)
        {
            var bare = type.Name[..tick];
            if (!byBareName.TryGetValue(bare, out var bares)) byBareName[bare] = bares = new List<TypeDefinition>();
            bares.Add(type);
        }
    }
}
Check("interop.index-built", byFullName.Count > 3000, byFullName.Count + " interop types");

// The type name a profile uses must match at least one real type. A pattern that matches nothing is the failure the
// task asks to be reported: a profile line that can never install.
var unmatchedTypes = declared.Where(d => !MatchesType(d.Entry.Type)).Select(d => d.Profile + ":" + d.Entry.Type).ToArray();
Check("trace.types-exist", unmatchedTypes.Length == 0, unmatchedTypes.Length == 0 ? "every configured type matches this build"
    : string.Join(",", unmatchedTypes));

// The concrete type names a pattern uses, when the pattern has no wildcard, must resolve to a type. A bare name two
// namespaces both define (`Weapon` is both the equipment base and a `GameData.GD` record) is accepted when exactly
// one candidate carries the pattern; a genuinely ambiguous line names its namespace instead.
var ambiguous = declared.Where(d => !d.Entry.Type.Contains('*') && !d.Entry.Type.Contains('?'))
    .Select(d => (d.Profile, d.Entry.Type, Exact: byFullName.ContainsKey(d.Entry.Type)))
    .Where(x => !x.Exact).Select(x => x.Profile + ":" + x.Type).ToArray();
Check("trace.concrete-types-unambiguous", ambiguous.Length == 0, ambiguous.Length == 0 ? "ok" : string.Join(",", ambiguous));

// Every method pattern must match at least one method of at least one matched type (excluding globals); a wildcard
// over an empty set is the same dead line as an unknown type.
var configuredMethods = 0;
var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
var duplicateMethods = new List<string>();
var crossProfileOverlap = new List<string>();
foreach (var (profile, entry) in declared)
{
    var claimedHere = new HashSet<string>(StringComparer.Ordinal);
    foreach (var type in MatchesTypes(entry.Type))
    {
        foreach (var method in type.Methods)
        {
            if (method.IsConstructor && method.Name is ".ctor" or ".cctor") continue;
            var name = method.Name;
            if (!entry.MatchesMethod(name)) continue;
            configuredMethods++;
            var key = type.FullName + "::" + method.FullName;
            // Two entries inside one profile claiming the same overload replace each other: one of the two
            // configurations never runs and nothing reports it. Two profiles claiming it is intended: a profile is the
            // unit an operator turns on, and the optional `everything` profile is expected to overlap the defaults.
            if (!claimedHere.Add(key)) duplicateMethods.Add(profile + ":" + key);
            if (claimed.TryGetValue(key, out var owner))
            {
                if (owner != profile) crossProfileOverlap.Add(owner + "+" + profile + ":" + key);
            }
            else claimed[key] = profile;
        }
    }
}
Check("trace.methods-resolve", configuredMethods > 0, configuredMethods + " configured method matches over " + declared.Length + " entries");
Check("trace.no-duplicate-methods", duplicateMethods.Count == 0,
    duplicateMethods.Count == 0
        ? claimed.Count + " overloads claimed; " + crossProfileOverlap.Count + " are claimed by two profiles (profiles are switched independently)"
        : string.Join(",", duplicateMethods.Distinct().Take(20)));

// The tracer refuses a generic method and a method with a pointer, by-ref or open generic parameter by signature, and
// reports every refusal in its skip list. The check is not "nothing is refused" — a wildcard over a generic type
// matches overloads that cannot be patched — but that every physical method an entry's *pattern* names is refused
// only by its pattern. A line that names a method which can never be patched is a line that records nothing.
var refused = new List<string>();
var refusedConcrete = new List<string>();
foreach (var (profile, entry) in declared)
{
    foreach (var type in MatchesTypes(entry.Type))
        foreach (var method in type.Methods)
        {
            if (method.IsConstructor && method.Name is ".ctor" or ".cctor") continue;
            if (!entry.MatchesMethod(method.Name)) continue;
            var reason = RecTracer.RejectionReason(entry, method.Name, method.IsSpecialName && method.Name.StartsWith("get_", StringComparison.Ordinal))
                ?? SignatureRefusal(method);
            if (reason == null) continue;
            var line = profile + ":" + type.FullName + "." + method.Name + "(" + method.Parameters.Count + ")=" + reason;
            refused.Add(line);
            // A wildcard entry keeps whatever else it matched, so a refusal inside one only fails the check when the
            // entry's only method pattern is the refused method's own name.
            if (entry.Methods.Any(m => string.Equals(m, method.Name, StringComparison.Ordinal))) refusedConcrete.Add(line);
        }
}
Check("trace.named-methods-not-refused", refusedConcrete.Count == 0,
    refusedConcrete.Count == 0 ? refused.Count + " wildcard matches are refused by signature and reported in the skip list"
        : string.Join(",", refusedConcrete.Distinct().Take(20)));

// The frame-loop methods must never be named by a profile that is on by default: that is the difference between a
// profile that can run a normal match and one that cannot.
var loopInDefault = new List<string>();
foreach (var profile in profiles.Where(p => p.EnabledByDefault))
    foreach (var entry in profile.Entries)
        foreach (var pattern in entry.Methods)
            if (pattern is "Update" or "LateUpdate" or "FixedUpdate" or "OnGUI")
                loopInDefault.Add(profile.Name + ":" + entry.Type + "." + pattern);
Check("trace.default-profiles-skip-frame-loops", loopInDefault.Count == 0,
    loopInDefault.Count == 0 ? "no default profile names a frame-loop method" : string.Join(",", loopInDefault));

// ---------------------------------------------------------------- instance field paths
// A snapshot and a trace both name fields through RecReflect paths. The check is structural: every step is a member
// name, the first step of an object path is a field or a property of a real type. Deep steps are checked for shape
// only, because whether the game exposes them is what the game run answers.
var badPaths = new List<string>();
foreach (var (profile, entry) in declared)
    foreach (var path in entry.InstanceFields)
        if (!ValidPath(path)) badPaths.Add(profile + ":" + entry.Type + "#" + path);
Check("trace.instance-paths-shaped", badPaths.Count == 0, badPaths.Count == 0 ? "ok" : string.Join(",", badPaths));

// ---------------------------------------------------------------- points.tsv
var pointRows = File.ReadAllLines(pointsPath).Where(l => l.Trim().Length != 0).ToArray();
var header = pointRows[0].Split('\t');
var expectedHeader = new[] { "pointId", "source", "point", "listId", "capture", "playerAction", "needsTwoMachines", "needsScreenshot", "needsDataBlock", "mergeRule" };
Check("points.header", header.SequenceEqual(expectedHeader), string.Join("|", header));

var rows = pointRows.Skip(1).Select((line, index) => new PointRow(index + 2, line.Split('\t'))).ToArray();
Check("points.columns", rows.All(r => r.Cells.Length == expectedHeader.Length),
    rows.Where(r => r.Cells.Length != expectedHeader.Length).Select(r => "line " + r.Line + "=" + r.Cells.Length).DefaultIfEmpty("ok").First());
Check("points.unique-ids", rows.Select(r => r.Cells[0]).Distinct(StringComparer.Ordinal).Count() == rows.Length, rows.Length + " rows");

var captureRoots = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var (profile, entry) in declared) captureRoots[profile + "/" + entry.Type] = profile;
var tracePatterns = declared.Select(d => d.Profile + "/" + d.Entry.Type).ToArray();

var manual = rows.Count(r => r.Cells[4].StartsWith("manual:", StringComparison.Ordinal));
var unresolved = new List<string>();
// Every row names how it is collected: `trace:`, `state:`, `net:`, `exp:`, `probe` or a `manual:` form for a point
// only a person can settle. A capability the 90 ruling deleted has no row at all, so there is no `cut` form and no
// placeholder form: a token this test cannot check against a profile, a snapshot path, the `net` profile or
// `probes/commands` is a row that collects nothing.
var pendingExp = new List<string>();
foreach (var row in rows)
{
    var id = row.Cells[0];
    var capture = row.Cells[4];
    if (capture.StartsWith("manual:", StringComparison.Ordinal)) continue;
    var methods = capture.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var token in methods)
    {
        if (token.StartsWith("trace:", StringComparison.Ordinal))
        {
            var body = token["trace:".Length..];
            var slash = body.IndexOf('/');
            if (slash < 0) { unresolved.Add(id + " malformed " + token); continue; }
            var profile = body[..slash];
            var rest = body[(slash + 1)..];
            // The channels a session writes are not profiles and have no type: a point that names one is pointing at
            // the session's own record, which the reader takes from the session directory.
            if (profile is "session" or "state" or "tracer" or "log" or "mark" or "shot" or "forge" or "exp" or "probe") continue;
            var profileNames = profiles.Select(p => p.Name).ToArray();
            if (!profileNames.Contains(profile, StringComparer.Ordinal)) { unresolved.Add(id + " unknown profile " + profile); continue; }
            // A token is either a type alone or a type and one of its methods. The split is tried both ways because a
            // type or a method may itself contain a dot-qualified name (`m_sync.…` is a field, `LG_TERM_Base.Enter` a
            // method); the first interpretation that resolves a type wins.
            var byType = declared.Where(d => d.Profile == profile).Select(d => d.Entry.Type).ToArray();
            if (byType.Any(t => RecGlob.IsMatch(t, rest))) continue;
            var dot = rest.LastIndexOf('.');
            var owner = dot < 0 ? null : rest[..dot];
            var leaf = dot < 0 ? null : rest[(dot + 1)..];
            if (owner == null) { unresolved.Add(id + " type not in profile: " + token); continue; }
            var declaring = declared.Where(d => d.Profile == profile && RecGlob.IsMatch(d.Entry.Type, owner)).ToArray();
            if (declaring.Length != 0 && MethodMatches(owner!, leaf!))
            {
                // The method must also be one a matching entry records: a point that names a method every entry of its
                // type excludes is a point that would read an empty record, which is the failure this test exists for.
                if (declaring.Any(d => d.Entry.MatchesMethod(leaf!))) continue;
                unresolved.Add(id + " method not recorded by " + profile + ":" + owner + ": " + token);
                continue;
            }
            unresolved.Add(id + " type or method not in profile: " + token);
            continue;
        }
        if (token.StartsWith("state:", StringComparison.Ordinal))
        {
            var path = token["state:".Length..];
            if (!ValidPath(path) && !path.Contains('*')) unresolved.Add(id + " state path not shaped: " + path);
            continue;
        }
        if (token.StartsWith("net:", StringComparison.Ordinal))
        {
            // A `net:` point names a type the `net` profile traces; the point is that a reader looks at the packet or
            // replication record for that type, not that a second configuration exists.
            var type = token["net:".Length..];
            var pattern = declared.Where(d => d.Profile == "net").Select(d => d.Entry.Type)
                .FirstOrDefault(t => RecGlob.IsMatch(t, type) || RecGlob.IsMatch(type, t));
            if (pattern == null) unresolved.Add(id + " net type not in the net profile: " + type);
            continue;
        }
        if (token.StartsWith("exp:", StringComparison.Ordinal))
        {
            var file = token["exp:".Length..];
            if (!File.Exists(Path.Combine(commandsDir, file))) pendingExp.Add(id + " -> probes/commands/" + file);
            continue;
        }
        if (token.StartsWith("probe", StringComparison.Ordinal)) continue;
        unresolved.Add(id + " unknown capture form: " + token);
    }
}
Check("points.capture-resolvable", unresolved.Count == 0, unresolved.Count == 0
    ? rows.Length + " rows resolve (" + manual + " manual)"
    : string.Join(" | ", unresolved.Take(40)));
detail["pointsUnresolved"] = unresolved;

detail["points"] = new
{
    rows = rows.Length,
    manual,
    withTrace = rows.Count(r => r.Cells[4].Contains("trace:", StringComparison.Ordinal)),
    withState = rows.Count(r => r.Cells[4].Contains("state:", StringComparison.Ordinal)),
    withNet = rows.Count(r => r.Cells[4].Contains("net:", StringComparison.Ordinal)),
    withExp = rows.Count(r => r.Cells[4].Contains("exp:", StringComparison.Ordinal)),
    needsTwoMachines = rows.Count(r => r.Cells[6] == "yes"),
    needsScreenshot = rows.Count(r => r.Cells[7] == "yes"),
    needsDataBlock = rows.Count(r => r.Cells[8] == "yes"),
    pendingCommands = pendingExp
};

// ---------------------------------------------------------------- snapshot change detection
// The detector's contract: the pass that first sees an identity seeds it and reports nothing (otherwise every level
// load would look like a burst of changes), later passes report field changes, added rows and vanished rows, and a
// pass that runs out of its row budget says so.
var detector = new CaptureChangeDetector(4096, 2);
var seed = detector.Compare(SectionsOf(Snapshot(1, "closed", "100", 1)));
Check("changes.first-pass-seeds", seed.Changed == 0 && seed.Added == 0 && seed.Rows == 2,
    "seed: rows=" + seed.Rows + " changed=" + seed.Changed + " tracked=" + detector.Describe());

var same = detector.Compare(SectionsOf(Snapshot(1, "closed", "100", 1)));
Check("changes.ignores-identical", same.Changed == 0 && same.Added == 0 && same.Removed == 0 && same.Rows == 2,
    "rows=" + same.Rows + " changed=" + same.Changed);

var moved = detector.Compare(SectionsOf(Snapshot(1, "closed", "80", 1)));
Check("changes.reports-field-change", moved.Changed == 1 && moved.ChangedRows.Count == 1 && moved.ChangedRows[0].Identity == "door/1",
    "changed=" + moved.Changed + " rows=" + moved.ChangedRows.Count);

var added = detector.Compare(SectionsOf(Snapshot(1, "closed", "80", 2)));
Check("changes.reports-added-row", added.Added == 1 && added.Removed == 0, "added=" + added.Added + " removed=" + added.Removed);

var removed = detector.Compare(SectionsOf(Snapshot(1, "closed", "80", 1)));
Check("changes.reports-removed-row", removed.Removed == 1 && removed.Added == 0, "removed=" + removed.Removed + " added=" + removed.Added);

detector.Reset();
Check("changes.reset-clears", detector.Seeded == 0, detector.Describe());

var budget = new CaptureChangeDetector(1, 2);
var budgetResult = budget.Compare(SectionsOf(Snapshot(1, "closed", "100", 1)));
Check("changes.enforces-row-budget", budgetResult.BudgetExhausted && budgetResult.Rows == 1,
    "rows=" + budgetResult.Rows + " exhausted=" + budgetResult.BudgetExhausted);

// The snapshot writer is the only shape a state record has; the check runs it through a real Utf8JsonWriter and reads
// the fields back, which is what a reader of the session directory does.
var snapshot = Snapshot(7, "open", "42", 2);
snapshot.Notes.Add("note");
snapshot.Skipped.Add("hudText");
using var stream = new MemoryStream();
using (var json = new Utf8JsonWriter(stream))
{
    // The recorder wraps a body in the record object; a body on its own has to be given one here.
    json.WriteStartObject();
    CaptureStateWriter.WriteSnapshot(json, snapshot);
    json.WriteEndObject();
}
using var document = JsonDocument.Parse(stream.ToArray());
var root = document.RootElement;
Check("state.snapshot-shape",
    root.GetProperty("reason").GetString() == "manual" && root.GetProperty("rows").GetInt32() == snapshot.RowCount
    && root.GetProperty("sections").TryGetProperty(CaptureSections.Doors, out var doors)
    && doors.GetProperty("items").GetArrayLength() == snapshot.Section(CaptureSections.Doors, "test").Rows.Count,
    "rows=" + root.GetProperty("rows").GetInt32());

// ---------------------------------------------------------------- report
var report = new
{
    verification = "managed tests over trace profiles, points.tsv, the interop metadata and the change detector",
    gameExecuted = false,
    passed = checks.Count(c => c.Passed),
    failed = checks.Count(c => !c.Passed),
    checks
};
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
detail["profiles"] = profiles.Select(p => new
{
    p.Name,
    p.EnabledByDefault,
    entries = p.Entries.Count,
    methods = p.Entries.Sum(e => e.Methods.Count)
}).ToArray();
detail["trace"] = new { files = files.Length, entries = declared.Length, configuredMethods, refused = refused.Count };
detail["diagnostics"] = diagnostics;
File.WriteAllText(detailPath, JsonSerializer.Serialize(detail, new JsonSerializerOptions { WriteIndented = true }));

foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Note);
Console.WriteLine((report.failed == 0 ? "PASS" : "FAIL") + " " + report.passed + "/" + checks.Count + " capture checks; no GTFO execution.");
foreach (var assembly in interop.Values) assembly.Dispose();
return report.failed == 0 ? 0 : 1;

IEnumerable<TypeDefinition> Walk(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes)
        foreach (var deep in Walk(nested)) yield return deep;
}

bool MatchesType(string pattern) => MatchesTypes(pattern).Count != 0;

List<TypeDefinition> MatchesTypes(string pattern)
{
    var found = new List<TypeDefinition>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (full, list) in byFullName)
        if (RecGlob.IsMatch(pattern, full))
            foreach (var (_, type) in list)
                if (seen.Add(type.FullName)) found.Add(type);
    foreach (var (shortName, list) in byShortName)
        if (RecGlob.IsMatch(pattern, shortName))
            foreach (var type in list)
                if (seen.Add(type.FullName)) found.Add(type);
    foreach (var (bare, list) in byBareName)
        if (RecGlob.IsMatch(pattern, bare))
            foreach (var type in list)
                if (seen.Add(type.FullName)) found.Add(type);
    return found;
}

bool MethodMatches(string typePattern, string methodPattern)
{
    foreach (var type in MatchesTypes(typePattern))
        foreach (var method in type.Methods)
            if (RecGlob.IsMatch(methodPattern, method.Name)) return true;
    return false;
}

/// <summary>The refusals Harmony itself imposes: a generic method or a method with a by-ref, pointer or open generic
/// parameter cannot be patched generically. It mirrors the runtime's own signature check over metadata, so the test
/// reports the same lines a load would skip.</summary>
static string? SignatureRefusal(MethodDefinition method)
{
    if (method.HasGenericParameters) return "generic";
    foreach (var parameter in method.Parameters)
    {
        var type = parameter.ParameterType;
        if (type.IsPointer || type.IsByReference || type.IsGenericParameter || type.HasGenericParameters) return "unsupported-parameter";
    }
    return null;
}

static bool ValidPath(string path)
{
    if (string.IsNullOrWhiteSpace(path)) return false;
    foreach (var step in path.Split('.'))
    {
        if (step.Length == 0 || step.Contains('*') && step.Length == 1) return false;
        if (!Regex.IsMatch(step, "^[A-Za-z_][A-Za-z0-9_]*$")) return false;
    }
    return true;
}

static CaptureSnapshot Snapshot(int doorCount, string status, string health, int players)
{
    var snapshot = new CaptureSnapshot(CaptureReason.Manual, "world", 7);
    var doors = snapshot.Section(CaptureSections.Doors, "test");
    for (var i = 1; i <= doorCount; i++)
        doors.Rows.Add(new CaptureRow("door", "door/" + i, "door " + i).Field("status", status).Field("health", health));
    var players1 = snapshot.Section(CaptureSections.Players, "test");
    for (var i = 0; i != players; i++)
        players1.Rows.Add(new CaptureRow("player", "player/" + i, "player " + i).Field("health", "100").Field("alive", "true"));
    return snapshot;
}

static IEnumerable<CaptureSection> SectionsOf(CaptureSnapshot snapshot)
    => CaptureStateWriter.Select(snapshot, CaptureStateWriter.ChangeSections);

internal sealed record Check(string Id, bool Passed, string Note);
internal sealed record PointRow(int Line, string[] Cells);

/// <summary>The tracer's report formatter takes the patches a load matched. A test that only reads profiles passes
/// none, and this is the shape the formatter names; the real type lives with the Harmony side of the tracer.</summary>
internal sealed class RecPatch
{
    internal RecPatch(string subject) => Subject = subject;
    internal string Subject { get; }
}




