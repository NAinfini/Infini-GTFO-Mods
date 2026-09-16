using System.Security.Cryptography;
using System.Text.Json;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;
using SNetwork;
using static Checks;

if (args.Length is < 1 or > 2) { Console.Error.WriteLine("Usage: EntityObservation <report.json> [sdk provenance]"); return 2; }
NativeReaderCases.Run();
ObserverRegistrationCases.Run();
InstanceResolverCases.Run();
Case("snapshot.exact-native-data", () =>
{
    using var s = new Scene(); var q = s.Query();
    Require(q.IsComplete, q.Code + ": " + string.Join(",", q.Items.Select(x => x.Code)));
    var x = q.RequireComplete().Single();
    Require(x.Ref == s.Ref && x.Kind == "enemy" && x.LifeState == "alive", "Identity/life not preserved.");
    Require(x.Position.SequenceEqual(new double[] { 1, 2, 3 }) && x.Faction == null && x.Tags.Count == 0, "Invented metadata.");
    Require(x.Receives.SequenceEqual(new[] { "health.heal" }) && s.Enemy.Damage.Sends == 0, "Unknown receiver or gameplay mutation.");
});
Case("snapshot.no-silent-host-support", () =>
{
    using var s = new Scene(observe: false); var q = s.Query();
    Require(!q.IsComplete && q.Items.Single().Code == "entity-observer-unavailable", "Missing reader became supported.");
});
Case("snapshot.full-health-still-receives-heal", () =>
{
    using var s = new Scene(); s.Enemy.Damage.Health = 100;
    Require(s.Query().RequireComplete().Single().Receives.Contains("health.heal"), "A valid no-op receiver was removed.");
});
Case("snapshot.dead-no-heal", () =>
{
    using var s = new Scene(); s.Enemy.Alive = false; s.Enemy.Damage.Health = 0;
    var x = s.Query().RequireComplete().Single(); Require(x.LifeState == "dead" && x.Receives.Count == 0, "Implicit revive support.");
});
var unsupported = new (string Name, Action<Scene> Change)[]
{
    ("missing-component", s => s.Enemy.Damage = null!),
    ("zero-receiver-pointer", s => s.Enemy.Damage.Pointer = IntPtr.Zero),
    ("receiver-unsetup", s => s.Enemy.Damage.IsSetup = false),
    ("missing-owner", s => s.Enemy.Damage.Owner = null!),
    ("wrong-owner", s => s.Enemy.Damage.Owner = Scene.NewEnemy(8, 20)),
    ("zero-health", s => s.Enemy.Damage.Health = 0),
    ("nan-health", s => s.Enemy.Damage.Health = float.NaN),
    ("infinite-maximum", s => s.Enemy.Damage.HealthMax = float.PositiveInfinity),
    ("above-maximum", s => s.Enemy.Damage.Health = 101),
    ("negative-maximum", s => s.Enemy.Damage.HealthMax = -1),
    ("negative-health", s => s.Enemy.Damage.Health = -1)
};
foreach (var item in unsupported) Case("receiver." + item.Name, () =>
{
    using var s = new Scene(); item.Change(s);
    Require(s.Query().RequireComplete().Single().Receives.Count == 0, "Unsupported native health receiver was declared.");
});
var unavailable = new (string Name, Action<Scene> Change)[]
{
    ("not-setup", s => s.Enemy.IsSetup = false),
    ("zero-native-pointer", s => s.Enemy.Pointer = IntPtr.Zero),
    ("nan-position", s => s.Enemy.Position = (float.NaN, 2, 3)),
    ("infinite-position", s => s.Enemy.Position = (1, float.PositiveInfinity, 3)),
    ("moving-during-read", s => { float moved = 0; s.Enemy.OnPositionRead = () => (++moved, 2, 3); })
};
foreach (var item in unavailable) Case("unavailable." + item.Name, () =>
{
    using var s = new Scene(); item.Change(s);
    var q = s.Query(); Require(!q.IsComplete && q.Items.All(x => x.Snapshot == null), "Uncertain observation was fabricated.");
});
Case("snapshot.reader-exception", () =>
{
    using var s = new Scene(); s.Enemy.OnPositionRead = () => throw new IOException("native getter");
    Require(s.Query().Items.Single().Code == "entity-observer-failed", "Native reader failure was hidden.");
});
Case("snapshot.frozen-values", () =>
{
    using var s = new Scene(); var x = s.Query().RequireComplete().Single(); s.Enemy.Position = (9, 8, 7);
    Require(x.Position.SequenceEqual(new double[] { 1, 2, 3 }), "Snapshot retained mutable native position.");
    bool rejected = false;
    try { ((IList<double>)x.Position)[0] = 9; } catch (NotSupportedException) { rejected = true; }
    Require(rejected, "Snapshot positions are mutable.");
});
Case("snapshot.negative-coordinates", () =>
{
    using var s = new Scene(); s.Enemy.Position = (-10, -20, -30);
    Require(s.Query().RequireComplete().Single().Position.SequenceEqual(new double[] { -10, -20, -30 }), "Coordinates were normalized.");
});
Case("snapshot.wrong-native-id", () =>
{
    using var s = new Scene(); Require(EnemyEntityObserver.Read(s.Enemy, s.Ref with { Id = "gtfo.enemy:8" }) == null,
        "Reader fabricated an association to another native ID.");
});
Case("lifecycle.registration-not-ready", () =>
{
    using var s = new Scene(start: false); Require(s.Query().Code == "runtime-not-ready", "Pre-start observation was accepted.");
});
Case("lifecycle.world-reset", () =>
{
    using var s = new Scene(); s.Kernel.BeginWorld(2);
    Require(!s.Query().IsComplete, "Previous-world reference survived.");
    var fresh = s.Module.TrackSpawn(s.Enemy);
    Require(s.Kernel.InspectEntities(new[] { fresh }).IsComplete && fresh != s.Ref, "New world did not use a new reference.");
});
Case("lifecycle.same-pointer-new-life", () =>
{
    using var s = new Scene(); var old = s.Module.CaptureDespawn(s.Enemy); s.Module.CompleteDespawn(old);
    var fresh = s.Module.TrackSpawn(s.Enemy); s.Module.CompleteDespawn(old);
    Require(!s.Query().IsComplete && s.Kernel.InspectEntities(new[] { fresh }).IsComplete, "Old life or teardown token survived.");
});
Case("lifecycle.clear-during-native-read", () =>
{
    using var s = new Scene(); s.Enemy.OnPositionRead = () => { s.Module.ClearWorld(); return (1, 2, 3); };
    Require(!s.Query().IsComplete, "Identity invalidation during native getter was missed.");
});
Case("lifecycle.gate-lost-during-read", () =>
{
    using var s = new Scene(); s.Enemy.OnPositionRead = () => { s.Allowed = false; return (1, 2, 3); };
    Require(!s.Query().IsComplete, "Changed phase/authority gate was ignored.");
});
Case("lifecycle.replaced-life-during-read", () =>
{
    using var s = new Scene(); s.Reader = (actor, reference) =>
    {
        var snapshot = EnemyEntityObserver.Read(actor, reference);
        s.Module.TrackDespawn(actor); s.Module.TrackSpawn(actor); return snapshot;
    };
    Require(!s.Query().IsComplete, "Replacement life was returned under the old reference.");
});
Case("lifecycle.wrong-reference-from-reader", () =>
{
    using var s = new Scene(); s.Reader = (_, reference) => new(reference with { LifeEpoch = 999 }, "enemy", null,
        "alive", Array.Empty<string>(), Array.Empty<string>(), new double[] { 0, 0, 0 });
    Require(!s.Query().IsComplete, "Mismatched reader identity accepted.");
});
Case("lifecycle.client-observation-unavailable", () =>
{
    using var s = new Scene(); SNet.IsMaster = false;
    Require(!s.Query().IsComplete && s.Enemy.Damage.Sends == 0, "Host-only native observation leaked to client.");
});
Case("lifecycle.stopped", () =>
{
    using var s = new Scene(); s.Kernel.StopRuntime(); Code(() => s.Query(), "runtime-not-ready");
});
Case("lifecycle.unregistered", () =>
{
    using var s = new Scene(); s.Module.Dispose(); Require(!s.Query().IsComplete, "Unregistered observer survived.");
});
Case("query.mixed-is-incomplete", () =>
{
    using var s = new Scene(); var q = s.Kernel.InspectEntities(new[] { s.Ref, s.Ref with { Id = "gtfo.enemy:8" } });
    Require(q.Status == "partial" && q.Items.Count == 2 && q.Items.Count(x => x.Snapshot != null) == 1, "Incomplete query silently truncated.");
    Code(() => q.RequireComplete(), "entity-query-incomplete");
});
Case("query.duplicates-and-empty", () =>
{
    using var s = new Scene(); var q = s.Kernel.InspectEntities(new[] { s.Ref, s.Ref });
    Require(q.IsComplete && q.Requested == 2 && q.Distinct == 1 && q.Items.Count == 1, "Reference dedupe failed.");
    Require(s.Kernel.InspectEntities(Array.Empty<EntityReference>()).IsComplete, "Empty explicit query is not complete.");
});
Case("query.budget-no-truncation", () =>
{
    using var s = new Scene(); var refs = Enumerable.Repeat(s.Ref, RuntimeKernel.MaximumEntityReferencesPerQuery + 1).ToArray();
    var q = s.Kernel.InspectEntities(refs); Require(q.Status == "rejected" && q.Items.Count == 0, "Budget silently returned partial coverage.");
});
Case("query.tick-budget-not-reset-by-advance", () =>
{
    using var s = new Scene(); s.Kernel.Advance(1, true);
    for (int i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick; i++) Require(s.Query().IsComplete, "Early budget rejection.");
    s.Kernel.Advance(1, true); Require(s.Query().Code == "entity-query-tick-budget", "Same tick reset query budget.");
    s.Kernel.Advance(2, true); Require(s.Query().IsComplete, "New tick did not replenish budget.");
});
Case("query.simulation-thread", () =>
{
    using var s = new Scene(); Code(() => Task.Run(() => s.Query()).GetAwaiter().GetResult(), "wrong-thread");
});
Case("query.observer-cannot-mutate-runtime", () =>
{
    using var s = new Scene(); s.Reader = (_, _) => { s.Kernel.BeginWorld(2); return null; };
    var q = s.Query(); Require(q.Items.Single().Code == "entity-observer-failed" && s.Kernel.WorldEpoch == 1,
        "Read-only observation mutated the shared world.");
});
Case("actors.no-fallback-and-no-inferred-enemy", () =>
{
    using var s = new Scene(); var other = Scene.NewEnemy(8, 20); var otherRef = s.Module.TrackSpawn(other);
    var snapshots = s.Kernel.InspectEntities(new[] { s.Ref, otherRef }).RequireComplete();
    var relations = new RuntimeFactionRelations(Array.Empty<RuntimeFactionRelation>());
    Require(relations.Resolve(snapshots[0], snapshots[1]) == "unknown" && relations.Resolve(snapshots[0], snapshots[0]) == "self",
        "Native kind was treated as a faction relationship.");
    var actors = new RuntimeActorContext(new Dictionary<string, EntityReference> { ["source"] = s.Ref });
    Require(s.Kernel.InspectActor(actors, "owner").Code == "actor-missing", "Missing owner fell back to source.");
});
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var report = new
{
    schemaVersion = 1, verification = "native-reader-and-single-provider-source-with-compiled-r3-sdk-and-game-doubles",
    gameExecuted = false, multiplayerExecuted = false, sdkProvenance = args.Length == 2 ? args[1] : "worktree",
    receiverSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "EnemyModule.source.txt")),
    readerSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "EnemyEntityObserver.source.txt")),
    typeReaderSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "EnemyTypeReader.source.txt")),
    sdkSha256 = Hash(typeof(RuntimeKernel).Assembly.Location), utc = DateTimeOffset.UtcNow,
    passed = Rows.Count(x => x.Passed), failed = Rows.Count(x => !x.Passed), checks = Rows
};
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(report.failed == 0 ? "PASS" : "FAIL")} {report.passed}/{Rows.Count} Enemy entity observation cases; no GTFO execution.");
return report.failed == 0 ? 0 : 1;
