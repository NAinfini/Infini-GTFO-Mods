using System;
using System.Collections.Generic;
using ForgeMap;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The blocking half of a trigger zone: for every zone the author marked `blocksPlayers`, every machine builds one
/// `BoxCollider` with `isTrigger = false` on layer 13 — the game's own `InvisibleWall` layer — at the zone's own
/// position, rotation and extents. The zone stays invisible: a collider with no renderer draws nothing, which is
/// the whole point of the object.
///
/// Every machine builds its own copy from the same package document, because collision is local physics: the local
/// player is pushed back by the body on that player's own machine, and no network message is involved. Nothing here
/// is host-gated for that reason, and nothing here touches navigation: the game builds its AI graph at generation
/// time, so a collider added now neither rebuilds the graph nor makes enemies avoid the volume — the layer's own
/// collision matrix is what decides who is stopped, and enemies do not collide with layer 13 at all.
///
/// The set is rebuilt when the level changes and only then: one world keeps one set of bodies, and the world after
/// it starts from the package data again rather than from the previous world's leftovers.
/// </summary>
internal sealed class TriggerZoneColliders : IDisposable
{
    /// <summary>The game's own `InvisibleWall` layer. The number is the game build's layer table, not this mod's
    /// choice: layer 13 is the layer the original invisible walls are on, and it is the one the physics matrix
    /// stops players on without touching enemies, glue projectiles or the projectile-blocker layer.</summary>
    internal const int InvisibleWallLayer = 13;

    private readonly Action<string> _report;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly List<GameObject> _built = new();
    private MapLevelReference? _level;
    private bool _synced;
    private bool _disposed;

    internal TriggerZoneColliders(Action<string> report)
        => _report = report ?? throw new ArgumentNullException(nameof(report));

    /// <summary>The bodies this machine currently holds.</summary>
    internal int Count => _built.Count;

    /// <summary>Rebuilds the bodies for the level this world is running, or clears them when there is no level.
    /// Called once per tick with the level the world reports; a level that did not change rebuilds nothing.</summary>
    internal void Sync(IReadOnlyList<TriggerZone> zones, MapLevelReference? level)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(zones);
        if (_synced && _level == level) return;
        Clear();
        _synced = true;
        _level = level;
        if (level is not { } current) return;
        foreach (var zone in TriggerZoneContract.ForLevel(zones, current))
        {
            if (!zone.BlocksPlayers) continue;
            try { _built.Add(Build(zone)); }
            catch (Exception error)
            {
                ReportOnce("build:" + zone.Id, "trigger-zone-collider: zone `" + zone.Id + "` could not be built ("
                    + error.GetType().Name + "); it does not block players.");
            }
        }
    }

    /// <summary>One zone's own body: an oriented box with the zone's blocking extents, on the invisible-wall layer,
    /// with no renderer and no other component.</summary>
    private static GameObject Build(TriggerZone zone)
    {
        var extents = zone.BlockingExtents();
        var host = new GameObject("ForgeTriggerZone:" + zone.Id) { layer = InvisibleWallLayer };
        host.transform.position = new Vector3((float)zone.Position[0], (float)zone.Position[1], (float)zone.Position[2]);
        host.transform.rotation = new Quaternion((float)zone.Rotation[0], (float)zone.Rotation[1],
            (float)zone.Rotation[2], (float)zone.Rotation[3]);
        var box = host.AddComponent<BoxCollider>();
        box.isTrigger = false;
        box.center = Vector3.zero;
        box.size = new Vector3((float)extents[0], (float)extents[1], (float)extents[2]);
        return host;
    }

    /// <summary>Destroys every body this machine built. A body whose destroy throws is reported once and dropped
    /// from the table: keeping it listed would make the next sync believe it is still there.</summary>
    private void Clear()
    {
        foreach (var host in _built)
        {
            try { if (host != null) UnityEngine.Object.Destroy(host); }
            catch (Exception error)
            {
                ReportOnce("destroy", "trigger-zone-collider: a zone body could not be destroyed ("
                    + error.GetType().Name + ").");
            }
        }
        _built.Clear();
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _report(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _synced = false;
        _level = null;
    }
}
