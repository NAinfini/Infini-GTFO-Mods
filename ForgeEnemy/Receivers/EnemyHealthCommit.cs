using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Receivers;

internal sealed record EnemyHealthSnapshot(EntityReference Target, IntPtr Receiver,
    bool IsSetup, bool Alive, float Health, float Maximum);

/// <summary>Synchronous native submission for a multi-target heal. No retry, timer, registration or second ledger.</summary>
internal sealed class EnemyHealthCommit : EnemyThreadBoundary
{
    internal const double MinimumAmount = 0.000001;
    internal const double MaximumAmount = 1000000;
    private readonly Func<bool> _canExecute;
    private readonly Func<EntityReference, EnemyHealthSnapshot?> _read;
    private readonly Func<float, float, float> _quantize;
    private readonly Action<EnemyHealthSnapshot, float> _submit;
    private readonly string _healthChangedBinding;

    /// <summary>One row of the multi-target heal result. Field names match the heal command's wire contract.</summary>
    private sealed record HealRow(EntityReference Target, string Status, string Code, double RequestedAmount,
        double? ActualAmount, float? HealthBefore, float? HealthAfter, double? OverflowAmount);

    internal EnemyHealthCommit(Func<bool> canExecute,
        Func<EntityReference, EnemyHealthSnapshot?> read, Func<float, float, float> quantize,
        Action<EnemyHealthSnapshot, float> submit, string healthChangedBinding)
    {
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _quantize = quantize ?? throw new ArgumentNullException(nameof(quantize));
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        if (string.IsNullOrWhiteSpace(healthChangedBinding))
            throw new ArgumentException("Binding ID is required.", nameof(healthChangedBinding));
        _healthChangedBinding = healthChangedBinding;
    }

    /// <summary>Commits a multi-target heal. `cap`, when given, lowers the effective ceiling below HealthMax for
    /// every target. `discardOnOverflow` implements the "discard" overheal policy (reject instead of clamp) per
    /// target; the "overheal" policy is structurally unsupported and must be rejected by the caller before this
    /// runs. Aggregation mirrors ForgeEnemy.Native.EnemyModule.Heal exactly: the handler always returns a
    /// CommandResult that is valid under CommandResultRules.TryValidate by itself, for every combination of
    /// per-target outcomes, so no combination reaches the kernel's "invalid-handler-result" fallback.</summary>
    internal CommandResult Execute(EntityReference[] targets, double requested, double? cap = null, bool discardOnOverflow = false)
    {
        CheckThread(); ArgumentNullException.ThrowIfNull(targets);
        if (!double.IsFinite(requested) || requested < MinimumAmount || requested > MaximumAmount)
            return CommandResult.Rejected("gtfo.enemy.amount_out_of_range");
        if (cap.HasValue && (!double.IsFinite(cap.Value) || cap.Value <= 0))
            return CommandResult.Rejected("gtfo.enemy.invalid_cap");
        // A fully successful heal publishes one fact per target; the command result budget caps at 128.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("gtfo.enemy.too_many_targets");

        var rows = new List<HealRow>(targets.Length);
        var facts = new List<RuntimeFact>();
        int committed = 0, rejected = 0, unknown = 0;
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            HealRow Row(string status, string code, float? before = null, float? after = null, double? actual = null, double? overflow = null)
                => new(target, status, code, requested, actual, before, after, overflow);

            if (stopCommitting) { rows.Add(Row("rejected", "gtfo.enemy.not_attempted_after_unknown_commit")); rejected++; continue; }

            EnemyHealthSnapshot before;
            float desired, quantized;
            double overflowAmount;
            try
            {
                if (!_canExecute()) { rows.Add(Row("rejected", "gtfo.enemy.authority_or_phase")); rejected++; continue; }
                var state = _read(target);
                if (state == null || state.Target != target)
                { rows.Add(Row("rejected", "gtfo.enemy.stale_or_unsupported_recipient")); rejected++; continue; }
                before = state;
                if (!before.IsSetup || before.Receiver == IntPtr.Zero)
                { rows.Add(Row("rejected", "gtfo.enemy.missing_health_receiver", before.Health)); rejected++; continue; }
                if (!float.IsFinite(before.Health) || !float.IsFinite(before.Maximum)
                    || before.Maximum <= 0 || before.Health > before.Maximum)
                { rows.Add(Row("rejected", "gtfo.enemy.invalid_health_state", before.Health)); rejected++; continue; }
                if (!before.Alive || before.Health <= 0)
                { rows.Add(Row("rejected", "gtfo.enemy.not_alive", before.Health)); rejected++; continue; }
                double effectiveCap = cap.HasValue ? Math.Min(before.Maximum, cap.Value) : before.Maximum;
                double desiredUncapped = (double)before.Health + requested;
                overflowAmount = Math.Max(0, desiredUncapped - effectiveCap);
                if (desiredUncapped > effectiveCap && discardOnOverflow)
                { rows.Add(Row("rejected", "gtfo.enemy.would_overheal", before.Health, overflow: overflowAmount)); rejected++; continue; }
                desired = (float)Math.Max(before.Health, Math.Min(effectiveCap, desiredUncapped));
                quantized = _quantize(desired, before.Maximum);
                if (!float.IsFinite(quantized) || quantized < 0 || quantized > before.Maximum)
                { rows.Add(Row("rejected", "gtfo.enemy.invalid_native_quantization", before.Health, overflow: overflowAmount)); rejected++; continue; }
                // Quantization/native getters can re-enter other mods. Never send a stale absolute HP value.
                if (!_canExecute())
                { rows.Add(Row("rejected", "gtfo.enemy.authority_or_phase", before.Health, overflow: overflowAmount)); rejected++; continue; }
                if (_read(target) != before)
                { rows.Add(Row("rejected", "gtfo.enemy.state_changed_before_commit", before.Health, overflow: overflowAmount)); rejected++; continue; }
                if (!_canExecute())
                { rows.Add(Row("rejected", "gtfo.enemy.authority_or_phase", before.Health, overflow: overflowAmount)); rejected++; continue; }
            }
            catch (Exception)
            {
                // No SendSetHealth call has occurred; this is known uncommitted, not an unknown commit.
                rows.Add(Row("rejected", "gtfo.enemy.preflight_exception")); rejected++; continue;
            }

            if (desired <= before.Health || quantized <= before.Health)
            {
                rows.Add(Row("committed", "committed", before.Health, before.Health, 0, overflowAmount));
                committed++; continue;
            }

            // Crossing this boundary means a packet or local mutation may already have occurred.
            try { _submit(before, desired); }
            catch (Exception)
            {
                rows.Add(Row("unknown", "gtfo.enemy.native_submit_exception", before.Health, overflow: overflowAmount));
                unknown++; stopCommitting = true; continue;
            }
            try
            {
                var after = _read(target);
                if (after == null || after.Target != target || after.Receiver != before.Receiver
                    || !after.IsSetup || !after.Alive)
                { rows.Add(Row("unknown", "gtfo.enemy.receiver_changed_during_commit", before.Health, overflow: overflowAmount)); unknown++; stopCommitting = true; continue; }
                if (after.Maximum != before.Maximum || !float.IsFinite(after.Health)
                    || after.Health < before.Health || after.Health > before.Maximum)
                { rows.Add(Row("unknown", "gtfo.enemy.unexpected_health_readback", before.Health, overflow: overflowAmount)); unknown++; stopCommitting = true; continue; }
                double actual = (double)after.Health - before.Health;
                rows.Add(Row("committed", "committed", before.Health, after.Health, actual, overflowAmount));
                committed++;
                if (actual > 0) facts.Add(new RuntimeFact(_healthChangedBinding,
                    RuntimeJson.From(new { target, value = after.Health, delta = actual })));
            }
            catch (Exception)
            {
                rows.Add(Row("unknown", "gtfo.enemy.readback_exception", before.Health, overflow: overflowAmount));
                unknown++; stopCommitting = true;
            }
        }

        var outputs = Envelope(rows);
        // The handler must always construct a CommandResult that CommandResultRules.TryValidate accepts on its
        // own, for every combination of per-target outcomes. In particular, a mix of rejected targets and
        // committed-but-zero-delta targets (e.g. a target already at full health) produces no facts even though
        // nothing was rejected due to an unknown commit; that case is Rejected/None below, not a factless Partial.
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs, facts.ToArray());
        if (unknown == 0)
        {
            if (facts.Count == 0)
            {
                // No target's HP actually changed: either every target was rejected outright, or the
                // committed targets all landed with zero effective delta (e.g. already at full health).
                string code = committed == 0
                    ? (rows.Count == 1 ? rows[0].Code : "gtfo.enemy.heal_all_rejected")
                    : "gtfo.enemy.heal_no_state_change";
                return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
            }
            return CommandResult.Partial(outputs, CommitStates.Confirmed, facts.ToArray());
        }
        if (facts.Count == 0)
            return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown,
                rows.Count == 1 ? rows[0].Code : "gtfo.enemy.heal_all_unknown", "", outputs, facts.ToArray());
        return CommandResult.Partial(outputs, CommitStates.Unknown, facts.ToArray());
    }

    private static JsonElement Envelope(IReadOnlyList<HealRow> rows) => RuntimeJson.From(new { results = rows });
}
