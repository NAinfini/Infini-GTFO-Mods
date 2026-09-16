// The generator that owns Network/NetworkMessages.cs: the message layout lives in the field tables below, so a layout
// change is a change here plus a rerun, never an edit of the generated file. The Marshal layout and the byte codec
// must agree field for field, and Marshal.SizeOf pads a nested struct, so every message is emitted as one flat
// primitive-only struct whose byte body the codec writes in the same order. One table keeps the two from drifting
// apart.
//
//   dotnet run --project ForgeRuntime/tools/NetworkMessagesGen            rewrites NetworkMessages.cs in place
//   dotnet run --project ForgeRuntime/tools/NetworkMessagesGen -- --check fails when the file is stale (exit 1)
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

internal static class Generator
{
    private static readonly string[] Words =
    {
        "W00", "W01", "W02", "W03", "W04", "W05", "W06", "W07", "W08", "W09", "W0A", "W0B", "W0C", "W0D", "W0E", "W0F",
        "W10", "W11", "W12", "W13", "W14", "W15", "W16", "W17", "W18", "W19", "W1A", "W1B", "W1C", "W1D", "W1E", "W1F",
        "W20", "W21", "W22", "W23", "W24", "W25", "W26", "W27", "W28", "W29", "W2A", "W2B", "W2C", "W2D", "W2E", "W2F",
        "W30", "W31", "W32", "W33", "W34", "W35", "W36", "W37", "W38", "W39", "W3A", "W3B", "W3C", "W3D", "W3E", "W3F"
    };

    private sealed record Field(string Type, string Name);

    private static readonly Field[] Empty = Array.Empty<Field>();

    private static readonly Field[] HelloMetadata =
    {
        new("ushort", "ProtocolVersion"), new("ushort", "Reserved"), new("ulong", "SenderSession"),
        new("byte", "Role"), new("byte", "PlanCount"), new("ushort", "Reserved2")
    };

    /// <summary>The plan-set digest: what a hello carries instead of the plan rows themselves.</summary>
    private static readonly Field[] HelloDigest =
    {
        new("ulong", "Digest0"), new("ulong", "Digest1"), new("ulong", "Digest2"), new("ulong", "Digest3")
    };

    private static readonly Field[] AckMetadata =
    {
        new("ushort", "ProtocolVersion"), new("ushort", "Reserved"), new("ulong", "SenderSession"),
        new("ulong", "SubjectSession"), new("byte", "Role"), new("byte", "Accepted"), new("ushort", "Reserved2"),
        new("long", "HostWorldEpoch")
    };

    private static readonly Field[] EpochMetadata =
    {
        new("ushort", "ProtocolVersion"), new("ushort", "Reserved"), new("ulong", "SenderSession"),
        new("long", "WorldEpoch"), new("long", "PreviousEpoch"), new("byte", "Reason"), new("byte", "Reserved2"),
        new("ushort", "Reserved3")
    };

    private static readonly Field[] RequestMetadata =
    {
        new("ushort", "ProtocolVersion"), new("ushort", "Reserved"), new("ulong", "SenderSession"),
        new("long", "WorldEpoch"), new("ulong", "EventId"), new("long", "LifeEpoch"), new("ulong", "EntityId"),
        new("int", "BindingIndex"), new("int", "NodeIndex"), new("int", "ScopeKind"), new("int", "PayloadLength")
    };

    private static readonly Field[] ResultMetadata =
    {
        new("ushort", "ProtocolVersion"), new("ushort", "Reserved"), new("ulong", "SenderSession"),
        new("ulong", "SubjectSession"), new("ulong", "EventId"), new("long", "WorldEpoch"),
        new("byte", "Status"), new("byte", "CommitState"), new("ushort", "Reserved2"), new("int", "FactCount")
    };

    private static readonly Field[] FactMetadata =
    {
        new("ushort", "ProtocolVersion"), new("ushort", "Reserved"), new("ulong", "SenderSession"),
        new("long", "WorldEpoch"), new("long", "LifeEpoch"), new("ulong", "EntityId"), new("ulong", "Sequence"),
        new("int", "BindingIndex"), new("int", "PayloadLength")
    };

    /// <summary>One variable-sync body: the world the snapshot describes, whether it is the whole table or this
    /// advance's own delta, and the length of the encoded snapshot in the payload slot.</summary>
    private static readonly Field[] VariablesMetadata =
    {
        new("ushort", "ProtocolVersion"), new("ushort", "Reserved"), new("ulong", "SenderSession"),
        new("long", "WorldEpoch"), new("long", "Sequence"), new("byte", "Complete"), new("byte", "Reserved2"),
        new("ushort", "Reserved3"), new("int", "PayloadLength")
    };

    /// <summary>Name per text slot, in wire order. A slot exists only as an index on the wire, so the name is where
    /// the protocol states what travels in it; one name per slot also fixes every named accessor's index.</summary>
    private static string[] SlotNames(string message) => message switch
    {
        "HelloMessage" => new[] { "RuntimeId", "RuntimeVersion", "ApiVersion", "GameBuild" },
        "HelloAckMessage" => new[] { "ReasonCode", "HostRuntimeId", "HostRuntimeVersion", "HostApiVersion", "HostGameBuild" },
        "WorldEpochMessage" => new[] { "Detail" },
        "CommandRequestMessage" => new[] { "PlanId", "ResourceId", "CommandId", "Endpoint" },
        "CommandResultMessage" => new[] { "Code", "Detail", "PlanId", "CommandId" },
        "FactMessage" => new[] { "PlanId", "Detail" },
        "VariablesMessage" => new[] { "Code", "Detail" },
        _ => throw new InvalidOperationException(message)
    };

    /// <summary>Writes the file, or verifies it without touching it. Verifying is the commit-time use: the file is
    /// read back as bytes and compared with what the tables here generate.</summary>
    private static int Main(string[] args)
    {
        var check = false;
        foreach (var argument in args)
        {
            if (argument == "--check") { check = true; continue; }
            Console.Error.WriteLine("Unknown argument: " + argument);
            Console.Error.WriteLine("Usage: dotnet run --project ForgeRuntime/tools/NetworkMessagesGen [--check]");
            return 2;
        }

        var target = TargetPath();
        var generated = new UTF8Encoding(false).GetBytes(Messages());
        if (check)
        {
            if (!File.Exists(target))
            {
                Console.Error.WriteLine(target + " is missing; run the generator without --check to write it.");
                return 1;
            }
            var current = File.ReadAllBytes(target);
            if (!current.AsSpan().SequenceEqual(generated))
            {
                Console.Error.WriteLine(target + " does not match the generated layout (file " + Hash(current) + ", generated " +
                    Hash(generated) + "); run the generator without --check to rewrite it.");
                return 1;
            }
            Console.WriteLine(target + " matches the generated layout (" + current.Length + " bytes, " + Hash(current) + ").");
            return 0;
        }

        File.WriteAllBytes(target, generated);
        Budget();
        Console.WriteLine("wrote " + target + " (" + generated.Length + " bytes, " + Hash(generated) + ")");
        return 0;
    }

    /// <summary>The one file this tool owns, found by walking up from the working directory: the documented command
    /// runs from the repository root, and any directory inside the repository resolves to the same file.</summary>
    private static string TargetPath()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "ForgeRuntime", "Network", "NetworkMessages.cs");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("ForgeRuntime/Network/NetworkMessages.cs was not found above " +
            Directory.GetCurrentDirectory() + "; run this tool from inside the repository.");
    }

    private static string Hash(byte[] bytes) => "SHA-256 " + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The widths the declared layout adds up to, printed so a layout change reports its own size.</summary>
    private static void Budget()
    {
        Console.WriteLine("metadata bytes: hello=" + (Sum(HelloMetadata) + Sum(HelloDigest)) + " ack=" + Sum(AckMetadata) + " epoch=" + Sum(EpochMetadata) +
            " request=" + Sum(RequestMetadata) + " result=" + Sum(ResultMetadata) + " fact=" + Sum(FactMetadata) + " variables=" + Sum(VariablesMetadata));
        Console.WriteLine("message bytes: hello=" + (Sum(HelloMetadata) + Sum(HelloDigest) + 4 * 130) + " ack=" + (Sum(AckMetadata) + 5 * 130) +
            " epoch=" + (Sum(EpochMetadata) + 130) + " request=" + (Sum(RequestMetadata) + 4 * 130 + 512) +
            " result=" + (Sum(ResultMetadata) + 4 * 130) + " fact=" + (Sum(FactMetadata) + 2 * 130 + 512) +
            " variables=" + (Sum(VariablesMetadata) + 2 * 130 + 512));
    }

    private static int Sum(Field[] fields)
    {
        var total = 0;
        foreach (var field in fields) total += Width(field.Type);
        return total;
    }

    private static int Width(string type) => type switch
    {
        "byte" => 1, "ushort" => 2, "int" => 4, "long" => 8, "ulong" => 8, _ => throw new InvalidOperationException(type)
    };

    private static string Messages()
    {
        var text = new StringBuilder();
        text.Append(Header);
        AppendStruct(text, "HelloMessage",            "One participant's runtime identity plus a summary of its plan set. Both directions carry it: the host\n/// checks the joining client against its own, and the client checks the host, so an unmatched pairing suspends that\n/// client's Forge on either side of the decision. The plan set travels as its count and one 32-byte digest — the\n/// rows themselves are never on the wire — so the hello is four identity text slots wide.",
            HelloMetadata, HelloDigest, 4, 0);
        AppendStruct(text, "HelloAckMessage",
            "The host's verdict for one client hello. `Accepted` is the only value that opens the gameplay gate; every\n/// other value carries the reason code the client must report when it suspends its own Forge. The host's own\n/// identity travels too, so a client can record exactly which build it matched against.",
            AckMetadata, Empty, 5, 0);
        AppendStruct(text, "WorldEpochMessage",
            "The host's world epoch and the transition that produced it.",
            EpochMetadata, Empty, 1, 0);
        AppendStruct(text, "CommandRequestMessage",
            "A client-local observation the host must execute once. The request's identity is\n/// `(sender session, world epoch, plan id, event id)`; the payload never participates in the key, and the event id\n/// is a per-world counter the sender owns rather than a hash.",
            RequestMetadata, Empty, 4, 1);
        AppendStruct(text, "CommandResultMessage",
            "A committed command's outcome, sent back to the requester. This is the projection a repeated request is\n/// answered with instead of running a second time.",
            ResultMetadata, Empty, 4, 0);
        AppendStruct(text, "FactMessage",
            "A committed fact the host owns: state that affects results but is not carried by the game's own replication.\n/// Facts are addressed by the same three-part entity reference the runtime kernel uses, so a fact from a stale\n/// world or a dead life can be refused on arrival.",
            FactMetadata, Empty, 2, 1);
        AppendStruct(text, "VariablesMessage",
            "The host's variable state: one body per advance, either that advance's own delta or the whole table a\n/// late joiner is owed. The snapshot itself is the kernel's own encoded form in the payload slot, and the epoch\n/// it names is checked against the client's world before it is applied, so a snapshot from a world this side\n/// already left can never land on the objects of the current one.",
            VariablesMetadata, Empty, 2, 1);
        text.Append(Envelope);
        return text.ToString();
    }

    private static void AppendStruct(StringBuilder text, string name, string summary, Field[] metadata, Field[] raw, int slots, int payloads)
    {
        text.Append("/// <summary>\n/// ").Append(summary).Append("\n/// </summary>\n");
        text.Append("[StructLayout(LayoutKind.Sequential, Pack = 1)]\ninternal struct ").Append(name).Append("\n{\n");
        foreach (var field in metadata) text.Append("    public ").Append(field.Type).Append(' ').Append(field.Name).Append(";\n");
        foreach (var field in raw) text.Append("    public ").Append(field.Type).Append(' ').Append(field.Name).Append(";\n");
        for (var index = 0; index < slots; index++) text.Append("    public ushort L").Append(Slot(index)).Append(";\n");
        text.Append('\n');
        for (var index = 0; index < slots; index++) AppendWords(text, "S" + Slot(index), 16);
        for (var index = 0; index < payloads; index++) AppendWords(text, "P" + index, 64);
        text.Append("    /// <summary>Metadata width, declared slot and payload counts, published as constants so the wire\n");
        text.Append("    /// checks can assert that this layout still matches what the protocol budget was sized for.</summary>\n");
        text.Append("    internal const int MetadataBytes = ").Append(Sum(metadata) + Sum(raw)).Append(";\n");
        text.Append("    internal const int SlotCount = ").Append(slots).Append(";\n");
        text.Append("    internal const int PayloadCount = ").Append(payloads).Append(";\n");
        text.Append("    internal const int DeclaredBytes = ").Append(Sum(metadata) + Sum(raw) + slots * 130 + payloads * 512).Append(";\n\n");
        AppendStorage(text, slots, payloads);
        AppendFrameClearing(text, name, slots, payloads);
        AppendSlotAccessors(text, name, slots);
        if (raw.Length > 0) AppendRawAccessors(text, name, raw);
        AppendPayloadAccessors(text, payloads);
        text.Append("}\n\n");
    }

    private static void AppendWords(StringBuilder text, string prefix, int count)
    {
        for (var index = 0; index < count; index++)
        {
            text.Append("    private ulong ").Append(prefix).Append('_').Append(Words[index]).Append(";\n");
            if (index % 4 == 3) text.Append('\n');
        }
    }

    /// <summary>
    /// Byte-level access to the private storage. A text slot's frame in the body is its length prefix followed by
    /// its content, while the struct keeps those as two fields — a `ushort` and 128 content bytes — so the codec
    /// composes the frame from both. That is why the caller passes the declared length in.
    /// </summary>
    private static void AppendStorage(StringBuilder text, int slots, int payloads)
    {
        text.Append("    /// <summary>The content bytes of one text slot: 128 bytes, with no length prefix of its own.</summary>\n");
        text.Append("    internal Span<byte> SlotStorage(int index)\n    {\n");
        text.Append("        if (index < 0 || index >= SlotCount) throw new NetworkContractException(NetworkCodes.FormatInvalid, \"Slot index \" + index + \" is out of range.\");\n");
        text.Append("        return MemoryMarshal.AsBytes(SlotWords().Slice(index * SlotContentStride / sizeof(ulong), SlotContentStride / sizeof(ulong)));\n    }\n\n");
        text.Append("    /// <summary>The content words of every text slot, in declaration order.</summary>\n");
        text.Append("    private Span<ulong> SlotWords() => MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in this).S0_W00, SlotCount * SlotContentStride / sizeof(ulong));\n\n");
        if (payloads == 0)
        {
            text.Append("    /// <summary>Payload slot storage, one 512-byte slot per call.</summary>\n");
            text.Append("    internal Span<byte> PayloadStorage(int index) => throw new NetworkContractException(NetworkCodes.EventNameUnknown, \"This message has no payload slot.\");\n\n");
            return;
        }
        text.Append("    /// <summary>Payload slot storage, one 512-byte slot per call.</summary>\n");
        text.Append("    internal Span<byte> PayloadStorage(int index) => MemoryMarshal.AsBytes(index switch\n    {\n");
        for (var index = 0; index < payloads; index++)
            text.Append("        ").Append(index).Append(" => MemoryMarshal.CreateSpan(ref Unsafe.As<byte, ulong>(ref Unsafe.As<ulong, byte>(ref P")
                .Append(index).Append("_W00)), 64),\n");
        text.Append("        _ => throw new NetworkContractException(NetworkCodes.FormatInvalid, \"Payload index \" + index + \" is out of range.\")\n    });\n\n");
    }

    /// <summary>
    /// Zeroes every slot frame of a message. A slot is written by its own setter, and a slot nobody wrote still has
    /// whatever the struct's storage held — a text slot's length prefix included. The codec clears the frames before
    /// it encodes, so a field left alone can only mean an empty slot, never a garbage one: without this, a message
    /// built with an object initializer would put undefined memory on the wire.
    /// </summary>
    private static void AppendFrameClearing(StringBuilder text, string name, int slots, int payloads)
    {
        if (slots == 0 && payloads == 0) return;
        text.Append("    /// <summary>\n");
        text.Append("    /// Zeroes the slot frames of an encoded body: the text slots, then each payload slot. The codec calls\n");
        text.Append("    /// this on the body it is about to fill, so a slot its caller left unwritten is empty rather than whatever\n");
        text.Append("    /// the message's storage happened to hold.\n");
        text.Append("    /// </summary>\n");
        text.Append("    internal static void ClearFrames(byte[] body)\n    {\n");
        text.Append("        if (body == null || body.Length != DeclaredBytes) throw new NetworkContractException(NetworkCodes.FormatInvalid, \"A ").Append(name).Append(" body is not its declared width.\");\n");
        if (slots > 0)
            text.Append("        body.AsSpan(MetadataBytes, SlotCount * SlotStride).Clear();\n");
        if (payloads > 0)
            text.Append("        body.AsSpan(MetadataBytes + SlotCount * SlotStride, PayloadCount * PayloadStride).Clear();\n");
        text.Append("    }\n\n");
    }

    private static void AppendSlotAccessors(StringBuilder text, string name, int slots)
    {
        text.Append("    /// <summary>Bytes one text slot's content occupies: what a named setter may fill, and what the body reserves after the prefix.</summary>\n");
        text.Append("    internal const int SlotContentStride = NetworkProtocol.TextSlotBytes;\n\n");
        text.Append("    /// <summary>Bytes one text slot occupies in the byte body: its length prefix plus its content.</summary>\n");
        text.Append("    internal const int SlotStride = NetworkProtocol.TextSlotStride;\n\n");
        text.Append("    internal ushort SlotLength(int index) => index switch\n    {\n");
        for (var index = 0; index < slots; index++) text.Append("        ").Append(index).Append(" => L").Append(Slot(index)).Append(", ");
        text.Append("\n        _ => throw new NetworkContractException(NetworkCodes.FormatInvalid, \"Slot index \" + index + \" is out of range.\")\n    };\n\n");
        text.Append("    /// <summary>Every text field this message can carry. A slot exists only as an index on the wire, so\n");
        text.Append("    /// each name is declared with its own constant and accessor: no caller has to know a slot number.</summary>\n");
        foreach (var (index, label) in Labels(name, slots))
        {
            text.Append("    internal const int ").Append(label).Append("Slot = ").Append(index).Append(";\n");
            text.Append("    internal string ").Append(label).Append(" { get => SlotText(").Append(index).Append("); set => SetSlotText(").Append(index).Append(", value); }\n");
        }
        text.Append('\n');
        AppendSlotText(text, slots);
    }

    /// <summary>
    /// Byte access to the fields that are neither metadata nor a text slot. The plan-set digest is the only one: a
    /// caller reads and writes it as the 32 bytes it is, while the struct carries four words the codec copies one by
    /// one, so the byte view and the Marshal image stay the same bytes.
    /// </summary>
    private static void AppendRawAccessors(StringBuilder text, string name, Field[] raw)
    {
        if (name != "HelloMessage" || raw.Length != 4) throw new InvalidOperationException(name + " has no byte accessor rule");
        text.Append("\n    /// <summary>The plan-set digest: SHA-256 over the ordinal-sorted plan rows, 32 bytes in four words.\n");
        text.Append("    /// It is computed once when a plan set is adopted, never per message, and compared instead of the rows.</summary>\n");
        text.Append("    internal Span<ulong> DigestWords() => MemoryMarshal.CreateSpan(ref Digest0, NetworkProtocol.PlanSetDigestBytes / sizeof(ulong));\n");
        text.Append("    internal byte[] PlanSetDigest() => MemoryMarshal.AsBytes(DigestWords()).ToArray();\n");
        text.Append("    internal void SetPlanSetDigest(ReadOnlySpan<byte> digest)\n    {\n");
        text.Append("        if (digest.Length != NetworkProtocol.PlanSetDigestBytes) throw new NetworkContractException(NetworkCodes.FormatInvalid, \"A plan-set digest is \" + NetworkProtocol.PlanSetDigestBytes + \" bytes, not \" + digest.Length + \".\");\n");
        text.Append("        digest.CopyTo(MemoryMarshal.AsBytes(DigestWords()));\n    }\n");
    }

    /// <summary>Slot index and accessor name of every text field, in wire order. The name table gives one name per
    /// slot, so a named accessor and the generic slot runtime always address the same slot.</summary>
    private static IEnumerable<(int Index, string Label)> Labels(string name, int slots)
    {
        var names = SlotNames(name);
        if (names.Length != slots) throw new InvalidOperationException(name + " declares " + slots + " slots but names " + names.Length);
        for (var index = 0; index < slots; index++) yield return (index, names[index]);
    }

    /// <summary>The generic slot runtime: length prefix plus Latin-1 content, both inside the slot's own storage.</summary>
    private static void AppendSlotText(StringBuilder text, int slots)
    {
        text.Append("    internal void SetSlotLength(int index, ushort length)\n    {\n        switch (index)\n        {\n");
        for (var index = 0; index < slots; index++) text.Append("            case ").Append(index).Append(": L").Append(Slot(index)).Append(" = length; return;\n");
        text.Append("            default: throw new NetworkContractException(NetworkCodes.FormatInvalid, \"Slot index \" + index + \" is out of range.\");\n        }\n    }\n\n");
        text.Append("    internal string SlotText(int index)\n    {\n");
        text.Append("        var length = SlotLength(index);\n");
        text.Append("        if (length > NetworkProtocol.TextSlotBytes) throw new NetworkContractException(NetworkCodes.FormatInvalid, \"Text slot length \" + length + \" exceeds its capacity.\");\n");
        text.Append("        if (length == 0) return string.Empty;\n");
        text.Append("        return Encoding.Latin1.GetString(SlotStorage(index)[..length]);\n    }\n\n");
        text.Append("    internal void SetSlotText(int index, string? value)\n    {\n");
        text.Append("        value ??= string.Empty;\n");
        text.Append("        if (value.Length > NetworkProtocol.TextSlotBytes) throw new NetworkContractException(NetworkCodes.PayloadTooLarge, \"Text slot overflow: \" + value.Length);\n");
        text.Append("        var slot = SlotStorage(index);\n");
        text.Append("        slot.Clear();\n");
        text.Append("        for (var position = 0; position < value.Length; position++)\n        {\n");
        text.Append("            var character = value[position];\n");
        text.Append("            if (character > 0xFF) throw new NetworkContractException(NetworkCodes.FormatInvalid, \"Text slot is Latin-1 only.\");\n");
        text.Append("            slot[position] = (byte)character;\n        }\n");
        text.Append("        SetSlotLength(index, (ushort)value.Length);\n    }\n");
    }

    private static void AppendPayloadAccessors(StringBuilder text, int payloads)
    {
        text.Append("\n    /// <summary>Stride of one payload slot in the byte body.</summary>\n");
        text.Append("    internal const int PayloadStride = 512;\n\n");
        if (payloads == 0)
        {
            text.Append("    internal byte[] PayloadBytes(int index, int length) => throw new NetworkContractException(NetworkCodes.EventNameUnknown, \"This message has no payload slot.\");\n");
            text.Append("    internal int SetPayload(int index, byte[]? bytes) => throw new NetworkContractException(NetworkCodes.EventNameUnknown, \"This message has no payload slot.\");\n");
            return;
        }
        text.Append("    internal byte[] PayloadBytes(int index, int length)\n    {\n");
        text.Append("        if (length < 0 || length > PayloadStride) throw new NetworkContractException(NetworkCodes.FormatInvalid, \"Payload length \" + length + \" is out of range.\");\n");
        text.Append("        return PayloadStorage(index)[..length].ToArray();\n    }\n\n");
        text.Append("    internal int SetPayload(int index, byte[]? bytes)\n    {\n");
        text.Append("        if (bytes == null || bytes.Length == 0) return 0;\n");
        text.Append("        if (bytes.Length > PayloadStride) throw new NetworkContractException(NetworkCodes.PayloadTooLarge, \"Payload of \" + bytes.Length + \" bytes exceeds the slot capacity.\");\n");
        text.Append("        var target = PayloadStorage(index);\n");
        text.Append("        target[..bytes.Length].Clear();\n");
        text.Append("        bytes.CopyTo(target);\n");
        text.Append("        return bytes.Length;\n    }\n");
    }

    private static string Slot(int index) => index.ToString("X");

    private const string Header = """
// Generated by ForgeRuntime/tools/NetworkMessagesGen: the message layout lives in that tool's field tables, so a
// layout change is a change there plus a rerun. Do not edit this file by hand.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ForgeRuntime.Network;

// Generated layout: one flat struct per message, primitive fields only. A nested struct makes Marshal.SizeOf pad it,
// and a padded Marshal image is not the byte body the codec writes, so every field lives in the same struct and
// Marshal.SizeOf<T>() stays equal to the codec's own offset walk.
//
// A text slot is a `ushort` length prefix followed by its content, declared as consecutive fields, so one slot is
// `NetworkProtocol.TextSlotBytes + sizeof(ushort)` bytes of the Marshal image and the codec copies that image whole.

/// <summary>Which side of the authority split a participant runs as.</summary>
internal enum SessionRole : byte
{
    Unknown = 0,
    Host = 1,
    Client = 2
}

/// <summary>Why a world moved on. Only the host ever evaluates a reason; receivers carry it for diagnostics.</summary>
internal enum WorldChangeReason : byte
{
    Unknown = 0,
    NewGeneration = 1,
    LevelCleanup = 2,
    // 3 was the checkpoint suspension. A checkpoint reload continues the same expedition and no longer suspends
    // the runtime, so nothing produces it; the number stays retired rather than reused, because the protocol's
    // values are frozen and a peer that still carries it must keep meaning what it meant.
    /// <summary>The host took itself out of the world: an unsupported host migration, a fault in a native
    /// callback, or the runtime stopping. A receiver carries the byte without switching on it.</summary>
    HostSuspend = 4
}

/// <summary>Result status vocabulary, mirroring the runtime kernel's command statuses on the wire.</summary>
internal static class NetworkStatus
{
    internal const byte Committed = 1;
    internal const byte Succeeded = 2;
    internal const byte Partial = 3;
    internal const byte Rejected = 4;
    internal const byte Failed = 5;
    internal const byte Cancelled = 6;
    internal const byte Expired = 7;
}

/// <summary>Commit-state vocabulary, mirroring the runtime kernel's commit states on the wire.</summary>
internal static class NetworkCommitState
{
    internal const byte None = 0;
    internal const byte Confirmed = 1;
    internal const byte Unknown = 2;
}

/// <summary>Message kinds. Values are frozen with the protocol version.</summary>
internal enum NetworkMessageKind : byte
{
    Hello = 1,
    HelloAck = 2,
    WorldEpoch = 3,
    CommandRequest = 4,
    CommandResult = 5,
    Fact = 6,
    Variables = 7
}


""";

    private const string Envelope = """
/// <summary>
/// One decoded message: the kind discriminator plus the struct for that kind. The transport hands a typed payload
/// straight to this layer, so the struct arrives already Marshal-decoded and only the kind tag is added here.
/// </summary>
internal readonly struct NetworkEnvelope
{
    private NetworkEnvelope(NetworkMessageKind kind, ulong sender)
    {
        Kind = kind;
        Sender = sender;
        Hello = default;
        Ack = default;
        Epoch = default;
        Request = default;
        Result = default;
        Fact = default;
        Variables = default;
    }

    private NetworkEnvelope(NetworkMessageKind kind, ulong sender, HelloMessage hello, HelloAckMessage ack, WorldEpochMessage epoch,
        CommandRequestMessage request, CommandResultMessage result, FactMessage fact, VariablesMessage variables)
    {
        Kind = kind;
        Sender = sender;
        Hello = hello;
        Ack = ack;
        Epoch = epoch;
        Request = request;
        Result = result;
        Fact = fact;
        Variables = variables;
    }

    internal NetworkEnvelope(NetworkMessageKind kind, ulong sender, HelloMessage hello) : this(kind, sender) => Hello = hello;
    internal NetworkEnvelope(NetworkMessageKind kind, ulong sender, HelloAckMessage ack) : this(kind, sender) => Ack = ack;
    internal NetworkEnvelope(NetworkMessageKind kind, ulong sender, WorldEpochMessage epoch) : this(kind, sender) => Epoch = epoch;
    internal NetworkEnvelope(NetworkMessageKind kind, ulong sender, CommandRequestMessage request) : this(kind, sender) => Request = request;
    internal NetworkEnvelope(NetworkMessageKind kind, ulong sender, CommandResultMessage result) : this(kind, sender) => Result = result;
    internal NetworkEnvelope(NetworkMessageKind kind, ulong sender, FactMessage fact) : this(kind, sender) => Fact = fact;
    internal NetworkEnvelope(NetworkMessageKind kind, ulong sender, VariablesMessage variables) : this(kind, sender) => Variables = variables;

    internal NetworkMessageKind Kind { get; }

    /// <summary>The sender SNet identifies on the receive callback, not a field the sender wrote.</summary>
    internal ulong Sender { get; }
    internal HelloMessage Hello { get; }
    internal HelloAckMessage Ack { get; }
    internal WorldEpochMessage Epoch { get; }
    internal CommandRequestMessage Request { get; }
    internal CommandResultMessage Result { get; }
    internal FactMessage Fact { get; }
    internal VariablesMessage Variables { get; }

    internal ushort ClaimedProtocol => Kind switch
    {
        NetworkMessageKind.Hello => Hello.ProtocolVersion,
        NetworkMessageKind.HelloAck => Ack.ProtocolVersion,
        NetworkMessageKind.WorldEpoch => Epoch.ProtocolVersion,
        NetworkMessageKind.CommandRequest => Request.ProtocolVersion,
        NetworkMessageKind.CommandResult => Result.ProtocolVersion,
        NetworkMessageKind.Fact => Fact.ProtocolVersion,
        NetworkMessageKind.Variables => Variables.ProtocolVersion,
        _ => 0
    };
}

/// <summary>One outgoing message: the kind tag the transport picks its event name from, plus the struct.</summary>
internal readonly struct NetworkMessage
{
    internal NetworkMessage(NetworkMessageKind kind, HelloMessage hello) : this(kind) => Hello = hello;
    internal NetworkMessage(NetworkMessageKind kind, HelloAckMessage ack) : this(kind) => Ack = ack;
    internal NetworkMessage(NetworkMessageKind kind, WorldEpochMessage epoch) : this(kind) => Epoch = epoch;
    internal NetworkMessage(NetworkMessageKind kind, CommandRequestMessage request) : this(kind) => Request = request;
    internal NetworkMessage(NetworkMessageKind kind, CommandResultMessage result) : this(kind) => Result = result;
    internal NetworkMessage(NetworkMessageKind kind, FactMessage fact) : this(kind) => Fact = fact;
    internal NetworkMessage(NetworkMessageKind kind, VariablesMessage variables) : this(kind) => Variables = variables;

    private NetworkMessage(NetworkMessageKind kind)
    {
        Kind = kind;
        Hello = default;
        Ack = default;
        Epoch = default;
        Request = default;
        Result = default;
        Fact = default;
        Variables = default;
    }

    internal NetworkMessageKind Kind { get; }
    internal HelloMessage Hello { get; }
    internal HelloAckMessage Ack { get; }
    internal WorldEpochMessage Epoch { get; }
    internal CommandRequestMessage Request { get; }
    internal CommandResultMessage Result { get; }
    internal FactMessage Fact { get; }
    internal VariablesMessage Variables { get; }
}
""";
}
