using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ForgeRuntime.Network;

/// <summary>
/// Guards the three properties the transport depends on: a message type is structurally blittable (so GTFO-API's
/// Marshal copy carries it), its Marshal size equals the layout the codec writes (so the two byte views cannot
/// drift), and it fits one datagram. All three are checked where a message type is registered, so a layout edit
/// that breaks any of them fails at startup instead of at the first send.
/// </summary>
internal static class NetworkWire
{
    internal static int RequireBlittable<T>(string kind) where T : struct
    {
        var type = typeof(T);
        if (!type.IsValueType || type.IsGenericTypeDefinition || type.ContainsGenericParameters)
            throw new NetworkContractException(NetworkCodes.PayloadNotBlittable, kind + " payload must be a closed value type.");
        // A reference anywhere in the value — a field, or a field of a nested value — makes the Marshal copy an
        // address rather than content, which is exactly what must never reach the wire.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new NetworkContractException(NetworkCodes.PayloadNotBlittable, kind + " payload holds a reference and cannot travel as a Marshal payload.");
        int size;
        try { size = Marshal.SizeOf<T>(); }
        catch (Exception error) { throw new NetworkContractException(NetworkCodes.PayloadNotBlittable, kind + " payload is not Marshal-structured: " + error.Message); }
        // A fieldless value type Marshals as one byte: it has no layout to agree with, so a declared width of zero
        // must not wave it through.
        if (size == 1 && type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0)
            throw new NetworkContractException(NetworkCodes.PayloadNotBlittable, kind + " payload has no fields to carry.");
        var declared = Declared(kind);
        if (declared != 0 && declared != size)
            throw new NetworkContractException(NetworkCodes.PayloadNotBlittable,
                kind + " declares " + declared + " bytes but Marshal lays it out as " + size + ".");
        if (size <= 0 || size > NetworkProtocol.MaximumDatagramBytes)
            throw new NetworkContractException(NetworkCodes.PayloadTooLarge, kind + " payload is " + size + " bytes.");
        return size;
    }

    /// <summary>The width each message's own layout constants declare; zero for a type that is not a protocol message.</summary>
    private static int Declared(string kind) => kind switch
    {
        NetworkProtocol.HelloEvent => HelloMessage.DeclaredBytes,
        NetworkProtocol.HelloAckEvent => HelloAckMessage.DeclaredBytes,
        NetworkProtocol.WorldEpochEvent => WorldEpochMessage.DeclaredBytes,
        NetworkProtocol.CommandRequestEvent => CommandRequestMessage.DeclaredBytes,
        NetworkProtocol.CommandResultEvent => CommandResultMessage.DeclaredBytes,
        NetworkProtocol.FactEvent => FactMessage.DeclaredBytes,
        NetworkProtocol.VariablesEvent => VariablesMessage.DeclaredBytes,
        _ => 0
    };
}

/// <summary>
/// The wire form of one message: the metadata fields, then one length prefix and body per text slot, then each
/// payload slot — the same order the generated struct declares them in. It is written once here rather than
/// discovered by reflection: the host runs on IL2CPP, where reflecting over a value type's fields is the one thing
/// this layer must not depend on.
/// <para>
/// The codec is only needed for the free-sized events (hello, command-request, fact, variables), whose content
/// length is part of the message. The other kinds travel as typed Marshal payloads and never reach this file.
/// </para>
/// </summary>
internal static class NetworkCodec
{
    internal static byte[] Write(HelloMessage value) => Write(NetworkProtocol.HelloEvent, HelloMessage.DeclaredBytes, value, (ref NetWriter writer, HelloMessage message) => WriteBody(ref writer, message));
    internal static byte[] Write(CommandRequestMessage value) => Write(NetworkProtocol.CommandRequestEvent, CommandRequestMessage.DeclaredBytes, value, (ref NetWriter writer, CommandRequestMessage message) => WriteBody(ref writer, message));
    internal static byte[] Write(FactMessage value) => Write(NetworkProtocol.FactEvent, FactMessage.DeclaredBytes, value, (ref NetWriter writer, FactMessage message) => WriteBody(ref writer, message));
    internal static byte[] Write(VariablesMessage value) => Write(NetworkProtocol.VariablesEvent, VariablesMessage.DeclaredBytes, value, (ref NetWriter writer, VariablesMessage message) => WriteBody(ref writer, message));

    /// <summary>Decodes one free-sized body by its kind. A body that is not exactly one message is refused.</summary>
    internal static NetworkEnvelope Read(NetworkMessageKind kind, byte[]? bytes, ulong sender) => kind switch
    {
        NetworkMessageKind.Hello => new NetworkEnvelope(kind, sender, ReadBody<HelloMessage>(NetworkProtocol.HelloEvent, bytes, (ref NetReader reader, ref HelloMessage message) => ReadBody(ref reader, ref message))),
        NetworkMessageKind.CommandRequest => new NetworkEnvelope(kind, sender, ReadBody<CommandRequestMessage>(NetworkProtocol.CommandRequestEvent, bytes, (ref NetReader reader, ref CommandRequestMessage message) => ReadBody(ref reader, ref message))),
        NetworkMessageKind.Fact => new NetworkEnvelope(kind, sender, ReadBody<FactMessage>(NetworkProtocol.FactEvent, bytes, (ref NetReader reader, ref FactMessage message) => ReadBody(ref reader, ref message))),
        NetworkMessageKind.Variables => new NetworkEnvelope(kind, sender, ReadBody<VariablesMessage>(NetworkProtocol.VariablesEvent, bytes, (ref NetReader reader, ref VariablesMessage message) => ReadBody(ref reader, ref message))),
        _ => throw new NetworkContractException(NetworkCodes.EventNameUnknown, "Kind " + kind + " has no free-sized body.")
    };

    private delegate void BodyWriter<T>(ref NetWriter writer, T value);
    private delegate void BodyReader<T>(ref NetReader reader, ref T message);

    private static byte[] Write<T>(string kind, int declaredBytes, T value, BodyWriter<T> write) where T : unmanaged
    {
        var size = NetworkWire.RequireBlittable<T>(kind);
        if (size != declaredBytes)
            throw new NetworkContractException(NetworkCodes.PayloadNotBlittable, kind + " declares " + declaredBytes + " bytes but Marshal lays it out as " + size + ".");
        var body = new byte[size];
        // The body starts as the message's Marshal image — the same fields in the same order — rather than as an
        // offset walk that would have to be kept in step with the struct by hand.
        MemoryMarshal.Write(body, ref value);
        // A slot nobody wrote still holds whatever the struct's storage had. Zeroing the slot frames in the body
        // itself — not in the message, which the caller still owns — makes an unwritten slot an empty one.
        ClearFrames<T>(body);
        var writer = new NetWriter(body);
        write(ref writer, value);
        writer.RequireComplete();
        return body;
    }

    private static void ClearFrames<T>(byte[] body) where T : unmanaged
    {
        if (typeof(T) == typeof(HelloMessage)) { HelloMessage.ClearFrames(body); return; }
        if (typeof(T) == typeof(CommandRequestMessage)) { CommandRequestMessage.ClearFrames(body); return; }
        if (typeof(T) == typeof(FactMessage)) { FactMessage.ClearFrames(body); return; }
        if (typeof(T) == typeof(VariablesMessage)) { VariablesMessage.ClearFrames(body); return; }
        throw new NetworkContractException(NetworkCodes.PayloadNotBlittable, typeof(T).Name + " has no encoded body.");
    }
    private static T ReadBody<T>(string kind, byte[]? bytes, BodyReader<T> read) where T : struct
    {
        var size = NetworkWire.RequireBlittable<T>(kind);
        if (bytes == null || bytes.Length != size)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, kind + " body is " + (bytes?.Length ?? 0) + " bytes, not " + size + ".");
        var reader = new NetReader(bytes);
        var message = default(T);
        read(ref reader, ref message);
        reader.RequireComplete();
        return message;
    }

    private static void WriteBody(ref NetWriter writer, HelloMessage message)
    {
        writer.UInt16(message.ProtocolVersion);
        writer.UInt16(message.Reserved);
        writer.UInt64(message.SenderSession);
        writer.Byte(message.Role);
        writer.Byte(message.PlanCount);
        writer.UInt16(message.Reserved2);
        for (var word = 0; word < NetworkProtocol.PlanSetDigestBytes / sizeof(ulong); word++) writer.UInt64(message.DigestWords()[word]);
        for (var slot = 0; slot < HelloMessage.SlotCount; slot++) writer.Text(message.SlotLength(slot), message.SlotStorage(slot), writer.ReserveText());
    }

    private static void ReadBody(ref NetReader reader, ref HelloMessage message)
    {
        message.ProtocolVersion = reader.UInt16();
        message.Reserved = reader.UInt16();
        message.SenderSession = reader.UInt64();
        message.Role = reader.Byte();
        message.PlanCount = reader.Byte();
        message.Reserved2 = reader.UInt16();
        var digest = message.DigestWords();
        for (var word = 0; word < digest.Length; word++) digest[word] = reader.UInt64();
        for (var slot = 0; slot < HelloMessage.SlotCount; slot++) message.SetSlotLength(slot, reader.Text(message.SlotStorage(slot), reader.ReserveText()));
    }

    private static void WriteBody(ref NetWriter writer, CommandRequestMessage message)
    {
        writer.UInt16(message.ProtocolVersion);
        writer.UInt16(message.Reserved);
        writer.UInt64(message.SenderSession);
        writer.Int64(message.WorldEpoch);
        writer.UInt64(message.EventId);
        writer.Int64(message.LifeEpoch);
        writer.UInt64(message.EntityId);
        writer.Int32(message.BindingIndex);
        writer.Int32(message.NodeIndex);
        writer.Int32(message.ScopeKind);
        writer.Int32(message.PayloadLength);
        for (var slot = 0; slot < CommandRequestMessage.SlotCount; slot++) writer.Text(message.SlotLength(slot), message.SlotStorage(slot), writer.ReserveText());
        writer.Payload(message.PayloadStorage(0), message.PayloadLength, writer.ReservePayload());
    }

    private static void ReadBody(ref NetReader reader, ref CommandRequestMessage message)
    {
        message.ProtocolVersion = reader.UInt16();
        message.Reserved = reader.UInt16();
        message.SenderSession = reader.UInt64();
        message.WorldEpoch = reader.Int64();
        message.EventId = reader.UInt64();
        message.LifeEpoch = reader.Int64();
        message.EntityId = reader.UInt64();
        message.BindingIndex = reader.Int32();
        message.NodeIndex = reader.Int32();
        message.ScopeKind = reader.Int32();
        message.PayloadLength = reader.Int32();
        if (message.PayloadLength < 0 || message.PayloadLength > NetworkProtocol.MaximumPayloadBytes)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Request payload length " + message.PayloadLength + " is out of range.");
        for (var slot = 0; slot < CommandRequestMessage.SlotCount; slot++) message.SetSlotLength(slot, reader.Text(message.SlotStorage(slot), reader.ReserveText()));
        reader.Payload(message.PayloadStorage(0), message.PayloadLength, reader.ReservePayload());
    }

    private static void WriteBody(ref NetWriter writer, FactMessage message)
    {
        writer.UInt16(message.ProtocolVersion);
        writer.UInt16(message.Reserved);
        writer.UInt64(message.SenderSession);
        writer.Int64(message.WorldEpoch);
        writer.Int64(message.LifeEpoch);
        writer.UInt64(message.EntityId);
        writer.UInt64(message.Sequence);
        writer.Int32(message.BindingIndex);
        writer.Int32(message.PayloadLength);
        for (var slot = 0; slot < FactMessage.SlotCount; slot++) writer.Text(message.SlotLength(slot), message.SlotStorage(slot), writer.ReserveText());
        writer.Payload(message.PayloadStorage(0), message.PayloadLength, writer.ReservePayload());
    }

    private static void ReadBody(ref NetReader reader, ref FactMessage message)
    {
        message.ProtocolVersion = reader.UInt16();
        message.Reserved = reader.UInt16();
        message.SenderSession = reader.UInt64();
        message.WorldEpoch = reader.Int64();
        message.LifeEpoch = reader.Int64();
        message.EntityId = reader.UInt64();
        message.Sequence = reader.UInt64();
        message.BindingIndex = reader.Int32();
        message.PayloadLength = reader.Int32();
        if (message.PayloadLength < 0 || message.PayloadLength > NetworkProtocol.MaximumPayloadBytes)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Fact payload length " + message.PayloadLength + " is out of range.");
        for (var slot = 0; slot < FactMessage.SlotCount; slot++) message.SetSlotLength(slot, reader.Text(message.SlotStorage(slot), reader.ReserveText()));
        reader.Payload(message.PayloadStorage(0), message.PayloadLength, reader.ReservePayload());
    }

    private static void WriteBody(ref NetWriter writer, VariablesMessage message)
    {
        writer.UInt16(message.ProtocolVersion);
        writer.UInt16(message.Reserved);
        writer.UInt64(message.SenderSession);
        writer.Int64(message.WorldEpoch);
        writer.Int64(message.Sequence);
        writer.Byte(message.Complete);
        writer.Byte(message.Reserved2);
        writer.UInt16(message.Reserved3);
        writer.Int32(message.PayloadLength);
        for (var slot = 0; slot < VariablesMessage.SlotCount; slot++) writer.Text(message.SlotLength(slot), message.SlotStorage(slot), writer.ReserveText());
        writer.Payload(message.PayloadStorage(0), message.PayloadLength, writer.ReservePayload());
    }

    private static void ReadBody(ref NetReader reader, ref VariablesMessage message)
    {
        message.ProtocolVersion = reader.UInt16();
        message.Reserved = reader.UInt16();
        message.SenderSession = reader.UInt64();
        message.WorldEpoch = reader.Int64();
        message.Sequence = reader.Int64();
        message.Complete = reader.Byte();
        message.Reserved2 = reader.Byte();
        message.Reserved3 = reader.UInt16();
        message.PayloadLength = reader.Int32();
        if (message.PayloadLength < 0 || message.PayloadLength > NetworkProtocol.MaximumPayloadBytes)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Variables payload length " + message.PayloadLength + " is out of range.");
        for (var slot = 0; slot < VariablesMessage.SlotCount; slot++) message.SetSlotLength(slot, reader.Text(message.SlotStorage(slot), reader.ReserveText()));
        reader.Payload(message.PayloadStorage(0), message.PayloadLength, reader.ReservePayload());
    }
}

/// <summary>
/// Little-endian writer over one message body, in the declared field order. It writes a text slot from the message's
/// own storage, so the body and the Marshal image it must equal are filled from the same fields.
/// </summary>
internal ref struct NetWriter
{
    private readonly Span<byte> _target;
    private int _offset;

    internal NetWriter(Span<byte> target)
    {
        _target = target;
        _offset = 0;
    }

    /// <summary>The body must be filled exactly: a layout edit that stops short is a fault, not a shorter message.</summary>
    internal readonly void RequireComplete()
    {
        if (_offset != _target.Length)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Message body wrote " + _offset + " of " + _target.Length + " bytes.");
    }

    /// <summary>
    /// Claims the next text slot in the body and returns it, all zero. A slot whose sender never wrote it stays the
    /// empty slot this returns, which is what makes an unwritten field mean "no text" rather than undefined memory.
    /// </summary>
    internal Span<byte> ReserveText()
    {
        var slot = _target.Slice(_offset, NetworkProtocol.TextSlotStride);
        _offset += slot.Length;
        return slot;
    }

    /// <summary>
    /// Writes one text slot: its length prefix, then its content. The body's frame was reserved zeroed, so a slot
    /// whose sender never wrote it — one whose declared length is not a content length — stays the empty slot the
    /// receiver reads, instead of undefined memory.
    /// </summary>
    internal void Text(ushort length, Span<byte> content, Span<byte> frame)
    {
        if (content.Length != NetworkProtocol.TextSlotBytes || frame.Length != NetworkProtocol.TextSlotStride)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Text slot is not the slot stride.");
        if (length > NetworkProtocol.TextSlotBytes) return;
        MemoryMarshal.Write(frame, ref length);
        content[..length].CopyTo(frame[sizeof(ushort)..]);
    }

    /// <summary>Claims the next payload slot in the body and returns it, all zero.</summary>
    internal Span<byte> ReservePayload()
    {
        var slot = _target.Slice(_offset, NetworkProtocol.PayloadSlotBytes);
        _offset += slot.Length;
        return slot;
    }

    /// <summary>Writes a payload slot's declared bytes into its reserved frame. The rest of the frame is reserved.</summary>
    internal void Payload(Span<byte> slot, int length, Span<byte> frame)
    {
        if (slot.Length != NetworkProtocol.PayloadSlotBytes || frame.Length != NetworkProtocol.PayloadSlotBytes)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Payload slot is not the payload stride.");
        if (length < 0 || length > NetworkProtocol.PayloadSlotBytes)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Payload length " + length + " is out of range.");
        slot[..length].CopyTo(frame);
    }

    internal void Byte(byte value) => _target[_offset++] = value;
    internal void UInt16(ushort value) => Put(value, sizeof(ushort));
    internal void Int32(int value) => Put(value, sizeof(int));
    internal void UInt64(ulong value) => Put(value, sizeof(ulong));
    internal void Int64(long value) => Put(value, sizeof(long));

    private void Put<T>(T value, int width) where T : unmanaged
    {
        if (_offset + width > _target.Length)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Message body is too small for its layout.");
        MemoryMarshal.Write(_target.Slice(_offset, width), ref value);
        _offset += width;
    }
}

/// <summary>Little-endian reader over one message body, the exact inverse of <see cref="NetWriter"/>.</summary>
internal ref struct NetReader
{
    private readonly ReadOnlySpan<byte> _source;
    private int _offset;

    internal NetReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _offset = 0;
    }

    internal readonly void RequireComplete()
    {
        if (_offset != _source.Length)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Message body read " + _offset + " of " + _source.Length + " bytes.");
    }

    /// <summary>Claims the next text slot of the body, which is where the next decode writes its content.</summary>
    internal ReadOnlySpan<byte> ReserveText() => Take(NetworkProtocol.TextSlotStride);

    /// <summary>
    /// Fills one text slot from its frame and reports the length it declared. A frame that declares more than a slot
    /// holds is a slot its sender left empty, so the caller records no length; what the frame declares is content and
    /// the rest is reserved, so a decode is reproducible from the body alone.
    /// </summary>
    internal ushort Text(Span<byte> content, ReadOnlySpan<byte> frame)
    {
        if (content.Length != NetworkProtocol.TextSlotBytes || frame.Length != NetworkProtocol.TextSlotStride)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Text slot is not the slot stride.");
        content.Clear();
        var length = MemoryMarshal.Read<ushort>(frame);
        if (length > NetworkProtocol.TextSlotBytes) return 0;
        frame.Slice(sizeof(ushort), length).CopyTo(content);
        return length;
    }

    /// <summary>Claims the next payload slot of the body.</summary>
    internal ReadOnlySpan<byte> ReservePayload() => Take(NetworkProtocol.PayloadSlotBytes);

    /// <summary>Fills one payload slot from its frame, up to what the message declares.</summary>
    internal void Payload(Span<byte> slot, int length, ReadOnlySpan<byte> frame)
    {
        if (slot.Length != NetworkProtocol.PayloadSlotBytes || frame.Length != NetworkProtocol.PayloadSlotBytes)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Payload slot is not the payload stride.");
        if (length < 0 || length > NetworkProtocol.PayloadSlotBytes)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Payload length " + length + " is out of range.");
        slot.Clear();
        frame[..length].CopyTo(slot);
    }

    internal byte Byte() => Take(sizeof(byte))[0];
    internal ushort UInt16() => Read<ushort>(sizeof(ushort));
    internal int Int32() => Read<int>(sizeof(int));
    internal ulong UInt64() => Read<ulong>(sizeof(ulong));
    internal long Int64() => Read<long>(sizeof(long));

    private T Read<T>(int width) where T : unmanaged => MemoryMarshal.Read<T>(Take(width));

    private ReadOnlySpan<byte> Take(int width)
    {
        if (_offset + width > _source.Length)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Message body ended after " + _offset + " of " + _source.Length + " bytes.");
        var slice = _source.Slice(_offset, width);
        _offset += width;
        return slice;
    }
}
