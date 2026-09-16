using System;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// Transport checks: the adapter claims each message type exactly once per process, routes a typed payload and a
/// free-sized body to the same receive path, survives a detach and a re-attach without claiming a name twice, and
/// sends every kind on the frozen event name and channel.
/// </summary>
internal static class TransportChecks
{
    internal static void Run(Suite suite)
    {
        Registration(suite);
        Reattach(suite);
        Inbound(suite);
        Sending(suite);
        RoundTrip(suite);
        Detach(suite);
    }

    private static void Registration(Suite suite)
    {
        suite.Case("transport: registration is one per kind");
        Harness.Open();
        var host = Harness.Attach(SessionRole.Host, 1);
        suite.True(ForgeNetworkTransport.Registered, "the attached layer reports registered");
        foreach (var kind in MessageKinds())
            suite.Equal(1, Api.HandlerCount(ForgeNetworkTransport.EventName(kind)), ForgeNetworkTransport.EventName(kind) + " is claimed exactly once");
        suite.True(host.Registered, "the attached host reports registered");
        suite.Throws<NetworkContractException>(() => host.Attach(1, "1"), NetworkCodes.DuplicateEventName, "a second attach on one participant is refused");
        suite.Throws<NetworkContractException>(() => Harness.Build(SessionRole.Client).Attach(0, "0"), NetworkCodes.FormatInvalid, "a zero session is refused");
        suite.Throws<NetworkContractException>(() => Harness.Build(SessionRole.Client).Hello(), NetworkCodes.HelloUnsent, "an unattached layer cannot build a hello");

        // The rig stands in for both participants in one process. The shipped API faults on a second claim of a
        // name, so a second participant that registered its own copy would throw here rather than attach.
        var claims = Api.RegistrationCount;
        var mark = Api.Mark();
        var second = Harness.Attach(SessionRole.Client, 2);
        suite.True(second.Registered, "a second participant in one process is attached");
        suite.Equal(claims, Api.RegistrationCount, "the second participant claimed no name again");
        suite.Equal(mark, Api.SentCount, "attaching sends nothing");
    }

    /// <summary>The one sequence the game performs on every session: attach, leave, attach again. The game's
    /// registration is process-lifetime, so the second attach must reuse it — no second claim, and no lost delivery
    /// to the owner that is attached now.</summary>
    private static void Reattach(Suite suite)
    {
        suite.Case("transport: attach, detach and attach again claims nothing twice");
        Harness.Open();
        var first = Harness.Attach(SessionRole.Client, 2);
        var claims = Api.RegistrationCount;
        first.Detach();
        suite.True(!first.Registered, "the detached participant owns nothing");
        var second = Harness.Attach(SessionRole.Client, 3);
        suite.True(second.Registered, "the new participant owns the events");
        suite.Equal(claims, Api.RegistrationCount, "the re-attach claimed no name again");
        foreach (var kind in MessageKinds())
            suite.Equal(1, Api.HandlerCount(ForgeNetworkTransport.EventName(kind)), ForgeNetworkTransport.EventName(kind) + " still has one handler");
        Harness.Send(second, second.Hello());
        suite.Equal(3UL, Harness.Last()!.Value.Hello.SenderSession, "the participant attached now can send");
    }

    /// <summary>The game's delegate outlives both a detach and a re-attach, so an inbound message reaches whoever
    /// owns the events now: the participant attached at this moment, and nobody while none is attached.</summary>
    private static void Inbound(Suite suite)
    {
        suite.Case("transport: an inbound message reaches the participant attached now");
        Harness.Open();
        var first = Harness.Attach(SessionRole.Host, 1);
        Harness.Master = 1;
        var hello = NetworkCodec.Write(Harness.HelloBody(SessionRole.Client, 2));
        var mark = Api.Mark();
        Api.Deliver(NetworkProtocol.HelloEvent, 2, hello);
        suite.Equal(mark + 1, Api.SentCount, "the attached host answered the inbound hello");
        suite.True(first.Handshake.Peer(2) != null, "the inbound hello reached the attached host");

        // A detach leaves the game's delegate in place, so what protects the message is the owner slot, not the
        // registration: a process with no participant must drop it rather than answer for a session it left.
        first.Detach();
        mark = Api.Mark();
        Api.Deliver(NetworkProtocol.HelloEvent, 2, hello);
        suite.Equal(mark, Api.SentCount, "a process with no participant drops the inbound hello");
        suite.Equal(1, Api.HandlerCount(NetworkProtocol.HelloEvent), "the dropped message did not cost a registration");

        // A peer this process has never seen, so the verdict below can only have come from this delivery.
        var second = Harness.Attach(SessionRole.Host, 3);
        mark = Api.Mark();
        Api.Deliver(NetworkProtocol.HelloEvent, 4, NetworkCodec.Write(Harness.HelloBody(SessionRole.Client, 4)));
        suite.Equal(mark + 1, Api.SentCount, "the participant attached now answers the next inbound hello");
        suite.True(second.Handshake.Peer(4) != null && first.Handshake.Peer(4) == null,
            "the inbound message went to the participant that owns the events now");
    }

    private static void Sending(Suite suite)
    {
        suite.Case("transport: send uses the frozen name and channel");
        Harness.Open();
        var sender = Harness.Attach(SessionRole.Client, 2);
        Harness.Send(sender, sender.Hello());
        suite.Equal(NetworkProtocol.HelloEvent, Api.Sent[0].EventName, "hello event name");
        suite.True(Api.Sent[0].FreeSized, "hello travels free-sized");
        suite.Equal(SNetwork.SNet_ChannelType.GameOrderCritical, Api.Sent[0].Channel, "hello channel");
        suite.Throws<NetworkContractException>(
            () => ForgeNetworkTransport.Send(new NetworkMessage((NetworkMessageKind)99, new HelloAckMessage())),
            NetworkCodes.EventNameUnknown, "an unknown kind cannot be sent");
        // A free-sized event names its own body length, so a body of no bytes has nothing to carry. It cannot happen
        // on this layer's encode path — every message's metadata is bytes — which is what this pins.
        suite.True(NetworkCodec.Write(new FactMessage()).Length > 0, "a fact body always carries bytes");
        suite.True(NetworkCodec.Write(new HelloMessage()).Length > 0, "a hello body always carries bytes");
    }

    private static void RoundTrip(Suite suite)
    {
        suite.Case("transport: free-sized round trip");
        var (host, client) = Harness.Pair();
        Harness.Send(client, client.Hello());
        var hello = Harness.Last()!.Value;
        suite.Equal(NetworkMessageKind.Hello, hello.Kind, "the body decodes back to a hello");
        suite.Equal(2UL, hello.Hello.SenderSession, "the body keeps its session");
        var ack = Harness.DeliverLast(host)!.Value;
        suite.Equal((byte)1, ack.Ack.Accepted, "the host accepted the transported hello");
        Harness.Send(host, ack);
        var ackEnvelope = Harness.Last()!.Value;
        suite.Equal(NetworkMessageKind.HelloAck, ackEnvelope.Kind, "the ack travels typed");
        suite.False(Api.Sent[^1].FreeSized, "the ack is a typed payload");
        Harness.DeliverLast(client);
        suite.True(client.Handshake.MatchesHost(1), "the client accepted the transported ack");

        suite.Case("transport: an epoch broadcast travels typed");
        var broadcast = host.AdvanceWorld(1, WorldChangeReason.NewGeneration);
        Harness.Send(host, broadcast);
        suite.Equal(NetworkProtocol.WorldEpochEvent, Api.Sent[^1].EventName, "epoch event name");
        suite.False(Api.Sent[^1].FreeSized, "the epoch broadcast is a typed payload");
        suite.Equal(1L, Harness.Last()!.Value.Epoch.WorldEpoch, "the epoch survives the trip");
    }

    private static void Detach(Suite suite)
    {
        suite.Case("transport: detach releases the events");
        Harness.Open();
        var host = Harness.Attach(SessionRole.Host, 1);
        host.Detach();
        suite.True(!host.Registered, "a detached layer reports unregistered");
        var before = Api.SentCount;
        suite.Throws<NetworkContractException>(() => Harness.Send(host, host.Hello()), NetworkCodes.HelloUnsent, "a detached layer cannot send");
        suite.Equal(before, Api.SentCount, "nothing reached the channel");
        var second = Harness.Attach(SessionRole.Client, 2);
        suite.True(second.Registered, "another participant may take the events over");
        Harness.Send(second, second.Hello());
        suite.Equal(before + 1, Api.SentCount, "the new owner can send");
    }

    private static NetworkMessageKind[] MessageKinds() => new[]
    {
        NetworkMessageKind.Hello, NetworkMessageKind.HelloAck, NetworkMessageKind.WorldEpoch,
        NetworkMessageKind.CommandRequest, NetworkMessageKind.CommandResult, NetworkMessageKind.Fact
    };
}
