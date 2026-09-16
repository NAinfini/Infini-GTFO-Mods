using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>The `forge.action.inventory.*` catalog rows this package is responsible for, and the exact point each
/// one stops at. Two of them have a native host entry point and a row on the node list, so they are declared and
/// bound here; the other four have no host entry point at all. This file says each of those by name rather than
/// declaring a row nothing answers. The catalog's `drop` row is not among them either: the node list carries no
/// drop node, so no capability and no binding may exist for it, and the body that used to answer for it is gone
/// with them rather than left behind as an unreachable implementation.
///
/// The split is decided by the native evidence (`evidence/inventory-actions.json`), never by the catalog text:
///
/// | row | native truth | verdict |
/// |---|---|---|
/// | `give` | `PlayerBackpackManager.MasterAddItem(pItemData_WithOwner, SNet_Player)` sends the add-item packet and applies it locally | implementable, needs the item-resource binding |
/// | `consume` | `PlayerBackpackManager.TryMasterRemovePocketItemWithID(uint, SNet_Player)` sends the remove-item packet and applies it locally | implementable, needs the item-resource binding |
/// | `pickup` | no host entry point takes a world item into a backpack: `LG_PickupItem_Sync` is the synced state machine, and this build has no world-item entity kind to name a source with | not implemented |
/// | `equip` | `PlayerInventoryLocal.CheckAndWield` / `DoWieldItem` are the input-driven local wield path, gated on `m_allowedToWieldItem` and the wield delay; `PlayerBackpackManager.EquipLocalGear` builds a gear from a `GearIDRange` instead of switching to held equipment | not implemented |
/// | `transfer` | no atomic two-backpack write exists: a remove-then-add pair is two packets, and a failure between them leaves the amount nowhere | not implemented, depends on a transaction |
/// | `stack_set` | `PlayerBackpack.AddPocketItem` / `RemovePocketItem` are local list edits with no packet in their bodies, and there is no host entry point that sets a count | not implemented |
///
/// The `Rows(give, consume)` entries below are binding rows, and the capability rows `Capabilities(give, consume)`
/// declares are the rows those bindings bind to. A `forge.action.*` row is owned by the provider that executes it —
/// the runtime's own contracts declare the combat and control rows, and no module anywhere declares an
/// `forge.action.inventory.*` row — so a binding whose capability nobody declares cannot register at all
/// (`binding-capability`) and the row is declared here. The names of every port, parameter and result field come
/// from the catalog row and are spelled here exactly as the catalog spells them, so an integration that wires these
/// cannot silently drift from the published shape. The one deliberate difference from the catalog text is recorded
/// on each row below.</summary>
public static class InventoryActionContract
{
    /// <summary>The two capabilities this package declares. `drop` is deliberately not among them: the node list
    /// has no drop node, so nothing declares that capability and no binding row may exist for it (ruling 110.5).</summary>
    public const string GiveCapability = "forge.action.inventory.give";
    public const string ConsumeCapability = "forge.action.inventory.consume";

    /// <summary>The four capabilities this package is responsible for and cannot execute. They are named here so
    /// the integration batch has one place that says which rows are deliberately unwired.</summary>
    public const string PickupCapability = "forge.action.inventory.pickup";
    public const string EquipCapability = "forge.action.inventory.equip";
    public const string TransferCapability = "forge.action.inventory.transfer";
    public const string StackSetCapability = "forge.action.inventory.stack_set";

    public const string GiveBinding = ModuleDefinition.ProviderId + ".binding.inventory_give";
    public const string ConsumeBinding = ModuleDefinition.ProviderId + ".binding.inventory_consume";

    public const string GiveHandler = "gtfo.inventory.give";
    public const string ConsumeHandler = "gtfo.inventory.consume";

    /// <summary>The permission the two writes carry. One name covers both because they act on the same object
    /// class — a player's own backpack — and a plan that may put an item into a backpack may also spend one out
    /// of it; splitting the name would invent a distinction the native path does not have.</summary>
    public const string WritePermission = "gtfo.inventory.write";

    /// <summary>The `give` row: the catalog's own ports, plus the `item` resource the native add needs and the
    /// catalog row names as well. Every port here is one the handler really reads and the shape below declares
    /// exactly this set, because the registration refuses a handler whose shape and row disagree.</summary>
    public const string GiveRowDocument = """
    {
      "id": "forge.action.inventory.give",
      "owner": "forge.module.gtfo.weapon",
      "kind": "action",
      "label": "给予 / 扣除物品（消耗品、资源包）",
      "version": "1.0.0",
      "parameters": { "description": "在容量和权限允许时给出物品。" },
      "graph": {
        "domains": ["map", "tool", "consumable", "player"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "id": "inventories", "type": "entity", "cardinality": "many" },
          { "id": "item", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item" },
          { "id": "quantity", "type": "integer" },
          { "id": "charges", "type": "integer" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "result", "type": "result", "schema": "forge.result.inventory.give",
            "fields": [
              { "id": "target", "type": "entity" },
              { "id": "status", "type": "enum", "schema": "execution_outcome" },
              { "id": "committed", "type": "enum", "schema": "commit_state" },
              { "id": "code", "type": "string" },
              { "id": "quantity", "type": "integer" },
              { "id": "target_count", "type": "integer" }
            ] }
        ],
        "parameters": [
          { "id": "capacity_policy", "type": "enum", "role": "structural", "required": true,
            "values": ["reject", "clamp", "drop"] }
        ],
        "recipients": { "input": "inventories", "target": "entity", "cardinality": "many",
          "requires": ["inventory.give"], "result": "result" }
      }
    }
    """;

    /// <summary>The `consume` row: the catalog's ports with one change, recorded here because the row cannot
    /// carry a port no handler reads. The native removal is one packet-sending call per unit
    /// (`TryMasterRemovePocketItemWithID`) with no transaction to join, and the row declares no transaction
    /// port at all: the handle kind has no member, so there is nothing for such a port to carry. The handler
    /// refuses a `charges` request by name rather than pretending to spend charges apart from the count.
    /// The shape below declares exactly the ports the handler reads, and the row declares exactly that set.</summary>
    public const string ConsumeRowDocument = """
    {
      "id": "forge.action.inventory.consume",
      "owner": "forge.module.gtfo.weapon",
      "kind": "action",
      "label": "扣除物品",
      "version": "1.0.0",
      "parameters": { "description": "按事务扣掉堆叠或使用次数。" },
      "graph": {
        "domains": ["map", "tool", "consumable", "player"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "id": "items", "type": "entity", "cardinality": "many" },
          { "id": "item", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item" },
          { "id": "count", "type": "integer" },
          { "id": "charges", "type": "integer" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "result", "type": "result", "schema": "forge.result.inventory.consume",
            "fields": [
              { "id": "target", "type": "entity" },
              { "id": "status", "type": "enum", "schema": "execution_outcome" },
              { "id": "committed", "type": "enum", "schema": "commit_state" },
              { "id": "code", "type": "string" },
              { "id": "count", "type": "integer" },
              { "id": "target_count", "type": "integer" }
            ] }
        ],
        "parameters": [],
        "recipients": { "input": "items", "target": "entity", "cardinality": "many",
          "requires": ["inventory.consume"], "result": "result" }
      }
    }
    """;

    /// <summary>The capability rows this package owns, in the order the catalog lists them, each declared only
    /// when its handler is supplied: a declared action nothing answers is a promise this package does not keep.
    /// The catalog's `drop` row declares nothing here and has no body: the node list has no drop node, so its
    /// binding row was deleted with it (ruling 110.5) and the implementation followed it out (ruling 133.3).</summary>
    public static IReadOnlyList<object> Capabilities(CommandHandler? give, CommandHandler? consume)
    {
        var rows = new List<object>(2);
        if (give != null) rows.Add(RuntimeJson.Parse(GiveRowDocument));
        if (consume != null) rows.Add(RuntimeJson.Parse(ConsumeRowDocument));
        return rows;
    }

    /// <summary>The handler shapes, resolved at registration against each capability's own ports. Every
    /// name here is a port the handler really reads; `viewers`, `marker`, `duration` and friends do not exist on
    /// these rows, so nothing is declared that the handler would ignore.</summary>
    public static readonly HandlerShape GiveShape = new HandlerShape()
        .Inputs("inventories", "item", "quantity", "charges").Outputs("result").Parameters("capacity_policy");
    public static readonly HandlerShape ConsumeShape = new HandlerShape()
        .Inputs("items", "item", "count", "charges").Outputs("result");

    /// <summary>The execute binding row of one inventory action: this provider's own id, the canonical capability
    /// it implements and the handler the native half supplies. `requires` is empty because the handler's own
    /// native lookup is what reads the world; the row needs no other binding to close a plan.</summary>
    public static object Row(string bindingId, string capabilityId, string handler) => new
    {
        id = bindingId,
        capabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    /// <summary>The registration support of one row. Each write carries <see cref="WritePermission"/>: these are
    /// the only bindings in this provider that change a player's own state, as opposed to the wield and deploy
    /// observations, which only read.</summary>
    public static BindingSupport Support(string bindingId)
        => new(bindingId, "implementation-only", new[] { WritePermission });

    /// <summary>The support rows of the bindings that are really declared, one per supplied handler.</summary>
    public static IReadOnlyList<BindingSupport> Support(CommandHandler? give, CommandHandler? consume)
    {
        var rows = new List<BindingSupport>(2);
        if (give != null) rows.Add(Support(GiveBinding));
        if (consume != null) rows.Add(Support(ConsumeBinding));
        return rows;
    }

    /// <summary>The handler names and shapes the native half must be registered with, so the integration batch
    /// never re-spells a shape. Each entry travels with the row that names it: a shape whose binding is not
    /// declared is refused by the runtime as an unused shape, so only supplied handlers are answered.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes(CommandHandler? give, CommandHandler? consume)
    {
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        if (give != null) shapes[GiveHandler] = GiveShape;
        if (consume != null) shapes[ConsumeHandler] = ConsumeShape;
        return shapes;
    }

    /// <summary>The execute rows bound to the native bodies themselves. The bodies are taken as handlers rather
    /// than as the adapter that owns them, so this file stays game-independent and the native assembly decides
    /// which object answers: a registration can only declare a row whose handler it was given, and a missing one
    /// leaves the row out instead of promising a body nothing supplies.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers(CommandHandler? give, CommandHandler? consume)
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        if (give != null) handlers[GiveHandler] = give;
        if (consume != null) handlers[ConsumeHandler] = consume;
        return handlers;
    }

    /// <summary>The execute rows, each declared only when its handler is really supplied.</summary>
    public static IReadOnlyList<object> Rows(CommandHandler? give, CommandHandler? consume)
    {
        var rows = new List<object>(2);
        if (give != null) rows.Add(Row(GiveBinding, GiveCapability, GiveHandler));
        if (consume != null) rows.Add(Row(ConsumeBinding, ConsumeCapability, ConsumeHandler));
        return rows;
    }
}
