using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>One submission's conclusion, in the same three shapes every action in this package reports: a
/// confirmed commit, a refusal that never reached the world, and an attempt whose effect this side could not
/// observe. `Commit` is the state the result's own `committed` column carries; the `Committed()` factories are
/// the only members named for it.</summary>
internal readonly record struct BehaviorOutcome(string Status, string Commit, string Code)
{
    internal static BehaviorOutcome Committed() => new(CommandStatuses.Succeeded, CommitStates.Confirmed, "committed");
    internal static BehaviorOutcome Refused(string code) => new(CommandStatuses.Rejected, CommitStates.None, code);
    internal static BehaviorOutcome Unseen(string code) => new(CommandStatuses.Failed, CommitStates.Unknown, code);
}

/// <summary>One target's line in a behaviour result, in the row order the catalog declares: the four fixed
/// columns first, then that action's own fields, which are carried as a ready property bag so the two actions
/// share the aggregation rule without sharing a row shape.</summary>
internal sealed record BehaviorRow(EntityReference Target, BehaviorOutcome Outcome, IReadOnlyList<(string Name, object? Value)> Fields);

/// <summary>The rules every behaviour action follows, kept in one place because the two actions and the sibling
/// rows of this family must not each invent their own: what one target's refusal is worth, what a mixed answer
/// means, and which code a run of identical refusals reports.</summary>
internal static class BehaviorResults
{
    /// <summary>Builds one target's row, the four fixed columns plus the action's own fields in declaration
    /// order.</summary>
    internal static JsonElement Rows(IReadOnlyList<BehaviorRow> rows)
        => RuntimeJson.From(new
        {
            results = rows.Select(row =>
            {
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["target"] = row.Target,
                    ["status"] = row.Outcome.Status,
                    ["committed"] = row.Outcome.Commit,
                    ["code"] = row.Outcome.Code
                };
                foreach (var (name, value) in row.Fields) fields[name] = value;
                return fields;
            }).ToArray()
        });

    /// <summary>One command's conclusion: everything confirmed is a success, nothing confirmed is a rejection or
    /// an unknown failure, and anything in between is partial with the weaker commit state. `action` names the
    /// fallback code, so a run of identical refusals reports the refusal itself.</summary>
    internal static CommandResult Aggregate(IReadOnlyList<BehaviorRow> rows, string action)
    {
        int committed = 0, unknown = 0;
        foreach (var row in rows)
        {
            if (row.Outcome.Commit == CommitStates.Confirmed) committed++;
            else if (row.Outcome.Commit == CommitStates.Unknown) unknown++;
        }
        var outputs = Rows(rows);
        if (committed == rows.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(rows, action + "-all-rejected"), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(rows, action + "-all-unknown"), "", outputs);
    }

    private static string Single(IReadOnlyList<BehaviorRow> rows, string fallback)
    {
        if (rows.Count == 1) return rows[0].Outcome.Code;
        var first = rows[0].Outcome.Code;
        foreach (var row in rows) if (row.Outcome.Code != first) return fallback;
        return first;
    }
}

/// <summary>`forge.action.enemy.ability`, decided without touching a game type.
///
/// The two decisions a request can carry are made before the first target, because a submission that cannot be
/// carried out as asked must not half-apply: `target_policy: nearest` has no native member behind it, an
/// `ability` reference this provider does not own is not an ability, and a `cooldown_scope` no native member can
/// honour is refused rather than ignored.
///
/// Per target the ability's own index is read from that enemy's table, the game's trigger is submitted, and the
/// result is claimed only while the world this attempt was made in is still the world it is reported in.</summary>
internal static class AbilityDecision
{
    /// <summary>The declared `target_policy` members, in the catalog's own order. A structural enum parameter
    /// arrives as its member index — the frame carries the value set index, not the member name — so `current`
    /// is index 0 and `nearest` index 1.
    ///
    /// `current` is the policy the game's own trigger implements: `EnemyAbilities.UseAbility` acts on the ability
    /// component and its owner's own target, and no native member names an agent as "the one to use this on".
    /// `nearest` would have to replace the target first, which is `forge.action.enemy.target_set`'s job, so it is
    /// refused by name rather than silently served as `current`.</summary>
    internal static readonly string[] TargetPolicyMembers = { "current", "nearest" };
    internal const int TargetCurrent = 0;
    internal const int TargetNearest = 1;

    /// <summary>The declared `cooldown_scope` set's member count, the ABI's own `lifetime_scope` ordering. This
    /// action implements no provider-managed window, so every member is refused by name and this count only
    /// separates a value outside the declared set from a policy.</summary>
    internal const int LifetimeScopeCount = 6;

    internal static CommandResult Run(CommandContext context, IBehaviorPorts ports, EnemyBehaviorLedger ledger,
        long worldEpoch)
    {
        if (!ports.CanExecute) return CommandResult.Rejected("authority-or-phase");
        int policy = context.Parameters.GetProperty("target_policy").GetInt32();
        if (policy < 0 || policy >= TargetPolicyMembers.Length || policy == TargetNearest)
            return CommandResult.Rejected("target-policy-unsupported");
        int scope = context.Parameters.GetProperty("cooldown_scope").GetInt32();
        if (scope < 0 || scope >= LifetimeScopeCount) return CommandResult.Rejected("cooldown-scope-unsupported");
        if (!context.Inputs.TryGetProperty("ability", out var abilityValue) || abilityValue.ValueKind == JsonValueKind.Null)
            return CommandResult.Rejected("ability-missing");
        ResourceRef abilityReference;
        try { abilityReference = RuntimeJson.ResourceRefOf(abilityValue); }
        catch (RuntimeContractException) { return CommandResult.Rejected("ability-invalid"); }
        if (!EnemyAbilityResources.TryAbility(abilityReference, out var ability))
            return CommandResult.Rejected("ability-kind-unsupported");
        var targets = context.Inputs.GetProperty("enemies").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<BehaviorRow>(targets.Length);
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            BehaviorRow Row(BehaviorOutcome outcome) => new(target, outcome,
                new (string, object?)[] { ("target_count", targets.Length) });
            if (stopCommitting) { rows.Add(Row(BehaviorOutcome.Refused("not-attempted-after-unknown-commit"))); continue; }
            if (!ports.IsCurrent(target.Id)) { rows.Add(Row(BehaviorOutcome.Refused("stale-or-unsupported-recipient"))); continue; }
            if (!ports.IsAlive(target.Id)) { rows.Add(Row(BehaviorOutcome.Refused("not-alive"))); continue; }
            if (!ports.CanTrigger(target.Id)) { rows.Add(Row(BehaviorOutcome.Refused("abilities-disabled"))); continue; }
            if (!ports.TryAbilityIndex(target.Id, (byte)ability, out int index))
            { rows.Add(Row(BehaviorOutcome.Refused("ability-not-registered"))); continue; }

            bool submitted, triggered;
            try { submitted = ports.TryUseAbility(target.Id, (byte)ability, index, out triggered); }
            catch (Exception) { rows.Add(Row(BehaviorOutcome.Unseen("native-commit-exception"))); stopCommitting = true; continue; }
            // An attempt that could not be made at all is this side's refusal; an attempt the game itself
            // refused is the machine's own answer, and the two are reported apart.
            if (!submitted) { rows.Add(Row(BehaviorOutcome.Refused("receiver-unavailable"))); continue; }
            if (!triggered) { rows.Add(Row(BehaviorOutcome.Refused("ability-refused"))); continue; }

            try
            {
                if (!ports.CanExecute) { rows.Add(Row(BehaviorOutcome.Unseen("authority-or-phase"))); stopCommitting = true; continue; }
                if (!ports.IsCurrent(target.Id))
                { rows.Add(Row(BehaviorOutcome.Unseen("receiver-changed-during-commit"))); stopCommitting = true; continue; }
                // The readback is the machine's own answer about the component the trigger reached.
                if (!ports.TryAbilityState(target.Id, (byte)ability, out var component, out bool finished))
                { rows.Add(Row(BehaviorOutcome.Unseen("ability-disappeared"))); stopCommitting = true; continue; }
                // The row this trigger replaces is read back before it goes. The game keeps one active ability
                // per agent, so a row still held here was ended by this trigger — but only a component the
                // machine still reports as unfinished was ended before its own lifetime ran out; a finished one
                // ended normally and publishes nothing, and one that can no longer be read is an unknown the
                // module is not told about either.
                var replaced = ledger.Find(target.Id, worldEpoch);
                if (replaced != null)
                {
                    bool readable = ports.TryAbilityState(target.Id, replaced.Ability, out _, out bool replacedFinished);
                    ledger.End(target.Id, worldEpoch);
                    if (readable)
                        ports.AbilityDropped(replaced, replacedFinished,
                            ForgeEnemy.EnemyAbilityInterruptedContract.ReasonSuperseded);
                }
                if (!finished) ledger.Start(new RunningBehavior(target.Id, (byte)ability, component, worldEpoch));
            }
            catch (Exception) { rows.Add(Row(BehaviorOutcome.Unseen("readback-exception"))); stopCommitting = true; continue; }
            rows.Add(Row(BehaviorOutcome.Committed()));
        }
        return BehaviorResults.Aggregate(rows, "ability");
    }
}

/// <summary>`forge.action.enemy.noise_emit`, decided without touching a game type. The position is the caller's,
/// the radius is the catalog's single radius, and the node the native struct needs is resolved from the position
/// inside the bridge: a position no node covers is refused before the native call, because the node is what the
/// game's propagation walks.</summary>
internal static class NoiseDecision
{
    internal const double MinimumRadius = 0.01;
    internal const double MaximumRadius = 1000;
    internal const double MinimumPositionComponent = -100000;
    internal const double MaximumPositionComponent = 100000;

    internal static CommandResult Run(CommandContext context, IBehaviorPorts ports)
    {
        if (!ports.CanExecute) return CommandResult.Rejected("authority-or-phase");
        EntityReference? source;
        try { source = context.GetEntityInput("source"); }
        catch (RuntimeContractException) { return CommandResult.Rejected("source-missing"); }
        if (source == null) return CommandResult.Rejected("source-missing");
        if (!context.Inputs.TryGetProperty("radius", out var radiusValue) || radiusValue.ValueKind != JsonValueKind.Number
            || !radiusValue.TryGetDouble(out double radius) || !double.IsFinite(radius)
            || radius < MinimumRadius || radius > MaximumRadius)
            return CommandResult.Rejected("radius-out-of-range");
        if (!context.Inputs.TryGetProperty("position", out var positionValue) || positionValue.ValueKind != JsonValueKind.Array
            || !TryPosition(positionValue, out var position))
            return CommandResult.Rejected("invalid-position");
        // The source the row's own `target` column carries is the entity the caller named, so it is validated
        // before the world is written even though no native member consumes it.
        if (source.WorldEpoch != context.WorldEpoch) return CommandResult.Rejected("source-missing");

        bool emitted;
        try { emitted = ports.TryEmitNoise(source.Id, position, radius); }
        catch (Exception) { return Failed("native-commit-exception"); }
        if (!emitted) return CommandResult.Rejected("noise-node-unavailable");
        // The noise event carries no result the submitting side can read back: the game's own update consumes the
        // datum. What the row can carry is the source the caller named, and the commit is claimed only while the
        // world this call was made in is still the world it is reported in.
        if (!ports.CanExecute) return Failed("authority-or-phase");
        var row = new BehaviorRow(source, BehaviorOutcome.Committed(), new (string, object?)[] { ("radius", radius) });
        return CommandResult.Succeeded(BehaviorResults.Rows(new[] { row }));
    }

    private static CommandResult Failed(string code)
        => CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, code, "",
            RuntimeJson.From(new { results = Array.Empty<object>() }));

    private static bool TryPosition(JsonElement value, out (double X, double Y, double Z) position)
    {
        position = default;
        if (value.GetArrayLength() != 3) return false;
        Span<double> components = stackalloc double[3];
        var index = 0;
        foreach (var component in value.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out double number)
                || !double.IsFinite(number) || number < MinimumPositionComponent || number > MaximumPositionComponent)
                return false;
            components[index++] = number;
        }
        position = (components[0], components[1], components[2]);
        return true;
    }
}
