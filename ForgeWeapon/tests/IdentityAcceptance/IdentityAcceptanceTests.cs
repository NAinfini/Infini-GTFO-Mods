using ForgeRuntime.Framework;
using ForgeWeapon;

namespace ForgeWeapon.Tests.IdentityAcceptance;

// Independent consumer: compiled Weapon + compiled SDK, synthetic adapter observations.
// No native method execution, game launch, installation or network access.
public sealed class IdentityAcceptanceTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Rejected(Action action, string? code = null)
    {
        try { action(); }
        catch (RuntimeContractException e) { if (code != null) Check(e.Code == code, "Expected " + code + "; got " + e.Code); return; }
        throw new Exception("Expected rejection" + (code == null ? "" : ": " + code));
    }

    private static EntityReference Owner(string id = "a", long life = 1, long world = 7) => new("fixture.player:" + id, world, life);

    private static EquipmentObservation Item(long number = 1, long life = 1, long world = 7) => new(
        new EntityReference(EquipmentIdentityId.Life(world, number), world, life), "fixture.rifle", "r1",
        Owner(world: world), "GearStandard", EquipmentLocation.Inventory, true, true);

    private static EquipmentUseTicket RecordAndCapture(Fixture f, EquipmentObservation item)
    { f.Session.Record(item); return f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true); }

    [Fact]
    public void public_module_no_executable_claims()
    {
        using var f = new Fixture(); using var doc = System.Text.Json.JsonDocument.Parse(f.Kernel.ExportManifest());
        var r = doc.RootElement.GetProperty("registry");
        // The weapon provider declares its own one row and binds the runtime's canonical Trigger rows, so the
        // registry holds the trigger contract's provider beside it — one row of each is the whole weapon surface.
        Check(r.GetProperty("providers").GetArrayLength() == 2
            && r.GetProperty("providers").EnumerateArray().Any(p => p.GetProperty("id").GetString() == ModuleDefinition.ProviderId)
            && r.GetProperty("providers").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "forge.contract.trigger"),
            "provider duplicated");
        // The provider publishes no executable claim, whatever the size of its observe-only surface.
        Check(r.GetProperty("capabilities").GetArrayLength() > 0
            && r.GetProperty("bindings").EnumerateArray().All(b => b.GetProperty("role").GetString() == "observe"), "executable bindings advertised");
        Check(f.Kernel.QueuedEvents == 0 && f.Kernel.LoadedPlans == 0, "identity inspection started gameplay");
    }

    [Fact]
    public void live_use_revalidates_native_and_owner()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item());
        f.NativeCalls = f.OwnerCalls = 0; f.Session.RequireCurrent(t);
        Check(f.NativeCalls > 0 && f.OwnerCalls > 0, "live predicates bypassed");
    }

    [Fact]
    public void duplicate_observation_preserves_current_ticket()
    {
        using var f = new Fixture(); var item = Item(); var t = RecordAndCapture(f, item);
        f.Session.Record(item); f.Session.RequireCurrent(t);
    }

    [Fact]
    public void same_resource_two_instances_remain_distinct()
    {
        using var f = new Fixture(); var first = RecordAndCapture(f, Item());
        var secondItem = Item(2) with { Slot = "GearSpecial" }; var second = RecordAndCapture(f, secondItem);
        f.Session.RequireCurrent(first); f.Session.RequireCurrent(second);
        Check(first.Observation.Entity != second.Observation.Entity, "resource identity used as instance key");
    }

    [Fact]
    public void ABA_transfer_never_revives_old_ticket()
    {
        using var f = new Fixture(); var item = Item(); var old = RecordAndCapture(f, item);
        f.Session.Record(item with { Owner = Owner("b") }); f.Session.Record(item);
        Rejected(() => f.Session.RequireCurrent(old), "equipment.observation-changed");
        var current = f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true); f.Session.RequireCurrent(current);
        Check(current.Observation.Entity == old.Observation.Entity, "ownership transition changed lifeEpoch");
    }

    [Fact]
    public void old_owner_cannot_capture_transferred_equipment()
    {
        using var f = new Fixture(); var item = Item(); f.Session.Record(item with { Owner = Owner("b") });
        Rejected(() => f.Session.CaptureOwnedUse(item.Entity, Owner(), true));
        f.Session.RequireCurrent(f.Session.CaptureOwnedUse(item.Entity, Owner("b"), true));
    }

    [Fact]
    public void owner_life_change_revokes_ticket()
    {
        using var f = new Fixture(); var item = Item(); var old = RecordAndCapture(f, item);
        f.Session.Record(item with { Owner = Owner(life: 2) });
        Rejected(() => f.Session.RequireCurrent(old), "equipment.observation-changed");
    }

    [Fact]
    public void unwield_revokes_use_without_destroying_item()
    {
        using var f = new Fixture(); var item = Item(); var old = RecordAndCapture(f, item);
        f.Session.Record(item with { IsWielded = false });
        Rejected(() => f.Session.RequireCurrent(old), "equipment.observation-changed");
        Rejected(() => f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true));
        f.Session.RequireCurrent(f.Session.CaptureOwnedUse(item.Entity, item.Owner!, false));
    }

    [Fact]
    public void not_ready_loading_state_never_authorizes_use()
    {
        using var f = new Fixture(); var item = Item() with { IsReady = false, IsWielded = false }; f.Session.Record(item);
        Rejected(() => f.Session.CaptureOwnedUse(item.Entity, item.Owner!, false));
    }

    [Fact]
    public void definition_revision_change_rejected_atomically()
    {
        using var f = new Fixture(); var item = Item(); var t = RecordAndCapture(f, item);
        Rejected(() => f.Session.Record(item with { ResourceRevision = "r2" }), "equipment.definition-changed");
        f.Session.RequireCurrent(t);
    }

    [Fact]
    public void occupied_slot_rejected_without_harming_incumbent()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item());
        Rejected(() => f.Session.Record(Item(2)), "equipment.slot-occupied"); f.Session.RequireCurrent(t);
    }

    [Fact]
    public void live_instance_cannot_be_replaced_by_new_life_implicitly()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item());
        Rejected(() => f.Session.Record(Item(life: 2)), "equipment.instance-still-live"); f.Session.RequireCurrent(t);
    }

    [Fact]
    public void native_disappearance_rejects_captured_use()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.NativeAlive = false;
        Rejected(() => f.Session.RequireCurrent(t));
    }

    [Fact]
    public void owner_disappearance_rejects_captured_use()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.OwnerAlive = false;
        Rejected(() => f.Session.RequireCurrent(t));
    }

    [Fact]
    public void native_predicate_exception_is_not_success()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item());
        f.NativeHook = _ => throw new InvalidOperationException("synthetic native reader failure");
        Rejected(() => f.Session.RequireCurrent(t));
    }

    [Fact]
    public void owner_predicate_exception_is_not_success()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item());
        f.OwnerHook = _ => throw new InvalidOperationException("synthetic owner reader failure");
        Rejected(() => f.Session.RequireCurrent(t));
    }

    [Fact]
    public void world_change_invalidates_old_ticket()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.Kernel.BeginWorld(8); f.Kernel.Advance(0, true);
        Rejected(() => f.Session.RequireCurrent(t)); Rejected(() => f.Session.Record(Item()));
        f.Session.RequireCurrent(RecordAndCapture(f, Item(world: 8)));
    }

    [Fact]
    public void stop_invalidates_ticket_and_dispose_is_idempotent()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.Kernel.StopRuntime();
        Rejected(() => f.Session.RequireCurrent(t)); f.Session.Dispose(); f.Session.Dispose();
    }

    [Fact]
    public void disposed_session_cannot_revalidate_use()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item()); f.Session.Dispose();
        Rejected(() => f.Session.RequireCurrent(t));
    }

    [Fact]
    public void ticket_cannot_cross_session_instances()
    {
        using var a = new Fixture(); using var b = new Fixture();
        var ticket = RecordAndCapture(a, Item()); RecordAndCapture(b, Item());
        Rejected(() => b.Session.RequireCurrent(ticket));
    }

    [Fact]
    public void client_authority_rejects_owned_use()
    {
        using var f = new Fixture(host: false); var item = Item();
        Rejected(() => { f.Session.Record(item); f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true); });
    }

    [Fact]
    public void record_before_ready_is_rejected()
    {
        using var f = new Fixture(start: false); Rejected(() => f.Session.Record(Item()));
        f.Start(true); f.Session.RequireCurrent(RecordAndCapture(f, Item()));
    }

    [Fact]
    public void resolver_cannot_transfer_and_return_stale_success()
    {
        using var f = new Fixture(); var item = Item(); var t = RecordAndCapture(f, item);
        f.NativeHook = _ => { f.Session.Record(item with { Owner = Owner("b") }); return true; };
        Rejected(() => f.Session.RequireCurrent(t));
    }

    [Fact]
    public void resolver_cannot_change_world_and_return_stale_success()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item());
        f.NativeHook = _ => { f.Kernel.BeginWorld(8); return true; }; Rejected(() => f.Session.RequireCurrent(t));
    }

    [Fact]
    public void simulation_thread_confinement()
    {
        using var f = new Fixture(); var t = RecordAndCapture(f, Item()); Exception? error = null;
        var thread = new Thread(() => { try { f.Session.RequireCurrent(t); } catch (Exception e) { error = e; } });
        thread.Start(); thread.Join(); Check(error is RuntimeContractException, "off-thread ticket validation accepted");
    }

    [Fact]
    public void same_slot_new_life_does_not_revive_old_ticket()
    {
        using var f = new Fixture(); var item = Item(); var old = RecordAndCapture(f, item);
        Check(f.Session.Remove(item.Entity), "explicit retire failed");
        var next = RecordAndCapture(f, Item(life: 2));
        Rejected(() => f.Session.RequireCurrent(old)); f.Session.RequireCurrent(next);
    }

    [Fact]
    public void late_remove_does_not_delete_replacement()
    {
        using var f = new Fixture(); var item = Item(); RecordAndCapture(f, item); f.Session.Remove(item.Entity);
        var next = RecordAndCapture(f, Item(life: 2));
        Check(!f.Session.Remove(item.Entity), "old-life removal matched new instance"); f.Session.RequireCurrent(next);
    }

    [Fact]
    public void retired_and_older_lives_cannot_return()
    {
        using var f = new Fixture(); var item = Item(life: 3); RecordAndCapture(f, item); f.Session.Remove(item.Entity);
        Rejected(() => f.Session.Record(item), "equipment.retired-life");
        Rejected(() => f.Session.Record(Item(life: 2)), "equipment.retired-life");
    }

    [Fact]
    public void retire_is_idempotent_and_frees_slot()
    {
        using var f = new Fixture(); var item = Item(); RecordAndCapture(f, item);
        Check(f.Session.Remove(item.Entity) && !f.Session.Remove(item.Entity), "retire not idempotent");
        f.Session.RequireCurrent(RecordAndCapture(f, Item(2)));
    }

    [Fact]
    public void active_budget_failure_does_not_poison_new_identity()
    {
        using var f = new Fixture(maxActive: 1, maxHistory: 2); var first = RecordAndCapture(f, Item());
        var second = Item(2) with { Slot = "GearSpecial" };
        Rejected(() => f.Session.Record(second), "equipment.active-budget"); f.Session.RequireCurrent(first);
        f.Session.Remove(first.Observation.Entity); f.Session.RequireCurrent(RecordAndCapture(f, second));
    }

    [Fact]
    public void history_budget_never_evicts_replay_protection()
    {
        using var f = new Fixture(maxActive: 1, maxHistory: 1); var first = RecordAndCapture(f, Item()); f.Session.Remove(first.Observation.Entity);
        Rejected(() => f.Session.Record(Item(2)), "equipment.history-budget");
        Rejected(() => f.Session.Record(Item()), "equipment.retired-life");
        f.Session.RequireCurrent(RecordAndCapture(f, Item(life: 2)));
    }

    [Fact]
    public void owner_world_mismatch_rejected_before_state_change()
    {
        using var f = new Fixture(); var item = Item(); var ticket = RecordAndCapture(f, item);
        Rejected(() => f.Session.Record(item with { Owner = Owner(world: 8) }), "equipment.stale-owner-world");
        f.Session.RequireCurrent(ticket);
    }

    [Fact]
    public void foreign_entity_namespace_not_cast_to_equipment()
    {
        using var f = new Fixture(); var item = Item() with { Entity = new EntityReference("fixture.player:a", 7, 1) };
        Rejected(() => f.Session.Record(item), "equipment.entity-namespace");
    }

    [Fact]
    public void world_item_does_not_infer_an_owner()
    {
        using var f = new Fixture(); var item = Item() with { Owner = null, Slot = null, Location = EquipmentLocation.World, IsWielded = false };
        f.Session.Record(item); Check(f.Session.TryResolve(item.Entity, out var observed, out _), "world observation lost");
        Check(observed!.Owner == null, "invented owner for world item");
        Rejected(() => f.Session.CaptureOwnedUse(item.Entity, Owner(), false));
    }

    [Fact]
    public void deployed_item_not_accepted_as_inventory_weapon()
    {
        using var f = new Fixture(); var item = Item() with { Slot = null, Location = EquipmentLocation.Deployed, IsWielded = false };
        f.Session.Record(item); Check(f.Session.TryResolve(item.Entity, out var observed, out _), "deployment observation lost");
        Check(observed!.Location == EquipmentLocation.Deployed, "deployment coerced into inventory");
        Rejected(() => f.Session.CaptureOwnedUse(item.Entity, item.Owner!, true));
    }

    [Fact]
    public void loss_of_host_clears_observations()
    {
        using var f = new Fixture(); var ticket = RecordAndCapture(f, Item()); f.Kernel.Advance(1, false);
        Check(f.Session.Count == 0, "authority loss retained live observations"); Rejected(() => f.Session.RequireCurrent(ticket));
    }

    [Fact]
    public void owner_probe_cannot_dispose_and_return_current_use()
    {
        using var f = new Fixture(); var ticket = RecordAndCapture(f, Item());
        f.OwnerHook = _ => { f.Session.Dispose(); return true; }; Rejected(() => f.Session.RequireCurrent(ticket));
    }

    private sealed class Fixture : IDisposable
    {
        public RuntimeKernel Kernel { get; } = new(new("forge.weapon.acceptance", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        public EquipmentIdentitySession Session { get; }
        public bool NativeAlive = true, OwnerAlive = true;
        public int NativeCalls, OwnerCalls;
        public Func<EquipmentObservation, bool>? NativeHook;
        public Func<EntityReference, bool>? OwnerHook;
        public Fixture(bool host = true, bool start = true, int maxActive = 1024, int maxHistory = 8192)
        {
            Kernel.BeginWorld(7);
            // The host registers the runtime's own contract providers before any domain package; the weapon module's
            // bindings name Trigger capabilities, and a binding whose capability is missing is refused.
            Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
            Session = new EquipmentIdentitySession(Kernel, RuntimeLogLevel.Off,
                e => { NativeCalls++; return NativeHook?.Invoke(e) ?? NativeAlive; },
                e => { OwnerCalls++; return OwnerHook?.Invoke(e) ?? OwnerAlive; }, maxActive, maxHistory);
            if (start) Start(host);
        }
        public void Start(bool host) { Kernel.StartRuntime(() => { }); Kernel.Advance(0, host); }
        public void Dispose() { Session.Dispose(); Kernel.StopRuntime(); }
    }
}
