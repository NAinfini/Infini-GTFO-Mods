using System;
using System.Globalization;
using GameData;
using Player;

namespace ForgeWeapon.Native;

/// <summary>The item half of the inventory rows: one authored `forge.resource.item` reference turned into the
/// game's own number. The number is `ItemDataBlock.persistentID` — the value `pItemData.itemID_gearCRC` carries
/// for a non-gear item — and the table it comes from is the game's own block table (`ItemDataBlock.GetBlock`),
/// which is the only authority on which item ids exist in this build.
///
/// <b>The id's spelling.</b> A resource id is the official persistent id as plain decimal text, which is the one
/// spelling this runtime already pins for an authored native block id (the enemy domain's `enemy-type` mount and
/// `v-e-type` read row both carry `EnemyDataBlock.persistentID` as decimal text). A text that is not a decimal id,
/// or an id no block answers, is refused by name; nothing is derived from a display name, an index or a checksum.
///
/// <b>Why the block's own slot matters.</b> `MasterAddItem` places the item in the slot its `pItemData` names, so
/// a resource pack or a consumable added as a pocket item would sit in a slot the game does not read it from.
/// `TryResolvePocketItem` is therefore the narrow resolver the pocket-item rows take: it answers only for blocks
/// whose own `inventorySlot` is `InPocket`, and a gear-slot block is refused rather than misplaced. Giving a
/// gear-slot item needs the game's own gear path (`PlayerBackpackManager.MasterAddItem(Item, SNet_Player)` over an
/// instance built by `ItemReplicationManager.SpawnItem`), which this batch does not implement and
/// `evidence/weapon-supply-items.json` records as the named gap.
///
/// This half is registration input, not a second resource system: the runtime's own resource registry, when it
/// exists, is what a plan resolves against, and this delegate is what the package answers with until then. The
/// integration passes one of these two members to `InventoryActionAdapter.ItemResolver`.</summary>
internal static class WeaponSupplyItems
{
    /// <summary>The whole answer for one authored item reference: the game's id and the slot the block itself
    /// declares. False means this process cannot name that resource at all.</summary>
    internal static bool TryResolve(string resourceId, out uint itemId, out InventorySlot slot)
    {
        itemId = 0u;
        slot = InventorySlot.None;
        if (resourceId == null || resourceId.Length == 0 || resourceId.Length > 10) return false;
        if (!uint.TryParse(resourceId, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0u) return false;
        ItemDataBlock? block;
        try { block = ItemDataBlock.GetBlock(id); }
        catch (Exception) { return false; }
        if (block == null) return false;
        if (block.persistentID != id) return false;
        itemId = block.persistentID;
        slot = block.inventorySlot;
        return true;
    }

    /// <summary>The narrowing the three pocket-item rows take: a resource this process can name <b>and</b> whose
    /// block says the item belongs in the pocket. A gear-slot block answers false, which the handler reports as the
    /// unresolved resource it is for that row rather than writing the item into a slot the game never reads.</summary>
    internal static bool TryResolvePocketItem(string resourceId, out uint itemId)
    {
        itemId = 0u;
        if (!TryResolve(resourceId, out uint id, out var slot)) return false;
        if (slot != InventorySlot.InPocket) return false;
        itemId = id;
        return true;
    }
}
