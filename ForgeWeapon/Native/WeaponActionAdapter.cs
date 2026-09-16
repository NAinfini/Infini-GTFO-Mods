using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Native;

/// <summary>
/// The three numerical instance-override rows as this package executes them: `fire_rate`, `spread` and `recoil`.
/// The decisions are made in <see cref="WeaponActionRuntime"/> and the writes in
/// <see cref="WeaponOverrideApplier"/>; this file is the seam between them and one dispatched command — it reads
/// the request frame, hands the decision a ledger to record against, and answers with the row's own result
/// schema.
///
/// Two rules are enforced here and not earlier. A row only ever runs where it is authoritative: the kernel
/// dispatches a command on the host, and this session's own readiness and host flag are the same gate the rest
/// of the package's actions check, so a replica never writes a block. And a request about an instance whose life
/// has ended is refused with `stale-equipment` before the applier is asked for anything — a reference from a
/// previous life names a weapon that no longer exists, and an instance override is meaningless without one.
/// </summary>
internal sealed class WeaponActionAdapter
{
    /// <summary>The refusal for a command dispatched where this package may not write, spelled the same way the
    /// package's other actions spell it.</summary>
    internal const string AuthorityCode = "authority-or-phase";

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly WeaponOverrideApplier _applier;
    private readonly Action<string> _report;

    internal WeaponActionAdapter(RuntimeKernel kernel, Func<bool> canExecute, WeaponOverrideApplier applier,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Whether this side may write an instance's block: the host's own readiness and the host flag the
    /// kernel recorded for the advance. Both are read from the same lifecycle the kernel dispatches under, so
    /// this is one gate and not a second one.</summary>
    internal bool Authoritative()
    {
        if (!_canExecute()) return false;
        var state = _kernel.Lifecycle;
        return state.StartupState == RuntimeStartupState.Ready && state.IsHost == true;
    }

    /// <summary>`forge.action.weapon.fire_rate`. Result fields: the four fixed columns, then `rate`.</summary>
    internal CommandResult FireRate(CommandContext context) => Run(context, WeaponActionRuntime.FireRate);

    /// <summary>`forge.action.weapon.spread`. Result fields: the four fixed columns, then `cone`.</summary>
    internal CommandResult Spread(CommandContext context) => Run(context, WeaponActionRuntime.Spread);

    /// <summary>`forge.action.weapon.recoil`. Result fields: the four fixed columns, then `horizontal`.</summary>
    internal CommandResult Recoil(CommandContext context) => Run(context, WeaponActionRuntime.Recoil);

    /// <summary>
    /// The shared body of the three block-replacing rows: decide, refuse as decided, otherwise record the request
    /// in the ledger — which is what writes it — and answer with the row's own result fields.
    /// </summary>
    private CommandResult Run(CommandContext context, Func<CommandContext, WeaponActionOutcome> decide)
    {
        if (!Authoritative()) return CommandResult.Rejected(AuthorityCode);
        var outcome = decide(context);
        if (outcome.Code.Length != 0) return CommandResult.Rejected(outcome.Code);
        var equipment = outcome.Equipment!;
        if (!_kernel.IsEntityCurrent(equipment))
            return CommandResult.Rejected(WeaponOverrideLedger.StaleEquipmentCode);

        var request = new WeaponOverrideRequest(equipment, outcome.Fields,
            new WeaponOverrideSource(context.PlanId ?? "", context.NodeId ?? "", context.SimulationTick),
            Ticks(context.Inputs, "duration"));
        if (!_applier.Ledger.Begin(request, context.SimulationTick, out var code))
        {
            _report("weapon.override-refused equipment=" + equipment.Id + " code=" + code);
            return CommandResult.Rejected(code);
        }
        return CommandResult.Succeeded(Rows(equipment, CommandStatuses.Succeeded, CommitStates.Confirmed,
            AppliedCode, ResultFields(outcome)));
    }

    /// <summary>The one line every accepted request answers with, in the row's own field order: the four fixed
    /// columns first, then this row's extra fields. `target` is the instance the override landed on, and the
    /// commit state distinguishes a write that happened from a request that was refused.</summary>
    private static JsonElement Rows(EntityReference equipment, string status, string commitState, string code,
        IReadOnlyList<object> extra)
        => RuntimeJson.From(new { rows = new[] { new ResultRow(equipment, status, commitState, code, extra) } });

    /// <summary>
    /// The row's own extra result fields, in catalog order and only for the fields the request actually set. A
    /// port the plan left out is absent from the result rather than reported as zero: zero is a value this
    /// package would have had to invent.
    /// </summary>
    private static IReadOnlyList<object> ResultFields(WeaponActionOutcome outcome)
    {
        var fields = new List<object>(1);
        foreach (var field in outcome.Fields)
        {
            switch (field.Name)
            {
                case "fire_rate":
                    // The row's own field is the rate the plan asked for, so the native seconds-between-shots
                    // value is converted back rather than reported raw.
                    fields.Add(new RateField(WeaponActionRuntime.RateFromShotDelay(field.Value)));
                    break;
                case "recoil_horizontal":
                    fields.Add(new HorizontalField(field.Value));
                    break;
                case "spread_cone":
                    fields.Add(new ConeField(field.Value));
                    break;
            }
        }
        return fields;
    }

    /// <summary>A duration input in ticks, or 0 for a request that lasts as long as the life does. An absent
    /// duration and an explicit zero are the same request; the ledger is what turns a positive value into an
    /// absolute expiry.</summary>
    private static long Ticks(JsonElement inputs, string id)
    {
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(id, out var value)) return 0;
        if (value.ValueKind != JsonValueKind.Number) return 0;
        return value.TryGetInt64(out var ticks) && ticks > 0 ? ticks : 0;
    }

    /// <summary>The code an accepted override answers with, so a caller can tell a write from a refusal without
    /// reading the status twice.</summary>
    internal const string AppliedCode = "override-applied";

    /// <summary>One result row: the four columns every action row carries, then this row's own fields, in the
    /// catalog's order.</summary>
    private sealed record ResultRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code,
        [property: JsonPropertyName("fields")] IReadOnlyList<object> Fields);

    /// <summary>`fire_rate`'s own result field.</summary>
    private sealed record RateField([property: JsonPropertyName("rate")] double Rate);

    /// <summary>`recoil`'s own result field.</summary>
    private sealed record HorizontalField([property: JsonPropertyName("horizontal")] double Horizontal);

    /// <summary>`spread`'s own result field.</summary>
    private sealed record ConeField([property: JsonPropertyName("cone")] double Cone);
}
