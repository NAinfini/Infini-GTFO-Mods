using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>The two `forge.action.combat.attribute_*` handlers for `gtfo.player` and the
/// `forge.action.player.movement_profile` preset that writes the same table: native modifications written per
/// recipient through `AgentModifierManager.AddSyncedModifierValue`, and the revoke that names what was written.
/// The provider owns the `AgentModifier` table, so this adapter is the one place that writes it and the one place
/// that keeps the modification ids it must later release. One movement preset is two members of that table under
/// one effect handle, never a second write path beside this one.
///
/// What the native entry can and cannot say is why the shape is what it is. `AddSyncedModifierValue(Agent,
/// AgentModifier, float value, float deltaPerSec)` takes one signed contribution and a decay rate and returns the
/// modification id it registered; there is no setter for an absolute aggregate, no ordering and no rate channel
/// on this row's ports, so `set`, `add` and `subtract` all land as one signed contribution (`subtract` submits the
/// negated amount) and the id the entry returned is the whole of what it confirms. A zero return means no
/// modification was registered, which is a refusal by name — never a retried or invented id.
///
/// The id, not the handle, is what the native clear takes, so the ledger is the provider's own: modification id →
/// the life it was written on, the attribute member, the submission and the tick it expires at. The handle a
/// command's modifications are filed under is the kernel's own effect handle — the step's `effect` block is what
/// gave it a duration and a cancellation — so `attribute_remove` can be asked for exactly what an
/// `attribute_apply` handed out, and the kernel calls `RestoreApply` back when that effect ends.
///
/// Three properties of the native path are recorded rather than assumed, and all three are the game-internal
/// confirmation items `ForgeMap/evidence/agent-modifier-hooks.json` lists: which side of `AddSyncedModifierValue`
/// sends the sync packet, what a zero return means beyond "no id came back", and whether two submissions of the
/// same `(agent, modifier)` pair accumulate. The implementation lands the ruled behaviour (host applies, zero is
/// a refusal) and does not fall back to a Forge-side imitation of any of them.
///
/// Cleanup is driven by the kernel's own lifecycle because nothing else tells a provider that a life ended or a
/// world did: `WorldChanged` clears the ledger and the ids it still holds, `TickAdvanced` drops what belongs to a
/// life the player identity no longer resolves, and the kernel's restore callback revokes a single effect's
/// modifications. A lifecycle callback may not mutate the kernel, so the sweep asks the player identity whether a
/// life is current instead of the kernel's entity check: both answer about the same table, and the identity is
/// this package's own half of it.</summary>
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
    /// <summary>The movement preset's own refusal: `jump_gravity` has no member in the native modification table,
    /// so a request that carries it cannot be served as asked and is refused by name rather than served without
    /// it.</summary>
    internal const string GravityCode = "jump-gravity-unsupported";
    /// <summary>The movement preset's own `duration` port: the preset is not a rule an `effect` block times, so it
    /// keeps the port and the tick pass that releases what came due.</summary>
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
    /// the three declared rows.</summary>
    internal static readonly string[] RefusalCodes =
    {
        AuthorityCode, KindCode, StaleCode, AttributeCode, NoOpAttributeCode, OperationCode, AmountCode,
        DurationCode, TargetsCode, WriteBudgetCode, HandleBudgetCode, IdExhaustedCode, CommitExceptionCode,
        ClearExceptionCode, AfterUnknownCode, HandleMissingCode, HandleStaleCode, AttributeMismatchCode,
        AllRejectedCode, AllUnknownCode, GravityCode
    };

    /// <summary>The attribute a movement preset's group answers with. A preset is two native modifiers, so a
    /// `remove` narrowed to one `agent_modifier` member never covers it — the name is deliberately outside the
    /// table those requests are validated against, and the whole preset is released by an unnarrowed remove, by
    /// the tick its own `duration` port expires at, or by the world's end.</summary>
    private const string ProfileAttribute = "movement-profile";

    /// <summary>One row of the apply result, in the canonical row's own columns: the four fixed columns first,
    /// then this row's `amount` and the command's `target_count`. `amount` is the signed contribution the entry
    /// accepted; the native call returns no readback of what the modification table holds, and reporting a total
    /// it never answered would be a different claim.</summary>
    internal sealed record ApplyRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code,
        [property: JsonPropertyName("amount")] double Amount,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>One row of the movement preset result, in its own row's columns: the four fixed columns first,
    /// then the speed multiplier the command asked for and the command's `target_count`.</summary>
    internal sealed record ProfileRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code,
        [property: JsonPropertyName("speed")] double Speed,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>One row of the remove result: the same fixed columns, the life the modification belonged to as
    /// `target`, and the number of modifications the request named.</summary>
    internal sealed record RemoveRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>The kernel's own identity of a handle value: the world, the generation, the pool slot and the
    /// creating provider. It is read so a request handle can be matched against the one the kernel cast, which is
    /// the only key a provider has: the kernel's handle table is private and `TryNative` answers only for a handle
    /// that carries a native object, while a handle registered here would be dropped by the kernel's own lifetime
    /// sweep — no provider in this package registers the native object resolver that sweep asks, so a `PlayerAgent`
    /// would answer "no current life" and release a live handle.
    ///
    /// The default value is the "no handle" marker, because the epoch a real handle carries starts at one: the
    /// first world the kernel begins is world one, and the first handle it casts is cast in it.</summary>
    private readonly record struct HandleKey(long WorldEpoch, long LifeEpoch, int Local, int Provider)
    {
        internal bool IsHandle => WorldEpoch != 0;

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
    /// expires, and the effect handle the command's own card filed it under.</summary>
    private sealed class Applied
    {
        internal uint Id;
        internal EntityReference Target = new("", 0, 0);
        /// <summary>The provider's own expiry, which only the movement preset sets: the sourced-modifier row's
        /// duration belongs to the kernel's effect lifecycle, so its entries carry none.</summary>
        internal long ExpiryTick = -1;
        internal HandleKey Handle;
        /// <summary>Whether a native clear failure for this id was already reported, so a failing clear is one
        /// diagnostic instead of one per tick while the id stays in the ledger.</summary>
        internal bool Reported;
    }

    /// <summary>The modifications one command wrote, under the effect handle the kernel cast for that step. A
    /// single attribute row groups one member; a movement preset groups the two members it wrote, under the name
    /// no single-attribute request can name.</summary>
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

    /// <summary>The `forge.action.player.movement_profile` entry point the registration's handler table holds. The
    /// preset writes the same native table the two sourced-modifier rows above write, so it is the same adapter
    /// that answers it and not a second write path.</summary>
    internal static CommandResult ProfileHandler(CommandContext context)
        => Current is { } adapter ? adapter.Profile(context) : CommandResult.Rejected(AuthorityCode);

    /// <summary>The `attribute_apply` row's own restore callback, which the kernel calls when the effect its card
    /// asked for ends — by its duration, by a cancellation, by a released plan or by the world. What ends is every
    /// modification that command filed under that handle, and a handle the module has already released through its
    /// own removal action is the no-op it has to be: the kernel keeps the instance until its duration runs out, and
    /// a restore that found nothing to undo is not a failure.</summary>
    internal static void RestoreApply(RuntimeEffectContext context)
    {
        if (Current is not { } adapter || !HandleKey.TryRead(context.Handle, out var key)) return;
        adapter.ReleaseGroup(key, "effect-ended");
    }

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
    /// the table, a no-op member, an unbounded or zero amount and an over-wide recipient set are all refused by
    /// name, and a command whose effect handle cannot be read writes nothing.
    ///
    /// The effect's handle is the kernel's, not this provider's: the plan's own `effect` block is what gave the
    /// step a duration, a layer count and a cancellation, and the same handle is what the kernel hands back in the
    /// restore callback below. A step with no `effect` block has no handle at all, and a modification nothing can
    /// name lasts until it is removed, its life ends or the world does.</summary>
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
        var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        // One row is written per recipient; the result budget is the row budget.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        // `subtract` is the same contribution with the sign the native modifier table reads as its opposite.
        double submitted = operation == "subtract" ? -amount : amount;

        // The kernel's handle is what this command's modifications are filed under, because it is what the kernel
        // calls back with when the effect ends. A value the kernel did not cast for this step — a card that asked
        // for no effect has none — leaves the group unnamed; the modifications are still in the ledger and the
        // sweep, the removal that names them and the world's own end all release them.
        HandleKey key = default;
        var named = context.EffectHandle is { } effectHandle && HandleKey.TryRead(effectHandle, out key) && key.IsHandle;

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
                // The instance's clock is the kernel's: this ledger keeps no expiry for a row whose duration the
                // plan's `effect` block owns, and the module is called back when that clock runs out.
                ExpiryTick = -1,
                Handle = key
            };
            _entries.Add(id, entry);
            written.Add(entry);
            rows.Add(Row("committed", CommitStates.Confirmed, CommittedCode, submitted));
            committed++;
        }

        // The handle is not this row's output: the kernel publishes the effect handle its card asked for into
        // this capability's own `modifier` port, so a module that returned one would be a second writer of it.
        JsonElement outputs = RuntimeJson.From(new { results = rows });
        if (named && written.Count != 0)
        {
            var group = new Group { Key = key, Attribute = attribute! };
            foreach (var entry in written) group.Ids.Add(entry.Id);
            _groups.Add(key, group);
        }
        return Aggregate(committed, rejected, unknown, rows.Select(row => row.Code).ToArray(), outputs);
    }

    /// <summary>The `forge.action.player.movement_profile` handler. The two modifiers the native table carries for
    /// movement are written as one preset under one effect handle, because a command that set only half a profile
    /// is not the state the plan asked for: both amounts and the lifetime are checked once, before the first
    /// recipient, the handle is minted before the first write, and a recipient whose second write the native entry
    /// refuses has its first one revoked through the same clear the expiry path uses. `jump_gravity` is refused by
    /// name before any of that, because the table has no member for it.</summary>
    internal CommandResult Profile(CommandContext context)
    {
        CheckThread();
        // The identity half owns the readiness, authority and fault gate, for the same reason the two rows above
        // route through it: a preset written where the same half refuses to read is a write nothing can observe.
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected(AuthorityCode);
        // `source` carries no faction or targeting restriction on this row either; it is still a required,
        // kernel-validated entity reference.
        _ = context.GetEntityInput("source");
        if (context.Inputs.TryGetProperty("jump_gravity", out var gravity)
            && gravity.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return CommandResult.Rejected(GravityCode);
        if (!Multiplier(context, "speed", out double speed)
            || !Multiplier(context, "acceleration", out double acceleration))
            return CommandResult.Rejected(AmountCode);
        long? expiry = null;
        if (context.Inputs.TryGetProperty("duration", out var durationValue) && durationValue.ValueKind != JsonValueKind.Null)
        {
            if (durationValue.ValueKind != JsonValueKind.Number || !durationValue.TryGetInt64(out var ticks)
                || ticks < 0 || context.SimulationTick > long.MaxValue - ticks)
                return CommandResult.Rejected(DurationCode);
            // Zero is no duration, the way the two rows above read the same port: a preset with no caller-set
            // lifetime lasts until it is removed, its life ends or the world does.
            if (ticks > 0) expiry = context.SimulationTick + ticks;
        }
        var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);

        // The preset's own effect handle is minted before the first write so a command that cannot name its effect
        // writes nothing: the handle is what a later remove, cancel or expiry holds, and an unnamed preset would
        // only be reachable by its life's end or the world's.
        JsonElement handle;
        try { handle = _registration.CreateEffectHandle("entity_life"); }
        catch (RuntimeContractException) { return CommandResult.Rejected(HandleBudgetCode); }
        if (!HandleKey.TryRead(handle, out var key)) return CommandResult.Rejected(HandleBudgetCode);
        _registration.RegisterCancel(handle, () => ReleaseGroup(key, "handle-cancelled"));

        var rows = new List<ProfileRow>(targets.Length);
        var written = new List<Applied>(targets.Length * 2);
        int committed = 0, rejected = 0, unknown = 0;
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            ProfileRow Row(string status, string state, string code)
                => new(target, status, state, code, speed, targets.Length);

            if (stopCommitting) { rows.Add(Row("rejected", CommitStates.None, AfterUnknownCode)); rejected++; continue; }
            if (!players.CanCommit) { rows.Add(Row("rejected", CommitStates.None, AuthorityCode)); rejected++; continue; }
            if (!IsKind(target, PlayerKind)) { rows.Add(Row("rejected", CommitStates.None, KindCode)); rejected++; continue; }
            var agent = players.CurrentAgent(target);
            if (agent == null) { rows.Add(Row("rejected", CommitStates.None, StaleCode)); rejected++; continue; }
            // A preset is two native modifications wherever the single-attribute row spends one, so the budget is
            // asked for twice and a recipient that finds it short writes neither.
            if (!TryReserveWrite(context.SimulationTick) || !TryReserveWrite(context.SimulationTick))
            { rows.Add(Row("rejected", CommitStates.None, WriteBudgetCode)); rejected++; continue; }

            var pair = new List<Applied>(2);
            string? failure = null;
            foreach (var (modifier, amount) in Preset(speed, acceleration))
            {
                uint id;
                try { id = AgentModifierManager.AddSyncedModifierValue(agent, modifier, (float)amount, 0f); }
                catch (Exception error)
                {
                    // The call entered the native modification path; whether it had already registered an id is
                    // not observable from here, so the commit stays unknown, whatever the pair already wrote stays
                    // in the ledger, and the remaining recipients are not retried.
                    _report("map.movement-profile-commit-exception: " + error.GetType().Name);
                    failure = CommitExceptionCode;
                    break;
                }
                // A zero answer is the native entry registering nothing. A preset the second write refuses is not
                // a preset, so the write that did land is revoked and the row reports the refusal.
                if (id == 0)
                {
                    if (pair.Count != 0) Clear(pair.Select(entry => entry.Id).ToList(), "movement-profile-pair-refused");
                    pair.Clear();
                    failure = IdExhaustedCode;
                    break;
                }
                var entry = new Applied { Id = id, Target = target, ExpiryTick = expiry ?? -1, Handle = key };
                _entries.Add(id, entry);
                pair.Add(entry);
            }
            if (failure == null)
            {
                written.AddRange(pair);
                rows.Add(Row("committed", CommitStates.Confirmed, CommittedCode));
                committed++;
                continue;
            }
            if (pair.Count == 0)
            {
                rows.Add(Row("rejected", CommitStates.None, failure));
                rejected++;
                continue;
            }
            // Half a preset the native entry stopped on: the pair stays in the ledger and the handle's group, so
            // a remove, a cancel, the expiry or the world's end still releases what did land.
            written.AddRange(pair);
            unknown++;
            stopCommitting = true;
            rows.Add(Row("unknown", CommitStates.Unknown, failure));
        }

        // Only a command that wrote something has an effect to name: the handle of a fully refused command is
        // left out of the frame instead of naming an empty group.
        JsonElement outputs = written.Count == 0
            ? RuntimeJson.From(new { results = rows })
            : RuntimeJson.From(new { results = rows, profile_handle = handle });
        if (written.Count != 0)
        {
            var group = new Group { Key = key, Attribute = ProfileAttribute };
            foreach (var entry in written) group.Ids.Add(entry.Id);
            _groups.Add(key, group);
        }
        return Aggregate(committed, rejected, unknown, rows.Select(row => row.Code).ToArray(), outputs);
    }

    /// <summary>The `forge.action.combat.attribute_remove` handler: the immediate removal, which ends what an
    /// apply handed out right here instead of waiting for the effect's own clock. The request names the handles —
    /// the effect handle collection, optionally narrowed to one attribute — and every modification the handles
    /// cover is released through the same native clear the restore and the expiry paths use. A request that names
    /// a handle this provider no longer holds, or an attribute one of them was not written under, is refused as a
    /// whole before anything is cleared.
    ///
    /// The kernel's effect instance is not ended here: it belongs to the plan's own card and its `cancel` handle,
    /// and it stays until its duration runs out or its handle is cancelled. By then the group is gone, so the
    /// restore callback this adapter answers is the no-op an already-undone effect has to be.</summary>
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

    /// <summary>The end of one effect: every modification filed under the handle is released through the same
    /// native clear the removal and the expiry paths use, and nothing else is. A handle no group answers for is
    /// the no-op of an effect the module already undid — which is also what the movement preset's own cancel hook
    /// needs, since a plan may cancel the handle that preset returned.</summary>
    private void ReleaseGroup(HandleKey key, string reason)
    {
        if (!_groups.TryGetValue(key, out var group)) return;
        _due.Clear();
        _due.AddRange(group.Ids);
        Clear(_due, reason);
    }

    /// <summary>Whether this adapter holds modifications under one handle — the question a caller asks to tell a
    /// command whose effect is still live from one it never filed.</summary>
    internal bool HoldsGroup(JsonElement handle)
        => HandleKey.TryRead(handle, out var key) && _groups.ContainsKey(key);

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

    /// <summary>The two native writes one movement preset is: the speed and the acceleration multiplier, in the
    /// order the row names them. The table carries no third member for the row's `jump_gravity` input.</summary>
    private static (AgentModifier Modifier, double Amount)[] Preset(double speed, double acceleration)
        => new[] { (AgentModifier.MovementSpeed, speed), (AgentModifier.MovementAcceleration, acceleration) };

    /// <summary>One of the preset's two multiplier inputs: a finite, non-zero number within the magnitude the
    /// native entry can carry. A port that is absent or not a number is refused the same way an out-of-range one
    /// is, because a preset with one of its two values missing is not the profile the row declares.</summary>
    private static bool Multiplier(CommandContext context, string port, out double amount)
    {
        amount = 0;
        if (!context.Inputs.TryGetProperty(port, out var value) || value.ValueKind != JsonValueKind.Number) return false;
        amount = value.GetDouble();
        return double.IsFinite(amount) && amount != 0 && Math.Abs(amount) <= MaximumAmount;
    }

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
