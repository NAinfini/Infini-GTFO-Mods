using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>The two `forge.action.combat.attribute_*` handlers for `gtfo.player`: one native modification written
/// per recipient through `AgentModifierManager.AddSyncedModifierValue`, and the revoke that names what was
/// written. The provider owns the `AgentModifier` table, so this adapter is the one place that writes it and the
/// one place that keeps the modification ids it must later release.
///
/// What the native entry can and cannot say is why the shape is what it is. `AddSyncedModifierValue(Agent,
/// AgentModifier, float value, float deltaPerSec)` takes one signed contribution and a decay rate and returns the
/// modification id it registered; there is no setter for an absolute aggregate, no ordering and no rate channel
/// on this row's ports, so `set`, `add` and `subtract` all land as one signed contribution (`subtract` submits the
/// negated amount) and the id the entry returned is the whole of what it confirms. A zero return means no
/// modification was registered, which is a refusal by name — never a retried or invented id.
///
/// The id, not the handle, is what the native clear takes, so the ledger is the provider's own: modification id →
/// the life it was written on, the attribute member, the submission and the tick it expires at. A command's
/// handle is the provider's own effect handle, minted once for the command and kept beside the ids it covers, so
/// `attribute_remove` can be asked for exactly what an `attribute_apply` handed out.
///
/// Three properties of the native path are recorded rather than assumed, and all three are the game-internal
/// confirmation items `ForgeMap/evidence/agent-modifier-hooks.json` lists: which side of `AddSyncedModifierValue`
/// sends the sync packet, what a zero return means beyond "no id came back", and whether two submissions of the
/// same `(agent, modifier)` pair accumulate. The implementation lands the ruled behaviour (host applies, zero is
/// a refusal) and does not fall back to a Forge-side imitation of any of them.
///
/// Cleanup is driven by the kernel's own lifecycle because nothing else tells a provider that a life ended or a
/// world did: `WorldChanged` clears the ledger and the ids it still holds, `TickAdvanced` expires what came due
/// and drops what belongs to a life the player identity no longer resolves, and the handle's own cancel hook
/// revokes a single command's effect. A lifecycle callback may not mutate the kernel, so the sweep asks the
/// player identity whether a life is current instead of the kernel's entity check: both answer about the same
/// table, and the identity is this package's own half of it.</summary>
internal sealed class AgentModifierAdapter : IDisposable
{
    /// <summary>The design's own ceiling on native modification writes per host tick, shared by every command
    /// that applies one: a command that finds the budget spent writes nothing and says so per recipient.</summary>
    internal const int MaximumModifierWritesPerTick = 32;
    /// <summary>The largest magnitude one submission may carry. The native entry takes a float and applies no
    /// bound of its own, so a value no modifier table would ever hold is refused here instead of being written.</summary>
    internal const double MaximumAmount = 1000000;
    /// <summary>The one entity kind this action accepts, named the way the identity half names it.</summary>
    internal const string PlayerKind = PlayerIdentityModule.EntityKind;

    internal const string AuthorityCode = "authority-or-phase";
    internal const string KindCode = "modifier-target-kind";
    internal const string StaleCode = "stale-or-unsupported-recipient";
    internal const string AttributeCode = "attribute-unknown";
    internal const string NoOpAttributeCode = "attribute-no-op";
    internal const string OperationCode = "operation-unsupported";
    internal const string AmountCode = "amount-out-of-range";
    internal const string DurationCode = "duration-out-of-range";
    internal const string TargetsCode = "too-many-targets";
    internal const string WriteBudgetCode = "modifier-budget";
    internal const string HandleBudgetCode = "handle-budget";
    internal const string IdExhaustedCode = "modifier-id-exhausted";
    internal const string CommitExceptionCode = "native-commit-exception";
    internal const string ClearExceptionCode = "native-clear-exception";
    internal const string AfterUnknownCode = "not-attempted-after-unknown-commit";
    internal const string HandleMissingCode = "modifier-handle-missing";
    internal const string HandleStaleCode = "stale-handle";
    internal const string AttributeMismatchCode = "modifier-attribute-mismatch";
    internal const string AllRejectedCode = "attribute-all-rejected";
    internal const string AllUnknownCode = "attribute-all-unknown";
    private const string CommittedCode = "committed";

    /// <summary>Every code this adapter can answer a row with besides `committed`, gathered in one place so the
    /// capability's own `codes` list and the handler cannot drift apart: the focused suite checks this set against
    /// the two declared rows.</summary>
    internal static readonly string[] RefusalCodes =
    {
        AuthorityCode, KindCode, StaleCode, AttributeCode, NoOpAttributeCode, OperationCode, AmountCode,
        DurationCode, TargetsCode, WriteBudgetCode, HandleBudgetCode, IdExhaustedCode, CommitExceptionCode,
        ClearExceptionCode, AfterUnknownCode, HandleMissingCode, HandleStaleCode, AttributeMismatchCode,
        AllRejectedCode, AllUnknownCode
    };

    /// <summary>One row of the apply result, in the canonical row's own columns: the four fixed columns first,
    /// then this row's `amount` and the command's `target_count`. `amount` is the signed contribution the entry
    /// accepted; the native call returns no readback of what the modification table holds, and reporting a total
    /// it never answered would be a different claim.</summary>
    internal sealed record ApplyRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code,
        [property: JsonPropertyName("amount")] double Amount,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>One row of the remove result: the same fixed columns, the life the modification belonged to as
    /// `target`, and the number of modifications the request named.</summary>
    internal sealed record RemoveRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>The kernel's own identity of a handle value: the world, the generation, the pool slot and the
    /// creating provider. It is read so a request handle can be matched against the one this provider minted,
    /// which is the only key a provider has: the kernel's handle table is private and `TryNative` answers only
    /// for a handle that carries a native object, while a handle registered here would be dropped by the
    /// kernel's own lifetime sweep — no provider in this package registers the native object resolver that
    /// sweep asks, so a `PlayerAgent` would answer "no current life" and release a live handle.</summary>
    private readonly record struct HandleKey(long WorldEpoch, long LifeEpoch, int Local, int Provider)
    {
        internal static bool TryRead(JsonElement value, out HandleKey key)
        {
            key = default;
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("worldEpoch", out var world) || !world.TryGetInt64(out var worldEpoch)
                || !value.TryGetProperty("lifeEpoch", out var life) || !life.TryGetInt64(out var lifeEpoch)
                || !value.TryGetProperty("local", out var local) || !local.TryGetInt32(out var slot)
                || !value.TryGetProperty("provider", out var provider) || !provider.TryGetInt32(out var index))
                return false;
            key = new HandleKey(worldEpoch, lifeEpoch, slot, index);
            return true;
        }
    }

    /// <summary>One live modification: what the native entry returned, the life it was written on, when it
    /// expires, and the handle the command it belongs to minted.</summary>
    private sealed class Applied
    {
        internal uint Id;
        internal EntityReference Target = new("", 0, 0);
        internal long ExpiryTick = -1;
        internal HandleKey Handle;
        /// <summary>Whether a native clear failure for this id was already reported, so a failing clear is one
        /// diagnostic instead of one per tick while the id stays in the ledger.</summary>
        internal bool Reported;
    }

    /// <summary>The modifications one apply command wrote, under the effect handle that command returned.</summary>
    private sealed class Group
    {
        internal HandleKey Key;
        internal string Attribute = "";
        internal readonly List<uint> Ids = new();
    }

    private readonly RuntimeModuleHandle _registration;
    private readonly Action<string> _report;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<uint, Applied> _entries = new();
    private readonly Dictionary<HandleKey, Group> _groups = new();
    // Reused sweep scratch: the sweep runs every tick, and a tick with nothing due allocates nothing.
    private readonly List<uint> _due = new();
    private readonly List<uint> _pending = new();
    private readonly List<EntityReference> _lost = new();
    private readonly List<Group> _resolved = new();
    private RuntimeLifecycleSubscription? _lifecycle;
    private long _writeTick = -1;
    private int _writesThisTick;
    private long _clearFailures;
    private bool _disposed;

    /// <summary>The one adapter attached to the one Map registration, or null while none is. The kernel's handler
    /// table is part of the registration, which is built before the registration handle — and therefore before the
    /// adapter that mints handles on it — exists, so the table holds the two static entry points below and they
    /// answer from here. One package registers at most one provider of one identity namespace, which is the same
    /// one-provider-per-process shape <see cref="PlayerIdentityModule.Current"/> already has.</summary>
    internal static AgentModifierAdapter? Current { get; private set; }

    /// <summary>The `attribute_apply` entry point the registration's handler table holds.</summary>
    internal static CommandResult ApplyHandler(CommandContext context)
        => Current is { } adapter ? adapter.Apply(context) : CommandResult.Rejected(AuthorityCode);

    /// <summary>The `attribute_remove` entry point the registration's handler table holds.</summary>
    internal static CommandResult RemoveHandler(CommandContext context)
        => Current is { } adapter ? adapter.Remove(context) : CommandResult.Rejected(AuthorityCode);

    /// <summary>The adapter subscribes on the registration it is handed, because a lifecycle observer is the one
    /// per-tick and per-world notification a provider gets without the kernel calling into it. The subscription
    /// needs a registration that exists, so the adapter is created after `RegisterModule` returned — the kernel
    /// accepts a subscription outside the registration window, though not inside a dispatch.</summary>
    internal AgentModifierAdapter(RuntimeModuleHandle registration, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _lifecycle = _registration.ObserveLifecycle(OnLifecycle);
        Current = this;
    }

    /// <summary>The modifications this provider still holds an id for, as the diagnostics the session reports
    /// and the focused tests read. It is the ledger's own size, not a claim about the native table.</summary>
    internal int LiveModifiers => _entries.Count;

    /// <summary>Native clear calls that threw since the last world began, reported once at the end of a cleanup
    /// pass that had one.</summary>
    internal long ClearFailures => _clearFailures;

    /// <summary>The `forge.action.combat.attribute_apply` handler. The whole request check runs before the first
    /// recipient, because a command that cannot be carried out as asked must not half-apply: an attribute outside
    /// the table, a no-op member, an unbounded or zero amount, a negative lifetime and an over-wide recipient set
    /// are all refused by name, and a command that cannot mint the handle naming its own effect writes nothing.</summary>
    internal CommandResult Apply(CommandContext context)
    {
        CheckThread();
        // The identity half owns the readiness, authority and fault gate: a command that reaches here while it
        // says no would be writing where the same half refuses to read.
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected(AuthorityCode);
        // `source` carries no faction or targeting restriction on this row (self, teammate and hostile are all
        // valid sources); it is still a required, kernel-validated entity reference.
        _ = context.GetEntityInput("source");
        // The kernel hands an enum port over as its member name, and it validated the index against the shared
        // `agent_modifier` set before that; the name is what this table answers for.
        var attribute = context.Inputs.TryGetProperty("attribute", out var attributeValue)
            && attributeValue.ValueKind == JsonValueKind.String ? attributeValue.GetString() : null;
        if (!AgentModifierValues.TryParse(attribute, out var modifier)) return CommandResult.Rejected(AttributeCode);
        // `None` is a member of the native enum but not an attribute anything can carry: applying it would
        // register an id that contributes nothing, which is why the ruled shape drops it from the shared set.
        if (modifier == AgentModifier.None) return CommandResult.Rejected(NoOpAttributeCode);
        var operation = context.Parameters.TryGetProperty("operation", out var operationValue)
            && operationValue.ValueKind == JsonValueKind.String ? operationValue.GetString() : null;
        if (operation is not ("set" or "add" or "subtract")) return CommandResult.Rejected(OperationCode);
        double amount = context.Inputs.TryGetProperty("amount", out var amountValue)
            && amountValue.ValueKind == JsonValueKind.Number ? amountValue.GetDouble() : double.NaN;
        if (!double.IsFinite(amount) || amount == 0 || Math.Abs(amount) > MaximumAmount)
            return CommandResult.Rejected(AmountCode);
        long? expiry = null;
        if (context.Inputs.TryGetProperty("duration", out var durationValue) && durationValue.ValueKind != JsonValueKind.Null)
        {
            if (durationValue.ValueKind != JsonValueKind.Number || !durationValue.TryGetInt64(out var ticks)
                || ticks < 0 || context.SimulationTick > long.MaxValue - ticks)
                return CommandResult.Rejected(DurationCode);
            // Zero is no duration, the way the other actions read the same port: a modification with no
            // caller-set lifetime lasts until it is removed, its life ends or the world does.
            if (ticks > 0) expiry = context.SimulationTick + ticks;
        }
        var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        // One row is written per recipient; the result budget is the row budget.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        // `subtract` is the same contribution with the sign the native modifier table reads as its opposite.
        double submitted = operation == "subtract" ? -amount : amount;

        // The effect handle is minted before the first write so a command that cannot name its effect does not
        // create one: the handle is what a later remove or cancel holds, and an unnamed modification would only
        // be reachable by expiry.
        JsonElement handle;
        try { handle = _registration.CreateEffectHandle("entity_life"); }
        catch (RuntimeContractException) { return CommandResult.Rejected(HandleBudgetCode); }
        if (!HandleKey.TryRead(handle, out var key)) return CommandResult.Rejected(HandleBudgetCode);
        _registration.RegisterCancel(handle, () => CancelGroup(key));

        var rows = new List<ApplyRow>(targets.Length);
        var written = new List<Applied>(targets.Length);
        int committed = 0, rejected = 0, unknown = 0;
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            ApplyRow Row(string status, string state, string code, double reported = 0)
                => new(target, status, state, code, reported, targets.Length);

            if (stopCommitting) { rows.Add(Row("rejected", CommitStates.None, AfterUnknownCode)); rejected++; continue; }
            if (!players.CanCommit) { rows.Add(Row("rejected", CommitStates.None, AuthorityCode)); rejected++; continue; }
            // Only the kind this action names. A `gtfo.enemy`, a map object or an unknown kind is refused by name
            // before any native read, so the refusal says what it is instead of reporting a dead player.
            if (!IsKind(target, PlayerKind)) { rows.Add(Row("rejected", CommitStates.None, KindCode)); rejected++; continue; }
            var agent = players.CurrentAgent(target);
            if (agent == null) { rows.Add(Row("rejected", CommitStates.None, StaleCode)); rejected++; continue; }
            if (!TryReserveWrite(context.SimulationTick))
            { rows.Add(Row("rejected", CommitStates.None, WriteBudgetCode)); rejected++; continue; }

            uint id;
            try { id = AgentModifierManager.AddSyncedModifierValue(agent, modifier, (float)submitted, 0f); }
            catch (Exception error)
            {
                // The call entered the native modification path; whether it had already registered an id is not
                // observable from here, so the commit stays unknown and the remaining recipients are not retried.
                unknown++;
                stopCommitting = true;
                _report("map.attribute-apply-commit-exception: " + error.GetType().Name);
                rows.Add(Row("unknown", CommitStates.Unknown, CommitExceptionCode));
                continue;
            }
            // A zero answer is the native entry registering nothing; there is no id to release and none to invent.
            if (id == 0) { rows.Add(Row("rejected", CommitStates.None, IdExhaustedCode)); rejected++; continue; }

            var entry = new Applied
            {
                Id = id,
                Target = target,
                ExpiryTick = expiry ?? -1,
                Handle = key
            };
            _entries.Add(id, entry);
            written.Add(entry);
            rows.Add(Row("committed", CommitStates.Confirmed, CommittedCode, submitted));
            committed++;
        }

        // Only a command that wrote something has an effect to name: the handle of a fully refused command is
        // left out of the frame instead of naming an empty group.
        JsonElement outputs = written.Count == 0
            ? RuntimeJson.From(new { results = rows })
            : RuntimeJson.From(new { results = rows, modifier = handle });
        if (written.Count != 0)
        {
            var group = new Group { Key = key, Attribute = attribute! };
            foreach (var entry in written) group.Ids.Add(entry.Id);
            _groups.Add(key, group);
        }
        return Aggregate(committed, rejected, unknown, rows.Select(row => row.Code).ToArray(), outputs);
    }

    /// <summary>The `forge.action.combat.attribute_remove` handler. The request names what an apply handed out —
    /// the effect handle collection, optionally narrowed to one attribute — and every modification the handles
    /// cover is released through the same native clear the expiry path uses. A request that names a handle this
    /// provider no longer holds, or an attribute one of them was not written under, is refused as a whole before
    /// anything is cleared.</summary>
    internal CommandResult Remove(CommandContext context)
    {
        CheckThread();
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected(AuthorityCode);
        var attribute = context.Inputs.TryGetProperty("attribute", out var attributeValue)
            && attributeValue.ValueKind == JsonValueKind.String ? attributeValue.GetString() : null;
        if (attribute != null && !AgentModifierValues.TryParse(attribute, out _))
            return CommandResult.Rejected(AttributeCode);
        if (!context.Inputs.TryGetProperty("modifiers", out var modifiers)
            || modifiers.ValueKind != JsonValueKind.Array || modifiers.GetArrayLength() == 0)
            return CommandResult.Rejected(HandleMissingCode);

        _resolved.Clear();
        foreach (var value in modifiers.EnumerateArray())
        {
            if (!HandleKey.TryRead(value, out var key) || !_groups.TryGetValue(key, out var group))
                return CommandResult.Rejected(HandleStaleCode);
            if (attribute != null && attribute != group.Attribute) return CommandResult.Rejected(AttributeMismatchCode);
            if (!_resolved.Contains(group)) _resolved.Add(group);
        }
        _pending.Clear();
        foreach (var group in _resolved)
            foreach (var id in group.Ids)
                if (_entries.ContainsKey(id) && !_pending.Contains(id)) _pending.Add(id);
        if (_pending.Count > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);

        var rows = new List<RemoveRow>(_pending.Count);
        int removed = 0, rejected = 0, unknown = 0;
        bool stopClearing = false;
        foreach (var id in _pending)
        {
            if (!_entries.TryGetValue(id, out var entry)) continue;
            RemoveRow Row(string status, string state, string code)
                => new(entry.Target, status, state, code, _pending.Count);

            if (stopClearing) { rows.Add(Row("rejected", CommitStates.None, AfterUnknownCode)); rejected++; continue; }
            if (!players.CanCommit) { rows.Add(Row("rejected", CommitStates.None, AuthorityCode)); rejected++; continue; }
            try { AgentModifierManager.ClearSyncedModifierChange(id); }
            catch (Exception error)
            {
                unknown++;
                stopClearing = true;
                _report("map.attribute-remove-clear-exception: " + error.GetType().Name);
                rows.Add(Row("unknown", CommitStates.Unknown, ClearExceptionCode));
                continue;
            }
            Forget(entry);
            rows.Add(Row("succeeded", CommitStates.Confirmed, CommittedCode));
            removed++;
        }
        var outputs = RuntimeJson.From(new { results = rows });
        return Aggregate(removed, rejected, unknown, rows.Select(row => row.Code).ToArray(), outputs);
    }

    /// <summary>The world-change and per-tick half. A world change clears everything the previous world's ledger
    /// still held, a failed or stopped runtime stops holding ids at all, and a tick expires what came due and
    /// drops what belongs to a life the identity no longer resolves. The kernel forbids a lifecycle observer from
    /// mutating it, so nothing here calls a kernel write path; the native clear and the provider's own ledger are
    /// not kernel state.</summary>
    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        try
        {
            if (value.Kind == RuntimeLifecycleKind.WorldChanged) { BeginWorld(); return; }
            if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            { EndWorld(); return; }
            if (value.Kind != RuntimeLifecycleKind.TickAdvanced) return;
            if (PlayerIdentityModule.Current is not { } players || !players.Authoritative) return;
            Sweep(value.Current.SimulationTick, players);
        }
        catch (Exception error)
        {
            // A throwing observer is removed by the kernel and latched as its own fault; the diagnostic names the
            // pass so the ledger's state is not silently abandoned.
            try { _report("map.attribute-modifier-sweep-failed: " + error.GetType().Name); }
            catch (Exception) { }
        }
    }

    private void Sweep(long tick, PlayerIdentityModule players)
    {
        _due.Clear();
        foreach (var entry in _entries.Values)
            if (entry.ExpiryTick >= 0 && entry.ExpiryTick <= tick) _due.Add(entry.Id);
        if (_due.Count != 0) Clear(_due, "expired");
        // A life that is no longer current takes its modifications with it; the reference is the identity's own
        // currency check, which is the same table the kernel's entity check routes to for this kind.
        _lost.Clear();
        foreach (var entry in _entries.Values)
            if (!players.IsCurrent(entry.Target) && !_lost.Contains(entry.Target)) _lost.Add(entry.Target);
        foreach (var target in _lost) OnEntityLost(target);
    }

    /// <summary>Releases every modification written on one life, which is what a despawn, a death that ended the
    /// life and the sweep's own currency check all reduce to.</summary>
    internal void OnEntityLost(EntityReference entity)
    {
        CheckThread();
        _due.Clear();
        foreach (var entry in _entries.Values) if (entry.Target == entity) _due.Add(entry.Id);
        if (_due.Count != 0) Clear(_due, "entity-lost");
    }

    /// <summary>Adopts a new world: the previous world's ids are released and the ledger starts empty. The world
    /// epoch is not kept here — every group's key already carries the world it was minted in, and the kernel's
    /// own handle table dies with that world, so a handle from the previous epoch resolves to nothing either
    /// way.</summary>
    internal void BeginWorld()
    {
        CheckThread();
        ClearAll("world-changed");
        _writeTick = -1;
        _writesThisTick = 0;
    }

    /// <summary>Stops holding ids: the runtime failed or stopped, so there is no world left to release them
    /// in and nothing may be written after it.</summary>
    internal void EndWorld()
    {
        CheckThread();
        ClearAll("world-ended");
        _writeTick = -1;
        _writesThisTick = 0;
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        _lifecycle = null;
        ClearAll("provider-disposed");
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>The effect handle's own cancel: a plan that cancels the handle an apply returned revokes exactly
    /// the modifications that command wrote, and nothing else.</summary>
    private void CancelGroup(HandleKey key)
    {
        if (!_groups.TryGetValue(key, out var group)) return;
        _due.Clear();
        _due.AddRange(group.Ids);
        Clear(_due, "handle-cancelled");
    }

    /// <summary>Releases the named ids through the native synced clear and forgets the ones that were released.
    /// An id whose clear threw stays in the ledger — with one diagnostic, not one per tick — so the world-end
    /// pass still tries it: dropping it here would leave a native modification nothing can ever name again.</summary>
    private void Clear(List<uint> ids, string reason)
    {
        foreach (var id in ids)
        {
            if (!_entries.TryGetValue(id, out var entry)) continue;
            try { AgentModifierManager.ClearSyncedModifierChange(id); }
            catch (Exception error)
            {
                _clearFailures++;
                if (!entry.Reported)
                {
                    entry.Reported = true;
                    _report("map.attribute-modifier-clear-failed " + reason + ": " + error.GetType().Name);
                }
                continue;
            }
            Forget(entry);
        }
    }

    private void ClearAll(string reason)
    {
        long before = _clearFailures;
        foreach (var entry in _entries.Values.ToArray())
        {
            try { AgentModifierManager.ClearSyncedModifierChange(entry.Id); }
            catch (Exception) { _clearFailures++; }
        }
        _entries.Clear();
        _groups.Clear();
        if (_clearFailures != before) _report("map.attribute-modifier-cleanup-incomplete " + reason
            + ": failures=" + (_clearFailures - before).ToString(CultureInfo.InvariantCulture));
    }

    private void Forget(Applied entry)
    {
        _entries.Remove(entry.Id);
        if (!_groups.TryGetValue(entry.Handle, out var group)) return;
        group.Ids.Remove(entry.Id);
        if (group.Ids.Count == 0) _groups.Remove(entry.Handle);
    }

    /// <summary>Whether this tick may still write. The budget is a provider counter, reset by each new tick, and
    /// a recipient that finds it spent is refused by name instead of silently skipped.</summary>
    private bool TryReserveWrite(long tick)
    {
        if (_writeTick != tick) { _writeTick = tick; _writesThisTick = 0; }
        if (_writesThisTick >= MaximumModifierWritesPerTick) return false;
        _writesThisTick++;
        return true;
    }

    private static bool IsKind(EntityReference reference, string kind)
        => reference.Id != null && reference.Id.StartsWith(kind + ":", StringComparison.Ordinal);

    /// <summary>The command-level conclusion of a run of rows, by the same rule the other combat actions use:
    /// everything confirmed is a success, nothing confirmed is a rejection or an unknown failure, and anything in
    /// between is partial with the weaker commit state. A row's own code is reported when every row agrees, and an
    /// empty recipient set commits nothing and succeeds, the way an empty heal does.</summary>
    private static CommandResult Aggregate(int committed, int rejected, int unknown, string[] codes, JsonElement outputs)
    {
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        var code = Single(codes, unknown > 0 ? AllUnknownCode : AllRejectedCode);
        return unknown == 0
            ? CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs)
            : CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, code, "", outputs);
    }

    private static string Single(string[] codes, string fallback)
    {
        if (codes.Length == 0) return fallback;
        if (codes.Length == 1) return codes[0];
        var first = codes[0];
        foreach (var code in codes) if (code != first) return fallback;
        return first;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Map attribute modifiers require the owning simulation thread.");
    }
}
