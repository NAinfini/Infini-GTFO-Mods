using System;
using System.Collections.Generic;
using ForgeRuntime.Network;

namespace ForgeRuntime.GameBindings;

/// <summary>What this process knows about its SNet session, read from the game by <c>NetworkBinding</c> so the
/// decisions below never touch a game type. <see cref="LocalSession"/> is the identity the transport attributes a
/// send to — the value GTFO-API hands a receiver as the sender, and the value the handshake compares a peer's
/// claimed session against. <see cref="LocalAddress"/> is the address a requested step names to reach this
/// machine's player, which is the game's own player slot rather than that account identity.</summary>
internal readonly record struct NetworkSessionFacts(bool HasLocalPlayer, ulong LocalSession, string LocalAddress, bool IsMaster, ulong MasterSession);

/// <summary>What one reading of the session facts means for the network host.</summary>
internal enum NetworkSessionAction : byte
{
    None = 0,
    Attach = 1,
    Detach = 2
}

/// <summary>
/// The network layer's two domain-side delegates, as this binding installs them. The executor answers a
/// client-local request the host accepted; the responder answers a requested step the host addressed to this
/// side's own session — a screen to present or an owner write to perform — and is handed the session the request
/// named, because the tier's kernel entry point needs the side it runs on. Both are supplied by the game-facing
/// half, which is the only half that owns a kernel.
/// </summary>
internal delegate NetworkCommandOutcome NetworkCommandRunner(CommandRequestMessage request, byte[] payload);

/// <summary>One requested step this side was addressed with, answered in the terms the wire writes a result from:
/// the session is the identity the answer's subject carries, the address is what the request's endpoint matched.</summary>
internal delegate NetworkReply? NetworkRequestResponder(CommandRequestMessage request, byte[] payload, ulong localSession, string localAddress);

/// <summary>The verdict this process reached about one peer, in the terms the binding decides on. <see cref="Detail"/>
/// is the verdict's own explanation when the handshake produced one — both plan counts for a plan-set difference.</summary>
internal readonly record struct NetworkVerdict(ulong Session, bool IsMaster, bool Matched, string Code, string Detail);

/// <summary>
/// The network binding's decisions, with no game type in reach: what a reading of SNet's session facts means, which
/// world reason a transition carries, whether a handshake verdict suspends this process, and when this side owes the
/// session a hello. The game-facing half (<c>NetworkBinding</c>) reads SNet and the runtime and performs the actions;
/// the network suite drives this class directly with doubles for all three.
/// </summary>
internal sealed class NetworkBindingLogic
{
    private readonly Func<NetworkSessionFacts, IReadOnlyList<PlanIdentity>?, NetworkHost> _create;
    private readonly Action<string, string> _suspend;
    private readonly Action<string> _warn;
    private IReadOnlyList<PlanIdentity>? _plans;
    private ulong _masterSession;
    private bool _suspended;
    private NetworkCommandRunner? _execute;
    private NetworkRequestResponder? _answer;

    internal NetworkBindingLogic(Func<NetworkSessionFacts, IReadOnlyList<PlanIdentity>?, NetworkHost> create, Action<string, string> suspend, Action<string> warn)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _suspend = suspend ?? throw new ArgumentNullException(nameof(suspend));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
    }

    /// <summary>
    /// The two halves of the command-request channel this side runs, decided by which one this side is. A host
    /// executes what its clients request and answers no requested step itself: a request for a presentation or an
    /// owner write is addressed to a client. A client answers both tiers the host requests and executes nothing
    /// itself, because it is not the world's authority. Both are installed on every attach — this is called from
    /// the attach path, before the host is installed — so a session that later changes side is not left with the
    /// previous side's answer.
    /// </summary>
    internal void BindCommands(NetworkCommandRunner? execute, NetworkRequestResponder? answer)
    {
        _execute = execute;
        _answer = answer;
        Install(Host);
    }

    private void Install(NetworkHost? host)
    {
        if (host == null) return;
        // The executor goes on only when this side really has one: while `CommandExecutor` is null the layer
        // refuses an accepted client-local request itself, with the no-consumer code and before the dedup ledger,
        // which is the answer a build without a domain executor owes. Both requested tiers are installed
        // unconditionally — a side that cannot answer one says so through the responder's own null.
        if (_execute != null) host.CommandExecutor = ExecuteRequest;
        host.PresentationReceiver = Answer;
        host.OwnerReceiver = Answer;
    }

    /// <summary>A client-local request the host accepted, run by the domain executor this side was bound with.
    /// The runner is only ever installed while it exists, so this is the one place that can reach it.</summary>
    private NetworkCommandOutcome ExecuteRequest(CommandRequestMessage request, byte[] payload)
        => _execute!.Invoke(request, payload);

    /// <summary>
    /// A requested step the host addressed to this side: a screen to present, or an owner write to perform. This
    /// is the one inbound message a client answers itself: the request names this side's session and the inputs
    /// the host resolved. A request this side cannot answer is reported as a refusal by the kernel's own entry
    /// point or by the domain half, never as a made-up success.
    /// </summary>
    private NetworkReply? Answer(CommandRequestMessage request, byte[] payload, ulong localSession, string localAddress)
        => _answer?.Invoke(request, payload, localSession, localAddress);

    /// <summary>The host this process answers for, or null while it is in no session.</summary>
    internal NetworkHost? Host { get; private set; }

    /// <summary>The SNet session the current host was attached to; zero while detached.</summary>
    internal ulong Session { get; private set; }

    /// <summary>
    /// Applies one reading of the session facts. A local player that is gone detaches; a session this process is not
    /// attached to is attached to, which is also how a second session replaces the first: another session's peers and
    /// world say nothing about this one, so nothing of the old host survives but the game's own registration. A
    /// session that has just opened has heard nothing from this side, so the plan set it holds is announced to it.
    /// </summary>
    internal NetworkSessionAction Observe(NetworkSessionFacts facts)
    {
        _masterSession = facts.MasterSession;
        var session = facts.HasLocalPlayer ? facts.LocalSession : 0;
        if (session == 0)
        {
            if (Host == null) return NetworkSessionAction.None;
            Detach();
            return NetworkSessionAction.Detach;
        }
        if (Host != null && Session == session) return NetworkSessionAction.None;
        Detach();
        var host = _create(facts, _plans);
        host.Handshake.VerdictChanged = peer => ObserveVerdict(new NetworkVerdict(peer.Session, peer.Session == _masterSession, peer.Executable, peer.Code, peer.Detail));
        Host = host;
        Session = session;
        _suspended = false;
        Install(host);
        SendHello(host);
        return NetworkSessionAction.Attach;
    }

    /// <summary>
    /// The plan set this process compares and advertises from now on. A side that adopts one while a session is open
    /// announces it — and when that side is the host, the announcement is the readiness signal: a client whose hello
    /// was refused with <see cref="NetworkCodes.PlansUnavailable"/> has been waiting for exactly this, and answers
    /// with the same hello once more.
    /// </summary>
    internal void AdoptPlans(IReadOnlyList<PlanIdentity> plans)
    {
        _plans = plans;
        if (Host is not { } host) return;
        host.AdoptPlans(plans);
        SendHello(host);
    }

    /// <summary>
    /// The verdicts that reach this process. A host only refuses the peer it just decided about, and a verdict about
    /// any other session changes nothing here, so the vanilla game keeps running in every case. A host that cannot
    /// compare yet is a retry rather than a difference; anything else that is not a match is the suspension ruling,
    /// and a repeated verdict repeats the ruling rather than the warning.
    /// </summary>
    internal void ObserveVerdict(NetworkVerdict verdict)
    {
        if (Host is not { Role: SessionRole.Client } host || !verdict.IsMaster) return;
        // The host had no plan set of its own to compare this side's hello against. Nothing is wrong and nothing is
        // decided: the message that can change this verdict is the host's own announcement.
        if (verdict.Code == NetworkCodes.PlansUnavailable) return;
        if (verdict.Matched)
        {
            // The host has now compared this side's plan set. A hello it refused before that is owed again; a hello it
            // already compared is not, so this sends only where a retry is due.
            SendHello(host);
            return;
        }
        if (_suspended) return;
        _suspended = true;
        var reason = verdict.Detail.Length == 0 ? verdict.Code : verdict.Code + ": " + verdict.Detail;
        var detail = "Forge and the host do not agree (" + reason + "); Forge is suspended for this session and the vanilla game continues.";
        _warn(detail);
        _suspend(verdict.Code, detail);
    }

    /// <summary>
    /// The epoch the kernel really reached, announced to this session's peers. A client has no world of its own to
    /// announce — its network world follows the host's broadcast, which the receive path applies by itself — so only
    /// a host sends here.
    /// </summary>
    internal void Announce(long epoch, WorldTransition transition, string detail)
    {
        if (Host is { Role: SessionRole.Host } host) ForgeNetworkTransport.Send(host.AdvanceWorld(epoch, Reason(transition), detail));
    }

    /// <summary>Drops the session binding: the transport keeps this process's registration, its listener delivers
    /// nothing, and the next session attaches a fresh host with its own role and plan set.</summary>
    internal void Stop()
    {
        Detach();
        _masterSession = 0;
        _suspended = false;
    }

    /// <summary>The wire reason a transition carries. Only the host evaluates one; receivers carry it for
    /// diagnostics, so this is a naming step rather than a decision. A checkpoint reload is not among them any
    /// more: the runtime keeps running across one, so the game itself causes no world change.</summary>
    internal static WorldChangeReason Reason(WorldTransition transition) => transition switch
    {
        WorldTransition.NewGeneration => WorldChangeReason.NewGeneration,
        WorldTransition.LevelCleanup => WorldChangeReason.LevelCleanup,
        _ => WorldChangeReason.HostSuspend
    };

    /// <summary>Sends the hello this side owes the session, if it owes one: a plan set the session has not heard is
    /// announced once, and a side with no adopted plan set announces nothing.</summary>
    private static void SendHello(NetworkHost host)
    {
        if (host.TakeHello() is { } hello) ForgeNetworkTransport.Send(hello);
    }

    private void Detach()
    {
        Host?.Detach();
        Host = null;
        Session = 0;
    }
}
