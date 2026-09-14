using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon;

int assertions = 0;
var passed = new List<string>();
void Check(bool condition, string reason)
{
    if (!condition) throw new Exception("FAIL: " + reason);
    assertions++;
}
void Reject(Action action, string code)
{
    try { action(); }
    catch (RuntimeContractException error) { Check(error.Code == code, error.Code + " != " + code); return; }
    throw new Exception("FAIL: expected rejection " + code);
}
void Case(string name, Action<TestWorld> action, bool start = true, int active = 1024, int history = 8192)
{
    using var world = new TestWorld(start, active, history);
    action(world); passed.Add(name); Console.WriteLine("PASS " + name);
}
Case("same-definition-two-instances", w =>
{
    var a = w.Track(); var b = w.Track(w.Item("b", "GearSpecial"));
    Check(a.Observation.ResourceId == b.Observation.ResourceId, "same resource");
    Check(w.Session.RequireCurrent(a).Entity != w.Session.RequireCurrent(b).Entity, "distinct instances");
});
Case("same-slot-replacement", w =>
{
    var old = w.Track(); Check(w.Session.Remove(old.Observation.Entity), "remove exact old life");
    var next = w.Track(w.Item("b"));
    Reject(() => w.Session.RequireCurrent(old), "equipment.stale-instance");
    Check(w.Session.RequireCurrent(next).Entity.Id.EndsWith(":b"), "replacement resolves");
});
Case("transfer-invalidates-old-owner-and-aba", w =>
{
    var item = w.Item(); var old = w.Track(item);
    w.Session.Record(item with { Owner = item.Owner! with { Id = "fixture.player:b" } });
    Reject(() => w.Session.CaptureOwnedUse(item.Entity, w.Owner, true), "equipment.owner-mismatch");
    Reject(() => w.Session.RequireCurrent(old), "equipment.observation-changed");
    w.Session.Record(item);
    Reject(() => w.Session.RequireCurrent(old), "equipment.observation-changed");
    Check(w.Session.RequireCurrent(w.Session.CaptureOwnedUse(item.Entity, w.Owner, true)) == item, "new ticket");
});
Case("duplicate-observation-is-idempotent", w =>
{
    var item = w.Item(); var ticket = w.Track(item); w.Session.Record(item);
    Check(w.Session.RequireCurrent(ticket) == item && w.Session.Count == 1, "duplicates do not churn identity");
});
Case("replicator-key-reuse-and-delayed-despawn", w =>
{
    var old = w.Track(); w.Session.Remove(old.Observation.Entity);
    Reject(() => w.Session.Record(old.Observation), "equipment.retired-life");
    var next = w.Track(w.Item(life: 2));
    Check(!w.Session.Remove(old.Observation.Entity), "late despawn cannot remove replacement");
    Reject(() => w.Session.RequireCurrent(old), "equipment.stale-instance");
    Check(w.Session.RequireCurrent(next).Entity.LifeEpoch == 2, "new life intact");
});
Case("world-reuse", w =>
{
    var old = w.Track(); w.Kernel.BeginWorld(8);
    Check(w.Session.Count == 0 && w.Session.IdentityHistoryCount == 0, "world flush");
    w.Kernel.Advance(0, true); var next = w.Track();
    Reject(() => w.Session.RequireCurrent(old), "equipment.stale-instance");
    Check(w.Session.RequireCurrent(next).Entity.WorldEpoch == 8, "new world");
});
Case("owner-respawn-and-live-owner-probe", w =>
{
    var old = w.Track(); w.OwnerCurrent = false;
    Reject(() => w.Session.RequireCurrent(old), "equipment.owner-not-current");
    w.OwnerCurrent = true;
    w.Session.Record(old.Observation with { Owner = w.Owner with { LifeEpoch = 2 } });
    Reject(() => w.Session.RequireCurrent(old), "equipment.observation-changed");
});
Case("slot-conflict-rejects-without-mutation", w =>
{
    var old = w.Track(); var reads = w.NativeReads;
    Reject(() => w.Session.Record(w.Item("b")), "equipment.slot-occupied");
    Check(w.Session.Count == 1 && w.Session.IdentityHistoryCount == 1, "no partial insert");
    Check(w.Session.RequireCurrent(old) == old.Observation, "old item stays valid");
});
Case("definition-cannot-change-inside-one-life", w =>
{
    var old = w.Track();
    Reject(() => w.Session.Record(old.Observation with { ResourceRevision = "r2" }), "equipment.definition-changed");
    Check(w.Session.RequireCurrent(old).ResourceRevision == "r1", "original revision retained");
});
Case("unwield-and-rewield-invalidates-use", w =>
{
    var old = w.Track(); w.Session.Record(old.Observation with { IsWielded = false });
    Check(w.Session.IsCurrent(old.Observation.Entity), "unwield is not destruction");
    Reject(() => w.Session.CaptureOwnedUse(old.Observation.Entity, w.Owner, true), "equipment.not-wielded");
    w.Session.Record(old.Observation);
    Reject(() => w.Session.RequireCurrent(old), "equipment.observation-changed");
});
Case("loading-is-not-ready-to-use", w =>
{
    var item = w.Item() with { IsReady = false, IsWielded = false }; w.Session.Record(item);
    Check(w.Session.IsCurrent(item.Entity), "loading instance exists");
    Reject(() => w.Session.CaptureOwnedUse(item.Entity, w.Owner, false), "equipment.not-loaded");
});
Case("drop-deploy-and-recall-change-preconditions", w =>
{
    var old = w.Track();
    w.Session.Record(old.Observation with { Location = EquipmentLocation.World, Owner = null, Slot = null, IsWielded = false });
    Check(w.Session.IsCurrent(old.Observation.Entity), "unowned world entity exists");
    Reject(() => w.Session.CaptureOwnedUse(old.Observation.Entity, w.Owner, false), "equipment.owner-mismatch");
    w.Session.Record(old.Observation with { Location = EquipmentLocation.Deployed, Slot = null, IsWielded = false });
    var deployed = w.Session.CaptureOwnedUse(old.Observation.Entity, w.Owner, false);
    Check(w.Session.RequireCurrent(deployed).Location == EquipmentLocation.Deployed, "deployed observation only");
    w.Session.Record(old.Observation);
    Reject(() => w.Session.RequireCurrent(deployed), "equipment.observation-changed");
    Reject(() => w.Session.RequireCurrent(old), "equipment.observation-changed");
});
Case("deploy-and-recall-keeps-one-life", w =>
{
    // A deployed sentry or barrier stays the same slot life; only its location and slot read back differently.
    var item = w.Item(); w.Track(item);
    w.Session.Record(item with { Location = EquipmentLocation.Deployed, Slot = null, IsWielded = false });
    Check(w.Session.IsCurrent(item.Entity) && w.Session.Count == 1, "deployed location kept the life");
    w.Session.Record(item);
    Check(w.Session.RequireCurrent(w.Session.CaptureOwnedUse(item.Entity, w.Owner, true)) == item,
        "recall restored the slot and wield precondition");
    Check(w.Session.Count == 1 && w.Session.IdentityHistoryCount == 1, "deploy and recall churned identity");
});
Case("native-probe-reread-and-fail-closed", w =>
{
    var ticket = w.Track(); int before = w.NativeReads; w.NativeCurrent = false;
    Check(!w.Session.TryResolve(ticket.Observation.Entity, out var result, out var code)
        && result == null && code == "equipment.native-not-current", "stale native rejected");
    Check(w.NativeReads > before, "resolution performs fresh probe");
});
Case("probe-exception-is-explicit", w =>
{
    var ticket = w.Track(); w.DuringProbe = () => throw new InvalidOperationException("synthetic probe error");
    Reject(() => w.Session.RequireCurrent(ticket), "equipment.probe-failed");
    Check(w.Session.Count == 1, "uncertain probe does not invent removal");
});
Case("probe-reentrant-record-and-dispose-rejected", w =>
{
    var ticket = w.Track(); w.DuringProbe = () => w.Session.Record(ticket.Observation);
    Reject(() => w.Session.RequireCurrent(ticket), "equipment.probe-reentrancy");
    w.DuringProbe = w.Session.Dispose;
    Reject(() => w.Session.RequireCurrent(ticket), "equipment.probe-reentrancy");
    w.DuringProbe = null; Check(w.Session.RequireCurrent(ticket) == ticket.Observation, "session remains attached");
});
Case("world-invalidated-during-native-probe", w =>
{
    var ticket = w.Track(); w.DuringProbe = () => w.Kernel.BeginWorld(8);
    Reject(() => w.Session.RequireCurrent(ticket), "equipment.not-authoritative-ready");
    w.DuringProbe = null; Check(w.Session.Count == 0, "old world was cleared");
});
Case("active-budget-preserves-existing-entry", w =>
{
    var old = w.Track();
    Reject(() => w.Session.Record(w.Item("b", "GearSpecial")), "equipment.active-budget");
    Check(w.Session.RequireCurrent(old) == old.Observation && w.Session.Count == 1, "atomic active refusal");
}, active: 1, history: 2);
Case("history-budget-never-evicts-retired-lives", w =>
{
    var a = w.Track(); w.Session.Remove(a.Observation.Entity);
    var b = w.Track(w.Item("b")); w.Session.Remove(b.Observation.Entity);
    Reject(() => w.Session.Record(w.Item("c")), "equipment.history-budget");
    Reject(() => w.Session.Record(a.Observation), "equipment.retired-life");
    var next = w.Track(w.Item(life: 2));
    Check(w.Session.RequireCurrent(next).Entity.LifeEpoch == 2 && w.Session.IdentityHistoryCount == 2,
        "known id can advance without evicting history");
}, active: 1, history: 2);
Case("live-replacement-needs-explicit-retirement", w =>
{
    var old = w.Track(); Reject(() => w.Session.Record(w.Item(life: 2)), "equipment.instance-still-live");
    Check(w.Session.RequireCurrent(old) == old.Observation, "no implicit despawn");
});
Case("not-ready-and-registration-window", w =>
{
    Reject(() => w.Session.Record(w.Item()), "equipment.not-authoritative-ready");
    Reject(() => new EquipmentIdentitySession(w.Kernel, RuntimeLogLevel.Off, _ => true, _ => true), "provider-conflict");
    w.Start(); var before = w.Kernel.ExportManifest();
    Reject(() => new EquipmentIdentitySession(w.Kernel, RuntimeLogLevel.Off, _ => true, _ => true), "registration-closed");
    Check(w.Kernel.ExportManifest() == before, "rejected registration is atomic");
}, start: false);
Case("stop-clears-and-dispose-is-idempotent", w =>
{
    var old = w.Track(); w.Kernel.StopRuntime();
    Check(w.Session.Count == 0 && !w.Session.IsCurrent(old.Observation.Entity), "stopped index cleared");
    w.Session.Dispose(); w.Session.Dispose();
    Check(!w.Session.Remove(old.Observation.Entity), "late cleanup is safe");
});
Case("failed-startup-denies-observations", w =>
{
    bool failed = false;
    try { w.Kernel.StartRuntime(() => throw new InvalidOperationException("synthetic startup failure")); }
    catch (InvalidOperationException) { failed = true; }
    Check(failed, "failure actually occurred");
    Reject(() => w.Session.Record(w.Item()), "equipment.not-authoritative-ready");
}, start: false);
Case("authority-loss-clears-observations", w =>
{
    var old = w.Track(); w.Kernel.Advance(1, false);
    Check(w.Session.Count == 0 && !w.Session.IsCurrent(old.Observation.Entity), "host loss is fail closed");
    Reject(() => w.Session.Record(w.Item()), "equipment.not-authoritative-ready");
});
Case("wrong-thread-does-not-mutate", w =>
{
    var old = w.Track();
    Reject(() => Task.Run(() => w.Session.Remove(old.Observation.Entity)).GetAwaiter().GetResult(), "wrong-thread");
    Reject(() => Task.Run(w.Session.Dispose).GetAwaiter().GetResult(), "wrong-thread");
    Check(w.Session.RequireCurrent(old) == old.Observation, "owning thread still works");
});
Case("foreign-session-ticket-rejected", w =>
{
    var ticket = w.Track(); using var other = new TestWorld(); other.Track();
    Reject(() => other.Session.RequireCurrent(ticket), "equipment.foreign-ticket");
});
Case("no-gameplay-claims-or-embedded-kernel", w =>
{
    using var manifest = JsonDocument.Parse(w.Kernel.ExportManifest());
    var root = manifest.RootElement;
    Check(root.GetProperty("registry").GetProperty("capabilities").EnumerateArray().Select(c => c.GetProperty("id").GetString())
        .SequenceEqual(new[] { ModuleDefinition.EquippedCapability, ModuleDefinition.UnequippedCapability }), "only the two observed wield triggers");
    Check(root.GetProperty("registry").GetProperty("bindings").EnumerateArray().All(b => b.GetProperty("role").GetString() == "observe"), "no executable bindings");
    Check(root.GetProperty("bindingSupport").EnumerateArray().All(s => s.GetProperty("verification").GetString() == "implementation-only"
        && s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).SequenceEqual(new[] { ModuleDefinition.WieldReadPermission })), "no game verification; exact read permission");
    Check(typeof(EquipmentIdentitySession).Assembly.GetType(typeof(RuntimeKernel).FullName!) == null, "single SDK");
});
Case("unknown-authority-denied-before-first-tick", w =>
{
    w.Kernel.StartRuntime(() => { });
    Reject(() => w.Session.Record(w.Item()), "equipment.not-authoritative-ready");
}, start: false);
var invalid = new (string Name, Func<EquipmentObservation, EquipmentObservation> Change, string Code)[]
{
    ("wrong-namespace", x => x with { Entity = x.Entity with { Id = "fixture.other:a" } }, "equipment.entity-namespace"),
    ("empty-instance-id", x => x with { Entity = x.Entity with { Id = "gtfo.equipment:" } }, "equipment.entity-namespace"),
    ("negative-life", x => x with { Entity = x.Entity with { LifeEpoch = -1 } }, "invalid-integer"),
    ("wrong-world", x => x with { Entity = x.Entity with { WorldEpoch = 6 } }, "equipment.stale-world"),
    ("missing-resource", x => x with { ResourceId = null! }, "equipment.resource-reference"),
    ("empty-revision", x => x with { ResourceRevision = "" }, "equipment.resource-reference"),
    ("missing-inventory-owner", x => x with { Owner = null }, "equipment.inventory-location"),
    ("old-owner-world", x => x with { Owner = x.Owner! with { WorldEpoch = 6 } }, "equipment.stale-owner-world"),
    ("self-owner", x => x with { Owner = x.Entity }, "equipment.self-owner"),
    ("missing-slot", x => x with { Slot = null }, "equipment.inventory-location"),
    ("world-with-slot", x => x with { Location = EquipmentLocation.World }, "equipment.non-inventory-location"),
    ("unloaded-wield", x => x with { IsReady = false }, "equipment.wielded-not-ready"),
    ("unknown-location", x => x with { Location = (EquipmentLocation)99 }, "equipment.location"),
    ("untrimmed-resource", x => x with { ResourceId = " fixture.rifle" }, "equipment.resource-reference"),
    ("unsafe-life", x => x with { Entity = x.Entity with { LifeEpoch = long.MaxValue } }, "invalid-integer")
};
foreach (var row in invalid)
    Case("invalid-" + row.Name, w =>
    {
        Reject(() => w.Session.Record(row.Change(w.Item())), row.Code);
        Check(w.NativeReads == 0 && w.Session.Count == 0 && w.Session.IdentityHistoryCount == 0,
            "invalid input rejected before probes or state changes");
    });
Console.WriteLine("RESULT " + JsonSerializer.Serialize(new
{
    verification = "managed-implementation-only", cases = passed.Count, assertions,
    testNames = passed, gameExecuted = false, installed = false, nativeCalls = 0
}));
