using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;
using LevelGeneration;
using UnityEngine;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeDevelopment.Native;

/// <summary>
/// The actual layout of a generated level, recorded into `forge.log.v1` (contract §3.2, `map.layout-generated`).
///
/// The report files already say which build jobs ran; they never said what the level became. This reads the
/// built level — every zone the generator created, the geomorph prefabs inside it and the gate through which the
/// zone was expanded from its parent — and writes one observation per generation attempt: an attempt opens with
/// `complete:false` when <see cref="LG_Factory.Setup"/> starts and closes with the whole layout once
/// <see cref="LG_Factory.FactoryDone"/> returned. The elevator's landing is a later moment, so it is recorded by
/// the observation that carries a landing tick once the ride reaches <see cref="ElevatorRideState.End"/>: the
/// website reads the last observation, and an attempt whose ride never lands stays visibly incomplete.
///
/// Every value here is read, never inferred from the plan: a zone with no readable geomorph reports an empty
/// tile list, and a direction the two zone centres do not settle reports `unknown`.
/// </summary>
internal static class GeneratedLayoutRecord
{
    private static long? _landingTick;
    private static bool _attemptOpen;
    private static bool _landingRecorded;

    /// <summary>Opens an attempt: a generation run has started and has built nothing yet.</summary>
    internal static void AttemptStarted()
    {
        _landingTick = null;
        _attemptOpen = true;
        _landingRecorded = false;
        Write(new RuntimeLogLayout
        {
            Complete = false,
            ElevatorLandedTick = null,
            Zones = Array.Empty<RuntimeLogLayoutZone>(),
            Connections = Array.Empty<RuntimeLogLayoutConnection>(),
        });
    }

    /// <summary>Closes the attempt with the layout the generator actually built.</summary>
    internal static void AttemptCompleted()
    {
        if (!_attemptOpen) return;
        Write(Capture());
    }

    /// <summary>Watches the elevator ride and records its landing once. Runs on the authoring monitor's own
    /// Update; reading the ride's private state field is reflection-free because the interop assembly exposes it,
    /// and a ride that does not exist yet is simply not a landing.</summary>
    internal static void PollLanding()
    {
        if (!_attemptOpen || _landingTick != null) return;
        var ride = ElevatorRide.Current;
        if (ride == null || ride.m_currentState != ElevatorRideState.End) return;
        _landingTick = RuntimeDiagnostics.SimulationTick;
        // The layout may not be built yet (the ride can exist before FactoryDone); the completion record then
        // carries the landing tick, so only an already-completed attempt gets a second observation here.
        if (!_landingRecorded && RuntimeDiagnostics.StructureComplete)
        {
            _landingRecorded = true;
            Write(Capture());
        }
    }

    private static RuntimeLogLayout Capture()
    {
        var floor = Builder.Current?.m_currentFloor;
        var zones = floor?.allZones;
        var rows = new List<RuntimeLogLayoutZone>();
        var links = new List<RuntimeLogLayoutConnection>();
        if (zones != null)
        {
            foreach (var zone in zones)
            {
                if (zone == null) continue;
                var locator = Locator(zone);
                if (locator == null) continue;
                rows.Add(new RuntimeLogLayoutZone
                {
                    Zone = locator,
                    Dimension = (int)zone.DimensionIndex,
                    Layer = LayerOf(zone),
                    LocalIndex = (int)zone.LocalIndex,
                    Floor = FloorOf(floor),
                    Tiles = Tiles(zone),
                });
                if (rows.Count >= RuntimeLogLayoutLimits.Zones) break;
            }
            foreach (var zone in zones)
            {
                if (links.Count >= RuntimeLogLayoutLimits.Connections) break;
                var child = Locator(zone);
                var parent = ParentOf(zone);
                if (child == null || parent == null) continue;
                links.Add(new RuntimeLogLayoutConnection { From = Locator(parent)!, To = child, Direction = DirectionBetween(parent, zone!) });
            }
        }
        return new RuntimeLogLayout
        {
            Complete = true,
            ElevatorLandedTick = _landingTick,
            Zones = rows,
            Connections = links,
        };
    }

    private static void Write(RuntimeLogLayout layout)
    {
        try { HostPlugin.Runtime?.ReportGeneratedLayout(in layout); }
        catch (Exception error) { RuntimeDiagnostics.Note("layout_record_failed", error.GetType().Name + ": " + error.Message); }
    }
    /// <summary>The zone that expanded this zone, read off the gate it was built through: the gate links the
    /// parent's area to the new one, and which side is the parent is a fact of the built level, not an assumption.</summary>
    private static LG_Zone? ParentOf(LG_Zone? zone)
    {
        var gate = zone?.m_sourceGate;
        if (gate == null) return null;
        var from = gate.m_linksFrom?.m_zone;
        var to = gate.m_linksTo?.m_zone;
        if (from == null || to == null) return null;
        return ReferenceEquals(from, zone) ? to : from;
    }

    private static string? Locator(LG_Zone? zone)
    {
        if (zone == null) return null;
        var layer = LayerOf(zone);
        return layer < 0 ? null
            : (int)zone.DimensionIndex + "/" + layer.ToString(CultureInfo.InvariantCulture) + "/"
                + ((int)zone.LocalIndex).ToString(CultureInfo.InvariantCulture);
    }

    private static int LayerOf(LG_Zone? zone)
    {
        try { return zone?.Layer == null ? -1 : (int)zone.Layer.m_type; }
        catch (Exception) { return -1; }
    }

    private static int FloorOf(LG_Floor? floor)
    {
        try { return floor == null ? -1 : floor.ID; }
        catch (Exception) { return -1; }
    }

    /// <summary>The geomorph prefabs built inside one zone, in area order and without duplicates: the same
    /// geomorph placed twice is one tile of this zone, not two.</summary>
    private static IReadOnlyList<string> Tiles(LG_Zone zone)
    {
        var tiles = new List<string>();
        var areas = zone.m_areas;
        if (areas == null) return tiles;
        foreach (var area in areas)
        {
            if (tiles.Count >= RuntimeLogLayoutLimits.TilesPerZone) break;
            string? name;
            try
            {
                var geomorph = area?.m_geomorph;
                if (geomorph == null) continue;
                name = geomorph.m_geoPrefab == null ? geomorph.name : geomorph.m_geoPrefab.name;
            }
            catch (Exception) { continue; }
            if (string.IsNullOrEmpty(name) || tiles.Contains(name)) continue;
            tiles.Add(name!);
        }
        return tiles;
    }

    /// <summary>The dominant world axis from the parent zone's centre to the child's, which is what "the child
    /// lies north of its parent" means for a generated level. A zero offset is `same` and an unreadable centre is
    /// `unknown`; neither is guessed into `north`.</summary>
    private static string DirectionBetween(LG_Zone parent, LG_Zone child)
    {
        Vector3 offset;
        try { offset = child.CenterPosition - parent.CenterPosition; }
        catch (Exception) { return RuntimeLogLayoutDirections.Unknown; }
        if (!float.IsFinite(offset.x) || !float.IsFinite(offset.y) || !float.IsFinite(offset.z)) return RuntimeLogLayoutDirections.Unknown;
        var ax = Math.Abs(offset.x); var ay = Math.Abs(offset.y); var az = Math.Abs(offset.z);
        if (ax == 0f && ay == 0f && az == 0f) return RuntimeLogLayoutDirections.Same;
        if (ax >= ay && ax >= az) return offset.x >= 0f ? RuntimeLogLayoutDirections.East : RuntimeLogLayoutDirections.West;
        if (az >= ay) return offset.z >= 0f ? RuntimeLogLayoutDirections.North : RuntimeLogLayoutDirections.South;
        return offset.y >= 0f ? RuntimeLogLayoutDirections.Up : RuntimeLogLayoutDirections.Down;
    }
}
