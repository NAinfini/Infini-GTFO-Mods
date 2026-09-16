using System;
using System.Collections.Generic;
using System.Text;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// The presentation direction of the command-request channel: the host asks one client to present a step of a plan
/// both sides hold, and the client answers with what presenting did. One message kind carries both directions, so
/// the checks here are about the routing that tells them apart and about the answer reaching the host.
/// </summary>
internal static class PresentationChecks
{
    internal static void Run(Suite suite)
    {
        Routed(suite);
        Answered(suite);
        Unaddressed(suite);
    }

    /// <summary>A presentation request the host addressed to this client is handed to the client's presenter with
    /// the plan, the step and the payload the host resolved, and its answer travels back as a result the host
    /// accepts from the client it asked.</summary>
    private static void Routed(Suite suite)
    {
        suite.Case("presentation: host asks a client and reads the answer");
        var (host, client) = Harness.Pair();
        Harness.Handshake(host, client);
        string? seenPlan = null; var seenStep = -1; string? seenPayload = null;
        client.PresentationReceiver = (request, payload, _, _) =>
        {
            seenPlan = request.PlanId; seenStep = request.BindingIndex;
            seenPayload = Encoding.UTF8.GetString(payload);
            return new NetworkReply(request.EventId, client.LocalSession,
                new NetworkCommandOutcome(NetworkCommandStatuses.Partial, NetworkCommitStates.None, "presented", "", 0));
        };
        var asked = harnessRequest(host.LocalSession, planId: "forge.plan.alpha", stepIndex: 1, eventId: 7, endpoint: "2", payload: "{\"condition\":{\"id\":\"x\"}}");
        // The sender the double reports is the session the game would attribute the send to: the host asked, so
        // the request carries the host's session and the client's presenter answers it.
        var answer = Harness.Deliver(client, host.LocalSession, asked);
        suite.True(answer.HasValue, "a client answers a presentation request with a result");
        if (answer.HasValue)
        {
            suite.Equal(NetworkStatus.Partial, answer.Value.Status, "the answer carries the presenter's status");
            suite.Equal(NetworkCommitState.None, answer.Value.CommitState, "the answer commits nothing");
            suite.Equal((ulong)2, answer.Value.SubjectSession, "the answer names the client it is about");
        }
        suite.Equal("forge.plan.alpha", seenPlan, "the presenter reads the plan the host named");
        suite.Equal(1, seenStep, "the presenter reads the step the host named");
        suite.Equal("{\"condition\":{\"id\":\"x\"}}", seenPayload, "the presenter reads the inputs the host resolved");
        // The host accepts the answer as this client's, which is what closes the request.
        var observedStatus = (byte)0;
        host.ResultObserver = result => observedStatus = result.Status;
        var delivered = Harness.Dispatch(host, Harness.Envelope(new NetworkApiDouble.Send(
            NetworkProtocol.CommandResultEvent, false, answer!.Value, SNetwork.SNet_ChannelType.GameOrderCritical, (ulong)2)));
        suite.Equal(NetworkStatus.Partial, observedStatus, "the host accepts a result from the client it asked");
        suite.True(delivered is null, "receiving a result answers nothing");
    }

    /// <summary>Without an installed presenter the client still answers: the host asked, and "nothing presented
    /// here" is an answer rather than a dropped request.</summary>
    private static void Answered(Suite suite)
    {
        suite.Case("presentation: a client without a presenter answers no-consumer");
        var (host, client) = Harness.Pair();
        var ask = harnessRequest(host.LocalSession, planId: "forge.plan.alpha", stepIndex: 0, eventId: 9, endpoint: "2", payload: "{}");
        var answer = Harness.Deliver(client, host.LocalSession, ask);
        suite.True(answer.HasValue, "the client answers instead of dropping the request");
        suite.Equal(NetworkStatus.Rejected, answer!.Value.Status, "an unanswerable presentation request is refused");
        suite.Equal(NetworkCodes.NoConsumer, answer.Value.Code, "the refusal names the missing consumer");
    }

    /// <summary>A presentation request addressed to another session is not this side's work, and one from a peer
    /// that is not the master is not either: both are left to the host layer's own routing.</summary>
    private static void Unaddressed(Suite suite)
    {
        suite.Case("presentation: another session's request is not answered here");
        var (host, client) = Harness.Pair();
        var elsewhere = harnessRequest(host.LocalSession, planId: "forge.plan.alpha", stepIndex: 0, eventId: 11, endpoint: "3", payload: "{}");
        suite.True(Harness.Deliver(client, host.LocalSession, elsewhere) is null, "a request addressed elsewhere is ignored");
        var fromPeer = harnessRequest(5, planId: "forge.plan.alpha", stepIndex: 0, eventId: 12, endpoint: "2", payload: "{}");
        suite.True(Harness.Deliver(client, 5, fromPeer) is null, "a request from a session that is not the master is ignored");
    }

    /// <summary>One request as the host would send it to present a step: the plan, the command and the step index in
    /// their slots, the sender and the recipient in theirs, and the node index marking the kind.</summary>
    private static CommandRequestMessage harnessRequest(ulong senderSession, string planId, int stepIndex, ulong eventId, string endpoint, string payload)
    {
        var message = new CommandRequestMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = senderSession,
            WorldEpoch = 0,
            EventId = eventId,
            LifeEpoch = 5,
            BindingIndex = stepIndex,
            NodeIndex = NetworkProtocol.PresentationNodeIndex
        };
        message.PlanId = planId;
        message.CommandId = "present.command";
        message.ResourceId = "forge.plan.alpha.binding.present";
        message.Endpoint = endpoint;
        message.PayloadLength = message.SetPayload(0, Encoding.UTF8.GetBytes(payload));
        return message;
    }
}

