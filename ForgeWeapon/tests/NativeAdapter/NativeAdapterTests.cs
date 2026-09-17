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
using WeaponType = global::Weapon;

namespace ForgeWeapon.Tests.NativeAdapter;

// Production Weapon plugin / WeaponNativeSession / EquipmentNativeAdapter / hook postfixes + compiled Weapon + compiled SDK.
// Native state is a managed double: every case below is synthetic and NOT game-verified. The gtfo.player
// provider here is a fixture standing in for ForgeMap; only its SDK surface (resolver + instance lookup) is used.
public sealed class NativeAdapterTests
{
    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    private static void Throws(string? code, Action action)
    {
        try { action(); }
        catch (RuntimeContractException error) when (code != null) { Require(error.Code == code, "Expected " + code + "; got " + error.Code); return; }
        catch (Exception) when (code == null) { return; }
        throw new Exception("Expected rejection " + code);
    }

    private static void Hook(Type hook, object instance)
        => Hook(hook, instance, Array.Empty<object>());
    /// <summary>Invokes a postfix with the patch's own instance plus its extra arguments. A body that does not read
    /// an argument is still handed one, so the shape the game calls is the shape the test calls.</summary>
    private static void Hook(Type hook, object instance, params object?[] arguments)
    {
        var postfix = hook.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!;
        var values = new object?[postfix.GetParameters().Length];
        values[0] = instance;
        for (var index = 0; index < arguments.Length && index + 1 < values.Length; index++) values[index + 1] = arguments[index];
        postfix.Invoke(null, values);
    }
    private static readonly Type stored = typeof(BackpackItemStored);
    private static readonly Type deployed = typeof(BackpackItemDeployed);

    private static void Reset() => World.Reset();

    [Fact]
    public void session_registration_before_hooks()
    {
        Reset();
        using var w = new World(start: false); string? manifest = null; WeaponNativeSession? current = null;
        w.DuringInstall = () => { manifest = w.Kernel.ExportManifest(); current = WeaponNativeSession.Current; };
        var session = w.StartSession();
        Require(w.Installs == 1 && ReferenceEquals(current, session), "Hooks ran before the session was current.");
        Require(manifest != null && manifest.Contains(ModuleDefinition.EquippedBinding), "Hooks ran before provider registration.");
    }

    [Fact]
    public void session_duplicate_provider_before_hooks()
    {
        Reset();
        using var w = new World(start: false); var other = w.Kernel.RegisterModule(ModuleDefinition.Create(), RuntimeLogLevel.Off);
        string before = w.Kernel.ExportManifest();
        Throws("provider-conflict", () => w.StartSession());
        Require(w.Installs == 0 && w.Removes == 0 && WeaponNativeSession.Current == null && w.Kernel.ExportManifest() == before,
            "Duplicate registration touched hooks or existing ownership.");
        other.Dispose();
    }

    [Fact]
    public void session_install_failure_rolls_back()
    {
        Reset();
        using var w = new World(start: false); string before = w.Kernel.ExportManifest(); var primary = new IOException("patch");
        try { w.StartSession(install: () => throw primary); throw new Exception("Expected install failure."); }
        catch (IOException error) { Require(ReferenceEquals(error, primary), "Install failure was replaced."); }
        Require(w.Removes == 1 && WeaponNativeSession.Current == null && w.Kernel.ExportManifest() == before, "Partial registration or hooks survived.");
        w.StartSession(); Require(WeaponNativeSession.Current != null && w.Installs == 1, "Rolled-back provider could not register again.");
    }

    [Fact]
    public void session_cleanup_failure_preserves_cause()
    {
        Reset();
        using var w = new World(start: false); string before = w.Kernel.ExportManifest(); var primary = new IOException("patch");
        try { w.StartSession(() => throw primary, () => throw new InvalidOperationException("unpatch")); }
        catch (IOException error) when (ReferenceEquals(error, primary))
        {
            Require(error.Data.Contains("ForgeWeapon.CleanupFailures") && WeaponNativeSession.Current == null
                && w.Kernel.ExportManifest() == before, "Cleanup failure hid the cause or skipped provider cleanup.");
            return;
        }
        throw new Exception("Expected startup failure.");
    }

    [Fact]
    public void session_late_registration_rejected()
    {
        Reset();
        using var w = new World(start: false); w.Kernel.StartRuntime(() => { });
        Throws(null, () => w.StartSession());
        Require(w.Installs == 0 && WeaponNativeSession.Current == null, "Late registration touched hooks.");
    }

    [Fact]
    public void module_exact_observe_claims()
    {
        Reset();
        using var w = new World();
        using var doc = JsonDocument.Parse(w.Kernel.ExportManifest()); var root = doc.RootElement;
        var bindings = root.GetProperty("registry").GetProperty("bindings").EnumerateArray()
            .Where(b => b.GetProperty("providerId").GetString() == ModuleDefinition.ProviderId).ToArray();
        var declared = new[]
        {
            ModuleDefinition.EquippedBinding, ModuleDefinition.UnequippedBinding,
            ModuleDefinition.ShotCommittedBinding, ModuleDefinition.HitCandidateBinding, ModuleDefinition.DespawnedBinding,
            ModuleDefinition.DeployCompletedBinding, ModuleDefinition.RecallCompletedBinding,
            WeaponDeployableFactsContract.FiredBinding, WeaponDeployableFactsContract.AmmoDepletedBinding,
            WeaponDeployableFactsContract.DetonatedBinding, WeaponMeleeHitContract.MeleeHitBinding,
            AttackInstanceContract.BurstStartedBinding, AttackInstanceContract.BurstEndedBinding,
            AttackInstanceContract.DryFireBinding,
            // The reload and inventory observation families: ten rows whose shapes the runtime's own trigger
            // contract owns, appended by `ReloadInventoryContract.Bindings`.
            ReloadInventoryContract.ReloadStartedBinding, ReloadInventoryContract.ReloadCompletedBinding,
            ReloadInventoryContract.ReloadTransferredBinding, ReloadInventoryContract.RefilledBinding,
            ReloadInventoryContract.StackChangedBinding, ReloadInventoryContract.PickedUpBinding,
            ReloadInventoryContract.DroppedBinding, ReloadInventoryContract.UseStartedBinding,
            ReloadInventoryContract.UseFailedBinding, ReloadInventoryContract.CarriedItemChangedBinding,
            // The two evaluator-answered reads: `observe` rows like the ones above, answered on demand instead of
            // by a dispatched body, so neither appears in the executed list below.
            InventoryQueryContract.EquipmentAmmoBinding, InventoryQueryContract.InventoryItemBinding,
            // This batch's own state and combat rows: the charge and aim triggers and the shot-resolution trigger.
            // The third is the one trigger here the provider's own native half publishes.
            WeaponStateTriggerContract.ChargeStateBinding, WeaponStateTriggerContract.AimStateBinding,
            CombatPrimitiveContract.ShotResolvedBinding,
            // The launch row the session supplies a body for: the game's own projectile spawn, so it is declared
            // as an executed row rather than an observed one.
            CombatPrimitiveContract.ProjectileLaunchBinding,
            // The action rows the session's own registration carries beside the observations: the ammunition
            // pair, the four instance overrides and the inventory give/consume pair. `drop` declares no row and
            // has no body, so nothing here names it.
            WeaponSupplyContract.AmmoAddBinding, WeaponSupplyContract.AmmoConsumeBinding,
            WeaponOverrideContract.FireRateBinding, WeaponOverrideContract.SpreadBinding, WeaponOverrideContract.RecoilBinding,
            WeaponOverrideContract.PropertyBinding,
            InventoryActionContract.GiveBinding, InventoryActionContract.ConsumeBinding
        };
        Require(bindings.Select(b => b.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(declared.OrderBy(id => id, StringComparer.Ordinal)),
            "Unexpected Weapon bindings: " + string.Join(", ", bindings.Select(b => b.GetProperty("id").GetString()).OrderBy(id => id, StringComparer.Ordinal)));
        // Exactly these rows execute; every other row the provider declares observes. The list is the one the
        // registration's own handler table answers, so a row that gained or lost a body fails here.
        var executed = new[]
        {
            WeaponSupplyContract.AmmoAddBinding, WeaponSupplyContract.AmmoConsumeBinding,
            WeaponOverrideContract.FireRateBinding, WeaponOverrideContract.SpreadBinding, WeaponOverrideContract.RecoilBinding,
            WeaponOverrideContract.PropertyBinding,
            InventoryActionContract.GiveBinding, InventoryActionContract.ConsumeBinding,
            CombatPrimitiveContract.ProjectileLaunchBinding
        };
        Require(bindings.All(b => b.GetProperty("role").GetString()
            == (executed.Contains(b.GetProperty("id").GetString()!, StringComparer.Ordinal) ? "execute" : "observe")),
            "Weapon claimed an executable binding it does not own.");
        // The support lines are matched against this provider's own rows rather than by the provider prefix: the
        // holder provider's id shares that prefix, so a prefix filter would count its two rows here.
        var support = root.GetProperty("bindingSupport").EnumerateArray()
            .Where(s => declared.Contains(s.GetProperty("bindingId").GetString()!, StringComparer.Ordinal)).ToArray();
        Require(support.Length == declared.Length && support.All(s => s.GetProperty("verification").GetString() == "implementation-only"),
            "Weapon claimed game verification: " + support.Length + " support lines for " + declared.Length + " bindings, verified="
            + string.Join(", ", support.Where(s => s.GetProperty("verification").GetString() != "implementation-only").Select(s => s.GetProperty("bindingId").GetString())));
    }

    // Every published fact is checked against the graph this provider registered: the payload must carry exactly
    // the trigger's required output ports, so a renamed or missing port cannot pass as a fact.
    private static void Fact(World w, RuntimeEvent? fact, string binding, string eventPrefix, params string[] ports)
    {
        var reason = " reports=[" + string.Join(" | ", w.Reports) + "] infos=[" + string.Join(" | ", w.Infos) + "]";
        Require(fact != null && fact.BindingId == binding, "No fact was published for " + binding + "." + reason);
        Require(fact!.EventId.StartsWith(eventPrefix, StringComparison.Ordinal), "Fact id differs: " + fact.EventId + reason);
        Require(fact.Outputs.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
            .SequenceEqual(ports.OrderBy(n => n, StringComparer.Ordinal)), "Fact ports differ: " + fact.Outputs.GetRawText() + reason);
    }

    /// <summary>A hit the way the native routine hands it over when what the bullet struck belongs to a map
    /// object: the collider stands on the door or the terminal, and the hit path can only see the collider, so
    /// the domain that owns map objects is handed exactly that.</summary>
    private static WeaponType.WeaponHitData MapObjectHit(PlayerAgent shooter, float x, float y, float z,
        UnityEngine.Component mapObject) => new()
    {
        owner = shooter,
        rayHit = new UnityEngine.RaycastHit
        {
            point = new UnityEngine.Vector3(x, y, z),
            collider = new UnityEngine.Collider { Parent = mapObject }
        },
        fireAtPos = new UnityEngine.Vector3()
    };

    private static void Fire(World w, WeaponType weapon)
    {
        // The Fire slot has one patch per declaring type, so the hook that matches the weapon is the one that runs:
        // the two unsynced bodies are the shooter's own weapon, the two synced bodies are this machine's copy of
        // somebody else's weapon.
        switch (weapon)
        {
            case Gear.ShotgunSynced syncedShotgun: Hook(typeof(SyncedShotgunFired), syncedShotgun); break;
            case Gear.BulletWeaponSynced synced: Hook(typeof(SyncedWeaponFired), synced); break;
            case Gear.Shotgun shotgun: Hook(typeof(ShotgunFired), shotgun); break;
            default: Hook(typeof(WeaponFired), weapon); break;
        }
        Require(!w.Session!.Faulted && w.Reports.Count == 0, "Firing faulted the session: " + w.Session.LastFault + " " + string.Join(" | ", w.Reports));
    }

    /// <summary>A hit the way the native routine hands it over: the ray hit names the collider the bullet was
    /// resolved against, and that collider's hierarchy decides the target. Passing no agent is world geometry —
    /// a real collider with no damage limb above it — which is the ordinary miss.
    /// </summary>
    private static WeaponType.WeaponHitData Hit(PlayerAgent shooter, float x, float y, float z, Agents.Agent? target = null) => new()
    {
        owner = shooter,
        rayHit = new UnityEngine.RaycastHit
        {
            point = new UnityEngine.Vector3(x, y, z),
            collider = new UnityEngine.Collider
            {
                Parent = target switch
                {
                    Enemies.EnemyAgent => new Dam_EnemyDamageLimb { BaseAgent = target },
                    PlayerAgent => new Dam_PlayerDamageLimb { BaseAgent = target },
                    _ => null
                }
            }
        },
        fireAtPos = new UnityEngine.Vector3()
    };

    /// <summary>Places the item as a world instance: the equipment life exists, the world instance is the same
    /// native object the backpack slot holds, and the slot marker says the player deployed it. `placed` is how many
    /// world instances the world holds after the call, so a player with two placed items still asserts one fact each.</summary>
    private static ItemEquippable Deploy(World w, PlayerBackpack backpack, PlayerAgent agent, BackpackItem item,
        InventorySlot slot = InventorySlot.GearStandard, int placed = 1)
    {
        var instance = (ItemEquippable)item.Instance!;
        instance.Owner = agent;
        backpack.SetDeployed(slot, true);
        Hook(typeof(BackpackItemDeployed), backpack);
        Require(w.Session!.Adapter.PlacementCount == placed && w.Reports.Count == 0,
            "The placed instance was not tracked: slot=" + slot + " count=" + w.Session.Adapter.PlacementCount + " tracked=" + w.Session.Adapter.TrackedCount
            + " identity=" + w.Session.Identity.Count + " hold=" + (w.Session.Adapter.PlacementOf(instance) != null)
            + " " + string.Join(" | ", w.Reports) + " " + string.Join(" | ", w.Infos));
        return instance;
    }

    [Fact]
    public void combat_one_shot_fact_per_fire_body_with_several_hits()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, new Gear.Shotgun());
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        var shotgun = (Gear.Shotgun)item.Instance!; shotgun.Owner = p.Agent;
        Fire(w, shotgun);
        Fact(w, w.Session!.Adapter.Published, ModuleDefinition.ShotCommittedBinding, "gtfo.weapon.shot:7:", "source", "equipment", "index");
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("equipment").GetProperty("id").GetString() == entity.Id
            && w.Session.Adapter.Published!.Outputs.GetProperty("index").GetInt64() == 1
            && w.Session.Adapter.Published!.Outputs.GetProperty("source").GetProperty("id").GetString() == p.Reference.Id,
            "Shot fact did not name the firing life, the player and the first shot.");
        // The shotgun's several hits all run inside the one Fire body, so they share the one shot: two candidates,
        // one shot fact, and every candidate carries the id of that shot. A hit on world geometry names no target,
        // so the port is absent from the payload rather than present and null.
        Hook(typeof(BulletHit), shotgun, Hit(p.Agent, 1, 2, 3)); Hook(typeof(BulletHit), shotgun, Hit(p.Agent, 4, 5, 6));
        Fact(w, w.Session.Adapter.Published, ModuleDefinition.HitCandidateBinding, "gtfo.weapon.hit:7:", "source", "equipment", "limb", "position");
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("equipment").GetProperty("id").GetString() == entity.Id,
            "The candidate did not name the firing life: " + w.Session.Adapter.Published.Outputs.GetRawText());
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("position").EnumerateArray().Select(v => v.GetDouble()).SequenceEqual(new[] { 4d, 5d, 6d }),
            "Hit candidate did not carry the second hit's position.");
        Fire(w, shotgun);
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("index").GetInt64() == 2, "A second shot reused the first shot's index.");
        Require(w.Session.Adapter.Published!.EventId != "gtfo.weapon.shot:7:1", "A second shot reused the first shot's event id.");
    }

    [Fact]
    public void combat_hit_names_the_target_its_collider_carries()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var enemy = w.Enemy(11); var other = w.Player("b");
        var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, new Gear.BulletWeapon());
        Hook(stored, p.Backpack); var weapon = (Gear.BulletWeapon)item.Instance!; weapon.Owner = p.Agent; Fire(w, weapon);
        // The enemy limb's base agent is the enemy domain's key, so the fact carries that domain's own reference.
        Hook(typeof(BulletHit), weapon, Hit(p.Agent, 1, 2, 3, enemy.Agent));
        Fact(w, w.Session!.Adapter.Published, ModuleDefinition.HitCandidateBinding, "gtfo.weapon.hit:7:", "source", "equipment", "target", "limb", "position");
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("target").GetProperty("id").GetString() == enemy.Reference.Id,
            "An enemy hit did not name the enemy reference: " + w.Session.Adapter.Published.Outputs.GetRawText());
        // The fact's own line is the whole assertion: nothing is mounted on the hit-candidate binding in this
        // fixture, so the kernel never sees the event and has no `status=ignored code=no-consumer` to log. The
        // module's own line is written where the fact is built, which is the point being asserted here.
        Require(w.Infos.Any(i => i.StartsWith("weapon.hit-fact", StringComparison.Ordinal)
            && i.Contains("target=" + enemy.Reference.Id, StringComparison.Ordinal)),
            "A candidate naming an enemy was not admitted: " + string.Join(" | ", w.Infos));
        // A player limb goes through the player domain's own key, which is the SNet_Player the agent belongs to.
        // The lookup is read from the calls this hit itself made: the kernel resolves the equipment life's own
        // owner after the fact is built, so the whole session's last lookup is the shooter's and not the target's.
        var before = w.LookupInputs.Count;
        Hook(typeof(BulletHit), weapon, Hit(p.Agent, 1, 2, 3, other.Agent));
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("target").GetProperty("id").GetString() == other.Reference.Id
            && w.LookupInputs.Skip(before).Any(input => ReferenceEquals(input, other.Net)),
            "A player hit did not name the player reference: " + w.Session.Adapter.Published.Outputs.GetRawText()
            + " lookups=[" + string.Join(",", w.LookupInputs.Select(i => i is SNet_Player player && w.PlayerRefs.TryGetValue(player, out var known)
                ? known.Id : i.GetType().Name)) + "]"
            + " expected=" + other.Reference.Id);
    }

    [Fact]
    public void combat_hit_target_is_omitted_when_nothing_can_name_it()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var unregistered = new Enemies.EnemyAgent { GlobalID = 12 };
        var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, new Gear.BulletWeapon());
        Hook(stored, p.Backpack); var weapon = (Gear.BulletWeapon)item.Instance!; weapon.Owner = p.Agent; Fire(w, weapon);
        // A limb whose agent no installed domain registered, and a limb whose agent is missing: both are honest
        // "cannot name it" answers, so neither may appear as a null port.
        Hook(typeof(BulletHit), weapon, Hit(p.Agent, 1, 2, 3, unregistered));
        Require(w.Session!.Adapter.Published!.Outputs.TryGetProperty("target", out _) == false,
            "An unregistered enemy produced a target port: " + w.Session.Adapter.Published.Outputs.GetRawText());
        var noAgent = Hit(p.Agent, 1, 2, 3, unregistered); noAgent.rayHit.collider!.Parent = new Dam_EnemyDamageLimb();
        Hook(typeof(BulletHit), weapon, noAgent);
        Require(w.Session.Adapter.Published!.Outputs.TryGetProperty("target", out _) == false,
            "A limb with no agent produced a target port: " + w.Session.Adapter.Published.Outputs.GetRawText());
        // A hit with no collider at all is the miss the game also reports, and it is still a candidate.
        var noCollider = Hit(p.Agent, 7, 8, 9); noCollider.rayHit.collider = null;
        Hook(typeof(BulletHit), weapon, noCollider);
        Fact(w, w.Session.Adapter.Published, ModuleDefinition.HitCandidateBinding, "gtfo.weapon.hit:7:", "source", "equipment", "limb", "position");
        Require(w.Reports.Count == 0, "A hit that cannot be named was reported as a failure: " + string.Join(" | ", w.Reports));
        // Omitting a port the graph declares optional must still be admitted; with no plan mounted on this
        // binding the kernel is never reached, so the module's own line is what the case reads.
        Require(w.Infos.Any(i => i.StartsWith("weapon.hit-fact", StringComparison.Ordinal)
            && i.Contains("target=unresolved", StringComparison.Ordinal)),
            "A candidate without a target was not admitted: " + string.Join(" | ", w.Infos));
    }

    /// <summary>A hit on a map object — a door, a terminal — carries that object's reference in the same
    /// `target` port an enemy or a player hit uses, and the hit is asked of the kind's own instance lookup with
    /// the collider itself. What a map object is and how it is addressed stays in the domain that owns it: this
    /// package never names a door or a terminal type, and a door the game did not make a zone's entrance is
    /// answered by that domain as nothing.</summary>
    [Fact]
    public void combat_hit_names_the_map_object_its_collider_stands_on()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, new Gear.BulletWeapon());
        Hook(stored, p.Backpack); var weapon = (Gear.BulletWeapon)item.Instance!; weapon.Owner = p.Agent; Fire(w, weapon);
        var shot = w.Session!.Adapter.Published!;
        Require(shot.BindingId == ModuleDefinition.ShotCommittedBinding, "The firing window published no shot fact to compare with.");
        var door = w.MapObject("door/0/0/2/security");
        var doorHit = MapObjectHit(p.Agent, 1, 2, 3, door.Object);
        Hook(typeof(BulletHit), weapon, doorHit);
        Fact(w, w.Session.Adapter.Published, ModuleDefinition.HitCandidateBinding, "gtfo.weapon.hit:7:",
            "source", "equipment", "target", "limb", "position");
        var candidate = w.Session.Adapter.Published!;
        Require(candidate.Outputs.GetProperty("target").GetProperty("id").GetString() == door.Reference.Id,
            "A door hit did not name the door's reference: " + candidate.Outputs.GetRawText());
        // The lookup was handed the object the bullet was resolved against — the collider — and never a door the
        // hit path guessed at: that is the whole of what this package knows about a map object.
        Require(ReferenceEquals(w.MapObjectLookupInputs.Last(), doorHit.rayHit.collider),
            "The map-object kind was not asked with the hit object itself.");
        // A terminal goes through the same kind and the same one leg; the address is the domain's own spelling.
        var terminal = w.MapObject("terminal/0/0/2/0");
        Hook(typeof(BulletHit), weapon, MapObjectHit(p.Agent, 4, 5, 6, terminal.Object));
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("target").GetProperty("id").GetString() == terminal.Reference.Id,
            "A terminal hit did not name the terminal's reference: " + w.Session.Adapter.Published.Outputs.GetRawText());
        Require(w.Reports.Count == 0 && w.Infos.Any(i => i.StartsWith("weapon.hit-fact", StringComparison.Ordinal)
            && i.Contains("target=" + terminal.Reference.Id, StringComparison.Ordinal)),
            "A map-object candidate was not admitted: " + string.Join(" | ", w.Infos) + " " + string.Join(" | ", w.Reports));
    }

    /// <summary>Every hit path asks for the same thing, so the one port a behavior mounts on — the equipment of
    /// the open shot — reads identically whichever of the four answers the kind lookup gives: an enemy, a player,
    /// a map object, or nothing at all. Only `target` differs, and it is absent rather than null when the object
    /// cannot be named.</summary>
    [Fact]
    public void combat_hit_equipment_port_is_the_same_for_every_kind_of_target()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var other = w.Player("b"); var enemy = w.Enemy(11);
        var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, new Gear.BulletWeapon());
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        var weapon = (Gear.BulletWeapon)item.Instance!; weapon.Owner = p.Agent; Fire(w, weapon);
        var mapObject = w.MapObject("door/0/0/2/security");
        var expected = "{\"id\":\"" + entity.Id + "\",\"worldEpoch\":" + entity.WorldEpoch + ",\"lifeEpoch\":1}";
        var kinds = new (string Kind, WeaponType.WeaponHitData Hit, string? Target)[]
        {
            ("enemy", Hit(p.Agent, 1, 2, 3, enemy.Agent), enemy.Reference.Id),
            ("player", Hit(p.Agent, 1, 2, 3, other.Agent), other.Reference.Id),
            ("map-object", MapObjectHit(p.Agent, 1, 2, 3, mapObject.Object), mapObject.Reference.Id),
            ("unresolved", Hit(p.Agent, 1, 2, 3), null)
        };
        foreach (var (kind, hit, target) in kinds)
        {
            Hook(typeof(BulletHit), weapon, hit);
            var candidate = w.Session!.Adapter.Published!;
            Require(candidate.BindingId == ModuleDefinition.HitCandidateBinding, "A " + kind + " hit published no candidate.");
            var outputs = candidate.Outputs;
            Require(outputs.GetProperty("equipment").GetRawText() == expected,
                "A " + kind + " hit changed the equipment port: " + outputs.GetRawText());
            Require(outputs.GetProperty("source").GetProperty("id").GetString() == p.Reference.Id,
                "A " + kind + " hit changed the source port: " + outputs.GetRawText());
            Require(outputs.TryGetProperty("target", out var named) == (target != null)
                && (target == null || named.GetProperty("id").GetString() == target),
                "A " + kind + " hit carried the wrong target port: " + outputs.GetRawText());
        }
    }

    /// <summary>The candidate names the equipment life that fired the shot rather than the object that was hit:
    /// the reference is the one the open window already holds, so the fact carries exactly what the same shot's
    /// own fact carried, which is what lets a mount on that gear block claim its own weapon's hit.</summary>
    [Fact]
    public void combat_hit_candidate_carries_the_equipment_of_its_own_shot()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, new Gear.BulletWeapon());
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        var weapon = (Gear.BulletWeapon)item.Instance!; weapon.Owner = p.Agent; Fire(w, weapon);
        var shot = w.Session!.Adapter.Published!;
        Require(shot.BindingId == ModuleDefinition.ShotCommittedBinding, "The firing window published no shot fact to compare with.");
        Hook(typeof(BulletHit), weapon, Hit(p.Agent, 1, 2, 3));
        Fact(w, w.Session.Adapter.Published, ModuleDefinition.HitCandidateBinding, "gtfo.weapon.hit:7:", "source", "equipment", "limb", "position");
        var candidate = w.Session.Adapter.Published!;
        Require(candidate.Outputs.GetProperty("equipment").GetRawText() == shot.Outputs.GetProperty("equipment").GetRawText()
            && candidate.Outputs.GetProperty("equipment").GetProperty("id").GetString() == entity.Id,
            "The candidate did not carry its own shot's equipment life: shot=" + shot.Outputs.GetRawText() + " candidate=" + candidate.Outputs.GetRawText());
        // The candidate and the shot are the same window, which the event scope names.
        Require(candidate.ScopeId.Contains(entity.Id, StringComparison.Ordinal) && shot.ScopeId.Contains(entity.Id, StringComparison.Ordinal),
            "The two facts of one window did not name the same equipment: shot=" + shot.ScopeId + " candidate=" + candidate.ScopeId);
    }

    /// <summary>A player who takes out another gun fires that weapon, so the window open afterwards belongs to
    /// the other equipment life and the hit inside it must name that life, not the one that fired before the swap.
    /// This is what makes a behavior mounted on a gear block follow the weapon actually in hand.</summary>
    [Fact]
    public void combat_hit_after_a_weapon_swap_carries_the_new_equipment_life()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, new Gear.BulletWeapon());
        Hook(stored, p.Backpack); var firstEntity = w.EntityOf(first.Instance!)!;
        var rifle = (Gear.BulletWeapon)first.Instance!; rifle.Owner = p.Agent; Fire(w, rifle);
        Hook(typeof(BulletHit), rifle, Hit(p.Agent, 1, 2, 3));
        Require(w.Session!.Adapter.Published!.Outputs.GetProperty("equipment").GetProperty("id").GetString() == firstEntity.Id,
            "The first window's candidate did not name the first life: " + w.Session.Adapter.Published.Outputs.GetRawText());
        // The swap: the other gun is taken out and fired, which opens the window on its own life.
        var second = World.Put(p.Backpack, InventorySlot.GearSpecial, 5678, new Gear.Shotgun());
        Hook(stored, p.Backpack); var secondEntity = w.EntityOf(second.Instance!)!;
        Require(secondEntity.Id != firstEntity.Id, "The swapped-in weapon reused the first weapon's equipment life.");
        var shotgun = (Gear.Shotgun)second.Instance!; shotgun.Owner = p.Agent; Fire(w, shotgun);
        Require(w.Session.Adapter.Published!.BindingId == ModuleDefinition.ShotCommittedBinding
            && w.Session.Adapter.Published.Outputs.GetProperty("equipment").GetProperty("id").GetString() == secondEntity.Id,
            "The swap did not open a shot window on the new life: " + w.Session.Adapter.Published.Outputs.GetRawText());
        Hook(typeof(BulletHit), shotgun, Hit(p.Agent, 4, 5, 6));
        Fact(w, w.Session.Adapter.Published, ModuleDefinition.HitCandidateBinding, "gtfo.weapon.hit:7:", "source", "equipment", "limb", "position");
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("equipment").GetProperty("id").GetString() == secondEntity.Id,
            "The hit after the swap still carried the old weapon's life: " + w.Session.Adapter.Published.Outputs.GetRawText());
    }

    /// <summary>The other half of the Fire slot: every machine but the shooter's holds that player's weapon as a
    /// synced instance and replays the replicated shot count through it, so the synced bodies are the only place a
    /// remote player's trigger pull exists here. One replay is one shot, the unsynced bodies are not reached from
    /// them, and the rifle and shotgun classes that declare no Fire of their own use their family's body.</summary>
    [Fact]
    public void combat_synced_fire_body_publishes_the_other_players_one_shot()
    {
        Reset();
        using var w = new World(); var host = w.Player("host"); var client = w.Player("client", synced: true);
        Require(host.Reference.Id != client.Reference.Id, "The fixture reused one player reference for two players.");
        var rifle = World.Put(client.Backpack, InventorySlot.GearStandard, 1234, new Gear.RifleWeaponSynced());
        Hook(stored, client.Backpack); var rifleEntity = w.EntityOf(rifle.Instance!)!;
        var weapon = (Gear.BulletWeaponSynced)rifle.Instance!; weapon.Owner = client.Agent;
        Fire(w, weapon);
        Fact(w, w.Session!.Adapter.Published, ModuleDefinition.ShotCommittedBinding, "gtfo.weapon.shot:7:", "source", "equipment", "index");
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("equipment").GetProperty("id").GetString() == rifleEntity.Id
            && w.Session.Adapter.Published!.Outputs.GetProperty("index").GetInt64() == 1
            && w.Session.Adapter.Published!.Outputs.GetProperty("source").GetProperty("id").GetString() == client.Reference.Id,
            "The synced shot fact did not name the client's life, the client and the first shot: " + w.Session.Adapter.Published.Outputs.GetRawText());
        var firstId = w.Session.Adapter.Published!.EventId;
        // The synced shotgun declares its own body: one replay there is one shot of its own life, and the rifle's
        // next replay advances that life by one instead of publishing a second fact for the first pull.
        var shotgun = World.Put(client.Backpack, InventorySlot.GearSpecial, 5678, new Gear.ShotgunSynced());
        Hook(stored, client.Backpack); var shotgunEntity = w.EntityOf(shotgun.Instance!)!;
        var syncedShotgun = (Gear.ShotgunSynced)shotgun.Instance!; syncedShotgun.Owner = client.Agent;
        Fire(w, syncedShotgun);
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("equipment").GetProperty("id").GetString() == shotgunEntity.Id
            && w.Session.Adapter.Published!.Outputs.GetProperty("index").GetInt64() == 1
            && w.Session.Adapter.Published!.Outputs.GetProperty("source").GetProperty("id").GetString() == client.Reference.Id,
            "The synced shotgun's shot was not one shot of its own life: " + w.Session.Adapter.Published.Outputs.GetRawText());
        var shotgunId = w.Session.Adapter.Published!.EventId;
        Fire(w, weapon);
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("index").GetInt64() == 2
            && w.Session.Adapter.Published!.Outputs.GetProperty("equipment").GetProperty("id").GetString() == rifleEntity.Id,
            "A second synced shot did not advance the same life's index by one: " + w.Session.Adapter.Published.Outputs.GetRawText());
        Require(w.Session.Adapter.Published!.EventId != firstId && w.Session.Adapter.Published!.EventId != shotgunId,
            "A synced shot reused another shot's event id.");
    }

    [Fact]
    public void combat_hit_without_an_open_shot_is_not_a_candidate()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, new Gear.BulletWeapon());
        Hook(stored, p.Backpack); var weapon = (Gear.BulletWeapon)item.Instance!; Fire(w, weapon);
        var shot = w.Session!.Adapter.Published!;
        // The weapon's owner is left unset, so this Fire opened no shot at all (`weapon.shot-owner-unresolved`):
        // the hit that follows has no window to belong to and must not be published on its own.
        Hook(typeof(BulletHit), weapon, Hit(p.Agent, 1, 2, 3));
        Require(ReferenceEquals(w.Session.Adapter.Published, shot), "A hit outside any shot published a candidate.");
        Require(w.Reports.Count == 0 && w.Infos.Any(i => i.Contains("weapon.hit-outside-shot")), "The dropped hit was not reported.");
    }

    /// <summary>The sentry never runs a weapon Fire body: its own firing component reaches the static bullet routine
    /// directly, so its rays arrive with no open shot and are not billed to whichever player happens to be around.
    /// `BulletHit` is static, so the game calls it with no weapon instance — the shape this case uses.</summary>
    [Fact]
    public void combat_sentry_fire_is_not_billed_to_a_player()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        var item = World.Put(p.Backpack, InventorySlot.GearStandard, 4321, new SentryGunInstance());
        Hook(stored, p.Backpack); var sentry = (SentryGunInstance)Deploy(w, p.Backpack, p.Agent, item);
        Require(w.Session!.Adapter.PlacementOf(sentry) != null, "The fixture sentry was not tracked as deployed.");
        var deployed = w.Session.Adapter.Published;
        Hook(typeof(BulletHit), null!, Hit(p.Agent, 1, 2, 3));
        Require(ReferenceEquals(w.Session.Adapter.Published, deployed), "A sentry's own shot was billed to a player.");
        Require(w.Reports.Count == 0 && w.Infos.Any(i => i.Contains("weapon.hit-outside-shot")), "The discarded sentry hit was not reported.");
    }

    [Fact]
    public void combat_client_publishes_nothing()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, new Gear.BulletWeapon());
        Hook(stored, p.Backpack); SNet.IsMaster = false;
        var weapon = (Gear.BulletWeapon)item.Instance!;
        Fire(w, weapon); Hook(typeof(BulletHit), weapon, Hit(p.Agent, 1, 2, 3));
        Require(w.Session!.Adapter.Published == null && w.Sink.Count == 0, "A non-master client published a combat fact.");
        SNet.IsMaster = true;
    }

    [Fact]
    public void combat_shot_without_the_equipment_life_publishes_nothing()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        var loose = new Gear.BulletWeapon { Owner = p.Agent };
        Fire(w, loose);
        Require(w.Session!.Adapter.Published == null, "A weapon with no recorded life published a shot.");
    }

    [Fact]
    public void deploy_recall_redeploy_ends_lives_once()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 4321, new SentryGunInstance());
        Hook(stored, p.Backpack); var equipment = w.EntityOf(item.Instance!)!;
        var sentry = (SentryGunInstance)Deploy(w, p.Backpack, p.Agent, item);
        Fact(w, w.Session!.Adapter.Published, ModuleDefinition.DeployCompletedBinding, "gtfo.equipment.deploy:7:", "actor", "deployed", "equipment_kind", "position");
        var first = w.Session.Adapter.Published!.Outputs.GetProperty("deployed").GetProperty("id").GetString()!;
        Require(w.Session.Adapter.PlacementCount == 1 && w.Session.Identity.Count == 1 && w.PlacementOf(sentry) != null, "Placement was not tracked.");
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("actor").GetProperty("id").GetString() == p.Reference.Id, "Deploy did not name the deploying player.");
        Require(w.Session.Adapter.ObserveDeployable(new EntityReference(first, 7, 1)) != null
            && w.Session.Identity.IsCurrent(new EntityReference(first, 7, 1)),
            "The deployed instance is not current: first=" + first + " observed="
            + (w.Session.Adapter.ObserveDeployable(new EntityReference(first, 7, 1)) != null)
            + " identity=" + w.Session.Identity.IsCurrent(new EntityReference(first, 7, 1))
            + " count=" + w.Session.Adapter.PlacementCount + " " + string.Join(" | ", w.Infos));
        // Re-running the same spawn body must not mint a second identity for the same world object.
        Hook(typeof(SentryPlaced), sentry);
        Require(w.Session.Adapter.PlacementOf(sentry)!.Entity.Id == first && w.Session.Adapter.PlacementCount == 1, "Repeated spawn replaced or duplicated the placement.");

        sentry.SyncedPickup(p.Agent);
        Hook(typeof(SentryRecalled), sentry);
        Fact(w, w.Session.Adapter.Published, ModuleDefinition.DespawnedBinding, "gtfo.equipment.despawn:7:", "entity", "reason");
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("reason").GetString() == "recalled"
            && w.Session.Adapter.PlacementOf(sentry) == null && w.Session.Adapter.PlacementCount == 0, "Recall did not end the placement.");
        var recall = w.Session.Adapter.Published;
        sentry.OnDestroy(); Hook(typeof(SentryWorldDestroyed), sentry);
        Require(ReferenceEquals(w.Session.Adapter.Published, recall), "The destroy path ended an already recalled placement.");

        p.Backpack.SetDeployed(InventorySlot.GearStandard, true);
        Hook(typeof(BackpackItemDeployed), p.Backpack);
        sentry.OnSpawn(); Hook(typeof(SentryPlaced), sentry);
        var second = w.Session.Adapter.Published!.Outputs.GetProperty("deployed").GetProperty("id").GetString()!;
        Require(second != first && w.Session.Adapter.PlacementCount == 1, "Redeploy reused the recalled identity.");
        Require(!w.Session.Identity.IsCurrent(new EntityReference(first, 7, 1)) && w.Session.Adapter.ObserveDeployable(new EntityReference(first, 7, 1)) == null,
            "The recalled instance is still resolvable.");
        Require(w.Session.Adapter.ObserveDeployable(new EntityReference(second, 7, 1)) != null,
            "The redeployed instance is not resolvable: second=" + second + " count=" + w.Session.Adapter.PlacementCount
            + " " + string.Join(" | ", w.Infos));
        Require(w.Session.Identity.IsCurrent(equipment), "Recall or redeploy ended the equipment life that places it.");
    }

    [Fact]
    public void deploy_despawn_then_destroy_publishes_once()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 4321, new SentryGunInstance());
        Hook(stored, p.Backpack); var sentry = (SentryGunInstance)Deploy(w, p.Backpack, p.Agent, item, InventorySlot.GearSpecial);
        sentry.OnDespawn(); Hook(typeof(SentryWorldDespawned), sentry);
        var despawn = w.Session!.Adapter.Published!;
        Require(w.Session.Adapter.Published!.Outputs.GetProperty("reason").GetString() == "despawned" && w.Session.Adapter.PlacementCount == 0,
            "OnDespawn did not end the placement.");
        sentry.OnDestroy(); Hook(typeof(SentryWorldDestroyed), sentry);
        Require(ReferenceEquals(w.Session.Adapter.Published, despawn), "A destroy after a despawn published a second ending.");
        Require(w.Sink.Count == 0 && w.Reports.Count == 0, "An ending was rejected: " + string.Join(" | ", w.Reports));
    }

    [Fact]
    public void deploy_mine_family_uses_its_own_ending_paths()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearClass, 99, new MineDeployerInstance());
        Hook(stored, p.Backpack); var mine = (MineDeployerInstance)Deploy(w, p.Backpack, p.Agent, item, InventorySlot.GearClass);
        Fact(w, w.Session!.Adapter.Published, ModuleDefinition.DeployCompletedBinding, "gtfo.equipment.deploy:7:", "actor", "deployed", "equipment_kind", "position");
        var deployed = w.Session.Adapter.Published!.Outputs.GetProperty("deployed").GetProperty("id").GetString()!;
        Require(w.Session.Adapter.ObserveDeployable(new EntityReference(deployed, 7, 1)) != null, "The mine instance is not resolvable.");
        mine.SyncedPickup(p.Agent); Hook(typeof(MineRecalled), mine);
        // The mine family declares no OnDespawn; a pickup is its recall, and it is also the ending of the life.
        Require(w.Session.Adapter.Published!.BindingId == ModuleDefinition.DespawnedBinding
            && w.Session.Adapter.Published!.Outputs.GetProperty("reason").GetString() == "recalled"
            && w.Session.Adapter.PlacementCount == 0, "Mine pickup did not end the placement.");
        mine.OnDestroy(); Hook(typeof(MineWorldDestroyed), mine);
        Require(w.Session.Adapter.PlacementCount == 0 && w.Sink.Count == 0, "Mine destroy republished or was rejected.");
    }

    [Fact]
    public void deploy_world_change_closes_placements_without_a_stale_fact()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 4321, new SentryGunInstance());
        Hook(stored, p.Backpack); var sentry = (SentryGunInstance)Deploy(w, p.Backpack, p.Agent, item);
        var deployed = w.Session!.Adapter.Published!.Outputs.GetProperty("deployed").GetProperty("id").GetString()!;
        var fact = w.Session.Adapter.Published;
        w.Kernel.BeginWorld(8); w.Tick();
        // A level change that never runs a spawn, pickup or destroy hook leaves the placement in flight; the next
        // hook-driven read is where the adapter notices it belongs to a world the kernel has already left.
        w.Session.Adapter.SyncWorld();
        Require(w.Session.Adapter.PlacementCount == 0 && !w.Current(new EntityReference(deployed, 8, 1))
            && w.Session.Adapter.ObserveDeployable(new EntityReference(deployed, 7, 1)) == null,
            "A placement survived the world change: count=" + w.Session.Adapter.PlacementCount
            + " current8=" + w.Current(new EntityReference(deployed, 8, 1))
            + " observed7=" + (w.Session.Adapter.ObserveDeployable(new EntityReference(deployed, 7, 1)) != null)
            + " " + string.Join(" | ", w.Infos));
        // A new-world epoch stamped on an old-world placement would be a fact about a world it never existed in.
        Require(ReferenceEquals(w.Session.Adapter.Published, fact) && w.Sink.Count == 0, "The world change invented a despawn fact.");
        Require(w.Infos.Any(i => i.Contains("weapon.deployments-closed")), "The closed placements were not reported.");
        var reissued = new EntityReference("gtfo.player:a", 8, 1);
        w.PlayerRefs[p.Net] = reissued; w.LivePlayers.Clear(); w.LivePlayers.Add(reissued);
        Hook(stored, p.Backpack);
        sentry.Owner = p.Agent;
        p.Backpack.SetDeployed(InventorySlot.GearStandard, true);
        sentry.OnSpawn(); Hook(typeof(SentryPlaced), sentry);
        var redeployed = w.Session.Adapter.Published!.Outputs.GetProperty("deployed").GetProperty("id").GetString()!;
        Require(redeployed != deployed && w.Session.Adapter.PlacementCount == 1, "The new world did not re-observe the deployed object.");
    }

    [Fact]
    public void deploy_level_change_paths_both_end_exactly_once()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1, new SentryGunInstance());
        var second = World.Put(p.Backpack, InventorySlot.GearSpecial, 2, new SentryGunInstance());
        Hook(stored, p.Backpack);
        // Two world instances in two slots, so every ending below is one placement ending once, not one call path.
        var a = (SentryGunInstance)Deploy(w, p.Backpack, p.Agent, first); var b = (SentryGunInstance)Deploy(w, p.Backpack, p.Agent, second, InventorySlot.GearSpecial, placed: 2);
        Hook(typeof(SentryPlaced), a); Hook(typeof(SentryPlaced), b);
        Require(w.Session!.Adapter.PlacementCount == 2, "Two placed instances were not both tracked.");
        a.OnDespawn(); Hook(typeof(SentryWorldDespawned), a);
        b.OnDestroy(); Hook(typeof(SentryWorldDestroyed), b);
        Require(w.Session.Adapter.PlacementCount == 0 && w.Reports.Count == 0, "A level-change ending was refused or left open.");
        // Both paths end the same way, and each instance ends only once even if the game runs both.
        a.OnDestroy(); Hook(typeof(SentryWorldDestroyed), a); b.OnDespawn(); Hook(typeof(SentryWorldDespawned), b);
        Require(w.Session.Adapter.PlacementCount == 0 && w.Reports.Count == 0, "A repeated ending published or was refused.");
    }

    [Fact]
    public void deploy_equipment_observer_names_the_open_placement()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 4321, new SentryGunInstance());
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        var instance = (ItemEquippable)item.Instance!;
        instance.transform.position = new UnityEngine.Vector3(1, 2, 3);
        Deploy(w, p.Backpack, p.Agent, item);
        var deployed = w.Session!.Adapter.Published!.Outputs.GetProperty("deployed").GetProperty("id").GetString()!;
        var equipment = w.Session.Adapter.ObserveEquipment(entity);
        Require(equipment != null && equipment.Tags.SequenceEqual(new[] { "deployed", deployed }) && equipment.Position.SequenceEqual(new[] { 1d, 2d, 3d })
            && equipment.Faction == p.Reference.Id, "The equipment observer did not name its open placement.");
    }

    // The deployment fact reports where the world instance stands, read from that instance after the spawn body
    // ran. It is never the requesting tool's position: the world object is the only thing that knows where the
    // placement ended up, and a second path for the same number is exactly what this port must not become.
    [Fact]
    public void deploy_completed_reports_the_world_instances_own_position()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 4321, new SentryGunInstance());
        Hook(stored, p.Backpack);
        var instance = (ItemEquippable)item.Instance!;
        instance.transform.position = new UnityEngine.Vector3(12.5f, -3f, 0.25f);
        Deploy(w, p.Backpack, p.Agent, item);
        var published = w.Session!.Adapter.Published!;
        Require(published.Outputs.GetProperty("position").EnumerateArray().Select(v => v.GetDouble()).SequenceEqual(new[] { 12.5d, -3d, 0.25d }),
            "The deployment fact did not carry the world instance's own position: " + published.Outputs.GetRawText());
    }

    // The spawn body is the other path into the same fact, and it reads the same instance: a sentry that the game
    // spawns on its own reports the position that instance already holds, not a position from the placing tool.
    [Fact]
    public void deploy_completed_from_the_spawn_body_reads_that_instance()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 4321, new SentryGunInstance());
        Hook(stored, p.Backpack);
        var sentry = (SentryGunInstance)item.Instance!;
        sentry.Owner = p.Agent;
        sentry.transform.position = new UnityEngine.Vector3(0f, -120f, 4f);
        sentry.OnSpawn(); Hook(typeof(SentryPlaced), sentry);
        var published = w.Session!.Adapter.Published!;
        Require(published.BindingId == ModuleDefinition.DeployCompletedBinding
            && published.Outputs.GetProperty("position").EnumerateArray().Select(v => v.GetDouble()).SequenceEqual(new[] { 0d, -120d, 4d }),
            "The spawn-path deployment fact did not carry the instance's own position: " + published.Outputs.GetRawText());
    }

    // A placement whose object cannot report a finite world point still publishes its identity, and leaves the
    // optional position port out entirely: an absent port says "not observable", which a null or a copied
    // request coordinate would misreport as a real world position.
    [Fact]
    public void deploy_completed_without_a_readable_world_position_omits_the_port()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 4321, new SentryGunInstance());
        Hook(stored, p.Backpack);
        var instance = (ItemEquippable)item.Instance!;
        // The object is gone, which is what a Unity object without a transform is: the read refuses it, and the
        // placement itself is unaffected because nothing else about this instance changed.
        instance.transform = null!;
        Deploy(w, p.Backpack, p.Agent, item);
        var published = w.Session!.Adapter.Published!;
        Require(published.BindingId == ModuleDefinition.DeployCompletedBinding
            && published.Outputs.TryGetProperty("position", out _) == false
            && published.Outputs.GetProperty("deployed").GetProperty("id").GetString() == "gtfo.equipment:7.1.1",
            "An unreadable world position did not leave the port out: " + published.Outputs.GetRawText());
        Require(w.Sink.Count == 0 && w.Reports.Count == 0, "A placement without a position was rejected: " + string.Join(" | ", w.Reports));
    }

    [Fact]
    public void capture_initial_snapshot_has_no_history()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        p.Inventory.WieldedItem = (ItemEquippable)item.Instance!;
        Hook(stored, p.Backpack);
        var entity = w.EntityOf(item.Instance!);
        Require(entity != null && entity.Id == "gtfo.equipment:7.1" && entity.WorldEpoch == 7 && entity.LifeEpoch == 1, "Stored item was not recorded.");
        Require(w.Session!.Identity.TryResolve(entity!, out var o, out var code), "Recorded item is not current: " + code);
        Require(o!.ResourceId == "gtfo.gear:1234" && o.ResourceRevision == EquipmentNativeAdapter.ResourceRevision && o.Owner == p.Reference
            && o.Slot == "GearStandard" && o.Location == EquipmentLocation.Inventory && o.IsReady && o.IsWielded, "Readback fields differ.");
        w.Tick(); Require(w.Sink.Count == 0, "First sighting synthesized a wield fact.");
    }

    [Fact]
    public void capture_item_without_gear_range_uses_item_id()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.ResourcePack, 0);
        item.GearIDRange = null; item.ItemID = 102; Hook(stored, p.Backpack);
        Require(w.Session!.Identity.TryResolve(w.EntityOf(item.Instance!)!, out var o, out _) && o!.ResourceId == "gtfo.item:102"
            && o.Slot == "ResourcePack", "Non-gear item resource identity differs.");
    }

    [Fact]
    public void gear_block_mount_matches_only_the_block_the_gear_came_from()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, offlineGearBlock: 10001);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        Require(w.GearBlockOf(item) == "10001", "The instance did not read its own offline gear block text back.");
        Require(w.MatchesGearBlock("10001", entity), "The gear's own block text did not match.");
        Require(!w.MatchesGearBlock("10002", entity), "Another block matched the same gear.");
        Require(!w.MatchesGearBlock("4294967295", entity), "The same gear matched a different block id.");
        // Another block's id is a mismatch, not a malformed reference: only a text that cannot be a block id at all
        // is reported, and that report is the next case's subject.
        Require(w.Reports.Count == 0, "Another block's own reference was reported as invalid: " + string.Join(" | ", w.Reports));
        // The block id has exactly one spelling: every text below is a second spelling of it or not an id at all.
        foreach (var reference in new[] { "", " ", "0x2711", "10001 ", " 10001", "+10001", "-1", "10001a", "10001.0",
            "010001", "0010001", "4294967296", "99999999999" })
            Require(!w.MatchesGearBlock(reference, entity), "Reference `" + reference + "` was accepted.");
        Require(w.MatchesGearBlock(reference: null, entity) == false, "A missing reference was accepted.");
    }

    [Fact]
    public void gear_block_mount_reports_each_unmatchable_reference_once()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, offlineGearBlock: 10001);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        // Each text below is a second spelling of the block id or not a decimal id at all, so the mount can never
        // match it; the plan asks again on every event, so the refusal is reported once per text, not once per ask.
        var invalid = new[] { "010001", "+10001", " 10001", "10001.0", "", "0x2711", "4294967296", "10001a" };
        foreach (var reference in invalid)
            for (var attempt = 0; attempt < 2; attempt++)
                Require(!w.MatchesGearBlock(reference, entity), "Reference `" + reference + "` was accepted.");
        Require(w.Reports.Count == invalid.Length && invalid.All(reference =>
                w.Reports.Count(report => report.Contains("\"" + reference + "\"", StringComparison.Ordinal)) == 1),
            "Unmatchable references were not reported exactly once each: " + string.Join(" | ", w.Reports));
        Require(w.Reports.All(report => report.StartsWith("weapon.gear-block-reference-invalid:", StringComparison.Ordinal)),
            "An unmatchable reference was reported as something else: " + string.Join(" | ", w.Reports));
        // A reference the current block does not spell but that is a well-formed block id is a mismatch, not a fault.
        Require(!w.MatchesGearBlock("10002", entity) && w.Reports.Count == invalid.Length,
            "A well-formed reference to another block was reported as invalid.");
    }

    [Fact]
    public void gear_block_mount_needs_the_games_own_record_spelling()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        // The record is read as one text, so a record that does not carry the game's own prefix, and one whose
        // suffix is not a plain decimal id, read no block id at all rather than being guessed at.
        var bare = World.Put(p.Backpack, InventorySlot.GearStandard, 1234); bare.GearIDRange!.PlayfabItemInstanceId = "10001";
        var padded = World.Put(p.Backpack, InventorySlot.GearSpecial, 4321); padded.GearIDRange!.PlayfabItemInstanceId = "OfflineGear_ID_010001";
        Hook(stored, p.Backpack);
        Require(w.GearBlockOf(bare) == null && w.GearBlockOf(padded) == null, "A record this build does not spell read a block id.");
        Require(!w.MatchesGearBlock("10001", w.EntityOf(bare.Instance!)!), "A record without the prefix matched its own digits.");
        Require(!w.MatchesGearBlock("010001", w.EntityOf(padded.Instance!)!) && !w.MatchesGearBlock("10001", w.EntityOf(padded.Instance!)!),
            "A zero-padded record matched a block id.");
        Require(w.Reports.Count == 1 && w.Reports[0].Contains("\"010001\"", StringComparison.Ordinal),
            "The zero-padded reference was not reported once: " + string.Join(" | ", w.Reports));
    }

    [Fact]
    public void gear_block_mount_refuses_other_domains_and_gone_lives()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, offlineGearBlock: 10001);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        Require(!w.MatchesGearBlock("10001", p.Reference), "A player reference matched a gear block.");
        Require(!w.MatchesGearBlock("10001", new EntityReference("gtfo.equipment:7.404", 7, 1)), "An unrecorded life matched.");
        Require(!w.MatchesGearBlock("10001", new EntityReference(entity.Id, 8, 1)), "A life from another world matched.");
        // The same slot holding a gear the game did not build from an offline block reads no id at all, and the
        // life it replaced is gone rather than remembered.
        var plain = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack);
        Require(w.GearBlockOf(plain) == null, "A gear without an offline record read a block id.");
        Require(!w.MatchesGearBlock("10001", w.EntityOf(plain.Instance!)!), "A gear without an offline record matched.");
        Require(!w.MatchesGearBlock("10001", entity), "A replaced life still matched.");
    }

    [Fact]
    public void gear_block_plan_loads_and_dispatches_only_for_its_own_block()
    {
        Reset();
        using var w = new World(start: false); var p = w.Player("a");
        var mounted = World.Put(p.Backpack, InventorySlot.GearStandard, 1234, offlineGearBlock: 10001);
        var other = World.Put(p.Backpack, InventorySlot.GearSpecial, 4321, offlineGearBlock: 10002);
        w.StartSession();
        // Loading is the first assertion: an unregistered kind is refused before the runtime starts.
        w.StartRuntime(w.Plan(new object[] { new { kind = "gear-block", reference = "10001" } }));
        Hook(stored, p.Backpack);
        var mountedEntity = w.EntityOf(mounted.Instance!)!; var otherEntity = w.EntityOf(other.Instance!)!;
        p.Inventory.WieldedItem = (ItemEquippable)other.Instance!; Hook(typeof(LocalItemWielded), p.Inventory); w.Tick();
        Require(w.Sink.Count == 0, "A gear from another block dispatched the plan.");
        p.Inventory.WieldedItem = (ItemEquippable)mounted.Instance!; Hook(typeof(LocalItemWielded), p.Inventory); w.Tick();
        Require(w.Sink.Count == 1 && w.Sink[0].Target == mountedEntity && w.Sink[0].Actor == p.Reference,
            "The mounted gear did not dispatch the plan: " + string.Join(" | ", w.Reports));
        Require(otherEntity != mountedEntity && w.Sink.All(s => s.Target != otherEntity), "The other block's gear was dispatched.");
    }

    [Fact]
    public void wield_observed_flip_publishes_equipped_then_unequipped()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(typeof(LocalItemWielded), p.Inventory); w.Tick();
        Require(w.Sink.Count == 1 && w.Sink[0].EventId.StartsWith("gtfo.equipment.equipped:7:", StringComparison.Ordinal)
            && w.Sink[0].Target == entity && w.Sink[0].Actor == p.Reference, "Equipped fact missing or wrong.");
        Hook(typeof(LocalItemWielded), p.Inventory); w.Tick(); Require(w.Sink.Count == 1, "Unchanged readback republished.");
        p.Inventory.WieldedItem = null; Hook(typeof(LocalItemUnwielded), p.Inventory); w.Tick();
        Require(w.Sink.Count == 2 && w.Sink[1].EventId.StartsWith("gtfo.equipment.unequipped:7:", StringComparison.Ordinal)
            && w.Sink[1].Target == entity && w.Sink[1].Actor == p.Reference && w.Sink[1].EventId != w.Sink[0].EventId, "Unequipped fact missing or wrong.");
        Require(w.EntityOf(item.Instance!) == entity, "Wield changed the equipment life.");
    }

    [Fact]
    public void wield_synced_inventory_path()
    {
        Reset();
        using var w = new World(); var p = w.Player("remote", synced: true); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 77);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(typeof(SyncedItemEquipped), p.Inventory); w.Tick();
        p.Inventory.WieldedItem = null; Hook(typeof(SyncedItemUnwielded), p.Inventory); w.Tick();
        Require(w.Sink.Count == 2 && w.Sink[0].EventId.StartsWith("gtfo.equipment.equipped:", StringComparison.Ordinal)
            && w.Sink[1].EventId.StartsWith("gtfo.equipment.unequipped:", StringComparison.Ordinal)
            && w.Sink.All(s => s.Actor == p.Reference && s.Target == entity), "Synced inventory facts differ.");
    }

    [Fact]
    public void wield_unobserved_change_is_stale_not_history()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        p.Inventory.WieldedItem = (ItemEquippable)item.Instance!;
        Require(!w.Current(entity), "Unobserved native change still resolved as the recorded observation.");
        w.Tick(); Require(w.Sink.Count == 0, "Unobserved change produced a fact.");
    }

    [Fact]
    public void wield_destroyed_agent_inventory_ignored()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        p.Agent.Destroyed = true; Hook(typeof(LocalItemWielded), p.Inventory);
        Require(w.Session!.Identity.Count == 0 && !w.Session.Faulted, "Destroyed agent was read or faulted the session.");
    }

    [Fact]
    public void exit_same_slot_new_weapon()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        p.Inventory.WieldedItem = (ItemEquippable)first.Instance!; Hook(stored, p.Backpack); var old = w.EntityOf(first.Instance!)!;
        p.Backpack.Slots![(int)InventorySlot.GearStandard] = null; first.Instance!.Destroyed = true; p.Inventory.WieldedItem = null;
        Hook(typeof(BackpackSlotCleared), p.Backpack);
        Require(!w.Current(old) && w.Session!.Identity.Count == 0, "Cleared slot kept the old life.");
        var next = World.Put(p.Backpack, InventorySlot.GearStandard, 5678); p.Inventory.WieldedItem = (ItemEquippable)next.Instance!;
        Hook(stored, p.Backpack); var replacement = w.EntityOf(next.Instance!)!;
        Require(replacement.Id != old.Id && w.Current(replacement) && !w.Current(old), "Replacement reused or revived the old life.");
        w.Tick(); Require(w.Sink.Count == 0, "Replacement synthesized unequipped/equipped history.");
    }

    [Fact]
    public void exit_same_slot_overwrite_without_clear_hook()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); var old = w.EntityOf(first.Instance!)!;
        var next = World.Put(p.Backpack, InventorySlot.GearStandard, 1234); Hook(stored, p.Backpack);
        var replacement = w.EntityOf(next.Instance!)!;
        Require(replacement.Id != old.Id && !w.Current(old) && w.Current(replacement) && w.Session!.Identity.Count == 1, "Overwritten slot kept the old life.");
        w.Tick(); Require(w.Sink.Count == 0, "Overwrite synthesized history.");
    }

    [Fact]
    public void exit_same_resource_multiple_instances()
    {
        Reset();
        using var w = new World(); var a = w.Player("a"); var b = w.Player("b");
        var items = new[] { World.Put(a.Backpack, InventorySlot.GearStandard, 1234), World.Put(a.Backpack, InventorySlot.GearSpecial, 1234),
            World.Put(b.Backpack, InventorySlot.GearStandard, 1234) };
        Hook(stored, a.Backpack); Hook(stored, b.Backpack);
        var refs = items.Select(i => w.EntityOf(i.Instance!)!).ToArray();
        Require(refs.Select(r => r.Id).Distinct().Count() == 3 && refs.All(w.Current), "Same definition collapsed instances.");
        Require(refs.Select(r => { w.Session!.Identity.TryResolve(r, out var o, out _); return o!.ResourceId; }).Distinct().Single() == "gtfo.gear:1234",
            "Resource identity diverged.");
    }

    [Fact]
    public void exit_transfer_retires_old_owner_without_history()
    {
        Reset();
        using var w = new World(); var a = w.Player("a"); var b = w.Player("b");
        var item = World.Put(a.Backpack, InventorySlot.GearStandard, 1234); a.Inventory.WieldedItem = (ItemEquippable)item.Instance!;
        Hook(stored, a.Backpack); var old = w.EntityOf(item.Instance!)!;
        a.Backpack.Slots![(int)InventorySlot.GearStandard] = null; a.Inventory.WieldedItem = null; Hook(typeof(BackpackSlotCleared), a.Backpack);
        World.Put(b.Backpack, InventorySlot.GearStandard, 1234, item.Instance); Hook(stored, b.Backpack);
        var next = w.EntityOf(item.Instance!)!;
        Require(next.Id != old.Id && !w.Current(old) && w.Current(next), "Transfer kept the old owner's life.");
        Require(w.Session!.Identity.TryResolve(next, out var o, out _) && o!.Owner == b.Reference && !o.IsWielded, "New owner readback differs.");
        Throws("equipment.stale-instance", () => w.Session.Identity.CaptureOwnedUse(old, a.Reference, false));
        w.Tick(); Require(w.Sink.Count == 0, "Transfer history was reconstructed.");
    }

    [Fact]
    public void exit_world_switch_clears_and_reobserves()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); var old = w.EntityOf(item.Instance!)!;
        w.Kernel.BeginWorld(8); w.Tick();
        Require(w.Session!.Identity.Count == 0 && !w.Current(old), "Old world observation survived.");
        var reissued = new EntityReference("gtfo.player:a", 8, 1);
        w.PlayerRefs[p.Net] = reissued; w.LivePlayers.Clear(); w.LivePlayers.Add(reissued);
        Hook(stored, p.Backpack); var next = w.EntityOf(item.Instance!)!;
        Require(next.WorldEpoch == 8 && next.Id == "gtfo.equipment:8.1" && w.Current(next), "New world did not re-observe.");
        w.Tick(); Require(w.Sink.Count == 0, "World switch produced wield history.");
    }

    [Fact]
    public void exit_stale_pointer_reuse_is_a_new_life()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); var old = w.EntityOf(first.Instance!)!; var pointer = first.Instance!.Pointer;
        first.Instance.Destroyed = true; Hook(typeof(BackpackSlotCleared), p.Backpack);
        Require(!w.Current(old) && w.Session!.Identity.Count == 0, "Destroyed instance stayed current.");
        var reused = new ItemEquippable { Pointer = pointer }; World.Put(p.Backpack, InventorySlot.GearStandard, 1234, reused);
        Hook(stored, p.Backpack); var next = w.EntityOf(reused)!;
        Require(next.Id != old.Id && w.Current(next) && !w.Current(old), "Reused native pointer revived the old life.");
    }

    [Fact]
    public void exit_stale_pointer_reuse_without_clear_hook()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var first = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); var old = w.EntityOf(first.Instance!)!;
        first.Instance!.Destroyed = true;
        Require(!w.Current(old), "Destroyed instance resolved before any hook.");
        var reused = new ItemEquippable { Pointer = first.Instance!.Pointer }; World.Put(p.Backpack, InventorySlot.GearStandard, 1234, reused);
        Hook(stored, p.Backpack); var next = w.EntityOf(reused)!;
        Require(next.Id != old.Id && !w.Current(old) && w.Current(next), "New BackpackItem at a reused pointer kept the old life.");
    }

    [Fact]
    public void exit_destroy_all_instances()
    {
        Reset();
        using var w = new World(); var p = w.Player("a");
        var items = new[] { World.Put(p.Backpack, InventorySlot.GearStandard, 1), World.Put(p.Backpack, InventorySlot.GearMelee, 2) };
        Hook(stored, p.Backpack); var refs = items.Select(i => w.EntityOf(i.Instance!)!).ToArray();
        foreach (var item in items) item.Instance!.Destroyed = true;
        Hook(typeof(BackpackInstancesDestroyed), p.Backpack);
        Require(w.Session!.Identity.Count == 0 && w.Session.Adapter.TrackedCount == 0 && !refs.Any(w.Current), "Destroyed instances stayed current.");
        w.Tick(); Require(w.Sink.Count == 0, "Destruction synthesized history.");
    }

    [Fact]
    public void owner_unresolved_player_not_recorded()
    {
        Reset();
        using var w = new World(); var p = w.Player("a", resolved: false); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); Hook(stored, p.Backpack);
        Require(w.Session!.Identity.Count == 0 && w.Session.Adapter.TrackedCount == 0, "Unresolved owner produced equipment identity.");
        Require(w.Reports.Count(r => r.StartsWith("weapon.owner-unresolved", StringComparison.Ordinal)) == 1 && !w.Session.Faulted,
            "Unresolved owner was not reported exactly once.");
    }

    [Fact]
    public void authority_client_or_gated_observation_ignored()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        SNet.IsMaster = false; Hook(stored, p.Backpack); Require(w.Session!.Identity.Count == 0, "Non-master recorded equipment.");
        SNet.IsMaster = true; w.CanExecute = false; Hook(stored, p.Backpack); Require(w.Session.Identity.Count == 0, "Gated host recorded equipment.");
        w.CanExecute = true; w.Tick(host: false); Hook(stored, p.Backpack); Require(w.Session.Identity.Count == 0, "Client tick recorded equipment.");
    }

    [Fact]
    public void owner_non_current_player_reference_is_unresolved()
    {
        Reset();
        // The SDK re-checks the provider's answer, so a reference its resolver no longer accepts never reaches the index.
        using var w = new World(); var p = w.Player("a"); w.LivePlayers.Clear(); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack);
        Require(w.Reports.SequenceEqual(new[] { "weapon.owner-unresolved: backpack items are not recorded without a player reference from its owning domain." })
            && !w.Session!.Faulted && w.Session.Adapter.TrackedCount == 0 && w.Session.Identity.Count == 0, "Non-current owner was recorded or faulted.");
        w.LivePlayers.Add(p.Reference); Hook(stored, p.Backpack);
        Require(w.Session!.Identity.Count == 1, "An unresolved owner latched observation off.");
    }

    [Fact]
    public void owner_resolved_through_sdk_player_lookup()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        Require(w.Session!.Identity.TryResolve(entity, out var o, out _) && o!.Owner == p.Reference, "Owner was not the SDK player reference.");
        Require(w.LookupInputs.Count > 0 && w.LookupInputs.All(i => ReferenceEquals(i, p.Net)),
            "Weapon asked the player lookup about something other than the backpack's SNet_Player.");
    }

    [Fact]
    public void owner_new_player_life_retires_equipment_without_history()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(stored, p.Backpack); var old = w.EntityOf(item.Instance!)!;
        var revived = new EntityReference(p.Reference.Id, p.Reference.WorldEpoch, 2);
        w.PlayerRefs[p.Net] = revived; w.LivePlayers.Clear(); w.LivePlayers.Add(revived);
        Require(!w.Current(old), "Equipment stayed current after its owner's life ended.");
        Hook(stored, p.Backpack); var next = w.EntityOf(item.Instance!)!;
        Require(next.Id != old.Id && w.Current(next) && w.Session!.Identity.TryResolve(next, out var o, out _) && o!.Owner == revived,
            "A new owner life did not start a new equipment life.");
        w.Tick(); Require(w.Sink.Count == 0, "Owner life change synthesized wield history.");
    }

    [Fact]
    public void owner_kind_without_an_instance_lookup_is_unresolved_not_a_fault()
    {
        Reset();
        // Ruling 53: a kind no installed package resolves is an unresolved instance, not a contract error. Nothing
        // is recorded and nothing is published, but the session keeps observing, so a later provider can answer.
        using var w = new World(playerLookup: false); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack);
        Require(!w.Session!.Faulted && w.Session.Identity.Count == 0 && w.Session.Adapter.TrackedCount == 0
            && w.Reports.Count(r => r.StartsWith("weapon.owner-unresolved", StringComparison.Ordinal)) == 1,
            "A kind with no instance lookup did not fail closed: " + w.Session.LastFault + " " + string.Join(" | ", w.Reports));
    }

    [Fact]
    public void owner_lookup_inside_entity_inspection_is_stale_not_fault()
    {
        Reset();
        // Kernel entity inspection forbids nested kernel queries, so equipment cannot be observed through it; it must not latch.
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        var inspected = w.Kernel.InspectEntities(new[] { entity });
        Require(inspected.Items.Single().Code == "stale-entity" && w.Current(entity) && !w.Session!.Faulted,
            "Entity inspection changed equipment state: " + inspected.Items.Single().Code);
    }

    [Fact]
    public void deploy_sentry_slot_reads_back_deployed()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack);
        Require(w.Session!.Identity.TryResolve(entity, out var o, out _) && o!.Location == EquipmentLocation.Deployed
            && o.Slot == null && !o.IsWielded && o.Owner == p.Reference && o.ResourceId == "gtfo.gear:55",
            "Deployed slot did not read back as a deployed location: " + o);
        Require(w.Session.Adapter.TrackedCount == 1 && w.Current(entity), "Deployment replaced the equipment life.");
        w.Tick(); Require(w.Sink.Count == 0, "Deployment synthesized wield history.");
    }

    [Fact]
    public void deploy_pickup_returns_to_inventory()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack);
        p.Backpack.SetDeployed(InventorySlot.GearSpecial, false); Hook(deployed, p.Backpack);
        Require(w.Session!.Identity.TryResolve(entity, out var o, out _) && o!.Location == EquipmentLocation.Inventory
            && o.Slot == nameof(InventorySlot.GearSpecial) && !o.IsWielded, "Recalled deployable did not return to its slot: " + o);
        w.Tick(); Require(w.Sink.Count == 0, "Deploy and recall synthesized wield history.");
    }

    [Fact]
    public void deploy_wielded_deployed_slot_publishes_no_fact()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
        Hook(stored, p.Backpack); w.Tick();
        p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(typeof(LocalItemWielded), p.Inventory); w.Tick();
        Require(w.Sink.Count == 1, "Wielding after the recorded snapshot did not publish.");
        p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack); w.Tick();
        Require(w.Sink.Count == 1, "A deployed slot was reported as unequipped.");
        Require(w.Session!.Identity.TryResolve(w.EntityOf(item.Instance!)!, out var o, out _) && !o!.IsWielded,
            "A deployed slot still read back as wielded.");
    }

    [Fact]
    public void deploy_stale_deployed_observation_is_not_current()
    {
        Reset();
        // IsNativeCurrent re-reads the marker, so a slot that changed state without a hook is stale, not a fault.
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        p.Backpack.SetDeployed(InventorySlot.GearSpecial, true);
        Require(!w.Current(entity) && !w.Session!.Faulted, "Deployed state changed without a hook but stayed current.");
        Hook(deployed, p.Backpack);
        Require(w.Current(entity), "The deploy hook did not refresh the recorded location.");
    }

    [Fact]
    public void log_deploy_location_lines_are_exact()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearSpecial, 55);
        Hook(stored, p.Backpack);
        p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack);
        p.Backpack.SetDeployed(InventorySlot.GearSpecial, true); Hook(deployed, p.Backpack);
        p.Backpack.SetDeployed(InventorySlot.GearSpecial, false); Hook(deployed, p.Backpack);
        Require(w.Infos.SequenceEqual(new[]
        {
            "weapon.equipment-life-started id=gtfo.equipment:7.1 world=7 owner=gtfo.player:a ownerLife=1 slot=GearSpecial resource=gtfo.gear:55 location=Inventory",
            // The marker is the backpack's own readback: it moves the recorded location and mints nothing. A
            // placement is entered from the world instance's own spawn and recall bodies, so an item no sentry or
            // mine instance backs — this one is a plain weapon — never becomes one; the deploy and recall facts
            // are pinned in the placement suite instead.
            "weapon.equipment-location id=gtfo.equipment:7.1 location=Deployed",
            "weapon.equipment-location id=gtfo.equipment:7.1 location=Inventory"
        }) && w.Reports.Count == 0, string.Join(" | ", w.Infos.Concat(w.Reports)));
    }

    [Fact]
    public void contract_rejected_observation_does_not_fault()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        item.IsLoaded = false; p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(stored, p.Backpack);
        Require(w.Reports.Contains("weapon.observation-rejected: equipment.wielded-not-ready") && !w.Session!.Faulted
            && w.Session.Adapter.TrackedCount == 0 && w.Session.Identity.Count == 0, "Rejected observation faulted or leaked a handle.");
        item.IsLoaded = true; Hook(stored, p.Backpack);
        Require(w.Session!.Identity.Count == 1, "A rejection latched observation off.");
    }

    [Fact]
    public void log_life_wield_and_clear_lines_are_exact()
    {
        Reset();
        // These lines are the in-game checklist's expectations; any format drift must fail here first.
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
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
    }

    [Fact]
    public void guard_unexpected_native_failure_latches()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234);
        Hook(stored, p.Backpack); var entity = w.EntityOf(item.Instance!)!;
        p.Backpack.ThrowOnSlots = true; Hook(typeof(BackpackSlotCleared), p.Backpack);
        Require(w.Session!.Faulted && w.Reports.Count(r => r.StartsWith("Weapon equipment observation disabled", StringComparison.Ordinal)) == 1,
            "Unexpected failure did not latch exactly once.");
        p.Backpack.ThrowOnSlots = false; Hook(stored, p.Backpack);
        Require(w.Session.Adapter.TrackedCount == 0 && !w.Current(entity) && w.Reports.Count == 1, "Faulted session kept observing native equipment.");
    }

    [Fact]
    public void session_dispose_unregisters_then_unhooks()
    {
        Reset();
        using var w = new World(); var p = w.Player("a"); World.Put(p.Backpack, InventorySlot.GearStandard, 1234); Hook(stored, p.Backpack);
        var session = w.Session!; w.Kernel.StopRuntime(); session.Dispose();
        using var doc = System.Text.Json.JsonDocument.Parse(w.Kernel.ExportManifest());
        Require(w.Removes == 1 && WeaponNativeSession.Current == null && !doc.RootElement.GetProperty("registry").GetProperty("providers")
            .EnumerateArray().Any(x => x.GetProperty("id").GetString() == ModuleDefinition.ProviderId), "Dispose leaked registration or hooks.");
        Hook(stored, p.Backpack); session.Dispose();
        Require(w.Removes == 1 && WeaponNativeSession.Current == null, "Dispose was not idempotent or a hook revived the session.");
    }

    [Fact]
    public void plugin_off()
    {
        Reset();
        using var w = new World(start: false); Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Off; Host.Runtime = w.Kernel;
        string before = w.Kernel.ExportManifest(); new WeaponPlugin().Load();
        Require(WeaponNativeSession.Current == null && Harmony.Patches == 0 && Harmony.Unpatches == 0 && w.Kernel.ExportManifest() == before,
            "Off activated registration or native hooks.");
    }

    [Fact]
    public void plugin_missing_runtime()
    {
        Reset();
        Host.Runtime = null; var plugin = new WeaponPlugin(); Throws(null, plugin.Load); Throws(null, plugin.Load);
        Require(Harmony.Patches == 0 && WeaponNativeSession.Current == null, "Unavailable host still installed patches.");
    }

    [Fact]
    public void plugin_suspended_host()
    {
        Reset();
        using var w = new World(start: false); Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = w.Kernel;
        Host.Suspension = "I-DIAG-EXAMPLE"; string before = w.Kernel.ExportManifest();
        var plugin = new WeaponPlugin(); plugin.Load();
        Require(WeaponNativeSession.Current == null && Harmony.Patches == 0 && Harmony.Unpatches == 0 && w.Kernel.ExportManifest() == before
            && plugin.Log.Errors.Count == 1 && plugin.Log.Errors[0].Contains("I-DIAG-EXAMPLE", StringComparison.Ordinal),
            "Suspended host installed native work or hid its reason.");
        Host.Suspension = null;
    }

    [Fact]
    public void plugin_invalid_log_level()
    {
        Reset();
        using var w = new World(start: false); Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = w.Kernel;
        var plugin = new WeaponPlugin(); plugin.Config.Preset["Logging.Level"] = "verbose"; Throws(null, plugin.Load);
        Require(WeaponNativeSession.Current == null && Harmony.Patches == 0, "Malformed Logging.Level still installed native work.");
    }

    [Fact]
    public void plugin_existing_weapon_provider_conflict()
    {
        Reset();
        using var w = new World(start: false); Host.Runtime = w.Kernel; var existing = w.Kernel.RegisterModule(ModuleDefinition.Create(), RuntimeLogLevel.Off);
        string before = w.Kernel.ExportManifest(); Throws("provider-conflict", new WeaponPlugin().Load);
        Require(Harmony.Patches == 0 && Harmony.Unpatches == 0 && WeaponNativeSession.Current == null && w.Kernel.ExportManifest() == before,
            "Plugin patched or removed a provider it did not own.");
    }

    [Fact]
    public void plugin_partial_hook_failure()
    {
        Reset();
        using var w = new World(start: false); Host.Runtime = w.Kernel; string before = w.Kernel.ExportManifest(); Harmony.FailPatchAt = 3;
        Throws(null, new WeaponPlugin().Load);
        Require(Harmony.Patches == 3 && Harmony.Unpatches == 1 && WeaponNativeSession.Current == null && w.Kernel.ExportManifest() == before,
            "Partial native load survived rollback.");
    }

    [Fact]
    public void plugin_log_failure_rollback()
    {
        Reset();
        using var w = new World(start: false); Host.Runtime = w.Kernel; string before = w.Kernel.ExportManifest();
        var plugin = new WeaponPlugin(); plugin.Log.ThrowInfo = true; Throws(null, plugin.Load);
        Require(Harmony.Patches == WeaponNativeHooks.Installed.Count && Harmony.Unpatches == 1 && WeaponNativeSession.Current == null
            && w.Kernel.ExportManifest() == before, "Post-registration failure leaked the module or hooks.");
    }

    [Fact]
    public void plugin_cleanup_failure_preserves_cause()
    {
        Reset();
        using var w = new World(start: false); Host.Runtime = w.Kernel; string before = w.Kernel.ExportManifest();
        var plugin = new WeaponPlugin(); plugin.Log.ThrowInfo = true; Harmony.UnpatchFailure = new InvalidOperationException("unpatch");
        try { plugin.Load(); }
        catch (IOException error)
        {
            Require(error.Data.Contains("ForgeWeapon.LoadCleanupFailure") && WeaponNativeSession.Current == null
                && w.Kernel.ExportManifest() == before && plugin.Log.Warnings.Count == 1, "Cleanup failure replaced the cause, stayed current or kept the provider.");
            return;
        }
        throw new Exception("Expected load failure.");
    }

    [Fact]
    public void plugin_success_owner_from_player_namespace_no_hot_reload()
    {
        Reset();
        using var w = new World(start: false); Host.Runtime = w.Kernel; var plugin = new WeaponPlugin();
        plugin.Load();
        var session = WeaponNativeSession.Current;
        Require(session != null && Harmony.Patches == WeaponNativeHooks.Installed.Count && plugin.Log.Infos.Count == 1, "Weapon native session was not installed.");
        Throws(null, plugin.Load);
        Require(Harmony.Patches == WeaponNativeHooks.Installed.Count && !plugin.Unload(), "Repeated Load or hot unload changed native lifetime.");
        w.StartRuntime();
        var p = w.Player("a"); var item = World.Put(p.Backpack, InventorySlot.GearStandard, 1234); Hook(stored, p.Backpack);
        var entity = w.EntityOf(item.Instance!)!;
        p.Inventory.WieldedItem = (ItemEquippable)item.Instance!; Hook(typeof(LocalItemWielded), p.Inventory); w.Tick();
        Require(w.Sink.Count == 1 && w.Sink[0].Actor == p.Reference && w.Sink[0].Target == entity, "Plugin session did not publish the gtfo.player owner.");
        Host.CanExecuteGameplay = false; p.Inventory.WieldedItem = null; Hook(typeof(LocalItemUnwielded), p.Inventory); w.Tick();
        Require(w.Sink.Count == 1, "Host gameplay gate did not reach the adapter.");
        w.Kernel.StopRuntime(); session!.Dispose();
    }
}
