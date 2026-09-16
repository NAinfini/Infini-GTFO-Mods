using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ForgeRuntime.Network;

/// <summary>
/// One plan's identity, as the handshake compares it. Plan files are never serialized: a plan's planId, the resource
/// it was compiled from, its resource revision and its frozen binding pin list are what tell two peers apart.
/// </summary>
internal readonly record struct PlanIdentity(string PlanId, string ResourceId, string ResourceRevision, string BindingPins);

/// <summary>
/// The whole plan set's one comparison value: SHA-256 over the rows <c>planId|resourceId|resourceRevision|bindingPins</c>,
/// one row per line, in ordinal planId order. The rows are hashed, never sent: a hello carries the digest and the
/// count, so the size of a plan set never bounds a message. A set is digested when it is adopted — once — and never
/// inside a tick.
/// </summary>
internal static class PlanSetDigest
{
    /// <summary>The digest of a canonical plan set: the caller passes the rows in the ordinal order both sides sort
    /// into, which is what makes the digest independent of the order a plan set was declared in.</summary>
    internal static byte[] Of(IReadOnlyList<PlanIdentity> canonical)
    {
        var text = new StringBuilder();
        for (var index = 0; index < canonical.Count; index++)
        {
            if (index != 0) text.Append('\n');
            var plan = canonical[index];
            text.Append(plan.PlanId).Append('|').Append(plan.ResourceId).Append('|')
                .Append(plan.ResourceRevision).Append('|').Append(plan.BindingPins);
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>Two digests are the same set summary; the comparison is over all 32 bytes, never a prefix.</summary>
    internal static bool Equal(byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right);
}

/// <summary>The local runtime facts a peer checks: which runtime, on which API version, against which game build.</summary>
internal readonly record struct RuntimeIdentitySummary(string Id, string Version, string ApiVersion, string GameBuild);

/// <summary>What one hello comparison decided, with the exact difference to report.</summary>
internal readonly record struct HandshakeEvaluation(bool Accepted, string Code, string Detail);

/// <summary>
/// The three states a participant's Forge can be in for one peer. <see cref="Matched"/> is the only state in which
/// that peer's requests are executable; <see cref="Mismatched"/> keeps the gameplay gate closed and carries the
/// reason code the host integration must surface.
/// </summary>
internal enum HandshakeState : byte
{
    Idle = 0,
    Waiting = 1,
    Matched = 2,
    Mismatched = 3
}

/// <summary>The handshake verdict for one peer, and the identity it was decided against.</summary>
internal sealed class PeerHandshake
{
    internal PeerHandshake(ulong session, SessionRole role, HandshakeState state, string code, string detail, RuntimeIdentitySummary identity, int planCount)
    {
        Session = session;
        Role = role;
        State = state;
        Code = code;
        Detail = detail;
        Identity = identity;
        PlanCount = planCount;
    }

    internal ulong Session { get; }
    internal SessionRole Role { get; }
    internal HandshakeState State { get; private set; }
    /// <summary>The local verdict for this peer; <see cref="NetworkCodes.Accepted"/> while accepted.</summary>
    internal string Code { get; private set; }
    /// <summary>Why <see cref="Code"/> was reached, in the terms a reader can act on — both plan counts for a plan-set
    /// difference. The wire carries codes, not sentences, so this is filled wherever the verdict is decided here.</summary>
    internal string Detail { get; private set; }
    internal RuntimeIdentitySummary Identity { get; }
    /// <summary>How many plans the peer advertised. The digest decides the comparison; the count is what a mismatch
    /// is reported with.</summary>
    internal int PlanCount { get; }
    /// <summary>The verdict this peer sent about us. A peer that refuses us is recorded even while our own check
    /// accepted it: both sides must agree before anything runs.</summary>
    internal HandshakeState PeerState { get; private set; } = HandshakeState.Idle;
    internal string PeerCode { get; private set; } = NetworkCodes.HelloUnsent;

    internal bool Executable => State == HandshakeState.Matched && PeerState != HandshakeState.Mismatched;

    /// <summary>Replaces this peer's verdict with a later hello from the same session.</summary>
    internal void Update(HandshakeState state, string code, string detail)
    {
        State = state;
        Code = code;
        Detail = detail;
    }

    /// <summary>Records what the peer itself decided about this side.</summary>
    internal void UpdatePeer(HandshakeState state, string code)
    {
        PeerState = state;
        PeerCode = code;
    }
}

/// <summary>
/// The hello/hello-ack state machine, pure logic: it owns identities, the plan-set comparison and the per-peer
/// verdict, and it owns nothing about how a message travels.
/// <para>
/// The comparison is symmetric on purpose. The host checks every joining client and answers with an explicit reason
/// code; the client checks the host's own hello the same way, so a client never has to trust a verdict it cannot
/// reproduce. A mismatch leaves that client's Forge suspended (its own state stays
/// <see cref="HandshakeState.Mismatched"/>), which is what keeps the vanilla game running while Forge stays out.
/// </para>
/// </summary>
internal sealed class NetworkHandshake
{
    private readonly Dictionary<ulong, PeerHandshake> _peers = new();
    private byte[] _digest = Array.Empty<byte>();

    /// <summary>
    /// Builds the handshake around a plan snapshot, or around none: a null snapshot means plan discovery has not run
    /// for this session yet, which is a state no peer's hello can be compared against.
    /// </summary>
    internal NetworkHandshake(SessionRole role, RuntimeIdentitySummary identity, IReadOnlyList<PlanIdentity>? plans)
    {
        if (role == SessionRole.Unknown) throw new NetworkContractException(NetworkCodes.RoleMismatch, "A session must know its role.");
        Role = role;
        Identity = identity;
        Plans = Array.Empty<PlanIdentity>();
        if (plans != null) AdoptPlans(plans);
    }

    internal SessionRole Role { get; }

    /// <summary>The local runtime lock this side compares every peer against. It is frozen for the session: a plan
    /// set may follow later plan discovery, the runtime identity never does.</summary>
    internal RuntimeIdentitySummary Identity { get; }

    /// <summary>The local plan set, ordered ordinally: both sides sort the same way, so the rows the digest covers
    /// are the same rows on both machines.</summary>
    internal IReadOnlyList<PlanIdentity> Plans { get; private set; }

    /// <summary>The digest of <see cref="Plans"/>. It is what a hello advertises and what the comparison uses.</summary>
    internal byte[] Digest => _digest;

    /// <summary>True once a plan set has been adopted, empty or not. A peer's hello is answered with
    /// <see cref="NetworkCodes.PlansUnavailable"/> until then rather than compared against nothing.</summary>
    internal bool PlansAdopted { get; private set; }

    /// <summary>The world epoch the host stamps on its hellos; host-owned, raised by the epoch broadcast.</summary>
    internal long WorldEpoch { get; private set; }

    internal IReadOnlyCollection<PeerHandshake> Peers => _peers.Values;

    /// <summary>The host's verdict about a client, or null when that client never completed a hello.</summary>
    internal PeerHandshake? Peer(ulong session) => _peers.TryGetValue(session, out var peer) ? peer : null;

    /// <summary>
    /// Called with the peer whenever this process records a verdict about it. Nothing in the handshake depends on a
    /// listener: it is how the host integration learns that a client cannot run this host's plans, or that a client
    /// must suspend itself, without the handshake knowing what suspending means.
    /// </summary>
    internal Action<PeerHandshake>? VerdictChanged { get; set; }

    /// <summary>
    /// Adopts the plan set this side compares and advertises from now on. The canonical order and the digest are
    /// computed here, once, at adoption — never per message and never per tick. The result says whether the set
    /// really changed, so a caller can tell a repeated adoption of the same set from a new one.
    /// </summary>
    internal bool AdoptPlans(IReadOnlyList<PlanIdentity> plans)
    {
        // The count travels in one byte. The kernel's own plan limit is far below it; a set that outgrew the field
        // would be reported as a different set on the peer, so it is refused here instead.
        if (plans.Count > byte.MaxValue)
            throw new NetworkContractException(NetworkCodes.PayloadTooLarge, "A plan set of " + plans.Count + " plans does not fit the hello's plan count.");
        var canonical = Canonical(plans);
        var digest = PlanSetDigest.Of(canonical);
        var changed = !PlansAdopted || !PlanSetDigest.Equal(_digest, digest);
        Plans = canonical;
        _digest = digest;
        PlansAdopted = true;
        return changed;
    }

    /// <summary>The host raises its world epoch; the value only ever moves forward.</summary>
    internal void RaiseEpoch(long epoch)
    {
        if (epoch <= WorldEpoch) throw new NetworkContractException(NetworkCodes.EpochNotIncreasing, "Host epoch " + epoch + " is not above " + WorldEpoch + ".");
        WorldEpoch = epoch;
    }

    /// <summary>Seeds the epoch this side advertises, at startup or when a client adopts the host's world. Unlike
    /// <see cref="RaiseEpoch"/> this accepts a value equal to the current one, because adopting a handshake is not a
    /// world transition.</summary>
    internal void AdoptEpoch(long epoch)
    {
        if (epoch < WorldEpoch) throw new NetworkContractException(NetworkCodes.EpochNotIncreasing, "Epoch " + epoch + " is below " + WorldEpoch + ".");
        WorldEpoch = epoch;
    }

    /// <summary>
    /// Builds this side's hello: its runtime identity, its plan count and its plan-set digest. A side with no adopted
    /// plan set has nothing to announce and nothing to compare, so it cannot build one.
    /// </summary>
    internal HelloMessage Hello(ulong session)
    {
        if (!PlansAdopted) throw new NetworkContractException(NetworkCodes.PlansUnavailable, "A hello needs an adopted plan set.");
        var message = new HelloMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = session,
            Role = (byte)Role,
            PlanCount = (byte)Plans.Count
        };
        message.RuntimeId = Identity.Id;
        message.RuntimeVersion = Identity.Version;
        message.ApiVersion = Identity.ApiVersion;
        message.GameBuild = Identity.GameBuild;
        message.SetPlanSetDigest(_digest);
        return message;
    }

    /// <summary>
    /// Handles a peer's hello. A host checks the client and records the verdict; a client checks the host and
    /// returns the ack its own verdict produces, so a mismatch is reported to the host that would otherwise keep
    /// sending to a client that cannot run its plans.
    /// </summary>
    internal NetworkMessage? ReceiveHello(NetworkEnvelope envelope, bool senderIsMaster)
    {
        var message = envelope.Hello;
        if (envelope.Sender == 0) throw new NetworkContractException(NetworkCodes.FormatInvalid, "Hello without a sender.");
        if (message.SenderSession != envelope.Sender)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Hello claims session " + message.SenderSession + " but arrived from " + envelope.Sender + ".");
        var peerRole = (SessionRole)message.Role;
        var identity = new RuntimeIdentitySummary(message.RuntimeId, message.RuntimeVersion, message.ApiVersion, message.GameBuild);
        var planCount = message.PlanCount;
        var digest = message.PlanSetDigest();
        if (Role == SessionRole.Host)
        {
            var evaluation = Compare(message.ProtocolVersion, peerRole, SessionRole.Client, identity, planCount, digest);
            Record(new PeerHandshake(envelope.Sender, peerRole, evaluation.Accepted ? HandshakeState.Matched : HandshakeState.Mismatched,
                evaluation.Code, evaluation.Detail, identity, planCount));
            return new NetworkMessage(NetworkMessageKind.HelloAck, Ack(evaluation, envelope.Sender));
        }
        // A client's own hello is addressed to the host, and SNet — not the message — says who the host is. A hello
        // from any other session is refused with the reason code for the fault and answered with nothing: answering a
        // host that SNet has not named would let a second Forge process claim this client's world, and recording it
        // would overwrite the verdict about the host this client actually follows.
        if (peerRole != SessionRole.Host || !senderIsMaster)
        {
            Record(new PeerHandshake(envelope.Sender, peerRole, HandshakeState.Mismatched,
                peerRole == SessionRole.Host ? NetworkCodes.SenderNotHost : NetworkCodes.RoleMismatch, "", identity, planCount));
            return null;
        }
        var hostEvaluation = Compare(message.ProtocolVersion, peerRole, SessionRole.Host, identity, planCount, digest);
        Record(new PeerHandshake(envelope.Sender, peerRole,
            hostEvaluation.Accepted ? HandshakeState.Matched : HandshakeState.Mismatched, hostEvaluation.Code, hostEvaluation.Detail, identity, planCount));
        return new NetworkMessage(NetworkMessageKind.HelloAck, Ack(hostEvaluation, envelope.Sender));
    }

    /// <summary>
    /// Handles the host's answer to this side's hello. On a host this records what the peer decided about us; on a
    /// client it is the verdict that opens or closes the gameplay gate.
    /// </summary>
    internal HandshakeEvaluation ReceiveAck(NetworkEnvelope envelope, bool senderIsMaster)
    {
        var message = envelope.Ack;
        if (message.SenderSession != envelope.Sender)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Ack claims session " + message.SenderSession + " but arrived from " + envelope.Sender + ".");
        if (message.ProtocolVersion != NetworkProtocol.Version)
            return Settle(envelope.Sender, NetworkCodes.ProtocolMismatch, "Ack protocol " + message.ProtocolVersion + " is not " + NetworkProtocol.Version + ".");
        if (message.SubjectSession != LocalSession)
            return Settle(envelope.Sender, NetworkCodes.AckUnmatched, "Ack answers session " + message.SubjectSession + ".");
        // A client knows the master from SNet, not from the message: an ack that did not come from the current host
        // cannot change this client's verdict.
        if (Role == SessionRole.Client && !senderIsMaster)
            return Settle(envelope.Sender, NetworkCodes.SenderNotHost, "Ack arrived from a non-master session.");
        var accepted = message.Accepted == 1;
        var code = accepted ? NetworkCodes.Accepted : NonEmpty(message.ReasonCode, NetworkCodes.HostUnmatched);
        return Settle(envelope.Sender, code, "");
    }

    /// <summary>Remembers the session this side sends its own messages under. The receive path compares an ack's
    /// subject against it, and the send path stamps it on every body.</summary>
    internal void BindLocalSession(ulong session) => LocalSession = session;

    internal ulong LocalSession { get; private set; }

    /// <summary>Drops a departed peer. Its requests, results and verdicts leave with it: a rejoining session is a
    /// new identity and must handshake again.</summary>
    internal void Forget(ulong session) => _peers.Remove(session);

    /// <summary>The client's gate: true only while the current host's verdict is a match.</summary>
    internal bool MatchesHost(ulong hostSession) => Role == SessionRole.Client
        && _peers.TryGetValue(hostSession, out var host) && host.State == HandshakeState.Matched;

    /// <summary>
    /// The host's gate for one peer: the rejection code while that peer may not run anything, null while it may.
    /// A peer that never handshook is refused rather than assumed compatible.
    /// </summary>
    internal string? RequirePeer(ulong session)
    {
        if (Role != SessionRole.Host) return NetworkCodes.RoleMismatch;
        if (!_peers.TryGetValue(session, out var peer)) return NetworkCodes.HelloUnsent;
        return peer.Executable ? null : peer.Code;
    }

    /// <summary>
    /// The whole comparison, in a fixed order so the reported reason is the first real difference rather than
    /// whichever check happened to run: protocol, role, runtime identity, runtime API version, game build, then the
    /// plan set. The identity checks are separate so a peer that runs a different runtime is told that, rather than
    /// being told its plans do not match.
    /// </summary>
    internal HandshakeEvaluation Compare(ushort protocol, SessionRole peerRole, SessionRole expectedRole, RuntimeIdentitySummary identity,
        int peerPlanCount, byte[] peerDigest)
    {
        if (protocol != NetworkProtocol.Version)
            return new HandshakeEvaluation(false, NetworkCodes.ProtocolMismatch, "Peer protocol " + protocol + " is not " + NetworkProtocol.Version + ".");
        if (peerRole != expectedRole)
            return new HandshakeEvaluation(false, NetworkCodes.RoleMismatch, "Peer role " + peerRole + " is not " + expectedRole + ".");
        if (identity.Id != Identity.Id || identity.Version != Identity.Version)
            return new HandshakeEvaluation(false, NetworkCodes.RuntimeMismatch, "Peer runtime " + identity.Id + " " + identity.Version + " is not " + Identity.Id + " " + Identity.Version + ".");
        if (identity.ApiVersion != Identity.ApiVersion)
            return new HandshakeEvaluation(false, NetworkCodes.RuntimeMismatch, "Peer runtime API " + identity.ApiVersion + " is not " + Identity.ApiVersion + ".");
        if (identity.GameBuild != Identity.GameBuild)
            return new HandshakeEvaluation(false, NetworkCodes.RuntimeMismatch, "Peer game build " + identity.GameBuild + " is not " + Identity.GameBuild + ".");
        return ComparePlans(peerPlanCount, peerDigest);
    }

    /// <summary>
    /// The plan-set comparison: equal counts and equal digests mean the same set, because both sides hash the same
    /// rows in the same order. A set this side has not adopted cannot be compared at all — that is a retry for the
    /// peer, not a difference — while a different set is reported with both counts.
    /// </summary>
    internal HandshakeEvaluation ComparePlans(int peerCount, byte[] peerDigest)
    {
        if (!PlansAdopted)
            return new HandshakeEvaluation(false, NetworkCodes.PlansUnavailable, "This runtime has no plan set to compare yet.");
        if (peerDigest.Length != NetworkProtocol.PlanSetDigestBytes)
            return new HandshakeEvaluation(false, NetworkCodes.FormatInvalid, "A plan-set digest is " + NetworkProtocol.PlanSetDigestBytes + " bytes, not " + peerDigest.Length + ".");
        if (peerCount != Plans.Count || !PlanSetDigest.Equal(_digest, peerDigest))
            return new HandshakeEvaluation(false, NetworkCodes.PlanSetMismatch, PlanDifference(peerCount));
        return new HandshakeEvaluation(true, NetworkCodes.Accepted, "");
    }

    /// <summary>How a plan-set difference is explained. The digests say only that the sets differ, so the two counts
    /// are what a reader can act on.</summary>
    internal string PlanDifference(int peerPlanCount)
        => "Plan sets differ: peer advertises " + peerPlanCount + " plans, this runtime has " + Plans.Count + ".";

    private HandshakeEvaluation Settle(ulong session, string code, string detail)
    {
        // The peer that answered may not be the session we addressed; the verdict still belongs to whoever answered.
        if (!_peers.TryGetValue(session, out var peer))
        {
            peer = new PeerHandshake(session, Role == SessionRole.Host ? SessionRole.Client : SessionRole.Host,
                HandshakeState.Mismatched, NetworkCodes.HelloUnsent, "", default, 0);
            _peers[session] = peer;
        }
        var state = code == NetworkCodes.Accepted ? HandshakeState.Matched : HandshakeState.Mismatched;
        peer.UpdatePeer(state, code);
        // On a client the peer's answer *is* the local verdict: there is no second decision to make about the host.
        // The ack carries a code rather than an explanation, so a plan-set difference is explained here from the
        // count the peer advertised.
        if (Role == SessionRole.Client) peer.Update(state, code, code == NetworkCodes.PlanSetMismatch ? PlanDifference(peer.PlanCount) : "");
        VerdictChanged?.Invoke(peer);
        return new HandshakeEvaluation(code == NetworkCodes.Accepted, code, detail);
    }

    /// <summary>Records a verdict and hands it to the observer, so every verdict this process reaches is reported
    /// once, after it is readable through <see cref="Peer"/>.</summary>
    private void Record(PeerHandshake peer)
    {
        _peers[peer.Session] = peer;
        VerdictChanged?.Invoke(peer);
    }

    private HelloAckMessage Ack(HandshakeEvaluation evaluation, ulong subject)
    {
        var message = new HelloAckMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = LocalSession,
            SubjectSession = subject,
            Role = (byte)Role,
            Accepted = (byte)(evaluation.Accepted ? 1 : 0),
            HostWorldEpoch = WorldEpoch
        };
        message.ReasonCode = evaluation.Code;
        message.HostRuntimeId = Identity.Id;
        message.HostRuntimeVersion = Identity.Version;
        message.HostApiVersion = Identity.ApiVersion;
        message.HostGameBuild = Identity.GameBuild;
        return message;
    }

    private static string NonEmpty(string value, string fallback) => value.Length == 0 ? fallback : value;

    private static IReadOnlyList<PlanIdentity> Canonical(IReadOnlyList<PlanIdentity> plans)
    {
        var sorted = new List<PlanIdentity>(plans);
        sorted.Sort((left, right) => string.CompareOrdinal(left.PlanId, right.PlanId));
        for (var index = 1; index < sorted.Count; index++)
            if (sorted[index].PlanId == sorted[index - 1].PlanId)
                throw new NetworkContractException(NetworkCodes.PlanSetMismatch, "Duplicate plan id " + sorted[index].PlanId + ".");
        return sorted.AsReadOnly();
    }
}
