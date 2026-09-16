using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeRuntime.Network;

namespace ForgeRuntime.GameBindings;

/// <summary>
/// The command-request channel's game-side routing: which inbound request this side must present or execute, and
/// how a kernel result is written back out as the answer to it.
/// <para>
/// One message kind carries every direction, because all of them are the same thing — "run this command, I am not
/// the authority for it" — and the difference is only who asked. A host asking a client to present is a requested
/// presentation (<see cref="IsPresentationRequest"/>), a host asking the one session that holds a thing to write it
/// is a requested owner write (<see cref="IsOwnerRequest"/>), and everything else is a client-local observation a
/// domain package's own executor answers. The kind marker travels in the request's node-index field, which no other
/// writer of this message has ever used, so a request a domain package sends stays exactly the message it was.
/// </para>
/// <para>
/// A requested step names one recipient session in its endpoint slot: a request addressed to somebody else is not
/// this side's work and is left to the host layer's own routing. A presentation handler never commits and never
/// publishes a fact, so its answer is always a non-committing result; an owner handler performs a real write on a
/// replica and keeps its own result, commit state and facts, which is the one thing the two tiers do differently.
/// </para>
/// </summary>
internal static class NetworkCommandRouting
{
    /// <summary>The node-index value that marks a presentation request. It is a protocol constant, not user data:
    /// the wire section declares it, and this file only reads it.</summary>
    internal const int PresentationNodeIndex = NetworkProtocol.PresentationNodeIndex;

    /// <summary>The node-index value that marks an owner request, read from the wire section the same way.</summary>
    internal const int OwnerNodeIndex = NetworkProtocol.OwnerNodeIndex;

    /// <summary>The event the presentation request's kernel context names. The message field is a number, so the
    /// string identity a command context reports is that number's decimal spelling: one identity, two spellings,
    /// and a recipient never parses an id out of a text slot to build a context.</summary>
    internal static string EventIdentity(ulong eventId) => eventId.ToString();

    /// <summary>The request that carries one presentation step to one recipient: the plan, the step, the command
    /// identity and the envelope around it travel in their own named slots, and the host's resolved inputs are the
    /// payload.</summary>
    internal static CommandRequestMessage PresentationRequest(PresentationOutput output, string recipientSession)
    {
        if (output == null) throw new NetworkContractException(NetworkCodes.FormatInvalid, "A presentation output cannot be null.");
        var message = Request(output.PlanId, output.CommandId, output.BindingId, output.StepIndex, output.EventId,
            output.WorldEpoch, output.SimulationTick, output.Inputs, PresentationNodeIndex);
        message.Endpoint = Recipient(recipientSession, "presentation");
        return message;
    }

    /// <summary>
    /// The same request addressed to another session. A decision addressed to several players carries one payload —
    /// the host's own resolved inputs — so it is encoded once and re-addressed per recipient instead of encoded
    /// again for each of them; what stays per recipient is the send, which is the tier's own rule.
    /// </summary>
    internal static CommandRequestMessage Addressed(CommandRequestMessage request, string recipientSession, string tier)
    {
        request.Endpoint = Recipient(recipientSession, tier);
        return request;
    }

    /// <summary>
    /// The request that carries one owner step to the session that holds what it changes: the plan, the step, the
    /// command identity and the holder in the same slots a presentation request uses, plus the scope the write
    /// happened in — an owner handler really commits, and the facts it publishes carry that scope back. The binding
    /// id is not sent: the recipient re-reads the step from its own registry by plan and step index, which is where
    /// the step's binding already is.
    /// </summary>
    internal static CommandRequestMessage OwnerRequest(OwnerOutput output, string recipientSession)
    {
        if (output == null) throw new NetworkContractException(NetworkCodes.FormatInvalid, "An owner output cannot be null.");
        var message = Request(output.PlanId, output.CommandId, output.ScopeId, output.StepIndex, output.EventId,
            output.WorldEpoch, output.SimulationTick, output.Inputs, OwnerNodeIndex);
        message.Endpoint = Recipient(recipientSession, "owner");
        return message;
    }

    /// <summary>The routing both requested tiers share: the identity fields in their own slots, the kind marker in
    /// the node index, and the host's resolved inputs as the payload. One builder, so the two tiers cannot drift
    /// into two spellings of the same envelope.</summary>
    private static CommandRequestMessage Request(string planId, string commandId, string resourceSlot, int stepIndex,
        string eventId, long worldEpoch, long simulationTick, JsonElement inputs, int nodeIndex)
    {
        var message = new CommandRequestMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            WorldEpoch = worldEpoch,
            EventId = EventField(eventId),
            LifeEpoch = simulationTick,
            BindingIndex = stepIndex,
            NodeIndex = nodeIndex
        };
        message.PlanId = planId;
        message.CommandId = commandId;
        // The free slot carries what the tier's recipient needs and the step itself does not name: the binding a
        // presentation request presents, and the scope an owner write commits in. Both already exist as slots, so
        // nothing about the routing is invented here.
        message.ResourceId = resourceSlot;
        message.PayloadLength = message.SetPayload(0, Encoding.UTF8.GetBytes(inputs.GetRawText()));
        return message;
    }

    /// <summary>The one address a requested step runs on. The wire's endpoint slot names exactly one address, so a
    /// recipient that names nobody is refused here rather than sent as a request nobody answers. The address is the
    /// routing half's own spelling — the game's player slot — and no tier parses it: it is compared as the text it
    /// travelled as.</summary>
    private static string Recipient(string recipientAddress, string tier)
    {
        if (string.IsNullOrEmpty(recipientAddress))
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "A " + tier + " recipient must be a real address.");
        return recipientAddress;
    }

    /// <summary>Whether this request asks this side to present. The sender is checked by the host layer — only the
    /// session's master may address a client — and what is read here is the kind marker and the address.</summary>
    internal static bool IsPresentationRequest(CommandRequestMessage request, string localAddress)
        => IsRequest(request, PresentationNodeIndex, localAddress);

    /// <summary>Whether this request asks this side to perform an owner write. It is the presentation request's
    /// sibling with its own marker, so the two are told apart by one field and nothing else.</summary>
    internal static bool IsOwnerRequest(CommandRequestMessage request, string localAddress)
        => IsRequest(request, OwnerNodeIndex, localAddress);

    private static bool IsRequest(CommandRequestMessage request, int nodeIndex, string localAddress)
        => request.NodeIndex == nodeIndex && request.Endpoint.Length > 0
            && string.Equals(request.Endpoint, localAddress, StringComparison.Ordinal);

    /// <summary>The plan a requested step names, or null when the request does not name one. Read before the step
    /// is looked up so a malformed request is refused by name rather than answered from a guess.</summary>
    internal static string? PresentationPlan(CommandRequestMessage request) => RequestPlan(request);

    /// <summary>The plan an owner request names, read the same way and for the same reason.</summary>
    internal static string? OwnerPlan(CommandRequestMessage request) => RequestPlan(request);

    private static string? RequestPlan(CommandRequestMessage request)
        => request.PlanId.Length == 0 ? null : request.PlanId;

    /// <summary>The resolved input frame a presentation request carries, or null when the body is not an object.
    /// The recipient re-validates every value against the step's own contract: this is a transport decode, never a
    /// reason to trust the sender.</summary>
    internal static JsonElement? TryReadInputs(byte[] payload)
    {
        if (payload == null || payload.Length == 0) return null;
        try
        {
            var inputs = RuntimeJson.Parse(Encoding.UTF8.GetString(payload));
            return inputs.ValueKind == JsonValueKind.Object ? inputs : null;
        }
        catch (RuntimeContractException) { return null; }
    }

    /// <summary>The event-field value one event id travels as. The request key already excludes the payload, so the
    /// field only has to be stable and distinct per event; a non-numeric host id is folded into it rather than
    /// refused, because the id is the plan's own and this layer has no business rejecting its spelling.</summary>
    internal static ulong EventField(string eventId)
    {
        if (ulong.TryParse(eventId, out var parsed)) return parsed;
        var hash = 1469598103934665603UL;
        foreach (var character in eventId) { hash ^= character; hash *= 1099511628211UL; }
        return hash;
    }

    /// <summary>One kernel result as the wire's own vocabulary spells it. The runtime's statuses and commit states
    /// are already this layer's words, so a status the wire cannot name is a fault in the handler rather than a
    /// silent default.</summary>
    internal static NetworkCommandOutcome Outcome(CommandResult result)
    {
        if (result == null) return NetworkCommandOutcome.Refused("null-result", "The presentation handler returned null.");
        var status = result.Status switch
        {
            CommandStatuses.Succeeded => NetworkCommandStatuses.Succeeded,
            CommandStatuses.Partial => NetworkCommandStatuses.Partial,
            CommandStatuses.Rejected => NetworkCommandStatuses.Rejected,
            CommandStatuses.Failed => NetworkCommandStatuses.Failed,
            CommandStatuses.Cancelled => NetworkCommandStatuses.Cancelled,
            CommandStatuses.Expired => NetworkCommandStatuses.Expired,
            _ => throw new NetworkContractException(NetworkCodes.FormatInvalid, "Command status '" + result.Status + "' has no wire spelling.")
        };
        var commit = result.CommitState switch
        {
            CommitStates.Confirmed => NetworkCommitStates.Confirmed,
            CommitStates.Unknown => NetworkCommitStates.Unknown,
            _ => NetworkCommitStates.None
        };
        // The fact count travels so the host can see that a presentation answer carried none: a presentation
        // handler never publishes a fact, and the count is what proves it rather than a claim in a comment.
        return new NetworkCommandOutcome(status, commit, result.Code, result.Detail, result.Facts.Count);
    }
}
