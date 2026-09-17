using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Native;

/// <summary>
/// The four numerical instance-override rows as this package executes them: `fire_rate`, `spread`, `recoil` and
/// the generic `property`. The decisions are made in <see cref="WeaponActionRuntime"/> and the writes in
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
        Current = this;
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
    internal CommandResult FireRate(CommandContext context)
        => Run(context, WeaponActionRuntime.FireRate, NamedFields);

    /// <summary>`forge.action.weapon.spread`. Result fields: the four fixed columns, then `cone`.</summary>
    internal CommandResult Spread(CommandContext context)
        => Run(context, WeaponActionRuntime.Spread, NamedFields);

    /// <summary>`forge.action.weapon.recoil`. Result fields: the four fixed columns, then `horizontal`.</summary>
    internal CommandResult Recoil(CommandContext context)
        => Run(context, WeaponActionRuntime.Recoil, NamedFields);

    /// <summary>`forge.action.weapon.property`. Result fields: the four fixed columns, then `field` and `value`.</summary>
    internal CommandResult Property(CommandContext context)
        => Run(context, WeaponActionRuntime.Property, PropertyFields);

    /// <summary>
    /// The `property` row's own restore callback, which the kernel calls when the effect its card asked for ends —
    /// by its duration, by a cancellation, by a released plan or by the world. What ends is what this card's writer
    /// set on the instance the application was about; a request the kernel kept after the module had already undone
    /// it is the no-op it should be.
    ///
    /// The context's subject is the row's own `equipment` recipient, and the kernel's plan/node identity is the
    /// ledger's writer identity, so nothing here has to keep a table of handles beside the ledger.
    /// </summary>
    internal static void RestoreProperty(RuntimeEffectContext context)
    {
        if (Current is not { } adapter || context.Subject.Count == 0) return;
        if (!adapter._applier.Ledger.RevertWriter(context.Subject[0], context.PlanId, context.NodeId, out var code))
            adapter._report("weapon.override-restore-refused equipment=" + context.Subject[0].Id + " code=" + code);
    }

    /// <summary>The adapter the registration's own restore table answers from, for the same reason the handler
    /// table does: the table is built before the adapter that holds the ledger exists, so one package registers one
    /// adapter and the table holds a static entry point that answers from here.</summary>
    internal static WeaponActionAdapter? Current { get; private set; }

    /// <summary>
    /// The shared body of the four block-replacing rows: decide, refuse as decided, otherwise record the request
    /// in the ledger — which is what writes it — and answer with the row's own result fields.
    /// </summary>
    private CommandResult Run(CommandContext context, Func<CommandContext, WeaponActionOutcome> decide,
        Func<IReadOnlyList<WeaponOverrideField>, IReadOnlyList<object>> extra)
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
            AppliedCode, extra(outcome.Fields)));
    }

    /// <summary>The one line every accepted request answers with, in the row's own field order: the four fixed
    /// columns first, then this row's extra fields. `target` is the instance the override landed on, and the
    /// commit state distinguishes a write that happened from a request that was refused.</summary>
    private static JsonElement Rows(EntityReference equipment, string status, string commitState, string code,
        IReadOnlyList<object> extra)
        => RuntimeJson.From(new { rows = new[] { new ResultRow(equipment, status, commitState, code, extra) } });

    /// <summary>
    /// The named rows' own extra result fields, in catalog order and only for the fields the request actually
    /// set. A port the plan left out is absent from the result rather than reported as zero: zero is a value this
    /// package would have had to invent.
    /// </summary>
    private static IReadOnlyList<object> NamedFields(IReadOnlyList<WeaponOverrideField> fields)
    {
        var extra = new List<object>(1);
        foreach (var field in fields)
        {
            switch (field.Name)
            {
                case "fire_rate":
                    // The row's own field is the rate the plan asked for, which is the unit the ledger stores:
                    // the native seconds-between-shots value is produced at the write and never travels back.
                    extra.Add(new RateField(field.Value));
                    break;
                case "recoil_horizontal":
                    extra.Add(new HorizontalField(field.Value));
                    break;
                case "spread_cone":
                    extra.Add(new ConeField(field.Value));
                    break;
            }
        }
        return extra;
    }

    /// <summary>`property`'s two extra fields: which name was accepted, and the value that was resolved against
    /// it. Both are the request's own, so a caller reads back what this row took rather than what the block now
    /// stores — the block's value is the native member, which is a different unit for `fire_rate`.</summary>
    private static IReadOnlyList<object> PropertyFields(IReadOnlyList<WeaponOverrideField> fields)
    {
        var field = fields[0];
        return new object[] { new PropertyNameField(field.Name), new PropertyValueField(field.Value) };
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

    /// <summary>`property`'s own result fields.</summary>
    private sealed record PropertyNameField([property: JsonPropertyName("field")] string Field);

    private sealed record PropertyValueField([property: JsonPropertyName("value")] double Value);
}
