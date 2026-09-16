using System;
using System.Collections.Generic;

namespace ForgeMap;

/// <summary>
/// The one question only the generated level can answer about an authored room: where that room ended up. A zone's
/// document carries the room's locator and its own pose in the room's local coordinates, so a zone becomes a volume
/// in the world exactly when this answers with the room's world pose.
///
/// The answer is a delegate because the knowledge lives with the level, not with this assembly: the resolver that
/// already reads a package's object references against the level the game generated owns the identity rules, and a
/// second lookup written here would be a second answer to which native object an authored room names. Returning
/// null is "this level holds no single room that reference names", which is a refusal and never a guess.
/// </summary>
/// <param name="room">The authored room reference a zone carries.</param>
/// <returns>The room's world pose, or null when this resolver cannot name exactly one room for the reference.</returns>
public delegate TriggerZoneRoomPose? TriggerZoneRoomLookup(TriggerZoneRoom room);

/// <summary>
/// The one step between an authored zone and a judged one: a zone's document pose is the room's local coordinates,
/// and only the room's own world pose turns it into a volume. Placement is a fold over a level, not a mutation: the
/// authored table stays what the document said, and every pass answers the table this level can judge.
///
/// A zone whose room cannot be resolved is refused by name and left out of the answer, because a zone judged in
/// coordinates nobody converted is a volume at the wrong place, and a wrong volume is worse than a missing one.
/// Zones of another level are carried through untouched: their own level places them when it is generated.
/// </summary>
public static class TriggerZonePlacement
{
    /// <summary>The code every room refusal carries, so one report line names the same thing whatever the reason
    /// was: the zone, its room, and that it is not judged.</summary>
    public const string RefusedCode = "trigger-zone-room";

    /// <summary>The zones of one level in world coordinates, in the document's own order. A zone that already
    /// carries a world pose and a zone of another level answer themselves; every other zone is placed through
    /// <paramref name="rooms"/>, or refused by name through <paramref name="refused"/> and left out.</summary>
    public static IReadOnlyList<TriggerZone> Of(IReadOnlyList<TriggerZone> zones, MapLevelReference level,
        TriggerZoneRoomLookup? rooms, Action<string> refused)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(refused);
        var placed = new List<TriggerZone>(zones.Count);
        foreach (var zone in zones)
        {
            if (zone.Placed || zone.Level != level) { placed.Add(zone); continue; }
            if (rooms == null)
            {
                refused(Refusal(zone, "no room resolver is installed in this build"));
                continue;
            }
            TriggerZoneRoomPose? pose;
            try { pose = rooms(zone.Room); }
            catch (Exception error) { refused(Refusal(zone, "the room resolver failed (" + error.GetType().Name + ")")); continue; }
            if (pose is not { } room)
            {
                refused(Refusal(zone, "this level holds no single room matching `" + zone.Room.SourcePrefab + "`"));
                continue;
            }
            try { placed.Add(zone.PlacedIn(room)); }
            catch (Exception error)
            {
                refused(Refusal(zone, "the resolved room pose cannot be used (" + error.GetType().Name + ": " + error.Message + ")"));
            }
        }
        return Array.AsReadOnly(placed.ToArray());
    }

    private static string Refusal(TriggerZone zone, string reason)
        => RefusedCode + ": zone `" + zone.Id + "` in room " + zone.Room + " is not judged because " + reason + ".";
}
