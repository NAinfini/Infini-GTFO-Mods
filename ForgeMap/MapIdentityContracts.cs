using System;
using ForgeRuntime.Framework;

namespace ForgeMap;

// Domain-local staging types, NOT a new content/export schema or public SDK.
// Only source-backed static world objects are in this batch; no player life policy.
internal enum MapObjectKind { Zone, Geomorph, Area, Plug, Door, Terminal, Scan }
// LayoutId is the main-level LevelLayoutDataBlock persistentID; ObjectId and Kind stay local to the object
// the adapter actually created.
internal sealed record MapObjectAddress(string LayoutId, string LayoutRevision,
    int Dimension, int Layer, int LocalZoneIndex, string PlacementId,
    string ObjectId, MapObjectKind Kind);

// SourceIdentityHash pins the full, already-validated source evidence, not preview bytes.
// SourceFile/SourcePathId preserve the website's exact SourceObjectIdentity pair.
// A trusted future adapter must supply this from locked definitions and real callbacks.
internal sealed record MapSourceLock(string ResourceId, string ResourceRevision,
    string SourceIdentityHash, string SourceFile, string SourcePathId);

// Never serialize a pointer into a resource ID, entity ID, receipt or content package.
internal readonly record struct MapNativeIdentity(long Pointer, int UnityInstanceId);
internal sealed record MapCreationObservation(MapObjectAddress Address,
    MapSourceLock? Source, MapNativeIdentity Native);
internal sealed record MapIdentitySnapshot(EntityReference Entity,
    MapObjectAddress Address, MapSourceLock Source);
internal sealed record MapIdentityReceipt(string Code, EntityReference Entity);
internal enum MapObservationGap { UnknownSource, CapacityExceeded, CallbackLost }

// Tickets are issued before a creation attempt and carried by that exact callback.
// Never recover a missing ticket by searching name, pointer, position or array order.
internal sealed class MapCreationTicket
{
    internal MapCreationTicket(object owner, long generation, MapIdentitySnapshot value)
    { Owner = owner; Generation = generation; Value = value; }
    internal object Owner { get; }
    internal long Generation { get; }
    internal MapIdentitySnapshot Value { get; }
}
