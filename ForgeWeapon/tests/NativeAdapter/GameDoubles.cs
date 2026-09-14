using System.Runtime.CompilerServices;

// Doubles mirror only the interop members the production native sources read. Names, namespaces and
// member kinds follow build 20403457 (dump.cs / interop); behaviour is synthetic and NOT game-verified.
public static class Pointers
{
    private static long _next = 0x10000;
    public static IntPtr Next() => new(Interlocked.Increment(ref _next));
}

/// <summary>Unity equality: a destroyed object compares equal to null.</summary>
public abstract class UnityObjectDouble
{
    public IntPtr Pointer { get; set; } = Pointers.Next();
    public bool Destroyed;
    private static bool Alive(UnityObjectDouble? value) => value is not null && !value.Destroyed;
    public static bool operator ==(UnityObjectDouble? left, UnityObjectDouble? right)
    {
        bool l = Alive(left), r = Alive(right);
        return !l || !r ? l == r : ReferenceEquals(left, right);
    }
    public static bool operator !=(UnityObjectDouble? left, UnityObjectDouble? right) => !(left == right);
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

public class Item : UnityObjectDouble { }
public class ItemEquippable : Item { }

public abstract class PlayerInventoryBase : UnityObjectDouble
{
    public Player.PlayerAgent Owner = null!;
    public ItemEquippable? WieldedItem;
}
public class PlayerInventoryLocal : PlayerInventoryBase
{
    public void DoWieldItem() { }
    public void UnWield() { }
}
public class PlayerInventorySynced : PlayerInventoryBase
{
    public void DoEquipItem(ItemEquippable item) { }
    public void UnWield() { }
}

namespace Gear
{
    public sealed class GearIDRange
    {
        public uint Checksum;
        public uint GetChecksum() => Checksum;
    }
}

namespace SNetwork
{
    public static class SNet { public static bool IsMaster { get; set; } = true; }
    public sealed class SNet_IPlayerAgent
    {
        public object? Target;
        public T? TryCast<T>() where T : class => Target as T;
    }
    public sealed class SNet_Player : UnityObjectDouble
    {
        public SNet_IPlayerAgent? PlayerAgent;
        public bool HasPlayerAgent => PlayerAgent != null;
    }
}

namespace Player
{
    public enum InventorySlot
    {
        None = 0, GearStandard = 1, GearSpecial = 2, GearClass = 3, ResourcePack = 4, Consumable = 5,
        ConsumableHeavy = 6, InPocket = 7, InLevelCarry = 8, Pickup = 9, GearMelee = 10, HackingTool = 11
    }
    public sealed class PlayerAgent : UnityObjectDouble
    {
        public SNetwork.SNet_Player Owner = null!;
        public PlayerInventoryBase Inventory = null!;
    }
    public sealed class BackpackItem
    {
        public IntPtr Pointer = Pointers.Next();
        public Item? Instance;
        public Gear.GearIDRange? GearIDRange;
        public uint ItemID;
        public bool IsLoaded = true;
    }
    public sealed class PlayerBackpack
    {
        public IntPtr Pointer = Pointers.Next();
        public SNetwork.SNet_Player? Owner;
        public bool ThrowOnSlots;
        private readonly BackpackItem?[] _slots = new BackpackItem?[12];
        private readonly bool[] _deployed = new bool[12];
        public BackpackItem?[]? Slots => ThrowOnSlots ? throw new InvalidOperationException("fixture native read failure") : _slots;
        public BackpackItem? CreateAndStoreBackpackItem(Item item, InventorySlot slot, Gear.GearIDRange gearIDRange) => null;
        public bool TryClearSlot(InventorySlot slot) => true;
        public void DestroyAllInstance() { }
        public bool IsDeployed(InventorySlot slot) => _deployed[(int)slot];
        public void SetDeployed(InventorySlot slot, bool mode) => _deployed[(int)slot] = mode;
    }
    public sealed class PlayerBackpackManager
    {
        public static readonly Dictionary<SNetwork.SNet_Player, PlayerBackpack> Backpacks = new();
        public static bool TryGetBackpack(SNetwork.SNet_Player player, out PlayerBackpack backpack)
            => Backpacks.TryGetValue(player, out backpack!);
    }
}
