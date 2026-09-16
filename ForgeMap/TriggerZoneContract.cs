using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The three authored trigger-zone shapes. The site's object editor writes one of these names in the
/// `shape` field, so the spelling here is the wire spelling.</summary>
public enum TriggerZoneShape { Box, Sphere, Capsule }

/// <summary>Who a zone reacts to. The site's editor writes it as `who`; `both` is the value a zone that names
/// nobody carries, which is the only default that cannot hide a target.</summary>
public enum TriggerZoneWho { Player, Enemy, Both }

/// <summary>
/// The authored room one trigger zone stands in, in the one locator spelling the package's own object references
/// use (`forge-project.json` `objectReferences`, kind `unique-geomorph-in-zone`): the author's zone id, that zone's
/// native coordinates, the room definition's identity and the native prefab the room is generated from. The grammar
/// is the same one, because a room the author can point at in the editor is the same room the runtime resolves, and
/// a second spelling would be a second answer to "which room is this".
///
/// The native coordinates are on the locator because this half cannot resolve the author's zone id: the mapping
/// from an authored zone to the zone the level generated lives with the project manifest, and a trigger-zone
/// document carries no such table. So the document carries the level's own coordinates and the resolver looks
/// inside exactly that zone — never across the whole level, where the same prefab placed in two zones would make
/// every such zone unplaceable.
///
/// The locator is data, not a position: where the room really ended up is the level's own answer after generation,
/// and only a resolver that can see the generated level may answer it. This half reads the reference; the native
/// half resolves it.
/// </summary>
public sealed class TriggerZoneRoom
{
    /// <summary>The one locator kind this grammar carries. Another kind names another way of finding a room, which
    /// this build does not have, so it is refused rather than interpreted.</summary>
    public const string Kind = "unique-geomorph-in-zone";
    /// <summary>The longest room definition id, matching the package reference grammar.</summary>
    public const int MaximumRoomIdChars = 512;
    /// <summary>The longest native prefab identity, matching the package reference grammar.</summary>
    public const int MaximumPrefabChars = 1024;
    /// <summary>The length of the room definition revision: one SHA-256 in lower-case hex.</summary>
    public const int RevisionChars = 64;
    /// <summary>The greatest layer a zone can stand on, in the site's own zone coordinate grammar.</summary>
    public const int MaximumLayer = 2;

    internal TriggerZoneRoom(string zoneAuthorId, int dimension, int layer, int localIndex, string roomId, string revision,
        string sourcePrefab)
    {
        ZoneAuthorId = zoneAuthorId;
        Dimension = dimension;
        Layer = layer;
        LocalIndex = localIndex;
        RoomId = roomId;
        Revision = revision;
        SourcePrefab = sourcePrefab;
    }

    /// <summary>The authored zone the room belongs to, in the site's own zone identity grammar.</summary>
    public string ZoneAuthorId { get; }
    /// <summary>The native zone's dimension, the coordinate the level's own zone table is addressed by.</summary>
    public int Dimension { get; }
    /// <summary>The native zone's layer inside its dimension, `LG_Zone.m_layer.m_type`.</summary>
    public int Layer { get; }
    /// <summary>The native zone's index inside its layer, `LG_Zone.LocalIndex`.</summary>
    public int LocalIndex { get; }
    /// <summary>The room definition's id inside the package.</summary>
    public string RoomId { get; }
    /// <summary>The room definition's revision, so a room re-authored between export and play is a room the
    /// reference no longer names.</summary>
    public string Revision { get; }
    /// <summary>The native prefab the room is generated from: the identity a resolver matches the generated level
    /// against. A prefab name alone is not this identity, which is why it is carried whole and validated.</summary>
    public string SourcePrefab { get; }

    /// <summary>Reads one `room` field. Every refusal is one code and one sentence naming the first thing that
    /// could not be read, so a mistyped locator is refused at the document instead of resolving to a room that was
    /// never meant. The grammar itself belongs to the document reader that carries every other field.</summary>
    public static bool TryParse(JsonElement row, out TriggerZoneRoom? room, out string? code, out string? reason)
        => TriggerZoneManifest.TryRoom(row, out room, out code, out reason);

    public override string ToString() => ZoneAuthorId + "/" + RoomId + "@" + Revision;
}

/// <summary>
/// Where one room really ended up in the level the game generated: a world position and a unit quaternion in the
/// same `x y z w` order every other pose in this family uses. Only the level's own data can answer this, so the
/// value exists solely to be handed to <see cref="TriggerZone.PlacedIn"/>; a pose that is not three finite metres
/// and a unit quaternion is refused by name rather than composed into a zone nothing can judge.
/// </summary>
public readonly struct TriggerZoneRoomPose
{
    private readonly double[] _position;
    private readonly double[] _rotation;

    private TriggerZoneRoomPose(double[] position, double[] rotation)
    {
        _position = position;
        _rotation = rotation;
    }

    /// <summary>The one way to build a pose: the room's world position in metres and its world rotation.</summary>
    public static TriggerZoneRoomPose Of(double[] position, double[] rotation)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(rotation);
        if (position.Length != 3) throw new RuntimeContractException("room-pose", "A room position requires three coordinates.");
        if (rotation.Length != TriggerZone.RotationComponents)
            throw new RuntimeContractException("room-pose", "A room rotation is a unit quaternion in `x y z w` order.");
        foreach (var value in position)
            if (!double.IsFinite(value)) throw new RuntimeContractException("room-pose", "A room position carries finite metre coordinates.");
        foreach (var value in rotation)
            if (!double.IsFinite(value) || Math.Abs(value) > 1)
                throw new RuntimeContractException("room-pose", "A room rotation is a unit quaternion in `x y z w` order.");
        if (!TriggerZoneManifest.IsUnitQuaternion(rotation))
            throw new RuntimeContractException("room-pose", "A room rotation is a unit quaternion in `x y z w` order.");
        return new TriggerZoneRoomPose((double[])position.Clone(), (double[])rotation.Clone());
    }

    /// <summary>The room's world position.</summary>
    public IReadOnlyList<double> Position => Array.AsReadOnly(_position);
    /// <summary>The room's world rotation, a unit quaternion in `x y z w` order.</summary>
    public IReadOnlyList<double> Rotation => Array.AsReadOnly(_rotation);

    /// <summary>One room-local point in world metres: the room's rotation applied to the offset, then the room's
    /// own position. This is the whole local-to-world half of a zone's pose.</summary>
    internal double[] Point(IReadOnlyList<double> local)
    {
        var (x, y, z) = TriggerZone.Rotate(_rotation, local[0], local[1], local[2]);
        return new[] { x + _position[0], y + _position[1], z + _position[2] };
    }

    /// <summary>One room-local rotation in world space: the room's own rotation composed with it.</summary>
    internal double[] Compose(IReadOnlyList<double> local) => TriggerZone.Multiply(_rotation, local);
}

/// <summary>
/// One authored trigger zone, read from a package's own exported map data and nothing else: an oriented volume
/// placed in a room, the level it belongs to, who it reacts to, and whether it also blocks players.
///
/// A zone is one map object of this provider: its object data is this record, its address is
/// <see cref="TriggerZoneAddress"/>, and the two facts it publishes are the map-object half's own, so a plan is
/// hung on the zone it is about rather than on a volume id a second mechanism would have to match.
///
/// The volume is the author's own object, not a native collider read back: the zone is invisible by construction
/// (nothing renders it), so the shape is the data the editor wrote. Containment is closed — a position exactly on
/// the boundary is inside — and every measurement is a world metre. The orientation is a unit quaternion in
/// `x y z w` order, the order the site's own transform writes, and a point is tested in the zone's local frame so
/// a rotated box, capsule or sphere is the same object the author placed.
///
/// The document's `position` and `rotation` are the room's local coordinates, because that is the only coordinate
/// system the site's editor can draw in. <see cref="Placed"/> says which pose this instance carries: an authored
/// zone is not judged, and <see cref="PlacedIn"/> is the one conversion into the world pose the tick, the
/// containment test and the blocking bodies all use.
/// </summary>
public sealed class TriggerZone
{
    /// <summary>The quaternion components of one rotation, in the site's `x y z w` order.</summary>
    public const int RotationComponents = 4;
    internal const double RotationTolerance = 0.001;

    internal TriggerZone(string id, MapLevelReference level, TriggerZoneShape shape, double[] size, double[] position,
        double[] rotation, TriggerZoneWho who, bool blocksPlayers, TriggerZoneRoom room, bool placed)
    {
        Id = id;
        Level = level;
        Shape = shape;
        Size = Array.AsReadOnly(size);
        Position = Array.AsReadOnly(position);
        Rotation = Array.AsReadOnly(rotation);
        Who = who;
        BlocksPlayers = blocksPlayers;
        Room = room;
        Placed = placed;
    }

    /// <summary>The authored object's own id inside the package: the address key a plan mounts on and the identity
    /// both facts are published under. Opaque here; the package document is its only source, which is also what
    /// refuses an id the map-object address cannot carry.</summary>
    public string Id { get; }
    /// <summary>The level this zone was placed in. A zone of another level is not this world's zone.</summary>
    public MapLevelReference Level { get; }
    public TriggerZoneShape Shape { get; }
    /// <summary>Full sizes in metres: a box's three edge lengths, a sphere's diameter repeated three times, and a
    /// capsule's diameter, full height and diameter again.</summary>
    public IReadOnlyList<double> Size { get; }
    /// <summary>The zone's origin: room-local metres while the zone is unplaced, world metres once
    /// <see cref="PlacedIn"/> produced it.</summary>
    public IReadOnlyList<double> Position { get; }
    /// <summary>The zone's orientation as a unit quaternion in `x y z w` order, in the frame
    /// <see cref="Position"/> is written in.</summary>
    public IReadOnlyList<double> Rotation { get; }
    public TriggerZoneWho Who { get; }
    /// <summary>Whether every machine builds a player-blocking collider for this zone. Blocking is local physics:
    /// it is decided by the package data here and instantiated identically on every machine.</summary>
    public bool BlocksPlayers { get; }
    /// <summary>The authored room this zone stands in. The document always names one: a pose without a room is a
    /// pose in a coordinate system nothing can convert, so a zone that names no room is refused at the document.</summary>
    public TriggerZoneRoom Room { get; }
    /// <summary>Whether <see cref="Position"/> and <see cref="Rotation"/> are world coordinates. Only a placed
    /// zone is judged, tested for containment or given a body: an authored zone is the author's own room-local
    /// numbers and means nothing in the world until the room it names has been resolved.</summary>
    public bool Placed { get; }

    /// <summary>The same zone in the world coordinates of the room it was authored in. A zone that is already
    /// placed answers itself, so a caller that re-places a table cannot compose a room pose twice.</summary>
    public TriggerZone PlacedIn(TriggerZoneRoomPose room)
    {
        if (Placed) return this;
        var position = room.Point(Position);
        var rotation = room.Compose(Rotation);
        return new TriggerZone(Id, Level, Shape, (double[])Size.ToArray(), position, rotation, Who, BlocksPlayers, Room, true);
    }

    /// <summary>One vector rotated by a unit quaternion in `x y z w` order.</summary>
    internal static (double X, double Y, double Z) Rotate(IReadOnlyList<double> rotation, double x, double y, double z)
    {
        var qx = rotation[0];
        var qy = rotation[1];
        var qz = rotation[2];
        var qw = rotation[3];
        var tx = 2 * (qy * z - qz * y);
        var ty = 2 * (qz * x - qx * z);
        var tz = 2 * (qx * y - qy * x);
        return (x + qw * tx + qy * tz - qz * ty,
            y + qw * ty + qz * tx - qx * tz,
            z + qw * tz + qx * ty - qy * tx);
    }

    /// <summary>Two rotations composed: the left one applied after the right one, both unit quaternions in
    /// `x y z w` order.</summary>
    internal static double[] Multiply(IReadOnlyList<double> left, IReadOnlyList<double> right)
        => new[]
        {
            left[3] * right[0] + left[0] * right[3] + left[1] * right[2] - left[2] * right[1],
            left[3] * right[1] - left[0] * right[2] + left[1] * right[3] + left[2] * right[0],
            left[3] * right[2] + left[0] * right[1] - left[1] * right[0] + left[2] * right[3],
            left[3] * right[3] - left[0] * right[0] - left[1] * right[1] - left[2] * right[2]
        };

    /// <summary>The box extents a blocking collider is built from: the zone's own oriented extents, so a sphere
    /// and a capsule block with the volume they enclose. Only a `blocksPlayers` zone has one.</summary>
    public double[] BlockingExtents() => Shape switch
    {
        TriggerZoneShape.Box => new[] { Size[0], Size[1], Size[2] },
        TriggerZoneShape.Sphere => new[] { Size[0], Size[0], Size[0] },
        _ => new[] { Size[0], Size[1], Size[0] }
    };

    /// <summary>Whether one world position is inside this zone. The point is rotated into the zone's own frame
    /// first, so an oriented box is tested against its own axes rather than against the world's.</summary>
    public bool Contains(IReadOnlyList<double> position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.Count != 3) throw new RuntimeContractException("spatial-point", "A position requires three coordinates.");
        var local = ToLocal(position[0] - Position[0], position[1] - Position[1], position[2] - Position[2]);
        const double epsilon = 1e-9;
        switch (Shape)
        {
            case TriggerZoneShape.Box:
                return Math.Abs(local[0]) <= Size[0] / 2d + epsilon
                    && Math.Abs(local[1]) <= Size[1] / 2d + epsilon
                    && Math.Abs(local[2]) <= Size[2] / 2d + epsilon;
            case TriggerZoneShape.Sphere:
                return Length(local[0], local[1], local[2]) <= Size[0] / 2d + epsilon;
            default:
                // Capsule: a `size[1]`-long segment along the zone's own Y axis with `size[0]`-diameter caps, so an
                // axis shorter than the diameter degenerates to the sphere of the same diameter.
                var segment = Math.Max(Size[1] / 2d - Size[0] / 2d, 0);
                var along = Math.Max(Math.Abs(local[1]) - segment, 0);
                return Length(local[0], along, local[2]) <= Size[0] / 2d + epsilon;
        }
    }

    /// <summary>The zone's own coordinates of one world offset: the inverse of the zone's rotation applied to it.
    /// A quaternion and its conjugate are inverses for a unit quaternion, which is what the parser guarantees.</summary>
    private double[] ToLocal(double x, double y, double z)
    {
        // conjugate(q) = (-x, -y, -z, w), and a quaternion and its conjugate are inverses for a unit quaternion,
        // which is what the parser guarantees.
        var conjugate = new[] { -Rotation[0], -Rotation[1], -Rotation[2], Rotation[3] };
        var (localX, localY, localZ) = Rotate(conjugate, x, y, z);
        return new[] { localX, localY, localZ };
    }

    private static double Length(double x, double y, double z)
    {
        var scale = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
        if (scale == 0) return 0;
        var nx = x / scale;
        var ny = y / scale;
        var nz = z / scale;
        return scale * Math.Sqrt(nx * nx + ny * ny + nz * nz);
    }

    public override string ToString() => Id + "@" + Level;
}

/// <summary>
/// The one package document that carries a level's trigger zones:
/// `plugins/&lt;package&gt;/forge/trigger-zones.json`, the same one-level package directory rule plans and the other
/// authored content files use. The document is data the author's own editor wrote, so it is parsed strictly and
/// all-or-nothing: a document that names one id twice, a shape it cannot build, or a number it cannot use names no
/// zone set at all. Reading the file off disk is the native half's job; this half is the document's one grammar.
/// </summary>
public static class TriggerZoneManifest
{
    public const int SchemaVersion = 1;
    /// <summary>Zones one package document may carry.</summary>
    public const int MaximumZones = 512;
    /// <summary>The smallest authored edge or diameter, in metres. A zero-sized zone contains nothing on one axis
    /// and would silently never fire, so it is refused rather than accepted as an empty volume.</summary>
    public const double MinimumSize = 0.05;
    public const double MaximumSize = 1000;
    public const int MaximumIdLength = 128;
    private const string FileName = "trigger-zones.json";

    /// <summary>The document's one file name, which is what a package scan looks for.</summary>
    public static string FileNameOnly => FileName;

    /// <summary>Reads one document. Every refusal is one code and one sentence naming the first thing that could not
    /// be read; a document that reads answers its zones in the order the author wrote them.</summary>
    public static bool TryParse(string text, out IReadOnlyList<TriggerZone>? zones, out string? code, out string? reason)
    {
        zones = null; code = null; reason = null;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException error) { code = "trigger-zone-json"; reason = error.Message; return false; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Refuse(out zones, out code, out reason, "trigger-zone-json", "The document's root is not an object.");
            if (!Known(root, out var unknown, "schemaVersion", "zones"))
                return Refuse(out zones, out code, out reason, "trigger-zone-field", "Unknown field `" + unknown + "`.");
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schema) || schema != SchemaVersion)
                return Refuse(out zones, out code, out reason, "trigger-zone-schema", "The document must declare schemaVersion " + SchemaVersion + ".");
            if (!root.TryGetProperty("zones", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return Refuse(out zones, out code, out reason, "trigger-zone-zones", "The document carries no `zones` array.");
            if (rows.GetArrayLength() > MaximumZones)
                return Refuse(out zones, out code, out reason, "trigger-zone-limit",
                    "The document carries " + rows.GetArrayLength() + " zones; the cap is " + MaximumZones + ".");
            var parsed = new List<TriggerZone>(rows.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryZone(row, out var zone, out code, out reason)) return false;
                if (!seen.Add(zone!.Id))
                    return Refuse(out zones, out code, out reason, "trigger-zone-duplicate",
                        "Zone id `" + zone.Id + "` is claimed more than once; the document names no zone set.");
                parsed.Add(zone);
            }
            zones = Array.AsReadOnly(parsed.ToArray());
            return true;
        }
    }

    private static bool TryZone(JsonElement row, out TriggerZone? zone, out string? code, out string? reason)
    {
        zone = null; code = null; reason = null;
        if (row.ValueKind != JsonValueKind.Object) return RefuseZone(out zone, out code, out reason, "trigger-zone-json", "A zone entry is not an object.");
        if (!Known(row, out var unknown, "id", "level", "room", "shape", "size", "position", "rotation", "who", "blocksPlayers"))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-field", "Unknown zone field `" + unknown + "`.");
        if (!row.TryGetProperty("room", out var roomRow))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-room",
                "A zone names the room it stands in; a room-local pose without a room cannot be placed.");
        if (!TriggerZoneRoom.TryParse(roomRow, out var room, out var roomCode, out var roomReason))
            return RefuseZone(out zone, out code, out reason, roomCode!, roomReason!);
        if (!Text(row, "id", out var id) || id!.Length > MaximumIdLength || !TriggerZoneAddress.IsZoneId(id))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-id", "A zone needs an id of at most "
                + MaximumIdLength + " characters that is not `" + TriggerZoneAddress.Category + "/` and carries no `:` or `/`.");
        if (!Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
            return RefuseZone(out zone, out code, out reason, "trigger-zone-level", "A zone needs a level reference of the form `<rundown>:<tier>:<index>`.");
        if (!Text(row, "shape", out var shapeText) || !TryShape(shapeText!, out var shape))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-shape", "A zone shape is `box`, `sphere` or `capsule`.");
        if (!Numbers(row, "size", 3, MinimumSize, MaximumSize, out var size))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-size", "A size carries three finite metre lengths in [" + MinimumSize + ", " + MaximumSize + "].");
        if (shape == TriggerZoneShape.Sphere && (size![1] != size[0] || size[2] != size[0]))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-size", "A sphere's three sizes are one diameter.");
        if (shape == TriggerZoneShape.Capsule && size![1] < size[0])
            return RefuseZone(out zone, out code, out reason, "trigger-zone-size", "A capsule is at least as tall as its own diameter.");
        if (!Numbers(row, "position", 3, -MaximumSize * 10, MaximumSize * 10, out var position))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-position", "A position carries three finite metre coordinates.");
        if (!Numbers(row, "rotation", TriggerZone.RotationComponents, -1, 1, out var rotation) || !IsUnitQuaternion(rotation!))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-rotation", "A rotation is a unit quaternion in `x y z w` order.");
        var who = TriggerZoneWho.Both;
        if (row.TryGetProperty("who", out var whoValue))
        {
            if (whoValue.ValueKind != JsonValueKind.String || !TryWho(whoValue.GetString()!, out who))
                return RefuseZone(out zone, out code, out reason, "trigger-zone-who", "`who` is `player`, `enemy` or `both`.");
        }
        var blocks = false;
        if (row.TryGetProperty("blocksPlayers", out var blocksValue))
        {
            if (blocksValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return RefuseZone(out zone, out code, out reason, "trigger-zone-blocks", "`blocksPlayers` is a boolean.");
            blocks = blocksValue.GetBoolean();
        }
        zone = new TriggerZone(id!, level, shape, size!, position!, rotation!, who, blocks, room!, false);
        return true;
    }

    /// <summary>The refusal one zone entry gets, in the shape that entry's own reader answers with. It is the
    /// same answer the set reader gives: a refusal names its code and says what the entry should have carried,
    /// and the zone it refused is null.</summary>
    private static bool RefuseZone(out TriggerZone? zone, out string? code, out string? reason, string refused, string message)
    {
        zone = null; code = refused; reason = message;
        return false;
    }

    /// <summary>Reads one zone's `room` locator: the same fields, the same validation and therefore the same
    /// meaning as the object reference a package's project manifest carries for the same room. A reference the
    /// manifest would refuse must not become a zone's own private answer to which room it stands in.
    /// The zone's native coordinates are required here too: without them this half would have to search the whole
    /// level, and a prefab two zones share would leave every zone that names it unplaced.</summary>
    internal static bool TryRoom(JsonElement row, out TriggerZoneRoom? room, out string? code, out string? reason)
    {
        room = null; code = "trigger-zone-room"; reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "`room` is an object."; return false; }
        if (!Known(row, out var unknown, "kind", "zoneAuthorId", "dimension", "layer", "localIndex", "room", "sourcePrefab"))
        { reason = "Unknown room field `" + unknown + "`."; return false; }
        if (!Text(row, "kind", 64, out var kind) || kind != TriggerZoneRoom.Kind)
        { reason = "A room locator kind is `" + TriggerZoneRoom.Kind + "`."; return false; }
        if (!Text(row, "zoneAuthorId", 64, out var zoneAuthorId) || !AuthorId(zoneAuthorId!))
        { reason = "A room locator needs the authored zone id it belongs to."; return false; }
        if (!Coordinate(row, "dimension", 0, int.MaxValue, out var dimension)
            || !Coordinate(row, "layer", 0, TriggerZoneRoom.MaximumLayer, out var layer)
            || !Coordinate(row, "localIndex", 0, int.MaxValue, out var localIndex))
        { reason = "A room locator carries the native zone it stands in as `dimension`, `layer` and `localIndex`."; return false; }
        if (!row.TryGetProperty("room", out var definition) || definition.ValueKind != JsonValueKind.Object)
        { reason = "A room locator carries the room definition it names."; return false; }
        if (!Known(definition, out unknown, "id", "revision"))
        { reason = "Unknown room definition field `" + unknown + "`."; return false; }
        if (!Text(definition, "id", TriggerZoneRoom.MaximumRoomIdChars, out var id) || !Identity(id!))
        { reason = "A room definition id is at most " + TriggerZoneRoom.MaximumRoomIdChars
            + " characters without whitespace or a path separator."; return false; }
        if (!Text(definition, "revision", TriggerZoneRoom.RevisionChars, out var revision) || !IsSha256(revision!))
        { reason = "A room definition revision is one lower-case SHA-256."; return false; }
        if (!Text(row, "sourcePrefab", TriggerZoneRoom.MaximumPrefabChars, out var prefab) || !IsPrefab(prefab!))
        { reason = "A room locator carries a complete native `Assets/` prefab identity."; return false; }
        room = new TriggerZoneRoom(zoneAuthorId!, dimension, layer, localIndex, id!, revision!, prefab!);
        return true;
    }

    /// <summary>One required zone coordinate: a JSON integer in the range the level itself addresses zones by. A
    /// fractional or negative coordinate names no zone, so it is refused at the document instead of being rounded
    /// into one that exists.</summary>
    private static bool Coordinate(JsonElement row, string field, int minimum, int maximum, out int value)
    {
        value = 0;
        if (!row.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var parsed) || parsed < minimum || parsed > maximum) return false;
        value = parsed;
        return true;
    }

    /// <summary>Whether every property of an object is one of the fields this grammar declares, so a mistyped field
    /// is refused instead of being read as its own default.</summary>
    private static bool Known(JsonElement row, out string? unknown, params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject())
            if (!names.Contains(property.Name)) { unknown = property.Name; return false; }
        unknown = null;
        return true;
    }

    private static bool Text(JsonElement row, string field, out string? value) => Text(row, field, 256, out value);

    /// <summary>One required text field of at most <paramref name="maximum"/> characters: not empty, not padded,
    /// and no control character, so a value this reads is one a diagnostic can print.</summary>
    private static bool Text(JsonElement row, string field, int maximum, out string? value)
    {
        value = null;
        if (!row.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.String) return false;
        var text = property.GetString();
        if (text == null || text.Length == 0 || text.Length > maximum || text.Trim() != text) return false;
        foreach (var character in text) if (character < 32 || character == 127) return false;
        value = text;
        return true;
    }

    /// <summary>The site's own zone identity grammar for one authored id: an alphanumeric first character and no
    /// whitespace, so the id a locator carries is one a level's own address can be compared against.</summary>
    private static bool AuthorId(string value)
    {
        if (value.Length == 0 || value.Length > 64 || !AsciiLetterOrDigit(value[0])) return false;
        foreach (var character in value)
            if (!(AsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-')) return false;
        return true;
    }

    /// <summary>ASCII letter or digit. Spelled here rather than with `char.IsAsciiLetterOrDigit`, which is a
    /// .NET 7 member this package's `net6.0` target does not have.</summary>
    private static bool AsciiLetterOrDigit(char character)
        => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    /// <summary>A room definition id: bounded, no control character and no path separator, because the id is what a
    /// resolver matches against the level's own object identity.</summary>
    private static bool Identity(string value)
    {
        if (value.Length == 0 || value.Length > TriggerZoneRoom.MaximumRoomIdChars) return false;
        foreach (var character in value)
            if (character < 32 || character == 127 || char.IsWhiteSpace(character) || character is '/' or '\\') return false;
        return value.Trim() == value;
    }

    /// <summary>One SHA-256 in lower-case hex: the room definition's revision, which is what makes the reference
    /// name the revision the author exported rather than any later one.</summary>
    private static bool IsSha256(string value)
    {
        if (value.Length != TriggerZoneRoom.RevisionChars) return false;
        foreach (var character in value)
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>A complete, unchanged native prefab identity: an `Assets/` path with no separator of another kind
    /// and no empty, `.` or `..` segment. A prefab's display name is not this identity, which is why the whole path
    /// is carried and checked instead of being matched loosely.</summary>
    private static bool IsPrefab(string value)
    {
        if (value.Length == 0 || value.Length > TriggerZoneRoom.MaximumPrefabChars
            || !value.StartsWith("Assets/", StringComparison.Ordinal) || value.Contains('\\')) return false;
        foreach (var character in value) if (char.IsControl(character)) return false;
        foreach (var part in value.Split('/')) if (part is "" or "." or "..") return false;
        return true;
    }

    private static bool Numbers(JsonElement row, string field, int count, double minimum, double maximum, out double[]? values)
    {
        values = null;
        if (!row.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() != count) return false;
        var parsed = new double[count];
        var index = 0;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number)
                || number < minimum || number > maximum) return false;
            parsed[index++] = number;
        }
        values = parsed;
        return true;
    }

    /// <summary>Whether four components are one unit quaternion within the tolerance every rotation in this family
    /// is accepted at. A rotation that is not one is not a rotation, in a document or in a resolved room pose.</summary>
    internal static bool IsUnitQuaternion(double[] rotation)
    {
        var length = Math.Sqrt(rotation[0] * rotation[0] + rotation[1] * rotation[1]
            + rotation[2] * rotation[2] + rotation[3] * rotation[3]);
        return Math.Abs(length - 1) <= TriggerZone.RotationTolerance;
    }

    private static bool TryShape(string text, out TriggerZoneShape shape)
    {
        switch (text)
        {
            case "box": shape = TriggerZoneShape.Box; return true;
            case "sphere": shape = TriggerZoneShape.Sphere; return true;
            case "capsule": shape = TriggerZoneShape.Capsule; return true;
            default: shape = TriggerZoneShape.Box; return false;
        }
    }

    private static bool TryWho(string text, out TriggerZoneWho who)
    {
        switch (text)
        {
            case "player": who = TriggerZoneWho.Player; return true;
            case "enemy": who = TriggerZoneWho.Enemy; return true;
            case "both": who = TriggerZoneWho.Both; return true;
            default: who = TriggerZoneWho.Both; return false;
        }
    }

    private static bool Refuse(out IReadOnlyList<TriggerZone>? zones, out string? code, out string? reason, string refused, string message)
    {
        zones = null; code = refused; reason = message;
        return false;
    }
}

/// <summary>
/// The address of one trigger zone: the one map-object namespace, this category, and the zone's own authored id as
/// the address key. A zone is not placed by the level generator, so it carries none of the level coordinates a door
/// or a terminal address is built from — it is placed by the author, and the id the author wrote is the whole
/// identity. The three coordinate fields are the constant `0` every zone address writes, which keeps one address
/// shape across the categories while the key stays the zone's own name.
///
/// The key is what makes a mount work: a plan's `map-object` attachment carries this category and a zone id, and
/// the `map-object` matcher compares that address with the address the publishing zone re-read from itself, so a
/// plan mounted on one zone is dispatched for that zone and for no other.
/// </summary>
public static class TriggerZoneAddress
{
    /// <summary>The category every trigger-zone address carries.</summary>
    public static readonly string Category = "trigger_zone";

    /// <summary>The one coordinate a zone address writes. A zone is not addressed by level coordinates, so the
    /// three fields carry this constant rather than an invented dimension, layer or local index.</summary>
    private const string ZoneCoordinate = "0";

    /// <summary>Whether a text is a zone id this address can carry. The id is the address's key, so it is the
    /// object's whole name inside the namespace: an empty key, a key with a `/` — which the address format reads as
    /// the next segment — and a key with a `:` — which an entity id reads as the namespace's own separator — are
    /// refused at the document instead of being published as an address nothing can match.</summary>
    public static bool IsZoneId(string? id)
        => !string.IsNullOrEmpty(id) && id.IndexOf('/') < 0 && id.IndexOf(':') < 0 && id != Category;

    /// <summary>Reads a zone address: this category, three constant coordinates and a zone id. Anything else —
    /// another category, another shape, an empty or unnameable key — is not this category's address.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, IsZoneId);

    /// <summary>The address of one zone, or null when the id is not one the address can carry. A caller that gets
    /// null never had an address to publish: the document refuses the same id at load.</summary>
    public static MapObjectReference? Of(string? zoneId)
        => IsZoneId(zoneId)
            ? new MapObjectReference(Category, ZoneCoordinate, ZoneCoordinate, ZoneCoordinate, zoneId!)
            : null;
}

/// <summary>
/// The two trigger rows of this family, declared the way every other Map row is: the capability row, its binding
/// row, and the registration row. A zone is a map object of this provider, so both rows are published through the
/// one map-object half — the event's subject is the zone and its `zone` port is the same object — and a plan is
/// mounted on the zone instead of on a volume id a second mechanism would match.
///
/// Both rows declare no inputs and no parameters, because that is what a trigger row is in this runtime: the plan
/// loader refuses a trigger node whose contract declares an input or a structural parameter (`trigger-shape`), so a
/// row carrying either would make every plan that uses it unloadable. What the author chooses instead lives on the
/// zone object itself — its shape, size, position, rotation, `who` and `blocksPlayers`.
/// </summary>
public static class TriggerZoneContract
{
    /// <summary>The Map provider these rows belong to: the same provider id the Map declaration names, and the
    /// owner every capability and binding row below is registered under. A registration refuses a row whose owner
    /// is not a declared provider, so a mismatch here fails at load rather than drifting quietly.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    public const string EnteredCapability = "forge.trigger.spatial.entered";
    public const string ExitedCapability = "forge.trigger.spatial.exited";
    public const string EnteredFact = "trigger_zone.entered";
    public const string ExitedFact = "trigger_zone.exited";

    /// <summary>The catalog's domain list for these rows: a zone is placed by the map author and can be wired from
    /// map, room, enemy, weapon, tool and consumable behaviours alike.</summary>
    internal static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable" };

    /// <summary>Every trigger row, in declaration order, as (fact, capability).</summary>
    public static readonly IReadOnlyList<(string Fact, string Capability)> Rows = Array.AsReadOnly(new[]
    {
        (EnteredFact, EnteredCapability),
        (ExitedFact, ExitedCapability)
    });

    /// <summary>The binding id of one capability: this provider's own suffix under its own namespace, exactly as
    /// the other Map facts spell it.</summary>
    public static string Binding(string capabilityId)
        => ProviderId + ".binding." + Suffix(capabilityId);

    /// <summary>The binding one fact is published through, by the fact's own name. A fact outside the two rows is
    /// refused by name rather than published on a binding nothing declared.</summary>
    public static string BindingOf(string fact) => Binding(Rows.Single(row => row.Fact == fact).Capability);

    internal static string Suffix(string capabilityId)
        => capabilityId.StartsWith("forge.trigger.", StringComparison.Ordinal)
            ? capabilityId["forge.trigger.".Length..]
            : throw new RuntimeContractException("trigger-zone-capability", capabilityId);

    /// <summary>One capability row: the catalog's own label and description, this build's `host` execution, the
    /// three ports the fact really publishes and no authoring parameter at all. `zone` is the zone entity itself,
    /// exactly as `target` is the entity that walked in or out.</summary>
    public static object Row(string capability, string label, string description)
        => new
        {
            id = capability, owner = ProviderId, kind = "trigger", label, version = "2.0.0",
            parameters = new { description },
            graph = new
            {
                domains = Domains, execution = "host", inputs = Array.Empty<object>(),
                outputs = new object[]
                {
                    new { id = "next", type = "execution" },
                    new { id = "target", type = "entity" },
                    new { id = "zone", type = "entity" }
                },
                parameters = Array.Empty<object>()
            }
        };

    /// <summary>The two capability rows in <see cref="Rows"/> order.</summary>
    public static object[] CapabilityRows() => new object[]
    {
        Row(EnteredCapability, "目标进入触发区", "目标走进了你指定的触发区。"),
        Row(ExitedCapability, "目标离开触发区", "目标走出了你指定的触发区。")
    };

    /// <summary>One binding row: an observation the host publishes, so it carries no handler beyond the fact name
    /// and no result row.</summary>
    public static object BindingRow(string capability)
        => new
        {
            id = Binding(capability), capabilityId = capability, providerId = ProviderId,
            handler = "gtfo.map." + Suffix(capability).Replace('.', '_'), role = "observe", status = "implemented",
            dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
        };

    public static object[] BindingRows() => Rows.Select(row => BindingRow(row.Capability)).ToArray();

    /// <summary>The registration row of one row. A zone is the author's own placed object, read from the package
    /// document and the zone table built from it; no native object of the level is read, so the row declares no
    /// permission: a plan needs none to be told that somebody walked into a volume it placed.</summary>
    public static BindingSupport Support(string capability)
        => new(Binding(capability), "implementation-only", Array.Empty<string>());

    public static BindingSupport[] Supports() => Rows.Select(row => Support(row.Capability)).ToArray();

    /// <summary>The zones of one level, in the document's own order. A zone placed in another level names a volume
    /// this world does not have.</summary>
    public static IReadOnlyList<TriggerZone> ForLevel(IReadOnlyList<TriggerZone> zones, MapLevelReference level)
        => Array.AsReadOnly(zones.Where(zone => zone.Level == level).ToArray());
}
