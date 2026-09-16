using System;
using System.Collections.Generic;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// Dedup-ledger checks: one key commits once, a repeat is answered with the first result, a reused event id with
/// other content is a conflict rather than a duplicate, and a full ledger refuses instead of forgetting.
/// </summary>
internal static class DedupChecks
{
    private static readonly NetworkRequestKey Key = new(2, 1, "forge.plan.alpha", 100);

    internal static void Run(Suite suite)
    {
        LedgerOnly(suite);
        HostPath(suite);
        Retransmission(suite);
    }

    /// <summary>
    /// What the host does with a retransmission: the first arrival executes, the repeat is answered from the ledger
    /// with the first execution's outcome, the world change retires the key, and a request no one can run is refused
    /// without being remembered.
    /// </summary>
    private static void Retransmission(Suite suite)
    {
        suite.Case("dedup: host replay and world change");
        var (host, client) = Harness.Pair();
        Harness.Handshake(host, client);
        var runs = new List<ulong>();
        host.CommandExecutor = (request, payload) => { runs.Add(request.EventId); return Outcome("succeeded", 3); };
        var door = Harness.Request(2, 0, 1, "forge.plan.alpha", "binding.a", "open");
        var first = Harness.Deliver(host, 2, door);
        var replay = Harness.Deliver(host, 2, door);
        suite.Equal(1, runs.Count, "the replay never reaches the executor");
        suite.Equal(first!.Value.FactCount, replay!.Value.FactCount, "the replay carries the first fact count");
        suite.Equal(first.Value.Status, replay.Value.Status, "the replay carries the first status");
        suite.Equal(first.Value.CommitState, replay.Value.CommitState, "the replay carries the first commit state");
        suite.Equal(NetworkCodes.Duplicate, replay.Value.Code, "the replay names itself as a duplicate");

        // The same key after the world moved is a different request: the retired world's history is gone.
        var next = host.AdvanceWorld(1, WorldChangeReason.NewGeneration);
        suite.Equal(1L, next.Epoch.WorldEpoch, "the host moved to the next world");
        suite.Equal(0, host.Ledger.Count, "the world change cleared the ledger");
        var moved = Harness.Deliver(host, 2, Harness.Request(2, 1, 1, "forge.plan.alpha", "binding.a", "open"));
        suite.Equal(2, runs.Count, "a request in the new world executes again");
        suite.Equal(NetworkStatus.Succeeded, moved!.Value.Status, "the new world's request succeeds");
        suite.Equal(1, host.Ledger.Count, "only the new world's key is remembered");

        // A request no one can run is refused before the ledger, so a consumer that attaches later can still answer
        // the same key instead of inheriting a remembered refusal.
        host.CommandExecutor = null;
        suite.Equal("no-consumer", Harness.Deliver(host, 2, Harness.Request(2, 1, 2, "forge.plan.alpha", "binding.a", "open"))!.Value.Code, "a request without a consumer reports none");
        suite.Equal(1, host.Ledger.Count, "the refusal was not stored");
        host.CommandExecutor = (request, payload) => { runs.Add(request.EventId); return Outcome("succeeded", 3); };
        suite.Equal(NetworkStatus.Succeeded, Harness.Deliver(host, 2, Harness.Request(2, 1, 2, "forge.plan.alpha", "binding.a", "open"))!.Value.Status, "the same key executes once a consumer exists");
        suite.Equal(3, runs.Count, "the late request reached the executor");
    }

    private static void LedgerOnly(Suite suite)
    {
        suite.Case("dedup: first arrival commits, repeat reuses");
        var ledger = new NetworkDedupLedger(1);
        var runs = 0;
        var first = ledger.Submit(Key, new byte[] { 1, 2, 3 }, () => { runs++; return Outcome("committed"); });
        suite.Equal(DedupStatuses.Committed, first.Status, "first arrival commits");
        suite.True(first.Execute, "first arrival may execute");
        var second = ledger.Submit(Key, new byte[] { 1, 2, 3 }, () => { runs++; return Outcome("committed"); });
        suite.Equal(DedupStatuses.Duplicate, second.Status, "repeat is a duplicate");
        suite.True(!second.Execute, "a duplicate never executes");
        suite.Equal(1, runs, "the command ran once");
        suite.Equal(NetworkCodes.Duplicate, second.Cached!.Code, "the duplicate names itself as one");
        suite.Equal(1, ledger.Count, "one key is remembered");
        suite.Equal(1, ledger.DuplicateHits, "duplicate hits are counted");

        suite.Case("dedup: same event id, other payload");
        var conflict = ledger.Submit(Key, new byte[] { 9, 9, 9 }, () => { runs++; return Outcome("committed"); });
        suite.Equal(DedupStatuses.Rejected, conflict.Status, "another payload is not a duplicate");
        suite.Equal(NetworkCodes.EventIdConflict, conflict.Code, "conflict code");
        suite.Equal(1, runs, "a conflict never executes");
        suite.Equal(1, ledger.Conflicts, "conflicts are counted");

        suite.Case("dedup: identity fields separate keys");
        var same = ledger.Submit(Key with { EventId = 101 }, new byte[] { 1 }, () => Outcome("committed"));
        var otherPlan = ledger.Submit(Key with { PlanId = "forge.plan.beta" }, new byte[] { 1 }, () => Outcome("committed"));
        var otherSession = ledger.Submit(Key with { SenderSession = 3 }, new byte[] { 1 }, () => Outcome("committed"));
        suite.Equal(DedupStatuses.Committed, same.Status, "another event id is another key");
        suite.Equal(DedupStatuses.Committed, otherPlan.Status, "another plan is another key");
        suite.Equal(DedupStatuses.Committed, otherSession.Status, "another sender is another key");

        suite.Case("dedup: stale world");
        var stale = ledger.Submit(Key with { WorldEpoch = 0, EventId = 200 }, new byte[] { 1 }, () => Outcome("committed"));
        suite.Equal(DedupStatuses.Rejected, stale.Status, "a request from another world is refused");
        suite.Equal(NetworkCodes.StaleWorld, stale.Code, "stale world code");
        suite.Equal(1, ledger.StaleRefusals, "stale refusals are counted");

        suite.Case("dedup: capacity refuses, never evicts");
        var small = new NetworkDedupLedger(1, 2);
        suite.Equal(DedupStatuses.Committed, small.Submit(Key with { EventId = 1 }, new byte[] { 1 }, () => Outcome("committed")).Status, "first key fits");
        suite.Equal(DedupStatuses.Committed, small.Submit(Key with { EventId = 2 }, new byte[] { 1 }, () => Outcome("committed")).Status, "second key fits");
        var full = small.Submit(Key with { EventId = 3 }, new byte[] { 1 }, () => Outcome("committed"));
        suite.Equal(DedupStatuses.Rejected, full.Status, "a full ledger refuses");
        suite.Equal(NetworkCodes.LedgerFull, full.Code, "capacity code");
        suite.Equal(2, small.Count, "the refused key was not stored");
        suite.Equal(DedupStatuses.Duplicate, small.Submit(Key with { EventId = 1 }, new byte[] { 1 }, () => Outcome("committed")).Status, "an earlier key is remembered and replays");
        suite.Equal(1, small.Refusals, "refusals are counted");

        suite.Case("dedup: world change clears");
        var worldLedger = new NetworkDedupLedger(1);
        worldLedger.Submit(Key, new byte[] { 1 }, () => Outcome("committed"));
        suite.Equal(1, worldLedger.Count, "the key is remembered in this world");
        worldLedger.BeginWorld(2);
        suite.Equal(0, worldLedger.Count, "a new world clears the ledger");
        suite.Equal(2L, worldLedger.WorldEpoch, "the ledger follows the world");
        suite.Equal(DedupStatuses.Rejected, worldLedger.Submit(Key, new byte[] { 1 }, () => Outcome("committed")).Status, "the old world's key is stale now");
        suite.Throws<NetworkContractException>(() => worldLedger.BeginWorld(2), NetworkCodes.EpochNotIncreasing, "an epoch cannot be reused");

        suite.Case("dedup: session leaves");
        var sessionLedger = new NetworkDedupLedger(1);
        sessionLedger.Submit(Key, new byte[] { 1 }, () => Outcome("committed"));
        sessionLedger.Submit(Key with { SenderSession = 3 }, new byte[] { 1 }, () => Outcome("committed"));
        sessionLedger.Forget(2);
        suite.Equal(1, sessionLedger.Count, "only the departed session's key left");
        suite.Equal(DedupStatuses.Committed, sessionLedger.Submit(Key, new byte[] { 1 }, () => Outcome("committed")).Status, "the same session number may commit again after a rejoin");
    }

    private static void HostPath(Suite suite)
    {
        suite.Case("dedup: host path");
        var (host, client) = Harness.Pair();
        Harness.Handshake(host, client);
        var executed = new List<ulong>();
        host.CommandExecutor = (request, payload) =>
        {
            executed.Add(request.EventId);
            return Outcome("succeeded", payload.Length);
        };
        var door = Harness.Request(2, 0, 1, "forge.plan.alpha", "binding.a", "open");
        var firstResult = Harness.Deliver(host, 2, door);
        suite.Equal(1, executed.Count, "the host executed the request once");
        suite.Equal(NetworkStatus.Succeeded, firstResult!.Value.Status, "the result reports success");
        suite.Equal(4, firstResult.Value.FactCount, "the result carries the executor's fact count");
        var repeat = Harness.Deliver(host, 2, door);
        suite.Equal(1, executed.Count, "the repeat did not execute");
        suite.Equal(NetworkStatus.Succeeded, repeat!.Value.Status, "the repeat replays the first execution's status");
        suite.Equal(NetworkCodes.Duplicate, repeat.Value.Code, "the repeat carries the duplicate code");

        suite.Case("dedup: host gates");
        var unhandshaken = Harness.Deliver(host, 42, Harness.Request(42, 0, 2, "forge.plan.alpha", "binding.a", "open"));
        suite.Equal(NetworkCodes.HelloUnsent, unhandshaken!.Value.Code, "a peer without a handshake is refused");
        var staleWorld = Harness.Deliver(host, 2, Harness.Request(2, 3, 3, "forge.plan.alpha", "binding.a", "open"));
        suite.Equal(NetworkCodes.StaleWorld, staleWorld!.Value.Code, "a request from another world is refused");
        suite.Equal(1, executed.Count, "neither refusal executed anything");

        suite.Case("dedup: no consumer");
        host.CommandExecutor = null;
        var orphan = Harness.Deliver(host, 2, Harness.Request(2, 0, 4, "forge.plan.alpha", "binding.a", "open"));
        suite.Equal("no-consumer", orphan!.Value.Code, "an unclaimed command reports no consumer");
    }

    private static NetworkCommandOutcome Outcome(string status, int facts = 0)
        => new(status, status == "succeeded" ? NetworkCommitStates.Confirmed : NetworkCommitStates.None, "committed", "", facts);
}
