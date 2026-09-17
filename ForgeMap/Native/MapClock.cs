using System;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>
/// The one place this package hangs on the kernel's own clock.
///
/// The runtime advances one kernel per simulation step, and it raises <c>TickAdvanced</c> exactly once per advance
/// after that advance's dispatch. A package that instead ticks off the local player's own <c>FixedUpdate</c> pays
/// one tick per player standing on the machine and needs a dedupe to survive it; observing the kernel's tick is the
/// same step the game measured, once, on every machine, with no patch of this package's own.
///
/// A registration costs somebody a callback every frame, so it is taken only while this package has work and given
/// back the moment it does not: an in-flight light-colour fade, or a trigger zone the running level placed with a
/// subscriber on its binding. With neither, nothing is registered and a frame costs nothing at all.
///
/// The one thing a callback must not do is take or release a subscription — both are kernel mutations, and the
/// kernel refuses them from inside a lifecycle notification. So work that appears does so outside this clock (a
/// command handler scheduling a fade, a player reconcile after a level loaded) and calls <see cref="Wake"/>; the
/// clock only ever gives its own registration back from inside the callback, which is the one mutation the kernel
/// does allow there.
/// </summary>
internal sealed class MapClock : IDisposable
{
    /// <summary>
    /// The period a trigger zone is judged at, taken from the game's own collision trigger:
    /// `LG_CollisionWorldEventTrigger` carries `public const float COLLISION_CHECK_INTERVAL = 0.1` and re-tests its
    /// volume from its `Update` on that timer (`m_collisionCheckTimer` at 0x38, `m_playerWasInLastCheck` at 0x3C;
    /// dump.cs of build 20403457, SHA-256 `BF657C0E…DE1CC`, lines 697075-697094). A zone of this package asks the
    /// same kind of question — "is anything inside this volume now" — so it is judged at that cadence instead of
    /// per frame. Judging per fixed frame would spend the host's query budget five to ten times more often for an
    /// answer the game's own trigger only changes at this rate. The game's check is point-in-volume; the travelled
    /// segment this package adds is its own, because the native trigger cannot see a body that crossed a thin
    /// volume between two of its checks either (`TriggerZone.IntersectsSegment`).
    ///
    /// One beat is also what a placement is measured against: `TriggerZoneModule.MaximumJudgedTravel` is this
    /// period at the quickest speed the game carries a body at, and the two are the same fact — this file is
    /// compiled into the clock's own test project without the module beside it, so they are kept in step by this
    /// note rather than by a shared expression. One beat is 0.1 second and the bound is 3 metres.
    /// </summary>
    internal const float TriggerZoneSeconds = 0.1f;

    private readonly RuntimeModuleHandle _registration;
    private readonly Action _refresh;
    private readonly Func<bool> _zonesPending;
    private readonly Action _zones;
    private readonly Func<bool> _fadesPending;
    private readonly Action<float> _fades;
    private readonly Func<float> _seconds;
    private RuntimeLifecycleSubscription? _subscription;
    private float _sinceZones;
    private bool _disposed;

    /// <summary>One clock for one session. <paramref name="refresh"/> is the level-facing half of the zone work —
    /// placing a new level's zones and keeping its blocking bodies in step — and runs when work is announced rather
    /// than from inside the callback, because placement is a native side effect that belongs to a native stage.
    /// <paramref name="seconds"/> is the step the game measured, injected so the cadence is testable without a
    /// Unity frame.</summary>
    internal MapClock(RuntimeModuleHandle registration, Action refresh, Func<bool> zonesPending, Action zones,
        Func<bool> fadesPending, Action<float> fades, Func<float> seconds)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _zonesPending = zonesPending ?? throw new ArgumentNullException(nameof(zonesPending));
        _zones = zones ?? throw new ArgumentNullException(nameof(zones));
        _fadesPending = fadesPending ?? throw new ArgumentNullException(nameof(fadesPending));
        _fades = fades ?? throw new ArgumentNullException(nameof(fades));
        _seconds = seconds ?? throw new ArgumentNullException(nameof(seconds));
    }

    /// <summary>How many times this session took the clock. A test reads it to see that a session with no work
    /// took it zero times, and that finishing its last work gave it back.</summary>
    internal long Registrations { get; private set; }

    /// <summary>Whether the clock is taken right now.</summary>
    internal bool Registered => _subscription != null;

    /// <summary>Announces that work may have appeared: a fade was scheduled, a level's zones were placed, a player
    /// reconciled. Never called from inside a clock or lifecycle callback.</summary>
    internal void Wake()
    {
        if (_disposed) return;
        _refresh();
        if (_subscription == null && (_zonesPending() || _fadesPending()))
        {
            _subscription = _registration.ObserveLifecycle(OnTick, replayCurrent: false);
            Registrations++;
        }
    }

    private void OnTick(RuntimeLifecycleEvent value)
    {
        if (value.Kind != RuntimeLifecycleKind.TickAdvanced) return;
        var seconds = _seconds();
        if (_fadesPending()) _fades(seconds);
        // The cadence accumulates the game's own step, so a machine whose frame rate differs from the host's still
        // judges the same number of times per second of play. The remainder of a beat is kept rather than dropped,
        // so the beat does not drift. A step that fell more than a beat behind is judged once and the excess is
        // dropped: a long hitch must not turn into a burst of judgments on the frames that follow it.
        _sinceZones += seconds;
        if (_sinceZones >= TriggerZoneSeconds)
        {
            _sinceZones -= TriggerZoneSeconds;
            if (_sinceZones >= TriggerZoneSeconds) _sinceZones = 0f;
            if (_zonesPending()) _zones();
        }
        if (!_zonesPending() && !_fadesPending()) Release();
    }

    private void Release()
    {
        var subscription = _subscription;
        _subscription = null;
        subscription?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}
