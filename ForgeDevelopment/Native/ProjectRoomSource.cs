using System.Collections.Generic;
using ForgeRuntime.Framework;
using HostPlugin = ForgeRuntime.Plugin;

namespace ForgeDevelopment.Native;

/// <summary>
/// The game-bound half of the development diagnostics' room resolution: the adapter that hands the one room
/// resolver to the scan, translated into the plain answer the scan speaks.
///
/// The rule itself lives with the map provider that registered Runtime's authoring-room resolver: it names an
/// authored room by the prefab object its `Assets/` path loads and the native zone the locator carries, so this file owns no
/// matching, no prefab identity and no second answer to the same question. It exists only because the scan is
/// the game-independent half of the report and must not name a native type: a build that installs no adapter
/// leaves the scan with no resolver, and a scan with no resolver refuses every authored room instead of
/// substituting a rule of its own.
///
/// The resolver's own refusal reason is translated, not swallowed: a zone the level does not have, a zone that
/// holds no such room and a build that cannot ask the question at all are three different things, and the report
/// says which one happened.
/// </summary>
internal static class ProjectRoomSource
{
    /// <summary>Every generated room this world holds for one authored room reference inside one zone, or the one
    /// reason the reference names no room of it. A refusal that counted several rooms is carried as those rooms,
    /// so the scan's own cardinality rule reports them as the candidates an author can look at.</summary>
    internal static ProjectRoomAnswer Resolve(long worldEpoch, string sourcePrefab, ProjectRoomScope zone)
    {
        var runtime = HostPlugin.Runtime;
        if (runtime == null) return ProjectRoomAnswer.Refused(ProjectReferenceReason.CreationContextUnverified);
        var answer = runtime.ResolveAuthoringRoom(worldEpoch, sourcePrefab,
            new AuthoringRoomScope(zone.Dimension, zone.Layer, zone.LocalZoneIndex));
        if (answer.Rooms.Count == 0) return ProjectRoomAnswer.Refused(Reason(answer.Refusal));
        var hits = new List<ProjectRoomHit>(answer.Rooms.Count);
        foreach (var room in answer.Rooms)
            hits.Add(new ProjectRoomHit(room.GeomorphInstanceId, room.ZoneInstanceId));
        return ProjectRoomAnswer.Answered(hits.AsReadOnly());
    }

    /// <summary>The report's own word for one refusal. The two room-specific reasons keep their own codes; a world
    /// the resolver cannot be asked in and a path that loads no prefab object remain "the source identity of this
    /// reference could not be established", which is what they are. An empty answer the resolver did not refuse is
    /// still no candidate: the zone held no such room.</summary>
    private static ProjectReferenceReason Reason(string? refusal) => refusal switch
    {
        AuthoringRoomResolution.ZoneUnresolved => ProjectReferenceReason.ZoneUnresolved,
        AuthoringRoomResolution.NoRoom => ProjectReferenceReason.NoCandidate,
        null => ProjectReferenceReason.NoCandidate,
        _ => ProjectReferenceReason.CreationContextUnverified
    };
}
