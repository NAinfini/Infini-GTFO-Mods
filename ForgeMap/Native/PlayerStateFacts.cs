using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>One read of one player's vital state, as the native receiver reports it right now. A value: the caller
/// never keeps it across a native call.</summary>
internal readonly record struct PlayerVitals(EntityReference Reference, float Health, float Maximum, float Infection, bool Alive);

/// <summary>What the player-state observation needs from the world it observes. The native implementation reads
/// the player identity half and the receiver behind each life; a test supplies its own. Every method is answered
/// on the owning simulation thread, and a reference the identity no longer holds answers null instead of a
/// guess.</summary>
internal interface IPlayerVitalsSource
{
    /// <summary>Whether this process may read and publish right now: the registration is live, the runtime is
    /// ready and this peer is the authority.</summary>
    bool Authoritative { get; }

    /// <summary>Records the lives the level currently holds, before a life-changing callback reads one.</summary>
    void Reconcile();

    /// <summary>The recorded player life behind a native instance, or null when this process tracks no such life.
    /// Both a `PlayerAgent` and its damage receiver are accepted, because the damage hooks are handed the
    /// receiver and the game-event hooks are handed the agent.</summary>
    EntityReference? ReferenceOf(object? instance);

    /// <summary>One read of a recorded life right now, or null when the identity no longer holds it or its
    /// receiver cannot be read.</summary>
    PlayerVitals? Read(EntityReference reference);
}

/// <summary>The player-state observation half of the Map provider: the damage application, the low-health event
/// and the infection write, each published as one fact about one player life.
///
/// The rules this half publishes under are the ones the player-life half already follows, because two families of
/// facts about the same lives must not disagree about what a fact is:
/// <list type="bullet">
/// <item>Only the host publishes, and only while the runtime is ready. A read on a machine that may not publish
/// is refused and reported once per reason.</item>
/// <item>One native transition is one event id: the fact, the world epoch, the life and a per-life transition
/// number claimed before the publish, so a callback that runs twice for one transition costs nothing and can
/// never reuse an id the kernel has already seen.</item>
/// <item>A fact whose required port cannot be read is not published. `player`/`target` is required on every row
/// here; a port the native side genuinely answered with nobody is written as JSON null, and a port this half
/// cannot observe is left out of the payload entirely.</item>
/// </list>
///
/// The damage row is the canonical `forge.trigger.combat.damage_applied`, so this half reads its ports from
/// `PlayerStateContract` and reports the attacker through whichever domain can name it: a player attacker through
/// this package's own identity, an enemy attacker through the kind that owns enemies. `friendly_fire` is a
/// derivation and is written as one in the evidence: the game posts a friendly-fire event of its own, and the
/// attacker's own domain is what this half can read, so the port is true for a player attacker, false for an
/// attacker another domain named, and absent when no domain can name the attacker at all.</summary>
internal interface IPlayerStateWorld
{
    /// <summary>One native damage application the receiver accepted.</summary>
    bool DamageApplied(object? damageBase, float amount, object? sourceAgent, int? kind);

    /// <summary>One native low-health event about one player.</summary>
    bool LowHealth(object? agent);

    /// <summary>One native infection write, with the value the receiver held when it was entered.</summary>
    bool InfectionChanged(object? damageBase, float before);

    /// <summary>The publication log of this half.</summary>
    IReadOnlyList<string> Journal { get; }

    /// <summary>Drops this half's per-world tables.</summary>
    void BeginWorld();
}

internal sealed class PlayerStateFacts : IPlayerStateWorld
{
    /// <summary>The entity kind the enemy package owns. Named here rather than referenced, because a domain
    /// package does not take a build dependency on its siblings.</summary>
    private const string EnemyKind = "gtfo.enemy";

    private const string Prefix = PlayerIdentityModule.EntityKind + ":";
    private const string WorldScope = "gtfo.world:";

    /// <summary>What this half already decided about one life in one world. One life is one subject, and a world
    /// change clears the table.</summary>
    private sealed class LifeRecord
    {
        /// <summary>Transition numbers, one per fact, so a repeated native report of one transition is the
        /// kernel's own `duplicate` instead of a second event.</summary>
        internal readonly Dictionary<string, long> Transitions = new(StringComparer.Ordinal);
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

    /// <summary>The one attached half. A hook runs through <see cref="PlayerStateHooks"/>, which reads this
    /// late, so a session that failed before the half existed simply publishes nothing.</summary>
    internal static PlayerStateFacts? Installed { get; private set; }

    /// <summary>The binding a fact publishes through, or null for the shipped one. The focused tests register
    /// their own binding ids on the same contract table — a module may only register its own bindings — so the
    /// mapping is a seam rather than a literal in the publish path.</summary>
    internal Func<string, string>? BindingOverride { get; init; }

    internal PlayerStateFacts(RuntimeModuleHandle registration, RuntimeKernel kernel, IPlayerVitalsSource source,
        Action<string> log, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Publishes through the Map registration the session already owns and becomes the half every hook
    /// reads. Called once per session, after that registration exists. The binding mapping is a parameter rather
    /// than a literal because a focused test registers its own binding ids on the same contract table.</summary>
    internal static PlayerStateFacts Attach(RuntimeModuleHandle registration, RuntimeKernel kernel,
        IPlayerVitalsSource source, Action<string> log, Action<string> report, Func<string, string>? bindingOverride = null)
    {
        var facts = new PlayerStateFacts(registration, kernel, source, log, report) { BindingOverride = bindingOverride };
        Installed = facts;
        return facts;
    }

    internal static void Detach(PlayerStateFacts facts)
    {
        if (ReferenceEquals(Installed, facts)) Installed = null;
    }

    internal long PublishedFacts => _publishedFacts;

    public IReadOnlyList<string> Journal => _journal;

    // ---------------------------------------------------------------- transition entries

    /// <summary>One native damage application: the receiver accepted `amount` from `sourceAgent` through the
    /// receive entry `kind` names. The damage is only published when the receiver's own accept path answered
    /// `true`, which is what the hook passes in: a refusal (ignored damage, a god-mode target) is not an
    /// application.</summary>
    public bool DamageApplied(object? damageBase, float amount, object? sourceAgent, int? kind)
    {
        CheckThread();
        if (!Ready()) return false;
        if (!ReferenceOf(damageBase, out var life)) return false;
        if (!float.IsFinite(amount) || amount <= 0) return false;
        var player = _source.ReferenceOf(sourceAgent);
        var attacker = sourceAgent == null ? null
            : player ?? _kernel.ResolveEntityInstance(EnemyKind, sourceAgent);
        bool? friendly = sourceAgent == null ? null : player != null ? true : attacker != null ? false : null;
        var payload = PlayerStateContract.DamageAppliedPayload(life, attacker, amount, kind, friendly);
        return Publish(life, "damage_applied", payload,
            kind is { } named ? "kind=" + PlayerStateContract.DamageKindName(named) : "kind=unnamed");
    }

    /// <summary>One native low-health event. The game posts the event with the player it is about; this half only
    /// publishes it as a fact.</summary>
    public bool LowHealth(object? agent)
    {
        CheckThread();
        if (!Ready()) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        return Publish(life, "low_health", PlayerStateContract.LowHealthPayload(life));
    }

    /// <summary>One native infection write: `before` is the value the receiver held when the write was entered,
    /// so the delta is the difference the write really made and never an amount the caller asked for. A write that
    /// did not change the value publishes nothing.</summary>
    public bool InfectionChanged(object? damageBase, float before)
    {
        CheckThread();
        if (!Ready()) return false;
        if (!ReferenceOf(damageBase, out var life)) return false;
        if (_source.Read(life) is not { } vitals) return false;
        if (!float.IsFinite(before) || !float.IsFinite(vitals.Infection)) return false;
        double delta = vitals.Infection - before;
        if (delta == 0) return false;
        return Publish(life, "infection_changed",
            PlayerStateContract.InfectionChangedPayload(life, vitals.Infection, delta));
    }

    // ---------------------------------------------------------------- plumbing

    private bool Ready()
        => !_disposed && _registration.IsRegistered && _source.Authoritative;

    private bool ReferenceOf(object? instance, out EntityReference reference)
    {
        reference = default;
        if (instance == null) return false;
        _source.Reconcile();
        if (_source.ReferenceOf(instance) is not { } found) return false;
        // The identity's own reconcile is what creates a life, and a reference is only handed out for a life the
        // identity still resolves right now.
        if (_source.Read(found) == null) return false;
        reference = found;
        return true;
    }

    private bool Publish(EntityReference reference, string fact, JsonElement outputs, string? detail = null)
    {
        if (!_lives.TryGetValue(reference.Id, out var record))
        {
            record = new LifeRecord();
            _lives[reference.Id] = record;
        }
        record.Transitions.TryGetValue(fact, out long transition);
        // The transition number is claimed before the publish: a refused publish must not let the next report
        // reuse an id the kernel may already have seen.
        record.Transitions[fact] = transition + 1;
        string eventId = Prefix + reference.Id + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + transition.ToString(CultureInfo.InvariantCulture);
        string binding = BindingOverride?.Invoke(fact) ?? PlayerStateContract.BindingOf(fact);
        // Nothing is listening on this fact's binding: the kernel would answer `no-consumer` for the event this
        // call is about to build, so the event value is never built. The transition above is still claimed,
        // because a report seen while nobody listened is one this half has already made.
        if (_gates.TryGetValue(binding, out var gate) && !gate.HasSubscribers) return false;
        var answer = _registration.Publish(new RuntimeEvent(eventId, binding,
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), WorldScope + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture),
            outputs));
        if (answer.Status == "queued")
        {
            _publishedFacts++;
            Record("player." + fact + " id=" + reference.Id + " transition=" + transition.ToString(CultureInfo.InvariantCulture)
                + (detail == null ? "" : " " + detail));
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
        if (_reported.Add(key)) { _journal.Add(message); _report("Map player state observation: " + message); }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player state observation requires the runtime's own simulation thread.");
    }

    /// <summary>Drops this half's per-world tables. The kernel's world epoch is what invalidates the events
    /// already published; this releases the memory of a world that no longer exists. Idempotent.</summary>
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
