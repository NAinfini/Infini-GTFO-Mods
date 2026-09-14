using System;
using System.Collections.Generic;
using System.Linq;
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
            return CommandResult.Rejected("amount-out-of-range");
        if (cap.HasValue && (!double.IsFinite(cap.Value) || cap.Value <= 0))
            return CommandResult.Rejected("invalid-cap");
        // A fully successful heal publishes one fact per target; the command result budget caps at 128.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<HealRow>(targets.Length);
        var facts = new List<RuntimeFact>();
        int committed = 0, rejected = 0, unknown = 0;
        bool stopCommitting = false;
        foreach (var target in targets)
        {
            HealRow Row(string status, string code, float? before = null, float? after = null, double? actual = null, double? overflow = null)
                => new(target, status, code, requested, actual, before, after, overflow);

            if (stopCommitting) { rows.Add(Row("rejected", "not-attempted-after-unknown-commit")); rejected++; continue; }

            EnemyHealthSnapshot before;
            float desired, quantized;
            double overflowAmount;
            try
            {
                if (!_canExecute()) { rows.Add(Row("rejected", "authority-or-phase")); rejected++; continue; }
                var state = _read(target);
                if (state == null || state.Target != target)
                { rows.Add(Row("rejected", "stale-or-unsupported-recipient")); rejected++; continue; }
                before = state;
                if (!before.IsSetup || before.Receiver == IntPtr.Zero)
                { rows.Add(Row("rejected", "missing-health-receiver", before.Health)); rejected++; continue; }
                if (!float.IsFinite(before.Health) || !float.IsFinite(before.Maximum)
                    || before.Maximum <= 0 || before.Health > before.Maximum)
                { rows.Add(Row("rejected", "invalid-health-state", before.Health)); rejected++; continue; }
                if (!before.Alive || before.Health <= 0)
                { rows.Add(Row("rejected", "not-alive", before.Health)); rejected++; continue; }
                double effectiveCap = cap.HasValue ? Math.Min(before.Maximum, cap.Value) : before.Maximum;
                double desiredUncapped = (double)before.Health + requested;
                overflowAmount = Math.Max(0, desiredUncapped - effectiveCap);
                if (desiredUncapped > effectiveCap && discardOnOverflow)
                { rows.Add(Row("rejected", "would-overheal", before.Health, overflow: overflowAmount)); rejected++; continue; }
                desired = (float)Math.Max(before.Health, Math.Min(effectiveCap, desiredUncapped));
                quantized = _quantize(desired, before.Maximum);
                if (!float.IsFinite(quantized) || quantized < 0 || quantized > before.Maximum)
                { rows.Add(Row("rejected", "quantization-failed", before.Health, overflow: overflowAmount)); rejected++; continue; }
                // Quantization/native getters can re-enter other mods. Never send a stale absolute HP value.
                if (!_canExecute())
                { rows.Add(Row("rejected", "authority-or-phase", before.Health, overflow: overflowAmount)); rejected++; continue; }
                if (_read(target) != before)
                { rows.Add(Row("rejected", "state-changed-before-commit", before.Health, overflow: overflowAmount)); rejected++; continue; }
                if (!_canExecute())
                { rows.Add(Row("rejected", "authority-or-phase", before.Health, overflow: overflowAmount)); rejected++; continue; }
            }
            catch (Exception)
            {
                // No SendSetHealth call has occurred; this is known uncommitted, not an unknown commit.
                rows.Add(Row("rejected", "preflight-exception")); rejected++; continue;
            }

            if (desired <= before.Health || quantized <= before.Health)
            {
                rows.Add(Row("committed", "committed", before.Health, before.Health, 0, overflowAmount));
                committed++; continue;
            }

            // Crossing this boundary means a packet or local mutation may already have occurred. Submission and
            // readback failures are distinct catalog codes, so each gets its own try/catch.
            try { _submit(before, desired); }
            catch (Exception)
            {
                rows.Add(Row("unknown", "native-commit-exception", before.Health, overflow: overflowAmount));
                unknown++; stopCommitting = true; continue;
            }
            try
            {
                var after = _read(target);
                if (after == null || after.Target != target || after.Receiver != before.Receiver
                    || !after.IsSetup || !after.Alive)
                { rows.Add(Row("unknown", "receiver-changed-during-commit", before.Health, overflow: overflowAmount)); unknown++; stopCommitting = true; continue; }
                if (after.Maximum != before.Maximum || !float.IsFinite(after.Health)
                    || after.Health < before.Health || after.Health > before.Maximum)
                { rows.Add(Row("unknown", "unexpected-health-readback", before.Health, overflow: overflowAmount)); unknown++; stopCommitting = true; continue; }
                double actual = (double)after.Health - before.Health;
                rows.Add(Row("committed", "committed", before.Health, after.Health, actual, overflowAmount));
                committed++;
                if (actual > 0) facts.Add(new RuntimeFact(_healthChangedBinding,
                    RuntimeJson.From(new { target, value = after.Health, delta = actual })));
            }
            catch (Exception)
            {
                rows.Add(Row("unknown", "readback-exception", before.Health, overflow: overflowAmount));
                unknown++; stopCommitting = true;
            }
        }

        var outputs = Envelope(rows);
        // r11: aggregation branches on whether any row committed, not on unknown==0/facts.Count==0. A committed
        // row (including zero-delta) alongside a rejected or unknown row is Partial with possibly-empty facts.
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs, facts.ToArray());
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed, facts.ToArray());
        if (unknown == 0)
        {
            string code = rows.Count == 1 ? rows[0].Code
                : rows.Select(r => r.Code).Distinct().Count() == 1 ? rows[0].Code : "heal-all-rejected";
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        }
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown,
            rows.Count == 1 ? rows[0].Code : "heal-all-unknown", "", outputs, facts.ToArray());
    }

    private static JsonElement Envelope(IReadOnlyList<HealRow> rows) => RuntimeJson.From(new { results = rows });
}
