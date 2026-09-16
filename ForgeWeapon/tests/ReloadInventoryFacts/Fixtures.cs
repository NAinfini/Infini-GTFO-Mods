// The fixture objects the two observers are handed. The observers are game-independent and name no GTFO type: they
// work in the native reads interface, an item key and a slot name, and the native half that turns a real
// `ItemEquippable` or `PlayerBackpack` into those is what a live game exercises. These stand-ins are therefore plain
// objects carrying the same roles and NO game behaviour at all — behaviour is synthetic and NOT game-verified. What
// pins the real member names is ForgeWeapon/tests/NativeLayout, which reads the interop metadata directly, and
// ForgeWeapon/evidence/{reload-facts,inventory-facts}.json, which records them for this family.

namespace ForgeWeapon.Tests.ReloadInventoryFacts;

/// <summary>One wielded or stored item, as the fixture's own tables describe it.</summary>
internal sealed class FakeItem
{
    private static long _next = 1;

    internal FakeItem() => Id = (IntPtr)Interlocked.Increment(ref _next);

    internal IntPtr Id { get; }
    /// <summary>The magazine for a weapon, or the charge for a tool.</summary>
    internal int Clip { get; set; }
    internal bool Reloading { get; set; }
    internal double[]? WorldPosition { get; set; }
    /// <summary>The player this item belongs to, as the game's owner chain reports it.</summary>
    internal FakePlayer? Owner { get; set; }
}

/// <summary>One occupied slot: the item and the instance the slot holds, which are separate so a slot can be
/// refilled with a different instance of the same equipment — the case the stack row exists for.</summary>
internal sealed class FakeBackpackItem
{
    internal FakeBackpackItem(FakeItem item, IntPtr? instance = null)
    { Item = item; Instance = instance ?? item.Id; }

    internal FakeItem Item { get; }
    internal IntPtr Instance { get; }
}

/// <summary>One player: the pool table and the slot table a case fills. The slot table is a list of names, because
/// the game's own table is positional and the observers key by the game's own slot name.</summary>
internal sealed class FakeBackpack
{
    internal FakeBackpack(FakePlayer owner) => Owner = owner;

    internal FakePlayer Owner { get; }
    internal Dictionary<string, long> Pool { get; } = new(StringComparer.Ordinal);
    internal List<string> Names { get; } = new();
    internal Dictionary<string, FakeBackpackItem?> Slots { get; } = new(StringComparer.Ordinal);

    /// <summary>An empty slot. A slot the game reports is a named entry in the table even when nothing is in
    /// it, which is what separates "the slot emptied" from "the table has no such slot".</summary>
    internal void Empty(string slot)
    {
        if (Slots.ContainsKey(slot)) return;
        Names.Add(slot);
        Slots[slot] = null;
    }

    /// <summary>Puts an item in a slot, adding the slot to the table when the case never named it.</summary>
    internal void Hold(string slot, FakeBackpackItem item)
    {
        if (!Slots.ContainsKey(slot)) Names.Add(slot);
        Slots[slot] = item;
    }

    /// <summary>Empties a slot that stays in the table.</summary>
    internal void Clear(string slot)
    {
        if (!Slots.ContainsKey(slot)) Names.Add(slot);
        Slots[slot] = null;
    }
}

/// <summary>The player record a fixture's owner chain ends in.</summary>
internal sealed class FakePlayer
{
    private static long _next = 1;

    internal FakePlayer() => Id = (IntPtr)Interlocked.Increment(ref _next);

    internal IntPtr Id { get; }
}
