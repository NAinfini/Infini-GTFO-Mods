using System;
using System.Collections.Generic;
using System.Text;

namespace ForgeRuntime.Network;

/// <summary>
/// Frozen startup facts one participant hands the network layer. Everything here is decided outside the network
/// layer — the loaded plan set, the runtime lock, the role for this process — so the network layer never reaches
/// into the kernel or the level lifecycle to find them. A null plan set means plan discovery has not run for this
/// session yet, which is not the same as a session whose discovered set is empty.
/// </summary>
internal sealed record NetworkHostConfiguration(
    SessionRole Role,
    RuntimeIdentitySummary Identity,
    IReadOnlyList<PlanIdentity>? Plans,
    long WorldEpoch = 0);

/// <summary>What the host's local executor is asked to do for one accepted request.</summary>
/// <param name="request">The decoded request; its identity fields are the dedup key that admitted it.</param>
/// <param name="payload">The request body bytes, opaque to this layer.</param>
internal delegate NetworkCommandOutcome NetworkCommandExecutor(CommandRequestMessage request, byte[] payload);

/// <summary>What a client does with a committed result or a committed fact. Both are projections for the domain
/// layer; the network layer stores neither.</summary>
internal delegate void NetworkResultObserver(CommandResultMessage result);

/// <summary>What a client does with a committed fact.</summary>
internal delegate void NetworkFactObserver(FactMessage fact, byte[] payload);

/// <summary>What a client does with the host's variable state: the kernel's own encoded snapshot, applied as the
/// host's values. The flag says whether the body is the whole table (a late joiner's first copy, or a world's first
/// broadcast) or the advance's own delta; the kernel accepts either, and the flag is what a diagnostic reads.</summary>
internal delegate void NetworkVariablesObserver(VariablesMessage variables, byte[] payload);

/// <summary>
/// What a client does with a request the host addressed to its own `presentation` step: present it and answer with
/// the result, or answer with the refusal that says why this side could not. Null means this request is not this
/// side's to answer, which the layer reports as the no-consumer refusal. The reply is returned as the two facts the
/// wire needs — what happened, and which request it answers — so this layer keeps owning every send.
/// </summary>
internal delegate NetworkReply? NetworkPresentationReceiver(CommandRequestMessage request, byte[] payload, ulong localSession, string localAddress);

/// <summary>
/// What a client does with a request the host addressed to its own `owner` step: execute the write on the machine
/// that holds it and answer with the handler's own result and facts. It is the presentation receiver's sibling on
/// the same message, and the same null rule applies: a request this side was not addressed with is not its answer.
/// </summary>
internal delegate NetworkReply? NetworkOwnerReceiver(CommandRequestMessage request, byte[] payload, ulong localSession, string localAddress);

/// <summary>One requested command's answer, in the terms a result message is written from. The subject session is
/// carried rather than re-derived, because the session a request addresses and the session that answers it are the
/// same fact and only the layer that admitted the request knows it for certain. Both requested tiers answer with
/// this one shape: what differs between them is what the recipient's own handler did, which the outcome carries.</summary>
internal readonly record struct NetworkReply(ulong EventId, ulong SubjectSession, NetworkCommandOutcome Outcome);

/// <summary>A snapshot of the layer's state, for the host's diagnostics.</summary>
internal readonly record struct NetworkHostStatus(
    SessionRole Role,
    long WorldEpoch,
    ulong HostSession,
    int PeerCount,
    int LedgerCount,
    int DuplicateHits,
    int Conflicts,
    int LedgerRefusals,
    int StaleRefusals,
    bool Registered);

/// <summary>
/// The Forge network layer: it owns the handshake, the dedup ledger and the epoch bookkeeping, and it owns no
/// lifecycle. Registration is explicit (<see cref="Attach"/>/<see cref="Detach"/>) and the caller decides when that
/// is true; nothing here subscribes to a level event or starts a tick.
/// <para>
/// Inbound traffic is refused before it reaches the domain layer: an unhandshaken peer, a wrong protocol version, a
/// stale world, a repeated key or a full ledger all produce an explicit code, and only a first-arrival request ever
/// calls <see cref="NetworkCommandExecutor"/>.
/// </para>
/// </summary>
internal sealed class NetworkHost
{
    private readonly NetworkHandshake _handshake;
    private readonly WorldEpochSync _epoch;
    private bool _registered;
    private long _transportToken;
    /// <summary>Whether this session has heard this side's current plan set. A hello is owed while it has not.</summary>
    private bool _helloSent;
    /// <summary>This side's own counter over the variable bodies it sent, so a receiver can order them.</summary>
    private long _variablesSequence;

    internal NetworkHost(NetworkHostConfiguration configuration)
    {
        Configuration = configuration;
        _handshake = new NetworkHandshake(configuration.Role, configuration.Identity, configuration.Plans);
        _epoch = new WorldEpochSync(configuration.Role, configuration.WorldEpoch);
        Ledger = new NetworkDedupLedger(configuration.WorldEpoch);
        _handshake.AdoptEpoch(configuration.WorldEpoch);
    }

    internal NetworkHostConfiguration Configuration { get; }
    internal NetworkHandshake Handshake => _handshake;
    internal WorldEpochSync Epoch => _epoch;
    internal NetworkDedupLedger Ledger { get; }

    internal SessionRole Role => Configuration.Role;

    /// <summary>Set by the host integration: the executor for accepted requests, and the observers for the results
    /// and facts a client receives. Unset means the layer still applies every gate and simply reports nothing.</summary>
    internal NetworkCommandExecutor? CommandExecutor { get; set; }
    internal NetworkResultObserver? ResultObserver { get; set; }
    internal NetworkFactObserver? FactObserver { get; set; }
    /// <summary>Set by the client integration when it can present: the answer to a presentation request this
    /// side's own session was addressed with. A host never sets it — a presentation request is addressed to a
    /// client — and a client that has not set it answers no-consumer rather than a silent drop.</summary>
    internal NetworkPresentationReceiver? PresentationReceiver { get; set; }
    /// <summary>Set by the client integration when it can execute an owner write: the answer to an owner request
    /// this side's own session was addressed with. The rule is the presentation receiver's: a request addressed
    /// elsewhere is not this side's to answer, and one this side cannot answer is refused by name.</summary>
    internal NetworkOwnerReceiver? OwnerReceiver { get; set; }
    /// <summary>Set by the client integration: what it does with the host's variable state. Unset leaves the layer
    /// deciding exactly the same way and applying nothing.</summary>
    internal NetworkVariablesObserver? VariablesObserver { get; set; }

    internal bool Registered => _registered;

    internal NetworkHostStatus Status => new(Role, Ledger.WorldEpoch, _epoch.HostSession, _handshake.Peers.Count, Ledger.Count,
        Ledger.DuplicateHits, Ledger.Conflicts, Ledger.Refusals, Ledger.StaleRefusals, _registered);

    /// <summary>
    /// Registers the protocol's events and starts answering this process's session. The session is the identity the
    /// transport observed, so it is supplied here rather than read from the game by this layer; the address is the
    /// one a requested step names to reach this machine's player, which is a fact about the player rather than
    /// about the account the session identifies.
    /// </summary>
    internal void Attach(ulong localSession, string localAddress)
    {
        if (_registered) throw new NetworkContractException(NetworkCodes.DuplicateEventName, "The Forge network layer is already attached.");
        if (localSession == 0) throw new NetworkContractException(NetworkCodes.FormatInvalid, "A session must be a real SNet session.");
        if (string.IsNullOrEmpty(localAddress)) throw new NetworkContractException(NetworkCodes.FormatInvalid, "A session must have an address to be requested at.");
        LocalSession = localSession;
        LocalAddress = localAddress;
        _handshake.BindLocalSession(localSession);
        // A session that has just opened has heard nothing from this side, so its plan set is owed to it.
        _helloSent = false;
        // A host is the world's owner, so it is the session its own broadcasts name. A client learns that session
        // from the accepted ack instead, which is why this binding is the host's alone.
        if (Configuration.Role == SessionRole.Host) _epoch.BindHost(localSession);
        _transportToken = ForgeNetworkTransport.Register(Receive);
        _registered = true;
    }

    /// <summary>
    /// Stops this layer owning the protocol events. GTFO-API cannot unregister, so the game-side registration stays
    /// with a listener that delivers nothing; a later <see cref="Attach"/> takes the names over.
    /// </summary>
    internal void Detach()
    {
        if (!_registered) return;
        ForgeNetworkTransport.Release(_transportToken);
        _transportToken = 0;
        _registered = false;
        LocalSession = 0;
        LocalAddress = "";
        _helloSent = false;
    }

    internal ulong LocalSession { get; private set; }

    /// <summary>The address this side's player is reached at, as the provider that owns the tier spells it. A
    /// requested step names it in its endpoint slot, so this is what the address of an inbound request is compared
    /// against; it is empty while this side is in no session.</summary>
    internal string LocalAddress { get; private set; } = "";

    /// <summary>This side's hello, to be sent to a peer.</summary>
    internal NetworkMessage Hello() => new(NetworkMessageKind.Hello, _handshake.Hello(RequireSession()));

    /// <summary>
    /// True while this side owes the session a hello: it has a session, a plan set it can advertise, and the session
    /// has not heard that plan set from it. A side that has adopted nothing has nothing to announce, and a side whose
    /// announcement the session already heard has nothing new to say.
    /// </summary>
    internal bool HelloOwed => _registered && _handshake.PlansAdopted && !_helloSent;

    /// <summary>
    /// Takes the hello this side owes, or null when the session has already heard this plan set. Taking it is what
    /// makes it sent: a caller sends exactly what this returns, and asks again only when a plan set is adopted or a
    /// peer announces its own.
    /// </summary>
    internal NetworkMessage? TakeHello()
    {
        if (!HelloOwed) return null;
        _helloSent = true;
        return Hello();
    }

    /// <summary>
    /// Adopts the plan set this side compares and advertises from now on. A set the session has not heard is owed to
    /// it again, which is what makes a host's adoption the readiness signal its clients wait for: the client that was
    /// refused with <see cref="NetworkCodes.PlansUnavailable"/> sees the host announce a set it can compare, and
    /// answers with the same hello once more.
    /// </summary>
    internal void AdoptPlans(IReadOnlyList<PlanIdentity> plans)
    {
        if (_handshake.AdoptPlans(plans)) _helloSent = false;
    }

    /// <summary>
    /// The host moves to a new world. The epoch rises, the dedup ledger drops every key from the old world, and the
    /// message is ready to broadcast. The caller does the same local reset on a client when it accepts the
    /// broadcast.
    /// </summary>
    internal NetworkMessage AdvanceWorld(long worldEpoch, WorldChangeReason reason, string detail = "")
    {
        // A broadcast needs a session to name as its sender, so a layer that never attached cannot advance the world
        // it would then have to announce. The role check is the epoch rule's own, and it runs next.
        RequireSession();
        var message = _epoch.Advance(worldEpoch, reason, detail);
        _handshake.RaiseEpoch(worldEpoch);
        Ledger.BeginWorld(worldEpoch);
        return new NetworkMessage(NetworkMessageKind.WorldEpoch, message);
    }

    /// <summary>Drops a departed peer's verdict and remembered results, so a rejoining session starts over.</summary>
    internal void Forget(ulong session)
    {
        _handshake.Forget(session);
        Ledger.Forget(session);
    }

    /// <summary>
    /// Inbound dispatch. Every kind is checked against the same protocol version first, then against the state that
    /// kind needs: a hello updates the peer verdict, an ack settles the handshake, an epoch moves a client's world,
    /// and a request or result leaves the layer again as a returned message or an observer call. This is a decision
    /// over one message and its sender, so it is answerable whether or not the transport has been attached; what the
    /// attach state governs is sending, not deciding.
    /// </summary>
    internal NetworkMessage? Receive(NetworkEnvelope envelope)
    {
        switch (envelope.Kind)
        {
            case NetworkMessageKind.Hello: return ReceiveHello(envelope);
            case NetworkMessageKind.HelloAck: return ReceiveAck(envelope, senderIsMaster: SenderIsMaster(envelope.Sender));
            case NetworkMessageKind.WorldEpoch: return ReceiveEpoch(envelope);
            case NetworkMessageKind.CommandRequest: return ReceiveRequest(envelope);
            case NetworkMessageKind.CommandResult: return ReceiveResult(envelope);
            case NetworkMessageKind.Fact: return ReceiveFact(envelope);
            case NetworkMessageKind.Variables: return ReceiveVariables(envelope);
            default: throw new NetworkContractException(NetworkCodes.EventNameUnknown, "Message kind " + envelope.Kind + " has no receive path.");
        }
    }

    /// <summary>
    /// Whether the inbound sender is the current master. The runtime supplies this (SNet's master), and it is asked
    /// here rather than read from a message, so an authority claim never travels in a payload.
    /// </summary>
    internal Func<ulong, bool> SenderIsMaster { get; set; } = _ => false;

    /// <summary>
    /// A peer's hello. A host compares the client's plan set and answers with its verdict. A client that is handed
    /// the host's own hello has just been told which plan set the host compares: a hello the host answered
    /// <see cref="NetworkCodes.PlansUnavailable"/> was refused rather than compared, so it is owed again. Clearing it
    /// before the comparison is what lets the verdict observer see the hello as owed, and reading the *previous*
    /// verdict is what keeps a hello the host already compared from being sent twice.
    /// </summary>
    private NetworkMessage? ReceiveHello(NetworkEnvelope envelope)
    {
        var senderIsMaster = SenderIsMaster(envelope.Sender);
        if (Role == SessionRole.Client && senderIsMaster
            && _handshake.Peer(envelope.Sender) is { PeerCode: NetworkCodes.PlansUnavailable })
            _helloSent = false;
        var answer = _handshake.ReceiveHello(envelope, senderIsMaster);
        // A peer that just announced its plan set is a peer that can be told the world's variables: this hello is
        // what a late joiner sends, and the whole table is what it is owed. The reply is the ack the caller still
        // owes it, so the snapshot is sent here rather than returned — one message travels back per return value.
        if (Role == SessionRole.Host && VariablesSource != null
            && _handshake.Peer(envelope.Sender) is { PeerCode: NetworkCodes.Accepted } && _registered)
            ForgeNetworkTransport.Send(TakeVariables(complete: true));
        return answer;
    }

    /// <summary>
    /// Set by the host integration: the kernel's whole variable table, encoded, and this advance's own delta. The
    /// layer owns neither the values nor their encoding — it owns when they travel — so the two are supplied as one
    /// source: a host with no source broadcasts nothing and a client applies nothing.
    /// </summary>
    internal Func<(string Complete, string Delta)>? VariablesSource { get; set; }

    /// <summary>
    /// One variable body: the whole table for a late joiner or a new world, otherwise the advance's own delta. The
    /// sequence is this side's own counter, so a receiver can tell a reordered body from the next one; the epoch is
    /// the kernel's, because a snapshot is only meaningful against the world it was taken in.
    /// </summary>
    internal NetworkMessage TakeVariables(bool complete)
    {
        RequireSession();
        var source = VariablesSource ?? throw new NetworkContractException(NetworkCodes.NoConsumer, "This side has no variable source to send.");
        var snapshot = source();
        var message = new VariablesMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = LocalSession,
            WorldEpoch = _epoch.WorldEpoch,
            Sequence = ++_variablesSequence,
            Complete = complete ? (byte)1 : (byte)0
        };
        var payload = Encoding.UTF8.GetBytes(complete ? snapshot.Complete : snapshot.Delta);
        if (payload.Length > NetworkProtocol.MaximumPayloadBytes)
            throw new NetworkContractException(NetworkCodes.PayloadTooLarge, "The variable snapshot is larger than one payload slot.");
        message.PayloadLength = message.SetPayload(0, payload);
        message.Code = NetworkCodes.VariablesSent;
        message.Detail = complete ? "complete" : "delta";
        return new NetworkMessage(NetworkMessageKind.Variables, message);
    }

    private NetworkMessage? ReceiveVariables(NetworkEnvelope envelope)
    {
        var claimed = envelope.ClaimedProtocol;
        if (claimed != NetworkProtocol.Version || Role != SessionRole.Client) return null;
        if (!SenderIsMaster(envelope.Sender)) return null;
        // A body from a world this client already left names objects the current world does not have, and the
        // kernel refuses it by the same rule (`variable-snapshot`); the layer drops it first so nothing is decoded
        // against a world that ended.
        if (envelope.Variables.WorldEpoch != Ledger.WorldEpoch) return null;
        VariablesObserver?.Invoke(envelope.Variables, envelope.Variables.PayloadBytes(0, envelope.Variables.PayloadLength));
        return null;
    }

    private NetworkMessage? ReceiveAck(NetworkEnvelope envelope, bool senderIsMaster)
    {
        var claimed = envelope.ClaimedProtocol;
        if (claimed != NetworkProtocol.Version) return null;
        var evaluation = _handshake.ReceiveAck(envelope, senderIsMaster);
        if (Role != SessionRole.Client || !evaluation.Accepted) return null;
        _epoch.BindHost(envelope.Sender);
        _epoch.Synchronize(envelope.Ack.HostWorldEpoch);
        // The host's world is now the world this client reasons about, so late messages from any earlier epoch are
        // refused from here on rather than answered against a stale ledger.
        _handshake.AdoptEpoch(envelope.Ack.HostWorldEpoch);
        Ledger.EnterWorld(envelope.Ack.HostWorldEpoch);
        return null;
    }

    private NetworkMessage? ReceiveEpoch(NetworkEnvelope envelope)
    {
        var claimed = envelope.ClaimedProtocol;
        if (claimed != NetworkProtocol.Version) return null;
        if (Role != SessionRole.Client) return null;
        var receipt = _epoch.Observe(envelope.Epoch, envelope.Sender, SenderIsMaster(envelope.Sender));
        if (!receipt.Accepted) return null;
        // The accepted broadcast is what retires the previous world: its ledger keys and its handshake epoch both
        // move with it, in the same step, so no request can straddle the two.
        _handshake.RaiseEpoch(receipt.WorldEpoch);
        Ledger.BeginWorld(receipt.WorldEpoch);
        return null;
    }

    private NetworkMessage? ReceiveRequest(NetworkEnvelope envelope)
    {
        var claimed = envelope.ClaimedProtocol;
        if (claimed != NetworkProtocol.Version) return null;
        var message = envelope.Request;
        var subject = message.SenderSession;
        // A requested write runs the other way around: the host asks one client to present a screen or to perform
        // an owner write, so the side that answers it is a client and the side that sends it is the master. It is
        // answered here, before the handshake gate, because the requester's peers are the clients this side does
        // not own verdicts for — what authorizes it is the sender being the session's master, which is asked of
        // the runtime, not read off the message. Both tiers travel as the same request and differ only in their
        // marker, which is what the answer's own tier carries.
        if (Role == SessionRole.Client
            && message.NodeIndex is NetworkProtocol.PresentationNodeIndex or NetworkProtocol.OwnerNodeIndex)
        {
            // The address is part of the request, and the layer owns it: a requested step names the player address
            // it runs on, so a request naming another one is not this side's answer to give. The address is the
            // local player's own rather than the session the message travelled under: the session says who sent it,
            // the address says which player the step is for.
            if (!SenderIsMaster(message.SenderSession) || LocalSession == 0 || message.Endpoint != LocalAddress) return null;
            var requested = envelope.Request.PayloadBytes(0, message.PayloadLength);
            // A client with no path for the tier answers that fact rather than dropping the request: the host
            // that asked is owed an answer either way, and "nothing ran here" is one.
            var answered = (message.NodeIndex == NetworkProtocol.OwnerNodeIndex
                    ? OwnerReceiver?.Invoke(message, requested, LocalSession, LocalAddress)
                        ?? new NetworkReply(message.EventId, LocalSession,
                            NetworkCommandOutcome.Refused(NetworkCodes.NoConsumer, "This side has no owner path."))
                    : PresentationReceiver?.Invoke(message, requested, LocalSession, LocalAddress)
                        ?? new NetworkReply(message.EventId, LocalSession,
                            NetworkCommandOutcome.Refused(NetworkCodes.NoConsumer, "This side has no presentation path.")));
            return Result(answered.SubjectSession, answered.EventId, message.WorldEpoch, answered.Outcome);
        }
        if (Role != SessionRole.Host) return null;
        var refused = _handshake.RequirePeer(subject);
        if (refused != null) return Result(subject, message.EventId, message.WorldEpoch, NetworkCommandOutcome.Refused(refused));
        if (message.WorldEpoch != Ledger.WorldEpoch)
            return Result(subject, message.EventId, message.WorldEpoch, NetworkCommandOutcome.Refused(NetworkCodes.StaleWorld));
        // A request no one can run is refused before the ledger sees it: there is no outcome to remember, and
        // remembering the refusal would answer every retransmission with it even after a consumer appears. No
        // executor is installed by the host on purpose: a domain package supplies one, and until then the layer
        // says so instead of accepting work nothing will do.
        if (CommandExecutor == null)
            return Result(subject, message.EventId, message.WorldEpoch, NetworkCommandOutcome.Refused(NetworkCodes.NoConsumer));
        var key = new NetworkRequestKey(subject, message.WorldEpoch, message.PlanId, message.EventId);
        var payload = message.PayloadBytes(0, message.PayloadLength);
        var decision = Ledger.Submit(key, payload, () => CommandExecutor(message, payload));
        if (!decision.Accepted) return Result(subject, message.EventId, message.WorldEpoch, NetworkCommandOutcome.Refused(decision.Code));
        return Result(subject, message.EventId, message.WorldEpoch, decision.Cached ?? decision.Outcome!);
    }

    private NetworkMessage? ReceiveResult(NetworkEnvelope envelope)
    {
        var claimed = envelope.ClaimedProtocol;
        if (claimed != NetworkProtocol.Version) return null;
        // Two directions, decided by the role. A client accepts the host's answer to its own request. A host
        // accepts the answer one of its clients gives to a presentation request, which is the one message that
        // travels host-to-client and is answered by the receiver: the observer is told the same way, and which
        // request an answer belongs to is read from the message the observer is handed.
        if (Role == SessionRole.Host)
        {
            if (_handshake.RequirePeer(envelope.Sender) != null) return null;
            ResultObserver?.Invoke(envelope.Result);
            return null;
        }
        // Only the host's answer counts, and only about this client's own request.
        if (!SenderIsMaster(envelope.Sender) || envelope.Result.SubjectSession != LocalSession) return null;
        ResultObserver?.Invoke(envelope.Result);
        return null;
    }

    private NetworkMessage? ReceiveFact(NetworkEnvelope envelope)
    {
        var claimed = envelope.ClaimedProtocol;
        if (claimed != NetworkProtocol.Version || Role != SessionRole.Client) return null;
        if (!SenderIsMaster(envelope.Sender)) return null;
        // A fact from a world this client already left would be applied to objects that no longer exist.
        if (envelope.Fact.WorldEpoch != Ledger.WorldEpoch) return null;
        FactObserver?.Invoke(envelope.Fact, envelope.Fact.PayloadBytes(0, envelope.Fact.PayloadLength));
        return null;
    }

    private NetworkMessage Result(ulong subject, ulong eventId, long worldEpoch, NetworkCommandOutcome outcome)
    {
        var message = new CommandResultMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = RequireSession(),
            SubjectSession = subject,
            EventId = eventId,
            WorldEpoch = worldEpoch,
            Status = Encode(outcome.Status),
            CommitState = NetworkCommitStates.Encode(outcome.CommitState),
            FactCount = outcome.FactCount
        };
        // A result is a reply to one request, so the plan and command identity slots are never written: the requester
        // recognizes its own request by the key it chose. An object initializer starts every slot empty, and a typed
        // payload is Marshalled as the struct itself, so an unmentioned slot is empty on the wire.
        message.Code = WireText.Clip(outcome.Code, NetworkProtocol.TextSlotBytes);
        message.Detail = WireText.Clip(outcome.Detail, NetworkProtocol.TextSlotBytes);
        return new NetworkMessage(NetworkMessageKind.CommandResult, message);
    }

    private static byte Encode(string status) => status switch
    {
        NetworkCommandStatuses.Succeeded => NetworkStatus.Succeeded,
        NetworkCommandStatuses.Partial => NetworkStatus.Partial,
        NetworkCommandStatuses.Rejected => NetworkStatus.Rejected,
        NetworkCommandStatuses.Failed => NetworkStatus.Failed,
        NetworkCommandStatuses.Cancelled => NetworkStatus.Cancelled,
        NetworkCommandStatuses.Expired => NetworkStatus.Expired,
        // A status the wire cannot name would arrive at the client as whatever the default was, so the sender refuses
        // it instead: the executor and this mapping share one vocabulary, and a mismatch is a fault, not a rejection.
        _ => throw new NetworkContractException(NetworkCodes.FormatInvalid, "Command status '" + status + "' has no wire spelling.")
    };

    private ulong RequireSession()
    {
        if (LocalSession == 0) throw new NetworkContractException(NetworkCodes.HelloUnsent, "The network layer is not attached to a session.");
        return LocalSession;
    }
}
