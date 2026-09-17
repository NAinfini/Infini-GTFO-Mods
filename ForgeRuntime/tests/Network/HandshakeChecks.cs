using System;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// Handshake checks: a matching peer opens the gameplay gate, and every way a peer can differ from this runtime
/// leaves it closed with the code that names the difference. Each case starts from <see cref="Harness.Open"/>, and
/// only the cases that send attach their participants.
/// </summary>
internal static class HandshakeChecks
{
    internal static void Run(Suite suite)
    {
        Matching(suite);
        PlanOrder(suite);
        PlanDigest(suite);
        ProtocolMismatch(suite);
        IdentityMismatch(suite);
        PlanSetMismatch(suite);
        RoleMismatch(suite);
        RejectedClient(suite);
        ClientRefusal(suite);
        AckFromNonMaster(suite);
        AckForAnotherSubject(suite);
        ClaimedIdentity(suite);
        PeerLeaves(suite);
        Detached(suite);
        ComparisonOrder(suite);
    }

    private static void Matching(Suite suite)
    {
        suite.Case("handshake: matching pair");
        var (host, client) = Harness.Pair();
        var ack = Harness.Handshake(host, client);
        suite.Equal((byte)1, ack.Accepted, "host accepted a matching client");
        suite.Equal(NetworkCodes.Accepted, ack.ReasonCode, "accept code");
        suite.Equal(2UL, ack.SubjectSession, "ack names the client");
        suite.Equal(1UL, ack.SenderSession, "ack names the host session");
        suite.Equal(Harness.Identity.Id, ack.HostRuntimeId, "ack names the host runtime");
        suite.True(client.Handshake.MatchesHost(1), "client gate is open for the current host");
        suite.Equal(null, host.Handshake.RequirePeer(2), "host gate is open for the client");
        suite.True(host.Handshake.Peer(2)!.Executable, "accepted peer is executable");
    }

    private static void PlanOrder(Suite suite)
    {
        suite.Case("handshake: plan order does not matter");
        Harness.Open();
        var reversedHost = Harness.Attach(SessionRole.Host, 1);
        var reversedClient = Harness.Attach(SessionRole.Client, 2, plans: new[] { Harness.Plans[1], Harness.Plans[0] });
        var orderAck = Harness.Handshake(reversedHost, reversedClient);
        suite.Equal((byte)1, orderAck.Accepted, "the same plans in another declaration order still match");
    }

    /// <summary>The digest replaces the plan rows, so it has to cover every field the rows used to carry, and it has
    /// to be the same value whichever order the plans were declared in.</summary>
    private static void PlanDigest(Suite suite)
    {
        suite.Case("handshake: the plan-set digest covers every compared field");
        Harness.Open();
        var direct = Harness.Build(SessionRole.Host);
        var reversed = Harness.Build(SessionRole.Host, plans: new[] { Harness.Plans[1], Harness.Plans[0] });
        suite.True(PlanSetDigest.Equal(direct.Handshake.Digest, reversed.Handshake.Digest), "the declaration order does not change the digest");
        suite.Equal((byte)2, direct.Handshake.Hello(1).PlanCount, "the hello advertises how many plans were hashed");
        suite.True(direct.Handshake.PlansAdopted, "a plan set is adopted when the handshake is built with one");
        suite.True(Harness.Build(SessionRole.Host, plans: Array.Empty<PlanIdentity>()).Handshake.PlansAdopted,
            "a discovered set of no plans is still an adopted set");
        suite.False(Harness.Build(SessionRole.Host, adopted: false).Handshake.PlansAdopted, "a session before plan discovery has adopted nothing");
        suite.False(PlanSetDigest.Equal(direct.Handshake.Digest,
            Harness.Build(SessionRole.Host, plans: new[] { Harness.Plans[0], Harness.Plans[1] with { ResourceId = "resource.gamma" } }).Handshake.Digest),
            "another resource id changes the digest");
        suite.False(PlanSetDigest.Equal(direct.Handshake.Digest,
            Harness.Build(SessionRole.Host, plans: new[] { Harness.Plans[0], Harness.Plans[1] with { ResourceRevision = "r2" } }).Handshake.Digest),
            "another resource revision changes the digest");
        suite.False(PlanSetDigest.Equal(direct.Handshake.Digest,
            Harness.Build(SessionRole.Host, plans: new[] { Harness.Plans[0], Harness.Plans[1] with { BindingPins = "binding.c,binding.d" } }).Handshake.Digest),
            "another binding pin list changes the digest");
        var adopted = Harness.Build(SessionRole.Host, adopted: false);
        suite.True(adopted.Handshake.AdoptPlans(Harness.Plans), "adopting the first set is a change");
        suite.False(adopted.Handshake.AdoptPlans(Harness.Plans), "adopting the same set again is not a change");
        suite.True(adopted.Handshake.AdoptPlans(new[] { Harness.Plans[0] }), "adopting another set is a change");
    }

    private static void ProtocolMismatch(Suite suite)
    {
        suite.Case("handshake: protocol mismatch");
        Harness.Open();
        var forged = Harness.HelloBody(SessionRole.Client);
        forged.ProtocolVersion = NetworkProtocol.Version + 1;
        var protocolAck = Harness.Build(SessionRole.Host).Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 2, forged))!.Value;
        suite.Equal((byte)0, protocolAck.Ack.Accepted, "another protocol version is refused");
        suite.Equal(NetworkCodes.ProtocolMismatch, protocolAck.Ack.ReasonCode, "protocol reason code");
    }

    private static void IdentityMismatch(Suite suite)
    {
        suite.Case("handshake: identity mismatch");
        Harness.Open();
        var versionAck = Harness.Build(SessionRole.Host).Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 2,
            Harness.HelloBody(SessionRole.Client, identity: new RuntimeIdentitySummary("NAinfini.ForgeRuntime", "1.0.0", "1.0.0", "20403457"))))!.Value;
        suite.Equal(NetworkCodes.RuntimeMismatch, versionAck.Ack.ReasonCode, "another runtime version is refused");
        var buildAck = Harness.Build(SessionRole.Host).Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 2,
            Harness.HelloBody(SessionRole.Client, identity: new RuntimeIdentitySummary("NAinfini.ForgeRuntime", "1.0.0", "1.0.0", "20403456"))))!.Value;
        suite.Equal(NetworkCodes.RuntimeMismatch, buildAck.Ack.ReasonCode, "another game build is refused");
    }

    private static void PlanSetMismatch(Suite suite)
    {
        suite.Case("handshake: plan set mismatch");
        Harness.Open();
        var fewerAck = Harness.Build(SessionRole.Host)
            .Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 2, Harness.HelloBody(SessionRole.Client, plans: new[] { Harness.Plans[0] })))!.Value;
        suite.Equal(NetworkCodes.PlanSetMismatch, fewerAck.Ack.ReasonCode, "a different plan count is refused");
        var revisedAck = Harness.Build(SessionRole.Host)
            .Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 2, Harness.HelloBody(SessionRole.Client, plans: new[] { Harness.Plans[0], new PlanIdentity("forge.plan.beta", "resource.beta", "r2", "binding.c") })))!.Value;
        suite.Equal(NetworkCodes.PlanSetMismatch, revisedAck.Ack.ReasonCode, "another resource revision is refused");
        var repinnedAck = Harness.Build(SessionRole.Host)
            .Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 2, Harness.HelloBody(SessionRole.Client, plans: new[] { Harness.Plans[0], new PlanIdentity("forge.plan.beta", "resource.beta", "r1", "binding.c,binding.d") })))!.Value;
        suite.Equal(NetworkCodes.PlanSetMismatch, repinnedAck.Ack.ReasonCode, "another binding pin list is refused");
        var noPlansAck = Harness.Build(SessionRole.Host, adopted: false)
            .Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 2, Harness.HelloBody(SessionRole.Client)))!.Value;
        suite.Equal(NetworkCodes.PlansUnavailable, noPlansAck.Ack.ReasonCode, "a host that has adopted no plan set cannot compare one");
        // The digests say only that the sets differ, so the comparison explains a difference with both counts.
        var explained = Harness.Build(SessionRole.Host).Handshake.ComparePlans(Harness.Plans.Length + 1, Harness.Build(SessionRole.Host).Handshake.Digest);
        suite.Equal(NetworkCodes.PlanSetMismatch, explained.Code, "a different count is a mismatch");
        suite.True(explained.Detail.Contains("Plan sets differ"), "the difference is named");
        suite.True(explained.Detail.Contains("3") && explained.Detail.Contains("2"), "the explanation carries both plan counts");
    }

    private static void RoleMismatch(Suite suite)
    {
        suite.Case("handshake: role mismatch");
        Harness.Open();
        var roleAck = Harness.Build(SessionRole.Host)
            .Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 2, Harness.HelloBody(SessionRole.Host)))!.Value;
        suite.Equal(NetworkCodes.RoleMismatch, roleAck.Ack.ReasonCode, "a second host is refused");
        var clientReceivesHost = Harness.Build(SessionRole.Client);
        var helloFromAnotherClient = clientReceivesHost.Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 2, Harness.HelloBody(SessionRole.Client)));
        suite.Equal(null, helloFromAnotherClient, "a client does not answer a hello that is not from a host");
        suite.Equal(HandshakeState.Mismatched, clientReceivesHost.Handshake.Peer(2)!.State, "the refused peer is recorded");
        suite.Equal(NetworkCodes.RoleMismatch, clientReceivesHost.Handshake.Peer(2)!.Code, "with the reason it was refused");
        // SNet, not the hello, names the host: a session that claims the role but is not the master is refused too.
        var helloFromANonMaster = clientReceivesHost.Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 4, Harness.HelloBody(SessionRole.Host, 4)));
        suite.Equal(null, helloFromANonMaster, "a client does not answer a host SNet has not named");
        suite.Equal(NetworkCodes.SenderNotHost, clientReceivesHost.Handshake.Peer(4)!.Code, "the un-named host is refused for claiming the role");
        suite.True(!clientReceivesHost.Handshake.MatchesHost(4), "the un-named host cannot open the client's gate");
        Harness.Master = 3;
        var helloFromAHost = clientReceivesHost.Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 3, Harness.HelloBody(SessionRole.Host, 3)));
        suite.Equal((byte)1, helloFromAHost!.Value.Ack.Accepted, "the same client answers the master's hello with its own verdict");
        suite.True(clientReceivesHost.Handshake.MatchesHost(3), "and the master's verdict opens the gate");
    }

    private static void RejectedClient(Suite suite)
    {
        suite.Case("handshake: rejected client stays gated");
        Harness.Open();
        var rejectingHost = Harness.Attach(SessionRole.Host, 1);
        var mismatchedClient = Harness.Attach(SessionRole.Client, 2, identity: new RuntimeIdentitySummary("OtherRuntime", "9.9.9", "1.0.0", "20403457"));
        var rejectedAck = Harness.Handshake(rejectingHost, mismatchedClient);
        suite.Equal((byte)0, rejectedAck.Accepted, "the host refused the client");
        suite.True(rejectingHost.Handshake.RequirePeer(2) != null, "host gate stays closed for a refused peer");
        suite.Equal(NetworkCodes.RuntimeMismatch, rejectingHost.Handshake.RequirePeer(2), "host reports why");
        suite.True(!mismatchedClient.Handshake.MatchesHost(1), "client gate stays closed after the host refuses it");
    }

    private static void ClientRefusal(Suite suite)
    {
        suite.Case("handshake: client refuses a mismatched host");
        Harness.Open();
        var clientSaysNo = Harness.Build(SessionRole.Client, plans: new[] { Harness.Plans[0] });
        Harness.Master = 3;
        var clientRefusal = clientSaysNo.Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 3, Harness.HelloBody(SessionRole.Host, 3)))!.Value;
        suite.Equal((byte)0, clientRefusal.Ack.Accepted, "the client refuses a host with another plan set");
        suite.Equal(NetworkCodes.PlanSetMismatch, clientRefusal.Ack.ReasonCode, "client reason code");
        suite.True(!clientSaysNo.Handshake.MatchesHost(1), "client gate stays closed");
    }

    private static void AckFromNonMaster(Suite suite)
    {
        suite.Case("handshake: ack from a non-master is ignored");
        var (host, waitingClient) = Harness.Pair();
        var ackBody = Harness.Handshake(host, waitingClient);
        suite.True(waitingClient.Handshake.MatchesHost(1), "the ack from the master is a verdict");
        // The same body, from the same session, once that session is no longer the master.
        Harness.Master = 9;
        waitingClient.Receive(new NetworkEnvelope(NetworkMessageKind.HelloAck, 1, ackBody));
        suite.True(!waitingClient.Handshake.MatchesHost(1), "an ack from a session that is not the master is not a verdict");
    }

    private static void AckForAnotherSubject(Suite suite)
    {
        suite.Case("handshake: ack for another subject is ignored");
        Harness.Open();
        var client = Harness.Attach(SessionRole.Client, 2);
        Harness.Master = 1;
        var wrongSubject = new HelloAckMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = 1,
            SubjectSession = 7,
            Role = (byte)SessionRole.Host,
            Accepted = 1
        };
        wrongSubject.ReasonCode = NetworkCodes.Accepted;
        client.Receive(new NetworkEnvelope(NetworkMessageKind.HelloAck, 1, wrongSubject));
        suite.True(!client.Handshake.MatchesHost(1), "an ack for another session is not a verdict");
    }

    private static void ClaimedIdentity(Suite suite)
    {
        suite.Case("handshake: identity cannot be claimed");
        Harness.Open();
        suite.Throws<NetworkContractException>(
            () => Harness.Build(SessionRole.Host).Receive(new NetworkEnvelope(NetworkMessageKind.Hello, 5, Harness.HelloBody(SessionRole.Client))),
            NetworkCodes.FormatInvalid, "a hello whose body names another sender is refused");
    }

    private static void PeerLeaves(Suite suite)
    {
        suite.Case("handshake: peer leaves");
        var (host, client) = Harness.Pair();
        Harness.Handshake(host, client);
        suite.Equal(null, host.Handshake.RequirePeer(2), "the client is admitted");
        host.Forget(2);
        suite.Equal(NetworkCodes.HelloUnsent, host.Handshake.RequirePeer(2), "a departed peer is not admitted");
        suite.Equal(null, host.Handshake.Peer(2), "the departed peer's verdict is gone");
    }

    private static void Detached(Suite suite)
    {
        suite.Case("handshake: a layer without a session cannot speak");
        Harness.Open();
        var detached = Harness.Build(SessionRole.Host);
        suite.True(!detached.Registered, "a layer that never attached owns no events");
        suite.Throws<NetworkContractException>(() => detached.Hello(), NetworkCodes.HelloUnsent, "a layer without a session cannot build a hello");
        suite.Throws<NetworkContractException>(() => detached.AdvanceWorld(1, WorldChangeReason.NewGeneration), NetworkCodes.HelloUnsent, "a layer without a session cannot advance the world");
    }

    private static void ComparisonOrder(Suite suite)
    {
        suite.Case("handshake: comparison order");
        Harness.Open();
        var ordered = Harness.Build(SessionRole.Host);
        var bothWrong = ordered.Handshake.Compare(NetworkProtocol.Version + 1, SessionRole.Host, SessionRole.Client,
            new RuntimeIdentitySummary("other", "1", "1", "1"), 0, ordered.Handshake.Digest);
        suite.Equal(NetworkCodes.ProtocolMismatch, bothWrong.Code, "protocol is reported before role");
        var identityFirst = ordered.Handshake.Compare(NetworkProtocol.Version, SessionRole.Client, SessionRole.Client,
            new RuntimeIdentitySummary("other", "1", "1", "1"), Harness.Plans.Length + 1, ordered.Handshake.Digest);
        suite.Equal(NetworkCodes.RuntimeMismatch, identityFirst.Code, "the runtime lock is reported before the plan set");
    }
}
