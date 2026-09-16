using System;
using System.Collections.Generic;
using System.Text;
using ForgeRuntime.GameBindings;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// The owner direction of the command-request channel: the host decides an `owner` step and asks the one session
/// that holds what it changes to execute it. It travels as the presentation tier's own message with its own node
/// index, so the checks here are about the marker that tells the two apart, about the answer reaching the host, and
/// about the client that has no owner path answering rather than dropping the request.
/// </summary>
internal static class OwnerChecks
{
    internal static void Run(Suite suite)
    {
        Routed(suite);
        Answered(suite);
        Tiers(suite);
        Unaddressed(suite);
        Bound(suite);
    }

    /// <summary>An owner request the host addressed to this client is handed to the client's owner receiver with
    /// the plan, the step, the scope and the payload the host resolved, and its answer travels back as a result the
    /// host accepts from the client it asked.</summary>
    private static void Routed(Suite suite)
    {
        suite.Case("owner: host asks the holder and reads the answer");
        var (host, client) = Harness.Pair();
        Harness.Handshake(host, client);
        string? seenPlan = null; var seenStep = -1; string? seenScope = null; string? seenPayload = null;
        client.OwnerReceiver = (request, payload, _, _) =>
        {
            seenPlan = request.PlanId; seenStep = request.BindingIndex; seenScope = request.ResourceId;
            seenPayload = Encoding.UTF8.GetString(payload);
            // An owner write commits: the answer keeps the handler's own status, commit state and fact count.
            return new NetworkReply(request.EventId, client.LocalSession,
                new NetworkCommandOutcome(NetworkCommandStatuses.Succeeded, NetworkCommitStates.Confirmed, "owner-committed", "", 2));
        };
        var asked = OwnerRequest(host.LocalSession, planId: "forge.plan.alpha", stepIndex: 3, eventId: 21,
            endpoint: "2", scope: "gtfo.world:1", payload: "{\"equipment\":{\"id\":\"gtfo.equipment:1\"}}");
        var answer = Harness.Deliver(client, host.LocalSession, asked);
        suite.True(answer.HasValue, "a client answers an owner request with a result");
        if (answer.HasValue)
        {
            suite.Equal(NetworkStatus.Succeeded, answer.Value.Status, "the answer carries the handler's status");
            suite.Equal(NetworkCommitState.Confirmed, answer.Value.CommitState, "an owner write really committed");
            suite.Equal(2, answer.Value.FactCount, "the facts the write published travel with the answer");
            suite.Equal((ulong)2, answer.Value.SubjectSession, "the answer names the client it is about");
        }
        suite.Equal("forge.plan.alpha", seenPlan, "the holder reads the plan the host decided");
        suite.Equal(3, seenStep, "the holder reads the step the host decided");
        suite.Equal("gtfo.world:1", seenScope, "the holder reads the scope its write commits in");
        suite.Equal("{\"equipment\":{\"id\":\"gtfo.equipment:1\"}}", seenPayload, "the holder reads the inputs the host resolved");
        // The host accepts the answer as this client's, which is what closes the request.
        var observed = (byte)0;
        host.ResultObserver = result => observed = result.Status;
        var delivered = Harness.Dispatch(host, Harness.Envelope(new NetworkApiDouble.Send(
            NetworkProtocol.CommandResultEvent, false, answer!.Value, SNetwork.SNet_ChannelType.GameOrderCritical, (ulong)2)));
        suite.Equal(NetworkStatus.Succeeded, observed, "the host accepts a result from the client it asked");
        suite.True(delivered is null, "receiving a result answers nothing");
    }

    /// <summary>Without an installed owner path the client still answers: the host asked, and "nothing ran here"
    /// is an answer rather than a dropped request.</summary>
    private static void Answered(Suite suite)
    {
        suite.Case("owner: a client without an owner path answers no-consumer");
        var (host, client) = Harness.Pair();
        var ask = OwnerRequest(host.LocalSession, "forge.plan.alpha", 0, 22, "2", "gtfo.world:1", "{}");
        var answer = Harness.Deliver(client, host.LocalSession, ask);
        suite.True(answer.HasValue, "the client answers instead of dropping the request");
        suite.Equal(NetworkStatus.Rejected, answer!.Value.Status, "an unanswerable owner request is refused");
        suite.Equal(NetworkCodes.NoConsumer, answer.Value.Code, "the refusal names the missing consumer");
    }

    /// <summary>The node index is what a request's tier is: a presentation request is never answered by the owner
    /// path and an owner request is never answered by the presenter, so one message kind carries both without a
    /// second layout and without either tier stealing the other's work.</summary>
    private static void Tiers(Suite suite)
    {
        suite.Case("owner: the marker picks the tier, not the receiver");
        var (host, client) = Harness.Pair();
        var ownerAsked = 0; var presented = 0;
        client.OwnerReceiver = (request, payload, _, _) =>
        {
            ownerAsked++;
            return new NetworkReply(request.EventId, client.LocalSession,
                new NetworkCommandOutcome(NetworkCommandStatuses.Succeeded, NetworkCommitStates.Confirmed, "owner", "", 0));
        };
        client.PresentationReceiver = (request, payload, _, _) =>
        {
            presented++;
            return new NetworkReply(request.EventId, client.LocalSession,
                new NetworkCommandOutcome(NetworkCommandStatuses.Partial, NetworkCommitStates.None, "presented", "", 0));
        };
        Harness.Deliver(client, host.LocalSession, OwnerRequest(host.LocalSession, "forge.plan.alpha", 0, 23, "2", "gtfo.world:1", "{}"));
        suite.Equal(1, ownerAsked, "an owner request reaches the owner path");
        suite.Equal(0, presented, "an owner request never reaches the presenter");
        var presentation = OwnerRequest(host.LocalSession, "forge.plan.alpha", 0, 24, "2", "gtfo.world:1", "{}");
        presentation.NodeIndex = NetworkProtocol.PresentationNodeIndex;
        Harness.Deliver(client, host.LocalSession, presentation);
        suite.Equal(1, presented, "a presentation request reaches the presenter");
        suite.Equal(1, ownerAsked, "a presentation request never reaches the owner path");
    }

    /// <summary>A requested step addressed to another session is not this side's work, and one from a peer that is
    /// not the master is not either: both are left to the host layer's own routing.</summary>
    private static void Unaddressed(Suite suite)
    {
        suite.Case("owner: another session's request is not answered here");
        var (host, client) = Harness.Pair();
        var elsewhere = OwnerRequest(host.LocalSession, "forge.plan.alpha", 0, 25, "3", "gtfo.world:1", "{}");
        suite.True(Harness.Deliver(client, host.LocalSession, elsewhere) is null, "a request addressed elsewhere is ignored");
        var fromPeer = OwnerRequest(5, "forge.plan.alpha", 0, 26, "2", "gtfo.world:1", "{}");
        suite.True(Harness.Deliver(client, 5, fromPeer) is null, "a request from a session that is not the master is ignored");
    }

    /// <summary>
    /// The binding installs both receivers on the host it attaches, and the domain responder it was bound with is
    /// what answers them: a receiver installed by the attach path itself would be overwritten by the binding's own
    /// install, which is the difference between a working client and one that answers no path at all.
    /// </summary>
    private static void Bound(Suite suite)
    {
        suite.Case("owner: the binding installs the responder it was bound with");
        Harness.Open();
        var host = Harness.Attach(SessionRole.Client, 2);
        var logic = new NetworkBindingLogic((_, _) => host, (_, _) => { }, _ => { });
        var asked = 0; var executed = 0;
        logic.BindCommands(
            null,
            (request, payload, session, _) =>
            {
                asked++; executed += request.NodeIndex == NetworkProtocol.OwnerNodeIndex ? 1 : 0;
                return new NetworkReply(request.EventId, session,
                    new NetworkCommandOutcome(NetworkCommandStatuses.Succeeded, NetworkCommitStates.Confirmed, "owner", "", 0));
            });
        Harness.Master = 1;
        logic.Observe(new NetworkSessionFacts(true, 2, Harness.Address(2), false, 1));
        var answer = Harness.Deliver(host, 1, OwnerRequest(1, "forge.plan.alpha", 0, 27, "2", "gtfo.world:1", "{}"));
        suite.True(answer.HasValue, "the attached client answers a request the host addressed to it");
        suite.Equal(1, asked, "the bound responder is the one the host's install reaches");
        suite.Equal(1, executed, "the responder was handed the owner tier's own marker");
    }

    /// <summary>One request as the host would send it to run an owner step: the plan, the command, the step and the
    /// scope in their slots, the holder in the address, and the node index marking the tier.</summary>
    private static CommandRequestMessage OwnerRequest(ulong senderSession, string planId, int stepIndex, ulong eventId,
        string endpoint, string scope, string payload)
    {
        var message = new CommandRequestMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = senderSession,
            WorldEpoch = 0,
            EventId = eventId,
            LifeEpoch = 5,
            BindingIndex = stepIndex,
            NodeIndex = NetworkProtocol.OwnerNodeIndex
        };
        message.PlanId = planId;
        message.CommandId = "owner.command";
        message.ResourceId = scope;
        message.Endpoint = endpoint;
        message.PayloadLength = message.SetPayload(0, Encoding.UTF8.GetBytes(payload));
        return message;
    }
}
