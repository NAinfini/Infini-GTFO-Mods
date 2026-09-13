using ForgeRuntime.Framework;
using ForgeWeapon;
using System.Text.Json;

// Independent consumer: compiled Weapon + compiled SDK, synthetic adapter observations.
var results = new List<object>(); int failures = 0;
void Test(string name, Action run)
{
    try { run(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; results.Add(new { name, status = "failed", error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Rejected(Action action, string? code = null)
{
    try { action(); }
    catch (RuntimeContractException e) { if (code != null) Check(e.Code == code, "Expected " + code + "; got " + e.Code); return; }
    throw new Exception("Expected rejection" + (code == null ? "" : ": " + code));
}
EntityReference Owner(string id = "a", long life = 1, long world = 7) => new("fixture.player:" + id, world, life);
EquipmentObservation Item(string id = "a", long life = 1, long world = 7) => new(
    new EntityReference("gtfo.equipment:" + id, world, life), "fixture.rifle", "r1",
    Owner(world: world), "GearStandard", EquipmentLocation.Inventory, true, true);
EquipmentUseTicket RecordAndCapture(Fixture f, EquipmentObservation item)
{ f.Session.Record(item); return f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true); }

Test("public-module-no-executable-claims", () =>
{
    using var f = new Fixture(); using var doc = JsonDocument.Parse(f.Kernel.ExportManifest());
    var r = doc.RootElement.GetProperty("registry");
    Check(r.GetProperty("providers").GetArrayLength() == 1, "provider duplicated");
    Check(r.GetProperty("capabilities").GetArrayLength() == 2 && r.GetProperty("bindings").EnumerateArray().All(b => b.GetProperty("role").GetString() == "observe"), "executable bindings advertised");
    Check(f.Kernel.QueuedEvents == 0 && f.Kernel.LoadedPlans == 0, "identity inspection started gameplay");
});
Test("live-use-revalidates-native-and-owner", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item());
    f.NativeCalls = f.OwnerCalls = 0; f.Session.RequireCurrent(t);
    Check(f.NativeCalls > 0 && f.OwnerCalls > 0, "live predicates bypassed");
});
Test("duplicate-observation-preserves-current-ticket", () =>
{
    using var f = new Fixture(); var item = Item(); var t = RecordAndCapture(f, item);
    f.Session.Record(item); f.Session.RequireCurrent(t);
});
Test("same-resource-two-instances-remain-distinct", () =>
{
    using var f = new Fixture(); var first = RecordAndCapture(f, Item());
    var secondItem = Item("b") with { Slot = "GearSpecial" }; var second = RecordAndCapture(f, secondItem);
    f.Session.RequireCurrent(first); f.Session.RequireCurrent(second);
    Check(first.Observation.Entity != second.Observation.Entity, "resource identity used as instance key");
});
Test("ABA-transfer-never-revives-old-ticket", () =>
{
    using var f = new Fixture(); var item = Item(); var old = RecordAndCapture(f, item);
    f.Session.Record(item with { Owner = Owner("b") }); f.Session.Record(item);
    Rejected(() => f.Session.RequireCurrent(old), "equipment.observation-changed");
    var current = f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true); f.Session.RequireCurrent(current);
    Check(current.Observation.Entity == old.Observation.Entity, "ownership transition changed lifeEpoch");
});
Test("old-owner-cannot-capture-transferred-equipment", () =>
{
    using var f = new Fixture(); var item = Item(); f.Session.Record(item with { Owner = Owner("b") });
    Rejected(() => f.Session.CaptureOwnedUse(item.Entity, Owner(), true));
    f.Session.RequireCurrent(f.Session.CaptureOwnedUse(item.Entity, Owner("b"), true));
});
Test("owner-life-change-revokes-ticket", () =>
{
    using var f = new Fixture(); var item = Item(); var old = RecordAndCapture(f, item);
    f.Session.Record(item with { Owner = Owner(life: 2) });
    Rejected(() => f.Session.RequireCurrent(old), "equipment.observation-changed");
});
Test("unwield-revokes-use-without-destroying-item", () =>
{
    using var f = new Fixture(); var item = Item(); var old = RecordAndCapture(f, item);
    f.Session.Record(item with { IsWielded = false });
    Rejected(() => f.Session.RequireCurrent(old), "equipment.observation-changed");
    Rejected(() => f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true));
    f.Session.RequireCurrent(f.Session.CaptureOwnedUse(item.Entity, item.Owner!, false));
});
Test("not-ready-loading-state-never-authorizes-use", () =>
{
    using var f = new Fixture(); var item = Item() with { IsReady = false, IsWielded = false }; f.Session.Record(item);
    Rejected(() => f.Session.CaptureOwnedUse(item.Entity, item.Owner!, false));
});
Test("definition-revision-change-rejected-atomically", () =>
{
    using var f = new Fixture(); var item = Item(); var t = RecordAndCapture(f, item);
    Rejected(() => f.Session.Record(item with { ResourceRevision = "r2" }), "equipment.definition-changed");
    f.Session.RequireCurrent(t);
});
Test("occupied-slot-rejected-without-harming-incumbent", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item());
    Rejected(() => f.Session.Record(Item("b")), "equipment.slot-occupied"); f.Session.RequireCurrent(t);
});
Test("live-instance-cannot-be-replaced-by-new-life-implicitly", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item());
    Rejected(() => f.Session.Record(Item(life: 2)), "equipment.instance-still-live"); f.Session.RequireCurrent(t);
});
Test("native-disappearance-rejects-captured-use", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.NativeAlive = false;
    Rejected(() => f.Session.RequireCurrent(t));
});
Test("owner-disappearance-rejects-captured-use", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.OwnerAlive = false;
    Rejected(() => f.Session.RequireCurrent(t));
});
Test("native-predicate-exception-is-not-success", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item());
    f.NativeHook = _ => throw new InvalidOperationException("synthetic native reader failure");
    Rejected(() => f.Session.RequireCurrent(t));
});
Test("owner-predicate-exception-is-not-success", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item());
    f.OwnerHook = _ => throw new InvalidOperationException("synthetic owner reader failure");
    Rejected(() => f.Session.RequireCurrent(t));
});
Test("world-change-invalidates-old-ticket", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.Kernel.BeginWorld(8); f.Kernel.Advance(0, true);
    Rejected(() => f.Session.RequireCurrent(t)); Rejected(() => f.Session.Record(Item()));
    f.Session.RequireCurrent(RecordAndCapture(f, Item(world: 8)));
});
Test("stop-invalidates-ticket-and-dispose-is-idempotent", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.Kernel.StopRuntime();
    Rejected(() => f.Session.RequireCurrent(t)); f.Session.Dispose(); f.Session.Dispose();
});
Test("disposed-session-cannot-revalidate-use", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.Session.Dispose();
    Rejected(() => f.Session.RequireCurrent(t));
});
Test("ticket-cannot-cross-session-instances", () =>
{
    using var a = new Fixture(); using var b = new Fixture();
    var ticket = RecordAndCapture(a, Item()); RecordAndCapture(b, Item());
    Rejected(() => b.Session.RequireCurrent(ticket));
});
Test("client-authority-rejects-owned-use", () =>
{
    using var f = new Fixture(host: false); var item = Item();
    Rejected(() => { f.Session.Record(item); f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true); });
});
Test("record-before-ready-is-rejected", () =>
{
    using var f = new Fixture(start: false); Rejected(() => f.Session.Record(Item()));
    f.Start(true); f.Session.RequireCurrent(RecordAndCapture(f, Item()));
});
Test("resolver-cannot-transfer-and-return-stale-success", () =>
{
    using var f = new Fixture(); var item = Item(); var t = RecordAndCapture(f, item);
    f.NativeHook = _ => { f.Session.Record(item with { Owner = Owner("b") }); return true; };
    Rejected(() => f.Session.RequireCurrent(t));
});
Test("resolver-cannot-change-world-and-return-stale-success", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item());
    f.NativeHook = _ => { f.Kernel.BeginWorld(8); return true; }; Rejected(() => f.Session.RequireCurrent(t));
});
Test("simulation-thread-confinement", () =>
{
    using var f = new Fixture(); var t = RecordAndCapture(f, Item()); Exception? error = null;
    var thread = new Thread(() => { try { f.Session.RequireCurrent(t); } catch (Exception e) { error = e; } });
    thread.Start(); thread.Join(); Check(error is RuntimeContractException, "off-thread ticket validation accepted");
});

Test("same-slot-new-life-does-not-revive-old-ticket", () =>
{
    using var f = new Fixture(); var item = Item(); var old = RecordAndCapture(f, item);
    Check(f.Session.Remove(item.Entity), "explicit retire failed");
    var next = RecordAndCapture(f, Item(life: 2));
    Rejected(() => f.Session.RequireCurrent(old)); f.Session.RequireCurrent(next);
});
Test("late-remove-does-not-delete-replacement", () =>
{
    using var f = new Fixture(); var item = Item(); RecordAndCapture(f, item); f.Session.Remove(item.Entity);
    var next = RecordAndCapture(f, Item(life: 2));
    Check(!f.Session.Remove(item.Entity), "old-life removal matched new instance"); f.Session.RequireCurrent(next);
});
Test("retired-and-older-lives-cannot-return", () =>
{
    using var f = new Fixture(); var item = Item(life: 3); RecordAndCapture(f, item); f.Session.Remove(item.Entity);
    Rejected(() => f.Session.Record(item), "equipment.retired-life");
    Rejected(() => f.Session.Record(Item(life: 2)), "equipment.retired-life");
});
Test("retire-is-idempotent-and-frees-slot", () =>
{
    using var f = new Fixture(); var item = Item(); RecordAndCapture(f, item);
    Check(f.Session.Remove(item.Entity) && !f.Session.Remove(item.Entity), "retire not idempotent");
    f.Session.RequireCurrent(RecordAndCapture(f, Item("b")));
});
Test("active-budget-failure-does-not-poison-new-identity", () =>
{
    using var f = new Fixture(maxActive: 1, maxHistory: 2); var first = RecordAndCapture(f, Item());
    var second = Item("b") with { Slot = "GearSpecial" };
    Rejected(() => f.Session.Record(second), "equipment.active-budget"); f.Session.RequireCurrent(first);
    f.Session.Remove(first.Observation.Entity); f.Session.RequireCurrent(RecordAndCapture(f, second));
});
Test("history-budget-never-evicts-replay-protection", () =>
{
    using var f = new Fixture(maxActive: 1, maxHistory: 1); var first = RecordAndCapture(f, Item()); f.Session.Remove(first.Observation.Entity);
    Rejected(() => f.Session.Record(Item("b")), "equipment.history-budget");
    Rejected(() => f.Session.Record(Item()), "equipment.retired-life");
    f.Session.RequireCurrent(RecordAndCapture(f, Item(life: 2)));
});
Test("owner-world-mismatch-rejected-before-state-change", () =>
{
    using var f = new Fixture(); var item = Item(); var ticket = RecordAndCapture(f, item);
    Rejected(() => f.Session.Record(item with { Owner = Owner(world: 8) }), "equipment.stale-owner-world");
    f.Session.RequireCurrent(ticket);
});
Test("foreign-entity-namespace-not-cast-to-equipment", () =>
{
    using var f = new Fixture(); var item = Item() with { Entity = new EntityReference("fixture.player:a", 7, 1) };
    Rejected(() => f.Session.Record(item), "equipment.entity-namespace");
});
Test("world-item-does-not-infer-an-owner", () =>
{
    using var f = new Fixture(); var item = Item() with { Owner = null, Slot = null, Location = EquipmentLocation.World, IsWielded = false };
    f.Session.Record(item); Check(f.Session.TryResolve(item.Entity, out var observed, out _), "world observation lost");
    Check(observed!.Owner == null, "invented owner for world item");
    Rejected(() => f.Session.CaptureOwnedUse(item.Entity, Owner(), false));
});
Test("deployed-item-not-accepted-as-inventory-weapon", () =>
{
    using var f = new Fixture(); var item = Item() with { Slot = null, Location = EquipmentLocation.Deployed, IsWielded = false };
    f.Session.Record(item); Check(f.Session.TryResolve(item.Entity, out var observed, out _), "deployment observation lost");
    Check(observed!.Location == EquipmentLocation.Deployed, "deployment coerced into inventory");
    Rejected(() => f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true));
});
Test("loss-of-host-clears-observations", () =>
{
    using var f = new Fixture(); var ticket = RecordAndCapture(f, Item()); f.Kernel.Advance(1, false);
    Check(f.Session.Count == 0, "authority loss retained live observations"); Rejected(() => f.Session.RequireCurrent(ticket));
});
Test("owner-probe-cannot-dispose-and-return-current-use", () =>
{
    using var f = new Fixture(); var ticket = RecordAndCapture(f, Item());
    f.OwnerHook = _ => { f.Session.Dispose(); return true; }; Rejected(() => f.Session.RequireCurrent(ticket));
});

Console.WriteLine($"INDEPENDENT IDENTITY: {results.Count - failures}/{results.Count} passed; gameExecuted=false; synthetic inputs");
if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Expected --report NEW.json");
    using var stream = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write);
    JsonSerializer.Serialize(stream, new { verification = "compiled-identity-sdk-consumer", gameExecuted = false, installed = false, failures, tests = results }, new JsonSerializerOptions { WriteIndented = true });
}
return failures == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    public RuntimeKernel Kernel { get; } = new(new("forge.weapon.acceptance", "0.1.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
    public EquipmentIdentitySession Session { get; }
    public bool NativeAlive = true, OwnerAlive = true;
    public int NativeCalls, OwnerCalls;
    public Func<EquipmentObservation, bool>? NativeHook;
    public Func<EntityReference, bool>? OwnerHook;
    public Fixture(bool host = true, bool start = true, int maxActive = 1024, int maxHistory = 8192)
    {
        Kernel.BeginWorld(7);
        Session = new EquipmentIdentitySession(Kernel,
            e => { NativeCalls++; return NativeHook?.Invoke(e) ?? NativeAlive; },
            e => { OwnerCalls++; return OwnerHook?.Invoke(e) ?? OwnerAlive; }, maxActive, maxHistory);
        if (start) Start(host);
    }
    public void Start(bool host) { Kernel.StartRuntime(() => { }); Kernel.Advance(0, host); }
    public void Dispose() { Session.Dispose(); Kernel.StopRuntime(); }
}
