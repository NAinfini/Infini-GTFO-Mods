using System;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// Epoch checks: the host's number only moves forward, a client only takes it from the current host, and every
/// accepted change retires the previous world's keys. Cases that attach start from <see cref="Harness.Open"/>.
/// </summary>
internal static class EpochChecks
{
    internal static void Run(Suite suite)
    {
        HostSide(suite);
        ClientSide(suite);
        ClientClears(suite);
    }

    private static void HostSide(Suite suite)
    {
        suite.Case("epoch: host advances forward");
        Harness.Open();
        var host = Harness.Attach(SessionRole.Host, 1);
        var first = host.AdvanceWorld(1, WorldChangeReason.NewGeneration);
        suite.Equal(1L, first.Epoch.WorldEpoch, "the host moved to the new world");
        suite.Equal(0L, first.Epoch.PreviousEpoch, "the broadcast names the world it left");
        suite.Equal((byte)WorldChangeReason.NewGeneration, first.Epoch.Reason, "the reason travels");
        suite.Equal(1UL, first.Epoch.SenderSession, "the broadcast names the host");
        suite.Throws<NetworkContractException>(() => host.AdvanceWorld(1, WorldChangeReason.LevelCleanup), NetworkCodes.EpochNotIncreasing, "an epoch cannot be reused");
        // An attached client is refused by the role rule; a layer that never attached cannot speak at all.
        var client = Harness.Attach(SessionRole.Client, 2);
        suite.Throws<NetworkContractException>(() => client.AdvanceWorld(1, WorldChangeReason.LevelCleanup), NetworkCodes.SenderNotHost, "a client cannot advance the world");
    }

    private static void ClientSide(Suite suite)
    {
        suite.Case("epoch: client follows the host, refuses rollback and gaps");
        var (host, client) = Harness.Pair();
        // The handshake is what tells a client which world the host is on; without it every broadcast is refused.
        Harness.Handshake(host, client);
        var first = host.AdvanceWorld(1, WorldChangeReason.NewGeneration);
        Harness.Send(host, first);
        var hostReply = Harness.DeliverLast(client);
        suite.Equal(null, hostReply, "an epoch changes state instead of answering");
        suite.Equal(1L, client.Epoch.WorldEpoch, "the client follows the host");
        suite.Equal(1L, client.Ledger.WorldEpoch, "the client's ledger follows the world");
        suite.Equal(1L, client.Status.WorldEpoch, "status reports the world the client follows");

        // A client that has not joined reports the join fault rather than a stale number: it has no baseline yet.
        var notJoined = new WorldEpochSync(SessionRole.Client, 0);
        notJoined.BindHost(1);
        var beforeHandshake = notJoined.Observe(first.Epoch, 1, true);
        suite.Equal(NetworkCodes.EpochUnavailable, beforeHandshake.Code, "a broadcast is refused before the handshake names the world");

        var rollback = new WorldEpochMessage { ProtocolVersion = NetworkProtocol.Version, SenderSession = 1, WorldEpoch = 0, PreviousEpoch = 1 };
        var rolled = client.Epoch.Observe(rollback, 1, true);
        suite.Equal(NetworkCodes.EpochNotIncreasing, rolled.Code, "an older epoch is refused");
        suite.Equal(1L, rolled.WorldEpoch, "the client keeps its world");
        var repeat = client.Epoch.Observe(first.Epoch, 1, true);
        suite.Equal(NetworkCodes.EpochNotIncreasing, repeat.Code, "a repeated broadcast is refused");

        var far = new WorldEpochMessage { ProtocolVersion = NetworkProtocol.Version, SenderSession = 1, WorldEpoch = 5, PreviousEpoch = 4 };
        var gapped = client.Epoch.Observe(far, 1, true);
        suite.Equal(NetworkCodes.EpochBeforeJoin, gapped.Code, "an epoch that does not follow the client's own is refused");
        suite.Equal(1L, client.Epoch.WorldEpoch, "the client keeps its world after a gap");

        var otherSender = new WorldEpochMessage { ProtocolVersion = NetworkProtocol.Version, SenderSession = 3, WorldEpoch = 2, PreviousEpoch = 1 };
        var stranger = client.Epoch.Observe(otherSender, 3, false);
        suite.Equal(NetworkCodes.SenderNotHost, stranger.Code, "a broadcast from another session is refused");
        var notMaster = client.Epoch.Observe(otherSender, 3, true);
        suite.Equal(NetworkCodes.SenderNotHost, notMaster.Code, "a broadcast from a session that is not bound as host is refused");
        suite.Throws<NetworkContractException>(() => client.Epoch.Observe(new WorldEpochMessage { SenderSession = 9, WorldEpoch = 2 }, 1, true), NetworkCodes.FormatInvalid, "a broadcast that names another sender is refused");
    }

    private static void ClientClears(Suite suite)
    {
        suite.Case("epoch: unsynchronized client");
        Harness.Open();
        var fresh = new WorldEpochSync(SessionRole.Client, 0);
        fresh.BindHost(1);
        var first = new WorldEpochMessage { ProtocolVersion = NetworkProtocol.Version, SenderSession = 1, WorldEpoch = 1, PreviousEpoch = 0 };
        var beforeHandshake = fresh.Observe(first, 1, true);
        suite.Equal(NetworkCodes.EpochUnavailable, beforeHandshake.Code, "an epoch is refused before the handshake names the world");

        suite.Case("epoch: client clears its ledger");
        // Both sides start in world 1, so the client's baseline is the host's number and a handshake alone opens it.
        var (host, clearing) = Harness.Pair(worldEpoch: 1);
        Harness.Handshake(host, clearing);
        // A level cleanup is the world transition this case uses: the checkpoint-suspension reason is retired, and
        // the rule under test belongs to the epoch, not to which transition produced it.
        var next = host.AdvanceWorld(2, WorldChangeReason.LevelCleanup);
        Harness.Send(host, next);
        Harness.DeliverLast(clearing);
        suite.Equal(2L, clearing.Ledger.WorldEpoch, "an accepted broadcast moves the ledger");
        suite.Equal(2L, clearing.Status.WorldEpoch, "status reports the current world");
        suite.Equal(2L, clearing.Epoch.WorldEpoch, "the client's epoch follows the host");
        var lateFact = new FactMessage { ProtocolVersion = NetworkProtocol.Version, SenderSession = 1, WorldEpoch = 1, Sequence = 1 };
        var observed = 0;
        clearing.FactObserver = (fact, payload) => observed++;
        Harness.Send(host, new NetworkMessage(NetworkMessageKind.Fact, lateFact));
        Harness.DeliverLast(clearing);
        suite.Equal(0, observed, "a fact from the retired world is dropped");
        var currentFact = new FactMessage { ProtocolVersion = NetworkProtocol.Version, SenderSession = 1, WorldEpoch = 2, Sequence = 2 };
        Harness.Send(host, new NetworkMessage(NetworkMessageKind.Fact, currentFact));
        Harness.DeliverLast(clearing);
        suite.Equal(1, observed, "a fact from the current world is delivered");

        suite.Case("epoch: client drops its host");
        var dropped = new WorldEpochSync(SessionRole.Client, 3);
        dropped.BindHost(1);
        dropped.Synchronize(3);
        dropped.Desynchronize();
        var afterLeave = dropped.Observe(new WorldEpochMessage { ProtocolVersion = NetworkProtocol.Version, SenderSession = 1, WorldEpoch = 4, PreviousEpoch = 3 }, 1, true);
        suite.Equal(NetworkCodes.SenderNotHost, afterLeave.Code, "a client without a host accepts nothing");
    }
}
