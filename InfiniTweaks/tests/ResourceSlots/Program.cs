using InfiniTweaks;
using LevelGeneration;
using Player;
using UnityEngine;

ResourceHandling.Checks();

namespace InfiniTweaks
{
    internal static class Settings { public static Entry Deposit = new(); }
    internal sealed class Entry { public bool Value = true; }
    internal static class PlacementPreview { public static void Hide() { } }
    internal static class Plugin { public static Logger PluginLog = new(); }
    internal sealed class Logger { public void LogWarning(string value) { } }
    internal static partial class ResourceHandling
    {
        private static int placed;
        internal static bool IsOpen(LG_WeakResourceContainer box) => box.ISOpen;
        internal static void ResetSelection() { _selected = null; PlacementPreview.Hide(); }
        private static bool Place(PlayerAgent player, Vector3 position, Quaternion rotation, Node node, bool floor) { placed++; return true; }
        internal static void Checks()
        {
            int checks = 0;
            void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
            var box = new LG_WeakResourceContainer { ISOpen = true };
            var anchors = new[] { new Vector3(.009f, 0, .163f), new Vector3(-.343f, 0, .163f), new Vector3(.358f, 0, .163f) };
            box.m_storage.m_storageSlots = anchors.Select(p => new StorageSlot { ResourcePack = new Transform { position = p }, Consumable = new Transform { position = p } }).ToArray();
            RegisterSlots(box); RegisterSlots(box);
            var slots = Slots[box.GetInstanceID()];
            Check(slots.Count == 3, "Repeated registration creates exactly one interaction per native compartment.");
            Check(slots.All(s => s.Interaction.active && s.Object.layer == LayerManager.LAYER_INTERACTION), "Open shelves use native interaction layer.");
            var player = new PlayerAgent();
            Check(slots[0].Interaction.PlayerCanInteract(player), "Held resource can select empty open slot.");
            player.Inventory.WieldedSlot = InventorySlot.Consumable;
            Check(CanDeposit(slots[0], player), "Consumables use their own native attachment point.");
            player.Inventory.WieldedSlot = InventorySlot.GearStandard;
            Check(!CanDeposit(slots[0], player), "Weapon cannot be deposited.");
            player.Inventory.WieldedSlot = InventorySlot.ResourcePack;
            player.CourseNode.m_dimension.DimensionIndex = 1;
            Check(!CanDeposit(slots[0], player), "Cross-dimension placement rejected.");
            player.CourseNode.m_dimension.DimensionIndex = 0;
            var sync = new LG_PickupItem_Sync { Pointer = new IntPtr(123) };
            var state = new pPickupItemState { placement = new() { position = BoxVolumes[0].Position } };
            TrackSlot(sync, state);
            Check(!CanDeposit(slots[0], player) && !slots[0].Interaction.active, "Occupied slot cannot be selected.");
            Check(CanDeposit(slots[1], player), "Neighboring compartment remains available.");
            state.updateCustomDataOnly = true; state.status = ePickupItemStatus.PickedUp;
            TrackSlot(sync, state);
            Check(slots[0].Occupant == sync.Pointer, "Ammo-only notification cannot free occupied slot.");
            state.updateCustomDataOnly = false; TrackSlot(sync, state);
            Check(CanDeposit(slots[0], player) && slots[0].Interaction.active, "Pickup immediately releases its exact compartment.");
            state.status = ePickupItemStatus.PlacedInLevel;
            TrackSlot(sync, state, BoxVolumes[1].Position);
            Check(slots[1].Occupant == sync.Pointer && slots[0].Occupant == IntPtr.Zero, "Initial setup uses actual world position instead of incomplete placement data.");
            ReleaseSlot(sync.Pointer);
            Check(slots[1].Occupant == IntPtr.Zero, "Despawn releases occupancy.");
            slots[0].Interaction.OnInteractionSelected!(player, true);
            Check(_selected == slots[0] && GuiManager.InteractionLayer.InteractPromptVisible, "Native selection shows deposit prompt.");
            slots[0].Interaction.OnInteractionTriggered!(player);
            Check(placed == 1 && _selected == null, "Native timed completion requests one placement and clears preview.");
            box.ISOpen = false; DepositContainerChanged(box.m_sync);
            Check(slots.All(s => !s.Interaction.active) && !CanDeposit(slots[0], player), "Closing hides interactions and prevents deposit.");
            box.ISOpen = true; DepositContainerChanged(box.m_sync);
            Check(slots.All(s => s.Interaction.active), "Reopening restores empty slots.");
            Check(FindPlacementSlot(BoxVolumes[0].Position) == slots[0] && FindPlacementSlot(BoxVolumes[0].Position + new Vector3(.1f, 0, 0)) == null,
                "Host slot validation applies only to exact attachment positions, not nearby floor drops.");
            TrackSlot(sync, new() { placement = new() { position = BoxVolumes[0].Position } }); ResetSlotOccupants();
            Check(slots.All(s => s.Occupant == IntPtr.Zero), "Checkpoint rebuild resets stale occupancy before reconstructing native state.");
            Check(Occupants.Count == 0, "Checkpoint reset also clears reverse sync ownership.");
            var original = new pPickupItemState { placement = new() { position = anchors[0] } };
            Check(TrackSlot(sync, original) == slots[0], "The authoritative lookup is returned for container attachment.");
            var competing = new LG_PickupItem_Sync { Pointer = (IntPtr)456 };
            Check(TrackSlot(competing, original) == null && slots[0].Occupant == sync.Pointer, "A second occupant cannot silently overwrite the first identity.");
            ReleaseSlot(sync.Pointer);
            box.transform.localPosition = new Vector3(10, 2, 5);
            box.transform.localRotation = Quaternion.Yaw90;
            box.transform.localScale = new Vector3(2, 2, 2);
            Check(FindSlot(new Vector3(10.326f, 2, 4.982f)) == slots[0], "Translated, rotated and scaled box resolves measured first shelf world point.");
            Check(FindSlot(new Vector3(10.326f, 2, 5.686f)) == slots[1], "Second shelf remains distinct after parent transformation.");
            Check(FindSlot(new Vector3(10.326f, 2, 4.284f)) == slots[2], "Third shelf remains distinct after parent transformation.");
            Check(FindSlot(new Vector3(20, 2, 5)) == null, "World point outside transformed container is rejected.");
            foreach (var volume in LockerVolumes) Check(volume.Size.x > 0 && volume.Size.y > 0 && volume.Size.z > 0, "Locker shelf volume is nonzero.");
            RemoveSlots(box); Check(Slots.Count == 0, "Container destruction releases slot registry.");
            Console.WriteLine($"PASS: {checks} production deposit-slot checks; managed doubles do not certify native selection/rendering in GTFO.");
        }
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string name) { } }
    public sealed class HarmonyPostfix : Attribute { }
}
namespace UnityEngine
{
    public class Object { private static int next; private int id = ++next; public int GetInstanceID() => id; public static void Destroy(Object value) { } }
    public class Component : Object { public GameObject gameObject = null!; public T? TryCast<T>() where T : class => this as T; }
    public class GameObject : Object
    {
        public int layer; public Transform transform = new();
        public GameObject(string name) { }
        public T AddComponent<T>() where T : Component, new() => new T { gameObject = this };
    }
    public class Transform
    {
        public Transform? parent;
        public Vector3 localPosition, localScale = Vector3.one;
        public Quaternion localRotation = Quaternion.identity;
        public Quaternion rotation { get => localRotation; set => localRotation = value; }
        private System.Numerics.Matrix4x4 World =>
            System.Numerics.Matrix4x4.CreateScale(localScale.x, localScale.y, localScale.z) *
            System.Numerics.Matrix4x4.CreateFromQuaternion(localRotation.Value) *
            System.Numerics.Matrix4x4.CreateTranslation(localPosition.x, localPosition.y, localPosition.z) *
            (parent?.World ?? System.Numerics.Matrix4x4.Identity);
        public Vector3 position
        {
            get { var p = System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, World); return new(p.X, p.Y, p.Z); }
            set => localPosition = parent == null ? value : parent.InverseTransformPoint(value);
        }
        public void SetParent(Transform value, bool world) { var previous = position; parent = value; if (world) position = previous; }
        public Vector3 InverseTransformPoint(Vector3 value)
        {
            if (!System.Numerics.Matrix4x4.Invert(World, out var inverse)) throw new Exception("Singular transform");
            var p = System.Numerics.Vector3.Transform(new(value.x, value.y, value.z), inverse); return new(p.X, p.Y, p.Z);
        }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 one => new(1, 1, 1);
        public float sqrMagnitude => x*x+y*y+z*z;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator *(Vector3 a, float b) => new(a.x*b,a.y*b,a.z*b);
    }
    public record struct Quaternion(System.Numerics.Quaternion Value)
    {
        public static Quaternion identity => new(System.Numerics.Quaternion.Identity);
        public static Quaternion Yaw90 => new(System.Numerics.Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0));
    }
    public sealed class BoxCollider : Component { public Vector3 size, center; }
    public static class Mathf { public static float Abs(float value) => Math.Abs(value); }
}
namespace LevelGeneration
{
    public sealed class Node { public Dimension m_dimension = new(); }
    public sealed class Dimension { public int DimensionIndex; }
    public sealed class LG_ResourceContainer_Storage : Component { public StorageSlot[] m_storageSlots = Array.Empty<StorageSlot>(); }
    public sealed class LG_WeakResourceContainer : Component { public string name = "box"; public Transform transform = new(); public bool ISOpen, m_isLocker; public Node SpawnNode = new(); public LG_ResourceContainer_Storage m_storage = new(); public LG_ResourceContainer_Sync m_sync = new(); }
    public sealed class StorageSlot { public Transform? ResourcePack, Consumable; }
    public sealed class LG_ResourceContainer_Sync : Component { public IntPtr Pointer => (IntPtr)GetInstanceID(); public void OnStateChange() { } }
    public sealed class LG_PickupItem_Sync { public IntPtr Pointer; }
}
namespace Player
{
    public enum InventorySlot { ResourcePack, Consumable, GearStandard }
    public sealed class PlayerAgent { public bool Alive = true, IsLocallyOwned = true; public Node CourseNode = new(); public Inventory Inventory = new(); }
    public class Inventory { public Item WieldedItem = new(); public InventorySlot WieldedSlot; }
    public sealed class Item { public string PublicName = "Ammo Pack"; }
    public sealed class PlayerInventoryLocal : Inventory { public PlayerAgent Owner = new(); public void DoWieldItem() { } }
}
namespace Localization { public static class Text { public static string Get(uint id) => id == 864 ? "Deposit {0}" : "Hold {0}"; } }
public sealed class Interact_Timed : Component
{
    public BoxCollider m_colliderToOwn = null!; public float InteractDuration; public string InteractionMessage = ""; public bool OnlyActiveWhenLookingStraightAt, active;
    public Func<PlayerAgent, bool>? ExternalPlayerCanInteract;
    public Action<PlayerAgent, bool>? OnInteractionSelected;
    public Action<PlayerAgent>? OnInteractionTriggered;
    public bool PlayerCanInteract(PlayerAgent player) => ExternalPlayerCanInteract!(player);
    public void SetActive(bool value) => active = value;
}
public static class LayerManager { public const int LAYER_INTERACTION = 7; }
public static class GuiManager { public static InteractionGuiLayer InteractionLayer = new(); }
public sealed class InteractionGuiLayer { public bool InteractPromptVisible; public void SetInteractPrompt(string title, string binding, ePUIMessageStyle style) { } }
public enum ePUIMessageStyle { Default }
public enum InputAction { Use }
public static class InputMapper { public static string GetBindingName(InputAction action) => "E"; }
public enum ePickupItemStatus { PlacedInLevel, PickedUp }
public struct pPickupItemState { public bool updateCustomDataOnly; public ePickupItemStatus status; public pPickupPlacement placement; }
public struct pPickupPlacement { public Vector3 position; }
