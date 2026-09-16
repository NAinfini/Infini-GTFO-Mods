using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>The `forge.action.combat.heal` handler for `gtfo.player`: the canonical capability, this provider's
/// own binding and the player lives this package already tracks. One row is written per recipient in the plan's
/// own order, and the row's `amount` is what the receiver's own readback showed, never what was asked for.
///
/// The shape of the exchange is the same one the enemy receiver uses for the same capability, because the two
/// are answers to one contract: an amount and a ceiling are validated once for the command, the overheal policy
/// is applied per target, a target that cannot receive anything is rejected by the code that names why, and a
/// target whose health cannot be read back after the write is an unknown commit rather than a success. The
/// differences are the domain's: identity is the player module's, the write is the player receiver's, and a
/// refusal is the player's own guard.
///
/// `overheal` is refused for the whole command, exactly as it is for enemies: GTFO health is quantized against
/// `HealthMax` by the receiver's own encoding, so there is no representable value above the ceiling and
/// accepting the policy would promise an effect the receiver cannot produce.</summary>
internal static class PlayerHealthAction
{
    internal const double MinimumAmount = 0.000001;
    internal const double MaximumAmount = 1000000;

    /// <summary>The handler's own ports, resolved once at registration against `forge.action.combat.heal`:
    /// `targets` is the recipient collection, whose whole declared width separates `source` from the rest.</summary>
    internal static readonly HandlerShape Ports = new HandlerShape()
        .Inputs("targets", "source", "amount", "cap").Outputs("result").Parameters("overheal_policy");

    /// <summary>One row of the multi-target heal result, in the canonical result schema's own columns:
    /// `target`, `status`, `committed`, `code`, then the row's own `amount` and the command's `target_count`.</summary>
    private sealed record HealRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("amount")] double ActualAmount,
        [property: JsonPropertyName("target_count")] int TargetCount);

    internal static CommandResult Execute(CommandContext context)
    {
        // The identity half owns the readiness, authority and fault gate: a command that reaches here while it
        // says no would be writing where a readback is already refused.
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected("authority-or-phase");
        // Source carries no faction or targeting restriction (teammate, hostile and self are all valid); it is
        // still a required, kernel-validated entity reference.
        _ = context.GetEntityInput("source");
        var policy = context.Parameters.GetProperty("overheal_policy").GetString();
        if (policy == "overheal") return CommandResult.Rejected("overheal-unsupported");
        double requested = context.Inputs.GetProperty("amount").GetDouble();
        if (!double.IsFinite(requested) || requested < MinimumAmount || requested > MaximumAmount)
            return CommandResult.Rejected("amount-out-of-range");
        double? cap = null;
        if (context.Inputs.TryGetProperty("cap", out var capElement))
        {
            double capValue = capElement.GetDouble();
            if (!double.IsFinite(capValue) || capValue <= 0) return CommandResult.Rejected("invalid-cap");
            cap = capValue;
        }
        var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        // One row is written per target; the result budget is the row budget.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<HealRow>(targets.Length);
        int committed = 0, rejected = 0, unknown = 0;
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            HealRow Row(string status, string state, string code, double actual = 0)
                => new(target, status, state, code, actual, targets.Length);

            if (stopCommitting) { rows.Add(Row("rejected", CommitStates.None, "not-attempted-after-unknown-commit")); rejected++; continue; }

            PlayerHealthSnapshot before;
            float desired;
            try
            {
                if (!players.CanCommit) { rows.Add(Row("rejected", CommitStates.None, "authority-or-phase")); rejected++; continue; }
                if (!PlayerHealthReceiver.TryRead(target, out before, out var code))
                { rows.Add(Row("rejected", CommitStates.None, code)); rejected++; continue; }
                double effectiveCap = cap.HasValue ? Math.Min(before.Maximum, cap.Value) : before.Maximum;
                double desiredUncapped = (double)before.Health + requested;
                // `discard` skips the whole target when it would overflow; `clamp` stops at the ceiling and is
                // what the remaining policy value means.
                if (desiredUncapped > effectiveCap && policy == "discard")
                { rows.Add(Row("rejected", CommitStates.None, "would-overheal")); rejected++; continue; }
                desired = (float)Math.Max(before.Health, Math.Min(effectiveCap, desiredUncapped));
                // A native read can re-enter other mods, so the state a commit was computed from is proved again
                // before the write; a change is a refusal, never a value applied to a stale snapshot.
                if (!players.CanCommit) { rows.Add(Row("rejected", CommitStates.None, "authority-or-phase")); rejected++; continue; }
                if (!PlayerHealthReceiver.TryRead(target, out var again, out _) || again != before)
                { rows.Add(Row("rejected", CommitStates.None, "state-changed-before-commit")); rejected++; continue; }
                if (!players.CanCommit) { rows.Add(Row("rejected", CommitStates.None, "authority-or-phase")); rejected++; continue; }
            }
            catch (Exception)
            {
                // No SendSetHealth call has occurred, so this is a known-uncommitted refusal.
                rows.Add(Row("rejected", CommitStates.None, "preflight-exception")); rejected++; continue;
            }

            // A target already at the effective ceiling is a real, committed no-op: clamping left nothing to add
            // and the receiver would store the value it already holds. Reporting a change nobody made is what
            // the contract forbids.
            if (desired <= before.Health)
            {
                rows.Add(Row("committed", CommitStates.Confirmed, "committed"));
                committed++; continue;
            }

            // Crossing this boundary means the write may already have landed. Its own failure and a failure to
            // read the effect back are separate codes, so each gets its own attempt. The one refusal that is
            // still known-uncommitted — the receiver resolved differently on the way in — is reported as such
            // instead of being folded into the unknown commit.
            try
            {
                if (!PlayerHealthReceiver.TrySubmit(before, desired))
                {
                    rows.Add(Row("rejected", CommitStates.None, "state-changed-before-commit"));
                    rejected++; continue;
                }
            }
            catch (Exception)
            {
                rows.Add(Row("unknown", CommitStates.Unknown, "native-commit-exception"));
                unknown++; stopCommitting = true; continue;
            }
            try
            {
                // The life and the receiver must be the same instances the preflight read; only the health is
                // allowed to have moved, and it may only have moved up within the same ceiling. Anything else
                // leaves the effect of the write unknown, which is what the row reports.
                if (!players.CanCommit
                    || !PlayerHealthReceiver.TryRead(target, out var after, out _)
                    || after.Target != before.Target || after.AgentPointer != before.AgentPointer
                    || after.ReceiverPointer != before.ReceiverPointer || !after.Alive)
                {
                    rows.Add(Row("unknown", CommitStates.Unknown, "receiver-changed-during-commit"));
                    unknown++; stopCommitting = true; continue;
                }
                if (after.Maximum != before.Maximum || after.Health < before.Health || after.Health > after.Maximum)
                {
                    rows.Add(Row("unknown", CommitStates.Unknown, "unexpected-health-readback"));
                    unknown++; stopCommitting = true; continue;
                }
                double actual = (double)after.Health - before.Health;
                rows.Add(Row("committed", CommitStates.Confirmed, "committed", actual));
                committed++;
            }
            catch (Exception)
            {
                rows.Add(Row("unknown", CommitStates.Unknown, "readback-exception"));
                unknown++; stopCommitting = true;
            }
        }

        var outputs = Envelope(rows);
        // Aggregation branches on whether any row committed, not on unknown==0: a committed row beside a
        // rejected or unknown one is partial with the commit state the unknown rows dictate.
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
        {
            string code = rows.Count == 1 ? rows[0].Code
                : rows.Select(r => r.Code).Distinct(StringComparer.Ordinal).Count() == 1 ? rows[0].Code : "heal-all-rejected";
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        }
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown,
            rows.Count == 1 ? rows[0].Code : "heal-all-unknown", "", outputs);
    }

    private static JsonElement Envelope(IReadOnlyList<HealRow> rows) => RuntimeJson.From(new { results = rows });
}
