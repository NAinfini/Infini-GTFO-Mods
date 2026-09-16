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
    /// <summary>The interop idiom the production sources read a native object's real type with: the double stands
    /// in for the interop object, so the cast answers from the managed type it really is.</summary>
    public T? TryCast<T>() where T : class => this as T;
}

public class Item : UnityObjectDouble
{
    public Player.PlayerAgent? Owner;
    public Transform transform = new();
}
public class ItemEquippable : Item
{
    /// <summary>The game's own reload flag, with the setter the reload life's rising and falling edges pass
    /// through. The hooks patch it by name, so the double declares the member kind the build's metadata does.</summary>
    public bool IsReloading { get; set; }
    /// <summary>The magazine, and the base member every weapon family answers it with: the reload transfer is the
    /// difference between two reads of it.</summary>
    public int Clip;
    public virtual int GetCurrentClip() => Clip;
    /// <summary>The gear spawn completion the build declares virtual on the gear root and every weapon family
    /// overrides; the archetype rebuild a weapon performs in it is what the override replay hook runs after.</summary>
    public virtual void OnGearSpawnComplete() { }
}

/// <summary>Unity side of a world position; only what the adapter reads, plus the local pose the presentation
/// hook writes and the hierarchy lookup it resolves a child path with.</summary>
public sealed class Transform
{
    public UnityEngine.Vector3 position;
    public UnityEngine.Vector3 localPosition;
    public UnityEngine.Vector3 localEulerAngles;
    public UnityEngine.Vector3 localScale = new(1, 1, 1);
    /// <summary>The game object this transform belongs to. Unity cannot have one without the other, so the owning
    /// game object assigns it once and nothing else may: a hand-assigned value is how a fixture would hand the
    /// production code a transform that no game object owns.</summary>
    public UnityEngine.GameObject gameObject
    {
        get => _gameObject ?? throw new InvalidOperationException("A transform only exists on its own game object.");
        internal set => _gameObject = value;
    }
    private UnityEngine.GameObject? _gameObject;
    /// <summary>Unity resolves a `/`-separated path against the hierarchy, one segment at a time, and answers null
    /// when any of them is absent; a flat name is the one-segment case.</summary>
    public readonly Dictionary<string, Transform> Children = new(StringComparer.Ordinal);
    public Transform? Find(string path)
    {
        var current = this;
        foreach (var segment in path.Split('/'))
        {
            if (!current.Children.TryGetValue(segment, out var child)) return null;
            current = child;
        }
        return current;
    }
}

/// <summary>The weapon family root. `Gear.BulletWeapon` fires and resolves hits, `Gear.Shotgun` overrides Fire,
/// `Gear.RifleWeapon` does not. Mirrors Modules-ASM interop member kinds only.</summary>
public class Weapon : ItemEquippable
{
    public class WeaponHitData
    {
        public UnityEngine.Vector3 fireAtPos;
        public UnityEngine.RaycastHit rayHit;
        public Player.PlayerAgent? owner;
    }
}

/// <summary>Native agent families a hit can name. The interop cast an agent is read with is the one every native
/// object already answers, so nothing is redeclared here.</summary>
namespace Agents
{
    public abstract class Agent : UnityEngine.Component
    {
    }

    /// <summary>The game's own global agent index the tag action resolves a `gtfo.enemy` reference through. This
    /// suite exercises no tag submission, so nothing registers an agent here; the member exists because the
    /// production source calls it.</summary>
    public static class AgentManager
    {
        private static readonly Dictionary<int, Agent> Index = new();
        public static void Register(int globalId, Agent agent) => Index[globalId] = agent;
        public static void Reset() => Index.Clear();
        public static bool GetAgent(int globalId, out Agent? agent) => Index.TryGetValue(globalId, out agent);
    }
}

namespace Enemies
{
    /// <summary>`Alive` and `IsTagged` are the two enemy properties the tag action reads: the first gates the
    /// submission, the second is the marker state the readback after it reports.</summary>
    public sealed class EnemyAgent : Agents.Agent
    {
        public ushort GlobalID;
        public bool Alive = true;
        public bool IsTagged;
    }
}

/// <summary>The game's own tag entry point. This suite exercises no tag submission; the member exists because the
/// production source calls it, and it changes nothing without a registered handler.</summary>
public static class ToolSyncManager
{
    public static void WantToTagEnemy(Enemies.EnemyAgent enemy) { }
}

/// <summary>The damage limb a bullet was resolved against: the game's own owner accessor, which is how the hit
/// object's native type decides whether the enemy or the player domain is asked for a reference.</summary>
public class Dam_EnemyDamageLimb : UnityEngine.Component
{
    public Agents.Agent? BaseAgent;
    /// <summary>The part's own limb index, which is the `limb` port a melee hit carries.</summary>
    public int m_limbID;
    public Agents.Agent? GetBaseAgent() => BaseAgent;
}
public class Dam_PlayerDamageLimb : UnityEngine.Component
{
    public Agents.Agent? BaseAgent;
    public Agents.Agent? GetBaseAgent() => BaseAgent;
}

/// <summary>Sentry and mine world instances. The sentry declares OnDespawn and both declare their own OnDestroy,
/// which is why the sentry's destroy path is patched on the sentry type and not on ItemEquippable.</summary>
public class SentryGunInstance : ItemEquippable
{
    /// <summary>The rounds the device has left, which the firing pair is measured across.</summary>
    public float Ammo;
    public void OnSpawn() { }
    public void SyncedPickup(Player.PlayerAgent agent) { }
    public void OnDespawn() { }
    public void OnDestroy() { }
}
public class MineDeployerInstance : ItemEquippable
{
    public void OnSpawn() { }
    public void SyncedPickup(Player.PlayerAgent agent) { }
    public void OnDestroy() { }
}

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
    public struct RaycastHit
    {
        public Vector3 point;
        public bool hasHit;
        public Collider? collider;
    }
    /// <summary>Unity's own hierarchy lookup: a hit collider finds the limb and the agent above it.</summary>
    public abstract class Component : UnityObjectDouble
    {
        public T? GetComponentInParent<T>() where T : class => this as T ?? Parent?.GetComponentInParent<T>();
        public Component? Parent;
        public GameObject? gameObject;
    }
    public sealed class Collider : Component { }

    /// <summary>A map object a hit can belong to — a door, a terminal — as the domain that owns it would see
    /// it. Weapon never names this type or any member of it: the hit path hands the collider to the map-object
    /// kind's own instance lookup, and this stands in for whatever that domain climbs to.</summary>
    public sealed class MapObjectDouble : Component { }

    /// <summary>The part container the presentation hook writes through: a part is a GameObject whose transform is
    /// posed, and `SetActive` is the interop spelling of the game object's `active` field (this build's interop
    /// exposes no `active` property). The two references are wired both ways as Unity does.</summary>
    public sealed class GameObject : UnityObjectDouble
    {
        public Transform transform { get; }
        public bool activeSelf = true;
        /// <summary>Unity wires the two references both ways: this is the only place a transform gets its owner.</summary>
        public GameObject() : this(new Transform()) { }
        public GameObject(Transform transform) { this.transform = transform; transform.gameObject = this; }
        public void SetActive(bool value) { activeSelf = value; }
        /// <summary>The component on this object and the parent a component lookup climbs to: Unity resolves
        /// `GetComponentInParent` against the object itself and then its parents, which is how the melee hit entry
        /// reaches the damage limb it landed on.</summary>
        public Component? Attached;
        public GameObject? Parent;
        public T? GetComponentInParent<T>() where T : class => Attached as T ?? Parent?.GetComponentInParent<T>();
    }
}

public abstract class PlayerInventoryBase : UnityObjectDouble
{
    public Player.PlayerAgent Owner = null!;
    public ItemEquippable? WieldedItem;
    /// <summary>The same item under the name the reload hooks read it by. This build exposes the wielded item
    /// twice — the field the reload bodies use and the property the equipment adapter uses — and the game keeps
    /// them the same object, so the double answers both from one storage. The field is declared non-nullable
    /// because the interop metadata carries no nullable annotation for it, which is the signature the hooks were
    /// compiled against.</summary>
    public ItemEquippable m_wieldedItem { get => WieldedItem!; set => WieldedItem = value; }
    /// <summary>The reload family's three native bodies: the game's own gate, the player's ask and the body that
    /// moves the rounds. The hooks patch them by name, so the double declares them here.</summary>
    public bool CanReloadCurrent() => true;
    public void TriggerReload() { }
    public void DoReload() { }
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
    /// <summary>The game's own part-slot enum, with the members `GearPartHolder` exposes a GameObject for and
    /// their real numeric values. Doubles follow build 20403457 metadata; behaviour is synthetic.</summary>
    public enum eGearComponent : byte
    {
        None = 0, FireMode = 1, Category = 2, FrontPart = 12, FrontPartAttachmentA = 13, FrontPartAttachmentB = 14,
        ReceiverPart = 16, ReceiverPartAttachment = 17, StockPart = 19, SightPart = 21, MagPart = 23,
        FlashlightPart = 25, ToolMainPart = 27, ToolMainPartAttachment = 29, ToolGripPart = 30, ToolDeliveryPart = 33,
        ToolDeliveryPartAttachment = 35, ToolPayloadPart = 37, ToolTargetingPart = 40, ToolScreenPart = 42,
        MeleeHeadPart = 44, MeleeNeckPart = 46, MeleeHandlePart = 48, MeleePommelPart = 50
    }

    /// <summary>One gear as the game holds it. It stands in for the interop type of the same name, so it derives
    /// from the interop base the game's own arrays and lists are constrained to — which is what lets the production
    /// hook's `Il2CppReferenceArray&lt;GearIDRange&gt;` signature compile against this fixture.
    ///
    /// That base's own constructor registers the object with IL2CPP and therefore needs a live game, which a test
    /// host is not: the fixture builds gears through <see cref="Create"/>, which never runs a constructor and
    /// suppresses the finalizer that would free a native handle that was never taken. Nothing in this fixture reads
    /// the base's `Pointer`, and the two members the production code reads are answered here.</summary>
    public sealed class GearIDRange : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        public uint Checksum;
        // The gear's own record of the offline gear block it was built from; the game writes it in
        // GearManager.LoadOfflineGearDatas, and a gear that did not come from such a block leaves it unset.
        public string? PlayfabItemInstanceId;
        // Per-component part block ids, the game's own readback of what a slot holds. A field initializer would not
        // run for an object built without a constructor, so it is made on first use.
        private Dictionary<eGearComponent, uint>? _components;
        public Dictionary<eGearComponent, uint> Components => _components ??= new();
        /// <summary>When set, reading a component throws the way a native read off a destroyed gear does. It is what
        /// the "an identity read that throws leaves the whole slot alone" case switches on.</summary>
        public bool ThrowOnRead;
        public uint GetChecksum() => Checksum;
        public uint GetCompID(eGearComponent comp)
            => ThrowOnRead ? throw new NullReferenceException("fixture gear-component read failure") : Components.TryGetValue(comp, out var value) ? value : 0;

        private GearIDRange() : base(IntPtr.Zero) { }

        /// <summary>One fixture gear, with no native object behind it and no finalizer to run.</summary>
        public static GearIDRange Create()
        {
            var gear = (GearIDRange)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GearIDRange));
            GC.SuppressFinalize(gear);
            return gear;
        }
    }

    /// <summary>The part container: twenty-one part GameObjects and the gear record naming the block it came from.
    /// The presentation hook's target and the only game type this package writes through.</summary>
    public sealed class GearPartHolder : UnityObjectDouble
    {
        public GearIDRange? GearIDRange;
        public UnityEngine.GameObject? FrontPart, FrontPartAttachmentA, FrontPartAttachmentB, ReceiverPart,
            ReceiverPartAttachment, StockPart, SightPart, MagPart, FlashlightPart, ToolMainPart, ToolMainPartAttachment,
            ToolGripPart, ToolDeliveryPart, ToolDeliveryPartAttachment, ToolPayloadPart, ToolTargetingPart,
            ToolScreenPart, MeleeHeadPart, MeleeNeckPart, MeleeHandlePart, MeleePommelPart;

        public void OnAllPartsSpawned() { }

        /// <summary>Fixture wiring for one slot: the production code has the explicit table this mirrors.</summary>
        internal void SetPart(ForgeWeapon.Native.GearPartSlot slot, UnityEngine.GameObject part)
        {
            switch (slot)
            {
                case ForgeWeapon.Native.GearPartSlot.FrontPart: FrontPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.FrontPartAttachmentA: FrontPartAttachmentA = part; break;
                case ForgeWeapon.Native.GearPartSlot.FrontPartAttachmentB: FrontPartAttachmentB = part; break;
                case ForgeWeapon.Native.GearPartSlot.ReceiverPart: ReceiverPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.ReceiverPartAttachment: ReceiverPartAttachment = part; break;
                case ForgeWeapon.Native.GearPartSlot.StockPart: StockPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.SightPart: SightPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.MagPart: MagPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.FlashlightPart: FlashlightPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.ToolMainPart: ToolMainPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.ToolMainPartAttachment: ToolMainPartAttachment = part; break;
                case ForgeWeapon.Native.GearPartSlot.ToolGripPart: ToolGripPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.ToolDeliveryPart: ToolDeliveryPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.ToolDeliveryPartAttachment: ToolDeliveryPartAttachment = part; break;
                case ForgeWeapon.Native.GearPartSlot.ToolPayloadPart: ToolPayloadPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.ToolTargetingPart: ToolTargetingPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.ToolScreenPart: ToolScreenPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.MeleeHeadPart: MeleeHeadPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.MeleeNeckPart: MeleeNeckPart = part; break;
                case ForgeWeapon.Native.GearPartSlot.MeleeHandlePart: MeleeHandlePart = part; break;
                case ForgeWeapon.Native.GearPartSlot.MeleePommelPart: MeleePommelPart = part; break;
            }
        }
    }
    /// <summary>The gear pool the loadout policy narrows: the game's own `GearManager.m_gearPerSlot`, indexed by the
    /// slot's own number, one `Il2CppSystem.Collections.Generic.List&lt;GearIDRange&gt;` per slot. A fixture list is
    /// the stand-in for that game-owned list, so a case can read back exactly what the narrowing wrote into it.
    /// `GetAllGearForSlot` is declared for the hook's own patch attribute and answers the projection's own input,
    /// which is the game's returned array read item by item.</summary>
    public sealed class GearManager
    {
        public static GearManager? Current;
        public GearPoolList?[] m_gearPerSlot = new GearPoolList?[12];

        public static GearIDRange[] GetAllGearForSlot(Player.InventorySlot slot)
            => Current == null || Current.m_gearPerSlot[(int)slot] == null
                ? Array.Empty<GearIDRange>()
                : Current.m_gearPerSlot[(int)slot]!.Items().ToArray();

        public void OnGearLoadingDone() { }
    }

    /// <summary>One slot of the game's own gear pool as the narrowing reads and rewrites it. It stands in for the
    /// interop list type the game's own pool holds, which is why it derives from it, and it keeps its items in a
    /// managed list a case can read back: "the narrowing rewrote the game's own list" is asserted on the list.
    ///
    /// The interop list's own constructor goes through IL2CPP, so a fixture builds one through
    /// <see cref="Create"/>, which never runs a constructor and suppresses the finalizer that would free a native
    /// handle that was never taken. Only the four members this fixture answers are ever read.</summary>
    public sealed class GearPoolList : Il2CppSystem.Collections.Generic.List<GearIDRange>
    {
        /// <summary>The items this fixture answers with. A field initializer would not run for an object built
        /// without a constructor, so the list is made by <see cref="Create"/> itself.</summary>
        private List<GearIDRange> _held = null!;

        /// <summary>When set, reading this list throws the way a native read off a destroyed list does. It is what
        /// the "a pool read that throws leaves every slot alone" case switches on.</summary>
        public bool ThrowOnRead;

        private GearPoolList(IntPtr pointer) : base(pointer) { }

        public static GearPoolList Create(params GearIDRange[] items)
        {
            var list = (GearPoolList)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GearPoolList));
            GC.SuppressFinalize(list);
            list._held = new List<GearIDRange>(items);
            return list;
        }

        public override int Count => ThrowOnRead ? throw new NullReferenceException("fixture gear-pool read failure") : _held.Count;

        public override GearIDRange this[int index]
            => ThrowOnRead ? throw new NullReferenceException("fixture gear-pool read failure") : _held[index];

        public override void Clear() => _held.Clear();

        public override void Add(GearIDRange gear) => _held.Add(gear);

        /// <summary>The block ids this list offers, in order, read back through the same record text the game's own
        /// offline loader writes onto a gear.</summary>
        public uint[] BlockIds() => _held.Select(Block).ToArray();

        /// <summary>The items this list holds, the way the fixture reads them back.</summary>
        public IReadOnlyList<GearIDRange> Items() => _held;

        /// <summary>The block id one offered gear carries, as the policy's allow-list names it.</summary>
        public static uint Block(GearIDRange gear) => uint.Parse(
            gear.PlayfabItemInstanceId!["OfflineGear_ID_".Length..], System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The same block id, or zero for a gear that carries no record text at all — a workshop gear,
        /// whose identity is its category component instead.</summary>
        public static uint BlockOrZero(GearIDRange gear) => gear.PlayfabItemInstanceId == null ? 0 : Block(gear);
    }
    /// <summary>Fire is the per-shot slot; BulletHit is the static hit routine every weapon family shares.</summary>
    public class BulletWeapon : Weapon
    {
        /// <summary>The burst the firing archetype set up on this weapon, read when a burst sequence starts.</summary>
        public int m_burstMax;
        /// <summary>The magazine's own capacity, which is the cap a clip-set request is refused over.</summary>
        public int ClipSize;
        public int GetMaxClip() => ClipSize;
        public void SetCurrentClip(int clip) => Clip = clip;
        /// <summary>The archetype instance the instance-override rows replace, and the magazine and last-fire
        /// stamp the same applier refreshes with it.</summary>
        public BulletWeaponArchetype? m_archeType;
        public int m_clip;
        public float m_lastFireTime;
        public virtual void Fire(bool resetRecoilSimilarity = true) { }
        public static bool BulletHit(Weapon.WeaponHitData? weaponRayData, bool doDamage) => true;
    }
    public class Shotgun : BulletWeapon
    {
        public override void Fire(bool resetRecoilSimilarity = true) { }
    }
    // Overrides nothing: the base BulletWeapon.Fire is the one shot entry point for the rifle family.
    public class RifleWeapon : BulletWeapon { }
    /// <summary>Every other machine's copy of a player's weapon: the class overrides Fire on its own and is never
    /// first person, which is the shape the build gives both synced families. The synced weapon is replayed from
    /// the replicated shot count, so this body — not the unsynced one — is what a remote player's trigger pull
    /// runs here.</summary>
    public class BulletWeaponSynced : BulletWeapon
    {
        public override void Fire(bool resetRecoilSimilarity = true) { }
        /// <summary>The one chain that rebuilds the archetype, and the member the gear-spawn replay hook is
        /// declared against.</summary>
        public override void OnGearSpawnComplete() { }
    }
    public class ShotgunSynced : BulletWeaponSynced
    {
        public override void Fire(bool resetRecoilSimilarity = true) { }
    }
    // Declares no Fire of its own, exactly like the rifle's unsynced and synced variants in the build.
    public class RifleWeaponSynced : BulletWeaponSynced { }
}

namespace SNetwork
{
    /// <summary>The two static reads the holder channel makes: whether this machine has a local player at all, and
    /// that player's own reference — which is the session a local step is answered with.</summary>
    public static class SNet
    {
        public static bool IsMaster { get; set; } = true;
        public static SNet_Player? LocalPlayer;
        public static bool HasLocalPlayer => LocalPlayer != null;
    }
    public sealed class SNet_IPlayerAgent
    {
        public object? Target;
        public T? TryCast<T>() where T : class => Target as T;
    }
    public sealed class SNet_Player : UnityObjectDouble
    {
        public SNet_IPlayerAgent? PlayerAgent;
        public bool HasPlayerAgent => PlayerAgent != null;
        /// <summary>The player's own session id, which is what a holder step is routed by.</summary>
        public ulong Lookup;
        /// <summary>The session slot this player holds. The production session half addresses a holder by it
        /// (`WeaponHolderChannel.SessionOf`), and the game's own answer is a method that throws for a player the
        /// session has no slot for, which is why the reader guards it. The stand-in answers the slot it was given
        /// and leaves the guard to the caller.</summary>
        public int Slot;
        public int PlayerSlotIndex() => Slot;
        /// <summary>The two membership answers the remote melee half reads: a swing by this machine's own player
        /// or by a bot is already published by the hit entry that performed it.</summary>
        public bool IsLocal;
        public bool IsBot;
    }
}

namespace Player
{
    public enum InventorySlot
    {
        None = 0, GearStandard = 1, GearSpecial = 2, GearClass = 3, ResourcePack = 4, Consumable = 5,
        ConsumableHeavy = 6, InPocket = 7, InLevelCarry = 8, Pickup = 9, GearMelee = 10, HackingTool = 11
    }
    public sealed class PlayerAgent : Agents.Agent
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
        /// <summary>This backpack's own ammunition pools, which the reload readback compares the pack against.</summary>
        public PlayerAmmoStorage? AmmoStorage;
        /// <summary>The pocket items the removal body counts before it takes one, keyed by the game's item id.</summary>
        public readonly Dictionary<uint, int> Pocket = new();
        public int CountPocketItem(uint itemID) => Pocket.TryGetValue(itemID, out var count) ? count : 0;
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
        /// <summary>The three manager wrappers the ammunition rows read and write through: the gift carries one
        /// relative amount per pool, and the two reads answer the pool of the player the row named.</summary>
        public static void GiveAmmoToPlayer(SNetwork.SNet_Player player, float standard, float special, float @class) { }
        public static int GetBulletsInPack(Player.AmmoType type, SNetwork.SNet_Player player) => 0;
        public static int GetAmmoMaxCap(Player.AmmoType type, SNetwork.SNet_Player player) => 0;
        /// <summary>The two host entry points the inventory rows submit through, both of them the game's own
        /// replication points rather than local list edits. The drop body that used a third one is gone: the node
        /// list has no drop node, so neither the row nor the implementation exists.</summary>
        public static void MasterAddItem(Player.pItemData_WithOwner data, SNetwork.SNet_Player? sendOnlyTo) { }
        public static bool TryMasterRemovePocketItemWithID(uint itemID, SNetwork.SNet_Player player) => false;
    }
}

/// <summary>The game's own expedition state, which the projection hook reads to stay out of a level. The build
/// exposes it as a static property, so the fixture keeps the same shape and a case sets it directly.</summary>
public static class GameStateManager
{
    public static bool IsInExpedition { get; set; }
}

namespace Globals
{
    /// <summary>Which rundown this process loaded, the key a loadout policy activates on.</summary>
    public static class Global
    {
        public static uint RundownIdToLoad;
    }
}

// The instance-override and item-id families the session hands to its own registration. These doubles are
// compile-time pins for the members those production sources name; no case in this suite drives them, so their
// behaviour is deliberately empty and the assertions that exercise them live in tests/WeaponOverride and
// tests/SupplyFacts.

/// <summary>The interop object base the block types derive from: `Il2CppClassPointerStore<T>` and the applier's
/// own `Construct<T>` both require it, and a double supplies its own pointer.</summary>
public class Il2CppDouble : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
{
    public Il2CppDouble() : base(Pointers.Next()) { }
    public new T? TryCast<T>() where T : class => this as T;
}

/// <summary>The two-component value type the archetype block carries inline, so it is copied by assignment
/// rather than cloned the way the recoil ranges are.</summary>
public struct Vector2
{
    public float x;
    public float y;
    public Vector2(float x, float y) { this.x = x; this.y = y; }
}

namespace GameData
{
    /// <summary>`MinMaxValue` is an il2cpp object, not an embedded pair of floats, which is why the applier has to
    /// clone it rather than assign the original's onto its own block.</summary>
    public class MinMaxValue : Il2CppDouble
    {
        public float Min;
        public float Max;
    }

    /// <summary>Only the members the applier copies and writes; the class defaults the native constructor writes
    /// are not modelled because the double's constructor is not the game's.</summary>
    public class ArchetypeDataBlock : Il2CppDouble
    {
        public int FireMode;
        public uint RecoilDataID;
        public int DamageBoosterEffect;
        public float Damage;
        public Vector2 DamageFalloff;
        public float StaggerDamageMulti;
        public float PrecisionDamageMulti;
        public int DefaultClipSize;
        public float DefaultReloadTime;
        public float CostOfBullet;
        public float ShotDelay;
        public float ShellCasingSize;
        public Vector2 ShellCasingSpeedRange;
        public bool PiercingBullets;
        public int PiercingDamageCountLimit;
        public float HipFireSpread;
        public float AimSpread;
        public float EquipTransitionTime;
        public float AimTransitionTime;
        public float BurstDelay;
        public int BurstShotCount;
        public int ShotgunBulletCount;
        public int ShotgunConeSize;
        public int ShotgunBulletSpread;
        public float SpecialChargetupTime;
        public float SpecialCooldownTime;
        public float SpecialSemiBurstCountTimeout;
        public float Sentry_StartFireDelay;
        public float Sentry_RotationSpeed;
        public float Sentry_DetectionMaxRange;
        public float Sentry_DetectionMaxAngle;
        public bool Sentry_FireTowardsTargetInsteadOfForward;
        public bool Sentry_ForceAimTowardsBody;
        public float Sentry_LongRangeThreshold;
        public float Sentry_ShortRangeThreshold;
        public bool Sentry_LegacyEnemyDetection;
        public bool Sentry_FireTagOnly;
        public bool Sentry_PrioTag;
        public float Sentry_StartFireDelayTagMulti;
        public float Sentry_RotationSpeedTagMulti;
        public float Sentry_DamageTagMulti;
        public float Sentry_StaggerDamageTagMulti;
        public float Sentry_CostOfBulletTagMulti;
        public float Sentry_ShotDelayTagMulti;
    }

    public class RecoilDataBlock : Il2CppDouble
    {
        public MinMaxValue? power;
        public float spring;
        public float dampening;
        public float hipFireCrosshairSizeDefault;
        public float hipFireCrosshairRecoilPop;
        public float hipFireCrosshairSizeMax;
        public MinMaxValue? horizontalScale;
        public MinMaxValue? verticalScale;
        public float directionalSimilarity;
        public float worldToViewSpaceBlendHorizontal;
        public float worldToViewSpaceBlendVertical;
        public float recoilPosImpulse;
        public float recoilPosShift;
        public float recoilPosShiftWeight;
        public float recoilPosStiffness;
        public float recoilPosDamping;
        public float recoilPosImpulseWeight;
        public float recoilCameraPosWeight;
        public float recoilAimingWeight;
        public float recoilRotImpulse;
        public float recoilRotStiffness;
        public float recoilRotDamping;
        public float recoilRotImpulseWeight;
        public float recoilCameraRotWeight;
        public float concussionIntensity;
        public float concussionFrequency;
        public float concussionDuration;
    }

    /// <summary>The two members the item-id resolver reads: the official id itself and the slot the block says the
    /// item belongs in, which is what narrows the pocket rows.</summary>
    public sealed class ItemDataBlock
    {
        public static readonly Dictionary<uint, ItemDataBlock> Blocks = new();
        public uint persistentID;
        public Player.InventorySlot inventorySlot;
        public static ItemDataBlock? GetBlock(uint id) => Blocks.TryGetValue(id, out var block) ? block : null;
    }
}

namespace Gear
{
    /// <summary>The archetype instance a weapon holds. `m_archetypeData` and `m_recoilData` are the two fields the
    /// applier swaps; `m_nextShotTimer` is the shot gate an override can zero. The firing archetypes the
    /// attack-instance hooks are declared on already live in HookTargetDoubles and narrow through `TryCast`, which
    /// is the interop idiom the applier uses for the burst length field.</summary>
    public class BulletWeaponArchetype : UnityObjectDouble
    {
        public GameData.ArchetypeDataBlock? m_archetypeData;
        public GameData.RecoilDataBlock? m_recoilData;
        public float m_nextShotTimer;
        public float m_nextBurstTimer;
    }
}

namespace SNetwork
{
    /// <summary>The two by-value handles the inventory rows put into a `pItemData`: the replicator reference a drop
    /// point carries and the player handle the game's own add path fills from the player it was given.</summary>
    public static class SNetStructs
    {
        public struct pReplicator
        {
            public ushort keyPlusOne;
            public bool IsValid() => keyPlusOne != 0;
        }

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
    public struct pItemData_Custom
    {
        public float ammo;
        public byte byteId;
        public byte byteState;
    }

    /// <summary>The item payload the three host entry points take. The two level-address fields keep the game's
    /// own namespaces, which is where the production source resolves them from.</summary>
    public struct pItemData
    {
        public pItemData_Custom custom;
        public uint itemID_gearCRC;
        public SNetwork.SNetStructs.pReplicator replicatorRef;
        public InventorySlot slot;
        public LevelGeneration.LG_LayerType originLayer;
        public AIGraph.pCourseNode originCourseNode;
    }

    public struct pItemData_WithOwner
    {
        public SNetwork.SNetStructs.pPlayer owningPlayer;
        public pItemData data;
    }

    /// <summary>The game's own in-level agent list, which is the candidate set a player reference is resolved
    /// against before the owning domain confirms it.</summary>
    public static class PlayerManager
    {
        public static List<PlayerAgent>? PlayerAgentsInLevel = new();
    }
}

namespace UnityEngine
{
    /// <summary>The rotation a drop point is validated with; only the identity is ever passed.</summary>
    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;
        public static Quaternion identity => new() { w = 1f };
    }
}
