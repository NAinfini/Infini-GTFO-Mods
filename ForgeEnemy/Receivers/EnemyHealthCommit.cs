using System;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Receivers;

internal sealed record EnemyHealthSnapshot(EntityReference Target, IntPtr Receiver,
    bool IsSetup, bool Alive, float Health, float Maximum);

/// <summary>One synchronous native submission. No retry, timer, registration or second ledger.</summary>
internal sealed class EnemyHealthCommit : EnemyThreadBoundary
{
    internal const double MinimumAmount = 0.000001;
    internal const double MaximumAmount = 1000000;
    private readonly Func<bool> _canExecute;
    private readonly Func<EntityReference, EnemyHealthSnapshot?> _read;
    private readonly Func<float, float, float> _quantize;
    private readonly Action<EnemyHealthSnapshot, float> _submit;
    private readonly string _healthChangedBinding;

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

    internal CommandResult Execute(EntityReference target, double requested)
    {
        CheckThread(); ArgumentNullException.ThrowIfNull(target);
        if (!double.IsFinite(requested) || requested < MinimumAmount || requested > MaximumAmount)
            return CommandResult.Rejected("gtfo.enemy.amount_out_of_range");
        EnemyHealthSnapshot before;
        float desired, quantized;
        try
        {
            if (!_canExecute()) return CommandResult.Rejected("gtfo.enemy.authority_or_phase");
            var state = _read(target);
            if (state == null || state.Target != target)
                return CommandResult.Rejected("gtfo.enemy.stale_or_unsupported_recipient");
            before = state;
            if (!before.IsSetup || before.Receiver == IntPtr.Zero)
                return CommandResult.Rejected("gtfo.enemy.missing_health_receiver");
            if (!float.IsFinite(before.Health) || !float.IsFinite(before.Maximum)
                || before.Maximum <= 0 || before.Health > before.Maximum)
                return CommandResult.Rejected("gtfo.enemy.invalid_health_state");
            if (!before.Alive || before.Health <= 0)
                return CommandResult.Rejected("gtfo.enemy.not_alive");
            desired = (float)Math.Min(before.Maximum, (double)before.Health + requested);
            quantized = _quantize(desired, before.Maximum);
            if (!float.IsFinite(quantized) || quantized < 0 || quantized > before.Maximum)
                return CommandResult.Rejected("gtfo.enemy.invalid_native_quantization");
            // Quantization/native getters can re-enter other mods. Never send a stale absolute HP value.
            if (!_canExecute()) return CommandResult.Rejected("gtfo.enemy.authority_or_phase");
            if (_read(target) != before)
                return CommandResult.Rejected("gtfo.enemy.state_changed_before_commit");
            if (!_canExecute()) return CommandResult.Rejected("gtfo.enemy.authority_or_phase");
        }
        catch (Exception error)
        {
            return CommandResult.Failed("gtfo.enemy.preflight_exception", error.GetType().Name);
        }
        if (desired <= before.Health || quantized <= before.Health)
            return Confirmed(before, requested, before.Health);

        // Crossing this boundary means a packet or local mutation may already have occurred.
        try { _submit(before, desired); }
        catch (Exception error)
        {
            return Unknown(before, requested, "gtfo.enemy.native_submit_exception", error.GetType().Name);
        }
        try
        {
            var after = _read(target);
            if (after == null || after.Target != target || after.Receiver != before.Receiver
                || !after.IsSetup || !after.Alive)
                return Unknown(before, requested, "gtfo.enemy.receiver_changed_during_commit");
            if (after.Maximum != before.Maximum || !float.IsFinite(after.Health)
                || after.Health < before.Health || after.Health > before.Maximum)
                return Unknown(before, requested, "gtfo.enemy.unexpected_health_readback");
            return Confirmed(before, requested, after.Health);
        }
        catch (Exception error)
        {
            return Unknown(before, requested, "gtfo.enemy.readback_exception", error.GetType().Name);
        }
    }

    private static CommandResult Unknown(EnemyHealthSnapshot before, double requested,
        string code, string detail = "") => CommandResult.FailedUnknown(RuntimeJson.From(new
        {
            target = before.Target, requestedAmount = requested, healthBefore = before.Health,
            overflowAmount = Math.Max(0, (double)before.Health + requested - before.Maximum)
        }), code, detail); // No invented actualAmount, healthAfter or health-change fact.

    private CommandResult Confirmed(EnemyHealthSnapshot before, double requested, float after)
    {
        double actual = (double)after - before.Health;
        JsonElement output = RuntimeJson.From(new
        {
            target = before.Target, requestedAmount = requested, actualAmount = actual,
            healthBefore = before.Health, healthAfter = after,
            overflowAmount = Math.Max(0, (double)before.Health + requested - before.Maximum)
        });
        return actual > 0 ? CommandResult.Succeeded(output, new RuntimeFact(_healthChangedBinding,
            RuntimeJson.From(new { target = before.Target, health_before = before.Health,
                health_after = after, delta = actual }))) : CommandResult.Succeeded(output);
    }
}
