using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>One named value an override asks to change on one equipment instance. The name is from
/// <see cref="WeaponOverrideLedger.Fields"/> and nothing else: a name outside that set has no native write point
/// and is refused with <see cref="WeaponOverrideLedger.UnknownFieldCode"/> rather than written somewhere close.
/// The value is a plain double here because this half is game-independent — the native half is the only place
/// that knows whether a given field is a float, an int or a nested range.</summary>
public sealed record WeaponOverrideField(string Name, double Value);

/// <summary>Where one override came from. Ruling 84 merges several overrides on one instance by writer order, and
/// the order has to be a property of the request rather than of the arrival order, so the identity of the writing
/// node travels with the request: `(plan, node, sequence)` compared as ordinals. `Sequence` is the caller's own
/// counter for a behaviour that fires more than once; it is not a clock.</summary>
public sealed record WeaponOverrideSource(string PlanId, string NodeId, long Sequence)
{
    /// <summary>The merge key: plan, then node, then sequence, each compared as an ordinal. A request with a
    /// larger key is a later write and wins the field it names.</summary>
    public int CompareTo(WeaponOverrideSource other)
    {
        if (other == null) throw new ArgumentNullException(nameof(other));
        var plan = string.CompareOrdinal(PlanId, other.PlanId);
        if (plan != 0) return plan;
        var node = string.CompareOrdinal(NodeId, other.NodeId);
        return node != 0 ? node : Sequence.CompareTo(other.Sequence);
    }
}

/// <summary>One request to change named values on one equipment instance for a bounded time. `DurationTicks`
/// of zero or less means the override lasts until the equipment life ends; a positive value is turned into an
/// absolute expiry by the ledger when the request is accepted, because a relative duration is only meaningful
/// once there is a tick to measure it from.</summary>
public sealed record WeaponOverrideRequest(EntityReference Equipment, IReadOnlyList<WeaponOverrideField> Fields,
    WeaponOverrideSource Source, long DurationTicks);

/// <summary>What the ledger currently holds for one equipment instance: the merged fields, the identity of the
/// writer that last set each one, and when the whole entry expires. `writers` is kept per field rather than per
/// entry so a later request can overwrite one value without discarding the others, which is the only merge order
/// ruling 84 allows — one order, computed from `(plan, node, sequence)`, with no second priority table.</summary>
public sealed class WeaponOverrideState
{
    internal WeaponOverrideState(EntityReference equipment)
    {
        Equipment = equipment;
        mutable = new List<WeaponOverrideField>();
        writers = new Dictionary<string, WeaponOverrideSource>(StringComparer.Ordinal);
    }

    // The merged field set and its per-field writers are the ledger's own working state, not a second public
    // view: the properties below are copies a caller can read without being able to write into the table the
    // ledger is deciding from.
    internal readonly List<WeaponOverrideField> mutable;
    internal readonly Dictionary<string, WeaponOverrideSource> writers;

    /// <summary>The instance this entry is about, as the equipment life's own reference. An override never
    /// invents a reference: a caller that has no current one has nothing to override.</summary>
    public EntityReference Equipment { get; }
    /// <summary>The accepted fields, in the order they were first set. One entry per name.</summary>
    public IReadOnlyList<WeaponOverrideField> Fields => mutable;
    /// <summary>The tick this entry stops being true, or 0 for an entry that lasts as long as the life does.</summary>
    public long ExpiryTick { get; internal set; }
    /// <summary>The source that last wrote each field, keyed by field name.</summary>
    public IReadOnlyDictionary<string, WeaponOverrideSource> Writers => writers;

    /// <summary>The value of one field, or null when this entry does not set it. A caller that needs to combine a
    /// request with what is already applied reads the difference from here instead of guessing a default.</summary>
    public double? Value(string name)
    {
        foreach (var field in mutable)
            if (string.Equals(field.Name, name, StringComparison.Ordinal)) return field.Value;
        return null;
    }
}

/// <summary>The write-back the native half supplies so a revert can be decided in one place. The ledger owns the
/// decision — what is applied, what expired, which world an entry belongs to — and the applier owns the native
/// objects, so the ledger calls this with a reference and never with a block.
///
/// A revert is asked for on three occasions and no more: one equipment life ending, one world epoch ending, and
/// one entry reaching its expiry tick. The applier restores the equipment's own block from its clone; a
/// reference the applier no longer holds is not an error, because an equipment life that already ended took its
/// clone with it.</summary>
public interface IWeaponOverrideSink
{
    /// <summary>Writes the merged fields of one accepted request onto the named instance.</summary>
    bool Apply(EntityReference equipment, IReadOnlyList<WeaponOverrideField> fields, out string code);
    /// <summary>Restores the instance's own block. Idempotent: restoring an instance that carries no override is
    /// a success with nothing to do, so a second revert of the same reference never fails a caller.</summary>
    bool Restore(EntityReference equipment, out string code);
    /// <summary>Restores every instance this machine has overridden. Returns how many it restored, so a world
    /// boundary can report whether anything was actually written back.</summary>
    int RestoreAll();
}

/// <summary>
/// The instance-override ledger: which equipment instance carries which changed values, who wrote them, and when
/// they stop being true. This is ruling 84's instance-level block replacement, decided here and written by the
/// native half.
///
/// What this file deliberately does not do: it never reads or writes an `ArchetypeDataBlock`, never touches the
/// gear pool, and never keeps a second copy of the game's own state. The field names are the one vocabulary the
/// native write points implement (see <see cref="Fields"/> for each name's destination and the evidence file for
/// the offsets), and the ledger holds only what a plan asked for.
///
/// The relationship with the gear-loadout session is a one-way ordering rule and not a shared data structure:
/// loadout narrows the gear pool before an instance exists, an override changes the block of an instance that
/// already exists, and an override never modifies the shared block or the pool array. There is therefore no
/// arbitration between the two — "pool first, then instance" is the whole of the rule.
/// </summary>
public sealed class WeaponOverrideLedger
{
    /// <summary>The most fields one request may carry. A request that names more is refused rather than
    /// truncated: a caller that asked for seventeen changes did not ask for sixteen of them.</summary>
    public const int MaximumOverrideFieldsPerRequest = 16;
    /// <summary>The most equipment instances one world may override at once. The count is of instances, not of
    /// requests, because the cost being bounded is the number of cloned blocks alive.</summary>
    public const int MaximumOverriddenEquipmentPerWorld = 64;

    /// <summary>The refusal code for a field name that has no native write point behind it.</summary>
    public const string UnknownFieldCode = "override-field-unknown";
    /// <summary>The refusal code for naming more fields, or overriding more instances, than the budget allows.</summary>
    public const string BudgetCode = "override-budget";
    /// <summary>The refusal code for an instance the native half cannot read the current block of. Nothing is
    /// cloned from nothing: an unreadable block is refused, not replaced with an empty one.</summary>
    public const string BlockMissingCode = "override-block-missing";
    /// <summary>The refusal code for a request about an instance that is no longer current, or about a field set
    /// that would not change anything.</summary>
    public const string StaleEquipmentCode = "stale-equipment";

    /// <summary>
    /// The field vocabulary, in the order the design lists it, each name against the native value it writes.
    /// Every destination is read per shot or per frame by the shipped code, which is what makes an instance
    /// clone effective at all; the evidence file carries the offsets and the read sites.
    ///
    /// Three names are additions to the design's own list rather than restatements of it: `burst_count` is the
    /// burst length the applier writes onto the burst archetypes, and the two spread scales are the movement and
    /// aim multipliers of the `spread` row. Two names from the design's list are deliberately absent: no value
    /// behind `spread_pattern` or `recoil_recovery` is written by the shipped code, so declaring either would
    /// advertise a write point the game does not have.
    /// </summary>
    public static readonly IReadOnlyList<string> Fields = new[]
    {
        "fire_rate", "burst_count", "spread_cone", "spread_movement_scale", "spread_aim_scale",
        "recoil_horizontal", "recoil_vertical", "recoil_recovery", "recoil_camera_kick"
    };

    private readonly Dictionary<string, WeaponOverrideState> _byEquipment = new(StringComparer.Ordinal);
    private readonly IWeaponOverrideSink? _sink;
    private long _world = -1;

    /// <summary>A ledger with no native half attached: every decision is still made — merging, budgets, expiry —
    /// but an accepted request cannot be written, so <see cref="Begin"/> reports it as unapplied. The focused
    /// tests use this shape, and so does a build whose native half failed to install.</summary>
    public WeaponOverrideLedger() { }

    /// <summary>A ledger whose accepted requests are written onto the native instances by <paramref name="sink"/>.</summary>
    public WeaponOverrideLedger(IWeaponOverrideSink sink)
        => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>Instances currently carrying an override in this world, expired entries excluded.</summary>
    public int Count
    {
        get
        {
            Sweep();
            return _byEquipment.Count;
        }
    }

    /// <summary>Whether a name is one the native half can write.</summary>
    public static bool IsKnownField(string? name)
    {
        if (name == null) return false;
        foreach (var field in Fields)
            if (string.Equals(field, name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Accepts one request. The whole request is checked before anything is written, so a request that names one
    /// unknown field changes nothing at all rather than its other fields: a half-applied weapon is a state no
    /// native code path produces, and ruling 84 rejects it by name.
    ///
    /// <paramref name="tick"/> is the world's current simulation tick. It is what a relative duration is measured
    /// from, and it is also what makes an already-expired request a refusal instead of a write that is reverted
    /// on the next sweep.
    /// </summary>
    public bool Begin(WeaponOverrideRequest request, long tick, out string code)
    {
        if (request == null) { code = StaleEquipmentCode; return false; }
        if (request.Fields == null || request.Fields.Count == 0) { code = StaleEquipmentCode; return false; }
        if (request.Fields.Count > MaximumOverrideFieldsPerRequest) { code = BudgetCode; return false; }
        foreach (var field in request.Fields)
            if (!IsKnownField(field.Name)) { code = UnknownFieldCode; return false; }
        if (request.Source == null) { code = StaleEquipmentCode; return false; }

        Sweep();
        var key = request.Equipment.Id ?? "";
        WeaponOverrideState? existing = null;
        var created = !_byEquipment.TryGetValue(key, out existing);
        if (created)
        {
            if (_byEquipment.Count >= MaximumOverriddenEquipmentPerWorld) { code = BudgetCode; return false; }
            existing = new WeaponOverrideState(request.Equipment);
            if (request.DurationTicks > 0)
            {
                if (request.DurationTicks > long.MaxValue - tick) { code = BudgetCode; return false; }
                existing.ExpiryTick = tick + request.DurationTicks;
            }
        }

        var merged = Merge(existing!, request);
        if (_sink == null) { code = ""; return false; }
        // The entry is only remembered once the write succeeded: a ledger that recorded a request the native
        // half refused would report an override that is not on the weapon.
        if (!_sink.Apply(request.Equipment, merged, out code)) return false;
        if (created) _byEquipment[key] = existing!;
        existing!.mutable.Clear();
        existing.mutable.AddRange(merged);
        foreach (var field in request.Fields) existing.writers[field.Name] = request.Source;
        code = "";
        return true;
    }

    /// <summary>Ends the override on one instance and asks the native half to put the instance's own block back.
    /// An instance that carries no override is a success: nothing to restore is not a failure to restore.</summary>
    public bool Revert(EntityReference equipment, out string code)
    {
        code = "";
        if (equipment == null || !_byEquipment.Remove(equipment.Id ?? "")) return true;
        return _sink == null || _sink.Restore(equipment, out code);
    }

    /// <summary>
    /// The fields one instance is currently overridden with, or an empty list when it carries none. This is what
    /// a gear spawn replays: `OnGearSpawnComplete` is the one place the game rebuilds an archetype, so an
    /// instance whose block was just rebuilt is written again from exactly this answer. Replay is idempotent
    /// because the answer is the merged absolute field set and not a delta.
    /// </summary>
    public IReadOnlyList<WeaponOverrideField> Pending(EntityReference equipment)
    {
        Sweep();
        return _byEquipment.TryGetValue(equipment?.Id ?? "", out var state)
            ? new List<WeaponOverrideField>(state.mutable)
            : Array.Empty<WeaponOverrideField>();
    }

    /// <summary>
    /// Starts a world, or returns whether the epoch actually changed. A notification for the epoch already in
    /// force gives nothing up: the instances it would restore are the ones the running world is using, and a
    /// repeated notification is not a world boundary. When the epoch did change, the previous world's entries are
    /// given up first — ruling 84 makes the world boundary an unconditional revert — and the native half restores
    /// every instance it holds. Nothing is carried across, because a reference from the old world is not
    /// resolvable in the new one and an override on it could never be reverted.
    /// </summary>
    public bool BeginWorld(long worldEpoch)
    {
        if (_world == worldEpoch) return false;
        _world = worldEpoch;
        _byEquipment.Clear();
        _sink?.RestoreAll();
        return true;
    }

    /// <summary>Ends the current world: same revert as <see cref="BeginWorld"/>, with no new epoch. Kept separate
    /// so an end that is not followed by a start still reverts, which is what an expedition teardown is.</summary>
    public void EndWorld()
    {
        _world = -1;
        _byEquipment.Clear();
        _sink?.RestoreAll();
    }

    /// <summary>
    /// Gives up every entry and reverts the native instances without ending the world. This is a checkpoint
    /// reload's boundary: the same expedition continues and the epoch does not move, but the level's gear is
    /// rebuilt from the game's own data, so an override this world accepted no longer exists on any instance and
    /// must not be replayed onto the rebuilt ones. It is <see cref="BeginWorld"/>'s revert without its epoch.
    /// </summary>
    public void RestoreAll()
    {
        _byEquipment.Clear();
        _sink?.RestoreAll();
    }

    /// <summary>
    /// Reverts every entry whose expiry tick has passed. Called from the places that already know the tick —
    /// accept, query and the native sweep — rather than from a timer of its own, so the ledger owns no clock and
    /// an idle world costs nothing.
    /// </summary>
    public void Sweep(long tick)
    {
        List<EntityReference>? expired = null;
        foreach (var state in _byEquipment.Values)
        {
            if (state.ExpiryTick <= 0 || state.ExpiryTick > tick) continue;
            (expired ??= new List<EntityReference>()).Add(state.Equipment);
        }
        if (expired == null) return;
        foreach (var equipment in expired) Revert(equipment, out _);
    }

    /// <summary>Whether one instance currently carries an override, for a caller that holds the reference and
    /// wants the ledger's own answer rather than a copy of its table.</summary>
    public bool IsOverridden(EntityReference equipment)
    {
        Sweep();
        return _byEquipment.ContainsKey(equipment?.Id ?? "");
    }

    /// <summary>Expired entries are dropped on every read, so the table a caller sees is never larger than the
    /// set of overrides that are still true. Sweeping without a tick only drops entries that were already
    /// expired when they were written, which cannot happen — the write path refuses those — so a read with no
    /// clock in hand is the no-op it should be rather than an arbitrary clock reading.</summary>
    private void Sweep() => Sweep(0);

    /// <summary>The merged field set of one request against one instance: a field the request names replaces the
    /// stored value when the request's source is at least the stored writer's, and a field it does not name is
    /// left alone. Equal sources mean a replay of the same write, which is a replace and not a refusal: a plan
    /// that dispatches the same node twice asked for the same value twice.</summary>
    private static IReadOnlyList<WeaponOverrideField> Merge(WeaponOverrideState state, WeaponOverrideRequest request)
    {
        var merged = new List<WeaponOverrideField>(state.mutable);
        foreach (var field in request.Fields)
        {
            if (!state.writers.TryGetValue(field.Name, out var writer) || writer.CompareTo(request.Source) <= 0)
            {
                var existing = Index(merged, field.Name);
                if (existing >= 0) merged[existing] = field;
                else merged.Add(field);
            }
        }
        return merged;
    }

    private static int Index(List<WeaponOverrideField> fields, string name)
    {
        for (var i = 0; i < fields.Count; i++)
            if (string.Equals(fields[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }
}
