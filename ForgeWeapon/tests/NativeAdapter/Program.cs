using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon;
using ForgeWeapon.Native;
using Player;
using SNetwork;

// Production WeaponNativeSession / EquipmentNativeAdapter / hook postfixes + compiled Weapon + compiled SDK.
// Native state is a managed double: every exit case below is synthetic and NOT game-verified.
if (args.Length != 1) { Console.Error.WriteLine("Usage: NativeAdapter <report.json>"); return 2; }
var checks = new List<object>(); int passed = 0, failed = 0;
void Case(string name, Action test)
{
    try { test(); passed++; checks.Add(new { name, passed = true }); }
    catch (Exception error) { failed++; checks.Add(new { name, passed = false, error = error.ToString() }); Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
    finally { World.Cleanup(); }
}
void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }
void Throws(string? code, Action action)
{
    try { action(); }
    catch (RuntimeContractException error) when (code != null) { Require(error.Code == code, "Expected " + code + "; got " + error.Code); return; }
    catch (Exception) when (code == null) { return; }
    throw new Exception("Expected rejection " + code);
}
void Hook(Type hook, object instance)
    => hook.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new[] { instance });
var stored = typeof(BackpackItemStored);

Case("session.registration-before-hooks", () =>
{
    var w = new World(start: false); string? manifest = null; WeaponNativeSession? current = null;
    w.DuringInstall = () => { manifest = w.Kernel.ExportManifest(); current = WeaponNativeSession.Current; };
    var session = w.StartSession();
    Require(w.Installs == 1 && ReferenceEquals(current, session), "Hooks ran before the session was current.");
    Require(manifest != null && manifest.Contains(ModuleDefinition.EquippedBinding), "Hooks ran before provider registration.");
});
Case("session.duplicate-provider-before-hooks", () =>
{
    var w = new World(start: false); var other = w.Kernel.RegisterModule(ModuleDefinition.Create());
    string before = w.Kernel.ExportManifest();
    Throws("provider-conflict", () => w.StartSession());
    Require(w.Installs == 0 && w.Removes == 0 && WeaponNativeSession.Current == null && w.Kernel.ExportManifest() == before,
        "Duplicate registration touched hooks or existing ownership.");
    other.Dispose();
});
Case("session.install-failure-rolls-back", () =>
{
    var w = new World(start: false); string before = w.Kernel.ExportManifest(); var primary = new IOException("patch");
    try { w.StartSession(install: () => throw primary); throw new Exception("Expected install failure."); }
    catch (IOException error) { Require(ReferenceEquals(error, primary), "Install failure was replaced."); }
    Require(w.Removes == 1 && WeaponNativeSession.Current == null && w.Kernel.ExportManifest() == before, "Partial registration or hooks survived.");
    w.StartSession(); Require(WeaponNativeSession.Current != null && w.Installs == 1, "Rolled-back provider could not register again.");
});
Case("session.cleanup-failure-preserves-cause", () =>
{
    var w = new World(start: false); string before = w.Kernel.ExportManifest(); var primary = new IOException("patch");
    try { w.StartSession(() => throw primary, () => throw new InvalidOperationException("unpatch")); }
    catch (IOException error) when (ReferenceEquals(error, primary))
    {
        Require(error.Data.Contains("ForgeWeapon.CleanupFailures") && WeaponNativeSession.Current == null
            && w.Kernel.ExportManifest() == before, "Cleanup failure hid the cause or skipped provider cleanup.");
        return;
    }
    throw new Exception("Expected startup failure.");
});
Case("session.late-registration-rejected", () =>
{
    var w = new World(start: false); w.Kernel.StartRuntime(() => { });
    Throws(null, () => w.StartSession());
    Require(w.Installs == 0 && WeaponNativeSession.Current == null, "Late registration touched hooks.");
});
Case("module.exact-observe-claims", () =>
{
    var w = new World();
    using var doc = JsonDocument.Parse(w.Kernel.ExportManifest()); var root = doc.RootElement;
    var bindings = root.GetProperty("registry").GetProperty("bindings").EnumerateArray()
        .Where(b => b.GetProperty("providerId").GetString() == ModuleDefinition.ProviderId).ToArray();
    Require(bindings.Select(b => b.GetProperty("id").GetString()).SequenceEqual(new[] { ModuleDefinition.EquippedBinding, ModuleDefinition.UnequippedBinding }), "Unexpected Weapon bindings.");
    Require(bindings.All(b => b.GetProperty("role").GetString() == "observe"), "Weapon claimed an executable binding.");
    var support = root.GetProperty("bindingSupport").EnumerateArray()
        .Where(s => s.GetProperty("bindingId").GetString()!.StartsWith(ModuleDefinition.ProviderId + ".", StringComparison.Ordinal)).ToArray();
    Require(support.Length == 2 && support.All(s => s.GetProperty("verification").GetString() == "implementation-only"), "Weapon claimed game verification.");
});
Case("capture.initial-snapshot-has-no-history", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    p.Inventory.WieldedItem = (ItemEquippable)item.Instance!;
    Hook(stored, p.Backpack);
    var entity = w.EntityOf(item.Instance!);
    Require(entity != null && entity.Id == "gtfo.equipment:7.1" && entity.WorldEpoch == 7 && entity.LifeEpoch == 1, "Stored item was not recorded.");
    Require(w.Session!.Identity.TryResolve(entity!, out var o, out var code), "Recorded item is not current: " + code);
    Require(o!.ResourceId == "gtfo.gear:1234" && o.ResourceRevision == EquipmentNativeAdapter.ResourceRevision && o.Owner == p.Reference
        && o.Slot == "GearStandard" && o.Location == EquipmentLocation.Inventory && o.IsReady && o.IsWielded, "Readback fields differ.");
    w.Tick(); Require(w.Sink.Count == 0, "First sighting synthesized a wield fact.");
});
Case("capture.item-without-gear-range-uses-item-id", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.ResourcePack, 0);
    item.GearIDRange = null; item.ItemID = 102; Hook(stored, p.Backpack);
    Require(w.Session!.Identity.TryResolve(w.EntityOf(item.Instance!)!, out var o, out _) && o!.ResourceId == "gtfo.item:102"
        && o.Slot == "ResourcePack", "Non-gear item resource identity differs.");
});
Case("wield.observed-flip-publishes-equipped-then-unequipped", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
    p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(typeof(LocalItemWielded), p.Inventory); w.Tick();
    Require(w.Sink.Count == 1 && w.Sink[0].EventId.StartsWith("gtfo.equipment.equipped:7:", StringComparison.Ordinal)
        && w.Sink[0].Target == entity && w.Sink[0].Actor == p.Reference, "Equipped fact missing or wrong.");
    Hook(typeof(LocalItemWielded), p.Inventory); w.Tick(); Require(w.Sink.Count == 1, "Unchanged readback republished.");
    p.Inventory.WieldedItem = null; Hook(typeof(LocalItemUnwielded), p.Inventory); w.Tick();
    Require(w.Sink.Count == 2 && w.Sink[1].EventId.StartsWith("gtfo.equipment.unequipped:7:", StringComparison.Ordinal)
        && w.Sink[1].Target == entity && w.Sink[1].Actor == p.Reference && w.Sink[1].EventId != w.Sink[0].EventId, "Unequipped fact missing or wrong.");
    Require(w.EntityOf(item.Instance!) == entity, "Wield changed the equipment life.");
});
Case("wield.synced-inventory-path", () =>
{
    var w = new World(); var p = w.Player("remote", synced: true); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 77);
    Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
    p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(typeof(SyncedItemEquipped), p.Inventory); w.Tick();
    p.Inventory.WieldedItem = null; Hook(typeof(SyncedItemUnwielded), p.Inventory); w.Tick();
    Require(w.Sink.Count == 2 && w.Sink[0].EventId.StartsWith("gtfo.equipment.equipped:", StringComparison.Ordinal)
        && w.Sink[1].EventId.StartsWith("gtfo.equipment.unequipped:", StringComparison.Ordinal)
        && w.Sink.All(s => s.Actor == p.Reference && s.Target == entity), "Synced inventory facts differ.");
});
Case("wield.unobserved-change-is-stale-not-history", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
    p.Inventory.WieldedItem = (ItemEquippable)item.Instance!;
    Require(!w.Current(entity), "Unobserved native change still resolved as the recorded observation.");
    w.Tick(); Require(w.Sink.Count == 0, "Unobserved change produced a fact.");
});
Case("wield.destroyed-agent-inventory-ignored", () =>
{
    var w = new World(); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    p.Agent.Destroyed = true; Hook(typeof(LocalItemWielded), p.Inventory);
    Require(w.Session!.Identity.Count == 0 && !w.Session.Faulted, "Destroyed agent was read or faulted the session.");
});
Case("exit.same-slot-new-weapon", () =>
{
    var w = new World(); var p = w.Player("a"); var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    p.Inventory.WieldedItem = (ItemEquippable)first.Instance!; Hook(stored, p.Backpack); var old = w.EntityOf(first.Instance!)!;
    p.Backpack.Slots![(int)InventorySlot.GearStandard] = null; first.Instance!.Destroyed = true; p.Inventory.WieldedItem = null;
    Hook(typeof(BackpackSlotCleared), p.Backpack);
    Require(!w.Current(old) && w.Session!.Identity.Count == 0, "Cleared slot kept the old life.");
    var next = World.Put(p.Backpack, InventorySlot.GearStandard, 5678); p.Inventory.WieldedItem = (ItemEquippable)next.Instance!;
    Hook(stored, p.Backpack); var replacement = w.EntityOf(next.Instance!)!;
    Require(replacement.Id != old.Id && w.Current(replacement) && !w.Current(old), "Replacement reused or revived the old life.");
    w.Tick(); Require(w.Sink.Count == 0, "Replacement synthesized unequipped/equipped history.");
});
Case("exit.same-slot-overwrite-without-clear-hook", () =>
{
    var w = new World(); var p = w.Player("a"); var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); var old = w.EntityOf(first.Instance!)!;
    var next = World.Put(p.Backpack, InventorySlot.GearStandard, 1234); Hook(stored, p.Backpack);
    var replacement = w.EntityOf(next.Instance!)!;
    Require(replacement.Id != old.Id && !w.Current(old) && w.Current(replacement) && w.Session!.Identity.Count == 1, "Overwritten slot kept the old life.");
    w.Tick(); Require(w.Sink.Count == 0, "Overwrite synthesized history.");
});
Case("exit.same-resource-multiple-instances", () =>
{
    var w = new World(); var a = w.Player("a"); var b = w.Player("b");
    var items = new[] { World.Put(a.Backpack, InventorySlot.GearStandard, 1234), World.Put(a.Backpack, InventorySlot.GearSpecial, 1234),
        World.Put(b.Backpack, InventorySlot.GearStandard, 1234) };
    Hook(stored, a.Backpack); Hook(stored, b.Backpack);
    var refs = items.Select(i => w.EntityOf(i.Instance!)!).ToArray();
    Require(refs.Select(r => r.Id).Distinct().Count() == 3 && refs.All(w.Current), "Same definition collapsed instances.");
    Require(refs.Select(r => { w.Session!.Identity.TryResolve(r, out var o, out _); return o!.ResourceId; }).Distinct().Single() == "gtfo.gear:1234",
        "Resource identity diverged.");
});
Case("exit.transfer-retires-old-owner-without-history", () =>
{
    var w = new World(); var a = w.Player("a"); var b = w.Player("b");
    var item = World.Put(a.Backpack, InventorySlot.GearStandard, 1234); a.Inventory.WieldedItem = (ItemEquippable)item.Instance!;
    Hook(stored, a.Backpack); var old = w.EntityOf(item.Instance!)!;
    a.Backpack.Slots![(int)InventorySlot.GearStandard] = null; a.Inventory.WieldedItem = null; Hook(typeof(BackpackSlotCleared), a.Backpack);
    World.Put(b.Backpack, InventorySlot.GearStandard, 1234, item.Instance); Hook(stored, b.Backpack);
    var next = w.EntityOf(item.Instance!)!;
    Require(next.Id != old.Id && !w.Current(old) && w.Current(next), "Transfer kept the old owner's life.");
    Require(w.Session!.Identity.TryResolve(next, out var o, out _) && o!.Owner == b.Reference && !o.IsWielded, "New owner readback differs.");
    Throws("equipment.stale-instance", () => w.Session.Identity.CaptureOwnedUse(old, a.Reference, false));
    w.Tick(); Require(w.Sink.Count == 0, "Transfer history was reconstructed.");
});
Case("exit.world-switch-clears-and-reobserves", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); var old = w.EntityOf(item.Instance!)!;
    w.Kernel.BeginWorld(8); w.Tick();
    Require(w.Session!.Identity.Count == 0 && !w.Current(old), "Old world observation survived.");
    var reissued = new EntityReference("fixture.player:a", 8, 1);
    w.PlayerRefs[p.Net] = reissued; w.LivePlayers.Clear(); w.LivePlayers.Add(reissued);
    Hook(stored, p.Backpack); var next = w.EntityOf(item.Instance!)!;
    Require(next.WorldEpoch == 8 && next.Id == "gtfo.equipment:8.1" && w.Current(next), "New world did not re-observe.");
    w.Tick(); Require(w.Sink.Count == 0, "World switch produced wield history.");
});
Case("exit.stale-pointer-reuse-is-a-new-life", () =>
{
    var w = new World(); var p = w.Player("a"); var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); var old = w.EntityOf(first.Instance!)!; var pointer = first.Instance!.Pointer;
    first.Instance.Destroyed = true; Hook(typeof(BackpackSlotCleared), p.Backpack);
    Require(!w.Current(old) && w.Session!.Identity.Count == 0, "Destroyed instance stayed current.");
    var reused = new ItemEquippable { Pointer = pointer }; World.Put(p.Backpack, InventorySlot.GearStandard, 1234, reused);
    Hook(stored, p.Backpack); var next = w.EntityOf(reused)!;
    Require(next.Id != old.Id && w.Current(next) && !w.Current(old), "Reused native pointer revived the old life.");
});
Case("exit.stale-pointer-reuse-without-clear-hook", () =>
{
    var w = new World(); var p = w.Player("a"); var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); var old = w.EntityOf(first.Instance!)!;
    first.Instance!.Destroyed = true;
    Require(!w.Current(old), "Destroyed instance resolved before any hook.");
    var reused = new ItemEquippable { Pointer = first.Instance!.Pointer }; World.Put(p.Backpack, InventorySlot.GearStandard, 1234, reused);
    Hook(stored, p.Backpack); var next = w.EntityOf(reused)!;
    Require(next.Id != old.Id && !w.Current(old) && w.Current(next), "New BackpackItem at a reused pointer kept the old life.");
});
Case("exit.destroy-all-instances", () =>
{
    var w = new World(); var p = w.Player("a");
    var items = new[] { World.Put(p.Backpack, InventorySlot.GearStandard, 1), World.Put(p.Backpack, InventorySlot.GearMelee, 2) };
    Hook(stored, p.Backpack); var refs = items.Select(i => w.EntityOf(i.Instance!)!).ToArray();
    foreach (var item in items) item.Instance!.Destroyed = true;
    Hook(typeof(BackpackInstancesDestroyed), p.Backpack);
    Require(w.Session!.Identity.Count == 0 && w.Session.Adapter.TrackedCount == 0 && !refs.Any(w.Current), "Destroyed instances stayed current.");
    w.Tick(); Require(w.Sink.Count == 0, "Destruction synthesized history.");
});
Case("owner.unresolved-player-not-recorded", () =>
{
    var w = new World(); var p = w.Player("a", resolved: false); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); Hook(stored, p.Backpack);
    Require(w.Session!.Identity.Count == 0 && w.Session.Adapter.TrackedCount == 0, "Unresolved owner produced equipment identity.");
    Require(w.Reports.Count(r => r.StartsWith("weapon.owner-unresolved", StringComparison.Ordinal)) == 1 && !w.Session.Faulted,
        "Unresolved owner was not reported exactly once.");
});
Case("authority.client-or-gated-observation-ignored", () =>
{
    var w = new World(); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    SNet.IsMaster = false; Hook(stored, p.Backpack); Require(w.Session!.Identity.Count == 0, "Non-master recorded equipment.");
    SNet.IsMaster = true; w.CanExecute = false; Hook(stored, p.Backpack); Require(w.Session.Identity.Count == 0, "Gated host recorded equipment.");
    w.CanExecute = true; w.Tick(host: false); Hook(stored, p.Backpack); Require(w.Session.Identity.Count == 0, "Client tick recorded equipment.");
});
Case("contract.rejected-observation-does-not-fault", () =>
{
    var w = new World(); var p = w.Player("a"); w.LivePlayers.Clear(); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack);
    Require(w.Reports.Contains("weapon.observation-rejected: equipment.owner-not-current") && !w.Session!.Faulted
        && w.Session.Adapter.TrackedCount == 0 && w.Session.Identity.Count == 0, "Rejected observation faulted or leaked a handle.");
    w.LivePlayers.Add(p.Reference); Hook(stored, p.Backpack);
    Require(w.Session!.Identity.Count == 1, "A rejection latched observation off.");
});
Case("contract.unloaded-wielded-item-rejected", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    item.IsLoaded = false; p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(stored, p.Backpack);
    Require(w.Reports.Contains("weapon.observation-rejected: equipment.wielded-not-ready") && w.Session!.Identity.Count == 0
        && !w.Session.Faulted, "Wielded unloaded item was recorded.");
});
Case("guard.unexpected-native-failure-latches", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
    p.Backpack.ThrowOnSlots = true; Hook(typeof(BackpackSlotCleared), p.Backpack);
    Require(w.Session!.Faulted && w.Reports.Count(r => r.StartsWith("Weapon equipment observation disabled", StringComparison.Ordinal)) == 1,
        "Unexpected failure did not latch exactly once.");
    p.Backpack.ThrowOnSlots = false; Hook(stored, p.Backpack);
    Require(w.Session.Adapter.TrackedCount == 0 && !w.Current(entity) && w.Reports.Count == 1, "Faulted session kept observing native equipment.");
});
Case("session.dispose-unregisters-then-unhooks", () =>
{
    var w = new World(); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearStandard, 1234); Hook(stored, p.Backpack);
    var session = w.Session!; w.Kernel.StopRuntime(); session.Dispose();
    using var doc = JsonDocument.Parse(w.Kernel.ExportManifest());
    Require(w.Removes == 1 && WeaponNativeSession.Current == null && !doc.RootElement.GetProperty("registry").GetProperty("providers")
        .EnumerateArray().Any(x => x.GetProperty("id").GetString() == ModuleDefinition.ProviderId), "Dispose leaked registration or hooks.");
    Hook(stored, p.Backpack); session.Dispose();
    Require(w.Removes == 1 && WeaponNativeSession.Current == null, "Dispose was not idempotent or a hook revived the session.");
});

var result = new { verification = "production-native-weapon-sources-with-managed-game-doubles", gameExecuted = false,
    multiplayerExecuted = false, gameVerified = false, passed, failed, checks };
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {passed}/{passed + failed} Weapon native adapter cases; managed doubles, no GTFO execution.");
return failed == 0 ? 0 : 1;

sealed class World : IDisposable
{
    internal const string Consumer = "fixture.consumer";
    internal const string Sink_ = Consumer + ".sink";
    internal const string SinkBinding = Consumer + ".binding.sink";
    internal const string RecordPermission = "fixture.consumer.record";
    private static readonly List<World> Live = new();
    private static readonly string[] PortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };
    internal RuntimeKernel Kernel { get; } = new(new("fixture.weapon.native", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
    internal WeaponNativeSession? Session { get; private set; }
    internal readonly Dictionary<SNet_Player, EntityReference> PlayerRefs = new();
    internal readonly HashSet<EntityReference> LivePlayers = new();
    internal readonly List<(string EventId, EntityReference Target, EntityReference Actor)> Sink = new();
    internal readonly List<string> Reports = new();
    internal int Installs, Removes;
    internal bool CanExecute = true;
    internal Action? DuringInstall;
    private readonly RuntimeModuleHandle players, consumer;
    private long tick;
    private bool disposed;

    internal sealed record Fixture(SNet_Player Net, PlayerAgent Agent, PlayerInventoryBase Inventory, PlayerBackpack Backpack, EntityReference Reference);

    internal World(bool start = true)
    {
        Live.Add(this);
        SNet.IsMaster = true;
        Kernel.BeginWorld(7);
        players = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = "fixture.players", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>> { ["fixture.player"] = r => LivePlayers.Contains(r) }));
        consumer = Kernel.RegisterModule(ConsumerModule());
        if (!start) return;
        StartSession();
        Kernel.StartRuntime(() => Kernel.LoadPlan(Plan(), new[] { ModuleDefinition.WieldReadPermission, RecordPermission }));
        Kernel.Advance(0, true);
    }

    internal WeaponNativeSession StartSession(Action? install = null, Action? remove = null)
        => Session = WeaponNativeSession.Start(Kernel, () => CanExecute,
            new WeaponPlayerReferences(p => PlayerRefs.TryGetValue(p, out var r) ? r : null, r => LivePlayers.Contains(r)),
            Reports.Add, install ?? (() => { Installs++; DuringInstall?.Invoke(); }), remove ?? (() => Removes++));

    internal void Tick(bool host = true) => Kernel.Advance(++tick, host);

    internal Fixture Player(string id, bool synced = false, bool resolved = true)
    {
        var net = new SNet_Player(); var agent = new PlayerAgent { Owner = net };
        PlayerInventoryBase inventory = synced ? new PlayerInventorySynced() : new PlayerInventoryLocal();
        inventory.Owner = agent; agent.Inventory = inventory; net.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
        var backpack = new PlayerBackpack { Owner = net }; PlayerBackpackManager.Backpacks[net] = backpack;
        var reference = new EntityReference("fixture.player:" + id, Kernel.WorldEpoch, 1);
        if (resolved) { PlayerRefs[net] = reference; LivePlayers.Add(reference); }
        return new(net, agent, inventory, backpack, reference);
    }

    internal static BackpackItem Put(PlayerBackpack backpack, InventorySlot slot, uint checksum, Item? instance = null)
    {
        var item = new BackpackItem { Instance = instance ?? new ItemEquippable(), GearIDRange = new Gear.GearIDRange { Checksum = checksum } };
        backpack.Slots![(int)slot] = item; return item;
    }

    internal EntityReference? EntityOf(Item item) => Session!.Adapter.EntityOf(item);
    internal bool Current(EntityReference? reference) => reference != null && Session!.Identity.IsCurrent(reference);

    private CommandResult Record(CommandContext context)
    {
        Sink.Add((context.EventId, context.GetEntityInput("target")!, context.GetEntityInput("actor")!));
        return CommandResult.Succeeded(RuntimeJson.From(new { fixtureOnly = true }));
    }

    private RuntimeModule ConsumerModule() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = Consumer, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = new[] { new { id = Sink_, owner = Consumer, kind = "action", label = "Fixture wield sink", version = "1.0.0", parameters = new { },
            graph = new
            {
                domains = new[] { "weapon" }, execution = "host",
                inputs = new[] { new { id = "enter", type = "execution" }, new { id = "target", type = "entity" }, new { id = "actor", type = "entity" } },
                outputs = new[] { new { id = "result", type = "result", schema = "fixture.consumer.result" } }, parameters = Array.Empty<object>(),
                recipients = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" }
            } } },
        bindings = new[] { new { id = SinkBinding, capabilityId = Sink_, providerId = Consumer, handler = Sink_, role = "execute",
            status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() } }
    }).GetRawText(), new Dictionary<string, CommandHandler> { [Sink_] = Record },
        new[] { new BindingSupport(SinkBinding, "implementation-only", new[] { RecordPermission }) });

    private JsonElement Graph(string id) => RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry").GetProperty("capabilities")
        .EnumerateArray().Single(c => c.GetProperty("id").GetString() == id).GetProperty("graph");
    private static object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new
    {
        index, type = Array.IndexOf(PortTypes, p.GetProperty("type").GetString()!),
        cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0, valueSet = -1, lifetime = -1,
        optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
    }).ToArray();
    private static object Layout(JsonElement graph) => new { inputs = Slots(graph.GetProperty("inputs")), outputs = Slots(graph.GetProperty("outputs")),
        constants = Array.Empty<object>(), promoted = Array.Empty<int>() };
    private static int Slot(JsonElement ports, string name) => ports.EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == name).i;

    private string Plan()
    {
        var sink = Graph(Sink_);
        object Binding(string bindingId, string capabilityId, string providerId, string providerVersion, string handler)
            => new { bindingId, capabilityId, capabilityVersion = "1.0.0", providerId, providerVersion, handler };
        object Entry(string name, int binding, JsonElement trigger) => new
        {
            nodeId = name, binding, layout = Layout(trigger),
            steps = new[] { new { nodeId = name + "_sink", binding = 0, layout = Layout(sink), inputs = new[]
            {
                new { slot = Slot(sink.GetProperty("inputs"), "target"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "equipment") },
                new { slot = Slot(sink.GetProperty("inputs"), "actor"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "actor") }
            } } }
        };
        return RuntimeJson.From(new
        {
            schemaVersion = 2, kind = "forge-runtime-plan", planId = "fixture.weapon.plan", resource = new { id = "fixture.resource", revision = "r1" },
            runtime = Kernel.Identity, domain = "weapon", authority = "host", failurePolicy = "stop-entrypoint",
            permissions = new[] { RecordPermission, ModuleDefinition.WieldReadPermission }, dependencies = Array.Empty<string>(), // ordinal-sorted
            limits = new { maxEventsPerTick = 16, maxCommandsPerTick = 16, maxQueuedEvents = 16, maxCausalDepth = 4 },
            // Plan binding locks are ordinal-sorted by bindingId.
            bindings = new[]
            {
                Binding(SinkBinding, Sink_, Consumer, "1.0.0", Sink_),
                Binding(ModuleDefinition.EquippedBinding, ModuleDefinition.EquippedCapability, ModuleDefinition.ProviderId, ModuleDefinition.Version, "gtfo.equipment.equipped"),
                Binding(ModuleDefinition.UnequippedBinding, ModuleDefinition.UnequippedCapability, ModuleDefinition.ProviderId, ModuleDefinition.Version, "gtfo.equipment.unequipped")
            },
            entrypoints = new[] { Entry("equipped", 1, Graph(ModuleDefinition.EquippedCapability)), Entry("unequipped", 2, Graph(ModuleDefinition.UnequippedCapability)) }
        }).GetRawText();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Kernel.StartupState != RuntimeStartupState.Registering) Kernel.StopRuntime();
        if (Session != null && ReferenceEquals(WeaponNativeSession.Current, Session)) Session.Dispose();
        consumer.Dispose(); players.Dispose();
    }

    internal static void Cleanup()
    {
        foreach (var world in Live.ToArray()) world.Dispose();
        Live.Clear(); PlayerBackpackManager.Backpacks.Clear(); SNet.IsMaster = true;
    }
}
