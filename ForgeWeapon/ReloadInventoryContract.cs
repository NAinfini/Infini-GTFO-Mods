using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>The reload and inventory half of the Weapon provider's trigger rows: the three combat reload facts and
/// the six equipment inventory and use facts. The shapes are the runtime's own trigger contract — the provider
/// `forge.contract.trigger` owns the shape of a trigger id and a second copy of one would be the drift this file
/// exists to prevent — so the nine ids below are named here and their shipped rows are read back from
/// <see cref="TriggerContracts"/> where a caller needs them. The one exception is the carried-item row, whose
/// canonical id is this provider's own: no runtime contract declares it, so this file declares it here, in the
/// same shape the runtime gives a trigger row.
///
/// The rows are declared together because they share one lifetime and one readback discipline: the reload rows are
/// decided by a single observation of one reload life, and the inventory rows are decided by one readback of the
/// backpack and pool after a native body returned. Every binding this file names is observe-only — this provider
/// writes no inventory, no pool and no reload state — so the whole family is `implementation-only`.
///
/// A reload life that moved no ammunition closes silently: the node list's `e-w-reload` is start and completion,
/// so there is no interrupted-reload row and no reason code for one. The tool-energy row is gone for the same
/// reason — the node list has no energy-change node, and the game's own `OnBatteryLevelChange` member carries no
/// owner, which made the row the one fact this family could publish for somebody else's tool.</summary>
public static class ReloadInventoryContract
{
    public const string ReloadStartedCapability = "forge.trigger.combat.reload_started";
    public const string ReloadCompletedCapability = "forge.trigger.combat.reload_completed";
    public const string ReloadTransferredCapability = "forge.trigger.combat.reload_transferred";
    public const string RefilledCapability = "forge.trigger.equipment.refilled";
    public const string StackChangedCapability = "forge.trigger.equipment.stack_changed";
    public const string PickedUpCapability = "forge.trigger.equipment.picked_up";
    public const string DroppedCapability = "forge.trigger.equipment.dropped";
    public const string UseStartedCapability = "forge.trigger.equipment.use_started";
    public const string UseFailedCapability = "forge.trigger.equipment.use_failed";
    /// <summary>The large item a player is carrying changing, which is the backpack's own in-level carry slot and
    /// not one of the pockets: the row is about the item a player hauls through the level, and its own capability
    /// row is the one this file declares.</summary>
    public const string CarriedItemChangedCapability = "forge.trigger.player.carried_item_changed";

    /// <summary>The nine capability ids, in the order the catalog and the binding rows below carry them. The
    /// shapes live in the runtime's own trigger contract, which a module cannot declare for itself, so this file
    /// spells the ids once and a caller that walks the family never restates the set.</summary>
    public static readonly IReadOnlyList<string> CapabilityIds = Array.AsReadOnly(new[]
    {
        ReloadStartedCapability, ReloadCompletedCapability, ReloadTransferredCapability, RefilledCapability,
        StackChangedCapability, PickedUpCapability, DroppedCapability, UseStartedCapability, UseFailedCapability
    });

    public const string ReloadStartedBinding = ModuleDefinition.ProviderId + ".binding.reload_started";
    public const string ReloadCompletedBinding = ModuleDefinition.ProviderId + ".binding.reload_completed";
    public const string ReloadTransferredBinding = ModuleDefinition.ProviderId + ".binding.reload_transferred";
    public const string RefilledBinding = ModuleDefinition.ProviderId + ".binding.refilled";
    public const string StackChangedBinding = ModuleDefinition.ProviderId + ".binding.stack_changed";
    public const string PickedUpBinding = ModuleDefinition.ProviderId + ".binding.picked_up";
    public const string DroppedBinding = ModuleDefinition.ProviderId + ".binding.dropped";
    public const string UseStartedBinding = ModuleDefinition.ProviderId + ".binding.use_started";
    public const string UseFailedBinding = ModuleDefinition.ProviderId + ".binding.use_failed";
    public const string CarriedItemChangedBinding = ModuleDefinition.ProviderId + ".binding.carried_item_changed";

    /// <summary>The permissions these facts read under. Ammunition and the equipment that carries it are one
    /// subject, so the reload and inventory rows read the same permission the wield facts already read.</summary>
    public const string AmmunitionReadPermission = ModuleDefinition.WieldReadPermission;

    /// <summary>The one observer the three reload bindings are implemented by, keyed by the equipment entity the
    /// reload belongs to.</summary>
    public const string ReloadHandler = "gtfo.weapon.reload_observe";
    /// <summary>The one observer the six inventory bindings are implemented by: one observer over a player's
    /// backpack, pool and wielded item.</summary>
    public const string InventoryHandler = "gtfo.weapon.inventory_observe";

    /// <summary>The one capability document this family declares: the carried-item row. The other nine ids are
    /// declared by the runtime's trigger contract, and a second declaration of one of them would be refused at
    /// registration as a conflict.</summary>
    public static IReadOnlyList<object> Capabilities() => new object[] { RuntimeJson.Parse(CarriedItemChangedRow) };

    /// <summary>The nine binding rows, in the order the capabilities above are declared, then the carried-item row.
    /// A row is a binding of this provider to a shape the runtime's own trigger contract owns, so it carries no
    /// shape of its own and the module definition only has to append this list.</summary>
    public static IReadOnlyList<object> Bindings() => new object[]
    {
        Binding(ReloadStartedBinding, ReloadStartedCapability, ReloadHandler),
        Binding(ReloadCompletedBinding, ReloadCompletedCapability, ReloadHandler),
        Binding(ReloadTransferredBinding, ReloadTransferredCapability, ReloadHandler),
        Binding(RefilledBinding, RefilledCapability, InventoryHandler),
        Binding(StackChangedBinding, StackChangedCapability, InventoryHandler),
        Binding(PickedUpBinding, PickedUpCapability, InventoryHandler),
        Binding(DroppedBinding, DroppedCapability, InventoryHandler),
        Binding(UseStartedBinding, UseStartedCapability, InventoryHandler),
        Binding(UseFailedBinding, UseFailedCapability, InventoryHandler),
        Binding(CarriedItemChangedBinding, CarriedItemChangedCapability, InventoryHandler)
    };

    /// <summary>One support row per binding. `implementation-only` is the same verification the package's existing
    /// observation bindings carry: the rows are answered by this package's own native observation and by nothing
    /// the runtime can check on its own.</summary>
    public static IReadOnlyList<BindingSupport> Support() => new BindingSupport[]
    {
        new(ReloadStartedBinding, "implementation-only", new[] { AmmunitionReadPermission }),
        new(ReloadCompletedBinding, "implementation-only", new[] { AmmunitionReadPermission }),
        new(ReloadTransferredBinding, "implementation-only", new[] { AmmunitionReadPermission }),
        new(RefilledBinding, "implementation-only", new[] { AmmunitionReadPermission }),
        new(StackChangedBinding, "implementation-only", new[] { AmmunitionReadPermission }),
        new(PickedUpBinding, "implementation-only", new[] { AmmunitionReadPermission }),
        new(DroppedBinding, "implementation-only", new[] { AmmunitionReadPermission }),
        new(UseStartedBinding, "implementation-only", new[] { AmmunitionReadPermission }),
        new(UseFailedBinding, "implementation-only", new[] { AmmunitionReadPermission }),
        new(CarriedItemChangedBinding, "implementation-only", new[] { AmmunitionReadPermission })
    };

    // The one row this file owns, written as the catalog writes it: the provider owns the capability, the graph
    // carries the domains the catalog row names and the `host` execution every fact row has, and the two ports are
    // the ones the inventory observer really publishes. `item` is nullable because losing the carried item is a
    // change the row reports: "nothing is carried now" is observed, not unknown.
    private const string CarriedItemChangedRow = """
    {
      "id": "forge.trigger.player.carried_item_changed",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "玩家大件物品变化",
      "version": "1.0.0",
      "parameters": { "description": "玩家搬着的大件东西换了。" },
      "graph": {
        "domains": ["map", "room", "player", "logic"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.player"], "id": "player", "type": "entity" },
          { "id": "item", "type": "entity", "nullable": true }
        ],
        "parameters": []
      }
    }
    """;

    private static object Binding(string id, string capabilityId, string handler) => new
    {
        id, capabilityId, providerId = ModuleDefinition.ProviderId, handler, role = "observe", status = "implemented",
        dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };
}
