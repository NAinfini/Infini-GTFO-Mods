using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AIGraph;
using ChainedPuzzles;
using Enemies;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LevelGeneration;
using Player;
using SNetwork;
using TMPro;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ForgeDevelopment.Native;

/// <summary>How much of a snapshot a caller wants. The per-frame change pass and the full snapshot take the same
/// collector with different budgets; two collectors would drift apart.</summary>
internal sealed class CaptureOptions
{
    internal CaptureOptions(string reason, long tick)
    {
        Reason = reason;
        Tick = tick;
    }

    internal string Reason { get; }
    internal long Tick { get; }
    /// <summary>Rows one section may add. A section over its cap says so in the snapshot's notes.</summary>
    internal int MaxRowsPerSection { get; init; } = 4096;
    internal int MaxTexts { get; init; } = 512;
    internal bool IncludeText { get; init; } = true;
    internal bool IncludeEnvironment { get; init; } = true;
    internal bool IncludeNavMarkers { get; init; } = true;
}

/// <summary>
/// The world half of the capture: it reads the live level and answers rows. Every member read here exists in this
/// build's interop metadata. A member a future build renames reads as null rather than throwing, because a snapshot
/// that dies on a renamed property costs the session's evidence.
/// </summary>
internal static class CaptureWorld
{
    private const string Where = "ForgeDevelopment.Native.CaptureWorld";

    internal static CaptureSnapshot Collect(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Collect(options, "world");
    }

    /// <summary>The data block export, which reads the loaded block table rather than the scene.</summary>
    internal static CaptureSnapshot CollectDataBlocks(string reason, long tick, int maxPerType)
        => CaptureDataBlocks.Collect(reason, tick, maxPerType);

    internal static CaptureSnapshot Collect(CaptureOptions options, string scope)
    {
        var snapshot = new CaptureSnapshot(options.Reason, scope, options.Tick);
        var floor = CurrentFloor();
        var zones = floor?.allZones;
        var zoneOf = zones == null ? new Dictionary<int, LG_Zone>() : MapNodesToZones(zones);
        snapshot.Section(CaptureSections.Level, "GameStateManager + RundownManager + LG_LevelBuilder.Current.m_currentFloor")
            .Rows.Add(LevelRow(floor));
        if (zones == null || zones.Count == 0) snapshot.Notes.Add("no generated floor: zone-derived sections are empty");
        else FillZones(snapshot, options, zones);
        FillObjects(snapshot, options, floor, zoneOf);
        return snapshot;
    }

    private static LG_Floor? CurrentFloor()
    {
        try
        {
            var builder = LG_LevelBuilder.Current;
            if (builder == null || builder.WasCollected) return null;
            var floor = builder.m_currentFloor;
            return floor == null || floor.WasCollected ? null : floor;
        }
        catch (Exception error) { Log("floor", error); return null; }
    }

    private static CaptureRow LevelRow(LG_Floor? floor)
    {
        var row = new CaptureRow("level", "level/current", "current level");
        row.Field("gameState", Safe(() => GameStateManager.CurrentStateName.ToString()));
        row.Field("inExpedition", Safe(() => GameStateManager.IsInExpedition.ToString()));
        row.Field("inAllowedInfectionState", Safe(() => GameStateManager.IsInAllowedInfectionState.ToString()));
        row.Field("minutesInLevel", Safe(() => GameStateManager.MinutesInLevel.ToString()));
        row.Field("rundownKey", Safe(() => RundownManager.ActiveRundownKey));
        row.Field("expeditionKey", Safe(() => RundownManager.ActiveExpeditionUniqueKey));
        row.Field("expedition", Safe(() => DescribeExpedition(RundownManager.ActiveExpedition)));
        row.Field("expeditionStarted", Safe(() => RundownManager.ExpeditionIsStarted.ToString()));
        row.Field("sessionGUID", Safe(() => RundownManager.SessionGUID));
        row.Field("isMaster", Safe(() => SNet.IsMaster.ToString()));
        row.Field("localSlot", Safe(() => SNet.LocalPlayer == null ? null : SNet.LocalPlayer.PlayerSlotIndex().ToString(CultureInfo.InvariantCulture)));
        row.Field("checkpoint", Safe(() => RecReflect.Describe(RecReflect.ReadPath(CheckpointManager.Current, "m_stateReplicator.State"))));
        row.Field("floorId", Safe(() => floor == null ? null : floor.ID.ToString(CultureInfo.InvariantCulture)));
        row.Field("floorType", Safe(() => floor == null ? null : floor.m_floorType.ToString()));
        row.Field("isBuilt", Safe(() => floor == null ? null : floor.IsBuilt.ToString()));
        row.Field("zoneCount", Safe(() => floor?.allZones == null ? null : floor!.allZones.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("dimensions", Safe(() => floor?.m_dimensions == null ? null : floor!.m_dimensions.Count.ToString(CultureInfo.InvariantCulture)));
        return row;
    }

    private static void FillZones(CaptureSnapshot snapshot, CaptureOptions options, Il2CppSystem.Collections.Generic.List<LG_Zone> zones)
    {
        var section = snapshot.Section(CaptureSections.Zones, "LG_Floor.allZones");
        for (var i = 0; i != zones.Count; i++)
        {
            if (section.Rows.Count >= options.MaxRowsPerSection) { Note(snapshot, section, zones.Count); break; }
            var zone = zones[i];
            if (zone == null || zone.WasCollected) continue;
            var row = new CaptureRow("zone", "zone/" + Safe(() => zone.ID.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture)),
                Safe(() => zone.AliasName) ?? "");
            row.Field("localIndex", Safe(() => zone.LocalIndex.ToString()));
            row.Field("alias", Safe(() => zone.Alias.ToString(CultureInfo.InvariantCulture)));
            row.Field("layer", Safe(() => zone.Layer == null ? null : zone.Layer.m_type.ToString()));
            row.Field("dimension", Safe(() => zone.DimensionIndex.ToString()));
            row.Field("position", Safe(() => Vec(zone.Position)));
            row.Field("areas", Safe(() => zone.m_areas == null ? null : zone.m_areas.Count.ToString(CultureInfo.InvariantCulture)));
            row.Field("courseNodes", Safe(() => zone.m_courseNodes == null ? null : zone.m_courseNodes.Count.ToString(CultureInfo.InvariantCulture)));
            row.Field("terminals", Safe(() => zone.TerminalsSpawnedInZone == null ? null : zone.TerminalsSpawnedInZone.Count.ToString(CultureInfo.InvariantCulture)));
            row.Field("lights", Safe(() => zone.m_lightsInZone == null ? null : zone.m_lightsInZone.Length.ToString(CultureInfo.InvariantCulture)));
            row.Field("entranceGateType", Safe(() => zone.m_sourceGate == null ? "none" : zone.m_sourceGate.Type.ToString()));
            row.Field("entranceDoor", Safe(() => zone.m_sourceGate == null || zone.m_sourceGate.SpawnedDoor == null ? "none" : RecReflect.Describe(zone.m_sourceGate.SpawnedDoor)));
            row.Field("buildStatus", Safe(() => zone.m_buildStatus.ToString()));
            section.Rows.Add(row);
        }
    }

    private static void FillObjects(CaptureSnapshot snapshot, CaptureOptions options, LG_Floor? floor, Dictionary<int, LG_Zone> zoneOf)
    {
        // The level's own hierarchy is walked once and classified. A door, terminal or pickup the game disabled is
        // still a child of the zone it was built in, which is exactly the object a snapshot is asked about, while a
        // scene search without inactive objects never answers it.
        var level = LevelObjects(floor);
        Fill(snapshot, options, CaptureSections.Doors, "LG_Floor hierarchy: LG_SecurityDoor", level.Doors, zoneOf, DoorRow);
        Fill(snapshot, options, CaptureSections.Doors, "LG_Floor hierarchy: LG_WeakDoor", level.WeakDoors, zoneOf, WeakDoorRow);
        Fill(snapshot, options, CaptureSections.Terminals, "LG_Floor hierarchy: LG_ComputerTerminal", level.Terminals, zoneOf, TerminalRow);
        Fill(snapshot, options, CaptureSections.Generators, "LG_Floor hierarchy: LG_PowerGenerator_Core", level.Generators, zoneOf, GeneratorRow);
        Fill(snapshot, options, CaptureSections.Generators, "LG_Floor hierarchy: LG_PowerGeneratorCluster", level.Clusters, zoneOf, ClusterRow);
        Fill(snapshot, options, CaptureSections.Generators, "LG_Floor hierarchy: LG_HSUActivator_Core", level.HsuActivators, zoneOf, HsuActivatorRow);
        Fill(snapshot, options, CaptureSections.Containers, "LG_Floor hierarchy: LG_ResourceContainer_Storage", level.Containers, zoneOf, ContainerRow);
        Fill(snapshot, options, CaptureSections.Containers, "LG_Floor hierarchy: LG_WeakResourceContainer", level.WeakContainers, zoneOf, WeakContainerRow);
        Fill(snapshot, options, CaptureSections.Pickups, "LG_Floor hierarchy: ItemInLevel", level.Pickups, zoneOf, PickupRow);
        Fill(snapshot, options, CaptureSections.Objectives, "LG_Floor hierarchy: LG_LevelExitGeo", level.Exits, zoneOf, ExitRow);
        Fill(snapshot, options, CaptureSections.ChainedPuzzles, "LG_Floor hierarchy: ChainedPuzzleInstance", level.Puzzles, zoneOf, PuzzleRow);
        Fill(snapshot, options, CaptureSections.Players, "PlayerManager.PlayerAgentsInLevel", PlayerAgents(), zoneOf, PlayerRow);
        // Enemies and nav markers are spawned at run time instead of being built into the level, and this build's
        // interop answers no inactive-including scene search, so they are asked of the active scene.
        Fill(snapshot, options, CaptureSections.Enemies, "Object.FindObjectsOfType<EnemyAgent>", Objects<EnemyAgent>(), zoneOf, EnemyRow);
        if (options.IncludeEnvironment)
            Fill(snapshot, options, CaptureSections.Environment, "LG_Floor hierarchy: LG_Light", level.Lights, zoneOf, LightRow);
        if (options.IncludeNavMarkers)
            Fill(snapshot, options, CaptureSections.NavMarkers, "Object.FindObjectsOfType<NavMarker>", Objects<NavMarker>(), zoneOf, NavMarkerRow);
        if (options.IncludeText) FillHudText(snapshot, options);
    }

    /// <summary>One component of each family the level owns, filled by a single walk of the floor hierarchy. The
    /// families are disjoint: a component lands in the first list whose type it is, which is why the walk tests them in
    /// one order rather than once per family.</summary>
    private sealed class LevelObjectSet
    {
        internal List<LG_SecurityDoor> Doors { get; } = new();
        internal List<LG_WeakDoor> WeakDoors { get; } = new();
        internal List<LG_ComputerTerminal> Terminals { get; } = new();
        internal List<LG_PowerGenerator_Core> Generators { get; } = new();
        internal List<LG_PowerGeneratorCluster> Clusters { get; } = new();
        internal List<LG_HSUActivator_Core> HsuActivators { get; } = new();
        internal List<LG_ResourceContainer_Storage> Containers { get; } = new();
        internal List<LG_WeakResourceContainer> WeakContainers { get; } = new();
        internal List<ItemInLevel> Pickups { get; } = new();
        internal List<LG_LevelExitGeo> Exits { get; } = new();
        internal List<ChainedPuzzleInstance> Puzzles { get; } = new();
        internal List<LG_Light> Lights { get; } = new();
    }

    private static LevelObjectSet LevelObjects(LG_Floor? floor)
    {
        var level = new LevelObjectSet();
        if (floor == null || floor.WasCollected) return level;
        try
        {
            foreach (var component in WorldInspection.FloorComponents(floor.transform))
            {
                if (component == null || component.WasCollected) continue;
                if (Add(level.Doors, component.TryCast<LG_SecurityDoor>())) continue;
                if (Add(level.WeakDoors, component.TryCast<LG_WeakDoor>())) continue;
                if (Add(level.Terminals, component.TryCast<LG_ComputerTerminal>())) continue;
                if (Add(level.Generators, component.TryCast<LG_PowerGenerator_Core>())) continue;
                if (Add(level.Clusters, component.TryCast<LG_PowerGeneratorCluster>())) continue;
                if (Add(level.HsuActivators, component.TryCast<LG_HSUActivator_Core>())) continue;
                if (Add(level.Containers, component.TryCast<LG_ResourceContainer_Storage>())) continue;
                if (Add(level.WeakContainers, component.TryCast<LG_WeakResourceContainer>())) continue;
                if (Add(level.Pickups, component.TryCast<ItemInLevel>())) continue;
                if (Add(level.Exits, component.TryCast<LG_LevelExitGeo>())) continue;
                if (Add(level.Puzzles, component.TryCast<ChainedPuzzleInstance>())) continue;
                Add(level.Lights, component.TryCast<LG_Light>());
            }
        }
        catch (Exception error) { Log("floor_walk", error); }
        return level;
    }

    private static bool Add<T>(List<T> list, T? value) where T : UnityObject
    {
        if (value == null || value.WasCollected) return false;
        list.Add(value);
        return true;
    }

    private static void Fill<T>(CaptureSnapshot snapshot, CaptureOptions options, string sectionName, string source,
        IReadOnlyList<T>? objects, Dictionary<int, LG_Zone> zoneOf, Func<T, LG_Zone?, CaptureRow> row) where T : UnityObject
    {
        var section = snapshot.Section(sectionName, source);
        if (objects == null) return;
        for (var i = 0; i != objects.Count; i++)
        {
            if (section.Rows.Count >= options.MaxRowsPerSection) { Note(snapshot, section, objects.Count); break; }
            var item = objects[i];
            if (item == null || item.WasCollected) continue;
            var zone = ZoneOf(item, zoneOf);
            var built = row(item, zone);
            if (zone != null && built.Zone == null) built.Zone = zone.AliasName;
            built.Field("active", Active(item));
            section.Rows.Add(built);
        }
    }

    /// <summary>Whether an object was switched on when its row was written. Recording an inactive door or terminal is
    /// only useful if the row says it was inactive, so every object row carries this one field.</summary>
    private static string Active(UnityObject value)
        => Safe(() => value is Component component ? component.gameObject.activeInHierarchy.ToString() : "n/a") ?? "unreadable";

    private static CaptureRow DoorRow(LG_SecurityDoor door, LG_Zone? zone)
    {
        var row = new CaptureRow("door", "door/security/" + Safe(() => door.m_serialNumber.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(() => RecReflect.Describe(door)) ?? "?");
        row.Field("doorType", Safe(() => door.DoorType.ToString()));
        row.Field("status", Safe(() => door.LastStatus.ToString()));
        row.Field("lastState", Safe(() => door.m_lastState.status.ToString()));
        row.Field("interactionAllowed", Safe(() => door.InteractionAllowed.ToString()));
        row.Field("inAnimation", Safe(() => door.InAnimation.ToString()));
        row.Field("requiresKey", Safe(() => door.RequiresKey().ToString()));
        row.Field("keyItem", Safe(() => door.m_keyItem == null ? "none" : door.m_keyItem.PublicName));
        row.Field("overrideCode", Safe(() => door.m_overrideCode));
        row.Field("chainedPuzzle", Safe(() => door.m_checkpointPuzzle == null ? "none" : RecReflect.Describe(door.m_checkpointPuzzle)));
        row.Field("linkedTerminal", Safe(() => door.LinkedComputerTerminal == null ? "none" : door.LinkedComputerTerminal.PublicName));
        row.Field("linksToLayer", Safe(() => door.LinksToLayerType.ToString()));
        row.Field("linkedZone", Safe(() => RecReflect.Describe(door.LinkedToZoneData)));
        row.Field("alarmWave", Safe(() => door.ActiveEnemyWaveData == null ? "none" : RecReflect.Describe(door.ActiveEnemyWaveData)));
        row.Field("checkpointDoor", Safe(() => door.IsCheckpointDoor.ToString()));
        row.Field("openedDuringPlay", Safe(() => door.Gate == null ? null : door.Gate.HasBeenOpenedDuringPlay.ToString()));
        row.Field("position", Safe(() => Vec(door.transform.position)));
        row.Field("glueVolume", Safe(() => RecReflect.ReadPath(door.m_sync, "m_attachedGlueVolume")?.ToString()));
        return row;
    }

    private static CaptureRow WeakDoorRow(LG_WeakDoor door, LG_Zone? zone)
    {
        var row = new CaptureRow("door", "door/weak/" + Safe(() => door.m_serialNumber.ToString(CultureInfo.InvariantCulture), "?"), "weak door");
        row.Field("doorType", Safe(() => door.DoorType.ToString()));
        row.Field("status", Safe(() => door.LastStatus.ToString()));
        row.Field("requiresKey", Safe(() => door.RequiresKey().ToString()));
        row.Field("interactionAllowed", Safe(() => door.InteractionAllowed.ToString()));
        row.Field("openedDuringPlay", Safe(() => door.Gate == null ? null : door.Gate.HasBeenOpenedDuringPlay.ToString()));
        row.Field("glueVolume", Safe(() => RecReflect.ReadPath(door.m_sync, "m_attachedGlueVolume")?.ToString()));
        row.Field("destroyed", Safe(() => RecReflect.ReadPath(door.m_destruction, "m_destroyed")?.ToString()));
        row.Field("position", Safe(() => Vec(door.transform.position)));
        return row;
    }

    private static CaptureRow TerminalRow(LG_ComputerTerminal terminal, LG_Zone? zone)
    {
        var row = new CaptureRow("terminal", "terminal/" + Safe(() => terminal.SyncID.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(() => terminal.PublicName) ?? "terminal");
        row.Field("state", Safe(() => terminal.CurrentStateName.ToString()));
        row.Field("key", Safe(() => terminal.ItemKey));
        row.Field("passwordProtected", Safe(() => terminal.IsPasswordProtected.ToString()));
        row.Field("hasPasswordPart", Safe(() => terminal.HasPasswordPart.ToString()));
        row.Field("heldPassword", Safe(() => terminal.HeldPasswordPart));
        row.Field("woType", Safe(() => terminal.WardenObjectiveType.ToString()));
        row.Field("puzzleSolved", Safe(() => terminal.PuzzleSolved.ToString()));
        row.Field("activeObjectiveItem", Safe(() => terminal.IsActiveWardenObjectiveItem.ToString()));
        row.Field("chainPuzzle", Safe(() => terminal.ChainedPuzzleForWardenObjective == null ? "none" : RecReflect.Describe(terminal.ChainedPuzzleForWardenObjective)));
        row.Field("gatherCommand", Safe(() => terminal.GatherTerminalCommand));
        row.Field("customLine", Safe(() => terminal.CustomLineText));
        row.Field("showInputLine", Safe(() => terminal.ShowInputLine.ToString()));
        row.Field("showCustomLine", Safe(() => terminal.ShowCustomLine.ToString()));
        row.Field("logVisible", Safe(() => terminal.IsLogVisible(terminal.ItemKey).ToString()));
        row.Field("position", Safe(() => Vec(terminal.transform.position)));
        return row;
    }

    private static CaptureRow GeneratorRow(LG_PowerGenerator_Core generator, LG_Zone? zone)
    {
        var row = new CaptureRow("generator", "generator/" + Safe(() => generator.m_serialNumber.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(() => generator.PublicName) ?? "generator");
        row.Field("status", Safe(() => RecReflect.ReadPath(generator.m_stateReplicator, "State.status")?.ToString()));
        row.Field("changedBy", Safe(() => RecReflect.ReadPath(generator.m_stateReplicator, "State.playerThatChangedTheState")?.ToString()));
        row.Field("key", Safe(() => generator.m_itemKey));
        row.Field("wardenObjective", Safe(() => generator.m_isWardenObjective.ToString()));
        row.Field("objectiveSolved", Safe(() => generator.ObjectiveItemSolved.ToString()));
        row.Field("chainIndex", Safe(() => generator.WardenObjectiveChainIndex.ToString(CultureInfo.InvariantCulture)));
        row.Field("linkedDoor", Safe(() => generator.LinkedSecurityDoor == null ? "none" : RecReflect.Describe(generator.LinkedSecurityDoor)));
        row.Field("position", Safe(() => Vec(generator.transform.position)));
        return row;
    }

    private static CaptureRow ClusterRow(LG_PowerGeneratorCluster cluster, LG_Zone? zone)
    {
        var row = new CaptureRow("generatorCluster", "generatorCluster/" + Safe(() => cluster.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "generator cluster");
        row.Field("generators", Safe(() => cluster.m_generators == null ? null : cluster.m_generators.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("chainedPuzzle", Safe(() => cluster.m_chainedPuzzleMidObjective == null ? "none" : RecReflect.Describe(cluster.m_chainedPuzzleMidObjective)));
        row.Field("position", Safe(() => Vec(cluster.transform.position)));
        return row;
    }

    private static CaptureRow HsuActivatorRow(LG_HSUActivator_Core activator, LG_Zone? zone)
    {
        var row = new CaptureRow("hsuActivator", "hsuActivator/" + Safe(() => activator.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "HSU activator");
        row.Field("status", Safe(() => RecReflect.ReadPath(activator.m_stateReplicator, "State.status")?.ToString()));
        row.Field("position", Safe(() => Vec(activator.transform.position)));
        return row;
    }

    private static CaptureRow ContainerRow(LG_ResourceContainer_Storage container, LG_Zone? zone)
    {
        var row = new CaptureRow("container", "container/" + Safe(() => container.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "resource container");
        row.Field("position", Safe(() => Vec(container.transform.position)));
        row.Field("items", Safe(() => RecReflect.ReadPath(container, "m_items")?.ToString()));
        return row;
    }

    private static CaptureRow WeakContainerRow(LG_WeakResourceContainer container, LG_Zone? zone)
    {
        var row = new CaptureRow("container", "container/weak/" + Safe(() => container.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "weak resource container");
        row.Field("locked", Safe(() => container.IsLocked().ToString()));
        row.Field("position", Safe(() => Vec(container.transform.position)));
        return row;
    }

    private static CaptureRow PickupRow(ItemInLevel item, LG_Zone? zone)
    {
        var row = new CaptureRow("pickup", "pickup/" + Safe(() => item.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), item.GetType().Name);
        row.Field("publicName", Safe(() => RecReflect.ReadPath(item, "PublicName") as string));
        row.Field("status", Safe(() => RecReflect.ReadPath(item, "PickupItemStatus")?.ToString()));
        row.Field("solved", Safe(() => RecReflect.ReadPath(item, "ObjectiveItemSolved")?.ToString()));
        row.Field("chainIndex", Safe(() => RecReflect.ReadPath(item, "WardenObjectiveChainIndex")?.ToString()));
        row.Field("pickedUpBy", Safe(() => RecReflect.Describe(RecReflect.ReadPath(item, "PickedUpByPlayer"))));
        row.Field("key", Safe(() => RecReflect.ReadPath(item, "ItemKey") as string));
        row.Field("container", Safe(() => item.container == null ? "none" : RecReflect.Describe(item.container)));
        row.Field("courseNode", Safe(() => item.CourseNode == null ? null : item.CourseNode.NodeID.ToString(CultureInfo.InvariantCulture)));
        row.Field("position", Safe(() => Vec(item.transform.position)));
        return row;
    }

    private static CaptureRow ExitRow(LG_LevelExitGeo exit, LG_Zone? zone)
    {
        var row = new CaptureRow("exit", "exit/" + Safe(() => exit.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "level exit");
        row.Field("position", Safe(() => Vec(exit.transform.position)));
        return row;
    }

    private static CaptureRow PuzzleRow(ChainedPuzzleInstance puzzle, LG_Zone? zone)
    {
        var row = new CaptureRow("chainedPuzzle", "chainedPuzzle/" + Safe(() => puzzle.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "chained puzzle");
        row.Field("puzzles", Safe(() => puzzle.NRofPuzzles().ToString(CultureInfo.InvariantCulture)));
        row.Field("state", Safe(() => RecReflect.Describe(RecReflect.ReadPath(puzzle, "m_stateReplicator.State"))));
        row.Field("survivalWaveId", Safe(() => puzzle.GetSetSurvivalWaveID().ToString(CultureInfo.InvariantCulture)));
        row.Field("position", Safe(() => Vec(puzzle.transform.position)));
        return row;
    }

    private static CaptureRow PlayerRow(PlayerAgent player, LG_Zone? zone)
    {
        var row = new CaptureRow("player", "player/" + Safe(() => player.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(() => player.PlayerName) ?? "player");
        row.Field("slot", Safe(() => player.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture)));
        row.Field("local", Safe(() => player.IsLocallyOwned.ToString()));
        row.Field("alive", Safe(() => player.Alive.ToString()));
        row.Field("health", Safe(() => player.Damage == null ? null : player.Damage.Health.ToString(CultureInfo.InvariantCulture)));
        row.Field("healthMax", Safe(() => player.Damage == null ? null : player.Damage.HealthMax.ToString(CultureInfo.InvariantCulture)));
        row.Field("infection", Safe(() => player.Damage == null ? null : player.Damage.Infection.ToString(CultureInfo.InvariantCulture)));
        row.Field("air", Safe(() => player.Damage == null ? null : player.Damage.Air.ToString(CultureInfo.InvariantCulture)));
        row.Field("grabbed", Safe(() => player.Damage == null ? null : player.Damage.GrabbedAtAll.ToString()));
        row.Field("cannotDie", Safe(() => player.Damage == null ? null : player.Damage.CannotDie.ToString()));
        row.Field("ignoreAllDamage", Safe(() => player.Damage == null ? null : player.Damage.IgnoreAllDamage.ToString()));
        row.Field("position", Safe(() => Vec(player.Position)));
        row.Field("dimension", Safe(() => player.DimensionIndex.ToString()));
        row.Field("gameObjectId", Safe(() => player.GameObjectID.ToString(CultureInfo.InvariantCulture)));
        row.Field("courseNode", Safe(() => player.CourseNode == null ? null : player.CourseNode.NodeID.ToString(CultureInfo.InvariantCulture)));
        row.Field("wielded", Safe(() => player.Inventory == null || player.Inventory.WieldedItem == null ? "none" : RecReflect.Describe(player.Inventory.WieldedItem)));
        row.Field("wieldedSlot", Safe(() => player.Inventory == null ? null : player.Inventory.WieldedSlot.ToString()));
        row.Field("flashlight", Safe(() => player.Inventory == null ? null : player.Inventory.FlashlightEnabled.ToString()));
        row.Field("backpack", Safe(() => BackpackOf(player)));
        row.Field("syncModel", Safe(() => RecReflect.Describe(player.PlayerSyncModel)));
        row.Field("playerSync", Safe(() => RecReflect.Describe(player.Sync)));
        return row;
    }

    /// <summary>The backpack is reached through the manager, not the agent: the agent has no backpack member, and the
    /// manager's lookup is the game's own answer for a player's inventory.</summary>
    private static string BackpackOf(PlayerAgent player)
    {
        if (player.Owner == null) return "none";
        if (!PlayerBackpackManager.TryGetBackpack(player.Owner, out var backpack) || backpack == null) return "none";
        var storage = backpack.AmmoStorage;
        var ammo = storage == null ? "none" : RecReflect.Describe(storage);
        return RecReflect.Describe(backpack) + " ammo=" + ammo;
    }

    private static CaptureRow EnemyRow(EnemyAgent enemy, LG_Zone? zone)
    {
        var row = new CaptureRow("enemy", "enemy/" + Safe(() => enemy.GlobalID.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(() => enemy.EnemyData == null ? null : enemy.EnemyData.name) ?? "enemy");
        row.Field("typeId", Safe(() => enemy.EnemyDataID.ToString(CultureInfo.InvariantCulture)));
        row.Field("alive", Safe(() => enemy.Alive.ToString()));
        row.Field("health", Safe(() => enemy.Damage == null ? null : enemy.Damage.Health.ToString(CultureInfo.InvariantCulture)));
        row.Field("healthMax", Safe(() => enemy.Damage == null ? null : enemy.Damage.HealthMax.ToString(CultureInfo.InvariantCulture)));
        row.Field("tagged", Safe(() => enemy.IsTagged.ToString()));
        row.Field("wasTagged", Safe(() => enemy.WasTagged.ToString()));
        row.Field("tagMarker", Safe(() => enemy.m_tagMarker == null ? "none" : RecReflect.Describe(enemy.m_tagMarker)));
        row.Field("tagTimer", Safe(() => enemy.EnemyTaggedTimer.ToString(CultureInfo.InvariantCulture)));
        row.Field("scannerColor", Safe(() => Color(enemy.m_scannerColor)));
        row.Field("behaviourState", Safe(() => RecReflect.ReadPath(enemy.AI, "m_behaviour.m_currentStateName")?.ToString()));
        row.Field("hibernating", Safe(() => Hibernating(enemy.AI).ToString()));
        row.Field("target", Safe(() => RecReflect.Describe(RecReflect.ReadPath(enemy.AI, "m_lastAgentDetected"))));
        row.Field("navGoal", Safe(() => RecReflect.Describe(RecReflect.ReadPath(enemy.AI, "NavmeshAgentGoal"))));
        row.Field("position", Safe(() => Vec(enemy.Position)));
        row.Field("courseNode", Safe(() => enemy.CourseNode == null ? null : enemy.CourseNode.NodeID.ToString(CultureInfo.InvariantCulture)));
        row.Field("sizeMultiplier", Safe(() => enemy.SizeMultiplier.ToString(CultureInfo.InvariantCulture)));
        row.Field("limbs", Safe(() => enemy.Damage == null || enemy.Damage.DamageLimbs == null ? null : enemy.Damage.DamageLimbs.Length.ToString(CultureInfo.InvariantCulture)));
        row.Field("glue", Safe(() => enemy.Damage == null ? null : enemy.Damage.AttachedGlueVolume.ToString(CultureInfo.InvariantCulture)));
        row.Field("onFire", Safe(() => enemy.Damage == null ? null : enemy.Damage.OnFire.ToString()));
        row.Field("heatLevel", Safe(() => enemy.Damage == null ? null : enemy.Damage.m_heatLevel.ToString(CultureInfo.InvariantCulture)));
        return row;
    }

    private static CaptureRow LightRow(LG_Light light, LG_Zone? zone)
    {
        var row = new CaptureRow("light", "light/" + Safe(() => light.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), light.GetType().Name);
        row.Field("intensity", Safe(() => light.GetIntensity().ToString(CultureInfo.InvariantCulture)));
        row.Field("color", Safe(() => RecReflect.Describe(RecReflect.ReadPath(light, "m_color"))));
        row.Field("position", Safe(() => Vec(light.GetPosition())));
        return row;
    }

    private static CaptureRow NavMarkerRow(NavMarker marker, LG_Zone? zone)
    {
        var row = new CaptureRow("navMarker", "navMarker/" + Safe(() => marker.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "nav marker");
        row.Field("tracked", Safe(() => RecReflect.Describe(marker.m_trackingObj)));
        row.Field("visible", Safe(() => marker.IsVisible.ToString()));
        row.Field("trackingDimension", Safe(() => RecReflect.Describe(marker.TrackingDimension)));
                row.Field("style", Safe(() => RecReflect.ReadPath(marker, "m_style")?.ToString()));
        row.Field("signInfo", Safe(() => RecReflect.ReadPath(marker, "m_signInfo")?.ToString()));
        row.Field("title", Safe(() => RecReflect.ReadPath(marker, "m_title.m_titleText")?.ToString()));
        row.Field("titleText", Safe(() => RecReflect.ReadPath(marker, "m_title.m_text")?.ToString()));
        row.Field("playerName", Safe(() => RecReflect.ReadPath(marker, "m_playerName.m_text")?.ToString()));
        row.Field("distance", Safe(() => RecReflect.ReadPath(marker, "m_distance.m_text")?.ToString()));
        row.Field("position", Safe(() => Vec(marker.transform.position)));
        return row;
    }

    /// <summary>The GUI texts. Every non-empty TMP text the active scene answers is recorded with its path, final
    /// text, colour and position, because a HUD number or a broadcast line is only verifiable through the text the
    /// game ended up showing, not through the field some code wrote. Hidden states are covered by the `enabled` flag
    /// next to `active`, which is the pair a reader needs to tell a text that was switched off from one never drawn.
    /// </summary>
    private static void FillHudText(CaptureSnapshot snapshot, CaptureOptions options)
    {
        var section = snapshot.Section(CaptureSections.HudText, "Object.FindObjectsOfType<TMP_Text>");
        var texts = Objects<TMP_Text>();
        if (texts == null) return;
        for (var i = 0; i != texts.Length; i++)
        {
            if (section.Rows.Count >= options.MaxTexts) { Note(snapshot, section, texts.Length); break; }
            var text = texts[i];
            if (text == null || text.WasCollected) continue;
            var content = Safe(() => text.text);
            if (string.IsNullOrEmpty(content)) continue;
            var row = new CaptureRow("hudText", "hudText/" + Path(text.transform), Safe(() => text.name) ?? "text");
            row.Field("text", content);
            row.Field("color", Safe(() => Color(text.color)));
            row.Field("enabled", Safe(() => text.enabled.ToString()));
            row.Field("position", Safe(() => Vec(text.transform.position)));
            row.Field("scale", Safe(() => Vec(text.transform.lossyScale)));
            row.Field("active", Active(text));
            section.Rows.Add(row);
        }
    }

    private static IReadOnlyList<PlayerAgent>? PlayerAgents()
    {
        try
        {
            var agents = PlayerManager.PlayerAgentsInLevel;
            if (agents == null) return null;
            var list = new List<PlayerAgent>(agents.Count);
            for (var i = 0; i != agents.Count; i++)
            {
                var agent = agents[i];
                if (agent != null) list.Add(agent);
            }
            return list;
        }
        catch (Exception error) { Log("players", error); return null; }
    }

    private static Dictionary<int, LG_Zone> MapNodesToZones(Il2CppSystem.Collections.Generic.List<LG_Zone> zones)
    {
        var map = new Dictionary<int, LG_Zone>();
        for (var i = 0; i != zones.Count; i++)
        {
            var zone = zones[i];
            if (zone == null || zone.WasCollected) continue;
            var nodes = zone.m_courseNodes;
            if (nodes == null) continue;
            for (var n = 0; n != nodes.Count; n++)
            {
                var node = nodes[n];
                if (node == null || node.WasCollected) continue;
                map[node.NodeID] = zone;
            }
        }
        return map;
    }

    private static LG_Zone? ZoneOf(UnityObject item, Dictionary<int, LG_Zone> zoneOf)
    {
        if (item is EnemyAgent enemy) return ZoneOfNode(enemy.CourseNode, zoneOf);
        // Every object this walks is a component; the cast keeps the walk off System.Object, whose alias the file
        // imports for the generic constraint above.
        var transform = item is Component component ? component.transform : null;
        while (transform != null)
        {
            var zone = transform.GetComponent<LG_Zone>();
            if (zone != null && !zone.WasCollected) return zone;
            transform = transform.parent;
        }
        return null;
    }

    private static LG_Zone? ZoneOfNode(AIG_CourseNode? node, Dictionary<int, LG_Zone> zoneOf)
        => node != null && zoneOf.TryGetValue(node.NodeID, out var owner) ? owner : null;

    /// <summary>Every object of one family in the loaded scene. This build's interop exposes no `includeInactive`
    /// overload of `FindObjectsOfType`, so a family reached this way is the active one; the level's own objects are
    /// walked through its hierarchy instead, which is where a disabled terminal or door still exists.</summary>
    private static T[]? Objects<T>() where T : UnityObject
    {
        try { return UnityObject.FindObjectsOfType<T>(); }
        catch (Exception error) { Log("find:" + typeof(T).Name, error); return null; }
    }

    private static string Path(Transform transform)
    {
        var text = new StringBuilder(64);
        while (transform != null)
        {
            text.Insert(0, "/" + transform.name);
            transform = transform.parent;
        }
        return text.ToString();
    }

    private static void Note(CaptureSnapshot snapshot, CaptureSection section, int available)
        => snapshot.Notes.Add(section.Name + ": " + available + " objects, capped at " + section.Rows.Count);

    private static string DescribeExpedition(GameData.ExpeditionInTierData? expedition)
    {
        if (expedition == null) return "none";
        var index = RecReflect.ReadPath(expedition, "Expedition.ExpeditionIndex");
        var tier = RecReflect.ReadPath(expedition, "Expedition.Tier");
        var name = RecReflect.ReadPath(expedition, "Descriptive.PublicName");
        return Convert.ToString(index, CultureInfo.InvariantCulture) + "/" + Convert.ToString(tier, CultureInfo.InvariantCulture)
            + " " + Convert.ToString(name, CultureInfo.InvariantCulture);
    }

    private static string Vec(Vector3 value)
        => value.x.ToString("0.###", CultureInfo.InvariantCulture) + "," + value.y.ToString("0.###", CultureInfo.InvariantCulture)
            + "," + value.z.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Color(Color value)
        => value.r.ToString("0.###", CultureInfo.InvariantCulture) + "," + value.g.ToString("0.###", CultureInfo.InvariantCulture)
            + "," + value.b.ToString("0.###", CultureInfo.InvariantCulture) + "," + value.a.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool Hibernating(EnemyAI? ai)
    {
        if (ai == null) return false;
        var soft = false;
        var hard = false;
        try { return ai.IsHibernating(out soft, out hard); }
        catch (Exception) { return false; }
    }

    private static string? Safe(Func<string?> read)
    {
        try { return read(); }
        catch (Exception error) { Log("read", error); return null; }
    }

    /// <summary>A read that answers a fallback when it cannot read at all. The identity of an object is the one field
    /// a snapshot must have, so an unreadable id falls back to the walk index rather than leaving the row keyless.</summary>
    private static string Safe(Func<string?> read, string fallback) => Safe(read) ?? fallback;

    private static void Log(string what, Exception error)
    {
        try
        {
            RecSession.Write("log", "capture_error", json =>
            {
                json.WriteString("where", Where + ":" + what);
                json.WriteString("error", error.GetType().Name);
                json.WriteString("message", RecReflect.Truncate(error.Message, 200));
            });
        }
        catch (Exception) { /* A recorder that cannot report its own failure must not take the frame with it. */ }
    }
}



