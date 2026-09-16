using System;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// Wire-format checks: every message is a blittable struct inside one datagram, the fixed text slots round-trip
/// exactly, the free-sized codec is the inverse of itself, and the event names stay inside the Forge prefix the
/// protocol version owns.
/// </summary>
internal static class WireChecks
{
    /// <summary>A payload with a reference field: the shape <see cref="NetworkWire.RequireBlittable{T}"/> must
    /// refuse. The fields are never read, only their types matter.</summary>
    private struct ReferencePayload
    {
        public ushort Header;
        public string Text;

        internal static ReferencePayload Instance() => new() { Header = 1, Text = "x" };
    }

    private struct EmptyPayload
    {
    }

    internal static void Run(Suite suite)
    {
        Harness.Open();
        suite.Case("wire: message sizes");
        foreach (var (name, size) in Sizes())
        {
            suite.True(size > 0 && size <= NetworkProtocol.MaximumDatagramBytes, name + " is " + size + " bytes, outside one datagram");
            var blittable = 0;
            try
            {
                blittable = name switch
                {
                    "hello" => NetworkWire.RequireBlittable<HelloMessage>(name),
                    "hello-ack" => NetworkWire.RequireBlittable<HelloAckMessage>(name),
                    "world-epoch" => NetworkWire.RequireBlittable<WorldEpochMessage>(name),
                    "command-request" => NetworkWire.RequireBlittable<CommandRequestMessage>(name),
                    "command-result" => NetworkWire.RequireBlittable<CommandResultMessage>(name),
                    "fact" => NetworkWire.RequireBlittable<FactMessage>(name),
                    _ => 0
                };
            }
            catch (NetworkContractException error) { suite.True(false, name + " payload rejected: " + error.Code); }
            suite.Equal(size, blittable, name + " Marshal size");
        }

        suite.Case("wire: declared layout matches Marshal");
        suite.Equal(Suite.SizeOf<HelloMessage>(), HelloMessage.DeclaredBytes, "hello declared bytes");
        suite.Equal(Suite.SizeOf<HelloAckMessage>(), HelloAckMessage.DeclaredBytes, "hello-ack declared bytes");
        suite.Equal(Suite.SizeOf<WorldEpochMessage>(), WorldEpochMessage.DeclaredBytes, "world-epoch declared bytes");
        suite.Equal(Suite.SizeOf<CommandRequestMessage>(), CommandRequestMessage.DeclaredBytes, "command-request declared bytes");
        suite.Equal(Suite.SizeOf<CommandResultMessage>(), CommandResultMessage.DeclaredBytes, "command-result declared bytes");
        suite.Equal(Suite.SizeOf<FactMessage>(), FactMessage.DeclaredBytes, "fact declared bytes");
        suite.Equal(HelloMessage.MetadataBytes + HelloMessage.SlotCount * NetworkProtocol.TextSlotStride, HelloMessage.DeclaredBytes, "a hello is its metadata plus its four identity slots");
        suite.Equal(HelloMessage.DeclaredBytes - HelloMessage.MetadataBytes, 4 * NetworkProtocol.TextSlotStride, "the hello's plan set is its count and digest, not text");
        suite.Equal(HelloMessage.DeclaredBytes - HelloMessage.MetadataBytes, HelloMessage.SlotCount * NetworkProtocol.TextSlotStride, "the hello's slot budget is what the layout declares");
        suite.Equal(CommandRequestMessage.MetadataBytes + 4 * NetworkProtocol.TextSlotStride + NetworkProtocol.PayloadSlotBytes, CommandRequestMessage.DeclaredBytes, "a request is its metadata, four text slots and one payload slot");
        suite.Equal(NetworkProtocol.TextSlotStride, HelloMessage.SlotStride, "the slot stride the codec walks");
        suite.Equal(NetworkProtocol.PayloadSlotBytes, FactMessage.PayloadStride, "the payload stride the codec walks");

        suite.Case("wire: non-blittable payload");
        suite.Throws<NetworkContractException>(() => NetworkWire.RequireBlittable<ReferencePayload>("reference"), NetworkCodes.PayloadNotBlittable, "a reference field is refused");
        suite.Throws<NetworkContractException>(() => NetworkWire.RequireBlittable<EmptyPayload>("empty"), NetworkCodes.PayloadNotBlittable, "a payload without fields is refused");

        suite.Case("wire: event names");
        foreach (var name in NetworkProtocol.EventNames)
            suite.True(name.StartsWith(NetworkProtocol.Prefix, StringComparison.Ordinal), name + " is outside the protocol prefix");
        suite.Equal(NetworkProtocol.Prefix + "hello", ForgeNetworkTransport.EventName(NetworkMessageKind.Hello), "hello event name");
        suite.Throws<NetworkContractException>(() => ForgeNetworkTransport.EventName((NetworkMessageKind)99), NetworkCodes.EventNameUnknown, "an unknown kind has no event name");

        suite.Case("wire: text slots");
        var text = new HelloAckMessage();
        text.ReasonCode = "forge.plan.alpha";
        suite.Equal("forge.plan.alpha", text.ReasonCode, "text round-trip");
        suite.Equal((ushort)"forge.plan.alpha".Length, text.SlotLength(HelloAckMessage.ReasonCodeSlot), "text length");
        text.ReasonCode = "";
        suite.Equal("", text.ReasonCode, "empty text");
        suite.Equal(0, text.SlotLength(HelloAckMessage.ReasonCodeSlot), "empty text has no length");
        suite.Throws<NetworkContractException>(() => text.ReasonCode = new string('x', NetworkProtocol.TextSlotBytes + 1), NetworkCodes.PayloadTooLarge, "over-long text is refused");
        suite.Throws<NetworkContractException>(() => text.ReasonCode = "caf\u00e9 \u4e2d\u6587", NetworkCodes.FormatInvalid, "non-Latin-1 text is refused");
        text.ReasonCode = "caf\u00e9";
        suite.Equal("caf\u00e9", text.ReasonCode, "Latin-1 text keeps its bytes");
        suite.Throws<NetworkContractException>(() => text.SlotText(99), NetworkCodes.FormatInvalid, "an out-of-range slot index is refused");
        suite.Equal(NetworkProtocol.TextSlotBytes, WireText.Clip(new string('y', NetworkProtocol.TextSlotBytes + 10), NetworkProtocol.TextSlotBytes).Length, "diagnostic text is clipped to the slot");
        suite.Equal("caf?", WireText.Clip("caf\u4e2d", 8), "a clipped diagnostic replaces what a slot cannot hold");

        suite.Case("wire: plan-set digest");
        var hello = new HelloMessage { ProtocolVersion = NetworkProtocol.Version, PlanCount = 2 };
        var digest = PlanSetDigest.Of(Harness.Plans);
        hello.SetPlanSetDigest(digest);
        suite.Equal((byte)2, hello.PlanCount, "plan count");
        suite.Equal(NetworkProtocol.PlanSetDigestBytes, hello.PlanSetDigest().Length, "the digest is thirty-two bytes");
        suite.True(PlanSetDigest.Equal(digest, hello.PlanSetDigest()), "the digest survives the struct");
        suite.Equal(NetworkProtocol.PlanSetDigestBytes / sizeof(ulong), hello.DigestWords().Length, "the digest travels as four words of the fixed layout");
        suite.Throws<NetworkContractException>(() => hello.SetPlanSetDigest(new byte[NetworkProtocol.PlanSetDigestBytes - 1]), NetworkCodes.FormatInvalid, "a digest that is not thirty-two bytes is refused");
        suite.Equal(HelloMessage.DeclaredBytes, Suite.SizeOf<HelloMessage>(), "the digest is inside the fixed layout, not an array");

        suite.Case("wire: payload slots");
        var request = new CommandRequestMessage { PayloadLength = 0 };
        request.PayloadLength = request.SetPayload(0, new byte[] { 1, 2, 3, 4 });
        suite.Equal(4, request.PayloadLength, "request payload length");
        suite.Equal((byte)3, request.PayloadBytes(0, request.PayloadLength)[2], "request payload byte");
        suite.Throws<NetworkContractException>(() => request.SetPayload(0, new byte[NetworkProtocol.PayloadSlotBytes + 1]), NetworkCodes.PayloadTooLarge, "over-long payload is refused");
        suite.Throws<NetworkContractException>(() => request.PayloadBytes(0, NetworkProtocol.PayloadSlotBytes + 1), NetworkCodes.FormatInvalid, "out-of-range declared length is refused");
        suite.Equal(3, request.PayloadBytes(0, 3).Length, "a shorter declared length reads fewer bytes");
        suite.Throws<NetworkContractException>(() => new HelloAckMessage().PayloadBytes(0, 0), NetworkCodes.EventNameUnknown, "a message without a payload slot refuses one");

        suite.Case("wire: codec round-trip");
        var sent = Harness.Request(7, 1, 42, "forge.plan.alpha", "binding.a", "door-open");
        var body = NetworkCodec.Write(sent);
        suite.Equal(Suite.SizeOf<CommandRequestMessage>(), body.Length, "request body is the message size");
        var envelope = NetworkCodec.Read(NetworkMessageKind.CommandRequest, body, 7);
        suite.Equal(NetworkMessageKind.CommandRequest, envelope.Kind, "decoded kind");
        suite.Equal(7UL, envelope.Sender, "decoded sender comes from the transport");
        suite.Equal(42UL, envelope.Request.EventId, "decoded event id");
        suite.Equal(1L, envelope.Request.WorldEpoch, "decoded world epoch");
        suite.Equal("forge.plan.alpha", envelope.Request.PlanId, "decoded plan id");
        suite.Equal("binding.a", envelope.Request.ResourceId, "decoded resource id");
        suite.Equal("scope.door", envelope.Request.CommandId, "decoded command id");
        suite.Equal("door-open", System.Text.Encoding.Latin1.GetString(envelope.Request.PayloadBytes(0, envelope.Request.PayloadLength)), "decoded payload");
        suite.Throws<NetworkContractException>(() => NetworkCodec.Read(NetworkMessageKind.CommandRequest, new byte[3], 7), NetworkCodes.FormatInvalid, "a short body is refused");
        suite.Throws<NetworkContractException>(() => NetworkCodec.Read(NetworkMessageKind.CommandResult, body, 7), NetworkCodes.EventNameUnknown, "a typed kind has no free-sized body");

        suite.Case("wire: codec hello round-trip");
        var helloBody = NetworkCodec.Write(Harness.Hello(SessionRole.Host, 1).Hello);
        var helloEnvelope = NetworkCodec.Read(NetworkMessageKind.Hello, helloBody, 3);
        suite.Equal(NetworkProtocol.Version, helloEnvelope.Hello.ProtocolVersion, "hello protocol version");
        suite.Equal(1UL, helloEnvelope.Hello.SenderSession, "hello sender session");
        suite.Equal((byte)SessionRole.Host, helloEnvelope.Hello.Role, "hello role");
        suite.Equal((byte)2, helloEnvelope.Hello.PlanCount, "hello plan count");
        suite.Equal("NAinfini.ForgeRuntime", helloEnvelope.Hello.RuntimeId, "hello runtime id");
        suite.Equal("20403457", helloEnvelope.Hello.GameBuild, "hello game build");
        suite.Equal(HelloMessage.SlotCount, 4, "a hello carries four identity slots");
        suite.True(PlanSetDigest.Equal(PlanSetDigest.Of(Harness.Plans), helloEnvelope.Hello.PlanSetDigest()), "hello plan-set digest");

        suite.Case("wire: codec fact round-trip");
        var fact = new FactMessage { ProtocolVersion = NetworkProtocol.Version, WorldEpoch = 4, LifeEpoch = 2, EntityId = 11, Sequence = 9, PayloadLength = 0 };
        fact.PayloadLength = fact.SetPayload(0, new byte[] { 7, 7, 7 });
        var factEnvelope = NetworkCodec.Read(NetworkMessageKind.Fact, NetworkCodec.Write(fact), 5);
        suite.Equal(9UL, factEnvelope.Fact.Sequence, "fact sequence");
        suite.Equal(3, factEnvelope.Fact.PayloadLength, "fact payload length");
        suite.Equal((byte)7, factEnvelope.Fact.PayloadBytes(0, factEnvelope.Fact.PayloadLength)[1], "fact payload byte");

        UnwrittenSlots(suite);
    }

    /// <summary>
    /// A message built with an object initializer leaves every slot it does not mention holding whatever its storage
    /// had. Those slots must reach the wire as empty ones, because nothing about an unmentioned slot is defined. This
    /// is the shape a command request has: it fills the three identity slots and leaves the endpoint one to mean
    /// "no endpoint".
    /// </summary>
    private static void UnwrittenSlots(Suite suite)
    {
        suite.Case("wire: an unwritten slot travels empty");
        var request = new CommandRequestMessage { ProtocolVersion = NetworkProtocol.Version, SenderSession = 2, EventId = 3 };
        request.PlanId = "forge.plan.alpha";
        request.ResourceId = "binding.a";
        request.CommandId = "scope.door";
        var encoded = NetworkCodec.Write(request);
        suite.Equal(0, CountNonZero(encoded, CommandRequestMessage.MetadataBytes + CommandRequestMessage.SlotStride * 3, CommandRequestMessage.SlotStride),
            "the unmentioned endpoint slot is zero on the wire");
        var decoded = NetworkCodec.Read(NetworkMessageKind.CommandRequest, encoded, 2).Request;
        suite.Equal("forge.plan.alpha", decoded.PlanId, "the written plan id survives");
        suite.Equal("binding.a", decoded.ResourceId, "the written resource id survives");
        suite.Equal("scope.door", decoded.CommandId, "the written command id survives");
        suite.Equal("", decoded.Endpoint, "the unmentioned endpoint slot decodes as empty text");
        suite.Equal(0, decoded.SlotLength(CommandRequestMessage.EndpointSlot), "an empty slot declares no length");

        // A hello whose digest was never written carries zeroes rather than the memory the caller's stack held: the
        // digest sits in the metadata, so the codec writes the four words the struct declares, filled or not.
        var hello = new HelloMessage { ProtocolVersion = NetworkProtocol.Version, SenderSession = 4, PlanCount = 1 };
        hello.RuntimeId = "NAinfini.ForgeRuntime";
        var helloBody = NetworkCodec.Write(hello);
        var helloRow = NetworkCodec.Read(NetworkMessageKind.Hello, helloBody, 4).Hello;
        suite.Equal("NAinfini.ForgeRuntime", helloRow.RuntimeId, "the written runtime id survives");
        suite.Equal(0, CountNonZero(helloBody, HelloMessage.MetadataBytes - NetworkProtocol.PlanSetDigestBytes, NetworkProtocol.PlanSetDigestBytes),
            "an unwritten plan-set digest is zero on the wire");

        // A typed payload is never encoded: the API Marshals the struct itself, so the struct a sender builds is what
        // travels. Building one with an object initializer is what makes its unmentioned slots empty — every field,
        // including each slot's length, starts at zero — and that is how the host's result message keeps its
        // identity slots.
        var result = new CommandResultMessage { ProtocolVersion = NetworkProtocol.Version, EventId = 3, FactCount = 1 };
        result.Code = NetworkCodes.Duplicate;
        result.Detail = "replayed";
        suite.Equal(0, result.SlotLength(CommandResultMessage.PlanIdSlot), "a result that names no plan declares no plan length");
        suite.Equal(0, result.SlotLength(CommandResultMessage.CommandIdSlot), "a result that names no command declares no command length");
        suite.Equal("", result.PlanId, "a result that names no plan reads as empty text");
        suite.Equal("", result.CommandId, "a result that names no command reads as empty text");
        suite.Equal(NetworkCodes.Duplicate, result.Code, "the written code survives");
        suite.Equal("replayed", result.Detail, "the written detail survives");
        suite.Equal(Suite.SizeOf<CommandResultMessage>(), CommandResultMessage.DeclaredBytes, "the result's declared size is its Marshal size");

        // The same rule seen from the other side: a message whose caller did fill a slot must not let that content
        // onto the wire once the slot is emptied. The codec writes each frame from the slot's declared length, so an
        // emptied slot travels empty rather than as whatever the content fields still hold.
        request.SetSlotText(CommandRequestMessage.EndpointSlot, "");
        var emptied = NetworkCodec.Read(NetworkMessageKind.CommandRequest, NetworkCodec.Write(request), 2).Request;
        suite.Equal("", emptied.Endpoint, "an emptied slot travels empty");
        suite.Equal(0, emptied.SlotLength(CommandRequestMessage.EndpointSlot), "an emptied slot declares no length");
    }

    private static int CountNonZero(byte[] body, int offset, int length)
    {
        var count = 0;
        for (var index = offset; index < offset + length; index++) if (body[index] != 0) count++;
        return count;
    }

    private static (string Name, int Size)[] Sizes() => new[]
    {
        ("hello", Suite.SizeOf<HelloMessage>()),
        ("hello-ack", Suite.SizeOf<HelloAckMessage>()),
        ("world-epoch", Suite.SizeOf<WorldEpochMessage>()),
        ("command-request", Suite.SizeOf<CommandRequestMessage>()),
        ("command-result", Suite.SizeOf<CommandResultMessage>()),
        ("fact", Suite.SizeOf<FactMessage>())
    };
}
