using System;
using System.Collections.Generic;
using SNetwork;

namespace ForgeRuntime.Network;

/// <summary>
/// The transport over GTFO-API's <c>NetworkAPI</c>: event names, one registration per message type, and the
/// send/receive calls. It carries messages and nothing else — no plan discovery, no gameplay gate, no level
/// lifecycle. <see cref="NetworkHost"/> is the only caller and owns every decision.
/// <para>
/// Event names are the frozen protocol names, never built from a message field. A payload is either a
/// Marshal-structured value type (registered and invoked as <c>RegisterEvent&lt;T&gt;</c>/<c>InvokeEvent&lt;T&gt;</c>)
/// or a body whose length is part of its content (the hello and request/fact bodies, which carry variable-size
/// payloads). Each type is registered once per process, so a type mismatch is a build or startup fault
/// instead of a dropped packet.
/// </para>
/// <para>
/// GTFO-API has no unregister and refuses a second registration of a name, so <see cref="Release"/> does not touch
/// the game's registration: it drops this layer's listener, so the delegate the game still holds delivers nothing,
/// and a later <see cref="Register"/> takes the names over without registering them again.
/// </para>
/// </summary>
internal static class ForgeNetworkTransport
{
    /// <summary>The channel every Forge message travels on. Gameplay-affecting traffic is order-critical.</summary>
    internal const SNet_ChannelType Channel = SNet_ChannelType.GameOrderCritical;

    private sealed record Listener(long Token, Func<NetworkEnvelope, NetworkMessage?> Receive);

    /// <summary>
    /// The one listener the protocol events deliver to. A process runs one Forge participant, so at most one attach
    /// owns the events at a time; a later attach takes them over, which is what makes the game's single registration
    /// serve its second session. The token tells a released owner apart from the one that owns the events now.
    /// </summary>
    private static Listener? _owner;
    private static long _nextToken;
    /// <summary>The game-side registration is process-lifetime: GTFO-API cannot unregister and faults on a repeat.</summary>
    private static bool _gameRegistered;

    /// <summary>The event name a message kind is registered under; the kind is a protocol value, never user data.</summary>
    internal static string EventName(NetworkMessageKind kind) => NetworkProtocol.EventName(kind);

    /// <summary>
    /// Registers every Forge event for one listener and returns the token that identifies it. GTFO-API faults on a
    /// second registration of a name and has no unregister, so the game-side registration happens once per process,
    /// on the first attach; every later attach only takes the names over. A payload is proved blittable and inside
    /// the message budget on that first registration, so a layout edit that breaks the transport fails there rather
    /// than at the first send.
    /// </summary>
    internal static long Register(Func<NetworkEnvelope, NetworkMessage?> receive)
    {
        if (receive == null) throw new NetworkContractException(NetworkCodes.EventNameUnknown, "Registration needs a receive target.");
        if (!_gameRegistered)
        {
            NetworkWire.RequireBlittable<HelloAckMessage>(NetworkProtocol.HelloAckEvent);
            NetworkWire.RequireBlittable<WorldEpochMessage>(NetworkProtocol.WorldEpochEvent);
            NetworkWire.RequireBlittable<CommandResultMessage>(NetworkProtocol.CommandResultEvent);
            GTFO.API.NetworkAPI.RegisterEvent(NetworkProtocol.HelloAckEvent, (ulong sender, HelloAckMessage payload) => Dispatch(new NetworkEnvelope(NetworkMessageKind.HelloAck, sender, payload)));
            GTFO.API.NetworkAPI.RegisterEvent(NetworkProtocol.WorldEpochEvent, (ulong sender, WorldEpochMessage payload) => Dispatch(new NetworkEnvelope(NetworkMessageKind.WorldEpoch, sender, payload)));
            GTFO.API.NetworkAPI.RegisterEvent(NetworkProtocol.CommandResultEvent, (ulong sender, CommandResultMessage payload) => Dispatch(new NetworkEnvelope(NetworkMessageKind.CommandResult, sender, payload)));
            GTFO.API.NetworkAPI.RegisterFreeSizedEvent(NetworkProtocol.HelloEvent, (sender, bytes) => Dispatch(NetworkCodec.Read(NetworkMessageKind.Hello, bytes, sender)));
            GTFO.API.NetworkAPI.RegisterFreeSizedEvent(NetworkProtocol.CommandRequestEvent, (sender, bytes) => Dispatch(NetworkCodec.Read(NetworkMessageKind.CommandRequest, bytes, sender)));
            GTFO.API.NetworkAPI.RegisterFreeSizedEvent(NetworkProtocol.FactEvent, (sender, bytes) => Dispatch(NetworkCodec.Read(NetworkMessageKind.Fact, bytes, sender)));
            _gameRegistered = true;
        }
        var token = ++_nextToken;
        _owner = new Listener(token, receive);
        return token;
    }

    /// <summary>Drops one listener. Its receive delegate stops being consulted, and a later <see cref="Register"/>
    /// may take the names over without touching the game's registration. Releasing an owner that a later attach has
    /// already replaced changes nothing.</summary>
    internal static void Release(long token)
    {
        if (token == 0 || _owner is not { } owner || owner.Token != token) return;
        _owner = null;
    }

    /// <summary>True while this layer owns the protocol's event names and has a listener to deliver to.</summary>
    internal static bool Registered => _gameRegistered && _owner != null;

    /// <summary>
    /// Sends one message. The API attributes a send to the local session, which is what this layer means by the
    /// sender: a participant that never attached has no session and cannot reach this call.
    /// </summary>
    internal static void Send(NetworkMessage message)
    {
        // A send with no listener is a fault at the call site: this layer only sends from an attached participant,
        // and an unattached process has no business putting Forge traffic on the channel.
        if (_owner == null) throw new NetworkContractException(NetworkCodes.HelloUnsent, "No Forge listener is registered, so nothing can be sent.");
        switch (message.Kind)
        {
            case NetworkMessageKind.HelloAck:
                GTFO.API.NetworkAPI.InvokeEvent(NetworkProtocol.HelloAckEvent, message.Ack, Channel);
                return;
            case NetworkMessageKind.WorldEpoch:
                GTFO.API.NetworkAPI.InvokeEvent(NetworkProtocol.WorldEpochEvent, message.Epoch, Channel);
                return;
            case NetworkMessageKind.CommandResult:
                GTFO.API.NetworkAPI.InvokeEvent(NetworkProtocol.CommandResultEvent, message.Result, Channel);
                return;
            case NetworkMessageKind.Hello:
                GTFO.API.NetworkAPI.InvokeFreeSizedEvent(NetworkProtocol.HelloEvent, NetworkCodec.Write(message.Hello), Channel);
                return;
            case NetworkMessageKind.CommandRequest:
                GTFO.API.NetworkAPI.InvokeFreeSizedEvent(NetworkProtocol.CommandRequestEvent, NetworkCodec.Write(message.Request), Channel);
                return;
            case NetworkMessageKind.Fact:
                GTFO.API.NetworkAPI.InvokeFreeSizedEvent(NetworkProtocol.FactEvent, NetworkCodec.Write(message.Fact), Channel);
                return;
            default:
                throw new NetworkContractException(NetworkCodes.EventNameUnknown, "Message kind " + message.Kind + " cannot be sent.");
        }
    }

    /// <summary>
    /// Hands one decoded message to the listener that owns the protocol events now, or drops it while none does: the
    /// game keeps calling the delegate it holds, so this is the only place a detach and a later attach take effect.
    /// A message the decision layer returns is its outbound reply — a hello's ack, a request's result — and is sent
    /// from here, because the game's own delegate returns void and this is the last frame that can see it.
    /// </summary>
    private static void Dispatch(NetworkEnvelope envelope)
    {
        if (_owner is not { } owner) return;
        if (owner.Receive(envelope) is { } reply) Send(reply);
    }
}
