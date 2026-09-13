using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeMap;

// Bounded structural identity bookkeeping. Only the session performs live probes.
// This index is not a registry, scheduler, native hook or complete world inventory.
internal sealed class MapObjectIdentityIndex
{
    private enum State { Pending, Bound, Retired, Quarantined }
    private sealed class Entry
    {
        internal Entry(MapCreationTicket ticket) { Ticket = ticket; }
        internal MapCreationTicket Ticket { get; }
        internal State Status;
        internal MapNativeIdentity? Native;
    }
    private readonly object owner = new();
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<MapObjectAddress, Entry> addresses = new();
    private readonly Dictionary<string, Entry> entities = new(StringComparer.Ordinal);
    private readonly Dictionary<MapNativeIdentity, Entry> nativeObjects = new();
    private readonly int maxEntries;
    private long nextId;
    private bool blocked;
    internal long WorldEpoch { get; private set; }
    internal long Generation { get; private set; }
    internal int ObservedCount { get; private set; }
    internal int HistoryCount => addresses.Count;
    internal int NativeKeyCount => nativeObjects.Count;
    internal long GapCount { get; private set; }
    internal MapObservationGap? LastGap { get; private set; }

    internal MapObjectIdentityIndex(int maxEntries = 4096)
    {
        Check(maxEntries is > 0 and <= 16384, "invalid-limits");
        this.maxEntries = maxEntries;
    }
    internal void BeginWorld(long epoch)
    {
        Check(epoch >= WorldEpoch && epoch <= RuntimeJson.MaxSafeInteger, "stale-world");
        if (epoch == WorldEpoch) return;
        Invalidate(); WorldEpoch = epoch;
    }
    internal void BeginGeneration()
    { Check(WorldEpoch > 0, "world-unavailable"); Invalidate(); }
    internal void Invalidate()
    {
        Check(Generation < RuntimeJson.MaxSafeInteger, "generation-exhausted");
        Generation++;
        addresses.Clear(); entities.Clear(); nativeObjects.Clear();
        ObservedCount = 0; GapCount = 0; LastGap = null; blocked = false;
    }
    internal void MarkGap(MapObservationGap reason)
    {
        Check(Enum.IsDefined(typeof(MapObservationGap), reason), "invalid-gap");
        if (GapCount < RuntimeJson.MaxSafeInteger) GapCount++;
        LastGap = reason;
    }
    internal MapCreationTicket BeginCreation(MapObjectAddress address, MapSourceLock source)
    {
        ValidateAddress(address); ValidateSource(source);
        Check(WorldEpoch > 0 && Generation > 0, "world-unavailable");
        Check(!blocked, "generation-blocked");
        addresses.TryGetValue(address, out var previous);
        if (previous != null)
        {
            Check(previous.Status == State.Retired, "address-already-tracked");
            Check(previous.Ticket.Value.Source == source, "source-lock-changed");
        }
        else if (addresses.Count >= maxEntries)
        { MarkGap(MapObservationGap.CapacityExceeded); Fail("identity-budget"); }
        var life = previous == null ? 1 : previous.Ticket.Value.Entity.LifeEpoch + 1;
        Check(life <= RuntimeJson.MaxSafeInteger && nextId < RuntimeJson.MaxSafeInteger, "identity-exhausted");
        var id = previous?.Ticket.Value.Entity.Id ?? "gtfo.map:" + sessionId + ":" + (++nextId).ToString(CultureInfo.InvariantCulture);
        var reference = new EntityReference(id, WorldEpoch, life);
        var ticket = new MapCreationTicket(owner, Generation, new MapIdentitySnapshot(reference, address, source));
        var entry = new Entry(ticket);
        addresses[address] = entry; entities[id] = entry;
        return ticket;
    }
    private Entry FindTicket(MapCreationTicket ticket, bool allowRetired = false)
    {
        Check(ticket != null && ReferenceEquals(ticket.Owner, owner), "foreign-ticket");
        Check(ticket!.Value.Entity.WorldEpoch == WorldEpoch, "stale-world");
        Check(ticket.Generation == Generation, "stale-generation");
        Check(!blocked, "generation-blocked");
        Check(entities.TryGetValue(ticket.Value.Entity.Id, out var entry)
            && ReferenceEquals(entry.Ticket, ticket), "stale-life");
        Check(entry!.Status != State.Quarantined, "ambiguous-identity");
        Check(allowRetired || entry.Status != State.Retired, "retired-life");
        return entry;
    }
    internal void ValidateTicket(MapCreationTicket ticket) => _ = FindTicket(ticket);
    internal MapIdentityReceipt ObserveCreated(MapCreationTicket ticket, MapCreationObservation observation)
    {
        var entry = FindTicket(ticket);
        Check(observation != null, "invalid-observation");
        ValidateAddress(observation!.Address); ValidateNative(observation.Native);
        if (observation.Source == null)
        { MarkGap(MapObservationGap.UnknownSource); Quarantine(entry); Fail("unknown-source"); }
        ValidateSource(observation.Source!);
        if (observation.Address != ticket.Value.Address)
        { Quarantine(entry); Fail("address-mismatch"); }
        if (observation.Source != ticket.Value.Source)
        { Quarantine(entry); Fail("source-mismatch"); }
        if (nativeObjects.TryGetValue(observation.Native, out var other) && !ReferenceEquals(entry, other))
        { Quarantine(entry); Quarantine(other); Fail("native-claimed"); }
        if (entry.Status == State.Bound)
        {
            if (entry.Native == observation.Native)
                return new MapIdentityReceipt("map.identity.duplicate", ticket.Value.Entity);
            ReserveNative(observation.Native, entry);
            Quarantine(entry); Fail("ambiguous-token");
        }
        ReserveNative(observation.Native, entry);
        entry.Native = observation.Native; entry.Status = State.Bound; ObservedCount++;
        return new MapIdentityReceipt("map.identity.observed", ticket.Value.Entity);
    }
    private void ReserveNative(MapNativeIdentity key, Entry entry)
    {
        if (!nativeObjects.ContainsKey(key) && nativeObjects.Count >= maxEntries * 2)
        {
            blocked = true; MarkGap(MapObservationGap.CapacityExceeded); Fail("native-key-budget");
        }
        nativeObjects[key] = entry;
    }
    private void Quarantine(Entry entry)
    {
        if (entry.Status == State.Bound) ObservedCount--;
        entry.Status = State.Quarantined;
        // Retain claimed native keys until invalidation; never pick another winner.
    }
    internal bool CancelCreation(MapCreationTicket ticket)
    {
        var entry = FindTicket(ticket, allowRetired: true);
        if (entry.Status == State.Retired) return false;
        Check(entry.Status == State.Pending, "creation-already-observed");
        entry.Status = State.Retired; return true;
    }
    internal bool Retire(MapCreationTicket ticket, MapNativeIdentity native)
    {
        var entry = FindTicket(ticket, allowRetired: true); ValidateNative(native);
        Check(entry.Native == native, "destruction-object-mismatch");
        if (entry.Status == State.Retired) return false;
        Check(entry.Status == State.Bound, "not-observed");
        nativeObjects.Remove(native); entry.Status = State.Retired; ObservedCount--;
        return true;
    }
    internal bool TryGetObserved(EntityReference reference, out MapCreationTicket? ticket,
        out MapNativeIdentity native, out string code)
    {
        ticket = null; native = default; code = "map.identity.not-observed";
        if (reference == null || string.IsNullOrEmpty(reference.Id))
        { code = "map.identity.invalid-reference"; return false; }
        if (reference.WorldEpoch != WorldEpoch)
        { code = "map.identity.stale-world"; return false; }
        if (blocked) { code = "map.identity.generation-blocked"; return false; }
        if (!entities.TryGetValue(reference.Id, out var entry)) return false;
        if (reference != entry.Ticket.Value.Entity)
        { code = "map.identity.stale-life"; return false; }
        if (entry.Status != State.Bound)
        {
            code = entry.Status == State.Quarantined ? "map.identity.ambiguous-identity"
                : entry.Status == State.Retired ? "map.identity.retired-life" : "map.identity.not-observed";
            return false;
        }
        ticket = entry.Ticket; native = entry.Native!.Value;
        code = "map.identity.observed"; return true;
    }
    internal static void ValidateNative(MapNativeIdentity value)
        => Check(value.Pointer > 0 && value.UnityInstanceId != 0, "invalid-native-identity");
    private static void ValidateAddress(MapObjectAddress value)
    {
        Check(value != null, "invalid-address");
        Check(Text(value!.LayoutId) && Text(value.LayoutRevision) && Text(value.PlacementId)
            && Text(value.ObjectId), "invalid-address");
        Check(value.Dimension >= 0 && value.Layer >= 0 && value.LocalZoneIndex >= 0,
            "invalid-zone-address");
        Check(Enum.IsDefined(typeof(MapObjectKind), value.Kind), "invalid-object-kind");
    }
    private static void ValidateSource(MapSourceLock value)
    {
        Check(value != null, "invalid-source");
        Check(Text(value!.ResourceId) && Text(value.ResourceRevision)
            && Text(value.SourceFile, 1024), "invalid-source");
        Check(value.SourceIdentityHash != null && value.SourceIdentityHash.Length == 64
            && value.SourceIdentityHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "invalid-source-hash");
        Check(long.TryParse(value.SourcePathId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pathId)
            && pathId.ToString(CultureInfo.InvariantCulture) == value.SourcePathId, "invalid-source-path-id");
    }
    private static bool Text(string? text, int max = 256) => text != null && text.Length > 0
        && text.Length <= max && text.Trim() == text && text.All(c => !char.IsControl(c));
    internal static void Check(bool condition, string code)
    { if (!condition) Fail(code); }
    private static void Fail(string code) => throw new RuntimeContractException("map.identity." + code, code);
}
