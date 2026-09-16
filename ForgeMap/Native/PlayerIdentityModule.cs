using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;
using Player;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>MAP5a player entity identity. One life is one PlayerAgent instance observed for one SNet_Player
/// in one Runtime world. Downed, revive, heal and warp never allocate or end a life here; respawn, landing
/// and checkpoint semantics belong to the rest of MAP5 and are not modelled. Only the host allocates lives.</summary>
internal sealed class PlayerIdentityModule : IPlayerLifeWorld, IDisposable
{
    internal const string EntityKind = "gtfo.player";
    private const string Prefix = EntityKind + ":";
    // Key is SNet_Player.Lookup, a Steam64 account ID on real players. It is a private dictionary key only:
    // never formatted, logged, serialized or placed in an entity ID, error or diagnostic.
    private sealed record Entry(ulong Key, SNet_Player Player, IntPtr PlayerPointer, PlayerAgent Agent, IntPtr AgentPointer,
        EntityReference Reference);
    private readonly record struct Observed(SNet_Player Player, PlayerAgent Agent);
    private readonly Dictionary<long, Entry> _players = new();
    // Per-world entity number for a player key, kept across that player's lives until the world changes.
    private readonly Dictionary<ulong, long> _numbers = new();
    private readonly HashSet<IntPtr> _unresolvedReported = new();
    private readonly HashSet<ulong> _conflictReported = new();
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canObserve;
    private readonly Action<string> _log, _warn;
    private readonly RuntimeModuleHandle _registration;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private long _nextLife, _nextNumber;
    private bool _disposed;
    internal bool IsRegistered => !_disposed && _registration.IsRegistered;

    /// <summary>The registered module whose lives the player selector answers with. The runtime rejects two
    /// providers of one identity namespace, so at most one Map module is ever registered, and the property is
    /// cleared again when that registration goes away.</summary>
    internal static PlayerIdentityModule? Current { get; private set; }

    /// <summary>Whether this process may read a recorded life at all right now: the registration is live, the
    /// runtime is ready and this peer is the authority. It is the same gate every readback in this class already
    /// applies, exposed because the life half asks before it reads rather than deriving a second gate.</summary>
    public bool Authoritative => CanObserve;

    /// <summary>Attaches this domain's half to the one Map registration. The registration itself is owned by
    /// the session, because the runtime accepts exactly one provider of an identity namespace and both of this
    /// package's namespaces have to be declared by that one provider. The registration is already in place when
    /// this constructor runs, so a rejected registration leaves no module and this half is simply not attached.</summary>
    internal PlayerIdentityModule(RuntimeModuleHandle registration, RuntimeKernel kernel, Func<bool> canObserve, Action<string> log, Action<string> warn)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canObserve = canObserve ?? throw new ArgumentNullException(nameof(canObserve));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
        // The resolver, the instance lookup, the read-only observer and the `forge.selector.target.players`
        // query binding are this half's whole runtime surface. The observer reads one named life through the
        // kernel's budgeted query; the binding answers with the lives this module records, so a selector never
        // enumerates native state on its own. The instance lookup lets other domains name a player without
        // ever seeing the account key.
        // Assigned only after the registration that owns it succeeded: a rejected registration leaves no
        // module for a selector step to answer from, and every later refusal is the kernel's own.
        Current = this;
        try
        {
            // Checkpoint reload, level cleanup and authority loss reach Map as a host world change; no local copy.
            _lifecycle = _registration.ObserveLifecycle(value =>
            {
                if (value.Kind == RuntimeLifecycleKind.WorldChanged
                    || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                    ClearWorld();
            });
        }
        catch { if (ReferenceEquals(Current, this)) Current = null; throw; }
    }

    internal int Count { get { CheckThread(); return _players.Count; } }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Map player identity requires the owning simulation thread.");
    }

    // Not the InLevel gameplay gate: agents are spawned during the elevator states of the same world epoch,
    // before InLevel, and a gated first readback would leave them unrecorded.
    private bool CanObserve
        => IsRegistered && _kernel.StartupState == RuntimeStartupState.Ready && _canObserve() && SNet.IsMaster;

    internal void ClearWorld()
    {
        CheckThread();
        _players.Clear(); _numbers.Clear(); _unresolvedReported.Clear(); _conflictReported.Clear(); _nextNumber = 0;
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _lifecycle.Dispose(); ClearWorld(); _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>Reads the in-level agent list after a native spawn or despawn body and reconciles lives.
    /// A changed agent or player pointer for a player key is a new life under the same per-world number.
    /// Public because it is the interface member <see cref="IPlayerLifeWorld"/> declares, which is how the
    /// player-life facts half reaches the identity this module owns.</summary>
    public void Reconcile()
    {
        CheckThread();
        if (!CanObserve) return;
        var observed = new Dictionary<ulong, Observed>();
        var conflicts = new HashSet<ulong>();
        var agents = PlayerManager.PlayerAgentsInLevel;
        int count = agents == null ? 0 : agents.Count;
        for (int i = 0; i < count; i++)
        {
            var agent = agents![i];
            if (agent == null) continue; // Unity equality: destroyed agents are not in the level.
            var player = agent.Owner;
            if (player == null || !Linked(player, agent))
            {
                if (_unresolvedReported.Add(agent.Pointer))
                    _warn("map.player-owner-unresolved: an in-level agent without a linked SNet_Player is not recorded.");
                continue;
            }
            ulong key = player.Lookup;
            if (observed.TryGetValue(key, out var prior))
            {
                if (prior.Agent.Pointer != agent.Pointer || prior.Player.Pointer != player.Pointer) conflicts.Add(key);
                continue;
            }
            observed.Add(key, new Observed(player, agent));
        }
        foreach (var entry in new List<Entry>(_players.Values))
        {
            string? reason = !observed.TryGetValue(entry.Key, out var now) ? "despawned"
                : conflicts.Contains(entry.Key) ? "key-conflict"
                : entry.Reference.WorldEpoch != _kernel.WorldEpoch || now.Agent.Pointer != entry.AgentPointer
                    || now.Player.Pointer != entry.PlayerPointer ? "replaced" : null;
            if (reason == null) continue;
            _players.Remove(_numbers[entry.Key]);
            _log("map.player-life-ended " + Describe(entry.Reference) + " reason=" + reason);
        }
        foreach (var item in observed)
        {
            if (conflicts.Contains(item.Key))
            {
                if (_conflictReported.Add(item.Key))
                    _warn("map.player-key-conflict: two in-level agents claim one SNet_Player; neither is recorded.");
                continue;
            }
            if (!_numbers.TryGetValue(item.Key, out var number)) _numbers.Add(item.Key, number = checked(++_nextNumber));
            if (_players.ContainsKey(number)) continue;
            var reference = new EntityReference(Prefix + number.ToString(CultureInfo.InvariantCulture),
                _kernel.WorldEpoch, checked(++_nextLife));
            var now = item.Value;
            _players.Add(number, new Entry(item.Key, now.Player, now.Player.Pointer, now.Agent, now.Agent.Pointer, reference));
            _log("map.player-life-started " + Describe(reference) + " bot=" + (now.Player.IsBot ? "true" : "false"));
        }
    }

    private Entry? Resolve(EntityReference reference)
    {
        CheckThread();
        if (!IsRegistered || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return null;
        if (reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(Prefix, StringComparison.Ordinal)
            || !long.TryParse(reference.Id.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || !_players.TryGetValue(number, out var entry) || entry.Reference != reference) return null;
        var agent = entry.Agent;
        var player = entry.Player;
        if (agent == null || agent.Pointer != entry.AgentPointer || player == null || player.Pointer != entry.PlayerPointer
            || player.Lookup != entry.Key) return null;
        var owner = agent.Owner;
        return owner != null && owner.Pointer == entry.PlayerPointer && Linked(player, agent) ? entry : null;
    }

    internal bool IsCurrent(EntityReference reference) => Resolve(reference) != null;

    /// <summary>The entity the replication data of a spawn names, or null while the identity tracks no life for
    /// that player. The player is matched by its native pointer, exactly as the instance lookup does, and the
    /// reference that answers is the one <see cref="Resolve"/> already verified.</summary>
    public EntityReference? ReferenceOf(SNet_Player? player)
    {
        CheckThread();
        if (player == null || !CanObserve) return null;
        var pointer = player.Pointer;
        foreach (var entry in _players.Values)
            if (entry.PlayerPointer == pointer) return Resolve(entry.Reference)?.Reference;
        return null;
    }

    /// <summary>The entity of a native agent a callback carried, or null when this module tracks no life for it.
    /// It is the same pointer comparison the instance lookup performs, over the agents the module recorded rather
    /// than over the player objects.</summary>
    public EntityReference? ReferenceOf(object? instance)
    {
        CheckThread();
        if (instance is not PlayerAgent agent || agent == null || !CanObserve) return null;
        var pointer = agent.Pointer;
        foreach (var entry in _players.Values)
            if (entry.AgentPointer == pointer) return Resolve(entry.Reference)?.Reference;
        return null;
    }

    /// <summary>One read of a recorded life right now: alive from the agent's own flag, downed from the
    /// locomotion machine's current state, and whether that state already ran its revive. A state machine that
    /// is not there to read answers "not revived" rather than guessing. Null means the identity no longer holds
    /// the reference, which is not the same as a life that is simply not downed.</summary>
    public RuntimePlayerLife? LifeOf(EntityReference reference)
    {
        CheckThread();
        if (!CanObserve) return null;
        if (Resolve(reference) is not { } entry || entry.Agent is not { } agent) return null;
        var locomotion = agent.Locomotion;
        bool readLocomotion = locomotion != null && locomotion.Pointer != IntPtr.Zero;
        var downed = readLocomotion && locomotion!.m_currentStateEnum == PlayerLocomotion.PLOC_State.Downed;
        var state = readLocomotion && downed ? locomotion!.TryCast<PLOC_Downed>() : null;
        return new RuntimePlayerLife(agent.Alive, downed, state is { m_isRevived: true });
    }

    /// <summary>The actor the game recorded on a downed life's revive interaction. The interop surface of this
    /// build does not expose that actor: the interact base keeps its per-interactor records in a nested info type
    /// whose `Agent` member the interaction itself does not carry, so there is no read here to make. The answer is
    /// null, and the `revived` fact's rescuer is the one the revive interaction's own callback named, exactly as a
    /// `revive_started` row's is — the caller keeps that actor and this read adds none.</summary>
    public EntityReference? ReviverOf(EntityReference reference)
    {
        CheckThread();
        return null;
    }

    /// <summary>The position of a recorded life in metres, or null when it cannot be read. Only finite
    /// coordinates answer: a position the game could not produce is not a position.</summary>
    public double[]? PositionOf(EntityReference reference)
    {
        CheckThread();
        if (!CanObserve) return null;
        if (Resolve(reference) is not { } entry || entry.Agent is not { } agent) return null;
        var position = agent.Position;
        return float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
            ? new double[] { position.x, position.y, position.z } : null;
    }

    /// <summary>The position a teleport argument names, in metres, or null when it is not a finite position.
    /// The argument is the game's own location struct, so the read is the same finite-coordinate rule the
    /// recorded life's own position goes through.</summary>
    public double[]? Position(object? locationData)
    {
        CheckThread();
        if (!CanObserve) return null;
        if (locationData is not pPlayerLocationData location) return null;
        var position = location.goodPosition;
        return float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
            ? new double[] { position.x, position.y, position.z } : null;
    }

    /// <summary>The position of a recorded life in metres, or null when it cannot be read. It is the same read
    /// <see cref="PositionOf"/> performs, under the name the life-world contract declares.</summary>
    public double[]? Position(EntityReference reference) => PositionOf(reference);

    /// <summary>The refusal a life transition gets when it is routed through the identity. The identity half owns
    /// which native agent is which recorded life and how to read it; publishing a transition is the player-life
    /// trigger half's, which does it once per transition. The members below implement the contract's transition
    /// half so this module carries exactly the surface it is read through, and every one of them refuses: a
    /// caller that reached them went around the one publisher, and answering `false` would report that as the
    /// transition having been judged.</summary>
    internal const string TransitionOwnerCode = "life-transition-owner";

    public bool Downed(object? downedState) => Forbidden();

    public bool Revived(object? downedState) => Forbidden();

    public bool ReviveStarted(object? rescuer, object? target) => Forbidden();

    public bool ReviveCancelled(string reason, object? rescuer, object? target) => Forbidden();

    public bool Died(bool alive, object? agent) => Forbidden();

    public bool Teleported(object? agent, object? destination) => Forbidden();

    public bool Respawned(object? spawnData) => Forbidden();

    private static bool Forbidden()
        => throw new RuntimeContractException(TransitionOwnerCode,
            "Player-life transitions are published by the player-life trigger half, not by the identity.");

    /// <summary>The identity half keeps no publication log: it publishes nothing. The contract asks for one
    /// because the half that does publish keeps it.</summary>
    public IReadOnlyList<string> Journal => Array.Empty<string>();

    /// <summary>Drops this world's recorded lives. The kernel's world epoch is what invalidates the references
    /// already handed out; this releases the table they were read from.</summary>
    public void BeginWorld() => ClearWorld();

    /// <summary>Whether a native change to a recorded life is legal right now: the same readiness, authority,
    /// ownership and fault gate every readback goes through. The action layer asks this instead of deriving a
    /// second gate from the kernel and the network flag, so a commit can never be attempted in a state the
    /// module would refuse to read in.</summary>
    internal bool CanCommit => CanObserve;

    /// <summary>The current agent behind a recorded life, for the half that reads or commits native state on it;
    /// null when the reference is not this module's current life. The instance is handed out only after the same
    /// verification a snapshot read performs, and the caller re-reads and re-compares the pointers it captured
    /// before it commits anything, so a life replaced mid-command is refused rather than written through.</summary>
    internal PlayerAgent? CurrentAgent(EntityReference reference)
    {
        CheckThread();
        return Resolve(reference)?.Agent;
    }

    /// <summary>Read-only snapshot for a current player life: position, health, alive/downed/dead and the
    /// host, local, bot and slot facts. A reference whose identity the module no longer holds is refused, and
    /// a life replaced while its native state was being read yields no snapshot at all.</summary>
    internal RuntimeEntitySnapshot? Observe(EntityReference reference)
    {
        CheckThread();
        if (!CanObserve) return null;
        var entry = Resolve(reference);
        if (entry == null) return null;
        var agent = entry.Agent;
        return agent == null || agent.Pointer != entry.AgentPointer
            ? null : PlayerObservation.Read(agent, entry.Player, entry.Reference);
    }

    /// <summary>The recorded lives that still resolve right now, in first-recorded order. The caller is the Map
    /// player candidate source, which the kernel reads under its query budget: this enumeration reads the
    /// module's own registry and resolves each entry as it lists it, and that one read is what a selector's
    /// `query` step spends. The order it answers in is the registry's, so the candidate source decides the
    /// order the kind publishes.</summary>
    internal IReadOnlyList<EntityReference> CurrentPlayers()
    {
        CheckThread();
        if (!CanObserve) return Array.Empty<EntityReference>();
        var current = new List<EntityReference>(_players.Count);
        foreach (var entry in _players.Values)
            if (Resolve(entry.Reference) is { } live) current.Add(live.Reference);
        return current;
    }

    /// <summary>SDK instance lookup: an <see cref="SNet_Player"/> maps to its recorded current life, anything else to null.
    /// It never allocates, so a player seen before the spawn readback has no reference yet rather than a guessed one.</summary>
    internal EntityReference? ResolveInstance(object instance)
    {
        CheckThread();
        if (instance is not SNet_Player player || player == null || !CanObserve) return null;
        var pointer = player.Pointer;
        foreach (var entry in _players.Values)
            if (entry.PlayerPointer == pointer) return Resolve(entry.Reference)?.Reference;
        return null;
    }

    private static bool Linked(SNet_Player player, PlayerAgent agent)
    {
        var linked = player.PlayerAgent?.TryCast<PlayerAgent>();
        return linked != null && linked.Pointer == agent.Pointer;
    }

    private static string Describe(EntityReference reference)
        => "id=" + reference.Id + " world=" + reference.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + " life=" + reference.LifeEpoch.ToString(CultureInfo.InvariantCulture);
}
