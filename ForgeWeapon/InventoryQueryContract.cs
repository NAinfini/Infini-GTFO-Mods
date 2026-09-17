using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>The two read-only rows this package owns and evaluates on demand: the ammunition of one equipment
/// life (`forge.query.equipment.ammo`) and whether a holder's backpack carries a named item
/// (`forge.condition.predicate.inventory_item`). The catalog lists the condition row already and has no owner for
/// it, and the ammunition row is this package's own id: the runtime's `forge.query.player.ammo`
/// (<c>ForgeMap.PlayerStateContract</c>) answers for the weapon a player is holding and for nothing else, so a
/// plan that has an equipment entity in hand cannot ask it anything. Both rows are declared here with the ports,
/// the labels and the description the catalog spells, and both are answered by this package's native half through
/// <see cref="IInventoryQuerySource"/>: a process without that half refuses both reads by code rather than
/// declaring a row nothing answers.</summary>
public static class InventoryQueryContract
{
    /// <summary>The equipment-ammunition row. Its canonical id is this provider's own: no contract in the
    /// runtime declares it, and the player-addressed row next to it answers a different subject.</summary>
    public const string EquipmentAmmoCapability = "forge.query.equipment.ammo";
    /// <summary>The held-item condition. The catalog row exists and every other provider is silent about it, so
    /// this package declares it rather than binding a capability nobody owns, which the runtime refuses as
    /// `binding-capability`.</summary>
    public const string InventoryItemCapability = "forge.condition.predicate.inventory_item";

    public const string EquipmentAmmoBinding = ModuleDefinition.ProviderId + ".binding.equipment_ammo";
    public const string InventoryItemBinding = ModuleDefinition.ProviderId + ".binding.inventory_item";

    /// <summary>The two evaluator names. The name is also the shape key, so a registration cannot supply an
    /// evaluator whose ports were never declared.</summary>
    public const string EquipmentAmmoHandler = "gtfo.weapon.equipment_ammo";
    public const string InventoryItemHandler = "gtfo.weapon.inventory_item";

    /// <summary>The permission both reads carry. They read the equipment a player holds and the backpack behind
    /// it — the same subject the wield and reload reads already carry — so the name is not split per row.</summary>
    public const string ReadPermission = ModuleDefinition.WieldReadPermission;

    /// <summary>The equipment-ammunition row: the equipment life it reads, and the three numbers the player
    /// row's own answer carries, under the same names. `clip_max` is what the weapon itself reports rather than
    /// what the loaded rounds imply, so a partly filled magazine reads as one.</summary>
    public const string EquipmentAmmoRowDocument = """
    {
      "id": "forge.query.equipment.ammo",
      "owner": "forge.module.gtfo.weapon",
      "kind": "state",
      "label": "装备剩余弹药",
      "version": "1.0.0",
      "parameters": { "description": "读一件装备的弹匣、弹匣容量和备用弹药。" },
      "graph": {
        "domains": ["map", "weapon", "logic"],
        "execution": "query",
        "inputs": [
          { "id": "equipment", "type": "entity", "entityKinds": ["gtfo.equipment"] }
        ],
        "outputs": [
          { "id": "clip", "type": "integer" },
          { "id": "clip_max", "type": "integer" },
          { "id": "reserve", "type": "integer" }
        ],
        "parameters": []
      }
    }
    """;

    /// <summary>The held-item condition, with the catalog's own ports: the holder, the item resource and how many
    /// of it the condition asks for, answered as one boolean. The card is the row's own label and description,
    /// spelled as the catalog spells them.</summary>
    public const string InventoryItemRowDocument = """
    {
      "id": "forge.condition.predicate.inventory_item",
      "owner": "forge.module.gtfo.weapon",
      "kind": "condition",
      "label": "持有指定物品",
      "version": "1.0.0",
      "parameters": { "description": "判断背包里有没有指定物品。" },
      "graph": {
        "domains": ["map", "room", "enemy", "weapon", "tool", "consumable", "player", "logic"],
        "execution": "query",
        "inputs": [
          { "id": "holder", "type": "entity" },
          { "id": "item", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item" },
          { "id": "count", "type": "integer" }
        ],
        "outputs": [
          { "id": "value", "type": "boolean" }
        ],
        "parameters": []
      }
    }
    """;

    /// <summary>The two capability documents. Both are declared by this file and by nothing else: a second
    /// declaration of either id is refused at registration as a conflict.</summary>
    public static IReadOnlyList<object> Capabilities() => new object[]
    {
        RuntimeJson.Parse(EquipmentAmmoRowDocument),
        RuntimeJson.Parse(InventoryItemRowDocument)
    };

    /// <summary>The ports each evaluator really reads, resolved at registration against the capability's own
    /// ports. `equipment` is the life the ammunition row is about; the condition reads its holder, its item
    /// resource and its count, and answers the one boolean the catalog declares.</summary>
    public static readonly HandlerShape EquipmentAmmoShape = new HandlerShape()
        .Inputs("equipment").Outputs("clip", "clip_max", "reserve");
    public static readonly HandlerShape InventoryItemShape = new HandlerShape()
        .Inputs("holder", "item", "count").Outputs("value");

    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [EquipmentAmmoHandler] = EquipmentAmmoShape,
        [InventoryItemHandler] = InventoryItemShape
    };

    /// <summary>The two binding rows: this provider's own id, the capability it implements, and the evaluator the
    /// native half supplies. `observe` is the role the runtime's other read-only rows carry, and a `state` or
    /// `condition` binding under it is the one that is answered by an evaluator on demand instead of by a
    /// dispatched body. `requires` is empty: the evaluator's own native read is what reaches the world.</summary>
    public static IReadOnlyList<object> Bindings() => new object[]
    {
        Row(EquipmentAmmoBinding, EquipmentAmmoCapability, EquipmentAmmoHandler),
        Row(InventoryItemBinding, InventoryItemCapability, InventoryItemHandler)
    };

    /// <summary>One support row per binding, under the same read permission. `implementation-only` is what every
    /// observation binding in this package carries: the answers come from this package's own native read and from
    /// nothing the runtime can check by itself.</summary>
    public static IReadOnlyList<BindingSupport> Support() => new BindingSupport[]
    {
        new(EquipmentAmmoBinding, "implementation-only", new[] { ReadPermission }),
        new(InventoryItemBinding, "implementation-only", new[] { ReadPermission })
    };

    /// <summary>The evaluator table the registration carries, taken from the reads that own the bodies so a shape
    /// and its evaluator can never be registered apart.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => InventoryQueryReads.Evaluators();

    /// <summary>`forge.query.equipment.ammo`: the magazine numbers, under the names the player row's own answer
    /// already uses.</summary>
    public static JsonElement AmmoAnswer(EquipmentAmmo ammo)
        => RuntimeJson.From(new { clip = ammo.Clip, clip_max = ammo.ClipMaximum, reserve = ammo.Reserve });

    /// <summary>`forge.condition.predicate.inventory_item`: the one boolean the catalog declares.</summary>
    public static JsonElement HeldAnswer(bool held) => RuntimeJson.From(new { value = held });

    private static object Row(string bindingId, string capabilityId, string handler) => new
    {
        id = bindingId,
        capabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role = "observe",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };
}
