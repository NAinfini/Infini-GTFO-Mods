using System;
using System.Collections.Generic;
using System.Globalization;
using GameData;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LevelGeneration;
using Player;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>The game-bound half of the level-event rows: every native member each row reads, and the one native
/// entry each of the three actions reaches. Nothing here decides whether something is news — the reports are
/// handed to <see cref="LevelEventModule"/>, which owns the identity of a fact and its transition.
///
/// Every member below is read from the build's own interop assemblies (20403457):
///
/// - `WardenObjectiveManager.CurrentState` / `GetLayerStatus` / `GetCurrentChainIndex` for the two objective
///   transitions and the reactor's chain step; `WardenObjectiveManager.OnLocalPlayerStartExpedition` for the
///   landing. `pWardenObjectiveState` keeps three fixed layer slots (`main_status` 0 / `second_status` 1 /
///   `third_status` 2), which is why a row about one layer carries the layer name and not an instance id.
/// - `IWardenObjective.OnStatusChange` is the objective's own status callback; `WO_HSUFindTakeSample` overrides
///   `OnLocalPlayerSolvedObjectiveItem` for the HSU's own "the sample is in" transition.
/// - `CheckpointManager.OnRecallComplete` is the end of a recall, which is the row's "read a checkpoint back".
/// - `WardenObjectiveManager.OnLocalPlayerEnterZone(PlayerAgent, LG_Zone)` is the game's own zone-entry entry;
///   `LG_Zone` carries `m_dimensionIndex` and `m_layer`, and `ZoneIndex` already resolves the world's zone table
///   to the three-layer address every level-scope row publishes.
/// - `LG_DimensionPortal.m_serialNumber` / `PortalChainPuzzle` name a portal, and its state change is the same
///   transition the vanilla `EventsOnPortalWarp` list hangs off.
/// - `WorldEventManager.ExecuteEvent(WardenObjectiveEventData)` is the game's own level-event executor, which is
///   the one native entry the three actions use: the event struct's type member selects the effect and its own
///   replicated fields carry it to every client.
///
/// Nothing in this file writes the state an event owns. An action builds the struct and hands it over, so the
/// game's own replication path stays the only writer, exactly as a vanilla mount point does it.</summary>
internal static class LevelEventObservation
{
    // ---- objective transitions ----------------------------------------------------------------------

    /// <summary>Reads the three fixed layer slots and reports the one whose status is a boundary row. A status
    /// outside the two the rows name is no fact: the two intermediate members are the objective's own progress
    /// and the checklist's rows are its start and its win.</summary>
    internal static void ReadObjectiveStatus(bool isRecall, Action<string, int, int, bool> report)
    {
        var state = WardenObjectiveManager.CurrentState;
        if (state == null) return;
        foreach (var (layer, type) in LayerSlots)
        {
            if (!WardenObjectiveManager.HasWardenObjectiveDataForLayer(type)) continue;
            int status = (int)state.GetLayerStatus(type);
            int chain = state.GetChainIndexForLayer(type);
            if (status is not (LevelEventContract.StatusStarted or LevelEventContract.StatusItemSolved)) continue;
            report(layer, status, chain, isRecall);
        }
    }

    /// <summary>The objective's chain index for one layer, which is what a reactor wave advances. Read from the
    /// objective machine's own state rather than from a counter this provider keeps.</summary>
    internal static int ChainIndex(string layer)
    {
        var state = WardenObjectiveManager.CurrentState;
        if (state == null) return -1;
        var type = LayerType(layer);
        if (type == null) return -1;
        return state.GetChainIndexForLayer(type.Value);
    }

    /// <summary>The layer members this provider names, in the order the checklist's own vocabulary lists them.
    /// `LG_LayerType` declares exactly these three, so a layer this table cannot spell is a layer the game does
    /// not have rather than one that gets folded into the nearest.</summary>
    internal static readonly IReadOnlyList<(string Layer, LG_LayerType Type)> LayerSlots = Array.AsReadOnly(new[]
    {
        ("main", LG_LayerType.MainLayer),
        ("secondary", LG_LayerType.SecondaryLayer),
        ("third", LG_LayerType.ThirdLayer)
    });

    internal static LG_LayerType? LayerType(string layer) => layer switch
    {
        "main" => LG_LayerType.MainLayer,
        "secondary" => LG_LayerType.SecondaryLayer,
        "third" => LG_LayerType.ThirdLayer,
        _ => null
    };

    internal static string LayerName(LG_LayerType type) => type switch
    {
        LG_LayerType.SecondaryLayer => "secondary",
        LG_LayerType.ThirdLayer => "third",
        _ => "main"
    };

    // ---- the HSU ------------------------------------------------------------------------------------

    /// <summary>The container a solved objective item came out of. The HSU's own objective component keeps the
    /// item it handed over, so the reference is the item's placement identity when the world can answer one and
    /// null when it cannot — a port whose value is unknown is left out rather than invented.</summary>
    internal static string? ItemReference(int serialNumber, string? key)
    {
        if (string.IsNullOrEmpty(key) && serialNumber == 0) return null;
        return string.IsNullOrEmpty(key)
            ? "hsu/" + serialNumber.ToString(CultureInfo.InvariantCulture)
            : "hsu/" + key;
    }

    // ---- the zone a player entered ------------------------------------------------------------------

    /// <summary>The three-layer address of one native zone, computed from the zone's own members: the dimension
    /// index it belongs to, the layer it was built on and its index inside that layer. This is the same address
    /// <see cref="ZoneIndex"/> resolves for every other level-scope row, so a plan that mounts on a zone by
    /// address compares one spelling.</summary>
    internal static string? ZoneAddress(LG_Zone? zone)
    {
        if (zone == null) return null;
        string layer = LayerName(zone.m_layer.m_type);
        return "dimension:" + ((int)zone.m_dimensionIndex).ToString(CultureInfo.InvariantCulture)
            + "/layer:" + layer
            + "/zone:" + zone.IDinLayer.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Which zone a player stands in now, from the agent's own last-entered zone. The game writes that
    /// field itself, so this is a read of the game's state and not a Forge-side ledger.</summary>
    internal static string? CurrentZoneAddress(PlayerAgent? agent) => ZoneAddress(agent?.m_lastEnteredZone);

    // ---- the dimension portal -----------------------------------------------------------------------

    /// <summary>One portal's identity: the serial number the level gave it, which is the same number its spawn
    /// placement carries. A portal that is not built yet answers null and publishes nothing.</summary>
    internal static string? PortalId(LG_DimensionPortal? portal)
        => portal == null ? null : "portal/" + portal.m_serialNumber.ToString(CultureInfo.InvariantCulture);

    internal static int Dimension(LG_DimensionPortal? portal)
        => portal == null ? 0 : (int)portal.m_targetDimension;

    /// <summary>The dimension a course node belongs to. `AIG_CourseNode.m_dimension` is the node's own dimension
    /// object and `Dimension.DimensionIndex` is the member this vocabulary spells, so the "from" side of a portal
    /// warp is the dimension the player's previous node was built in — read, not remembered.</summary>
    internal static int NodeDimension(AIGraph.AIG_CourseNode? node)
        => node == null ? 0 : (int)node.m_dimension.DimensionIndex;
}
