using ForgeRuntime.Framework;
using ForgeWeapon;

namespace ForgeWeapon.Tests.Identity;

// Managed-equipment identity cases. Every case is a compiled production session with synthetic adapter
// observations: no GTFO call, no resource or damage implementation is faked.
public sealed class IdentityTests
{
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception("FAIL: " + reason);
    }

    private static void Reject(Action action, string code)
    {
        try { action(); }
        catch (RuntimeContractException error) { Check(error.Code == code, error.Code + " != " + code); return; }
        throw new Exception("FAIL: expected rejection " + code);
    }

    private static void Case(Action<TestWorld> action, bool start = true, int active = 1024, int history = 8192)
    {
        using var world = new TestWorld(start, active, history);
        action(world);
    }

    [Fact]
    public void same_definition_two_instances()
    {
        Case(w =>
        {
            var a = w.Track(); var b = w.Track(w.Item(2, "GearSpecial"));
            Check(a.Observation.ResourceId == b.Observation.ResourceId, "same resource");
            Check(w.Session.RequireCurrent(a).Entity != w.Session.RequireCurrent(b).Entity, "distinct instances");
        });
    }

    [Fact]
    public void same_slot_replacement()
    {
        Case(w =>
        {
            var old = w.Track(); Check(w.Session.Remove(old.Observation.Entity), "remove exact old life");
            var next = w.Track(w.Item(2));
            Reject(() => w.Session.RequireCurrent(old), "equipment.stale-instance");
            Check(w.Session.RequireCurrent(next).Entity.Id.EndsWith(":7.2"), "replacement resolves");
        });
    }

    [Fact]
    public void transfer_invalidates_old_owner_and_aba()
    {
        Case(w =>
        {
            var item = w.Item(); var old = w.Track(item);
            w.Session.Record(item with { Owner = item.Owner! with { Id = "fixture.player:b" } });
            Reject(() => w.Session.CaptureOwnedUse(item.Entity, w.Owner, true), "equipment.owner-mismatch");
            Reject(() => w.Session.RequireCurrent(old), "equipment.observation-changed");
            w.Session.Record(item);
            Reject(() => w.Session.RequireCurrent(old), "equipment.observation-changed");
            Check(w.Session.RequireCurrent(w.Session.CaptureOwnedUse(item.Entity, w.Owner, true)) == item, "new ticket");
        });
    }

    [Fact]
    public void duplicate_observation_is_idempotent()
    {
        Case(w =>
        {
            var item = w.Item(); var ticket = w.Track(item); w.Session.Record(item);
            Check(w.Session.RequireCurrent(ticket) == item && w.Session.Count == 1, "duplicates do not churn identity");
        });
    }

    [Fact]
    public void replicator_key_reuse_and_delayed_despawn()
    {
        Case(w =>
        {
            var old = w.Track(); w.Session.Remove(old.Observation.Entity);
            Reject(() => w.Session.Record(old.Observation), "equipment.retired-life");
            var next = w.Track(w.Item(life: 2));
            Check(!w.Session.Remove(old.Observation.Entity), "late despawn cannot remove replacement");
            Reject(() => w.Session.RequireCurrent(old), "equipment.stale-instance");
            Check(w.Session.RequireCurrent(next).Entity.LifeEpoch == 2, "new life intact");
        });
    }

    [Fact]
    public void world_reuse()
    {
        Case(w =>
        {
            var old = w.Track(); w.Kernel.BeginWorld(8);
            Check(w.Session.Count == 0 && w.Session.IdentityHistoryCount == 0, "world flush");
            w.Kernel.Advance(0, true); var next = w.Track();
            Reject(() => w.Session.RequireCurrent(old), "equipment.stale-instance");
            Check(w.Session.RequireCurrent(next).Entity.WorldEpoch == 8, "new world");
        });
    }

    [Fact]
    public void owner_respawn_and_live_owner_probe()
    {
        Case(w =>
        {
            var old = w.Track(); w.OwnerCurrent = false;
            Reject(() => w.Session.RequireCurrent(old), "equipment.owner-not-current");
            w.OwnerCurrent = true;
            w.Session.Record(old.Observation with { Owner = w.Owner with { LifeEpoch = 2 } });
            Reject(() => w.Session.RequireCurrent(old), "equipment.observation-changed");
        });
    }

    [Fact]
    public void slot_conflict_rejects_without_mutation()
    {
        Case(w =>
        {
            var old = w.Track(); var reads = w.NativeReads;
            Reject(() => w.Session.Record(w.Item(2)), "equipment.slot-occupied");
            Check(w.Session.Count == 1 && w.Session.IdentityHistoryCount == 1, "no partial insert");
            Check(w.Session.RequireCurrent(old) == old.Observation, "old item stays valid");
        });
    }

    [Fact]
    public void definition_cannot_change_inside_one_life()
    {
        Case(w =>
        {
            var old = w.Track();
            Reject(() => w.Session.Record(old.Observation with { ResourceRevision = "r2" }), "equipment.definition-changed");
            Check(w.Session.RequireCurrent(old).ResourceRevision == "r1", "original revision retained");
        });
    }

    [Fact]
    public void unwield_and_rewield_invalidates_use()
    {
        Case(w =>
        {
            var old = w.Track(); w.Session.Record(old.Observation with { IsWielded = false });
            Check(w.Session.IsCurrent(old.Observation.Entity), "unwield is not destruction");
            Reject(() => w.Session.CaptureOwnedUse(old.Observation.Entity, w.Owner, true), "equipment.not-wielded");
            w.Session.Record(old.Observation);
            Reject(() => w.Session.RequireCurrent(old), "equipment.observation-changed");
        });
    }

    [Fact]
    public void loading_is_not_ready_to_use()
    {
        Case(w =>
        {
            var item = w.Item() with { IsReady = false, IsWielded = false }; w.Session.Record(item);
            Check(w.Session.IsCurrent(item.Entity), "loading instance exists");
            Reject(() => w.Session.CaptureOwnedUse(item.Entity, w.Owner, false), "equipment.not-loaded");
        });
    }

    [Fact]
    public void drop_deploy_and_recall_change_preconditions()
    {
        Case(w =>
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
    }

    [Fact]
    public void deploy_and_recall_keeps_one_life()
    {
        Case(w =>
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
    }

    [Fact]
    public void entity_id_shapes_never_overlap()
    {
        Case(w =>
        {
            // One namespace, two tables: only the id shape decides which one answers, and a world instance id is
            // never read as an equipment life (or the reverse) even when the life it extends is current.
            Check(EquipmentIdentityId.ShapeOf("gtfo.equipment:7.3") == EquipmentEntityShape.Life
                && EquipmentIdentityId.ShapeOf("gtfo.equipment:7.3.2") == EquipmentEntityShape.DeployedInstance,
                "the two issued shapes are recognized");
            foreach (var id in new[] { "gtfo.equipment:", "gtfo.equipment:7", "gtfo.equipment:7.", "gtfo.equipment:.3",
                "gtfo.equipment:7.3.", "gtfo.equipment:7.3.2.1", "gtfo.equipment:a.3", "gtfo.equipment:7.b",
                "gtfo.equipmentx:7.3", "fixture.other:7.3" })
                Check(EquipmentIdentityId.ShapeOf(id) == EquipmentEntityShape.None, "not an id this provider issues: " + id);
            var item = w.Item(); w.Track(item);
            Check(w.Session.IsCurrent(item.Entity), "the recorded life is current");
            Check(!w.Session.IsCurrent(item.Entity with { Id = item.Entity.Id + ".1" }),
                "a world instance id is not answered by the equipment table");
        });
    }

    [Fact]
    public void native_probe_reread_and_fail_closed()
    {
        Case(w =>
        {
            var ticket = w.Track(); int before = w.NativeReads; w.NativeCurrent = false;
            Check(!w.Session.TryResolve(ticket.Observation.Entity, out var result, out var code)
                && result == null && code == "equipment.native-not-current", "stale native rejected");
            Check(w.NativeReads > before, "resolution performs fresh probe");
        });
    }

    [Fact]
    public void probe_exception_is_explicit()
    {
        Case(w =>
        {
            var ticket = w.Track(); w.DuringProbe = () => throw new InvalidOperationException("synthetic probe error");
            Reject(() => w.Session.RequireCurrent(ticket), "equipment.probe-failed");
            Check(w.Session.Count == 1, "uncertain probe does not invent removal");
        });
    }

    [Fact]
    public void probe_reentrant_record_and_dispose_rejected()
    {
        Case(w =>
        {
            var ticket = w.Track(); w.DuringProbe = () => w.Session.Record(ticket.Observation);
            Reject(() => w.Session.RequireCurrent(ticket), "equipment.probe-reentrancy");
            w.DuringProbe = w.Session.Dispose;
            Reject(() => w.Session.RequireCurrent(ticket), "equipment.probe-reentrancy");
            w.DuringProbe = null; Check(w.Session.RequireCurrent(ticket) == ticket.Observation, "session remains attached");
        });
    }

    [Fact]
    public void world_invalidated_during_native_probe()
    {
        Case(w =>
        {
            var ticket = w.Track(); w.DuringProbe = () => w.Kernel.BeginWorld(8);
            Reject(() => w.Session.RequireCurrent(ticket), "equipment.not-authoritative-ready");
            w.DuringProbe = null; Check(w.Session.Count == 0, "old world was cleared");
        });
    }

    [Fact]
    public void active_budget_preserves_existing_entry()
    {
        Case(w =>
        {
            var old = w.Track();
            Reject(() => w.Session.Record(w.Item(2, "GearSpecial")), "equipment.active-budget");
            Check(w.Session.RequireCurrent(old) == old.Observation && w.Session.Count == 1, "atomic active refusal");
        }, active: 1, history: 2);
    }

    [Fact]
    public void history_budget_never_evicts_retired_lives()
    {
        Case(w =>
        {
            var a = w.Track(); w.Session.Remove(a.Observation.Entity);
            var b = w.Track(w.Item(2)); w.Session.Remove(b.Observation.Entity);
            Reject(() => w.Session.Record(w.Item(3)), "equipment.history-budget");
            Reject(() => w.Session.Record(a.Observation), "equipment.retired-life");
            var next = w.Track(w.Item(life: 2));
            Check(w.Session.RequireCurrent(next).Entity.LifeEpoch == 2 && w.Session.IdentityHistoryCount == 2,
                "known id can advance without evicting history");
        }, active: 1, history: 2);
    }

    [Fact]
    public void live_replacement_needs_explicit_retirement()
    {
        Case(w =>
        {
            var old = w.Track(); Reject(() => w.Session.Record(w.Item(life: 2)), "equipment.instance-still-live");
            Check(w.Session.RequireCurrent(old) == old.Observation, "no implicit despawn");
        });
    }

    [Fact]
    public void not_ready_and_registration_window()
    {
        Case(w =>
        {
            Reject(() => w.Session.Record(w.Item()), "equipment.not-authoritative-ready");
            Reject(() => new EquipmentIdentitySession(w.Kernel, RuntimeLogLevel.Off, _ => true, _ => true), "provider-conflict");
            w.Start(); var before = w.Kernel.ExportManifest();
            Reject(() => new EquipmentIdentitySession(w.Kernel, RuntimeLogLevel.Off, _ => true, _ => true), "registration-closed");
            Check(w.Kernel.ExportManifest() == before, "rejected registration is atomic");
        }, start: false);
    }

    [Fact]
    public void stop_clears_and_dispose_is_idempotent()
    {
        Case(w =>
        {
            var old = w.Track(); w.Kernel.StopRuntime();
            Check(w.Session.Count == 0 && !w.Session.IsCurrent(old.Observation.Entity), "stopped index cleared");
            w.Session.Dispose(); w.Session.Dispose();
            Check(!w.Session.Remove(old.Observation.Entity), "late cleanup is safe");
        });
    }

    [Fact]
    public void failed_startup_denies_observations()
    {
        Case(w =>
        {
            bool failed = false;
            try { w.Kernel.StartRuntime(() => throw new InvalidOperationException("synthetic startup failure")); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed, "failure actually occurred");
            Reject(() => w.Session.Record(w.Item()), "equipment.not-authoritative-ready");
        }, start: false);
    }

    [Fact]
    public void authority_loss_clears_observations()
    {
        Case(w =>
        {
            var old = w.Track(); w.Kernel.Advance(1, false);
            Check(w.Session.Count == 0 && !w.Session.IsCurrent(old.Observation.Entity), "host loss is fail closed");
            Reject(() => w.Session.Record(w.Item()), "equipment.not-authoritative-ready");
        });
    }

    /// <summary>Runs one action on a thread this session does not own and rethrows what it threw. `Task.Run` is not
    /// a usable foreign thread here: xUnit runs the body on a pool thread under its own sync context, and the pool
    /// then runs the queued work item inline on that very thread, where the session rightly sees its owner. A
    /// dedicated thread is deterministic.</summary>
    private static void OnForeignThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.Start(); thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void wrong_thread_does_not_mutate()
    {
        Case(w =>
        {
            var old = w.Track();
            Reject(() => OnForeignThread(() => w.Session.Remove(old.Observation.Entity)), "wrong-thread");
            Reject(() => OnForeignThread(w.Session.Dispose), "wrong-thread");
            Check(w.Session.RequireCurrent(old) == old.Observation, "owning thread still works");
        });
    }

    [Fact]
    public void foreign_session_ticket_rejected()
    {
        Case(w =>
        {
            var ticket = w.Track(); using var other = new TestWorld(); other.Track();
            Reject(() => other.Session.RequireCurrent(ticket), "equipment.foreign-ticket");
        });
    }

    [Fact]
    public void no_gameplay_claims_or_embedded_kernel()
    {
        Case(w =>
        {
            using var manifest = System.Text.Json.JsonDocument.Parse(w.Kernel.ExportManifest());
            var root = manifest.RootElement;
            var registry = root.GetProperty("registry");
            var capabilities = registry.GetProperty("capabilities").EnumerateArray().ToArray();
            var bindings = registry.GetProperty("bindings").EnumerateArray().ToArray();
            // The surface this provider carries is the declaration the session registered, so the manifest is
            // compared against that declaration rather than against a hand-written row list: this fixture supplies
            // no body for any action row, so the declaration is the observation rows and nothing executable.
            using var declaration = System.Text.Json.JsonDocument.Parse(ModuleDefinition.Create().RegistryJson);
            var declaredCapabilities = declaration.RootElement.GetProperty("capabilities").EnumerateArray()
                .Select(row => row.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Check(capabilities.Where(c => c.GetProperty("owner").GetString() == ModuleDefinition.ProviderId)
                .Select(c => c.GetProperty("id").GetString()).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(declaredCapabilities),
                "the declared provider surface");
            var migrated = new[] { ModuleDefinition.EquippedCapability, ModuleDefinition.UnequippedCapability,
                ModuleDefinition.ShotCommittedCapability, ModuleDefinition.HitCandidateCapability,
                ModuleDefinition.DespawnedCapability, ModuleDefinition.DeployCompletedCapability };
            Check(capabilities.Where(c => migrated.Contains(c.GetProperty("id").GetString()))
                .All(c => c.GetProperty("owner").GetString() == "forge.contract.trigger"),
                "every migrated row is owned by the runtime's trigger contract");
            var declaredBindings = declaration.RootElement.GetProperty("bindings").EnumerateArray()
                .Select(row => row.GetProperty("capabilityId").GetString()!)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Check(bindings.Select(b => b.GetProperty("capabilityId").GetString())
                .OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(declaredBindings), "every declared binding is bound");
            Check(bindings.All(b => b.GetProperty("role").GetString() == "observe"), "no executable bindings");
            // Every binding is implementation-only and asks only for the read permissions its own facts need.
            var reads = new[] { ModuleDefinition.WieldReadPermission, ModuleDefinition.CombatReadPermission, ModuleDefinition.DeployableReadPermission };
            Check(root.GetProperty("bindingSupport").EnumerateArray().All(s => s.GetProperty("verification").GetString() == "implementation-only"
                && s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).All(reads.Contains)), "no game verification; read-only permissions");
            Check(typeof(EquipmentIdentitySession).Assembly.GetType(typeof(RuntimeKernel).FullName!) == null, "single SDK");
        });
    }

    [Fact]
    public void unknown_authority_denied_before_first_tick()
    {
        Case(w =>
        {
            w.Kernel.StartRuntime(() => { });
            Reject(() => w.Session.Record(w.Item()), "equipment.not-authoritative-ready");
        }, start: false);
    }

    public static TheoryData<string, Func<EquipmentObservation, EquipmentObservation>, string> Invalid => new()
    {
        { "wrong-namespace", x => x with { Entity = x.Entity with { Id = "fixture.other:a" } }, "equipment.entity-namespace" },
        { "empty-instance-id", x => x with { Entity = x.Entity with { Id = "gtfo.equipment:" } }, "equipment.entity-namespace" },
        { "unnumbered-life-id", x => x with { Entity = x.Entity with { Id = "gtfo.equipment:a" } }, "equipment.entity-namespace" },
        { "deployed-instance-is-not-a-life", x => x with { Entity = x.Entity with { Id = "gtfo.equipment:7.1.1" } }, "equipment.entity-namespace" },
        { "negative-life", x => x with { Entity = x.Entity with { LifeEpoch = -1 } }, "invalid-integer" },
        { "wrong-world", x => x with { Entity = x.Entity with { WorldEpoch = 6 } }, "equipment.stale-world" },
        { "missing-resource", x => x with { ResourceId = null! }, "equipment.resource-reference" },
        { "empty-revision", x => x with { ResourceRevision = "" }, "equipment.resource-reference" },
        { "missing-inventory-owner", x => x with { Owner = null }, "equipment.inventory-location" },
        { "old-owner-world", x => x with { Owner = x.Owner! with { WorldEpoch = 6 } }, "equipment.stale-owner-world" },
        { "self-owner", x => x with { Owner = x.Entity }, "equipment.self-owner" },
        { "missing-slot", x => x with { Slot = null }, "equipment.inventory-location" },
        { "world-with-slot", x => x with { Location = EquipmentLocation.World }, "equipment.non-inventory-location" },
        { "unloaded-wield", x => x with { IsReady = false }, "equipment.wielded-not-ready" },
        { "unknown-location", x => x with { Location = (EquipmentLocation)99 }, "equipment.location" },
        { "untrimmed-resource", x => x with { ResourceId = " fixture.rifle" }, "equipment.resource-reference" },
        { "unsafe-life", x => x with { Entity = x.Entity with { LifeEpoch = long.MaxValue } }, "invalid-integer" }
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void invalid_observation_is_rejected_before_probes_or_state(string name, Func<EquipmentObservation, EquipmentObservation> change, string code)
    {
        Case(w =>
        {
            Reject(() => w.Session.Record(change(w.Item())), code);
            Check(w.NativeReads == 0 && w.Session.Count == 0 && w.Session.IdentityHistoryCount == 0,
                "invalid input rejected before probes or state changes: " + name);
        });
    }
}
