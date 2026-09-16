using System;
using System.Collections.Generic;

namespace ForgeRuntime.Network;

/// <summary>
/// A committed command's outcome, local to the host. The dedup ledger hands the first outcome back for every repeat
/// of a request, so a retransmitted packet cannot commit twice, and it keeps its own copy instead of asking the
/// runtime kernel for a result it has already consumed.
/// </summary>
internal sealed record NetworkCommandOutcome(string Status, string CommitState, string Code, string Detail, int FactCount)
{
    internal static NetworkCommandOutcome Refused(string code, string detail = "") => new(NetworkCommandStatuses.Rejected, NetworkCommitStates.None, code, detail, 0);
}

/// <summary>The wire statuses, spelled the same way the runtime's command results spell them. A result the host
/// executes becomes one of these on the wire, so a status outside this set is a fault rather than a default.</summary>
internal static class NetworkCommandStatuses
{
    internal const string Succeeded = "succeeded";
    internal const string Partial = "partial";
    internal const string Rejected = "rejected";
    internal const string Failed = "failed";
    internal const string Cancelled = "cancelled";
    internal const string Expired = "expired";
}

/// <summary>Local string spellings of the wire commit states, shared by outcomes and their messages.</summary>
internal static class NetworkCommitStates
{
    internal const string None = "none";
    internal const string Confirmed = "confirmed";
    internal const string Unknown = "unknown";

    internal static byte Encode(string commitState) => commitState switch
    {
        Confirmed => NetworkCommitState.Confirmed,
        Unknown => NetworkCommitState.Unknown,
        _ => NetworkCommitState.None
    };
}

/// <summary>One request as the ledger sees it: the key fields plus the payload the key deliberately excludes.</summary>
internal readonly record struct NetworkRequestKey(ulong SenderSession, long WorldEpoch, string PlanId, ulong EventId);

/// <summary>What the ledger did with one request.</summary>
internal readonly record struct DedupDecision(string Status, string Code, NetworkCommandOutcome? Outcome, NetworkCommandOutcome? Cached)
{
    /// <summary>True only for the first arrival of a key: the caller may execute the command exactly then.</summary>
    internal bool Execute => Status == DedupStatuses.Committed;

    internal bool Accepted => Status == DedupStatuses.Committed || Status == DedupStatuses.Duplicate;
}

/// <summary>The three answers the ledger gives: it ran the request, it already had the result, or it refused.</summary>
internal static class DedupStatuses
{
    internal const string Committed = "committed";
    internal const string Duplicate = "duplicate";
    internal const string Rejected = "rejected";
}

/// <summary>
/// The cross-process dedup ledger. Its key is `(sender session, world epoch, plan id, event id)` — a fixed set of
/// fields the sender owns — so identity never depends on serializing or hashing a payload. The payload is compared
/// only to tell a repeat apart from a reuse of the same event id with different content, which is a conflict rather
/// than a duplicate.
/// <para>
/// The ledger is per world: an epoch change clears every entry, so a request that arrives after its world ended is
/// refused as stale instead of being answered from the previous world's history. Capacity is a hard ceiling: a full
/// ledger refuses new keys with an explicit code rather than evicting an entry and letting a late repeat re-execute.
/// </para>
/// </summary>
internal sealed class NetworkDedupLedger
{
    private sealed class Entry
    {
        internal Entry(byte[] payload, NetworkCommandOutcome outcome)
        {
            Payload = payload;
            Outcome = outcome;
        }

        internal byte[] Payload { get; }
        internal NetworkCommandOutcome Outcome { get; }
    }

    private readonly Dictionary<NetworkRequestKey, Entry> _entries = new();
    private readonly int _capacity;

    internal NetworkDedupLedger(long worldEpoch, int capacity = NetworkProtocol.MaximumLedgerEntries)
    {
        if (capacity <= 0) throw new NetworkContractException(NetworkCodes.LedgerFull, "Ledger capacity must be positive.");
        WorldEpoch = worldEpoch;
        _capacity = capacity;
    }

    /// <summary>The world this ledger belongs to. It only ever moves to a higher epoch.</summary>
    internal long WorldEpoch { get; private set; }

    internal int Count => _entries.Count;
    internal int DuplicateHits { get; private set; }
    internal int Conflicts { get; private set; }
    internal int Refusals { get; private set; }
    internal int StaleRefusals { get; private set; }

    /// <summary>
    /// Decides one request. <paramref name="execute"/> runs only on the first arrival of a key, and its outcome is
    /// what every later repeat of that key receives, byte for byte.
    /// </summary>
    internal DedupDecision Submit(NetworkRequestKey key, byte[] payload, Func<NetworkCommandOutcome> execute)
    {
        if (key.WorldEpoch != WorldEpoch)
        {
            StaleRefusals++;
            return new DedupDecision(DedupStatuses.Rejected, NetworkCodes.StaleWorld, null, null);
        }
        if (_entries.TryGetValue(key, out var existing))
        {
            if (!SamePayload(existing.Payload, payload))
            {
                Conflicts++;
                return new DedupDecision(DedupStatuses.Rejected, NetworkCodes.EventIdConflict, null, null);
            }
            DuplicateHits++;
            // The history answers the repeat: the first outcome, under the code that says why this answer is the
            // first one's rather than a second execution's.
            return new DedupDecision(DedupStatuses.Duplicate, NetworkCodes.Duplicate, null,
                existing.Outcome with { Code = NetworkCodes.Duplicate });
        }
        if (_entries.Count >= _capacity)
        {
            Refusals++;
            return new DedupDecision(DedupStatuses.Rejected, NetworkCodes.LedgerFull, null, null);
        }
        var outcome = execute();
        _entries.Add(key, new Entry(payload, outcome));
        return new DedupDecision(DedupStatuses.Committed, NetworkCodes.Accepted, outcome, null);
    }

    /// <summary>
    /// Moves the ledger to a new world: every key, and every remembered result, belongs to the world it was
    /// committed in and is dropped here. A world may not be reused or rewind.
    /// </summary>
    internal void BeginWorld(long worldEpoch)
    {
        if (worldEpoch <= WorldEpoch) throw new NetworkContractException(NetworkCodes.EpochNotIncreasing, "Ledger epoch " + worldEpoch + " is not above " + WorldEpoch + ".");
        EnterWorld(worldEpoch);
    }

    /// <summary>
    /// Adopts a world the ledger has not been in, which is what a client's handshake does with the host's epoch: it
    /// starts this client in the host's world, and starting there is not a move away from anything.
    /// </summary>
    internal void EnterWorld(long worldEpoch)
    {
        if (worldEpoch < 0) throw new NetworkContractException(NetworkCodes.EpochNotIncreasing, "Ledger epoch " + worldEpoch + " is negative.");
        WorldEpoch = worldEpoch;
        _entries.Clear();
    }

    /// <summary>Drops one departed session's keys, so its results are not answerable to a later session that reuses
    /// the number.</summary>
    internal void Forget(ulong session)
    {
        var stale = new List<NetworkRequestKey>();
        foreach (var key in _entries.Keys) if (key.SenderSession == session) stale.Add(key);
        foreach (var key in stale) _entries.Remove(key);
    }

    private static bool SamePayload(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
        return true;
    }
}
