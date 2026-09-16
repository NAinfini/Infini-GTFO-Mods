// Test doubles for the runtime spawn requirement source (Native/EnemySpawnRequirementSource.cs).
// Member kinds follow build 20403457 (interop metadata); behaviour is synthetic and NOT game-verified.
// Only the members the source reads are declared.
namespace UnityEngine
{
    public class Object { }
    public class MonoBehaviour : Object { }

    /// <summary>Component container keyed by type; the source walks children, so this double is flat.</summary>
    public sealed class GameObject : Object
    {
        private readonly Dictionary<Type, List<Object>> components = new();
        public T Add<T>(T component) where T : Object
        {
            if (!components.TryGetValue(typeof(T), out var found)) components.Add(typeof(T), found = new List<Object>());
            found.Add(component);
            return component;
        }
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Object =>
            components.TryGetValue(typeof(T), out var found) ? found.Cast<T>().ToArray() : Array.Empty<T>();
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }
}
namespace UnityEngine.AI
{
    public sealed class NavMeshAgent : UnityEngine.Object
    {
        public int agentTypeID;
        public float radius, height;
        public int areaMask;
        public bool autoTraverseOffMeshLink;
    }
    public struct NavMeshBuildSettings
    {
        public int agentTypeID;
        public float agentRadius, agentHeight, agentSlope, agentClimb;
    }
    public static class NavMesh
    {
        public static readonly List<NavMeshBuildSettings> Settings = new();
        public static readonly Dictionary<string, int> AreaIndices = new();
        public static int GetSettingsCount() => Settings.Count;
        public static NavMeshBuildSettings GetSettingsByIndex(int index) => Settings[index];
        public static int GetAreaFromName(string areaName) => AreaIndices.TryGetValue(areaName, out var index) ? index : -1;
    }
}
namespace AirNavigation
{
    public sealed class FlyingAirGraphAgent : UnityEngine.MonoBehaviour { }
}
namespace GTFO.API
{
    public static class GameDataAPI
    {
        public static event Action? OnGameDataInitialized;
        public static void RaiseGameDataInitialized() => OnGameDataInitialized?.Invoke();
    }
}
namespace GameData
{
    // The block table and the block's interop identity live in GameDoubles.cs, with the agent that carries the
    // block; this half adds the data members the spawn requirement source reads.
    public sealed partial class EnemyDataBlock
    {
        public List<string> BasePrefabs = new();
        public List<ModelData> ModelDatas = new();
        public uint MovementDataId, BalancingDataId;
        public List<uint> ArenaDimensions = new();
    }
    public sealed class ModelData { public UnityEngine.Vector2 SizeRange; }
    public sealed class EnemyMovementDataBlock : GameDataBlockBase<EnemyMovementDataBlock>
    {
        public Enemies.ES_StateEnum LocomotionPathMove;
        public bool AllowClimbDownLadders;
    }
    public sealed class EnemyBalancingDataBlock : GameDataBlockBase<EnemyBalancingDataBlock>
    {
        public float EnemyCollisionRadius;
        public bool CanBePushed;
    }
}
namespace Il2CppSystem
{
    /// <summary>Il2CppInterop exposes IL2CPP delegate types as classes with managed conversion and combination.</summary>
    public sealed class Action
    {
        private readonly System.Action? handler;
        private Action(System.Action? handler) { this.handler = handler; }
        public static implicit operator Action(System.Action handler) => new(handler);
        public static Action operator +(Action? left, Action? right) => new(Delegate.Combine(left?.handler, right?.handler) as System.Action);
        public static Action operator -(Action? left, Action? right) => new(Delegate.Remove(left?.handler, right?.handler) as System.Action);
        public void Invoke() => handler?.Invoke();
    }
}
namespace AssetShards
{
    public static class AssetShardManager
    {
        public static bool EnemyAssetsIsLoaded;
        // The interop surface: a property plus accessor methods taking the IL2CPP delegate.
        public static Il2CppSystem.Action? OnEnemyAssetsLoaded { get; set; }
        public static void add_OnEnemyAssetsLoaded(Il2CppSystem.Action value) => OnEnemyAssetsLoaded += value;
        public static void remove_OnEnemyAssetsLoaded(Il2CppSystem.Action value) => OnEnemyAssetsLoaded -= value;
        public static readonly Dictionary<string, UnityEngine.Object> Assets = new();
        public static T? GetLoadedAsset<T>(string assetPath, bool forceAssetDatabase) where T : UnityEngine.Object =>
            Assets.TryGetValue(assetPath, out var asset) ? (T)asset : null;
        public static void RaiseEnemyAssetsLoaded()
        {
            EnemyAssetsIsLoaded = true;
            OnEnemyAssetsLoaded?.Invoke();
        }
    }
}
