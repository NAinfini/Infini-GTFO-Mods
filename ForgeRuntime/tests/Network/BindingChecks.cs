using System;
using System.Collections.Generic;
using ForgeRuntime.GameBindings;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// Binding checks: what a reading of the SNet session facts does to the network host, which handshake verdict may
/// suspend this process, which reason a world transition carries, and which side is allowed to announce an epoch.
/// The decisions are pure, so they run here with the same doubles the transport uses.
/// </summary>
internal static class BindingChecks
{
    private const ulong Local = 7;
    private const ulong Foreign = 9;

    internal static void Run(Suite suite)
    {
        Session(suite);
        Plans(suite);
        Verdicts(suite);
        UnavailableHost(suite);
        Worlds(suite);
        Deferral(suite);
    }

    /// <summary>
    /// A session can open before the first FixedTick discovers plans. Such a side has no set to compare, so it
    /// announces nothing and refuses a peer's hello with the handshake's own code; adopting the set while the session
    /// is open announces it, and the peer's retried hello is then compared against the set the host actually loaded.
    /// </summary>
    private static void Plans(Suite suite)
    {
        suite.Case("binding: a host that adopts a plan set while attached announces it");
        Harness.Open();
        var host = Harness.Attach(SessionRole.Host, 1, adopted: false);
        Harness.Master = 1;
        var logic = For(host, out _, out _);
        logic.Observe(new NetworkSessionFacts(true, 1, Harness.Address(1), true, 1));
        suite.Equal(0, Api.SentCount, "a side with no adopted plan set announces nothing");
        var mark = Api.Mark();
        Api.Deliver(NetworkProtocol.HelloEvent, 2, NetworkCodec.Write(Harness.HelloBody(SessionRole.Client, 2)));
        suite.Equal(NetworkCodes.PlansUnavailable, ((HelloAckMessage)Api.Since(mark).Payload).ReasonCode,
            "a host with no loaded plan cannot compare a peer's plan set");
        suite.True(host.Handshake.Peer(2) is { State: HandshakeState.Mismatched }, "the refusal is the peer's recorded verdict");

        logic.AdoptPlans(Harness.Plans);
        suite.Equal(mark + 2, Api.SentCount, "adopting a set while attached announces it, after the refusal it owes an answer to");
        var announcement = Harness.Envelope(Api.Since(mark + 1));
        suite.Equal(NetworkMessageKind.Hello, announcement.Kind, "the announcement is this side's own hello");
        suite.Equal((byte)2, announcement.Hello.PlanCount, "the announcement advertises the adopted set");
        mark = Api.Mark();
        Api.Deliver(NetworkProtocol.HelloEvent, 2, NetworkCodec.Write(Harness.HelloBody(SessionRole.Client, 2)));
        suite.Equal(NetworkCodes.Accepted, ((HelloAckMessage)Api.Since(mark).Payload).ReasonCode,
            "the hello sent after discovery is compared against the loaded set");
        suite.Equal(mark + 1, Api.SentCount, "answering a hello sends exactly the ack");
        logic.AdoptPlans(Harness.Plans);
        suite.Equal(mark + 1, Api.SentCount, "adopting the same set again announces nothing");
    }

    /// <summary>
    /// A host that cannot compare yet is a retry rather than a difference: the refusal suspends nothing and warns
    /// about nothing, and the host's own announcement — the next message that can change the verdict — is answered
    /// with the same hello once more.
    /// </summary>
    private static void UnavailableHost(Suite suite)
    {
        suite.Case("binding: a host without a plan set is a retry, not a suspension");
        Harness.Open();
        var logic = Build(new List<NetworkHost>(), out var suspensions, out var warnings);
        logic.Observe(new NetworkSessionFacts(true, Local, Harness.Address(Local), false, Foreign));
        logic.AdoptPlans(Harness.Plans);
        suite.Equal(1, Api.SentCount, "the client announces its adopted set once");
        Harness.Master = Foreign;
        var refusal = new HelloAckMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = Foreign,
            SubjectSession = Local,
            Role = (byte)SessionRole.Host,
            Accepted = 0
        };
        refusal.ReasonCode = NetworkCodes.PlansUnavailable;
        Harness.Dispatch(logic.Host!, new NetworkEnvelope(NetworkMessageKind.HelloAck, Foreign, refusal));
        suite.Equal(0, suspensions.Count, "a host that cannot compare yet suspends nothing");
        suite.Equal(0, warnings.Count, "and there is nothing to warn about");
        suite.Equal(NetworkCodes.PlansUnavailable, logic.Host!.Handshake.Peer(Foreign)!.PeerCode, "the refusal is recorded as the host's verdict");

        var mark = Api.Mark();
        Harness.Dispatch(logic.Host!, new NetworkEnvelope(NetworkMessageKind.Hello, Foreign, Harness.HelloBody(SessionRole.Host, Foreign)));
        suite.Equal(mark + 1, Api.SentCount, "the host's own announcement brings the retried hello");
        suite.True(logic.Host!.Handshake.MatchesHost(Foreign), "and the host that now compares opens the client's gate");
        suite.Equal(0, suspensions.Count, "nothing was suspended on the way");
    }

    private static void Session(Suite suite)
    {
        suite.Case("binding: a session attaches once, and a lost session detaches");
        Harness.Open();
        var created = new List<NetworkHost>();
        var logic = Build(created, out _, out _);
        suite.Equal(NetworkSessionAction.None, logic.Observe(new NetworkSessionFacts(false, 0, "", false, 0)), "no local player leaves an unattached binding alone");
        suite.True(logic.Host == null, "nothing is attached without a session");
        suite.Equal(NetworkSessionAction.Attach, logic.Observe(new NetworkSessionFacts(true, Local, Harness.Address(Local), false, Foreign)), "a local player in a session attaches");
        suite.Equal(Local, logic.Session, "the session attached to is the local one");
        suite.Equal(SessionRole.Client, logic.Host!.Role, "a participant that is not the master is the client");
        suite.Equal(1, created.Count, "one host answers for one session");
        suite.Equal(NetworkSessionAction.None, logic.Observe(new NetworkSessionFacts(true, Local, Harness.Address(Local), false, Foreign)), "re-reading the same session changes nothing");
        suite.Equal(1, created.Count, "the same session keeps the host it built");
        suite.Equal(NetworkSessionAction.Detach, logic.Observe(new NetworkSessionFacts(false, 0, "", false, 0)), "losing the local player detaches");
        suite.True(logic.Host == null && logic.Session == 0, "a detached binding holds no host");

        // Another session's peers and world say nothing about this one, so the old host is dropped rather than
        // reconfigured: a session change is the one moment a re-attach is allowed to claim nothing new.
        suite.Equal(NetworkSessionAction.Attach, logic.Observe(new NetworkSessionFacts(true, Local + 1, Harness.Address(Local + 1), true, Local + 1)), "a different session attaches a different host");
        suite.Equal(SessionRole.Host, logic.Host!.Role, "a participant that is the master is the host");
        suite.Equal(2, created.Count, "the second session builds its own host");
        logic.Stop();
        suite.True(logic.Host == null && logic.Session == 0, "stopping drops the session binding");
    }

    private static void Verdicts(Suite suite)
    {
        suite.Case("binding: only a client's own mismatch suspends this process");
        Harness.Open();
        var logic = Build(new List<NetworkHost>(), out var suspensions, out var warnings);
        logic.Observe(new NetworkSessionFacts(true, Local, Harness.Address(Local), false, Foreign));
        logic.ObserveVerdict(new NetworkVerdict(Foreign, true, false, NetworkCodes.PlanSetMismatch, "Plan sets differ: peer advertises 3 plans, this runtime has 2."));
        suite.Equal(1, suspensions.Count, "a client suspends on a mismatched host");
        suite.Equal(NetworkCodes.PlanSetMismatch, suspensions[0], "the suspension carries the refusal code");
        suite.Equal(1, warnings.Count, "the client reports the mismatch once");
        suite.True(warnings[0].Contains("Plan sets differ"), "the warning explains the difference");
        logic.ObserveVerdict(new NetworkVerdict(Foreign, true, false, NetworkCodes.PlanSetMismatch, ""));
        suite.Equal(1, suspensions.Count, "a repeated verdict does not suspend twice");
        logic.ObserveVerdict(new NetworkVerdict(Foreign, true, true, NetworkCodes.Accepted, ""));
        suite.Equal(1, suspensions.Count, "a matched host suspends nothing");
        logic.ObserveVerdict(new NetworkVerdict(Foreign + 1, true, false, NetworkCodes.ProtocolMismatch, ""));
        suite.Equal(1, suspensions.Count, "a verdict about a session that is not the master suspends nothing");

        // A host refuses the peer it decided about and keeps running: suspending the host would end the expedition
        // for everyone in the lobby because of one peer's plan set.
        Harness.Open();
        var host = Build(new List<NetworkHost>(), out var hostSuspensions, out var hostWarnings);
        host.Observe(new NetworkSessionFacts(true, Foreign, Harness.Address(Foreign), true, Foreign));
        host.ObserveVerdict(new NetworkVerdict(Local, false, false, NetworkCodes.PlanSetMismatch, ""));
        host.ObserveVerdict(new NetworkVerdict(Local, true, false, NetworkCodes.RoleMismatch, ""));
        suite.Equal(0, hostSuspensions.Count, "a host never suspends itself over a peer");
        suite.Equal(0, hostWarnings.Count, "a host keeps its own log out of the peer's verdict");
    }

    private static void Worlds(Suite suite)
    {
        suite.Case("binding: only a host announces the epoch the kernel reached");
        var (host, client) = Harness.Pair();
        var hostLogic = For(host, out _, out _);
        hostLogic.Observe(new NetworkSessionFacts(true, 1, Harness.Address(1), true, 1));
        var mark = Api.Mark();
        hostLogic.Announce(5, WorldTransition.NewGeneration, "level generation started");
        suite.Equal(mark + 1, Api.SentCount, "the host announces its world");
        var sent = Api.Since(mark);
        suite.Equal(NetworkProtocol.WorldEpochEvent, sent.EventName, "the announcement is a world epoch");
        var epoch = (WorldEpochMessage)sent.Payload;
        suite.Equal(5L, epoch.WorldEpoch, "the announced epoch is the one the kernel reached");
        suite.Equal((byte)WorldChangeReason.NewGeneration, epoch.Reason, "the reason names the transition");
        suite.Equal("level generation started", epoch.Detail, "the detail travels with the reason");

        var clientLogic = For(client, out _, out _);
        clientLogic.Observe(new NetworkSessionFacts(true, 2, Harness.Address(2), false, 1));
        mark = Api.Mark();
        clientLogic.Announce(5, WorldTransition.LevelCleanup, "level cleanup");
        suite.Equal(mark, Api.SentCount, "a client announces nothing: its world follows the host's broadcast");

        suite.Case("binding: every transition carries its own reason");
        suite.Equal(WorldChangeReason.NewGeneration, NetworkBindingLogic.Reason(WorldTransition.NewGeneration), "a new generation");
        suite.Equal(WorldChangeReason.LevelCleanup, NetworkBindingLogic.Reason(WorldTransition.LevelCleanup), "a level cleanup");
        suite.Equal(WorldChangeReason.HostSuspend, NetworkBindingLogic.Reason(WorldTransition.HostSuspend), "a host suspension");
        // A checkpoint reload no longer ends a world: the runtime keeps running across one, so the transition and
        // the wire reason it mapped to are both gone and nothing produces a suspension for it any more.
    }

    private static void Deferral(Suite suite)
    {
        suite.Case("binding: a world change deferred by a dispatch is announced once");
        var pending = new PendingWorldChange();
        suite.False(pending.Pending, "nothing is pending to begin with");
        suite.False(pending.Take(out _, out _), "an empty deferral yields nothing");
        pending.Remember(WorldTransition.NewGeneration, "level generation started");
        pending.Remember(WorldTransition.LevelCleanup, "level cleanup");
        suite.True(pending.Pending, "a remembered transition is pending");
        suite.True(pending.Take(out var transition, out var detail), "the deferral is taken once");
        suite.Equal(WorldTransition.LevelCleanup, transition, "the last transition names the world the kernel leaves");
        suite.Equal("level cleanup", detail, "the last detail travels with it");
        suite.False(pending.Pending, "taking it consumes the deferral");
        suite.False(pending.Take(out _, out _), "a second take announces nothing");
        var withoutDetail = new PendingWorldChange();
        withoutDetail.Remember(WorldTransition.HostSuspend, null!);
        withoutDetail.Take(out _, out var empty);
        suite.Equal(string.Empty, empty, "a transition without a detail reports none");
    }

    /// <summary>A binding whose hosts are built the way the game builds them: the role comes from SNet, the plan set
    /// from the snapshot this process adopted, and only the master answers for the world. Until a set is adopted the
    /// host is built without one, exactly as it is before plan discovery has run.</summary>
    private static NetworkBindingLogic Build(List<NetworkHost> created, out List<string> suspensions, out List<string> warnings)
    {
        var createdHosts = created;
        var codes = suspensions = new List<string>();
        var messages = warnings = new List<string>();
        return new NetworkBindingLogic(
            (facts, plans) =>
            {
                var host = Harness.Build(facts.IsMaster ? SessionRole.Host : SessionRole.Client, plans: plans, adopted: plans != null);
                host.SenderIsMaster = sender => sender == facts.MasterSession;
                host.Attach(facts.LocalSession, facts.LocalAddress);
                createdHosts.Add(host);
                return host;
            },
            (code, _) => codes.Add(code),
            message => messages.Add(message));
    }

    /// <summary>A binding over an already attached host, for checks about what the binding does with it.</summary>
    private static NetworkBindingLogic For(NetworkHost host, out List<string> suspensions, out List<string> warnings)
    {
        var codes = suspensions = new List<string>();
        var messages = warnings = new List<string>();
        return new NetworkBindingLogic((_, _) => host, (code, _) => codes.Add(code), message => messages.Add(message));
    }
}
