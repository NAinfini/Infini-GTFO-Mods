using System;
using System.Collections.Generic;
using ForgeRuntime.GameBindings;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// End-to-end flow checks: two participants, each with its own binding decisions, driven through the transport double
/// so a hello, a refusal, a readiness announcement and the retry it causes are observed as messages rather than as
/// one handshake's internal state. The flows are the ones the live game produces — the host adopts its plan set
/// before or after a client attaches, the two sets differ, a set is larger than any fixed layout could hold, and a
/// client detaches and comes back.
/// </summary>
internal static class HandshakeFlowChecks
{
    internal static void Run(Suite suite)
    {
        HostAdoptsFirst(suite);
        ClientAttachesFirst(suite);
        PlanSetsDiffer(suite);
        ManyPlans(suite);
        Reattach(suite);
    }

    /// <summary>Both sides know their plan set before the session opens: one hello and one ack each way, and the
    /// world the host broadcasts afterwards is accepted.</summary>
    private static void HostAdoptsFirst(Suite suite)
    {
        suite.Case("flow: the host adopts before the client attaches");
        Harness.Open();
        var host = new End(SessionRole.Host, 1);
        host.Adopt(Harness.Plans);
        host.Observe();
        var client = new End(SessionRole.Client, 2);
        client.Adopt(Harness.Plans);
        client.Observe();
        var crossed = Harness.Pump(host.Host, client.Host);
        suite.Equal(4, crossed, "a matching pair is one hello and one ack each way");
        suite.Equal(1, host.Sends(NetworkProtocol.HelloEvent), "the host announces its plan set once");
        suite.Equal(1, client.Sends(NetworkProtocol.HelloEvent), "the client announces its plan set once");
        suite.True(host.Host.Handshake.RequirePeer(2) == null, "the host admits the client");
        suite.True(client.Host.Handshake.MatchesHost(1), "the client's gate is open");
        suite.Equal(0, client.Suspensions.Count, "a matching pair suspends nothing");
        EpochAccepted(suite, host, client, 4);
    }

    /// <summary>The client attaches while the host has adopted nothing: its hello is a retry rather than a
    /// difference, and the host's own announcement after discovery brings it back exactly once.</summary>
    private static void ClientAttachesFirst(Suite suite)
    {
        suite.Case("flow: the client attaches before the host adopts");
        Harness.Open();
        var host = new End(SessionRole.Host, 1);
        host.Observe();
        var client = new End(SessionRole.Client, 2);
        client.Adopt(Harness.Plans);
        client.Observe();
        var cursor = Harness.Pump(host.Host, client.Host);
        suite.Equal(2, cursor, "one hello and one refusal are the whole exchange while the host has nothing to compare");
        suite.Equal(NetworkCodes.PlansUnavailable, client.PeerCode(1), "the refusal is recorded as the transient code");
        suite.True(!client.Host.Handshake.MatchesHost(1), "the client's gate stays closed until the host can compare");
        suite.Equal(0, client.Suspensions.Count, "a host without a plan set suspends nothing");
        suite.Equal(0, client.Warnings.Count, "and warns about nothing");
        suite.Equal(0, host.Sends(NetworkProtocol.HelloEvent), "the host announces nothing it has not adopted");
        // A world broadcast is refused while the handshake is open: no ack has named this host yet.
        Harness.Send(host.Host, host.Host.AdvanceWorld(3, WorldChangeReason.NewGeneration, "level generation started"));
        Harness.DeliverLast(client.Host);
        suite.Equal(0L, client.Host.Epoch.WorldEpoch, "a world broadcast before the handshake completes is refused");

        host.Adopt(Harness.Plans);
        suite.Equal(1, host.Sends(NetworkProtocol.HelloEvent), "adopting the set while attached announces it");
        cursor = Harness.Pump(host.Host, client.Host, cursor);
        suite.Equal(7, cursor, "the refused broadcast, the host's announcement, the retried hello and both answers follow");
        suite.Equal(2, client.Sends(NetworkProtocol.HelloEvent), "the client's hello went out exactly twice");
        suite.True(client.Host.Handshake.MatchesHost(1), "the retried hello is compared and matches");
        suite.True(host.Host.Handshake.RequirePeer(2) == null, "the host admits the client");
        suite.Equal(0, client.Suspensions.Count, "nothing was suspended on the way");
        EpochAccepted(suite, host, client, 7);
    }

    /// <summary>Different plan sets: the host refuses the peer and keeps running, and the client suspends itself with
    /// the code and the explanation the refusal carries.</summary>
    private static void PlanSetsDiffer(Suite suite)
    {
        suite.Case("flow: different plan sets suspend the client only");
        Harness.Open();
        var host = new End(SessionRole.Host, 1);
        host.Adopt(Harness.Plans);
        host.Observe();
        var client = new End(SessionRole.Client, 2);
        client.Adopt(new[] { Harness.Plans[0] });
        client.Observe();
        Harness.Pump(host.Host, client.Host);
        suite.True(!client.Host.Handshake.MatchesHost(1), "the client's gate stays closed");
        suite.Equal(1, client.Suspensions.Count, "the client suspends itself once");
        suite.Equal(NetworkCodes.PlanSetMismatch, client.Suspensions[0], "with the code that names the difference");
        suite.True(client.Warnings[0].Contains("Plan sets differ"), "the warning names the difference");
        suite.True(client.Warnings[0].Contains("advertises 2 plans") && client.Warnings[0].Contains("has 1"),
            "and it carries both plan counts");
        suite.Equal(0, host.Suspensions.Count, "the host keeps running with a peer it refused");
        suite.Equal(NetworkCodes.PlanSetMismatch, host.Host.Handshake.RequirePeer(2), "the host refuses that peer");
        suite.Equal(NetworkCodes.PlanSetMismatch, host.Host.Handshake.Peer(2)!.Code, "the host's verdict names the difference");
        // A refused client is not joined to the host's world either.
        Harness.Send(host.Host, host.Host.AdvanceWorld(1, WorldChangeReason.NewGeneration, "level generation started"));
        Harness.DeliverLast(client.Host);
        suite.Equal(0L, client.Host.Epoch.WorldEpoch, "a refused client takes no world from the host");
    }

    /// <summary>Forty plans travel as the same message a two-plan set does: the set is a count and a digest, so no
    /// plan set is too large for the hello.</summary>
    private static void ManyPlans(Suite suite)
    {
        suite.Case("flow: a plan set of forty handshakes normally");
        Harness.Open();
        var many = new List<PlanIdentity>();
        for (var index = 0; index < 40; index++)
            many.Add(new PlanIdentity("forge.plan." + index.ToString("D3"), "resource." + index, "r1", "binding." + index));
        var host = new End(SessionRole.Host, 1);
        host.Adopt(many);
        host.Observe();
        var client = new End(SessionRole.Client, 2);
        client.Adopt(many);
        client.Observe();
        suite.Equal(Suite.SizeOf<HelloMessage>(), ((byte[])Api.Since(0).Payload).Length, "the hello is the same size whatever the plan set holds");
        var crossed = Harness.Pump(host.Host, client.Host);
        suite.Equal(4, crossed, "a forty-plan handshake is still one hello and one ack each way");
        suite.Equal(40, host.Host.Handshake.Peer(2)!.PlanCount, "the peer's plan count is carried");
        suite.True(client.Host.Handshake.MatchesHost(1), "forty plans match");
        suite.True(host.Host.Handshake.RequirePeer(2) == null, "and the host admits the client");
        suite.Equal(0, client.Suspensions.Count, "nothing is suspended");
        EpochAccepted(suite, host, client, 2);
    }

    /// <summary>A client that loses its session and comes back handshakes again from scratch: one hello for the new
    /// session, one ack, and nothing announced twice.</summary>
    private static void Reattach(Suite suite)
    {
        suite.Case("flow: a client that detaches and comes back handshakes once more");
        Harness.Open();
        var host = new End(SessionRole.Host, 1);
        host.Adopt(Harness.Plans);
        host.Observe();
        var client = new End(SessionRole.Client, 2);
        client.Adopt(Harness.Plans);
        client.Observe();
        var cursor = Harness.Pump(host.Host, client.Host);
        suite.Equal(4, cursor, "the first handshake is one hello and one ack each way");
        suite.Equal(NetworkSessionAction.Detach, client.Observe(hasLocalPlayer: false), "losing the local player detaches the client");
        var before = Api.SentCount;
        suite.Equal(NetworkSessionAction.Attach, client.Observe(), "the session comes back");
        suite.Equal(before + 1, Api.SentCount, "the re-attached client announces its plan set exactly once");
        cursor = Harness.Pump(host.Host, client.Host, cursor);
        suite.Equal(before + 2, Api.SentCount, "the second handshake is the client's hello and the host's answer");
        suite.Equal(2, client.Sends(NetworkProtocol.HelloEvent), "no hello is sent twice for one attach");
        suite.True(client.Host.Handshake.MatchesHost(1), "the client's gate is open again");
        suite.True(host.Host.Handshake.RequirePeer(2) == null, "the host admits the client again");
        suite.Equal(0, client.Suspensions.Count, "a returning client is not suspended");
    }

    /// <summary>The host broadcasts the world it moved to, and the client it handshook with takes it.</summary>
    private static void EpochAccepted(Suite suite, End host, End client, long epoch)
    {
        Harness.Send(host.Host, host.Host.AdvanceWorld(epoch, WorldChangeReason.NewGeneration, "level generation started"));
        Harness.DeliverLast(client.Host);
        suite.Equal(epoch, client.Host.Epoch.WorldEpoch, "the world broadcast is accepted once the handshake completed");
    }

    /// <summary>One participant: the binding decisions, the host they attach, and the record of what this process did
    /// about the verdicts it reached.</summary>
    private sealed class End
    {
        internal End(SessionRole role, ulong session)
        {
            Role = role;
            Session = session;
            Logic = new NetworkBindingLogic(
                (facts, plans) =>
                {
                    var host = Harness.Build(facts.IsMaster ? SessionRole.Host : SessionRole.Client, plans: plans, adopted: plans != null);
                    host.SenderIsMaster = sender => sender == facts.MasterSession;
                    host.Attach(facts.LocalSession, facts.LocalAddress);
                    return Harness.Track(host);
                },
                (code, _) => Suspensions.Add(code),
                message => Warnings.Add(message));
        }

        internal SessionRole Role { get; }
        internal ulong Session { get; }
        internal NetworkBindingLogic Logic { get; }
        internal NetworkHost Host => Logic.Host ?? throw new InvalidOperationException("This participant is in no session.");
        internal List<string> Suspensions { get; } = new();
        internal List<string> Warnings { get; } = new();

        /// <summary>One reading of this participant's session facts. The host of every flow is session 1.</summary>
        internal NetworkSessionAction Observe(bool hasLocalPlayer = true)
        {
            AsThisProcess();
            return Logic.Observe(new NetworkSessionFacts(hasLocalPlayer, hasLocalPlayer ? Session : 0,
                hasLocalPlayer ? Harness.Address(Session) : "", Role == SessionRole.Host, 1));
        }

        internal void Adopt(IReadOnlyList<PlanIdentity> plans)
        {
            AsThisProcess();
            Logic.AdoptPlans(plans);
        }

        /// <summary>The game attributes a send to the session that made it, and this participant is that session; the
        /// rig needs telling because it stands in for both machines at once.</summary>
        private void AsThisProcess() => NetworkApiDouble.Sender = Session;

        /// <summary>What the peer said about this side, as this side recorded it.</summary>
        internal string PeerCode(ulong peer) => Host.Handshake.Peer(peer)?.PeerCode ?? "";

        /// <summary>How many messages this participant sent for one protocol event.</summary>
        internal int Sends(string eventName)
        {
            var count = 0;
            for (var index = 0; index < Api.SentCount; index++)
                if (Api.Since(index).Sender == Session && Api.Since(index).EventName == eventName) count++;
            return count;
        }
    }
}
