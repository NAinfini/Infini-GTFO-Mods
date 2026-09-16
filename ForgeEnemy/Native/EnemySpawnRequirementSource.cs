using System;
using System.Collections.Generic;
using AirNavigation;
using AssetShards;
using ForgeEnemy.Spawn;
using GameData;
using GTFO.API;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeEnemy.Native;

/// <summary>
/// Runtime data source for the spawn requirement table. It reads the loaded game data and hands typed
/// rows to <see cref="EnemySpawnRequirementCatalog.Build"/>, which owns the only derivation: the
/// offline evidence path in tests calls the same entry, so the two sources cannot drift apart.
///
/// Timepoint. The three DataBlock table reads happen after GTFO-API's <c>GameDataAPI.OnGameDataInitialized</c>
/// (raised by <c>GTFO.API.Patches.GameDataInit_Patches.Initialize_Postfix</c> after <c>GameData.Initialize</c>,
/// so custom EnemyDataBlocks injected by MTFO are already in the table). Base prefabs are asset-shard
/// objects, not DataBlocks, so they are read only once <c>AssetShardManager.EnemyAssetsIsLoaded</c> is true;
/// <c>GetLoadedAsset</c> cannot resolve a prefab before its shard is loaded. Whichever of the two
/// preconditions completes last is the build point. Both facts were read from the shipped assemblies:
/// GTFO-API 0.5.0 (<c>dev.gtfomodding.gtfo-api</c>) and Shards-ASM.
/// </summary>
internal static class EnemySpawnRequirementSource
{
    /// <summary>
    /// Unity reserves the first three NavMesh areas. The project's area table (NavMeshProjectSettings)
    /// is an editor-only object and no runtime API enumerates it, so the reserved names are looked up
    /// through the engine at runtime instead of being assumed by index. Areas defined beyond these
    /// three cannot be enumerated at runtime; the pinned build defines none
    /// (evidence/e2-spawn-space-20403457, hash-locked).
    /// </summary>
    private static readonly string[] ReservedAreaNames = { "Walkable", "Not Walkable", "Jump" };

    /// <summary>The shard manager's event takes the IL2CPP delegate; subscribe and unsubscribe with one instance.</summary>
    private static readonly Il2CppSystem.Action EnemyAssetsLoaded = (System.Action)OnEnemyAssetsLoaded;

    private static Action<string> _report = null!;
    private static bool _attached;

    /// <summary>
    /// The table derived from the running game, or null while the build point has not been reached or
    /// after a reported failure. Never a partial table.
    /// </summary>
    internal static EnemySpawnRequirementCatalog? Catalog { get; private set; }

    /// <summary>Last bounded diagnostic, so a failed report cannot hide the failure.</summary>
    internal static string? LastFailure { get; private set; }

    /// <summary>Type name of a throwing reporter, kept so a broken logger does not replace the primary failure.</summary>
    internal static string? LastReporterFailure { get; private set; }

    /// <summary>One-time subscription. Called from plugin Load, which is single-attempt.</summary>
    internal static void Attach(Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (_attached) throw new InvalidOperationException("The spawn requirement source attaches once.");
        _attached = true;
        _report = report;
        GameDataAPI.OnGameDataInitialized += OnGameDataInitialized;
    }

    private static void OnGameDataInitialized()
    {
        GameDataAPI.OnGameDataInitialized -= OnGameDataInitialized;
        if (AssetShardManager.EnemyAssetsIsLoaded) Build();
        // The interop exposes the shard manager's event as a property plus accessor methods.
        else AssetShardManager.add_OnEnemyAssetsLoaded(EnemyAssetsLoaded);
    }

    private static void OnEnemyAssetsLoaded() => Build();

    private static void Build()
    {
        AssetShardManager.remove_OnEnemyAssetsLoaded(EnemyAssetsLoaded);
        try
        {
            Catalog = EnemySpawnRequirementCatalog.Build(new EnemySpawnInputs(
                AgentTypes(), Areas(), Enemies(), MovementBlocks(), BalancingBlocks(), BasePrefabs()));
        }
        catch (Exception error)
        {
            // A native callback must not throw into the game; the failure stays visible as one bounded
            // diagnostic and Catalog stays null, so no caller can read a guessed requirement.
            Fail(error.GetType().Name + ": " + error.Message);
        }
    }

    // The runtime mirror of NavMeshProjectSettings.m_Settings: the same agentTypeID, agentRadius,
    // agentHeight, agentSlope and agentClimb the offline evidence extracts from globalgamemanagers.
    private static List<NavMeshAgentType> AgentTypes()
    {
        var types = new List<NavMeshAgentType>();
        int count = NavMesh.GetSettingsCount();
        for (int index = 0; index < count; index++)
        {
            var settings = NavMesh.GetSettingsByIndex(index);
            types.Add(new NavMeshAgentType(settings.agentTypeID, settings.agentRadius, settings.agentHeight,
                settings.agentSlope, settings.agentClimb));
        }
        return types;
    }

    private static List<NavMeshArea> Areas()
    {
        var areas = new List<NavMeshArea>();
        foreach (var name in ReservedAreaNames)
        {
            int index = NavMesh.GetAreaFromName(name);
            if (index >= 0) areas.Add(new NavMeshArea(index, name));
        }
        return areas;
    }

    private static List<EnemySpawnRow> Enemies()
    {
        var rows = new List<EnemySpawnRow>();
        foreach (var block in GameDataBlockBase<EnemyDataBlock>.GetAllBlocks())
        {
            if (block == null || !block.internalEnabled) continue;
            var paths = new List<string>();
            foreach (var path in block.BasePrefabs) paths.Add(path);
            var sizes = new List<EnemySizeRange>();
            foreach (var model in block.ModelDatas)
            {
                var range = model.SizeRange;
                sizes.Add(new EnemySizeRange(range.x, range.y));
            }
            var arenas = new List<uint>();
            foreach (var dimension in block.ArenaDimensions) arenas.Add(dimension);
            rows.Add(new EnemySpawnRow(block.persistentID, block.name, block.MovementDataId, block.BalancingDataId,
                paths, sizes, arenas));
        }
        return rows;
    }

    private static List<EnemyMovementRow> MovementBlocks()
    {
        var rows = new List<EnemyMovementRow>();
        foreach (var block in GameDataBlockBase<EnemyMovementDataBlock>.GetAllBlocks())
        {
            if (block == null || !block.internalEnabled) continue;
            rows.Add(new EnemyMovementRow(block.persistentID, (int)block.LocomotionPathMove, block.AllowClimbDownLadders));
        }
        return rows;
    }

    private static List<EnemyBalancingRow> BalancingBlocks()
    {
        var rows = new List<EnemyBalancingRow>();
        foreach (var block in GameDataBlockBase<EnemyBalancingDataBlock>.GetAllBlocks())
        {
            if (block == null || !block.internalEnabled) continue;
            rows.Add(new EnemyBalancingRow(block.persistentID, block.EnemyCollisionRadius, block.CanBePushed));
        }
        return rows;
    }

    // Every distinct base prefab any enabled enemy references, resolved at the build point. The offline
    // extractor reads the whole prefab hierarchy, so the runtime lookup walks children too.
    private static List<EnemyBasePrefabRow> BasePrefabs()
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var block in GameDataBlockBase<EnemyDataBlock>.GetAllBlocks())
        {
            if (block == null || !block.internalEnabled) continue;
            foreach (var path in block.BasePrefabs) paths.Add(path);
        }
        var rows = new List<EnemyBasePrefabRow>();
        foreach (var path in paths)
        {
            var asset = AssetShardManager.GetLoadedAsset<GameObject>(path, false);
            if (asset == null) throw new InvalidOperationException("Base prefab " + path + " is not loaded.");
            var agents = asset.GetComponentsInChildren<NavMeshAgent>(true);
            if (agents.Length > 1) throw new InvalidOperationException("Base prefab " + path + " has " + agents.Length + " NavMeshAgents.");
            EnemyGroundNavigation? navigation = null;
            if (agents.Length == 1)
            {
                var agent = agents[0];
                navigation = new EnemyGroundNavigation(agent.agentTypeID, agent.radius, agent.height,
                    unchecked((uint)agent.areaMask), agent.autoTraverseOffMeshLink);
            }
            rows.Add(new EnemyBasePrefabRow(path, navigation, asset.GetComponentsInChildren<FlyingAirGraphAgent>(true).Length > 0));
        }
        return rows;
    }

    private static void Fail(string detail)
    {
        LastFailure = detail.Length <= 2048 ? detail : detail[..2048];
        try { _report("Spawn requirements unavailable; custom enemies have no space contract: " + LastFailure); }
        catch (Exception reporter) { LastReporterFailure = reporter.GetType().Name; }
    }
}
