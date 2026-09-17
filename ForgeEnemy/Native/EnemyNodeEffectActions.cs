using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agents;
using Enemies;
using ForgeRuntime.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The three node-list actions this package's own native write paths carry:
/// `forge.action.enemy.kill`, `forge.action.enemy.mark` and `forge.action.enemy.target`.
///
/// Each row names exactly one native submission:
/// <list type="bullet">
/// <item>`kill` ends the target enemies' lives through `Dam_EnemyDamageBase.InstantDead(bool force)`, the game's
/// own unconditional end-of-life entry. The one thing the call cannot report is whether the life really ended,
/// so the commit is confirmed by reading the agent's own liveness back — a target that still reads alive is an
/// unknown commit, never a claimed one.</item>
/// <item>`mark` builds Forge's own `NavMarker` on the enemy's model object through `GuiManager.NavMarkerLayer`,
/// then colours it with `NavMarker.SetColor`. It is the one marking path in the build that accepts a colour and
/// a caller-set lifetime at all (`%TEMP%\nativeinv\ni-markcolor.md`, route A): the game's own BioTracker tag has
/// a one-field packet, so no colour ever crosses the wire, and the vanilla tag's red is left alone. The marker is
/// per-machine presentation, so the row is `execution: presentation` (ruling 122.3): the host decides the step,
/// and each addressed player runs this handler on its own machine, which is why the gate below is the
/// presentation one and the result claims no commit.</item>
/// <item>`target` points enemies at one player through the game's own target propagation
/// (`EnemyAgent.PropagateTargetFull`, or `PropagateTargetLimited` when a chance is asked for). The agent
/// propagated is the player entity's own native instance, resolved by the kind's registered owner through the
/// kernel, so this action never invents an agent for a reference another package minted.</item>
/// </list>
///
/// Every request is checked before the first target, because an action that cannot be carried out as asked must
/// not half-apply; a refusal names its own reason. `kill` and `target` gate on the module's own authority
/// (`CanExecute`: the registration, the startup state, the gameplay flag and the game's master flag), the same
/// gate every other host write in this package passes; `mark` gates on `CanPresent`, because the machine that
/// performs it is a recipient rather than the master.</summary>
internal sealed partial class EnemyModule
{
    internal const string KillBinding = EnemyNodeEffectContract.KillBinding;
    internal const string RemoveBinding = EnemyNodeEffectContract.RemoveBinding;
    internal const string MarkBinding = EnemyNodeEffectContract.MarkBinding;
    internal const string TargetBinding = EnemyNodeEffectContract.TargetBinding;
    internal const string KillHandler = EnemyNodeEffectContract.KillHandler;
    internal const string RemoveHandler = EnemyNodeEffectContract.RemoveHandler;
    internal const string MarkHandler = EnemyNodeEffectContract.MarkHandler;
    internal const string TargetHandler = EnemyNodeEffectContract.TargetHandler;

    /// <summary>The one recipient kind `target` accepts: a player, spelled the way the Map provider spells it.
    /// A reference of another kind is refused before any native read.</summary>
    private const string PlayerKind = "gtfo.player";
    /// <summary>The one visibility policy `mark` implements. The catalog's row declares it as the structural
    /// choice an author makes; every machine that renders a NavMarker would see it, so `team` is what a marker
    /// that is not routed to named players can honestly be.</summary>
    private const string TeamVisibility = "team";
    /// <summary>The marker option the game's own enemy marker uses (`NavMarkerOption.EnemyTitleDistance`), so a
    /// Forge mark is the same icon, distance and title the game draws rather than a second visual language.</summary>
    private const long EnemyMarkerOption = (long)NavMarkerOption.EnemyTitleDistance;
    /// <summary>The seconds one tick of plan time is, which is the unit `duration` is declared in. The kernel's
    /// tick is the level's own simulation step and the catalog's other duration ports are ticks too; this family
    /// converts once, here, because the native marker's own timer is in seconds.</summary>
    private const double SecondsPerTick = 1.0 / 60.0;
    /// <summary>The bound on one marker's requested lifetime: a day of plan time. A longer request is refused
    /// rather than kept as a value the arithmetic below cannot represent.</summary>
    private const long MaximumDurationTicks = 60L * 60L * 24L * 60L;
    private const double MinimumChance = 0.0, MaximumChance = 1.0;
    private const double MinimumColorComponent = 0.0, MaximumColorComponent = 1.0;

    /// <summary>The four rows' own ports, resolved at registration against the capability rows in
    /// `EnemyNodeEffectContract`.</summary>
    internal static readonly HandlerShape KillPorts = EnemyNodeEffectContract.KillShape();
    internal static readonly HandlerShape RemovePorts = EnemyNodeEffectContract.RemoveShape();
    internal static readonly HandlerShape MarkPorts = EnemyNodeEffectContract.MarkShape();
    internal static readonly HandlerShape TargetPorts = EnemyNodeEffectContract.TargetShape();

    /// <summary>One target's line in a node action's result, in the row order the schema declares: the four
    /// fixed columns first, then the action's own fields. Field names are the wire contract's, so each is spelled
    /// here exactly as `EnemyNodeEffectContract` spells it. `kill` and `remove` share this row because their two
    /// schemas declare the same five columns.</summary>
    private sealed record KillRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("target_count")] int TargetCount);
    private sealed record MarkRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("target_count")] int TargetCount);
    private sealed record TargetRow(EntityReference Target, string Status, string Committed, string Code,
        bool Propagated, [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>`forge.action.enemy.kill`. The native call is the only place the world is written: a failure
    /// observed after it stays unknown, and the run then stops committing further targets rather than writing a
    /// world whose first write is already in doubt.</summary>
    internal CommandResult Kill(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        var targets = Entities(context.Inputs, "targets");
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<KillRow>(targets.Length);
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            KillRow Row(string status, string committed, string code)
                => new(target, status, committed, code, targets.Length);
            if (stopCommitting) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "not-attempted-after-unknown-commit")); continue; }
            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "stale-or-unsupported-recipient")); continue; }
            var enemy = entry.Enemy;
            Dam_EnemyDamageBase? receiver;
            try
            {
                if (!enemy.Alive) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "not-alive")); continue; }
                receiver = enemy.Damage;
            }
            catch (Exception) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "health-receiver-unavailable")); continue; }
            // The instant-death entry is the receiver's own, so a receiver that is not set up or belongs to
            // another agent is not this life's to end.
                if (receiver == null || receiver.Pointer == IntPtr.Zero || !receiver.IsSetup
                    || receiver.Owner == null || receiver.Owner.Pointer != entry.EnemyPointer)
                { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "missing-health-receiver")); continue; }
            var receiverPointer = receiver.Pointer;

            var called = true;
            try { receiver.InstantDead(true); }
            catch (Exception) { rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "native-commit-exception")); stopCommitting = true; called = false; }
            if (!called) continue;

            try
            {
                if (!CanExecute) { rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "authority-or-phase")); stopCommitting = true; continue; }
                var current = Resolve(target);
                if (current == null || !ReferenceEquals(current, entry) || enemy.Damage == null
                    || enemy.Damage.Pointer != receiverPointer)
                { rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "receiver-changed-during-commit")); stopCommitting = true; continue; }
                // The call reports nothing, so the only proof of a committed kill is the agent's own liveness.
                if (enemy.Alive) { rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "death-unseen")); stopCommitting = true; continue; }
            }
            catch (Exception) { rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "readback-exception")); stopCommitting = true; continue; }
            rows.Add(Row(CommandStatuses.Succeeded, CommitStates.Confirmed, "committed"));
        }
        return AggregateNodeRows(OutcomeRows(rows, row => row.Committed, row => row.Code),
            RuntimeJson.From(new { results = rows }), "kill");
    }

    /// <summary>`forge.action.enemy.remove`. The world is written once per target, through the game's own
    /// replication despawn: an enemy's replicator is asked to go away, which is the path a despawned enemy always
    /// takes, and no life ends, no death settlement runs and no corpse is left behind.
    ///
    /// The call reports nothing, so the proof of a committed removal is this module's own despawn observation:
    /// the reference this provider minted for the target no longer resolves once the game's despawn hook has run.
    /// A target still resolvable after the call is an unknown commit, never a claimed one, and the run then stops
    /// committing further targets rather than writing a world whose first write is already in doubt.</summary>
    internal CommandResult Remove(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        var targets = Entities(context.Inputs, "targets");
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<KillRow>(targets.Length);
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            KillRow Row(string status, string commitState, string code)
                => new(target, status, commitState, code, targets.Length);
            if (stopCommitting)
            {
                rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "not-attempted-after-unknown-commit"));
                continue;
            }
            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "stale-or-unsupported-recipient")); continue; }
            var enemy = entry.Enemy;
            try
            {
                if (!enemy.Alive) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "not-alive")); continue; }
                var replicator = enemy.Sync?.Replicator;
                if (replicator == null) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "replicator-unavailable")); continue; }
                replicator.Despawn();
                // The game's own despawn is what this module observes, so the removal is confirmed by the
                // reference it minted no longer answering — not by the call's own return.
                if (Resolve(target) != null)
                { rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "removal-unseen")); stopCommitting = true; continue; }
            }
            catch (Exception) { rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "readback-exception")); stopCommitting = true; continue; }
            rows.Add(Row(CommandStatuses.Succeeded, CommitStates.Confirmed, "committed"));
        }
        return AggregateNodeRows(OutcomeRows(rows, row => row.Committed, row => row.Code),
            RuntimeJson.From(new { results = rows }), "remove");
    }

    /// <summary>`forge.action.enemy.mark`. The request check runs before the first target: an unknown visibility
    /// policy, a malformed colour or an out-of-range duration refuses the whole command rather than marking part
    /// of a set. Every marker built in one dispatch shares one `marker` handle, which is what a later `cancel`
    /// removes them through.
    ///
    /// The row is this provider's one `presentation` step (ruling 122.3), so the handler runs on each addressed
    /// player's own machine: the gate is the presentation one, the per-row commit column is the tier's own `none`,
    /// and the aggregate reports the placements as a non-committing result — a NavMarker is local presentation,
    /// not world state, and the tier refuses a handler that claims otherwise.</summary>
    internal CommandResult Mark(CommandContext context)
    {
        if (!CanPresent) return CommandResult.Rejected("authority-or-phase");
        if (!context.Parameters.TryGetProperty("visibility_policy", out var declared)
            || declared.ValueKind != JsonValueKind.String || declared.GetString() != TeamVisibility)
            return CommandResult.Rejected("visibility-unsupported");
        if (!TryColor(context.Inputs, out var color)) return CommandResult.Rejected("invalid-color");
        if (!TryDuration(context.Inputs, out var duration)) return CommandResult.Rejected("duration-out-of-range");
        var targets = Entities(context.Inputs, "targets");
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");
        if (GuiManager.NavMarkerLayer == null) return CommandResult.Rejected("marker-layer-unavailable");

        var rows = new List<MarkRow>(targets.Length);
        JsonElement marker = default;
        string? markerKey = null;
        int marked = 0, failed = 0;
        foreach (var target in targets)
        {
            MarkRow Row(string status, string commitState, string code, double remaining)
                => new(target, status, commitState, code, remaining, targets.Length);
            var entry = Resolve(target);
            if (entry == null) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "stale-or-unsupported-recipient", 0)); continue; }
            var enemy = entry.Enemy;
            GameObject? model;
            try
            {
                if (!enemy.Alive) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "not-alive", 0)); continue; }
                model = enemy.MainModelGO;
            }
            catch (Exception) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "enemy-model-unavailable", 0)); continue; }
            // The game's own tag placement declines silently when the enemy has no model object; a marker with
            // nothing to track would be a handle onto nothing, so it is refused by name.
            if (model == null) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "enemy-model-unavailable", 0)); continue; }

            NavMarker? placed;
            try
            {
                // The marker is built first and the handle is cast only once one really exists, so a dispatch
                // that placed nothing mints nothing: a handle that named no marker would be a name for nothing.
                placed = GuiManager.NavMarkerLayer.PlaceCustomMarker((NavMarkerOption)EnemyMarkerOption, model, "", 0f, false);
                if (placed == null) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "marker-unplaced", 0)); continue; }
                placed.SetColor(color);
                if (markerKey == null)
                {
                    marker = _registration.CreateEffectHandle("encounter");
                    markerKey = marker.GetRawText();
                }
            }
            catch (Exception) { rows.Add(Row(CommandStatuses.Failed, CommitStates.None, "native-placement-exception", 0)); failed++; continue; }
            if (Resolve(target) == null || !ReferenceEquals(Resolve(target), entry))
            { rows.Add(Row(CommandStatuses.Failed, CommitStates.None, "receiver-changed-during-placement", 0)); failed++; continue; }
            // The lifetime the row reports is the one the marker really holds: the recorded expiry minus the
            // tick the placement happened on, which is what a caller can rely on rather than what it asked for.
            long expiresAt = _kernel.CurrentTick + duration;
            rows.Add(Row(CommandStatuses.Succeeded, CommitStates.None, "marker-placed",
                Seconds(duration)));
            marked++;
            _markers.Add(new MarkerRecord(target, entry.EnemyPointer, placed, markerKey, expiresAt));
        }

        if (markerKey != null)
        {
            try
            {
                _registration.RegisterNative(marker, _markers);
                _registration.RegisterCancel(marker, () => RemoveMarked(markerKey));
            }
            catch (Exception) { _report("mark handle registration failed; marks from this dispatch cannot be cancelled."); }
        }
        var outputs = markerKey == null
            ? RuntimeJson.From(new { results = rows })
            : RuntimeJson.From(new { results = rows, marker });
        // A presented mark is work the handler really did and none of it commits world state, which is the one
        // non-committing success the result rules allow. A dispatch that placed nothing is a refusal when nothing
        // failed and a failure when something did; how many of the targets were placed is in the rows.
        if (marked > 0) return CommandResult.Partial(outputs, CommitStates.None);
        if (failed == 0)
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None,
                rows.Count == 1 ? rows[0].Code : "mark-all-rejected", "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.None,
            rows.Count == 1 ? rows[0].Code : "mark-all-failed", "", outputs);
    }

    /// <summary>`forge.action.enemy.target`. The propagated agent is the player entity's own native instance,
    /// read by the kind's own lookup — the module's registered agent resolver — so a reference no provider can
    /// turn back into an instance is refused before the first enemy is touched, because a propagation without a
    /// target would be a different action. A reference of another kind is refused even earlier, by name.</summary>
    internal CommandResult Target(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected("authority-or-phase");
        var target = context.GetEntityInput("target");
        if (target.Id == null || !target.Id.StartsWith(PlayerKind + ":", StringComparison.Ordinal))
            return CommandResult.Rejected("target-not-a-player");
        // The kernel's own table is asked first: the provider that owns the reference's kind is the authority on
        // whether it still names a live instance, and a reference no provider answers for is refused before the
        // native lookup runs.
        // The request is judged before anything native is read: a chance outside the closed unit interval is the
        // caller's own error, and a command that cannot be carried out as asked must not half-apply.
        double? chance = null;
        if (context.Inputs.TryGetProperty("chance", out var chanceValue) && chanceValue.ValueKind != JsonValueKind.Null)
        {
            if (chanceValue.ValueKind != JsonValueKind.Number || !chanceValue.TryGetDouble(out var requested)
                || !double.IsFinite(requested) || requested < MinimumChance || requested > MaximumChance)
                return CommandResult.Rejected("chance-out-of-range");
            chance = requested;
        }
        var enemies = Entities(context.Inputs, "enemies");
        if (enemies.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");
        dynamic? player;
        try { player = _agentOf(target); }
        catch (Exception) { return CommandResult.Rejected("target-unavailable"); }
        // A null agent is either a reference the kind's own owner does not answer for, or an instance the game's
        // table no longer holds; both are the same refusal at this boundary, and both happen before any enemy is
        // touched.
        if (player == null) return CommandResult.Rejected("target-unavailable");

        var rows = new List<TargetRow>(enemies.Length);
        foreach (var reference in enemies)
        {
            TargetRow Row(string status, string commitState, string code, bool propagated)
                => new(reference, status, commitState, code, propagated, enemies.Length);
            var entry = Resolve(reference);
            if (entry == null) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "stale-or-unsupported-recipient", false)); continue; }
            var enemy = entry.Enemy;
            try { if (!enemy.Alive) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "not-alive", false)); continue; } }
            catch (Exception) { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "enemy-state-unavailable", false)); continue; }
            bool propagated;
            try
            {
                // The chance form answers whether the propagation was kept, which is the one reading that tells a
                // refused propagation from a committed one; the full form takes no target policy and returns void.
                propagated = chance.HasValue
                    ? enemy.PropagateTargetLimited(player, (float)chance.Value)
                    : PropagateFull(enemy, player);
            }
            catch (Exception) { rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "native-commit-exception", false)); continue; }
            if (Resolve(reference) == null || !ReferenceEquals(Resolve(reference), entry))
            { rows.Add(Row(CommandStatuses.Failed, CommitStates.Unknown, "receiver-changed-during-commit", propagated)); continue; }
            if (chance.HasValue && !propagated)
            { rows.Add(Row(CommandStatuses.Rejected, CommitStates.None, "propagation-refused", false)); continue; }
            rows.Add(Row(CommandStatuses.Succeeded, CommitStates.Confirmed, "committed", propagated));
        }
        return AggregateNodeRows(OutcomeRows(rows, row => row.Committed, row => row.Code),
            RuntimeJson.From(new { results = rows }), "target");
    }

    /// <summary>The full-propagation call, which the build declares as `void`. It is wrapped so the two forms
    /// of the same native entry answer one shape at the call site.</summary>
    private static bool PropagateFull(EnemyAgent enemy, dynamic player)
    {
        enemy.PropagateTargetFull(player);
        return true;
    }

    /// <summary>The entity references one collection input carries, in the plan's own order.</summary>
    private static EntityReference[] Entities(JsonElement inputs, string port)
    {
        var values = inputs.GetProperty(port).EnumerateArray();
        var references = new List<EntityReference>();
        foreach (var value in values) references.Add(RuntimeJson.Entity(value));
        return references.ToArray();
    }

    /// <summary>One command's conclusion, by the rule every multi-target action in this repository uses:
    /// everything confirmed is a success, nothing confirmed is a rejection or an unknown failure, and anything in
    /// between is partial with the weaker commit state.</summary>
    private static CommandResult AggregateNodeRows(IReadOnlyList<(string Committed, string Code)> rows, JsonElement outputs, string action)
    {
        int committed = 0, unknown = 0;
        foreach (var row in rows)
        {
            if (row.Committed == CommitStates.Confirmed) committed++;
            else if (row.Committed == CommitStates.Unknown) unknown++;
        }
        if (committed == rows.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        string code = rows.Count == 1 ? rows[0].Code : action + "-all-rejected";
        if (unknown == 0) return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown,
            rows.Count == 1 ? rows[0].Code : action + "-all-unknown", "", outputs);
    }

    /// <summary>One action's rows as the aggregate's own input: the commit column and the code each row carries.</summary>
    private static (string Committed, string Code)[] OutcomeRows<T>(IReadOnlyList<T> rows,
        Func<T, string> committed, Func<T, string> code)
    {
        var outcomes = new (string Committed, string Code)[rows.Count];
        for (int index = 0; index < rows.Count; index++) outcomes[index] = (committed(rows[index]), code(rows[index]));
        return outcomes;
    }

    /// <summary>The requested colour as the Unity value the native setter takes. The row's `color` port is a
    /// `vector3` (red, green, blue) and `opacity` is the fourth channel, because the catalog has no colour port
    /// type; each component has to be a finite number inside the closed unit interval, and an opacity outside it
    /// is refused rather than clamped.</summary>
    private static bool TryColor(JsonElement inputs, out Color color)
    {
        color = default;
        if (!inputs.TryGetProperty("color", out var value) || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() != 3) return false;
        Span<float> components = stackalloc float[3];
        var index = 0;
        foreach (var component in value.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out var number)
                || !double.IsFinite(number) || number < MinimumColorComponent || number > MaximumColorComponent)
                return false;
            components[index++] = (float)number;
        }
        float alpha = 1f;
        if (inputs.TryGetProperty("opacity", out var opacity) && opacity.ValueKind != JsonValueKind.Null)
        {
            if (opacity.ValueKind != JsonValueKind.Number || !opacity.TryGetDouble(out var requested)
                || !double.IsFinite(requested) || requested < MinimumColorComponent || requested > MaximumColorComponent)
                return false;
            alpha = (float)requested;
        }
        color = new Color(components[0], components[1], components[2], alpha);
        return true;
    }

    /// <summary>The requested marker lifetime in plan ticks. An absent port and a present null both mean "no
    /// caller-set lifetime", which the row declares as `duration`; the value the result reports is always the
    /// number the step asked for.</summary>
    private static bool TryDuration(JsonElement inputs, out long duration)
    {
        duration = 0;
        if (!inputs.TryGetProperty("duration", out var value) || value.ValueKind == JsonValueKind.Null) return true;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out duration)
            && duration >= 0 && duration <= MaximumDurationTicks;
    }

    /// <summary>The seconds a tick count is worth, which is the unit the native marker's own lifetime is in.</summary>
    private static double Seconds(long ticks) => ticks * SecondsPerTick;

    /// <summary>The same step read the other way: a native length in seconds as plan ticks, rounded to the
    /// nearest tick and saturated rather than wrapped, so a length the clock cannot measure is published as the
    /// longest tick there is instead of as a negative one.</summary>
    private static long Ticks(double seconds)
    {
        double ticks = Math.Round(seconds / SecondsPerTick, MidpointRounding.AwayFromZero);
        if (ticks <= 0) return 0;
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }
}
