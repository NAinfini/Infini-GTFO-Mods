using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;
using static IdentityFixture;

var checks = new List<string>();
// Checks inside a Scenario scope count as that native identity spec's managed substitute, never as native execution.
var scenarioChecks = new SortedDictionary<string, int>(StringComparer.Ordinal);
var activeScenarios = Array.Empty<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks.Add(name); Console.Error.WriteLine("PASS: " + name);
    foreach (var id in activeScenarios) scenarioChecks[id] = scenarioChecks.GetValueOrDefault(id) + 1;
}
IDisposable Scenario(params string[] ids)
{
    activeScenarios = ids;
    return new ScenarioScope(() => activeScenarios = Array.Empty<string>());
}
void Reject(Action action, string suffix, string name)
{
    try { action(); }
    catch (RuntimeContractException error)
    { Check(error.Code == "map.identity." + suffix, name + " [" + error.Code + "]"); return; }
    throw new InvalidOperationException("FAIL: accepted " + name);
}
void RuntimeReject(Action action, string name)
{
    try { action(); }
    catch (RuntimeContractException error)
    { Check(true, name + " [" + error.Code + "]"); return; }
    throw new InvalidOperationException("FAIL: accepted " + name);
}
var assembly = typeof(ModuleDefinition).Assembly;
Check(typeof(MapIdentitySession).Assembly == assembly, "Production session is tested from the actual Map assembly");
Check(!typeof(MapIdentitySession).IsPublic && !typeof(MapSourceLock).IsPublic,
    "Staging identity seam does not create a public replacement SDK/export schema");
Check(assembly.GetType("IdentityFixture") == null, "Synthetic adapter is not shipped in Map");
var module = ModuleDefinition.Create();
Check(module.Handlers.Count == 0 && module.BindingSupport.Count == 0 && (module.EntityResolvers?.Count ?? 0) == 0,
    "Default provider still exports no unsupported gameplay or native resolver");
Check(!assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("Unity") || a.Name.StartsWith("BepInEx")),
    "Managed identity implementation introduces no native loader dependency");
using (Scenario("same-zone-index", "same-resource-placements"))
using (var f = new IdentityFixture())
{
    var basic = Address();
    var addresses = new[] { basic, basic with { Dimension = 1 }, basic with { Layer = 1 },
        basic with { LayoutId = "layout-b" }, basic with { LayoutRevision = "revision-b" },
        basic with { PlacementId = "placement-b" }, basic with { ObjectId = "source-area-b" },
        basic with { Kind = MapObjectKind.Geomorph }, basic with { LocalZoneIndex = 999 } };
    var references = new List<EntityReference>();
    for (var i = 0; i < addresses.Length; i++)
    {
        var ticket = f.Begin(addresses[i]); var receipt = f.Bind(ticket, Key(i + 1));
        Check(f.Session.TryResolve(receipt.Entity, out var value, out _) && value!.Address == addresses[i]
            && value.Source == Source(), "Exact source-backed address survives: " + i);
        references.Add(receipt.Entity);
    }
    Check(references.Distinct().Count() == addresses.Length, "Dimensions/layers/layouts/placements/areas/kinds never merge");
    Check(references.All(r => r.Id.Split(':').Length == 3 && r.Id.StartsWith("gtfo.map:")
        && Guid.TryParseExact(r.Id.Split(':')[1], "N", out _)),
        "Entity IDs are opaque, not native pointers or resource identifiers");
    Check(f.Session.ObservedCount == addresses.Length && f.Session.HistoryCount == addresses.Length,
        "Bound identities and history counts match exact instances");
}
using (Scenario("duplicate-observation"))
using (var f = new IdentityFixture())
{
    var ticket = f.Begin();
    Check(!f.Session.TryResolve(ticket.Value.Entity, out var pending, out var code) && pending == null
        && code == "map.identity.not-observed" && f.ProbeCalls == 0, "Pending attempt is not a created object or proven absence");
    var first = f.Bind(ticket); var repeated = f.Bind(ticket);
    Check(first.Entity == repeated.Entity && repeated.Code == "map.identity.duplicate"
        && f.Session.ObservedCount == 1, "Duplicate exact creation returns the original receipt without double allocation");
    Reject(() => f.Begin(), "address-already-tracked", "A second creation cannot silently replace a live author address");
    Check(f.Current(first.Entity), "Rejected duplicate intent leaves the original exact identity intact");
    Check(JsonSerializer.Serialize(ticket.Value).Contains("9223372036854775807"), "Source int64 path ID is preserved as a string without float rounding");
    Check(!JsonSerializer.Serialize(ticket.Value).Contains("Pointer"), "Diagnostic identity snapshot excludes native pointer fields");
}
using (Scenario("same-native-id-new-life"))
using (var f = new IdentityFixture())
{
    var old = f.Begin(); var before = f.Bind(old);
    Reject(() => f.Session.Retire(old, Key(2)), "destruction-object-mismatch", "Wrong-object destruction cannot retire an identity");
    Check(f.Current(before.Entity), "Wrong-object destruction preserves the current life");
    Check(f.Session.Retire(old, Key()) && !f.Session.Retire(old, Key()), "Exact destruction is idempotent");
    Check(!f.Current(before.Entity), "Destroyed object no longer resolves");
    var fresh = f.Begin(); var after = f.Bind(fresh);
    Check(before.Entity.Id == after.Entity.Id && after.Entity.LifeEpoch == before.Entity.LifeEpoch + 1,
        "Native pointer and author address reuse creates a new life on the same entity ID");
    Reject(() => f.Callback(old), "stale-life", "Late old-life creation cannot attach to replacement");
    Reject(() => f.Session.Retire(old, Key()), "stale-life", "Late old-life destruction cannot kill replacement");
    Check(!f.Current(before.Entity) && f.Current(after.Entity), "Only the replacement life resolves after native ID reuse");
    f.Session.Retire(fresh, Key());
    Reject(() => f.Begin(source: Source() with { ResourceRevision = "changed" }), "source-lock-changed",
        "Retired address cannot silently switch its locked resource revision");
}
using (Scenario("generation-reentry"))
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); var old = f.Bind(ticket);
    f.Session.BeginGeneration();
    Check(!f.Current(old.Entity) && f.Session.HistoryCount == 0, "Generation reentry invalidates all current associations");
    Reject(() => f.Callback(ticket), "stale-generation", "Previous generation ticket cannot reenter the same world");
    var replacement = f.Bind(f.Begin());
    Check(replacement.Entity != old.Entity && f.Current(replacement.Entity), "Same-world reentry issues a distinct instance reference");
    f.Kernel.BeginWorld(2);
    Check(!f.Current(replacement.Entity) && f.Session.HistoryCount == 0, "Public world callback immediately clears Map identity history");
    Reject(() => f.Callback(ticket), "stale-world", "Previous world callback fails before native probing");
    Check(f.Bind(f.Begin()).Entity.WorldEpoch == 2, "New creation consumes the current shared world epoch");
}
using (var a = new IdentityFixture())
using (var b = new IdentityFixture())
{
    var foreign = a.Begin();
    Reject(() => b.Callback(foreign), "foreign-ticket", "Creation ticket cannot be reused by another Map session");
    Check(b.ProbeCalls == 0 && b.Session.HistoryCount == 0, "Foreign ticket is rejected before native access or mutation");
    var refA = a.Bind(foreign).Entity; var refB = b.Bind(b.Begin()).Entity;
    Check(refA != refB && !b.Current(refA), "Independent sessions do not accidentally alias opaque entity IDs");
}
using (Scenario("ambiguous-token"))
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); var old = f.Bind(ticket);
    Reject(() => f.Bind(ticket, Key(2)), "ambiguous-token", "One creation token cannot select between two native objects");
    Check(!f.Current(old.Entity) && f.Session.ObservedCount == 0, "Conflicting observation revokes the earlier mapping instead of keeping the first");
    Reject(() => f.Callback(ticket), "ambiguous-identity", "Later duplicate cannot heal a quarantined token");
    var second = f.Begin(Address("placement-b"));
    Reject(() => f.Bind(second, Key(2)), "native-claimed", "Ambiguous alternate native key remains quarantined against reassignment");
    Check(!f.Current(second.Value.Entity), "Contender does not resolve after a native claim conflict");
}
using (Scenario("same-resource-placements"))
using (var f = new IdentityFixture())
{
    var first = f.Begin(); var bound = f.Bind(first); var second = f.Begin(Address("placement-b"));
    Reject(() => f.Bind(second), "native-claimed", "One native identity cannot be claimed by two author instances");
    Check(!f.Current(bound.Entity) && !f.Current(second.Value.Entity) && f.Session.ObservedCount == 0,
        "Native ownership conflict quarantines both author identities");
}
var sourceChanges = new[] { Source() with { ResourceId = "other" }, Source() with { ResourceRevision = "other" },
    Source() with { SourceIdentityHash = new string('b', 64) }, Source() with { SourceFile = "other.assets" },
    Source() with { SourcePathId = "9223372036854775806" } };
using (Scenario("revision-mismatch"))
    for (var i = 0; i < sourceChanges.Length; i++)
    {
        using var f = new IdentityFixture(); var ticket = f.Begin(); var prior = f.Bind(ticket);
        var source = sourceChanges[i];
        Reject(() => f.Session.ObserveCreated(ticket, new(ticket.Value.Address, source, Key())), "source-mismatch", "Exact source lock mismatch rejected: " + i);
        Check(!f.Current(prior.Entity), "Conflicting source evidence invalidates prior mapping: " + i);
    }
using (Scenario("unknown-source"))
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); f.Native.Add(Key()); f.Incarnations[Key()] = ticket;
    Reject(() => f.Session.ObserveCreated(ticket, new(ticket.Value.Address, null, Key())), "unknown-source",
        "Unknown source is unresolved even when an object is alive");
    Check(f.ProbeCalls == 0 && f.Session.GapCount == 1 && f.Session.LastGap == MapObservationGap.UnknownSource,
        "Unknown provenance records a gap without probing or inventing source evidence");
    Check(!f.Current(ticket.Value.Entity), "Unknown source does not expose a reference as current");
}
using (Scenario("same-resource-placements"))
using (var f = new IdentityFixture())
{
    var a = f.Begin(); var boundA = f.Bind(a); var b = f.Begin(Address("placement-b"));
    Check(a.Value.Entity != b.Value.Entity && a.Value.Source == b.Value.Source,
        "One source lock placed twice issues one distinct creation ticket per placement");
    f.Native.Add(Key(2)); f.Incarnations[Key(2)] = b;
    Reject(() => f.Session.ObserveCreated(b, new(a.Value.Address, b.Value.Source, Key(2))), "address-mismatch",
        "Callback for placement B carrying placement A's address is rejected, not matched by resource");
    Check(f.Current(boundA.Entity) && !f.Current(b.Value.Entity), "Rejected sibling callback leaves placement A intact and B unresolved");
}
using (Scenario("area-course-node"))
using (var f = new IdentityFixture())
{
    var areaA = f.Begin(); var boundA = f.Bind(areaA);
    var areaB = f.Begin(Address() with { ObjectId = "source-area-b" });
    Check(areaA.Value.Entity != areaB.Value.Entity, "Two areas of one geomorph placement get distinct identities");
    f.Native.Add(Key(2)); f.Incarnations[Key(2)] = areaB;
    Reject(() => f.Session.ObserveCreated(areaB, new(areaA.Value.Address, areaB.Value.Source, Key(2))), "address-mismatch",
        "Observation naming the sibling area cannot bind the requested area");
    Check(f.Current(boundA.Entity) && !f.Current(areaB.Value.Entity), "Wrong-area observation leaves the sibling area intact");
}
using (Scenario("area-course-node"))
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); var prior = f.Bind(ticket);
    Reject(() => f.Session.ObserveCreated(ticket, new(ticket.Value.Address with { Dimension = 1 }, ticket.Value.Source, Key())),
        "address-mismatch", "Observed native dimension cannot substitute for the requested dimension");
    Check(!f.Current(prior.Entity), "Address conflict invalidates the previously observed mapping");
}
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); var observation = new MapCreationObservation(ticket.Value.Address, ticket.Value.Source, Key());
    Reject(() => f.Session.ObserveCreated(ticket, observation), "native-not-current", "Failed live probe cannot bind a pending creation");
    Check(f.Session.ObservedCount == 0 && !f.Current(ticket.Value.Entity), "Probe rejection leaves no current identity");
    f.ThrowProbe = true;
    Reject(() => f.Session.ObserveCreated(ticket, observation), "probe-failed", "Probe exception is reported without pretending success");
    f.ThrowProbe = false;
    var bound = f.Bind(ticket);
    Check(f.Current(bound.Entity), "Valid evidence can bind a still-pending attempt after a transient probe failure");
    f.Native.Clear();
    Check(!f.Session.TryResolve(bound.Entity, out var unavailable, out var code) && unavailable == null
        && code == "map.identity.native-not-current", "Every lookup rechecks native liveness; stale cache cannot grant validity");
    f.ThrowProbe = true;
    Check(!f.Session.TryResolve(bound.Entity, out _, out code) && code == "map.identity.probe-failed",
        "Read probe exceptions are surfaced as unresolvable, not successful reads");
}
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); f.Native.Add(Key()); f.Incarnations[Key()] = ticket;
    f.DuringProbe = _ => f.Kernel.BeginWorld(2);
    Reject(() => f.Session.ObserveCreated(ticket, new(ticket.Value.Address, ticket.Value.Source, Key())),
        "observation-changed", "World invalidation during a creation probe prevents stale commit");
    f.DuringProbe = null;
    Check(f.Session.ObservedCount == 0 && !f.Current(ticket.Value.Entity), "Probe-crossed world cannot repopulate cleared associations");
}
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); var bound = f.Bind(ticket);
    f.DuringProbe = _ => f.Kernel.BeginWorld(2);
    Check(!f.Session.TryResolve(bound.Entity, out var value, out var code) && value == null
        && code == "map.identity.observation-changed", "World invalidation during lookup rejects an otherwise-live native object");
}
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); var bound = f.Bind(ticket);
    Action[] reentrant = { () => f.Session.Retire(ticket, Key()), () => f.Session.BeginGeneration(),
        () => f.Session.MarkGap(MapObservationGap.CallbackLost), () => f.Session.Dispose(),
        () => f.Begin(Address("in-probe")) };
    for (var i = 0; i < reentrant.Length; i++)
    {
        var nested = reentrant[i]; f.DuringProbe = _ => nested();
        Check(!f.Session.TryResolve(bound.Entity, out _, out var code) && code == "map.identity.probe-reentrancy",
            "Native probe cannot reenter identity mutation: " + i);
    }
    f.DuringProbe = null;
    Check(f.Current(bound.Entity) && f.Session.HistoryCount == 1 && f.Session.GapCount == 0,
        "Rejected reentrant calls leave the live identity state unchanged");
}
using (var f = new IdentityFixture(start: false))
{
    Reject(() => f.Begin(), "not-ready", "Registering provider does not imply observation readiness");
    Check(f.Kernel.StartRuntime(() => {}), "Explicit public startup enters Ready");
    var ticket = f.Begin(); var bound = f.Bind(ticket);
    Check(f.Current(bound.Entity) && f.Kernel.Lifecycle.IsHost == null,
        "Read-only observation does not fabricate authority before the first simulation tick");
    f.Kernel.Advance(1, false);
    Check(f.Current(bound.Entity), "Client observation remains read-only and does not claim gameplay authority");
    f.Kernel.StopRuntime();
    Check(!f.Current(bound.Entity) && f.Session.ObservedCount == 0 && f.Session.HistoryCount == 0,
        "Runtime stop invalidates domain state and detaches the lifecycle observer");
    Reject(() => f.Begin(), "session-detached", "Stopped session cannot accept new source observations");
}
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); var bound = f.Bind(ticket); var calls = f.ProbeCalls;
    RuntimeReject(() => Task.Run(() => f.Current(bound.Entity)).GetAwaiter().GetResult(),
        "Wrong-thread lookup is rejected by the actual shared SDK");
    RuntimeReject(() => Task.Run(() => f.Begin(Address("off-thread"))).GetAwaiter().GetResult(),
        "Wrong-thread mutation is rejected by the actual shared SDK");
    Check(f.ProbeCalls == calls && f.Session.HistoryCount == 1, "Wrong-thread calls never reach native probes or mutate identity");
    f.Session.Dispose(); f.Session.Dispose();
    Check(!f.Current(bound.Entity) && f.Session.HistoryCount == 0, "Explicit detach is idempotent and retains no current mappings");
    f.Kernel.BeginWorld(2);
    Check(f.Session.HistoryCount == 0, "Detached session receives no repopulating world callback");
}
using (Scenario("incomplete-observations"))
using (var f = new IdentityFixture(limit: 2))
{
    var a = f.Begin(); var first = f.Bind(a); f.Session.Retire(a, Key());
    var b = f.Begin(Address("b")); f.Bind(b, Key(2)); f.Session.Retire(b, Key(2));
    Reject(() => f.Begin(Address("c")), "identity-budget", "Bounded history refuses new identities instead of evicting tombstones");
    Check(f.Session.HistoryCount == 2 && f.Session.GapCount == 1 && f.Session.LastGap == MapObservationGap.CapacityExceeded,
        "Capacity failure is explicit incomplete evidence, not silent truncation");
    Check(!f.Current(first.Entity), "Capacity pressure cannot resurrect a retired life");
    var reuse = f.Begin(); f.Bind(reuse); f.Session.Retire(reuse, Key());
    Check(f.Session.HistoryCount == 2, "Known address reuse advances life without growing bounded history");
    f.Session.BeginGeneration();
    Check(f.Session.GapCount == 0 && f.Bind(f.Begin(Address("c"))).Entity != first.Entity,
        "Explicit new generation resets bounded capture without reusing old instance identity");
}
using (Scenario("incomplete-observations"))
using (var f = new IdentityFixture())
{
    var ticket = f.Begin(); var bound = f.Bind(ticket);
    f.Session.MarkGap(MapObservationGap.CallbackLost);
    Check(f.Session.GapCount == 1 && f.Current(bound.Entity), "Reported capture gap is not inferred destruction of a live observed object");
    Check(!f.Session.TryResolve(bound.Entity with { Id = "gtfo.map:never-observed" }, out _, out var code)
        && code == "map.identity.not-observed", "Missing capture is unknown, never a complete-inventory absence claim");
    f.Native.Clear();
    Check(!f.Current(bound.Entity), "Incomplete capture still requires liveness for every known reference");
}
using (var f = new IdentityFixture())
{
    foreach (var value in new[] { "9223372036854775808", "-9223372036854775809", "1.0", "1e3", "+1", "01", " 1" })
        Reject(() => f.Begin(source: Source() with { SourcePathId = value }), "invalid-source-path-id", "Noncanonical/out-of-int64 source path rejected: " + value);
    Reject(() => f.Begin(source: Source() with { SourceIdentityHash = "preview-hash" }), "invalid-source-hash", "Invalid digest is not accepted as a source lock");
    Reject(() => f.Begin(Address() with { Dimension = -1 }), "invalid-zone-address", "Unknown dimension cannot be coerced to zero");
    Reject(() => f.Begin(Address() with { Kind = (MapObjectKind)99 }), "invalid-object-kind", "Unknown world object kind is rejected");
    Reject(() => f.Begin(Address() with { PlacementId = " name " }), "invalid-address", "Author IDs are not silently trimmed");
    var ticket = f.Begin(source: Source() with { SourcePathId = "-9223372036854775808" });
    Check(f.Bind(ticket).Entity.LifeEpoch == 1, "Full negative int64 source path ID remains exact");
    var other = f.Begin(Address("invalid-native"));
    foreach (var native in new[] { new MapNativeIdentity(0, 1), new MapNativeIdentity(-1, 1), new MapNativeIdentity(123, 0) })
        Reject(() => f.Session.ObserveCreated(other, new(other.Value.Address, other.Value.Source, native)),
            "invalid-native-identity", "Invalid native observation rejected: " + native.UnityInstanceId + "/" + native.Pointer);
    Check(f.Session.ObservedCount == 1, "Malformed observations do not partially bind an identity");
    Check(!f.Session.TryResolve(null!, out _, out var code) && code == "map.identity.invalid-reference", "Null reference never resolves");
}
{
    var index = new MapObjectIdentityIndex(1); index.BeginWorld(1);
    var ticket = index.BeginCreation(Address(), Source());
    index.ObserveCreated(ticket, new(Address(), Source(), Key()));
    Reject(() => index.ObserveCreated(ticket, new(Address(), Source(), Key(2))), "ambiguous-token", "Core index quarantines an alternate key");
    for (var i = 0; i < 200; i++)
    {
        try { index.ObserveCreated(ticket, new(Address(), Source(), Key(i + 3))); }
        catch (RuntimeContractException error) { if (error.Code != "map.identity.ambiguous-identity") throw; }
    }
    Check(index.HistoryCount == 1 && index.NativeKeyCount == 2 && index.ObservedCount == 0,
        "Repeated conflicting callbacks do not grow quarantined native-key storage");
    Reject(() => index.BeginWorld(0), "stale-world", "World epoch cannot move backwards");
    RuntimeReject(() => new MapObjectIdentityIndex(0), "Zero identity budget is rejected");
}
using (Scenario("same-native-id-new-life"))
using (var f = new IdentityFixture())
{
    var old = f.Begin(); var before = f.Bind(old);
    var next = f.Begin(Address("replacement-with-missed-destroy"));
    f.Incarnations[Key()] = next; // Both numeric native IDs stay unchanged.
    Check(!f.Session.TryResolve(before.Entity, out _, out var code) && code == "map.identity.native-not-current",
        "Exact creation witness rejects old life when both native pointer and Unity ID are reused");
    Reject(() => f.Bind(next), "native-claimed", "Missed destruction cannot silently transfer an existing native claim");
    Check(!f.Current(before.Entity) && !f.Current(next.Value.Entity), "Unresolved incarnation conflict fails closed for both tickets");
}
using (var f = new IdentityFixture())
{
    var cancelled = f.Begin();
    Check(f.Session.CancelCreation(cancelled) && !f.Session.CancelCreation(cancelled), "Never-created attempt cancellation is idempotent");
    Reject(() => f.Callback(cancelled), "retired-life", "Cancelled creation cannot be revived by a delayed callback");
    var retry = f.Begin(); var bound = f.Bind(retry);
    Check(bound.Entity.Id == cancelled.Value.Entity.Id && bound.Entity.LifeEpoch > cancelled.Value.Entity.LifeEpoch,
        "Explicit retry after failed creation gets a new life without discarding the tombstone");
    Reject(() => f.Session.CancelCreation(retry), "creation-already-observed", "Cancellation cannot impersonate destruction of an observed object");
    Check(f.Current(bound.Entity), "Rejected cancellation leaves the actual observed object intact");
}
var scenarioSpec = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "fixtures", "native-identity-scenarios.json"))).RootElement;
var specCases = scenarioSpec.GetProperty("cases").EnumerateArray().ToArray();
var specIds = specCases.Select(c => c.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal).ToArray();
Check(!scenarioSpec.GetProperty("nativeExecuted").GetBoolean() && specCases.All(c =>
        !string.IsNullOrWhiteSpace(c.GetProperty("managedSubstitute").GetString())
        && !string.IsNullOrWhiteSpace(c.GetProperty("nativeOnly").GetString())),
    "Native identity specs keep native execution false and separate managed substitute from native-only proof");
Check(specIds.Distinct().Count() == specIds.Length && specIds.SequenceEqual(scenarioChecks.Keys),
    "Every native identity spec id has tagged managed checks and no tag lacks a spec");
Console.WriteLine(JsonSerializer.Serialize(new {
    verification = "production-managed-map-identity-with-synthetic-native-probes",
    nativeGameExecuted = false, nativeHooksInstalled = false, gameplayBindingsRegistered = false,
    productionAssembly = assembly.GetName().FullName,
    sdkAssembly = typeof(RuntimeKernel).Assembly.GetName().FullName,
    nativeScenariosExecuted = false, managedScenarioChecks = scenarioChecks,
    passed = checks.Count, checks
}, new JsonSerializerOptions { WriteIndented = true }));

sealed class ScenarioScope : IDisposable
{
    private readonly Action end;
    internal ScenarioScope(Action end) => this.end = end;
    public void Dispose() => end();
}
