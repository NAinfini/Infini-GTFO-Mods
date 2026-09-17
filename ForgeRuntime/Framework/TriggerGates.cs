using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>
/// The one option block every trigger entry point may carry, and the state those options need at runtime.
///
/// The five options are the authoring panel's own trigger options (plan §3.4 统一选项): a card's trigger face is
/// the same face whatever event stands behind it, so the options belong to the entry point, not to a binding —
/// every trigger entry is gated in exactly one place, <see cref="RuntimeKernel"/>'s dispatch loop, and no trigger
/// provider implements a counter, a clock or a die of its own.
///
/// The block is written in the plan file as `entrypoints[].gate`, a structural object the loader resolves once and
/// refuses at load. Nothing in it is read from an event payload, so a plan without one keeps the behaviour it had:
/// an entry with no gate walks every dispatch it claims.
///
/// Host authority is structural here rather than a policy. The table below is only touched from a host advance,
/// and what a client sees is the effect of a decision the host already made, so the gate state never travels on
/// the wire: a client neither charges a threshold nor draws a random number, and never needs the table.
/// </summary>
public static class TriggerGateContract
{
    /// <summary>The scope spellings the accounting may carry. `player` is the per-player reading: the threshold, the
    /// cap and the clock are charged against the player the event is about, so two players have two of each. `level`
    /// is the whole-card reading, one set for the level whatever the event names. `instance` is the per-host
    /// reading: one set per mounting entity, which is how "each enemy counts its own hits" is written.</summary>
    public const string PlayerScope = "player";
    public const string LevelScope = "level";
    public const string InstanceScope = "instance";

    /// <summary>The scopes a gate may name, as the compiler spells them (plan §3.4 统一选项).</summary>
    internal static readonly string[] Scopes = { PlayerScope, LevelScope, InstanceScope };

    /// <summary>
    /// One entry point's `gate` block, or null where the entry declares none. The block is optional and its own
    /// fields are all optional, so a card that leaves an option alone writes nothing for it and the loader fills in
    /// the two defaults the authoring panel shows: no reset delay, a per-player cooldown scope.
    ///
    /// Each option refuses with its own code, because a rejected plan has to name the option that is wrong: one
    /// code per field is what makes a refusal actionable for whoever wrote the card. Anything else in the block is
    /// `unknown-field`, and a block that names no option at all is `gate-empty` rather than a no-op the loader keeps.
    /// </summary>
    internal static TriggerGateOptions? Parse(JsonElement entry, IReadOnlyList<PlanAttachment> attachments)
    {
        if (!entry.TryGetProperty("gate", out var gate) || gate.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(gate.ValueKind == JsonValueKind.Object, "gate-shape", "gate");
        // Every option is optional, so the shape check is the unknown-field half alone; the at-least-one half is the
        // options record's own emptiness, which is also what the loader would have to answer anyway.
        RuntimeJson.Shape(gate, "", string.Join(' ', gateFields));
        var options = new TriggerGateOptions(
            Count(gate, "accumulate"), Amount(gate), Offset(gate), Delay(gate, "reset_delay"),
            Count(gate, "max_count"), Count(gate, "cooldown"), Scope(gate), Probability(gate), Once(gate));
        // A threshold below one is the option that is not a threshold: it would fire on the arrival that reached it
        // and on every arrival after, which is what leaving the option out already means. Each option's own value is
        // judged before anything is asked about the block as a whole, so a card that wrote a number wrong is told
        // that rather than told about a scope.
        RuntimeJson.Require(options.Accumulate is not { } threshold || threshold >= 1, "gate-count", "accumulate");
        // An amount source is only a source for a window it can fill, and a head start is only a head start inside
        // the window it starts: both are refused where they name no threshold, instead of being kept as fields
        // nothing would ever read.
        RuntimeJson.Require(options.AccumulateAmount == null || options.Accumulate != null, "gate-amount",
            "accumulate_amount needs accumulate: there is no window to fill without a threshold.");
        RuntimeJson.Require(options.ThresholdOffset == 0 || options.Accumulate != null, "gate-offset",
            "threshold_offset needs accumulate: a head start only means something inside a window.");
        RuntimeJson.Require(options.Accumulate is not { } window || options.ThresholdOffset < window, "gate-offset",
            "threshold_offset must be smaller than accumulate: a start at or above the threshold is a fire.");
        // The scope is the subject the threshold, the cap and the clock are booked against, so it is required by
        // exactly the options that are booked and forbidden to the ones that are not: a card that names no scoped
        // option and a card that names one without a scope are both refused, because the runtime does not guess
        // which subject an author meant (plan §3.4 统一选项: 编译器按卡种写默认值，运行时不猜). Asked before the
        // emptiness check, because a block that names the scope alone has named something and its own refusal is the
        // actionable one.
        var scoped = options.Accumulate != null || options.MaxCount != null || options.Cooldown != null;
        RuntimeJson.Require(scoped == (options.Scope != null), "gate-scope", scoped
            ? "A gate with accumulate, max_count or cooldown must name the scope those options are booked against."
            : "scope needs accumulate, max_count or cooldown: there is nothing to book against a subject.");
        RuntimeJson.Require(!options.IsDefault, "gate-empty", "A gate block must name at least one option.");
        RuntimeJson.Require(options.Cooldown == null || options.Cooldown > 0,
            "gate-cooldown", "A cooldown of zero fires on every event.");
        // A per-instance gate is booked against the entity a mount target accepted, so a plan whose mounts take no
        // subject — `level` alone — has no instance to count and is refused rather than silently counting the level.
        RuntimeJson.Require(options.Scope != InstanceScope || attachments.Any(a => a.Kind != "level"), "gate-scope",
            "scope `instance` needs a mount target that accepts an entity; this plan mounts on the level alone.");
        return options;
    }

    private static readonly string[] gateFields =
    {
        "accumulate", "accumulate_amount", "threshold_offset", "reset_delay", "max_count", "cooldown", "scope",
        "probability", "once"
    };

    /// <summary>One non-negative count option. An explicit null is the same as leaving the field out, which is how
    /// an authoring panel writes a control it has not filled in.</summary>
    private static long? Count(JsonElement gate, string name)
    {
        if (!gate.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsFinite(number) && number == Math.Truncate(number) && number is >= 0 and <= RuntimeJson.MaxSafeInteger,
            "gate-count", name);
        return (long)value.GetDouble();
    }

    /// <summary>The event output port whose number one activation contributes, or null to count the activation
    /// itself. A port name is not a number, so a misspelled one is a load refusal here; a port the matched event
    /// does not carry is refused when the gate is charged, because only then is the event known.</summary>
    private static string? Amount(JsonElement gate)
    {
        if (!gate.TryGetProperty("accumulate_amount", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
            "gate-amount", "accumulate_amount must name an event output port.");
        return value.GetString();
    }

    /// <summary>The head start the first window gets: a non-negative amount that a window of this size still needs
    /// something more to reach.</summary>
    private static long Offset(JsonElement gate)
    {
        if (!gate.TryGetProperty("threshold_offset", out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsFinite(number) && number == Math.Truncate(number) && number is >= 0 and <= RuntimeJson.MaxSafeInteger,
            "gate-offset", "threshold_offset must be a whole number of at least zero.");
        return (long)value.GetDouble();
    }

    /// <summary>The reset delay, which is a duration rather than a count and so carries the delay's own code.</summary>
    private static long Delay(JsonElement gate, string name)
    {
        if (!gate.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsFinite(number) && number == Math.Truncate(number) && number is >= 0 and <= RuntimeJson.MaxSafeInteger,
            "gate-reset-delay", name);
        return (long)value.GetDouble();
    }

    /// <summary>The scope the gate's accounting is booked against, or null where the block names none. The spelling
    /// is the compiler's own; the runtime never fills in a default.</summary>
    private static string? Scope(JsonElement gate)
    {
        if (!gate.TryGetProperty("scope", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var scope = RuntimeJson.Text(value);
        RuntimeJson.Require(TriggerGateContract.Scopes.Contains(scope, StringComparer.Ordinal), "gate-scope", scope);
        return scope;
    }

    private static double? Probability(JsonElement gate)
    {
        if (!gate.TryGetProperty("probability", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsFinite(number) && number is > 0 and <= 1, "gate-probability", "probability");
        return value.GetDouble();
    }

    private static bool Once(JsonElement gate)
    {
        if (!gate.TryGetProperty("once", out var value) || value.ValueKind == JsonValueKind.Null) return false;
        RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "gate-once", "once");
        return value.ValueKind == JsonValueKind.True;
    }
}

/// <summary>
/// One entry point's resolved trigger options. Every option is absent by default, and an absent option is a check
/// that does not happen: an entry with no <see cref="Accumulate"/> fires on the first matching event, one with no
/// <see cref="MaxCount"/> never runs out, one with no <see cref="Cooldown"/> never waits, one with no
/// <see cref="Probability"/> always fires and one without <see cref="Once"/> does not latch.
///
/// <see cref="Accumulate"/> is the threshold one window has to reach and <see cref="ResetDelay"/> is the gap that
/// ends a window: an entry that needs three arrivals and allows no gap longer than ten ticks forgets what it had
/// after eleven quiet ticks and starts again from one. The threshold is charged in full for every window —
/// reaching it fires the entry, which empties what it had — so a window is the span between two activations.
///
/// <see cref="AccumulateAmount"/> says what one activation contributes: the number in the event output port it
/// names, or, without it, the activation itself, which is the "count or amount" the option offers. The amount is
/// read from the matched event's own outputs, so a card that accumulates damage accumulates the damage the event
/// carried rather than a number the plan restated.
///
/// <see cref="ThresholdOffset"/> is a head start, and the first window's alone: once a window ends, whether by
/// firing or by the reset delay, the next one starts at zero and needs the whole threshold.
/// <see cref="MaxCount"/> is a separate budget — how many times the entry may ever fire — and <see cref="Once"/>
/// is that budget being one, which is the whole of the option's difference from a cap of one.
///
/// <see cref="Scope"/> is which subject the three booked options are charged against, and it is required by
/// whichever of them is declared: `player` books one set per player, `level` one set for the whole level, and
/// `instance` one set per mounting entity. The runtime does not default it — the compiler writes the card's own
/// default — so a block that names a booked option without a scope never loads.
/// </summary>
public sealed record TriggerGateOptions(
    long? Accumulate = null, string? AccumulateAmount = null, long ThresholdOffset = 0, long ResetDelay = 0,
    long? MaxCount = null, long? Cooldown = null, string? Scope = null,
    double? Probability = null, bool Once = false)
{
    /// <summary>Whether this block asks for anything at all. A block that names no check is refused at load rather
    /// than kept as a no-op, so a plan never carries a gate object that means nothing. The two threshold parts
    /// count as naming something even where they stand alone: their refusal is then the actionable one — they
    /// belong to a window that is not there — instead of a block that is called empty for having named them.</summary>
    public bool IsDefault => Accumulate == null && AccumulateAmount == null && ThresholdOffset == 0 && MaxCount == null
        && Cooldown == null && Probability == null && !Once;

    /// <summary>The number of times this entry may ever fire, or null while it may fire forever.</summary>
    internal long? Cap => Once ? 1 : MaxCount;
}

/// <summary>One entry point's refusal, as a receipt reports it: the entry that was refused, the check that refused
/// it, and how far that entry had got — the two numbers the "why did my trigger not fire" question is asked about.</summary>
public sealed record TriggerGateReceipt(string PlanId, string NodeId, string Code, long Accumulated, long Fired);

/// <summary>
/// The host's gate state: one row per gated entry point, each carrying one booked set per scope subject — a player,
/// the level, or a mounting entity. This is a second table rather than another variable scope because nothing here
/// is authored, named or read by a plan — a plan's own variables are the author's, and a counter the framework
/// keeps for a trigger is not.
///
/// A row's lifecycle follows the entry point's: a released plan forgets its rows, a checkpoint keeps them (a
/// threshold half reached is progress the level made, not a fact of the machine that made it), and an entry with
/// no gate owns no row at all. A per-instance row's entries are dropped when the level's objects are rebuilt —
/// a restored checkpoint re-creates them — and an instance whose entity is already gone when it is next asked
/// about is dropped at that question rather than counted as a stranger's history.
/// </summary>
internal sealed class RuntimeTriggerGateStore
{
    private const int MaximumScopesPerGate = 1024;
    private const int MaximumRows = 65536;
    private const long MaximumCounter = 9007199254740991;

    /// <summary>One subject's booked state: the threshold, the cap and the clock of exactly one player, of the
    /// level, or of one mounting entity. Everything an option counts lives here, so the scope is not a property of
    /// the cooldown alone — a card whose threshold is per-enemy keeps one window per enemy as well.</summary>
    private sealed class Scope
    {
        /// <summary>Arrivals counted towards the current threshold, including the first window's head start.</summary>
        internal long Accumulated;
        /// <summary>How many times the entry has fired. Also the cap's counter, so `once` is a cap of one.</summary>
        internal long Fired;
        /// <summary>The tick of the last counted arrival, and whether there has ever been one: a gap longer than the
        /// reset delay forgets what was accumulated instead of adding to it. The flag is a lifetime latch that a
        /// reset never clears, so it is also what keeps the head start to the first window.</summary>
        internal long LastArrival;
        internal bool Started;
        /// <summary>The tick this subject's clock was charged at.</summary>
        internal long Charged = long.MinValue;
        /// <summary>How many draws the deterministic die has handed out, so a draw is a function of the world and
        /// the row's own history rather than of how the process was scheduled.</summary>
        internal long Rolls;
        /// <summary>For an instance scope, the entity this set is booked against: its identity is the key, and the
        /// reference is what says whether that entity is still alive.</summary>
        internal EntityReference Entity;
    }

    private sealed class Row
    {
        /// <summary>The booked sets by scope subject: the entity id of a player or an instance, or the empty
        /// string for the level.</summary>
        internal readonly Dictionary<string, Scope> Scopes = new(StringComparer.Ordinal);

        /// <summary>One booked set, or null where that subject has never been asked about.</summary>
        internal Scope? Find(string key) => Scopes.TryGetValue(key, out var scope) ? scope : null;
    }

    private readonly Dictionary<string, Row> rows = new(StringComparer.Ordinal);
    /// <summary>The plans whose entry points declare a gate at all, and by which node. A plan outside this table
    /// pays one dictionary miss per dispatch and nothing else, which is the whole cost of the feature for a card
    /// that does not use it.</summary>
    private readonly Dictionary<string, Dictionary<string, TriggerGateOptions>> gated = new(StringComparer.Ordinal);

    /// <summary>The gated entries of one plan by node id, or false where the plan declares none.</summary>
    internal bool TryEntries(string planId, out Dictionary<string, TriggerGateOptions> entries)
        => gated.TryGetValue(planId, out entries!);

    /// <summary>This world's die seed, drawn once by the host when the world begins and carried by every
    /// checkpoint of it. The host is the only machine that rolls — a client never evaluates a gate — so the seed
    /// is host state like the counters beside it and never travels on the wire; a checkpoint restored on the
    /// machine that rolled keeps handing out the same sequence, which is what makes a probability option
    /// reproducible across a reload. Zero means the host has not begun a world yet, and no draw can happen before
    /// it has: a roll is a dispatch, and a dispatch only runs in an advance of a world that started.</summary>
    private ulong sessionSeed;
    /// <summary>Begins a world: the counters go (the world ended, not the plan) and the die takes its seed.</summary>
    internal void BeginWorld(long seed)
    {
        rows.Clear();
        sessionSeed = unchecked((ulong)seed);
    }

    internal int Count => rows.Count;
    internal bool HasGates => gated.Count > 0;

    /// <summary>
    /// Registers one plan's gated entry points, replacing whatever the plan declared before. Entry points with no
    /// gate are dropped here rather than filtered at dispatch, so the hot path never asks a question whose answer
    /// is already known. A reload keeps the rows: the checkpoint, not the file, owns the counters.
    /// </summary>
    internal void Declare(string planId, IReadOnlyList<ResolvedEntry> entries)
    {
        var table = new Dictionary<string, TriggerGateOptions>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (entry.Gate is { IsDefault: false } gate) table[entry.NodeId] = gate;
        if (table.Count == 0) gated.Remove(planId); else gated[planId] = table;
    }

    /// <summary>Drops every row of one released plan and stops gating its entries: a released entry point is not a
    /// counter the world kept.</summary>
    internal void ForgetPlan(string planId)
    {
        if (!gated.Remove(planId)) return;
        var prefix = planId + ".";
        foreach (var key in rows.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            rows.Remove(key);
    }

    /// <summary>Drops every row without forgetting which entries are gated: the world, not the plan, ended.</summary>
    internal void Clear() => rows.Clear();

    /// <summary>
    /// The one gate check, called once per (plan, entry) a dispatch claims, in plan and entry order. A refused
    /// entry is reported through <paramref name="refusals"/> and its work items are dropped; an accepted one has
    /// already charged its threshold, its cap and its clocks by the time this returns true, so the walk that
    /// follows sees only entries allowed to run.
    ///
    /// <param name="player"/> is the entity a per-player gate is booked against — the event's instigator, or
    /// the entity the plan's mount accepted when the event names no instigator.
    ///
    /// <param name="instance"/> is the entity a per-instance gate is booked against: the mounting entity this very
    /// activation was matched on. A scope whose subject this event cannot name is refused as `gate-scope` rather
    /// than silently sharing one set between everyone.
    ///
    /// <paramref name="outputs"/> is the matched event's own payload, and the only place an amount threshold can
    /// be read from: the number a card accumulates is the number the event carried.
    /// </summary>
    internal bool Admit(string planId, string nodeId, TriggerGateOptions options, long tick, EntityReference? player,
        EntityReference? instance, Func<EntityReference, bool>? resolve, JsonElement outputs,
        List<TriggerGateReceipt> refusals)
    {
        var row = RowOf(planId, nodeId);
        var key = ScopeKey(options.Scope, player, instance);
        if (key == null)
        {
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-scope", 0, 0));
            return false;
        }
        var scoped = row.Find(key);
        // An instance whose entity is already gone is not a subject this world has: its booked set is dropped and
        // this arrival starts a fresh one, rather than adding to a counter that belonged to a dead enemy.
        if (scoped != null && options.Scope == TriggerGateContract.InstanceScope
            && scoped.Entity.Id.Length > 0 && resolve != null && !Current(resolve, scoped.Entity))
        { row.Scopes.Remove(key); scoped = null; }
        if (scoped == null)
        {
            // The booked set is what a new subject costs, so the cap is asked of the subjects that still exist:
            // an instance subject whose entity is gone is dropped before the cap is read, because a level that has
            // been running long enough to outlive a thousand enemies must not be told its card is out of room by
            // a thousand dead ones. The sweep is the same judgement the arrival above makes about its own subject.
            if (row.Scopes.Count >= MaximumScopesPerGate && options.Scope == TriggerGateContract.InstanceScope && resolve != null)
                foreach (var gone in row.Scopes.Where(pair => pair.Value.Entity.Id.Length > 0 && !Current(resolve, pair.Value.Entity))
                    .Select(pair => pair.Key).ToArray())
                    row.Scopes.Remove(gone);
            if (row.Scopes.Count >= MaximumScopesPerGate)
            {
                refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-scope", 0, 0));
                return false;
            }
            scoped = new Scope { Entity = instance ?? default };
            row.Scopes.Add(key, scoped);
        }
        // The cap is the cheapest check and the only one that can never be undone, so it is asked first: a spent
        // entry does no bookkeeping at all.
        if (options.Cap is { } cap && scoped.Fired >= cap)
        {
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-max-count", scoped.Accumulated, scoped.Fired));
            return false;
        }
        if (options.Cooldown is { } cooldown && scoped.Charged != long.MinValue && tick - scoped.Charged < cooldown)
        {
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-cooldown", scoped.Accumulated, scoped.Fired));
            return false;
        }
        if (options.Accumulate is { } threshold)
        {
            // A refused arrival is not an arrival: an event that does not carry the port the gate named leaves the
            // window exactly as it was, so a later event that does carry one can still reach the threshold.
            if (!Contribution(options, outputs, out var amount))
            {
                refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-amount", scoped.Accumulated, scoped.Fired));
                return false;
            }
            // A reset delay of zero is the absence of one: the window is only ever forgotten when the plan asked for
            // a delay and the arrivals waited longer than it. Reading zero as "forget immediately" would make every
            // threshold of a plan that never named a delay unreachable.
            if (scoped.Started && options.ResetDelay > 0 && tick - scoped.LastArrival > options.ResetDelay) scoped.Accumulated = 0;
            // The head start is the first window's alone, which is what the lifetime latch already remembers: both
            // ways a window ends — firing, and the reset delay — leave the next one needing the whole threshold.
            if (!scoped.Started) scoped.Accumulated = options.ThresholdOffset;
            scoped.Started = true;
            scoped.LastArrival = tick;
            scoped.Accumulated = Math.Min(MaximumCounter, scoped.Accumulated + amount);
            if (scoped.Accumulated < threshold)
            {
                refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-accumulate", scoped.Accumulated, scoped.Fired));
                return false;
            }
            scoped.Accumulated = 0;
        }
        if (options.Probability is { } probability && NextDraw(planId, nodeId, scoped) >= probability)
        {
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-probability", scoped.Accumulated, scoped.Fired));
            return false;
        }
        // Every check passed, so this activation happens: the two things that can only be counted once it does are
        // charged here and nowhere earlier. A refused activation costs an event, never a clock.
        scoped.Fired++;
        if (options.Cooldown != null) scoped.Charged = tick;
        return true;
    }

    /// <summary>
    /// The subject one booking is keyed by: the entity id of the player or the mounting instance, the empty string
    /// for the level, and null where the scope names a subject this activation cannot. The level's own set is keyed
    /// by an empty string rather than by a sentinel entity so a checkpoint writes it the same way it writes a
    /// player's.
    ///
    /// A gate that books none of the three scoped options — a roll, or a plain `once` — still owns one set: the
    /// entry point's own row, keyed like the level's. The set is not the level's scope, it is the absence of a
    /// subject, and nothing else may be handed it.
    /// </summary>
    private static string? ScopeKey(string? scope, EntityReference? player, EntityReference? instance) => scope switch
    {
        null or TriggerGateContract.LevelScope => "",
        TriggerGateContract.PlayerScope => player == null ? null : player.Id,
        TriggerGateContract.InstanceScope => instance == null ? null : instance.Id,
        _ => null
    };

    /// <summary>
    /// What one arrival adds to the window: one activation, or the number the event carries in the output port the
    /// gate named. A missing port, a value that is not a whole non-negative number, and one too large for the
    /// counters are the same refusal, because from the gate's side they are the same problem — the amount the card
    /// asked for cannot be read, so counting the arrival as one would fire on a number nobody chose.
    /// </summary>
    private static bool Contribution(TriggerGateOptions options, JsonElement outputs, out long amount)
    {
        amount = 1;
        if (options.AccumulateAmount is not { } port) return true;
        if (outputs.ValueKind != JsonValueKind.Object || !outputs.TryGetProperty(port, out var value)) return false;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) return false;
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < 0) return false;
        amount = number > MaximumCounter ? MaximumCounter : (long)number;
        return true;
    }

    /// <summary>Whether one booked entity is still part of this world: the question is put to the resolver that
    /// owns the entity's kind and to nobody else. A kind nothing resolves counts as current, exactly as an effect's
    /// own subject check reads it — this kernel only drops what it can prove is gone.</summary>
    private static bool Current(Func<EntityReference, bool> resolve, EntityReference entity)
    {
        try { return resolve(entity); }
        catch (Exception) { return false; }
    }

    private Row RowOf(string planId, string nodeId)
    {
        var key = planId + "." + nodeId;
        if (rows.TryGetValue(key, out var row)) return row;
        RuntimeJson.Require(rows.Count < MaximumRows, "gate-budget", "Gate row capacity reached.");
        row = new Row(); rows.Add(key, row); return row;
    }

    /// <summary>
    /// One deterministic draw. The die is a pure function of the row's address, how many draws it has already
    /// made and the world's own seed, so the same plan in the same world hands out the same sequence: a
    /// probability option is reproducible in a replay, and a checkpoint carries the seed that makes it so. It is
    /// deliberately not one shared generator, because then one entry's draws would shift another's.
    /// </summary>
    private double NextDraw(string planId, string nodeId, Scope scope)
    {
        var seed = Mix(Mix(Mix((ulong)scope.Rolls + 1, Hash(planId)), Hash(nodeId)), sessionSeed);
        scope.Rolls++;
        unchecked
        {
            var state = (uint)(seed ^ (seed >> 32));
            state += 0x6d2b79f5u;
            var value = (state ^ (state >> 15)) * (1u | state);
            value ^= value + ((value ^ (value >> 7)) * (61u | value));
            return (value ^ (value >> 14)) / 4294967296d;
        }
    }

    private static ulong Hash(string text)
    {
        var value = 14695981039346656037UL;
        foreach (var character in text) value = unchecked((value ^ character) * 1099511628211UL);
        return value;
    }

    private static ulong Mix(ulong left, ulong right)
    {
        unchecked
        {
            var value = left ^ (right + 0x9e3779b97f4a7c15UL + (left << 6) + (left >> 2));
            value ^= value >> 33; value *= 0xff51afd7ed558ccdUL; value ^= value >> 33;
            return value;
        }
    }

    /// <summary>The whole host table as one JSON row list. Written by the host and read back by the host: this is
    /// the checkpoint's half of the gate state, never a message a client applies. Every booked set travels with the
    /// subject it belongs to, so a checkpoint taken with half a threshold reached puts that half back for the same
    /// player, the same enemy or the level.</summary>
    internal string Save()
    {
        var entries = rows.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
        {
            key = pair.Key,
            scopes = pair.Value.Scopes.OrderBy(scope => scope.Key, StringComparer.Ordinal).Select(scope => new
            {
                subject = scope.Key,
                accumulated = scope.Value.Accumulated,
                fired = scope.Value.Fired,
                lastArrival = scope.Value.LastArrival,
                started = scope.Value.Started,
                charged = scope.Value.Charged,
                rolls = scope.Value.Rolls
            }).ToArray()
        }).ToArray();
        return RuntimeJson.StableText(RuntimeJson.From(new { seed = unchecked((long)sessionSeed), rows = entries }));
    }

    /// <summary>
    /// Restores a checkpoint's gate rows over the declarations that are loaded. A row is restored only where a
    /// loaded plan declares that exact entry point with a gate: a checkpoint names counters, and a counter whose
    /// address no longer exists is not put back where a card's next option would charge a stranger's history. A row
    /// for an entry point that is declared now but was not when the checkpoint was taken is simply absent, which is
    /// the same as never having fired.
    ///
    /// A per-instance row is the one thing a checkpoint does not carry back: restoring one re-creates the level's
    /// own objects, so a saved instance subject names an entity that no longer exists, exactly as a saved per-enemy
    /// variable does. The counter is dropped with the object it counted.
    ///
    /// The seed comes back with the rows: a checkpoint is the world's own die, so the sequence a probability option
    /// was drawing from continues where the save left it instead of restarting from a fresh draw.
    /// </summary>
    internal void Restore(string json)
    {
        rows.Clear();
        var document = RuntimeJson.Parse(json);
        if (document.ValueKind == JsonValueKind.Null) { sessionSeed = 0; return; }
        RuntimeJson.Require(document.ValueKind == JsonValueKind.Object, "gate-checkpoint",
            "Gate state must be an object carrying the seed and the rows.");
        RuntimeJson.Shape(document, "", "seed rows");
        sessionSeed = document.TryGetProperty("seed", out var seed)
            ? unchecked((ulong)RuntimeJson.Integer(seed, long.MinValue, long.MaxValue)) : 0UL;
        var entries = RuntimeJson.Rows(document, "rows", MaximumRows);
        RuntimeJson.Require(entries.Length <= MaximumRows, "gate-checkpoint", "Too many gate rows.");
        foreach (var entry in entries)
        {
            var key = RuntimeJson.Text(entry, "key");
            var separator = key.IndexOf('.');
            if (separator <= 0 || separator == key.Length - 1) continue;
            var planId = key[..separator];
            var nodeId = key[(separator + 1)..];
            if (!gated.TryGetValue(planId, out var table) || !table.TryGetValue(nodeId, out var options)) continue;
            RuntimeJson.Shape(entry, "key scopes");
            var row = new Row();
            foreach (var scoped in RuntimeJson.Rows(entry, "scopes", MaximumScopesPerGate))
            {
                if (row.Scopes.Count >= MaximumScopesPerGate) break;
                RuntimeJson.Shape(scoped, "subject accumulated fired lastArrival started charged rolls");
                // The empty subject is the level's own set and an unscoped gate's single set, so it is the one
                // legitimately empty string in a checkpoint and is read here rather than through the shared text
                // rule; every other key is an entity id and is validated as one.
                var subjectValue = scoped.GetProperty("subject");
                RuntimeJson.Require(subjectValue.ValueKind == JsonValueKind.String, "gate-checkpoint", "subject");
                var subject = subjectValue.GetString()!;
                if (subject.Length > 0) RuntimeJson.Text(subject);
                // The level's own set and an unscoped gate's single set are both keyed by the empty string; every
                // other key is an entity id, and only a per-player gate is booked against one. A set whose key does
                // not match the scope this load declares is dropped rather than restored onto another subject.
                if (subject.Length == 0)
                {
                    if (options.Scope is not (null or TriggerGateContract.LevelScope)) continue;
                }
                else if (options.Scope != TriggerGateContract.PlayerScope) continue;
                row.Scopes[subject] = new Scope
                {
                    Accumulated = RuntimeJson.Integer(scoped.GetProperty("accumulated"), 0, MaximumCounter),
                    Fired = RuntimeJson.Integer(scoped.GetProperty("fired"), 0, MaximumCounter),
                    LastArrival = RuntimeJson.Integer(scoped.GetProperty("lastArrival"), long.MinValue, long.MaxValue),
                    Started = RuntimeJson.Flag(scoped, "started"),
                    Charged = RuntimeJson.Integer(scoped.GetProperty("charged"), long.MinValue, long.MaxValue),
                    Rolls = RuntimeJson.Integer(scoped.GetProperty("rolls"), 0, MaximumCounter)
                };
            }
            rows[key] = row;
        }
    }

    /// <summary>
    /// Drops the booked sets of every per-instance gate. Called at the one lifecycle boundary that rebuilds the
    /// level's own objects — restoring a checkpoint — because the entities those sets were keyed by are gone with
    /// them; a per-player set survives it, since the players are the same players.
    /// </summary>
    internal void PruneInstances(HashSet<string> instanceNodes)
    {
        if (instanceNodes.Count == 0) return;
        foreach (var pair in rows)
        {
            var separator = pair.Key.IndexOf('.');
            if (separator <= 0 || !instanceNodes.Contains(pair.Key[(separator + 1)..])) continue;
            pair.Value.Scopes.Clear();
        }
    }

    /// <summary>The booked sets of the per-player and the per-instance gates, for a caller reporting what a
    /// checkpoint carried.</summary>
    internal int ScopeRows => rows.Values.Sum(row => row.Scopes.Count);

    /// <summary>What one gated entry point has fired and accumulated for one subject, or null where the plan
    /// declares no gate for that entry at all. A subject that has never been asked about reads as the window it
    /// would start: nothing accumulated, nothing fired.</summary>
    internal (long Fired, long Accumulated)? StateOf(string planId, string nodeId, string subject)
    {
        var key = planId + "." + nodeId;
        if (!rows.TryGetValue(key, out var row)) return null;
        var scoped = row.Find(subject);
        return scoped == null ? (0, 0) : (scoped.Fired, scoped.Accumulated);
    }

    /// <summary>How many subjects one gated entry point is currently booked against, or null where the plan
    /// declares no gate for it.</summary>
    internal int? ScopeCount(string planId, string nodeId)
        => rows.TryGetValue(planId + "." + nodeId, out var row) ? row.Scopes.Count : null;
}
