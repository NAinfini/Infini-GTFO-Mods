// Stand-ins for the game members the inventory action adapter reads and calls, following build 20403457's interop
// member kinds: `PlayerBackpackManager`'s three host entry points are the ones evidence/inventory-actions.json
// decodes, and every field the adapter fills on a `pItemData` is the interop struct's own field. Behaviour is
// synthetic and NOT game-verified; the hooks let a case say what the game's own body then did.

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    /// <summary>The interop array shape `PlayerBackpack.Slots` has in this build: a length and an indexer. The
    /// double keeps exactly that surface, because the adapter never uses anything else from it.</summary>
    public sealed class Il2CppReferenceArray<T>
    {
        private readonly T[] _items;
        public Il2CppReferenceArray(int length) => _items = new T[length];
        public int Length => _items.Length;
        public T this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }
    }
}

namespace SNetwork
{
    /// <summary>The game's own network gate: the inventory packets are host-sent, so this is the flag the adapter
    /// asks before it submits anything.</summary>
    public static class SNet
    {
        public static bool IsMaster { get; set; } = true;
        public static void Reset() => IsMaster = true;
    }

    /// <summary>One player as the game addresses it. The adapter only carries the instance the player domain
    /// resolved; the name exists so a fixture can label a player.</summary>
    public class SNet_Player
    {
        public string NickName = "";
    }

    public static class SNetStructs
    {
        /// <summary>`pReplicator` is a single `keyPlusOne` in this build; the zero the adapter leaves in
        /// `pItemData.replicatorRef` is the invalid reference, which is what an item that no level instance
        /// backs carries.</summary>
        public struct pReplicator
        {
            public ushort keyPlusOne;
            public bool IsValid() => keyPlusOne != 0;
        }

        /// <summary>`pPlayer` is the handle the game's own paths fill from the player they were given, so a case
        /// can read back which player an add was addressed to.</summary>
        public struct pPlayer
        {
            public bool IsBot;
            public ulong lookup;
            private SNet_Player? _player;
            public void SetPlayer(SNet_Player? player)
            {
                _player = player;
                lookup = player == null ? 0UL : 1UL;
            }
            public SNet_Player? Player => _player;
        }
    }
}

namespace Player
{
    /// <summary>The game's own slot enum, byte-valued, with the members the inventory actions name. `InPocket` is
    /// the slot the native removal body builds into its `pItemData`, which is what makes a pocket item exactly
    /// this slot.</summary>
    public enum InventorySlot : byte
    {
        None = 0, GearStandard = 1, GearSpecial = 2, GearClass = 3, ResourcePack = 4,
        Consumable = 5, ConsumableHeavy = 6, InPocket = 7, InLevelCarry = 8, Pickup = 9
    }

    public struct pItemData_Custom
    {
        public float ammo;
        public byte byteId;
        public byte byteState;
    }

    public struct pItemData
    {
        public pItemData_Custom custom;
        public uint itemID_gearCRC;
        public SNetwork.SNetStructs.pReplicator replicatorRef;
        public InventorySlot slot;
        public LG_LayerType originLayer;
        public pCourseNode originCourseNode;
    }

    public struct pItemData_WithOwner
    {
        public SNetwork.SNetStructs.pPlayer owningPlayer;
        public pItemData data;
    }

    /// <summary>One item in a backpack slot. `ItemID` is the number the game carries in
    /// `pItemData.itemID_gearCRC` for a non-gear item.</summary>
    public class BackpackItem
    {
        public uint ItemID;
    }

    /// <summary>One player's backpack. `Slots` is the interop array shape, `Owner` is the player the game built
    /// the backpack for, and `Pocket` is the pocket-item count `CountPocketItem` answers. Nothing here mutates a
    /// slot by itself: a case decides what the native body did.</summary>
    public class PlayerBackpack
    {
        public SNetwork.SNet_Player? Owner;
        public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BackpackItem>? Slots;
        public readonly Dictionary<uint, int> Pocket = new();
        public int CountPocketItem(uint itemID) => Pocket.TryGetValue(itemID, out var count) ? count : 0;
    }

    /// <summary>The two host entry points the adapter submits through and the backpack lookup. Each one applies
    /// what its hook says and records the request, which is the order the game's own bodies use: the packet first,
    /// the local change after.</summary>
    public static class PlayerBackpackManager
    {
        public static readonly Dictionary<SNetwork.SNet_Player, PlayerBackpack> Backpacks = new();
        public static readonly List<pItemData_WithOwner> AddRequests = new();
        public static readonly List<(uint ItemID, SNetwork.SNet_Player Player)> PocketRemovals = new();
        public static Exception? ThrowOnAdd;
        public static Exception? ThrowOnRemove;
        public static Action<PlayerBackpack, pItemData>? OnAdd;
        public static Action<PlayerBackpack, uint>? OnRemovePocket;

        public static void Reset()
        {
            Backpacks.Clear(); AddRequests.Clear(); PocketRemovals.Clear();
            ThrowOnAdd = null; ThrowOnRemove = null;
            OnAdd = null; OnRemovePocket = null;
        }

        public static bool TryGetBackpack(SNetwork.SNet_Player? player, out PlayerBackpack? backpack)
        {
            backpack = null;
            if (player == null) return false;
            if (!Backpacks.TryGetValue(player, out var found) || found == null) return false;
            backpack = found;
            return true;
        }

        public static void MasterAddItem(pItemData_WithOwner dataWithOwner, SNetwork.SNet_Player? sendOnlyTo)
        {
            if (ThrowOnAdd != null) throw ThrowOnAdd;
            AddRequests.Add(dataWithOwner);
            var owner = dataWithOwner.owningPlayer.Player;
            if (owner != null && Backpacks.TryGetValue(owner, out var backpack)) OnAdd?.Invoke(backpack, dataWithOwner.data);
        }

        public static bool TryMasterRemovePocketItemWithID(uint itemID, SNetwork.SNet_Player player)
        {
            if (ThrowOnRemove != null) throw ThrowOnRemove;
            if (!Backpacks.TryGetValue(player, out var backpack)) return false;
            if (backpack.CountPocketItem(itemID) <= 0) return false;
            PocketRemovals.Add((itemID, player));
            OnRemovePocket?.Invoke(backpack, itemID);
            return true;
        }
    }
}

/// <summary>The two value types `pItemData` carries that the interop declares outside `Player`. The spellings are
/// kept so the production text is unchanged; the interop types themselves are value types with no native call in
/// their construction, so a case never has to reach the game for one.</summary>
public struct pCourseNode
{
}

public enum LG_LayerType
{
}
