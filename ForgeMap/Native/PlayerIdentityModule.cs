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
internal sealed class PlayerIdentityModule : IDisposable
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

    internal PlayerIdentityModule(RuntimeKernel kernel, Func<bool> canObserve, Action<string> log, Action<string> warn)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canObserve = canObserve ?? throw new ArgumentNullException(nameof(canObserve));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
        // The one Map provider identity. The resolver is its only runtime surface: no capability, binding or
        // observer, because a snapshot would need the alive/downed/dead mapping that MAP5 has not verified.
        _registration = kernel.RegisterModule(ModuleDefinition.Create() with
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [EntityKind] = IsCurrent }
        });
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
        catch { _registration.Dispose(); throw; }
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
        _registration.Dispose(); _lifecycle.Dispose(); ClearWorld(); _disposed = true;
    }

    /// <summary>Reads the in-level agent list after a native spawn or despawn body and reconciles lives.
    /// A changed agent or player pointer for a player key is a new life under the same per-world number.</summary>
    internal void Reconcile()
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

    private static bool Linked(SNet_Player player, PlayerAgent agent)
    {
        var linked = player.PlayerAgent?.TryCast<PlayerAgent>();
        return linked != null && linked.Pointer == agent.Pointer;
    }

    private static string Describe(EntityReference reference)
        => "id=" + reference.Id + " world=" + reference.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + " life=" + reference.LifeEpoch.ToString(CultureInfo.InvariantCulture);
}
