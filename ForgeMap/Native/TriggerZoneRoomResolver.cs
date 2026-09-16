using System;
using System.Collections.Generic;
using AssetShards;
using ForgeMap;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The three coordinates of one zone of the level being played, as the map-object addresses already spell them and
/// as an authored room locator carries them. Every search is scoped by them: the authored zone id cannot be resolved
/// on this side, so the document carries the level's own coordinates and the search runs inside exactly that zone.
/// The scope carries the coordinates rather than a zone object, because the object belongs to one world and a scope
/// outliving it would be a reference into a level that ended.
/// </summary>
public readonly struct TriggerZoneRoomScope
{
    public TriggerZoneRoomScope(int dimension, int layer, int localZoneIndex)
    {
        Dimension = dimension;
        Layer = layer;
        LocalZoneIndex = localZoneIndex;
    }

    /// <summary>The zone's dimension, `LG_Zone.m_dimensionIndex`.</summary>
    public int Dimension { get; }
    /// <summary>The zone's layer type, `LG_Zone.m_layer.m_type`.</summary>
    public int Layer { get; }
    /// <summary>The zone's index inside its layer, `LG_Zone.LocalIndex`.</summary>
    public int LocalZoneIndex { get; }
}

/// <summary>
/// One generated room of this level: the geomorph the level really built, the zone it stands in, and where its
/// own transform put it. The pose is the room's world pose, which is the only frame an authored zone's
/// room-local position and rotation can be read in — and the identity is native instance ids, so a caller can
/// find the same object again in its own traversal instead of matching a name.
/// </summary>
public sealed class TriggerZoneRoomHit
{
    internal TriggerZoneRoomHit(int geomorphInstanceId, int zoneInstanceId, int dimension, int layer,
        int localZoneIndex, double[] position, double[] rotation)
    {
        GeomorphInstanceId = geomorphInstanceId;
        ZoneInstanceId = zoneInstanceId;
        Dimension = dimension;
        Layer = layer;
        LocalZoneIndex = localZoneIndex;
        Position = Array.AsReadOnly(position);
        Rotation = Array.AsReadOnly(rotation);
    }

    /// <summary>The geomorph's own instance id, the identity a report names it by.</summary>
    public int GeomorphInstanceId { get; }
    /// <summary>The instance id of the zone the room stands in.</summary>
    public int ZoneInstanceId { get; }
    public int Dimension { get; }
    public int Layer { get; }
    public int LocalZoneIndex { get; }
    /// <summary>The room's world position in metres.</summary>
    public IReadOnlyList<double> Position { get; }
    /// <summary>The room's world rotation, a unit quaternion in `x y z w` order.</summary>
    public IReadOnlyList<double> Rotation { get; }
}

/// <summary>
/// What the level answered for one authored room reference: the rooms that reference names, or the one reason the
/// question could not be answered. The rooms are the answer, not a guess: one room is the reference's room, and a
/// refusal is never a pick between candidates. The three reasons a level can refuse for are told apart, because
/// "the zone is not there", "no room of this source is in it" and "several rooms of this source are in it" are
/// three different things for an author to fix.
/// </summary>
public sealed class TriggerZoneRoomResolution
{
    /// <summary>The refusal of a world with no generated level: no room of it exists to be named.</summary>
    public const string NoWorld = "no-world";
    /// <summary>The refusal of a prefab path the game's own asset loader holds no object for. The identity an
    /// authored reference carries is the loaded prefab object, so a path that loads nothing names nothing —
    /// a prefab's display name is not that identity and is never substituted for it.</summary>
    public const string PrefabNotLoaded = "prefab-not-loaded";
    /// <summary>The refusal of coordinates the level has no zone for: nothing can be searched, so nothing is
    /// claimed — not even that the room is absent.</summary>
    public const string ZoneUnresolved = "zone-unresolved";
    /// <summary>The refusal of a zone that holds no room generated from this reference's prefab object.</summary>
    public const string NoRoom = "no-room";
    /// <summary>The refusal of a zone that holds several: the same prefab placed twice names no single room, and
    /// the rooms it found are carried so a report can name them.</summary>
    public const string MultipleRooms = "multiple-rooms";

    internal TriggerZoneRoomResolution(string? refusal, IReadOnlyList<TriggerZoneRoomHit> rooms)
    {
        Refusal = refusal;
        Rooms = rooms;
    }

    /// <summary>Why no room could be named at all, or null when the searched zone answered with exactly one room.</summary>
    public string? Refusal { get; }

    /// <summary>Every room of the searched zone whose source prefab is this reference's prefab object, in the
    /// level's own area order. Empty on every refusal that observed none; a `multiple-rooms` refusal carries the
    /// rooms it counted. Exactly one room and no refusal is the answer this reference names.</summary>
    public IReadOnlyList<TriggerZoneRoomHit> Rooms { get; }

    /// <summary>The one room this reference names, or null when the search found none or more than one.</summary>
    public TriggerZoneRoomHit? Room => Refusal == null && Rooms.Count == 1 ? Rooms[0] : null;
}

/// <summary>
/// The one resolver that can name an authored room in the level the game generated, shared by every reader of
/// the same reference: the trigger zones that compose a zone's room-local pose into world coordinates, and the
/// development diagnostics that report which native object an authored reference resolved to.
///
/// A room's identity is the prefab object the game's own asset loader returned for the reference's `Assets/`
/// path, compared by object reference against the prefab each generated geomorph was created from
/// (`LG_Geomorph.m_geoPrefab`). A name is never compared: a prefab's display name is not the original asset
/// identity, which is exactly why the reference carries the whole path and why this is a reference comparison.
///
/// The search is scoped by the locator's own native zone coordinates, asked for through the one zone table the
/// map-object addresses already use (`ZoneIndex`), so "the room a reference names" is answered from the same view
/// of the level the door and terminal addresses are built from, and never from a second walk of the native graph.
/// The scope is required: an authored zone id cannot be resolved on this side, and a search across the whole level
/// would refuse every zone whose room prefab the level happened to place twice. Within its zone the answer must be
/// unique: zero rooms and several rooms are both a refusal, because a room-local pose composed with the wrong
/// room's transform is a volume at the wrong place, and a wrong volume is worse than a missing one.
/// </summary>
public static class TriggerZoneRoomResolver
{
    /// <summary>Every room of one zone whose source prefab is the object the reference's path loads. The zone is
    /// the locator's own native coordinates, so this is the answer for a caller that can name the zone the
    /// reference claims — which every authored room locator now does.</summary>
    public static TriggerZoneRoomResolution Resolve(long worldEpoch, string sourcePrefab, TriggerZoneRoomScope zone)
        => Search(worldEpoch, sourcePrefab, zone);

    /// <summary>The room pose one authored zone reference names in this world, or null when this level cannot name
    /// exactly one room for it. This is the production answer the trigger-zone placement composes a zone's own
    /// document pose with, and it searches the zone the locator itself carries.</summary>
    internal static TriggerZoneRoomPose? Lookup(long worldEpoch, TriggerZoneRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return Resolve(worldEpoch, room.SourcePrefab,
            new TriggerZoneRoomScope(room.Dimension, room.Layer, room.LocalIndex)).Room is { } hit
            ? TriggerZoneRoomPose.Of(ToArray(hit.Position), ToArray(hit.Rotation))
            : null;
    }

    /// <summary>One search: the loaded prefab object's identity, then the one zone the scope names and inside it
    /// every generated area whose geomorph was created from that object. A geomorph is one room however many areas
    /// it has, so it is counted once. Coordinates the level has no zone for are their own refusal: the level was
    /// never asked the room question at all.</summary>
    private static TriggerZoneRoomResolution Search(long worldEpoch, string sourcePrefab, TriggerZoneRoomScope zone)
    {
        if (worldEpoch == 0) return Refused(TriggerZoneRoomResolution.NoWorld);
        if (string.IsNullOrEmpty(sourcePrefab)) return Refused(TriggerZoneRoomResolution.PrefabNotLoaded);
        var prefab = AssetShardManager.GetLoadedAsset<GameObject>(sourcePrefab, false);
        if (prefab == null || prefab.WasCollected) return Refused(TriggerZoneRoomResolution.PrefabNotLoaded);
        var identity = prefab.Pointer;
        var index = ZoneIndex.Current(worldEpoch);
        var rooms = new List<TriggerZoneRoomHit>();
        var counted = new HashSet<int>();
        var found = false;
        foreach (var candidate in index.Zones())
        {
            if (!index.TryCoordinates(candidate, out var coordinates)) continue;
            if (coordinates.Dimension != zone.Dimension || coordinates.Layer != zone.Layer
                || coordinates.Zone != zone.LocalZoneIndex) continue;
            found = true;
            var areas = candidate.m_areas;
            if (areas == null) continue;
            for (var i = 0; i != areas.Count; i++)
            {
                var geomorph = areas[i] is { } area && !area.WasCollected ? area.m_geomorph : null;
                if (geomorph == null || geomorph.WasCollected || !From(geomorph, identity)) continue;
                if (!counted.Add(geomorph.GetInstanceID())) continue;
                var transform = geomorph.transform;
                if (transform == null || transform.WasCollected) continue;
                var position = transform.position;
                var rotation = transform.rotation;
                rooms.Add(new TriggerZoneRoomHit(geomorph.GetInstanceID(), candidate.GetInstanceID(), coordinates.Dimension,
                    coordinates.Layer, coordinates.Zone,
                    new[] { (double)position.x, (double)position.y, (double)position.z },
                    new[] { (double)rotation.x, (double)rotation.y, (double)rotation.z, (double)rotation.w }));
            }
        }
        if (!found) return Refused(TriggerZoneRoomResolution.ZoneUnresolved);
        if (rooms.Count == 0) return Refused(TriggerZoneRoomResolution.NoRoom);
        var answer = Array.AsReadOnly(rooms.ToArray());
        return new TriggerZoneRoomResolution(rooms.Count > 1 ? TriggerZoneRoomResolution.MultipleRooms : null, answer);
    }

    /// <summary>Whether this geomorph was created from that exact prefab object.</summary>
    private static bool From(LG_Geomorph geomorph, IntPtr identity)
    {
        var prefab = geomorph.m_geoPrefab;
        return prefab != null && !prefab.WasCollected && prefab.Pointer == identity;
    }

    private static TriggerZoneRoomResolution Refused(string refusal)
        => new(refusal, Array.Empty<TriggerZoneRoomHit>());

    private static double[] ToArray(IReadOnlyList<double> values)
    {
        var copy = new double[values.Count];
        for (var i = 0; i != values.Count; i++) copy[i] = values[i];
        return copy;
    }
}
