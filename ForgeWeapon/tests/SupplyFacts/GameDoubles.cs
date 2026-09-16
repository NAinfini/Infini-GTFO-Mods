// Stand-ins for the game members the supply adapter and the item resolver read and call, declared with build
// 20403457's member shapes: the two `PlayerBackpackManager` entry points are the ones evidence/weapon-supply.json
// decodes, the pool members are the `PlayerAmmoStorage` ones it decodes, and `ItemDataBlock` is the game's own
// block table the item resolver looks an authored id up in. Behaviour is synthetic and NOT game-verified: each
// double applies what the game's own body applies — the gift clamps at the pool's cap and the storage write clamps
// into [0, cap] — so a case can assert on what really landed.

namespace SNetwork
{
    /// <summary>The game's own network gate: both ammunition writes are host work, so this is the flag the adapter
    /// asks before it submits anything.</summary>
    public static class SNet
    {
        public static bool IsMaster { get; set; } = true;
        public static void Reset() => IsMaster = true;
    }

    /// <summary>One player as the game addresses it, with the two flags the ledger's rule reads.</summary>
    public class SNet_Player
    {
        public string NickName = "";
        public bool IsLocal;
        public bool IsBot;
    }
}

namespace Player
{
    /// <summary>The game's own ammunition pools, in the declaration order the catalog's `ammo_type` parameter
    /// shares.</summary>
    public enum AmmoType : byte
    {
        Standard = 0, Special = 1, Class = 2, ResourcePackRel = 3, None = 4, CurrentConsumable = 5
    }

    /// <summary>The game's own slot enum, byte-valued.</summary>
    public enum InventorySlot : byte
    {
        None = 0, GearStandard = 1, GearSpecial = 2, GearClass = 3, ResourcePack = 4,
        Consumable = 5, ConsumableHeavy = 6, InPocket = 7, InLevelCarry = 8, Pickup = 9
    }

    /// <summary>One pool: the bullets it holds and the cap the game's own bodies clamp against.</summary>
    public sealed class Pool
    {
        public int Bullets;
        public int Cap = 100;
    }

    /// <summary>One player's ammunition storage. `UpdateBulletsInPack` is the body the resource pack itself calls
    /// on a local backpack: it adds the bullet delta and clamps the result into the pool's own bounds.</summary>
    public class PlayerAmmoStorage
    {
        public IntPtr Pointer { get; set; } = new IntPtr(1);
        public readonly Dictionary<AmmoType, Pool> Pools = new();
        public List<(AmmoType Type, int Delta)> Writes { get; } = new();

        public float UpdateBulletsInPack(AmmoType type, int bulletCount)
        {
            Writes.Add((type, bulletCount));
            var pool = PoolOf(this, type);
            pool.Bullets = Math.Clamp(pool.Bullets + bulletCount, 0, pool.Cap);
            return pool.Bullets;
        }

        internal static Pool PoolOf(PlayerAmmoStorage storage, AmmoType type)
        {
            if (!storage.Pools.TryGetValue(type, out var pool)) storage.Pools[type] = pool = new Pool();
            return pool;
        }
    }

    /// <summary>One player's backpack: the owner the game's lookup answers for and the storage the pools live in.
    /// </summary>
    public class PlayerBackpack
    {
        public SNetwork.SNet_Player? Owner;
        public PlayerAmmoStorage? AmmoStorage;
    }

    /// <summary>The two ammunition entry points and the backpack lookup. `GiveAmmoToPlayer` applies what the
    /// game's own gift applies: each relative amount is a fraction of that pool's capacity, added and clamped at
    /// the cap. The storage write is applied by the storage double itself.</summary>
    public static class PlayerBackpackManager
    {
        public static readonly Dictionary<SNetwork.SNet_Player, PlayerBackpack> Backpacks = new();
        public static readonly List<(SNetwork.SNet_Player Player, float Standard, float Special, float Class)> Gifts = new();
        public static Exception? ThrowOnGift;
        public static Exception? ThrowOnRead;

        public static void Reset()
        {
            Backpacks.Clear();
            Gifts.Clear();
            ThrowOnGift = null;
            ThrowOnRead = null;
        }

        public static bool TryGetBackpack(SNetwork.SNet_Player? player, out PlayerBackpack? backpack)
        {
            backpack = null;
            if (player == null) return false;
            if (!Backpacks.TryGetValue(player, out var found) || found == null) return false;
            backpack = found;
            return true;
        }

        public static int GetBulletsInPack(AmmoType type, SNetwork.SNet_Player player)
        {
            if (ThrowOnRead != null) throw ThrowOnRead;
            if (!Backpacks.TryGetValue(player, out var backpack) || backpack?.AmmoStorage == null) return 0;
            return PlayerAmmoStorage.PoolOf(backpack.AmmoStorage, type).Bullets;
        }

        public static int GetAmmoMaxCap(AmmoType type, SNetwork.SNet_Player player)
        {
            if (ThrowOnRead != null) throw ThrowOnRead;
            if (!Backpacks.TryGetValue(player, out var backpack) || backpack?.AmmoStorage == null) return 0;
            return PlayerAmmoStorage.PoolOf(backpack.AmmoStorage, type).Cap;
        }

        public static void GiveAmmoToPlayer(SNetwork.SNet_Player player, float standardRel, float specialRel, float classRel)
        {
            if (ThrowOnGift != null) throw ThrowOnGift;
            Gifts.Add((player, standardRel, specialRel, classRel));
            if (!Backpacks.TryGetValue(player, out var backpack) || backpack?.AmmoStorage == null) return;
            Apply(backpack.AmmoStorage, AmmoType.Standard, standardRel);
            Apply(backpack.AmmoStorage, AmmoType.Special, specialRel);
            Apply(backpack.AmmoStorage, AmmoType.Class, classRel);
        }

        private static void Apply(PlayerAmmoStorage storage, AmmoType type, float relative)
        {
            if (relative <= 0f) return;
            var pool = PlayerAmmoStorage.PoolOf(storage, type);
            pool.Bullets = Math.Min(pool.Bullets + (int)MathF.Round(relative * pool.Cap), pool.Cap);
        }
    }
}

namespace GameData
{
    /// <summary>The game's own item block: the persistent id an authored item reference names and the slot the
    /// block itself says the item belongs in.</summary>
    public class ItemDataBlock
    {
        public uint persistentID;
        public Player.InventorySlot inventorySlot = Player.InventorySlot.InPocket;
        public static readonly Dictionary<uint, ItemDataBlock> Blocks = new();
        public static Exception? ThrowOnLookup;

        public static void Reset()
        {
            Blocks.Clear();
            ThrowOnLookup = null;
        }

        public static ItemDataBlock? GetBlock(uint id)
        {
            if (ThrowOnLookup != null) throw ThrowOnLookup;
            return Blocks.TryGetValue(id, out var block) ? block : null;
        }
    }
}
