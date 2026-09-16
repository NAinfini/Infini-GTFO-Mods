// Test-only managed doubles. They do not execute or validate native GTFO/Unity behavior.
using System.Globalization;

namespace UnityEngine
{
    public class Object
    {
        private static int _next;
        public int InstanceId = Interlocked.Increment(ref _next);
        public string name = "same-display-name";
        public int GetInstanceID() => InstanceId;
    }
    public class Component : Object
    {
        private Transform? _transform;
        private GameObject? _gameObject;
        public Component? ParentComponent;
        public virtual Transform transform => _transform ??= new Transform();
        public GameObject gameObject => _gameObject ??= new GameObject();
        public T? TryCast<T>() where T : class => this as T;
        public T? GetComponentInParent<T>() where T : class => ParentComponent as T;
    }
    public class Transform : Component
    {
        public override Transform transform => this;
        public readonly List<Transform> Children = new();
        public int childCount => Children.Count;
        public Transform GetChild(int index) => Children[index];
        public Vector3 position, forward;
        public Quaternion rotation;
    }
    public class GameObject : Object
    {
        public Transform transform = new();
        public List<Component> Components = new();
        public T[] GetComponents<T>() where T : Component => Components.OfType<T>().ToArray();
    }
    public struct Quaternion { }
    public struct Vector3
    {
        public float x, y, z;
        public float sqrMagnitude => x*x + y*y + z*z;
        public Vector3 normalized => this;
        public static float Dot(Vector3 a, Vector3 b) => a.x*b.x + a.y*b.y + a.z*b.z;
        public static Vector3 operator *(Vector3 a, float value) => new() { x=a.x*value, y=a.y*value, z=a.z*value };
        public static Vector3 operator -(Vector3 a, Vector3 b) => new() { x=a.x-b.x, y=a.y-b.y, z=a.z-b.z };
    }
}
namespace UnityEngine.AI
{
    public enum NavMeshPathStatus { PathComplete, PathPartial, PathInvalid }
    public class NavMeshPath { public NavMeshPathStatus status = NavMeshPathStatus.PathInvalid; }
    public struct NavMeshHit { public UnityEngine.Vector3 position; }
    public static class NavMesh
    {
        public static bool SamplePosition(UnityEngine.Vector3 position, out NavMeshHit hit, float distance, int mask)
        { hit = default; return false; }
        public static bool CalculatePath(UnityEngine.Vector3 from, UnityEngine.Vector3 to, int mask, NavMeshPath path) => false;
    }
}
namespace AIGraph
{
    public class AIG_CourseNode : UnityEngine.Component
    {
        public bool IsValid = true;
        public int NodeID;
        public LevelGeneration.LG_Area? m_area;
        public LevelGeneration.LG_Zone? m_zone;
        public object? m_navigationInfoHolder;
        public UnityEngine.Vector3 Position;
        public List<Gear.ItemInLevel> m_itemsInNode = new();
    }
}
public enum eDimensionIndex { Reality, Dimension_1 }
public class ExpeditionData
{
    public uint LevelLayoutData = 10, SecondaryLayout, ThirdLayout;
}
public static class Builder
{
    public static LevelGeneration.LG_Floor? CurrentFloor;
    public static ExpeditionData? LevelGenExpedition = new();
}
public class LG_Geomorph : UnityEngine.Component
{
    public UnityEngine.GameObject? m_geoPrefab = new();
    public LevelGeneration.LG_Zone? m_zone;
    public LevelGeneration.LG_Area?[]? m_areas = Array.Empty<LevelGeneration.LG_Area?>();
    public List<LevelGeneration.LG_Plug?>? m_plugs = new();
    public bool m_placed = true, m_areasSetup = true, m_gatesSetup = true;
}
namespace LevelGeneration
{
    public enum LG_LayerType { MainLayer, SecondaryLayer, ThirdLayer }
    public class Dimension
    {
        private static long _nextPointer;
        public IntPtr Pointer = new(Interlocked.Increment(ref _nextPointer));
        public eDimensionIndex DimensionIndex;
        public List<LG_Layer> Layers = new();
    }
    public class LG_Layer
    {
        public Dimension? m_dimension;
        public LG_LayerType m_type;
        public List<LG_Zone> m_zones = new();
    }
    public class LG_Floor : UnityEngine.Component
    {
        public Dimension? MainDimension = new();
        public List<LG_Zone?>? allZones = new();
    }
    public class LG_Zone : UnityEngine.Component
    {
        public bool ThrowOnPosition;
        public UnityEngine.Vector3 Position => ThrowOnPosition ? throw new InvalidOperationException("injected zone read failure") : default;
        public eDimensionIndex DimensionIndex;
        public LG_Layer? Layer;
        public int LocalIndex, m_buildStatus;
        public List<LG_Area?>? m_areas = new();
        public List<AIGraph.AIG_CourseNode>? m_courseNodes = new();
        public object? m_navInfo;
        public List<LG_ComputerTerminal> TerminalsSpawnedInZone = new();
        public LG_ZoneSettings? m_settings;
    }
    public class LG_ZoneSettings
    {
        public GameData.ExpeditionZoneData? m_zoneData;
    }
    public class LG_Area : UnityEngine.Component
    {
        public AIGraph.AIG_CourseNode? m_courseNode;
        public LG_Zone? m_zone;
        public LG_Geomorph? m_geomorph;
        public int UID;
        public UnityEngine.Vector3 Position;
    }
    public class LG_Plug : UnityEngine.Component
    {
        public LG_Plug? m_pariedWith;
        public LG_Area? m_linksFrom, m_linksTo, OriginalParentArea;
        public UnityEngine.Vector3 m_forward, m_position, m_linkPosition;
        public int m_dir;
    }
    public class LG_GenericTerminalItem : UnityEngine.Component
    {
        public AIGraph.AIG_CourseNode? SpawnNode;
        public string? TerminalItemKey;
        public bool ShowInFloorInventory;
    }
    public class LG_ComputerTerminal : UnityEngine.Component
    {
        public AIGraph.AIG_CourseNode? SpawnNode;
        public int SyncID;
        public string? PublicName;
        public bool m_isSetup, IsRegistered;
    }
    public class LG_ComputerTerminalManager
    {
        public static LG_ComputerTerminalManager? Current;
        public Dictionary<int,LG_ComputerTerminal>? m_terminals;
    }
}
namespace Gear
{
    public class ItemData { public uint persistentID; public int inventorySlot; }
    public struct CustomData { public float ammo; }
    public class ItemInLevel : UnityEngine.Component
    {
        public AIGraph.AIG_CourseNode? CourseNode;
        public ItemData? ItemDataBlock;
        public CustomData GetCustomData() => default;
    }
    public class ResourcePackPickup : ItemInLevel { public int m_packType; }
}
namespace SNetwork { public static class SNet { public static bool IsMaster = true; } }
namespace GameData
{
    /// <summary>The zone's own data block, limited to the members the inspection reads. A live zone carries the
    /// game's authored values; the default below is a disabled respawn policy so a double that never sets one is
    /// still an honest live block.</summary>
    public class ExpeditionZoneData
    {
        public bool EnemyRespawning;
        public bool EnemyRespawnRequireOtherZone;
        public int EnemyRespawnRoomDistance;
        public float EnemyRespawnTimeInterval;
        public float EnemyRespawnCountMultiplier = 1f;
        public List<uint>? EnemyRespawnExcludeList = new();
        public float HealthMulti = 1f, WeaponAmmoMulti = 1f, ToolAmmoMulti = 1f, DisinfectionMulti = 1f;
    }
}
namespace ForgeDevelopment.Native
{
    internal static class RuntimeDiagnostics
    {
        internal static long WorldEpoch = 7;
        internal static long? SimulationTick = 12;
        internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        internal static string Vector(UnityEngine.Vector3 value) => "0,0,0";
        internal static string Rotation(UnityEngine.Quaternion value) => "0,0,0,1";
        internal static string PathOf(UnityEngine.Transform? value) => value == null ? "unavailable" : "same-display-path";
        internal static string Zone(LevelGeneration.LG_Zone? value) => value == null ? "unavailable" : $"{value.DimensionIndex}/{value.Layer?.m_type}/{value.LocalIndex}";
    }
}
