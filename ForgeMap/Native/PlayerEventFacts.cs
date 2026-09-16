using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;
using Player;

namespace ForgeMap.Native;

/// <summary>The publication side of the player's own events: a supply use, a pickup and a ping. Each one is one
/// game event about the player who did it, so each row publishes one fact about one life, under the same rules the
/// player-life and player-state halves publish under (host only, one transition per event id, a required port that
/// cannot be read publishes nothing).
///
/// Two properties of these three rows are worth stating where the code is:
/// <list type="bullet">
/// <item>All three are observed from the game's own event funnel, which every machine runs for the events it
/// produced. Publishing is still host-only, so a client's own supply use or pickup is dropped on that client and
/// published by the host only if the host can observe it. Whether the host observes a client's pickup and supply
/// use is an open question this build's metadata cannot answer; it is recorded in
/// `evidence/player-event-facts.json` and in the report rather than answered by a derived host fact.</item>
/// <item>A ping is the one row of the three that the host genuinely cannot derive: it is an input. It is observed
/// from two entries — the game's own `player_ping` event and the marker call the game makes with the pinging agent
/// and the world position — and the two are de-duplicated per life, per tick and per position, so a machine that
/// sees both publishes one fact. If a two-machine check shows a client's ping never reaches the host through
/// either entry, the row is where the missing host channel is visible, not a place to publish a client-side
/// fact.</item>
/// </list></summary>
internal interface IPlayerEventWorld
{
    /// <summary>One supply use: the player and the catalog's `supply_kind` member.</summary>
    bool SupplyUsed(object? agent, string kind);

    /// <summary>One pickup: the player and the catalog's `pickup_kind` member.</summary>
    bool ItemPickedUp(object? agent, string kind);

    /// <summary>One ping: the player, the position pinged, and the native object that was pinged when the entry
    /// carried one.</summary>
    bool Ping(object? agent, double[]? position, object? target);

    IReadOnlyList<string> Journal { get; }

    void BeginWorld();
}

internal sealed class PlayerEventFacts : IPlayerEventWorld
{
    private const string Prefix = PlayerIdentityModule.EntityKind + ":";
    private const string WorldScope = "gtfo.world:";

    /// <summary>The distance under which two pings of one life in one tick are the same ping. The two entries the
    /// game reaches for one ping carry the same world position bit for bit, so this only guards against a
    /// re-encoded position; it is not a smoothing radius.</summary>
    internal const double PingDeduplicationRadius = 0.001;

    private sealed class LifeRecord
    {
        internal readonly Dictionary<string, long> Transitions = new(StringComparer.Ordinal);
        internal long PingTick = -1;
        internal double[]? PingPosition;
    }

    private readonly RuntimeModuleHandle _registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;
    private readonly RuntimeKernel _kernel;
    private readonly IPlayerVitalsSource _source;
    private readonly Action<string> _log;
    private readonly Action<string> _report;
    private readonly Dictionary<string, LifeRecord> _lives = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly List<string> _journal = new();
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;
    private long _publishedFacts;

    internal static PlayerEventFacts? Installed { get; private set; }

    /// <summary>The binding a fact publishes through, or null for the shipped one. The focused tests register
    /// their own binding ids on the same contract table — a module may only register its own bindings — so the
    /// mapping is a seam rather than a literal in the publish path.</summary>
    internal Func<string, string>? BindingOverride { get; init; }

    internal PlayerEventFacts(RuntimeModuleHandle registration, RuntimeKernel kernel, IPlayerVitalsSource source,
        Action<string> log, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    internal static PlayerEventFacts Attach(RuntimeModuleHandle registration, RuntimeKernel kernel,
        IPlayerVitalsSource source, Action<string> log, Action<string> report, Func<string, string>? bindingOverride = null)
    {
        var facts = new PlayerEventFacts(registration, kernel, source, log, report) { BindingOverride = bindingOverride };
        Installed = facts;
        return facts;
    }

    internal static void Detach(PlayerEventFacts facts)
    {
        if (ReferenceEquals(Installed, facts)) Installed = null;
    }

    internal long PublishedFacts => _publishedFacts;

    public IReadOnlyList<string> Journal => _journal;

    public bool SupplyUsed(object? agent, string kind)
    {
        CheckThread();
        if (!Ready()) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        string member;
        try { member = PlayerEventContract.SupplyKind(kind); }
        catch (RuntimeContractException error) { ReportOnce("supply:" + kind, error.Message); return false; }
        return Publish(life, "supply_used", PlayerEventContract.SupplyUsedPayload(life, member));
    }

    public bool ItemPickedUp(object? agent, string kind)
    {
        CheckThread();
        if (!Ready()) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        string member;
        try { member = PlayerEventContract.PickupKind(kind); }
        catch (RuntimeContractException error) { ReportOnce("pickup:" + kind, error.Message); return false; }
        return Publish(life, "item_picked_up", PlayerEventContract.ItemPickedUpPayload(life, member));
    }

    /// <summary>One ping. The position is required, so a ping whose position could not be read publishes nothing;
    /// the target port is left out because no entry this build can read names the pinged object as an entity, which
    /// is the framework's own object-to-entity gap and not a claim that the ping had no target.</summary>
    public bool Ping(object? agent, double[]? position, object? target)
    {
        CheckThread();
        if (!Ready()) return false;
        if (position == null || position.Length != 3 || !Finite(position)) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        if (IsRepeat(life, position)) return false;
        return Publish(life, "ping", PlayerEventContract.PingPayload(life, position, null));
    }

    private bool IsRepeat(EntityReference life, double[] position)
    {
        var record = Record(life);
        long tick = Math.Max(0, _kernel.CurrentTick);
        bool repeat = record.PingTick == tick && record.PingPosition is { } prior
            && Distance(prior, position) <= PingDeduplicationRadius;
        record.PingTick = tick;
        record.PingPosition = position;
        return repeat;
    }

    private static bool Finite(double[] position)
        => double.IsFinite(position[0]) && double.IsFinite(position[1]) && double.IsFinite(position[2]);

    private static double Distance(double[] left, double[] right)
    {
        double dx = left[0] - right[0], dy = left[1] - right[1], dz = left[2] - right[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private bool Ready() => !_disposed && _registration.IsRegistered && _source.Authoritative;

    private bool ReferenceOf(object? instance, out EntityReference reference)
    {
        reference = default;
        if (instance == null) return false;
        _source.Reconcile();
        if (_source.ReferenceOf(instance) is not { } found) return false;
        if (_source.Read(found) == null) return false;
        reference = found;
        return true;
    }

    private LifeRecord Record(EntityReference reference)
    {
        if (!_lives.TryGetValue(reference.Id, out var record))
        {
            record = new LifeRecord();
            _lives[reference.Id] = record;
        }
        return record;
    }

    private bool Publish(EntityReference reference, string fact, JsonElement outputs)
    {
        var record = Record(reference);
        record.Transitions.TryGetValue(fact, out long transition);
        record.Transitions[fact] = transition + 1;
        string eventId = Prefix + reference.Id + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + transition.ToString(CultureInfo.InvariantCulture);
        string binding = BindingOverride?.Invoke(fact) ?? PlayerEventContract.BindingOf(fact);
        // Nothing is listening on this fact's binding: the kernel would answer `no-consumer` for the event this
        // call is about to build, so the event value is never built. The transition above is still claimed,
        // because a report seen while nobody listened is one this half has already made.
        if (_gates.TryGetValue(binding, out var gate) && !gate.HasSubscribers) return false;
        var answer = _registration.Publish(new RuntimeEvent(eventId, binding,
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick),
            WorldScope + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture), outputs));
        if (answer.Status == "queued")
        {
            _publishedFacts++;
            Record("player." + fact + " id=" + reference.Id + " transition=" + transition.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        if (answer.Status == "rejected")
            ReportOnce("publish:" + fact + ":" + answer.Code, "player " + fact + " fact rejected: " + answer.Code);
        return false;
    }

    private void Record(string line)
    {
        _journal.Add(line);
        _log("map." + line);
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) { _journal.Add(message); _report("Map player event observation: " + message); }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player event observation requires the runtime's own simulation thread.");
    }

    public void BeginWorld()
    {
        CheckThread();
        _lives.Clear();
        _reported.Clear();
    }

    public void Dispose()
    {
        CheckThread();
        Detach(this);
        _disposed = true;
        _lives.Clear();
    }
}

/// <summary>The seam the funnel hook runs through, with one typed entry per row so a hook body names the fact it
/// publishes rather than shaping a payload. Every entry runs on the session that owns the registration the fact
/// is published through: a session that failed before it created the half runs none of them, and a callback that
/// fails disables the session exactly as the other readbacks do.</summary>
internal static class PlayerEventHooks
{
    internal static void LowHealth(PlayerStateFacts? state, PlayerAgent player)
    {
        if (state == null) return;
        _ = state.LowHealth(player);
    }

    internal static void SupplyUsed(PlayerEventFacts? events, PlayerAgent player, string kind)
    {
        if (events == null) return;
        _ = events.SupplyUsed(player, kind);
    }

    internal static void ItemPickedUp(PlayerEventFacts? events, PlayerAgent player, string kind)
    {
        if (events == null) return;
        _ = events.ItemPickedUp(player, kind);
    }

    internal static void Ping(PlayerEventFacts? events, PlayerAgent player, double[]? position, object? target)
    {
        if (events == null) return;
        _ = events.Ping(player, position, target);
    }
}
