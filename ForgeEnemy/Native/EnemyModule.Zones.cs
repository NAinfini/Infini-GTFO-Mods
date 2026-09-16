using System;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The one kind-level read this provider answers besides a snapshot: where one of its own lives stands.
/// It is the `EntityZones` responder the kernel's own zone read asks, so every zone question about a `gtfo.enemy`
/// — the `where` value row, a zone-filtered selector, another provider comparing places — arrives here and nowhere
/// else.
///
/// The two outcomes a bare null used to collapse into one are told apart by what this responder does with them.
/// A life whose course node names no zone stands outside every zone of this world: that is an answer, and it is
/// the null the kernel reports as `entity-zone-outside`. A life this provider cannot place — one it no longer
/// tracks, one whose course node, zone or layer is missing or was collected mid-read, one whose chain throws —
/// is a read that could not be made, and it is refused with <see cref="ZoneUnknownCode"/> rather than answered as
/// an entity standing nowhere. A selector that read "cannot place" as "outside the zone I asked about" would
/// quietly drop the entity from a set it may well belong to, which is why the refusal has to travel as itself.
/// The level objects can be collected at any point of the chain, so the whole read is one guarded step.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The refusal this provider's zone read makes when it cannot place a life it owns. The kernel
    /// reports the code as this provider spelled it, so an unreadable life stays tellable apart from one that
    /// stands outside every zone.</summary>
    internal const string ZoneUnknownCode = "enemy-zone-unknown";

    /// <summary>The zone one tracked enemy life stands in: the zone its own course node belongs to, named by the
    /// level's three coordinates — the same text the Map provider names a zone with, so a trigger row that filters
    /// a candidate set by zone compares one place rather than two. A course node that names no zone is the one
    /// answer that is not a failure: the life stands outside every zone this world has.</summary>
    private EntityReference? ZoneOfEnemy(EntityReference reference)
    {
        if (Resolve(reference) is not { } entry) throw Unknown("This provider does not track that life.");
        try
        {
            var courseNode = entry.Enemy.CourseNode;
            if (courseNode == null) throw Unknown("This life's course node does not read.");
            var zone = courseNode.m_zone;
            if (zone == null) return null;
            if (zone.WasCollected) throw Unknown("This life's zone was collected before it could be read.");
            var layer = zone.m_layer;
            if (layer == null) throw Unknown("This life's zone names no layer.");
            if (layer.WasCollected) throw Unknown("This life's zone layer was collected before it could be read.");
            return RuntimeZones.Reference(_kernel.WorldEpoch, (int)zone.m_dimensionIndex, (int)layer.m_type,
                (int)zone.LocalIndex);
        }
        // The refusal above travels as its own code; anything else the native chain throws is the same unreadable
        // answer rather than a second kind of failure.
        catch (RuntimeContractException) { throw; }
        catch (Exception error) { throw Unknown("This life's zone does not read: " + error.GetType().Name); }
    }

    private static RuntimeContractException Unknown(string detail) => new(ZoneUnknownCode, detail);
}
