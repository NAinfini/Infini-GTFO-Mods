using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon;
using ForgeWeapon.Native;
using HarmonyLib;
using Player;
using SNetwork;
using Host = ForgeRuntime.Plugin;
using WeaponPlugin = ForgeWeapon.Native.Plugin;

// Production Weapon plugin / WeaponNativeSession / EquipmentNativeAdapter / hook postfixes + compiled Weapon + compiled SDK.
// Native state is a managed double: every exit case below is synthetic and NOT game-verified. The gtfo.player
// provider here is a fixture standing in for ForgeMap; only its SDK surface (resolver + instance lookup) is used.
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
    var reissued = new EntityReference("gtfo.player:a", 8, 1);
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
Case("owner.non-current-player-reference-is-unresolved", () =>
{
    // The SDK re-checks the provider's answer, so a reference its resolver no longer accepts never reaches the index.
    var w = new World(); var p = w.Player("a"); w.LivePlayers.Clear(); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack);
    Require(w.Reports.SequenceEqual(new[] { "weapon.owner-unresolved: backpack items are not recorded without a player reference from its owning domain." })
        && !w.Session!.Faulted && w.Session.Adapter.TrackedCount == 0 && w.Session.Identity.Count == 0, "Non-current owner was recorded or faulted.");
    w.LivePlayers.Add(p.Reference); Hook(stored, p.Backpack);
    Require(w.Session!.Identity.Count == 1, "An unresolved owner latched observation off.");
});
Case("owner.resolved-through-sdk-player-lookup", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
    Require(w.Session!.Identity.TryResolve(entity, out var o, out _) && o!.Owner == p.Reference, "Owner was not the SDK player reference.");
    Require(w.LookupInputs.Count > 0 && w.LookupInputs.All(i => ReferenceEquals(i, p.Net)),
        "Weapon asked the player lookup about something other than the backpack's SNet_Player.");
});
Case("owner.new-player-life-retires-equipment-without-history", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(stored, p.Backpack); var old = w.EntityOf(item.Instance!)!;
    var revived = new EntityReference(p.Reference.Id, p.Reference.WorldEpoch, 2);
    w.PlayerRefs[p.Net] = revived; w.LivePlayers.Clear(); w.LivePlayers.Add(revived);
    Require(!w.Current(old), "Equipment stayed current after its owner's life ended.");
    Hook(stored, p.Backpack); var next = w.EntityOf(item.Instance!)!;
    Require(next.Id != old.Id && w.Current(next) && w.Session!.Identity.TryResolve(next, out var o, out _) && o!.Owner == revived,
        "A new owner life did not start a new equipment life.");
    w.Tick(); Require(w.Sink.Count == 0, "Owner life change synthesized wield history.");
});
Case("owner.missing-player-lookup-fails-closed", () =>
{
    var w = new World(playerLookup: false); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack);
    Require(w.Session!.Faulted && w.Session.Identity.Count == 0 && w.Session.Adapter.TrackedCount == 0
        && w.Session.LastFault == "RuntimeContractException: gtfo.player"
        && w.Reports.Count(r => r.StartsWith("Weapon equipment observation disabled", StringComparison.Ordinal)) == 1,
        "Missing gtfo.player instance lookup did not fail closed: " + w.Session.LastFault);
});
Case("owner.lookup-inside-entity-inspection-is-stale-not-fault", () =>
{
    // Kernel entity inspection forbids nested kernel queries, so equipment cannot be observed through it; it must not latch.
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
    var inspected = w.Kernel.InspectEntities(new[] { entity });
    Require(inspected.Items.Single().Code == "stale-entity" && w.Current(entity) && !w.Session!.Faulted,
        "Entity inspection changed equipment state: " + inspected.Items.Single().Code);
});
var deployed = typeof(BackpackItemDeployed);
Case("deploy.sentry-slot-reads-back-deployed", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
    Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
    p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack);
    Require(w.Session!.Identity.TryResolve(entity, out var o, out _) && o!.Location == EquipmentLocation.Deployed
        && o.Slot == null && !o.IsWielded && o.Owner == p.Reference && o.ResourceId == "gtfo.gear:55",
        "Deployed slot did not read back as a deployed location: " + o);
    Require(w.Session.Adapter.TrackedCount == 1 && w.Current(entity), "Deployment replaced the equipment life.");
    w.Tick(); Require(w.Sink.Count == 0, "Deployment synthesized wield history.");
});
Case("deploy.pickup-returns-to-inventory", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
    Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
    p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack);
    p.Backpack.SetDeployed(InventorySlot.GearSpecial, false); Hook(deployed, p.Backpack);
    Require(w.Session!.Identity.TryResolve(entity, out var o, out _) && o!.Location == EquipmentLocation.Inventory
        && o.Slot == nameof(InventorySlot.GearSpecial) && !o.IsWielded, "Recalled deployable did not return to its slot: " + o);
    w.Tick(); Require(w.Sink.Count == 0, "Deploy and recall synthesized wield history.");
});
Case("deploy.wielded-deployed-slot-publishes-no-fact", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
    Hook(stored, p.Backpack); w.Tick();
    p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(typeof(LocalItemWielded), p.Inventory); w.Tick();
    Require(w.Sink.Count == 1, "Wielding after the recorded snapshot did not publish.");
    p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack); w.Tick();
    Require(w.Sink.Count == 1, "A deployed slot was reported as unequipped.");
    Require(w.Session!.Identity.TryResolve(w.EntityOf(item.Instance!)!, out var o, out _) && !o!.IsWielded,
        "A deployed slot still read back as wielded.");
});
Case("deploy.stale-deployed-observation-is-not-current", () =>
{
    // IsNativeCurrent re-reads the marker, so a slot that changed state without a hook is stale, not a fault.
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
    Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
    p.Backpack.SetDeployed(InventorySlot.GearSpecial, true);
    Require(!w.Current(entity) && !w.Session!.Faulted, "Deployed state changed without a hook but stayed current.");
    Hook(deployed, p.Backpack);
    Require(w.Current(entity), "The deploy hook did not refresh the recorded location.");
});
Case("log.deploy-location-lines-are-exact", () =>
{
    var w = new World(); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
    Hook(stored, p.Backpack);
    p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack);
    p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack);
    p.Backpack.SetDeployed(InventorySlot.GearSpecial, false); Hook(deployed, p.Backpack);
    Require(w.Infos.SequenceEqual(new[]
    {
        "weapon.equipment-life-started id=gtfo.equipment:7.1 world=7 owner=gtfo.player:a ownerLife=1 slot=GearSpecial resource=gtfo.gear:55 location=Inventory",
        "weapon.equipment-location id=gtfo.equipment:7.1 location=Deployed",
        "weapon.equipment-location id=gtfo.equipment:7.1 location=Inventory"
    }) && w.Reports.Count == 0, string.Join(" | ", w.Infos.Concat(w.Reports)));
});
Case("contract.rejected-observation-does-not-fault", () =>
{
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    item.IsLoaded = false; p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(stored, p.Backpack);
    Require(w.Reports.Contains("weapon.observation-rejected: equipment.wielded-not-ready") && !w.Session!.Faulted
        && w.Session.Adapter.TrackedCount == 0 && w.Session.Identity.Count == 0, "Rejected observation faulted or leaked a handle.");
    item.IsLoaded = true; Hook(stored, p.Backpack);
    Require(w.Session!.Identity.Count == 1, "A rejection latched observation off.");
});
Case("log.life-wield-and-clear-lines-are-exact", () =>
{
    // These lines are the in-game checklist's expectations; any format drift must fail here first.
    var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
    Hook(stored, p.Backpack); Hook(stored, p.Backpack);
    p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(typeof(LocalItemWielded), p.Inventory);
    p.Backpack.Slots![(int)InventorySlot.GearStandard] = null; p.Inventory.WieldedItem = null; Hook(typeof(BackpackSlotCleared), p.Backpack);
    World.Put(p.Backpack, InventorySlot.GearSpecial, 77, item.Instance); Hook(stored, p.Backpack);
    w.Kernel.BeginWorld(8); w.Tick();
    var reissued = new EntityReference("gtfo.player:a", 8, 1);
    w.PlayerRefs[p.Net] = reissued; w.LivePlayers.Clear(); w.LivePlayers.Add(reissued);
    Hook(stored, p.Backpack);
    Require(w.Infos.SequenceEqual(new[]
    {
        "weapon.equipment-life-started id=gtfo.equipment:7.1 world=7 owner=gtfo.player:a ownerLife=1 slot=GearStandard resource=gtfo.gear:1234 location=Inventory",
        "weapon.wield-fact kind=equipped id=gtfo.equipment:7.1 owner=gtfo.player:a status=queued code=accepted",
        "weapon.equipment-life-ended id=gtfo.equipment:7.1 reason=slot-changed",
        "weapon.equipment-life-started id=gtfo.equipment:7.2 world=7 owner=gtfo.player:a ownerLife=1 slot=GearSpecial resource=gtfo.gear:77 location=Inventory",
        "weapon.equipment-lives-cleared world=7 count=1 reason=world-changed",
        "weapon.equipment-life-started id=gtfo.equipment:8.1 world=8 owner=gtfo.player:a ownerLife=1 slot=GearSpecial resource=gtfo.gear:77 location=Inventory"
    }) && w.Reports.Count == 0, string.Join(" | ", w.Infos.Concat(w.Reports)));
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
Case("plugin.off", () =>
{
    var w = new World(start: false); Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Off; Host.Runtime = w.Kernel;
    string before = w.Kernel.ExportManifest(); new WeaponPlugin().Load();
    Require(WeaponNativeSession.Current == null && Harmony.Patches == 0 && Harmony.Unpatches == 0 && w.Kernel.ExportManifest() == before,
        "Off activated registration or native hooks.");
});
Case("plugin.missing-runtime", () =>
{
    Host.Runtime = null; var plugin = new WeaponPlugin(); Throws(null, plugin.Load); Throws(null, plugin.Load);
    Require(Harmony.Patches == 0 && WeaponNativeSession.Current == null, "Unavailable host still installed patches.");
});
Case("plugin.existing-weapon-provider-conflict", () =>
{
    var w = new World(start: false); Host.Runtime = w.Kernel; using var existing = w.Kernel.RegisterModule(ModuleDefinition.Create());
    string before = w.Kernel.ExportManifest(); Throws("provider-conflict", new WeaponPlugin().Load);
    Require(Harmony.Patches == 0 && Harmony.Unpatches == 0 && WeaponNativeSession.Current == null && w.Kernel.ExportManifest() == before,
        "Plugin patched or removed a provider it did not own.");
});
Case("plugin.partial-hook-failure", () =>
{
    var w = new World(start: false); Host.Runtime = w.Kernel; string before = w.Kernel.ExportManifest(); Harmony.FailPatchAt = 3;
    Throws(null, new WeaponPlugin().Load);
    Require(Harmony.Patches == 3 && Harmony.Unpatches == 1 && WeaponNativeSession.Current == null && w.Kernel.ExportManifest() == before,
        "Partial native load survived rollback.");
});
Case("plugin.log-failure-rollback", () =>
{
    var w = new World(start: false); Host.Runtime = w.Kernel; string before = w.Kernel.ExportManifest();
    var plugin = new WeaponPlugin(); plugin.Log.ThrowInfo = true; Throws(null, plugin.Load);
    Require(Harmony.Patches == WeaponNativeHooks.Types.Count && Harmony.Unpatches == 1 && WeaponNativeSession.Current == null
        && w.Kernel.ExportManifest() == before, "Post-registration failure leaked the module or hooks.");
});
Case("plugin.cleanup-failure-preserves-cause", () =>
{
    var w = new World(start: false); Host.Runtime = w.Kernel; string before = w.Kernel.ExportManifest();
    var plugin = new WeaponPlugin(); plugin.Log.ThrowInfo = true; Harmony.UnpatchFailure = new InvalidOperationException("unpatch");
    try { plugin.Load(); }
    catch (IOException error)
    {
        Require(error.Data.Contains("ForgeWeapon.LoadCleanupFailure") && WeaponNativeSession.Current == null
            && w.Kernel.ExportManifest() == before && plugin.Log.Warnings.Count == 1, "Cleanup failure replaced the cause, stayed current or kept the provider.");
        return;
    }
    throw new Exception("Expected load failure.");
});
Case("plugin.success-owner-from-player-namespace-no-hot-reload", () =>
{
    var w = new World(start: false); Host.Runtime = w.Kernel; var plugin = new WeaponPlugin();
    plugin.Load();
    var session = WeaponNativeSession.Current;
    Require(session != null && Harmony.Patches == WeaponNativeHooks.Types.Count && plugin.Log.Infos.Count == 1, "Weapon native session was not installed.");
    Throws(null, plugin.Load);
    Require(Harmony.Patches == WeaponNativeHooks.Types.Count && !plugin.Unload(), "Repeated Load or hot unload changed native lifetime.");
    w.StartRuntime();
    var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234); Hook(stored, p.Backpack);
    var entity = w.EntityOf(item.Instance!)!;
    p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(typeof(LocalItemWielded), p.Inventory); w.Tick();
    Require(w.Sink.Count == 1 && w.Sink[0].Actor == p.Reference && w.Sink[0].Target == entity, "Plugin session did not publish the gtfo.player owner.");
    Host.CanExecuteGameplay = false; p.Inventory.WieldedItem = null; Hook(typeof(LocalItemUnwielded), p.Inventory); w.Tick();
    Require(w.Sink.Count == 1, "Host gameplay gate did not reach the adapter.");
    w.Kernel.StopRuntime(); session!.Dispose();
    Require(Harmony.Unpatches == 1 && WeaponNativeSession.Current == null, "Shutdown leaked hooks or the session.");
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
    internal WeaponNativeSession? Session => WeaponNativeSession.Current;
    internal readonly List<object> LookupInputs = new();
    internal readonly Dictionary<SNet_Player, EntityReference> PlayerRefs = new();
    internal readonly HashSet<EntityReference> LivePlayers = new();
    internal readonly List<(string EventId, EntityReference Target, EntityReference Actor)> Sink = new();
    internal readonly List<string> Reports = new(), Infos = new();
    internal int Installs, Removes;
    internal bool CanExecute = true;
    internal Action? DuringInstall;
    private readonly RuntimeModuleHandle players, consumer;
    private long tick;
    private bool disposed;

    internal sealed record Fixture(SNet_Player Net, PlayerAgent Agent, PlayerInventoryBase Inventory, PlayerBackpack Backpack, EntityReference Reference);

    internal World(bool start = true, bool playerLookup = true)
    {
        Live.Add(this);
        SNet.IsMaster = true;
        Kernel.BeginWorld(7);
        // Stand-in for ForgeMap's gtfo.player surface: the resolver plus, unless disabled, the SNet_Player instance lookup.
        players = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = "fixture.players", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>> { [EquipmentNativeAdapter.PlayerKind] = r => LivePlayers.Contains(r) })
        {
            EntityInstanceResolvers = playerLookup ? new Dictionary<string, Func<object, EntityReference?>>
            {
                [EquipmentNativeAdapter.PlayerKind] = instance =>
                {
                    LookupInputs.Add(instance);
                    return instance is SNet_Player player && PlayerRefs.TryGetValue(player, out var reference) ? reference : null;
                }
            } : null
        });
        consumer = Kernel.RegisterModule(ConsumerModule());
        if (!start) return;
        StartSession();
        StartRuntime();
    }

    internal WeaponNativeSession StartSession(Action? install = null, Action? remove = null)
        => WeaponNativeSession.Start(Kernel, () => CanExecute, Reports.Add, Infos.Add,
            install ?? (() => { Installs++; DuringInstall?.Invoke(); }), remove ?? (() => Removes++));

    internal void StartRuntime()
    {
        Kernel.StartRuntime(() => Kernel.LoadPlan(Plan()));
        Kernel.Advance(0, true);
    }

    internal void Tick(bool host = true) => Kernel.Advance(++tick, host);

    internal Fixture Player(string id, bool synced = false, bool resolved = true)
    {
        var net = new SNet_Player(); var agent = new PlayerAgent { Owner = net };
        PlayerInventoryBase inventory = synced ? new PlayerInventorySynced() : new PlayerInventoryLocal();
        inventory.Owner = agent; agent.Inventory = inventory; net.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
        var backpack = new PlayerBackpack { Owner = net }; PlayerBackpackManager.Backpacks[net] = backpack;
        var reference = new EntityReference(EquipmentNativeAdapter.PlayerKind + ":" + id, Kernel.WorldEpoch, 1);
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
        // schemaVersion 3 (D-017 R4-a): the entry names its first step, and the sink step has no execution output,
        // so its successor list is empty and the entrypoint ends after the single action.
        object Entry(string name, int binding, JsonElement trigger) => new
        {
            nodeId = name, binding, layout = Layout(trigger), start = 0,
            steps = new[] { new { nodeId = name + "_sink", nodeKind = "action", binding = 0, layout = Layout(sink), inputs = new[]
            {
                new { slot = Slot(sink.GetProperty("inputs"), "target"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "equipment") },
                new { slot = Slot(sink.GetProperty("inputs"), "actor"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "actor") }
            }, successors = Array.Empty<int?>() } }
        };
        return RuntimeJson.From(new
        {
            schemaVersion = 3, kind = "forge-runtime-plan", planId = "fixture.weapon.plan", resource = new { id = "fixture.resource", revision = "r1" },
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
        WeaponNativeSession.Current?.Dispose();
        consumer.Dispose(); players.Dispose();
    }

    internal static void Cleanup()
    {
        foreach (var world in Live.ToArray()) world.Dispose();
        Live.Clear(); PlayerBackpackManager.Backpacks.Clear(); SNet.IsMaster = true;
        Harmony.Reset(); Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = null; Host.CanExecuteGameplay = true;
    }
}
