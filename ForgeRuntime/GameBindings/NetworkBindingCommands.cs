using System;
using System.Collections.Generic;
using System.Text;
using ForgeRuntime.Framework;
using ForgeRuntime.Network;

namespace ForgeRuntime.GameBindings;

/// <summary>
/// The command-request channel's game half: what the binding does with a presentation or owner decision the host
/// reached, and what it does with a requested step the host addressed to this side. The decisions themselves are in
/// <see cref="NetworkCommandRouting"/>; this file is the half that touches the kernel and the transport.
/// <para>
/// A requested step's inputs are resolved by the host before they travel, so a recipient runs what the host decided
/// instead of walking the plan again: a plan may read the world, and only one machine owns it. Each intent becomes
/// one request per recipient session — the wire's endpoint slot names exactly one session — carrying the plan, the
/// step, the command identity and the envelope around it. A presentation step is addressed to every session its
/// provider named; an owner step is addressed to the one session that holds the thing it changes.
/// </para>
/// </summary>
internal static class NetworkBindingCommands
{
    /// <summary>The presentation intents one advance decided, sent to the players the provider named. Nothing is
    /// sent for an empty set: the dispatch refuses a step it cannot address a player for, so an empty set never
    /// reaches here. What is per recipient is the send — the payload is the host's own resolved inputs and is the
    /// same bytes for every session the step addresses, so one decision is encoded once and re-addressed.</summary>
    internal static void Present(NetworkHost host, IReadOnlyList<PresentationOutput> presentations)
    {
        if (host.Role != SessionRole.Host || presentations.Count == 0) return;
        foreach (var output in presentations)
        {
            if (output.Recipients.Count == 0) continue;
            var request = NetworkCommandRouting.PresentationRequest(output, output.Recipients[0]);
            for (var i = 0; i < output.Recipients.Count; i++)
                ForgeNetworkTransport.Send(new NetworkMessage(NetworkMessageKind.CommandRequest,
                    NetworkCommandRouting.Addressed(request, output.Recipients[i], "presentation")));
        }
    }

    /// <summary>
    /// The owner steps one advance decided, each sent to the one session that holds what it changes. Nothing is
    /// sent for an empty set, and there is no broadcast form of this tier: the dispatch refuses a step whose
    /// holder cannot be named with `owner-holder`, so an intent that reaches here always names one session.
    /// </summary>
    internal static void Own(NetworkHost host, IReadOnlyList<OwnerOutput> ownerCommands)
    {
        if (host.Role != SessionRole.Host || ownerCommands.Count == 0) return;
        foreach (var output in ownerCommands)
            ForgeNetworkTransport.Send(new NetworkMessage(NetworkMessageKind.CommandRequest,
                NetworkCommandRouting.OwnerRequest(output, output.Holder)));
    }

    /// <summary>Executes a requested step this side was addressed with, and answers with what the kernel's own
    /// entry point for that tier reported. The address is what the request's endpoint had to match to reach this
    /// far, and the session is the identity the answer's subject carries. A request that is not addressed here, or
    /// whose payload cannot be decoded, is not answered by this file at all — the host layer's own refusal covers
    /// it.</summary>
    internal static NetworkReply? Execute(RuntimeKernel kernel, ulong localSession, string localAddress, CommandRequestMessage request, byte[] payload)
    {
        if (NetworkCommandRouting.IsPresentationRequest(request, localAddress))
        {
            var planId = NetworkCommandRouting.PresentationPlan(request);
            var inputs = NetworkCommandRouting.TryReadInputs(payload);
            if (planId == null || inputs is not { } frame)
                return new NetworkReply(request.EventId, localSession,
                    NetworkCommandOutcome.Refused(NetworkCodes.FormatInvalid, "A presentation request must name a plan and carry an input frame."));
            var result = kernel.ExecutePresentationCommand(planId, request.BindingIndex, request.CommandId,
                NetworkCommandRouting.EventIdentity(request.EventId), request.Endpoint, request.LifeEpoch, frame);
            return new NetworkReply(request.EventId, localSession, NetworkCommandRouting.Outcome(result));
        }
        if (!NetworkCommandRouting.IsOwnerRequest(request, localAddress)) return null;
        var ownerPlan = NetworkCommandRouting.OwnerPlan(request);
        var ownerInputs = NetworkCommandRouting.TryReadInputs(payload);
        if (ownerPlan == null || ownerInputs is not { } ownerFrame)
            return new NetworkReply(request.EventId, localSession,
                NetworkCommandOutcome.Refused(NetworkCodes.FormatInvalid, "An owner request must name a plan and carry an input frame."));
        // The scope an owner write commits in travels in the request's free slot, because the recipient's facts are
        // published under it: the handler's own result is kept here rather than reported as a dispatch.
        var owned = kernel.ExecuteOwnerCommand(ownerPlan, request.BindingIndex, request.CommandId,
            NetworkCommandRouting.EventIdentity(request.EventId), request.ResourceId, request.LifeEpoch, ownerFrame);
        return new NetworkReply(request.EventId, localSession, NetworkCommandRouting.Outcome(owned));
    }

    /// <summary>
    /// The host broadcasts this advance's own variable delta. The kernel owns the values and their encoding — the
    /// delta is what this advance wrote or cleared — and the binding owns the sending, because it owns the session.
    /// A tick that wrote nothing still sends its empty delta: a client that missed a body is corrected by the next
    /// one, and losing one datagram is not losing the state.
    /// </summary>
    internal static void BroadcastVariables(NetworkHost host, RuntimeKernel kernel)
    {
        if (host.Role != SessionRole.Host || !host.Registered || host.VariablesSource == null) return;
        ForgeNetworkTransport.Send(host.TakeVariables(complete: false));
    }

    /// <summary>
    /// Applies one variable body the host sent. The layer has already refused a body from another world or another
    /// sender; what is left is the kernel's own snapshot decode, which carries the epoch a second time and refuses a
    /// mismatched one by name.
    /// </summary>
    internal static void ApplyVariables(RuntimeKernel kernel, VariablesMessage message, byte[] payload)
    {
        if (message.WorldEpoch != kernel.WorldEpoch || message.SenderSession == 0)
            throw new RuntimeContractException(NetworkCodes.StaleWorld, "A variable body belongs to another world.");
        kernel.ApplyVariables(Encoding.UTF8.GetString(payload));
    }
}
