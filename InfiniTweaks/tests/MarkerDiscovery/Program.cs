using System.Reflection;
using InfiniTweaks;
using LevelGeneration;
using Player;

var sync = new LG_ResourceContainer_Sync(1);
var box = new LG_WeakResourceContainer { m_sync = sync, Open = true };
var fog = new ItemInLevel { Id = 117, container = new Storage { m_core = box } };
var other = new ItemInLevel { Id = 114, container = new Storage { m_core = new LG_WeakResourceContainer { m_sync = new(2) } } };
ResourceHandling.Items.Add(fog.Id, fog); ResourceHandling.Items.Add(other.Id, other);
void Call(string method, params object[] args) => typeof(ItemMarkers).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
Call("ContainerOpened", sync, new pResourceContainerItemState(eResourceContainerStatus.Closed));
Check(ItemMarkers.Remembered.Count == 0, "Closed lockers never discover contents.");
Call("ContainerOpened", sync, new pResourceContainerItemState(eResourceContainerStatus.Open));
Check(ItemMarkers.Remembered.SequenceEqual(new[] { 117 }), "Open locker uses sync ownership, without requiring parent-child transforms, and excludes other lockers.");
ItemMarkers.Remembered.Clear();
var local = new PlayerAgent { IsLocallyOwned = true };
Call("PickupSelected", fog.Interaction, false, local);
Call("PickupSelected", fog.Interaction, true, new PlayerAgent());
Check(ItemMarkers.Remembered.Count == 0, "Deselection and remote selection do not discover local pickups.");
Call("PickupSelected", new Interact_Timed(), true, local);
Check(ItemMarkers.Remembered.Count == 0, "Unrelated interactions cannot reveal an item.");
Call("PickupSelected", fog.Interaction, true, local);
Check(ItemMarkers.Remembered.SequenceEqual(new[] { 117 }), "Native pickup selection discovers exactly its owned item, including sibling components.");
ItemMarkers.Remembered.Clear(); Settings.Markers.Value = false;
Call("PickupSelected", fog.Interaction, true, local);
Check(ItemMarkers.Remembered.Count == 0, "Disabled markers do not discover selected pickups.");
Settings.Markers.Value = true; fog.Empty = true;
ItemMarkers.UpdateLabel(fog);
Check(ItemMarkers.Forgotten.SequenceEqual(new[] { 117 }), "Zero quantity removes an existing counted pickup.");
fog.Empty = false; ItemMarkers.UpdateLabel(fog);
Check(ItemMarkers.Remembered.SequenceEqual(new[] { 117 }), "Quantity arriving after locker opening re-registers the item.");
ItemMarkers.Remembered.Clear(); box.Open = false; ItemMarkers.UpdateLabel(fog);
Check(ItemMarkers.Remembered.Count == 0, "Quantity updates never reveal a closed locker.");
ItemMarkers.Pins.Add(117, new()); ItemMarkers.UpdateLabel(fog);
Check(ItemMarkers.Labels == 1, "Existing pin updates its label without rediscovery.");
ItemMarkers.Remembered.Clear(); ItemMarkers.Pins.Clear(); fog.container = null;
fog.Sync.State = new() { status = ePickupItemStatus.PlacedInLevel, placement = new() { hasBeenPickedUp = true } };
ItemMarkers.Dismissed.Add(117);
ItemMarkers.PlacementCompleted(fog, fog.Sync.State);
Check(!ItemMarkers.Dismissed.Contains(117) && ItemMarkers.Remembered.SequenceEqual(new[] { 117 }), "Fresh floor drop clears an old dismissal and requests its marker.");
ItemMarkers.Remembered.Clear(); fog.Empty = true; ItemMarkers.UpdateLabel(fog);
fog.Empty = false; ItemMarkers.UpdateLabel(fog);
Check(ItemMarkers.Remembered.SequenceEqual(new[] { 117 }), "Late quantity on a dropped floor item requests rediscovery.");
ItemMarkers.Remembered.Clear(); ItemMarkers.Dismissed.Add(117);
fog.Sync.State = fog.Sync.State with { updateCustomDataOnly = true };
ItemMarkers.PlacementCompleted(fog, fog.Sync.State); ItemMarkers.UpdateLabel(fog);
Check(ItemMarkers.Dismissed.Contains(117) && ItemMarkers.Remembered.Count == 0, "Quantity-only updates preserve a later manual dismissal.");
ItemMarkers.Dismissed.Clear(); fog.Sync.State = new() { status = ePickupItemStatus.PlacedInLevel };
ItemMarkers.PlacementCompleted(fog, fog.Sync.State); ItemMarkers.UpdateLabel(fog);
Check(ItemMarkers.Remembered.Count == 0, "Untouched floor spawns do not reveal unexplored items.");
fog.Sync.State = new() { status = ePickupItemStatus.PickedUp, placement = new() { hasBeenPickedUp = true } };
ItemMarkers.UpdateLabel(fog);
Check(ItemMarkers.Remembered.Count == 0, "A held resource cannot be rediscovered by a quantity update.");
Console.WriteLine("PASS: 15 production discovery routing checks; Unity physics is not simulated.");

public class Interact_Timed { public void OnSelectedChange(bool selected, PlayerAgent player, bool fromCamera) { } }
public class ItemInLevel
{
    public PickupSync Sync = new();
    public PickupSync GetSyncComponent() => Sync;
    public T? TryCast<T>() where T : class => this as T;
    public int Id; public bool Empty; public Storage? container;
    public Interact_Timed Interaction = new();
    public Interact_Timed GetPickupInteraction() => Interaction;
    public int GetInstanceID() => Id;
}
public class CarryItemPickup_Core : ItemInLevel { }
public class PickupSync { public pPickupItemState State; public pPickupItemState GetCurrentState() => State; }
public class Storage { public LG_WeakResourceContainer? m_core; }
namespace Player { public class PlayerAgent { public bool IsLocallyOwned; } }
namespace LevelGeneration
{
    public enum ePickupItemStatus { PlacedInLevel, PickedUp }
    public record struct pPickupPlacement { public bool hasBeenPickedUp; }
    public record struct pPickupItemState { public ePickupItemStatus status; public bool updateCustomDataOnly; public pPickupPlacement placement; }
    public enum eResourceContainerStatus { Open, Closed }
    public record pResourceContainerItemState(eResourceContainerStatus status);
    public class LG_ResourceContainer_Sync
    {
        public IntPtr Pointer; public LG_ResourceContainer_Sync(int id) => Pointer = (IntPtr)id;
        public void OnStateChange() { }
    }
    public class LG_WeakResourceContainer
    {
        public LG_ResourceContainer_Sync? m_sync; public bool Open;
        public T? TryCast<T>() where T : class => this as T;
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Method)] public class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string name) { } }
    public class HarmonyPostfix : Attribute { }
}
namespace InfiniTweaks
{
    internal static class Settings { public static Flag Markers = new(); public class Flag { public bool Value = true; } }
    internal static class ResourceHandling
    {
        internal static Dictionary<int, ItemInLevel> Items = new();
        internal static bool IsOpen(LG_WeakResourceContainer box) => box.Open;
    }
    internal static partial class ItemMarkers
    {
        internal class Pin { }
        internal static Dictionary<int, Pin> Pins = new();
        internal static List<int> Remembered = new(), Forgotten = new();
        private static void CarryChanged(CarryItemPickup_Core item) { }
        internal static HashSet<int> Dismissed = new();
        internal static int Labels;
        private static bool Depleted(ItemInLevel item) => item.Empty;
        private static void RememberPickup(ItemInLevel item, bool explicitDiscovery) { if (!Dismissed.Contains(item.Id)) Remembered.Add(item.Id); }
        private static void Forget(int id) => Forgotten.Add(id);
        private static void Label(Pin pin) => Labels++;
    }
}
