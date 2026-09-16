using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The wave facts of build 20403457. Every one of them is a Forge derivation from native state, because
/// the game has no wave started/exhausted/cleared event (see
/// <c>ForgeEnemy/evidence/enemy-wave-facts.json</c> and <c>ForgeMap/evidence/encounter-wave-hooks.json</c>):
///
///   * a wave is identified by the EventID its own <c>Mastermind.MastermindEvent</c> base carries, and that
///     identity is written into the event frame's scope id (<c>gtfo.wave:&lt;worldEpoch&gt;:&lt;EventID&gt;</c>),
///     the same way an attack instance is the scope of its own facts;
///   * `wave_started` is the wave's own spawn callback, read after the native body so the instance identity is
///     the one the game assigned;
///   * `wave_spawned` is one <c>SurvivalWave.SpawnGroup</c> call: the members of every group that call
///     registered belong to that one batch, and `count` is how many of them this provider can name;
///   * `wave_exhausted` is <c>SurvivalWave.TryEndEvent</c> answering true — the wave's own "nothing left to
///     spend" test — and `count` is the groups that wave registered in its life;
///   * `wave_cleared` is a wave's last group leaving <c>Mastermind.m_activeGroups</c>, read after the
///     Mastermind's own group maintenance, which is the native answer to "is anything of this wave still alive".
///
/// A teardown is not a clear: <c>SurvivalWave.OnDespawn</c> fires for level cleanup and event cancellation too,
/// so it only retires this provider's own bookkeeping and publishes nothing. Nothing here writes the world, and
/// no fact is emitted for a wave this provider has not observed spawn.
///
/// The `wave` output port of every catalog row is declared optional and is never published: this runtime mints no
/// handle for a provider, so the instance identity travels in the frame's scope id instead of a handle port. See
/// <c>EnemyWaveContract</c> and the evidence file.</summary>
internal sealed class EnemyWaveFacts
{
    /// <summary>Waves one world may have tracked at once. A wave leaves the table when its own teardown is
    /// observed, so the bound is the number of live waves plus those still alive in the Mastermind's group list;
    /// the budget is refused rather than guessed at.</summary>
    internal const int MaximumTrackedWaves = 256;

    /// <summary>The scope prefix a wave's own facts carry. The identity is the native EventID, never a
    /// Forge-invented number.</summary>
    internal const string WaveScopePrefix = "gtfo.wave:";

    private readonly RuntimeKernel _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly Func<bool> _canObserve;
    private readonly Action<string> _report;
    private readonly Func<EnemyAgent?, EntityReference?> _referenceOf;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    /// <summary>The state one observed wave has in this world.</summary>
    private sealed class WaveState
    {
        internal WaveState(IntPtr pointer) => Pointer = pointer;
        /// <summary>The native instance this state was opened for. Re-checked on every touch, so a recycled
        /// wrapper or a second wave on the same EventID is never read as this one.</summary>
        internal IntPtr Pointer { get; }
        /// <summary>The groups this wave registered and that have not left the Mastermind's list yet. Members are
        /// native pointers: the interop layer hands out a new managed wrapper for the same native object on every
        /// call, so identity is the pointer the whole file compares.</summary>
        internal readonly HashSet<IntPtr> LiveGroups = new();
        /// <summary>Groups this wave registered over its whole life; `wave_exhausted.count`.</summary>
        internal int Groups;
        internal bool Exhausted;
        internal bool Cleared;
    }

    /// <summary>One open <c>SurvivalWave.SpawnGroup</c> call: the batch boundary the game itself does not have,
    /// defined as the groups that call registers. It is opened by the call's prefix and closed by its postfix, so
    /// a group registered from anywhere else is not part of a batch.</summary>
    private sealed class WaveBatch
    {
        internal WaveBatch(ushort eventId, IntPtr wave) { EventId = eventId; Wave = wave; }
        internal ushort EventId { get; }
        internal IntPtr Wave { get; }
        /// <summary>The distinct groups `SurvivalWave.RegisterGroup` delivered inside the call.</summary>
        internal readonly List<EnemyGroup> Delivered = new();
        internal readonly HashSet<IntPtr> DeliveredPointers = new();
    }

    /// <summary>The token the spawn prefix hands to the spawn postfix. It carries the native instance and nothing
    /// else: the EventID is read from the wave after its own spawn body ran, because that body is what registers
    /// the event with the Mastermind.</summary>
    internal sealed class WaveSpawnObservation
    {
        private readonly EnemyWaveFacts _owner;
        private bool _consumed;
        internal IntPtr Wave { get; }
        internal WaveSpawnObservation(EnemyWaveFacts owner, IntPtr wave) { _owner = owner; Wave = wave; }
        internal bool TryConsume(EnemyWaveFacts owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true; return true;
        }
    }

    private readonly Dictionary<ushort, WaveState> _waves = new();
    private long _waveEpoch = long.MinValue;
    private WaveBatch? _batch;
    private long _eventSequence;

    internal EnemyWaveFacts(RuntimeKernel kernel, RuntimeModuleHandle registration, Func<bool> canObserve,
        Action<string> report, Func<EnemyAgent?, EntityReference?> referenceOf)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _canObserve = canObserve ?? throw new ArgumentNullException(nameof(canObserve));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _referenceOf = referenceOf ?? throw new ArgumentNullException(nameof(referenceOf));
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Enemy wave facts require the owning simulation thread.");
    }

    /// <summary>The five bindings this family publishes through, as one set: the wave table is only kept while a
    /// plan subscribes to at least one of them, and a native hook that no plan feeds reads nothing.</summary>
    internal bool CanPublish
    {
        get
        {
            if (!_canObserve()) return false;
            foreach (var binding in EnemyWaveContract.Bindings) if (_kernel.HasSubscribers(binding)) return true;
            return false;
        }
    }

    /// <summary>Drops every wave of an earlier world. A new world invalidates the EventID space itself, so the
    /// table is emptied on first touch rather than trusted across the boundary — the same discipline an entity
    /// reference's world epoch gets.</summary>
    private void EnsureWaveEpoch()
    {
        if (_waveEpoch == _kernel.WorldEpoch) return;
        _waves.Clear();
        _batch = null;
        _waveEpoch = _kernel.WorldEpoch;
    }

    /// <summary>The wave a hook is about, or null when this provider never saw it spawn, when the EventID names a
    /// different native instance, or when the world moved on.</summary>
    private WaveState? TrackedWave(SurvivalWave? wave)
    {
        EnsureWaveEpoch();
        if (wave == null) return null;
        if (!_waves.TryGetValue(wave.EventID, out var state) || state.Pointer != wave.Pointer) return null;
        return state;
    }

    internal WaveSpawnObservation? BeforeWaveSpawn(SurvivalWave? wave)
    {
        CheckThread();
        if (!CanPublish || wave == null) return null;
        EnsureWaveEpoch();
        // The same instance is only ever observed once per world. The EventID is not readable yet at this point —
        // the wave's own spawn body is what registers the event — so the check is on the native instance, and a
        // second spawn callback for a wave already in the table is dropped instead of opened twice.
        foreach (var tracked in _waves.Values) if (tracked.Pointer == wave.Pointer) return null;
        if (_waves.Count >= MaximumTrackedWaves) { _report("wave table is full; no wave facts for this instance"); return null; }
        return new WaveSpawnObservation(this, wave.Pointer);
    }

    /// <summary>The native spawn callback ran. The wave's own EventID is the identity every later fact uses; a
    /// zero id means the game has not assigned one, so no fact is published rather than one keyed by a guess.</summary>
    internal void AfterWaveSpawn(SurvivalWave? wave, WaveSpawnObservation? before)
    {
        CheckThread();
        if (before == null || !before.TryConsume(this) || !CanPublish || wave == null
            || wave.Pointer != before.Wave) return;
        ushort eventId = wave.EventID;
        if (eventId == 0) { _report("wave spawned without an EventID; no wave facts for this instance"); return; }
        EnsureWaveEpoch();
        if (_waves.ContainsKey(eventId)) return;
        _waves[eventId] = new WaveState(wave.Pointer);
        PublishWave(EnemyWaveContract.WaveStartedBinding, eventId, RuntimeJson.EmptyObject);
    }

    /// <summary>A `SurvivalWave.SpawnGroup` call is starting: the batch boundary. A call nested inside another one
    /// does not open a second batch, so the outer call's own accounting stays the one that is published.</summary>
    internal void BeforeWaveGroupStep(SurvivalWave? wave)
    {
        CheckThread();
        if (_batch != null || !CanPublish) return;
        var state = TrackedWave(wave);
        if (state == null) return;
        _batch = new WaveBatch(wave!.EventID, state.Pointer);
    }

    /// <summary>`SurvivalWave.RegisterGroup` returned: the wave produced a group. The group joins the wave's live
    /// set and — inside a batch — the batch's delivered set, counted once per native instance.</summary>
    internal void AfterWaveGroup(SurvivalWave? wave, EnemyGroup? group)
    {
        CheckThread();
        if (group == null) return;
        var state = TrackedWave(wave);
        if (state == null) return;
        state.Groups++;
        state.LiveGroups.Add(group.Pointer);
        if (_batch == null || _batch.Wave != state.Pointer) return;
        if (_batch.DeliveredPointers.Add(group.Pointer)) _batch.Delivered.Add(group);
    }

    /// <summary>`SurvivalWave.SpawnGroup` returned: the batch is closed. The members of the groups it delivered
    /// are the batch, and the batch's payments are compared with its deliveries.</summary>
    internal void AfterWaveGroupStep(SurvivalWave? wave)
    {
        CheckThread();
        var batch = _batch;
        if (batch == null || wave == null || wave.Pointer != batch.Wave) return;
        _batch = null;
        if (!CanPublish || !_waves.TryGetValue(batch.EventId, out var state) || state.Pointer != batch.Wave) return;
        var members = HarvestBatchMembers(batch);
        // The frame is built from references this provider still owns: an enemy that despawned while the batch was
        // being harvested is dropped, never published as a name that resolves to nothing.
        PublishWave(EnemyWaveContract.WaveSpawnedBinding, batch.EventId,
            RuntimeJson.From(new { spawned = members, count = members.Length }));
    }

    /// <summary>Every member of every group the batch delivered, as the references this module already owns.
    /// `EnemyGroup.Members` is the native member list; a member this provider has no live life for is left out
    /// instead of being named by an id that would not resolve.</summary>
    private EntityReference[] HarvestBatchMembers(WaveBatch batch)
    {
        var members = new List<EntityReference>();
        foreach (var group in batch.Delivered)
        {
            var agents = group.Members;
            if (agents == null) continue;
            int count = agents.Count;
            for (int index = 0; index < count; index++)
            {
                var reference = _referenceOf(agents[index]);
                if (reference != null && !members.Contains(reference)) members.Add(reference);
            }
        }
        return members.ToArray();
    }

    /// <summary>`SurvivalWave.TryEndEvent` returned: true is the wave's own "nothing left to spend" answer. The
    /// count is what this provider watched the wave register, which is the only group number the game exposes.</summary>
    internal void AfterWaveEndTest(SurvivalWave? wave, bool ended)
    {
        CheckThread();
        if (!ended) return;
        var state = TrackedWave(wave);
        if (state == null || state.Exhausted) return;
        state.Exhausted = true;
        PublishWave(EnemyWaveContract.WaveExhaustedBinding, wave!.EventID,
            RuntimeJson.From(new { count = state.Groups }));
    }

    /// <summary>The Mastermind finished its own group maintenance: the post-maintenance group list is the native
    /// answer to what is still alive. A wave whose groups have all left that list is cleared; a wave that never
    /// registered a group is not, and neither is one whose own teardown was already observed.</summary>
    internal void AfterGroupMaintenance()
    {
        CheckThread();
        if (_waves.Count == 0 || !CanPublish) return;
        EnsureWaveEpoch();
        var mastermind = Mastermind.Current;
        if (mastermind == null) return;
        var active = mastermind.m_activeGroups;
        if (active == null) return;
        var alive = new HashSet<IntPtr>();
        int count = active.Count;
        for (int index = 0; index < count; index++)
        {
            var group = active[index];
            if (group != null) alive.Add(group.Pointer);
        }
        foreach (var pair in _waves)
        {
            var state = pair.Value;
            if (state.Cleared || state.LiveGroups.Count == 0) continue;
            bool anyAlive = false;
            foreach (var pointer in state.LiveGroups) if (alive.Contains(pointer)) { anyAlive = true; break; }
            if (anyAlive) continue;
            state.Cleared = true;
            PublishWave(EnemyWaveContract.WaveClearedBinding, pair.Key, RuntimeJson.EmptyObject);
        }
    }

    /// <summary>The wave is being torn down. A teardown is not a clear — level cleanup and cancellation take the
    /// same path — so this only retires the bookkeeping: the wave leaves the table and no fact is published.</summary>
    internal void BeforeWaveDespawn(SurvivalWave? wave)
    {
        CheckThread();
        if (wave == null || _waves.Count == 0) return;
        EnsureWaveEpoch();
        if (_batch != null && _batch.Wave == wave.Pointer) _batch = null;
        if (_waves.TryGetValue(wave.EventID, out var state) && state.Pointer == wave.Pointer) _waves.Remove(wave.EventID);
    }

    /// <summary>Publishes one wave fact. The frame's scope is the wave instance itself, which is where a wave's
    /// runtime identity travels; the `wave` handle port stays absent because this runtime mints no provider
    /// handle, and an absent port is the honest form of a value that does not exist.</summary>
    private void PublishWave(string binding, ushort eventId, JsonElement outputs)
    {
        if (!CanPublish) return;
        var result = _registration.Publish(new RuntimeEvent(
            "gtfo.enemy.wave:" + _kernel.WorldEpoch + ":" + (++_eventSequence), binding,
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick),
            WaveScopePrefix + _kernel.WorldEpoch + ":" + eventId.ToString(CultureInfo.InvariantCulture), outputs));
        // Runtime owns causal propagation and dispatch. Rejection must never reopen this native observation.
        if (result.Status == "rejected") _report(binding + " rejected: " + result.Code);
    }
}
