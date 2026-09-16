using System;

namespace ForgeDevelopment.Native;

/// <summary>
/// The session side of the snapshot writer: the two calls that put a snapshot and a change record on the `state`
/// channel. It is a separate part of the same type because the record shape is serialized without a session, while
/// these two methods need one.
/// </summary>
internal static partial class CaptureStateWriter
{
    /// <summary>Records one snapshot on the state channel.</summary>
    internal static void Write(CaptureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RecSession.Write(Channel, SnapshotKind, json => WriteSnapshot(json, snapshot));
    }

    /// <summary>Writes the differential snapshot a change pass produces. It is the same channel with a different kind
    /// so a reader can take the periodic snapshots and the per-frame diff separately.</summary>
    internal static void WriteChanges(CaptureChangeResult result, long tick, string scope)
    {
        ArgumentNullException.ThrowIfNull(result);
        RecSession.Write(Channel, ChangesKind, json => WriteChangeBody(json, result, tick, scope));
    }
}
