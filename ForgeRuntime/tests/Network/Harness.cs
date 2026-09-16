using System;
using System.Collections.Generic;
using System.Text;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// Test rig around the transport double: it routes recorded sends to a receiving <see cref="NetworkHost"/> the way
/// the real transport would on the other machine.
/// </summary>
internal static class Harness
{
    internal static readonly RuntimeIdentitySummary Identity = new("NAinfini.ForgeRuntime", "1.2.0", "2.0.0", "20403457");

    internal static readonly PlanIdentity[] Plans =
    {
        new("forge.plan.alpha", "resource.alpha", "r3", "binding.a,binding.b"),
        new("forge.plan.beta", "resource.beta", "r1", "binding.c")
    };

    /// <summary>The session number the fake lobby treats as the master.</summary>
    internal static ulong Master { get; set; }

    /// <summary>
    /// Builds a participant. A plan set is what plan discovery adopted: <paramref name="adopted"/> false is the state
    /// before discovery has run, in which the handshake has no set to compare and announces none.
    /// </summary>
    internal static NetworkHost Build(SessionRole role, long worldEpoch = 0, IReadOnlyList<PlanIdentity>? plans = null,
        RuntimeIdentitySummary? identity = null, bool adopted = true)
    {
        var host = new NetworkHost(new NetworkHostConfiguration(role, identity ?? Identity, adopted ? plans ?? Plans : null, worldEpoch));
        host.SenderIsMaster = sender => sender == Master;
        return host;
    }

    /// <summary>The hello one participant would send, built without attaching it to the transport.</summary>
    internal static NetworkMessage Hello(SessionRole role, ulong session, long worldEpoch = 0, IReadOnlyList<PlanIdentity>? plans = null,
        RuntimeIdentitySummary? identity = null)
    {
        var handshake = new NetworkHandshake(role, identity ?? Identity, plans ?? Plans);
        handshake.AdoptEpoch(worldEpoch);
        handshake.BindLocalSession(session);
        return new NetworkMessage(NetworkMessageKind.Hello, handshake.Hello(session));
    }

    /// <summary>The hello body alone, for checks that deliver a message rather than a send.</summary>
    internal static HelloMessage HelloBody(SessionRole role, ulong session = 2, long worldEpoch = 0, IReadOnlyList<PlanIdentity>? plans = null,
        RuntimeIdentitySummary? identity = null) => Hello(role, session, worldEpoch, plans, identity).Hello;

    /// <summary>
    /// Starts a case from a clean recording: whatever a previous case attached is released, because a case must not
    /// inherit another case's listeners. The game-side registration is not touched — GTFO-API claims a name once per
    /// process — so a case that attaches reuses it, which is exactly what the layer has to do in the game.
    /// </summary>
    internal static void Open()
    {
        for (var index = _attached.Count - 1; index >= 0; index--) _attached[index].Detach();
        _attached.Clear();
        Api.ClearRecording();
        Master = 0;
    }

    /// <summary>Registers a participant against the double and returns it. <see cref="Open"/> must have run first.
    /// The address is what a requested step names to reach this participant; the rig spells it as the session,
    /// because one process standing in for two machines has no player slot to read.</summary>
    internal static NetworkHost Attach(SessionRole role, ulong session, long worldEpoch = 0, IReadOnlyList<PlanIdentity>? plans = null,
        RuntimeIdentitySummary? identity = null, bool adopted = true, string? address = null)
    {
        var host = Build(role, worldEpoch, plans, identity, adopted);
        host.Attach(session, address ?? Address(session));
        return Track(host);
    }

    /// <summary>The address this rig answers a requested step at. Production reads the game's own player address;
    /// here the session is the one identity both participants of a case can name.</summary>
    internal static string Address(ulong session) => session.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Remembers a participant a case built itself, so <see cref="Open"/> detaches it like any other.</summary>
    internal static NetworkHost Track(NetworkHost host)
    {
        _attached.Add(host);
        return host;
    }

    /// <summary>A host on session 1 and a client on session 2, both attached, with session 1 as the master.</summary>
    internal static (NetworkHost Host, NetworkHost Client) Pair(long worldEpoch = 0)
    {
        Open();
        var host = Attach(SessionRole.Host, 1, worldEpoch);
        Master = 1;
        return (host, Attach(SessionRole.Client, 2, worldEpoch));
    }

    /// <summary>A host and a client that are built but not registered, for checks about receive-only decisions.</summary>
    internal static (NetworkHost Host, NetworkHost Client) Unregistered()
    {
        Open();
        var host = Build(SessionRole.Host);
        Master = 1;
        return (host, Build(SessionRole.Client));
    }

    private static readonly List<NetworkHost> _attached = new();

    /// <summary>The last message this process sent, decoded, or null when it sent nothing since the last case.</summary>
    internal static NetworkEnvelope? Last() => Api.SentCount == 0 ? null : Envelope(Api.Sent[^1]);

    /// <summary>Converts one recorded send back into an envelope, exactly as the receive path decodes it.</summary>
    internal static NetworkEnvelope Envelope(NetworkApiDouble.Send sent)
    {
        if (sent.FreeSized)
        {
            var kind = sent.EventName switch
            {
                NetworkProtocol.HelloEvent => NetworkMessageKind.Hello,
                NetworkProtocol.CommandRequestEvent => NetworkMessageKind.CommandRequest,
                NetworkProtocol.FactEvent => NetworkMessageKind.Fact,
                _ => throw new InvalidOperationException("Unexpected free-sized event " + sent.EventName)
            };
            return NetworkCodec.Read(kind, (byte[])sent.Payload, sent.Sender);
        }
        return sent.EventName switch
        {
            NetworkProtocol.HelloAckEvent => new NetworkEnvelope(NetworkMessageKind.HelloAck, sent.Sender, (HelloAckMessage)sent.Payload),
            NetworkProtocol.WorldEpochEvent => new NetworkEnvelope(NetworkMessageKind.WorldEpoch, sent.Sender, (WorldEpochMessage)sent.Payload),
            NetworkProtocol.CommandResultEvent => new NetworkEnvelope(NetworkMessageKind.CommandResult, sent.Sender, (CommandResultMessage)sent.Payload),
            _ => throw new InvalidOperationException("Unexpected event " + sent.EventName)
        };
    }

    /// <summary>Sends as one participant: the API attributes the send to that participant's session, exactly as the
    /// game attributes it to the local session, so the receiver sees the sender SNet would report.</summary>
    internal static void Send(NetworkHost sender, NetworkMessage message)
    {
        NetworkApiDouble.Sender = sender.LocalSession;
        ForgeNetworkTransport.Send(message);
    }

    /// <summary>Delivers the last recorded send to a receiver, as the sender's own session.</summary>
    internal static NetworkMessage? DeliverLast(NetworkHost receiver) => Dispatch(receiver, Last()!.Value);

    /// <summary>
    /// Hands one envelope to a participant the way an inbound datagram reaches the game. The receiver is the process
    /// that is now running, so anything its receive path sends — the answer to a hello, or a hello a verdict observer
    /// owes — is attributed to that process's session rather than to whoever sent the message being delivered.
    /// </summary>
    internal static NetworkMessage? Dispatch(NetworkHost receiver, NetworkEnvelope envelope)
    {
        NetworkApiDouble.Sender = receiver.LocalSession;
        return receiver.Receive(envelope);
    }

    /// <summary>
    /// Runs one hello/hello-ack exchange between two attached participants and returns the ack the host produced.
    /// Both sides send through the transport, so the bodies travel the same encode/decode path the real sender
    /// would use, and each send is delivered to the participant it is addressed to.
    /// </summary>
    internal static HelloAckMessage Handshake(NetworkHost host, NetworkHost client)
    {
        if (!host.Registered || !client.Registered)
            throw new InvalidOperationException("Both participants must be attached before a handshake.");
        Send(client, client.Hello());
        var ack = DeliverLast(host)!.Value.Ack;
        Send(host, new NetworkMessage(NetworkMessageKind.HelloAck, ack));
        DeliverLast(client);
        return ack;
    }

    /// <summary>Builds a request body as the given client would.</summary>
    internal static CommandRequestMessage Request(ulong session, long worldEpoch, ulong eventId, string planId, string bindingId, string payload)
    {
        var message = new CommandRequestMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = session,
            WorldEpoch = worldEpoch,
            EventId = eventId,
            LifeEpoch = 1,
            EntityId = 7,
            BindingIndex = 2,
            NodeIndex = 0,
            ScopeKind = 1
        };
        message.PlanId = planId;
        message.ResourceId = bindingId;
        message.CommandId = "scope.door";
        message.PayloadLength = message.SetPayload(0, Encoding.Latin1.GetBytes(payload));
        return message;
    }

    /// <summary>Sends one request body as the given client and returns the result the host answered with, if any.</summary>
    internal static CommandResultMessage? Deliver(NetworkHost host, ulong clientSession, CommandRequestMessage message)
    {
        NetworkApiDouble.Sender = clientSession;
        ForgeNetworkTransport.Send(new NetworkMessage(NetworkMessageKind.CommandRequest, message));
        return DeliverLast(host) is { } reply && reply.Kind == NetworkMessageKind.CommandResult ? reply.Result : null;
    }

    /// <summary>
    /// Carries recorded sends between two participants until neither of them answers any more: every recorded send
    /// from <paramref name="from"/> on goes to the other side, and the message a receive returns is sent the way the
    /// transport sends it. This is the real two-machine loop with both machines in one process, which is what makes
    /// an exchange of hellos observable end to end. Returns where to resume, so a case can drive one phase of an
    /// exchange at a time.
    /// </summary>
    internal static int Pump(NetworkHost host, NetworkHost client, int from = 0, int limit = 32)
    {
        var cursor = from;
        while (cursor < Api.SentCount && cursor - from < limit)
        {
            var sent = Api.Sent[cursor++];
            var receiver = sent.Sender == client.LocalSession ? host : client;
            if (Dispatch(receiver, Envelope(sent)) is { } reply) Send(receiver, reply);
        }
        return cursor;
    }
}
