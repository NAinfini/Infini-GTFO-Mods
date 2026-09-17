using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

/// <summary>One authored destination of a dimension move: where a player lands and which way they face when they
/// get there. Both are the same two vectors the game's own warp entry takes, so nothing is converted here.</summary>
public sealed record DimensionDestination(double PositionX, double PositionY, double PositionZ,
    double LookX, double LookY, double LookZ)
{
    public float[] Position => new[] { (float)PositionX, (float)PositionY, (float)PositionZ };
    public float[] LookDirection => new[] { (float)LookX, (float)LookY, (float)LookZ };
}

/// <summary>
/// The destination table of one dimension move, read from the row's two `positions` and `look_dirs` collections.
/// It is the EOS dimension-warp feature's own shape — a list of position-and-look entries — folded into
/// `forge.action.player.dimension`, and it keeps that feature's rule for a team larger than the list: the
/// destinations are used in order and start over, so a four-player team with two destinations puts two players on
/// each one. Order is the recipient order the kernel resolves, which is why the table is a collection and not a
/// set.
///
/// The entries travel as two collections rather than one table parameter because this runtime has no list
/// parameter type: `vector3` with `cardinality = "many"` is the one collection form a plan can author (`cf.
/// forge.control.flow.for_each_position`'s own `positions` port), and both halves are zipped by index here. The
/// two collections must therefore be the same length — a table this layer cannot pair exactly is refused rather
/// than truncated.
///
/// Reading is strict on purpose: a table this module cannot place exactly is refused with one code instead of
/// being truncated to the entries that happened to parse, because "some players were not moved" is a different
/// request from the one the author wrote.
/// </summary>
public static class DimensionDestinations
{
    /// <summary>No destination was authored at all.</summary>
    public const string EmptyCode = "dimension-locations-empty";
    /// <summary>The table, or one of its entries, is not a position and a look direction.</summary>
    public const string InvalidCode = "dimension-locations-invalid";
    /// <summary>The most destinations one move may carry. The kernel's fact budget is 128 recipients, and a table
    /// past this size is a data error rather than a bigger move.</summary>
    public const int Maximum = 128;

    /// <summary>The entries, in authored order, or the one code that names why the table was refused. Both
    /// collections are the row's own ports and must be present and the same length.</summary>
    public static bool TryRead(JsonElement positions, JsonElement lookDirs,
        out IReadOnlyList<DimensionDestination> destinations, out string code)
    {
        destinations = Array.Empty<DimensionDestination>();
        code = InvalidCode;
        if (positions.ValueKind != JsonValueKind.Array || lookDirs.ValueKind != JsonValueKind.Array) return false;
        var positionRows = positions.EnumerateArray().ToArray();
        var lookRows = lookDirs.EnumerateArray().ToArray();
        if (positionRows.Length != lookRows.Length) return false;
        var rows = new List<DimensionDestination>(positionRows.Length);
        for (var index = 0; index < positionRows.Length; index++)
        {
            if (!Vector(positionRows[index], out var position)) return false;
            if (!Vector(lookRows[index], out var look)) return false;
            rows.Add(new DimensionDestination(position[0], position[1], position[2], look[0], look[1], look[2]));
        }
        if (rows.Count == 0) { code = EmptyCode; return false; }
        if (rows.Count > Maximum) return false;
        destinations = rows.AsReadOnly();
        code = "";
        return true;
    }

    /// <summary>The destination for the recipient at <paramref name="index"/>, counting from zero: the authored
    /// order, reused from the top once the table runs out.</summary>
    public static DimensionDestination At(IReadOnlyList<DimensionDestination> destinations, int index)
        => destinations[index % destinations.Count];

    /// <summary>One entry of a collection: an array of exactly three finite numbers, which is what the runtime's
    /// own `vector3` collection form holds. A member that is itself an object with `x`/`y`/`z` is deliberately not
    /// accepted — one spelling keeps the plan encoding and this reader in agreement.</summary>
    private static bool Vector(JsonElement entry, out float[] value)
    {
        value = Array.Empty<float>();
        if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 3) return false;
        var read = new float[3];
        var index = 0;
        foreach (var part in entry.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Number || !part.TryGetDouble(out var number)) return false;
            if (!double.IsFinite(number)) return false;
            read[index++] = (float)number;
        }
        value = read;
        return true;
    }
}
