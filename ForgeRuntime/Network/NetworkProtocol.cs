using System;

namespace ForgeRuntime.Network;

/// <summary>
/// Frozen shape of the Forge wire protocol. Everything here is versioned with <see cref="ProtocolVersion"/>: the
/// event names carry it, the hello exchange compares it, and a changed struct layout is a new protocol version
/// rather than a compatibility branch. Messages are fixed-layout blittable structs (see
/// <see cref="NetworkMessages"/>); variable-length text lives in fixed-width byte slots, never in a serializer.
/// </summary>
internal static class NetworkProtocol
{
    /// <summary>Wire revision of this assembly's message set. The hello exchange refuses a peer that reports another.</summary>
    internal const ushort Version = 1;

    /// <summary>Event names are registered once, per message type, and never built from user data.</summary>
    internal const string Prefix = "NAinfini.Forge.v1.";

    internal const string HelloEvent = Prefix + "hello";
    internal const string HelloAckEvent = Prefix + "hello-ack";
    internal const string WorldEpochEvent = Prefix + "world-epoch";
    internal const string CommandRequestEvent = Prefix + "command-request";
    internal const string CommandResultEvent = Prefix + "command-result";
    internal const string FactEvent = Prefix + "fact";
    internal const string VariablesEvent = Prefix + "variables";

    /// <summary>Every registered event name, in registration order. Duplicate or missing entries are registration faults.</summary>
    internal static readonly string[] EventNames =
    {
        HelloEvent, HelloAckEvent, WorldEpochEvent, CommandRequestEvent, CommandResultEvent, FactEvent, VariablesEvent
    };

    /// <summary>The one event name a message kind travels under. The kind is a protocol value, so a name is never
    /// built from anything a peer sent.</summary>
    internal static string EventName(NetworkMessageKind kind) => kind switch
    {
        NetworkMessageKind.Hello => HelloEvent,
        NetworkMessageKind.HelloAck => HelloAckEvent,
        NetworkMessageKind.WorldEpoch => WorldEpochEvent,
        NetworkMessageKind.CommandRequest => CommandRequestEvent,
        NetworkMessageKind.CommandResult => CommandResultEvent,
        NetworkMessageKind.Fact => FactEvent,
        NetworkMessageKind.Variables => VariablesEvent,
        _ => throw new NetworkContractException(NetworkCodes.EventNameUnknown, "Message kind " + kind + " has no event name.")
    };

    /// <summary>
    /// Largest message this layer sends, in bytes: the command request, whose metadata, four text slots and payload
    /// slot are wider than every other layout. Every message is checked against this budget where its type is
    /// registered, so a layout edit that outgrows it fails at startup. A message this wide is one GTFO-API event
    /// body and may span several SNet packets; sending it is the API's business.
    /// </summary>
    internal const int MaximumDatagramBytes = 60 + 4 * TextSlotStride + PayloadSlotBytes;

    /// <summary>Reserved bytes of text per slot. The frame the layout carries is this plus the length prefix.</summary>
    internal const int TextSlotBytes = 128;

    /// <summary>
    /// Bytes one text slot occupies in a message: the length prefix followed by the slot's content, declared as
    /// consecutive fields so the Marshal image and the codec see the same frame. Every message width, the datagram
    /// budget and the transport's stride check are built from this one number.
    /// </summary>
    internal const int TextSlotStride = TextSlotBytes + sizeof(ushort);

    /// <summary>Reserved bytes of each message's payload slot.</summary>
    internal const int PayloadSlotBytes = 512;

    /// <summary>
    /// Bytes of the plan-set digest a hello carries: one SHA-256 over the ordinal-sorted plan rows. The digest — not
    /// the rows — is what two peers compare, so the plan set's size never bounds a message.
    /// </summary>
    internal const int PlanSetDigestBytes = 32;

    /// <summary>
    /// Largest committed-fact payload one message can carry. The budget is the datagram envelope minus the fact
    /// header; the runtime kernel's own event payload budget stays the local limit, so a fact larger than this must
    /// be split by its owner rather than truncated here.
    /// </summary>
    internal const int MaximumFactPayloadBytes = PayloadSlotBytes;

    /// <summary>
    /// Largest request payload one message can carry: the same slot as a fact, with no separate budget, because both
    /// travel as the Marshal body of one datagram and neither is allowed to overrun it.
    /// </summary>
    internal const int MaximumPayloadBytes = PayloadSlotBytes;

    /// <summary>
    /// The node-index value that marks a command request as a requested presentation rather than a client-local
    /// observation: the host asks one client to present a `presentation` step of a plan both sides hold. It is a
    /// protocol value. Everything a domain package ever sent or will send leaves this field zero, which is what
    /// keeps one message kind carrying both directions without a second message layout.
    /// </summary>
    internal const int PresentationNodeIndex = 1;

    /// <summary>
    /// The node-index value that marks a command request as a requested owner write: the host decided an `owner`
    /// step and asks the one session that holds the thing it changes to execute it. It is the presentation
    /// marker's sibling — same message, same direction, same answer — and the two differ in what the recipient's
    /// write is: a screen write commits nothing and this one commits world state on a replica, which is why the
    /// recipient keeps the handler's own result and facts.
    /// </summary>
    internal const int OwnerNodeIndex = 2;

    /// <summary>Entries one world's dedup ledger holds before it refuses new keys.</summary>
    internal const int MaximumLedgerEntries = 4096;
}

/// <summary>
/// Result codes the Forge network layer reports. They follow the runtime kernel's rejection vocabulary
/// (lowercase, hyphenated, stable) so the host integration can log them beside the existing codes without a
/// translation table.
/// </summary>
internal static class NetworkCodes
{
    // Handshake
    internal const string Accepted = "accepted";
    internal const string ProtocolMismatch = "protocol-mismatch";
    internal const string RuntimeMismatch = "runtime-mismatch";
    internal const string PlansUnavailable = "plans-unavailable";
    internal const string PlanSetMismatch = "plan-set-mismatch";
    internal const string RoleMismatch = "role-mismatch";
    internal const string FormatInvalid = "format-invalid";
    internal const string HelloUnsent = "hello-unsent";
    internal const string AckUnmatched = "ack-unmatched";
    internal const string HostUnmatched = "handshake-unmatched";

    // Command requests
    internal const string NoConsumer = "no-consumer";

    // Variable sync
    internal const string VariablesSent = "variables-sent";
    internal const string VariablesApplied = "variables-applied";

    // Dedup ledger
    internal const string Duplicate = "duplicate";
    internal const string EventIdConflict = "event-id-conflict";
    internal const string LedgerFull = "ledger-full";
    internal const string StaleWorld = "stale-world";

    // World epoch
    internal const string EpochAccepted = "epoch-accepted";
    internal const string SenderNotHost = "sender-not-host";
    internal const string EpochNotIncreasing = "epoch-not-increasing";
    internal const string EpochBeforeJoin = "epoch-before-join";
    internal const string EpochUnavailable = "epoch-unavailable";

    // Transport registration
    internal const string PayloadNotBlittable = "payload-not-blittable";
    internal const string PayloadTooLarge = "payload-too-large";
    internal const string DuplicateEventName = "duplicate-event-name";
    internal const string UnregisteredEventName = "unregistered-event-name";
    internal const string EventNameUnknown = "event-name-unknown";
}

/// <summary>
/// The network layer's own contract failure. Registration and decode faults are programming or peer errors that
/// must be reported with a stable code, never swallowed into a silent dropped message.
/// </summary>
internal sealed class NetworkContractException : Exception
{
    internal NetworkContractException(string code, string message) : base(message) => Code = code;
    internal string Code { get; }
}

/// <summary>Text that does not fit a slot, and the one place a diagnostic is trimmed to fit one.</summary>
internal static class WireText
{
    /// <summary>Truncates text to a slot capacity for detail that is informational rather than identity. A character
    /// a slot cannot hold is replaced, because a clipped diagnostic must not fail the message it explains.</summary>
    internal static string Clip(string? value, int capacity)
    {
        value ??= string.Empty;
        if (value.Length <= capacity && IsLatin1(value)) return value;
        var clipped = new System.Text.StringBuilder(Math.Min(capacity, value.Length));
        foreach (var character in value)
        {
            if (clipped.Length == capacity) break;
            clipped.Append(character <= 0xFF ? character : '?');
        }
        return clipped.ToString();
    }

    private static bool IsLatin1(string value)
    {
        foreach (var character in value) if (character > 0xFF) return false;
        return true;
    }
}
