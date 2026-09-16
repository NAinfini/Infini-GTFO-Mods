using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeDevelopment.Native;

/// <summary>
/// The authoring-side session ledger the diagnostic rows write into. A session is one `request` handle with
/// `session` life, cast through the kernel's own handle pool, and everything the registered rows record —
/// traces, metric aggregates, the sampled/recorded/dropped counts and the assertion tally — lives here and
/// nowhere else: no kernel state is written, no fact is published and no world field is touched, which is what
/// the `presentation` tier means for these rows.
///
/// The handle is minted once per world and dropped with the world, so a record can never be read back as if it
/// belonged to the level that replaced the one it was taken in.
/// </summary>
internal sealed class DiagnosticSessions
{
    /// <summary>Records one session may write in a single tick. The budget is the session's own; the kernel's
    /// command budget is a different ceiling and is not consulted here.</summary>
    internal const int MaximumRecordsPerTick = 64;
    /// <summary>Entity snapshots one `inspect` may read in a single call.</summary>
    internal const int MaximumInspectedEntities = 32;
    /// <summary>Distinct metrics one session may aggregate; a name past the ceiling is refused rather than folded
    /// into an existing series.</summary>
    internal const int MaximumMetricsPerSession = 64;

    /// <summary>One metric's aggregate inside one session. A window is a run of ticks, and the aggregate covers
    /// every sample the session took inside the current run.</summary>
    internal sealed class MetricSeries
    {
        internal long Window = 1;
        internal long WindowStartedTick = -1;
        internal double Sum;
        internal double Minimum;
        internal double Maximum;
        internal double Last;
        internal long Samples;

        internal void Add(double value, long tick)
        {
            if (WindowStartedTick < 0) WindowStartedTick = tick;
            else if (Window > 0 && tick - WindowStartedTick >= Window) Reset(tick);
            if (Samples == 0) { Minimum = value; Maximum = value; }
            else { Minimum = Math.Min(Minimum, value); Maximum = Math.Max(Maximum, value); }
            Sum += value; Last = value;
            if (Samples < long.MaxValue) Samples++;
        }

        private void Reset(long tick)
        {
            WindowStartedTick = tick; Sum = 0; Minimum = 0; Maximum = 0; Last = 0; Samples = 0;
        }
    }

    /// <summary>One session's own record: the sample rate it was opened with, how much it had to drop, how many
    /// records it really wrote, and the metric series it aggregates.</summary>
    internal sealed class Session
    {
        internal int SampleRate = 1;
        internal long Dropped;
        internal long Records;
        internal long Seen;
        internal long BudgetTick = -1;
        internal int RecordsThisTick;
        internal long Assertions;
        internal readonly Dictionary<string, MetricSeries> Metrics = new(StringComparer.Ordinal);
    }

    private readonly RuntimeModuleHandle _registration;
    private readonly Func<long> _tick;
    private Session? _session;
    private JsonElement _handle;
    private bool _hasHandle;
    /// <summary>The one thing the session's handle stands for. A session names no game object, but the kernel's own
    /// handle check asks a handle for the object it holds, so the session registers itself as that object: the
    /// handle then answers `TryNative` exactly while its slot is live, which is the liveness question this ledger
    /// needs and the only one the SDK answers.</summary>
    private readonly object _token = new();

    internal DiagnosticSessions(RuntimeModuleHandle registration, Func<long> tick)
    {
        _registration = registration;
        _tick = tick;
    }

    /// <summary>The session's own handle, or a default value while no session is live. It is the value a plan wires
    /// into a row's `session` input, so it is handed back as the handle value itself and never as a string that a
    /// caller would have to re-encode.</summary>
    internal JsonElement Handle => _hasHandle ? _handle : default;

    internal bool HasSession => _session != null;

    /// <summary>
    /// Opens the one session this provider owns for the current world. The handle is cast through
    /// <see cref="RuntimeModuleHandle.CreateRequestHandle"/>, so it obeys the same pool, budget and staleness rules
    /// as every other handle in the runtime; a world that already has a session is the state the caller asked for
    /// and is answered without minting a second one. The session's kind is `request` because that is the kind the
    /// runtime's handle API casts — the catalog's older `subscription` spelling is a catalog-side reshape.
    /// </summary>
    internal bool TryBegin(int sampleRate, out JsonElement handle, out string code)
    {
        handle = Handle;
        if (_session != null) { code = "diagnostic-session-open"; return true; }
        try
        {
            _handle = _registration.CreateRequestHandle("session");
            _registration.RegisterNative(_handle, _token);
            _hasHandle = true;
            _session = new Session { SampleRate = Math.Max(sampleRate, 1) };
            handle = _handle;
            code = "diagnostic-session-started";
            return true;
        }
        catch (RuntimeContractException error)
        {
            _hasHandle = false; _session = null;
            code = error.Code;
            return false;
        }
    }

    /// <summary>
    /// Resolves the session a row's `session` input names. Only the handle this provider minted for the current
    /// world answers: a handle of another kind or another provider, one from a world that has ended, one whose slot
    /// was recycled, and no handle at all are all refused by name, never answered with an implicit session invented
    /// for the caller.
    /// </summary>
    internal bool TryResolve(JsonElement input, out SessionToken token, out string code)
    {
        token = default;
        if (_session == null || !_hasHandle) { code = DiagnosticCodes.SessionMissing; return false; }
        // The value has to be this world's session handle before the kernel is asked about it, so a handle of
        // another provider or another world is named as the wrong session rather than probed in the pool.
        if (input.ValueKind != JsonValueKind.Object
            || !string.Equals(Normalized(input), Normalized(_handle), StringComparison.Ordinal))
        {
            code = "diagnostic-handle-foreign";
            return false;
        }
        // "Is this a live handle of mine" is asked of the kernel's own entry point: the session registered the one
        // object its handle stands for, so a released slot, a recycled generation and a handle from an ended world
        // all answer false here, and a live one answers with that object.
        if (!_registration.TryNative(input, out var held) || !ReferenceEquals(held, _token))
        {
            code = DiagnosticCodes.StaleSession;
            return false;
        }
        token = new SessionToken(_session);
        code = "diagnostic-session";
        return true;
    }

    /// <summary>The canonical text of a handle value: the kernel writes one handle's value through one serializer,
    /// so two spellings of the same live handle differ only in whitespace, and the field-wise comparison is done
    /// through the parsed document rather than through a string that a re-serialization could reorder.</summary>
    private static string Normalized(JsonElement value)
    {
        var world = value.GetProperty("worldEpoch").GetInt64();
        var generation = value.GetProperty("lifeEpoch").GetInt64();
        var local = value.GetProperty("local").GetInt32();
        var provider = value.GetProperty("provider").GetInt32();
        return world + ":" + generation + ":" + local + ":" + provider;
    }

    /// <summary>Charges one record against the session's per-tick budget. A refusal is counted and named; nothing
    /// here silently skips a record.</summary>
    internal bool TryRecord(SessionToken token, out string code)
    {
        var session = token.Session;
        if (session == null || !ReferenceEquals(session, _session)) { code = DiagnosticCodes.SessionMissing; return false; }
        var tick = _tick();
        if (session.BudgetTick != tick) { session.BudgetTick = tick; session.RecordsThisTick = 0; }
        if (session.RecordsThisTick >= MaximumRecordsPerTick)
        {
            session.Dropped++;
            code = DiagnosticCodes.Budget;
            return false;
        }
        session.RecordsThisTick++;
        if (session.Records < long.MaxValue) session.Records++;
        code = "diagnostic-recorded";
        return true;
    }

    /// <summary>
    /// Whether this call is the one the sample rate takes. The decision is the session's own record count, so the
    /// sampling is reproducible from the numbers the session reports rather than from a hidden random source, and a
    /// rate of 1 takes every call. A call the rate does not take is counted as a drop.
    /// </summary>
    internal bool TrySample(SessionToken token, int sampleRate, out bool sampled, out string code)
    {
        sampled = false;
        var session = token.Session;
        if (session == null || !ReferenceEquals(session, _session)) { code = DiagnosticCodes.SessionMissing; return false; }
        var rate = sampleRate > 0 ? sampleRate : session.SampleRate;
        session.SampleRate = rate;
        session.Seen++;
        sampled = rate <= 1 || session.Seen % rate == 0;
        if (!sampled) session.Dropped++;
        code = sampled ? "diagnostic-sampled" : "diagnostic-sampled-out";
        return true;
    }

    /// <summary>Adds one sample to a metric's aggregate. The series table is bounded, so a plan that invents a new
    /// metric name every tick is refused instead of growing the session without limit.</summary>
    internal bool TryMetric(SessionToken token, string name, double value, long window, out MetricSnapshot snapshot, out string code)
    {
        snapshot = default;
        var session = token.Session;
        if (session == null || !ReferenceEquals(session, _session)) { code = DiagnosticCodes.SessionMissing; return false; }
        if (!session.Metrics.TryGetValue(name, out var series))
        {
            if (session.Metrics.Count >= MaximumMetricsPerSession) { code = DiagnosticCodes.Budget; return false; }
            series = new MetricSeries();
            session.Metrics.Add(name, series);
        }
        series.Window = window;
        series.Add(value, _tick());
        snapshot = new MetricSnapshot(name, series.Samples, series.Sum, series.Minimum, series.Maximum, series.Last);
        code = "diagnostic-metric";
        return true;
    }

    internal void CountAssertion(SessionToken token, bool failed)
    {
        var session = token.Session;
        if (session == null || !ReferenceEquals(session, _session) || !failed) return;
        if (session.Assertions < long.MaxValue) session.Assertions++;
    }

    internal long Dropped => _session?.Dropped ?? 0;
    internal long Records => _session?.Records ?? 0;
    internal long FailedAssertions => _session?.Assertions ?? 0;

    /// <summary>A new world starts with no session: the previous world's handle is already stale and the record it
    /// named belongs to a level that no longer exists.</summary>
    internal void BeginWorld()
    {
        _session = null;
        _handle = default;
        _hasHandle = false;
    }

    /// <summary>The world is over. The handle stays in the kernel's pool until the kernel releases it — a value
    /// read after this is `stale-handle` — so there is nothing to release here beyond dropping the record.</summary>
    internal void EndWorld() => BeginWorld();

    /// <summary>A token that names the live session, and nothing else: it is not a handle value, so a record can
    /// never be written against a session a world change already dropped.</summary>
    internal readonly struct SessionToken
    {
        private readonly Session? _session;
        internal SessionToken(Session session) { _session = session; }
        internal Session? Session => _session;
    }
    /// <summary>The numbers one metric reports back.</summary>
    internal readonly record struct MetricSnapshot(string Name, long Samples, double Sum, double Minimum, double Maximum, double Last);
}

/// <summary>The refusal codes the diagnostic rows answer with. They are spelled once here so the handlers, the
/// evidence file and the tests cannot drift apart.</summary>
internal static class DiagnosticCodes
{
    /// <summary>Registration was attempted outside `RuntimeMode.Authoring`. The gate is checked before anything is
    /// registered, so a player build never grows these endpoints at all.</summary>
    internal const string Disabled = "diagnostic-disabled";
    /// <summary>A row was reached without a live session handle of this provider's own.</summary>
    internal const string SessionMissing = "diagnostic-session-missing";
    /// <summary>A row's own `session` input did not name the live session: the wrong value, a handle this provider
    /// did not mint, or a handle from a world that has ended.</summary>
    internal const string StaleSession = "diagnostic-stale-session";
    /// <summary>A session's own per-tick or per-call budget is spent, or a series ceiling is reached. The dropped
    /// count is reported beside it.</summary>
    internal const string Budget = "diagnostic-budget";
    /// <summary>`inspect` was asked for a field no entity snapshot carries.</summary>
    internal const string FieldsUnknown = "diagnostic-fields-unknown";
    /// <summary>`inspect` was given a reference the world does not currently hold.</summary>
    internal const string EntityUnavailable = "diagnostic-entity-unavailable";
    /// <summary>An `assert` policy this build does not implement. Nothing here pauses a plan: the kernel owns no
    /// pause state, so a value that would mean "halt" is refused by name instead of pretending.</summary>
    internal const string PolicyUnsupported = "diagnostic-policy-unsupported";
    /// <summary>A sample rate below 1 would mean "record nothing at all", which is not a rate.</summary>
    internal const string SampleRateInvalid = "diagnostic-sample-rate-invalid";
}
