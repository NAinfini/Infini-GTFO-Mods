using System;
using System.Globalization;

namespace ForgeWeapon;

/// <summary>Which of this provider's two tables answers for a reference, decided by the id alone.</summary>
public enum EquipmentEntityShape
{
    /// <summary>Not an id this provider issues.</summary>
    None = 0,
    /// <summary>A backpack equipment life.</summary>
    Life = 1,
    /// <summary>One world instance placed by a backpack equipment life.</summary>
    DeployedInstance = 2
}

/// <summary>Every reference Weapon owns lives in the one `gtfo.equipment` namespace, in one of two disjoint
/// shapes: a backpack equipment life `gtfo.equipment:&lt;world&gt;.&lt;life&gt;`, and one world instance that
/// life placed, `gtfo.equipment:&lt;world&gt;.&lt;life&gt;.&lt;instance&gt;`. One equipment life can place and
/// recall several world instances, so the instance number counts the placements of that one life and a
/// deployment id always begins with the life id that placed it. The rule is the segment count, so the resolver
/// and the observer of the single namespace can send each reference to the table its own shape names.</summary>
public static class EquipmentIdentityId
{
    public const string Namespace = "gtfo.equipment";
    public const string Prefix = Namespace + ":";

    /// <summary>A backpack equipment life: world and life are this adapter's own per-world sequences.</summary>
    public static string Life(long world, long life) => Prefix + Number(world) + "." + Number(life);

    /// <summary>One world instance placed by <paramref name="lifeId"/>, numbered inside that life.</summary>
    public static string DeployedInstance(string lifeId, long instance) => lifeId + "." + Number(instance);

    public static EquipmentEntityShape ShapeOf(string? id)
    {
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return EquipmentEntityShape.None;
        var rest = id.AsSpan(Prefix.Length);
        var first = rest.IndexOf('.');
        if (first <= 0 || !Digits(rest[..first])) return EquipmentEntityShape.None;
        rest = rest[(first + 1)..];
        var second = rest.IndexOf('.');
        if (second < 0) return Digits(rest) ? EquipmentEntityShape.Life : EquipmentEntityShape.None;
        return Digits(rest[..second]) && Digits(rest[(second + 1)..])
            ? EquipmentEntityShape.DeployedInstance : EquipmentEntityShape.None;
    }

    private static bool Digits(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return false;
        foreach (var c in value) if (c is < '0' or > '9') return false;
        return true;
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
