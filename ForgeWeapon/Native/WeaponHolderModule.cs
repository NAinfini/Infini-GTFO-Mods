using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Native;

/// <summary>
/// The holder provider's registration and its two handlers: the executable half of
/// <see cref="WeaponHolderChannelContract"/>.
///
/// The provider is its own module rather than a set of rows on <see cref="ModuleDefinition.ProviderId"/>, because
/// the two facts that make an `owner` row executable — the execution tier and the owner-session resolver — are
/// registration facts of the module that owns the binding. One provider, one resolver table, one tier: a step
/// whose holder cannot be named is refused by the tier with <c>owner-holder</c>, and nothing is sent anywhere.
///
/// A handler here runs on the holder's machine and nowhere else. The kernel invokes it through its own owner
/// entry point, which is the same invocation path a host-tier handler goes through with one difference:
/// <see cref="CommandContext.IsHost"/> is false, because the advance that decided the timing ran on another
/// machine. Both handlers refuse a command that arrives while this machine is not the addressed holder, which is
/// the check that keeps a reload from ever landing on the wrong inventory.
/// </summary>
internal sealed class WeaponHolderModule : IDisposable
{
    private readonly RuntimeKernel _kernel;
    private readonly Action<string> _report;
    private readonly RuntimeModuleHandle _registration;
    private bool _disposed;

    private WeaponHolderModule(RuntimeKernel kernel, RuntimeLogLevel logLevel, Action<string> report)
    {
        _kernel = kernel;
        _report = report;
        _registration = kernel.RegisterModule(Build(), logLevel);
    }

    /// <summary>Registers the holder provider. The caller owns the returned module and disposes it at unload.</summary>
    internal static WeaponHolderModule Register(RuntimeKernel kernel, RuntimeLogLevel logLevel, Action<string> report)
        => new(kernel ?? throw new ArgumentNullException(nameof(kernel)), logLevel,
            report ?? throw new ArgumentNullException(nameof(report)));

    /// <summary>The registration itself, built by the contract from the two handler bodies and the one resolver
    /// this machine can answer: the rows, their bindings, their shapes and their owner-session table all come from
    /// the same place, so a declared row can never lack the resolver its tier needs.</summary>
    private RuntimeModule Build()
        => WeaponHolderActionsContract.Module(WeaponHolderChannel.SessionOf, Reload, ClipSet, AutoFire);

    /// <summary>`forge.action.weapon.reload`. Refusals, in the order they are checked: a command that is not
    /// addressed here, an equipment instance this machine cannot see, an equipment that is not the held item, and
    /// the game's own reload check. The result's `clip` is read back after the body ran, so a plan reads what the
    /// weapon holds rather than what it asked for.</summary>
    private CommandResult Reload(CommandContext context)
    {
        if (!Read(context, out var equipment, out var holder, out var failure)) return failure!;
        if (!WeaponHolderChannel.IsLocalHolder(equipment))
            return CommandResult.Rejected(WeaponHolderChannelContract.NotAddressedCode);
        // `drop` is the one structural member this build has no native behaviour for: the reload path keeps the
        // chambered round, and a request that asked for the other behaviour is refused rather than served as
        // `keep` under a different name. `transfer_policy` has the same shape for the reserve pool.
        var policy = Text(context.Parameters, "chamber_policy");
        if (policy != null && !string.Equals(policy, "keep", StringComparison.Ordinal))
            return CommandResult.Rejected(WeaponHolderChannelContract.UnsupportedPolicyCode);
        var transfer = Text(context.Parameters, "transfer_policy");
        if (transfer != null && !string.Equals(transfer, "magazine", StringComparison.Ordinal))
            return CommandResult.Rejected(WeaponHolderChannelContract.UnsupportedPolicyCode);
        if (Present(context.Inputs, "reload_profile"))
            return CommandResult.Rejected(WeaponHolderChannelContract.UnsupportedPolicyCode);
        if (!WeaponHolderChannel.TryReload(equipment, out var clip, out var code))
        {
            _report("weapon.holder-reload-refused equipment=" + equipment.Id + " code=" + code);
            return CommandResult.Rejected(code);
        }
        return CommandResult.Succeeded(Rows(equipment, clip), new[]
        {
            new RuntimeFact(WeaponHolderActionsContract.ReloadBinding,
                RuntimeJson.From(new { actor = holder, equipment, clip }))
        });
    }

    /// <summary>`forge.action.weapon.clip_set`. The amount is read from the frame only when the structural policy
    /// asks for one; `fill` reads the weapon's own capacity instead. A count above that capacity is refused by
    /// name, never clamped.</summary>
    private CommandResult ClipSet(CommandContext context)
    {
        if (!Read(context, out var equipment, out var holder, out var failure)) return failure!;
        if (!WeaponHolderChannel.IsLocalHolder(equipment))
            return CommandResult.Rejected(WeaponHolderChannelContract.NotAddressedCode);
        var policy = Text(context.Parameters, "clip_policy");
        var hasAmount = TryInteger(context.Inputs, "amount", out var amount);
        if (!WeaponHolderChannel.TrySetClip(equipment, policy ?? "", hasAmount, amount, out var clip, out var code))
        {
            _report("weapon.holder-clip-refused equipment=" + equipment.Id + " code=" + code);
            return CommandResult.Rejected(code);
        }
        return CommandResult.Succeeded(Rows(equipment, clip), new[]
        {
            new RuntimeFact(WeaponHolderActionsContract.ClipSetBinding,
                RuntimeJson.From(new { actor = holder, equipment, clip }))
        });
    }

    /// <summary>
    /// `forge.action.weapon.auto_fire`. The same order of checks as the magazine rows — addressed here, the
    /// equipment is this machine's held instance — and then the one this row adds: the weapon's own state has to
    /// be the one the request named. The shot is the native `Fire` body, so the magazine, the fire rate and the
    /// replicated shot count are the game's; the result carries the fixed columns alone, because one `Fire` body
    /// is one shot and a count here would always read one.
    /// </summary>
    private CommandResult AutoFire(CommandContext context)
    {
        if (!Read(context, out var equipment, out var holder, out var failure)) return failure!;
        if (!WeaponHolderChannel.IsLocalHolder(equipment))
            return CommandResult.Rejected(WeaponHolderChannelContract.NotAddressedCode);
        var requiredState = Text(context.Parameters, "required_state") ?? WeaponHolderActionsContract.StateNone;
        if (!WeaponHolderChannel.TryFire(equipment, requiredState, out var code))
        {
            _report("weapon.holder-fire-refused equipment=" + equipment.Id + " state=" + requiredState + " code=" + code);
            return CommandResult.Rejected(code);
        }
        return CommandResult.Succeeded(Fired(equipment), new[]
        {
            new RuntimeFact(WeaponHolderActionsContract.AutoFireBinding,
                RuntimeJson.From(new { actor = holder, equipment }))
        });
    }

    /// <summary>The two ports both handlers read, plus the refusals that belong to reading them. A frame with no
    /// equipment, or with one this machine has no live instance for, is refused before any write is attempted.
    /// The `holder` port is carried into the fact's actor port: it is the plan's own declaration of whose weapon
    /// this is, and it is never invented here.</summary>
    private bool Read(CommandContext context, out EntityReference equipment, out EntityReference? holder, out CommandResult? failure)
    {
        failure = null;
        holder = null;
        equipment = null!;
        if (!TryEntity(context.Inputs, "equipment", out equipment) || !_kernel.IsEntityCurrent(equipment))
        {
            failure = CommandResult.Rejected(WeaponHolderChannelContract.StaleEquipmentCode);
            return false;
        }
        holder = TryEntity(context.Inputs, "holder", out var named) ? named : null;
        return true;
    }

    /// <summary>One result row: the four fixed columns and this row's own `clip`, which is the magazine as it
    /// stands after the write.</summary>
    private static JsonElement Rows(EntityReference equipment, int clip)
        => RuntimeJson.From(new
        {
            rows = new[]
            {
                new
                {
                    target = equipment, status = CommandStatuses.Succeeded,
                    committed = CommitStates.Confirmed, code = "", clip
                }
            }
        });

    /// <summary>One result row for the fire row: the four fixed columns every action result carries. A shot that
    /// the native body really produced is the fact of this row having run at all, so there is no fifth column.</summary>
    private static JsonElement Fired(EntityReference equipment)
        => RuntimeJson.From(new
        {
            rows = new[]
            {
                new
                {
                    target = equipment, status = CommandStatuses.Succeeded,
                    committed = CommitStates.Confirmed, code = ""
                }
            }
        });

    private static bool TryEntity(JsonElement inputs, string id, out EntityReference reference)
    {
        reference = null!;
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(id, out var value)) return false;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var resolved = RuntimeJson.Entity(value);
        if (string.IsNullOrEmpty(resolved.Id)) return false;
        reference = resolved;
        return true;
    }

    private static bool TryInteger(JsonElement inputs, string id, out int value)
    {
        value = 0;
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(id, out var element)) return false;
        if (element.ValueKind != JsonValueKind.Number) return false;
        return element.TryGetInt32(out value);
    }

    private static string? Text(JsonElement parameters, string id)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(id, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Present(JsonElement inputs, string id)
        => inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(id, out var value)
           && value.ValueKind != JsonValueKind.Null;

    public void Dispose()
    {
        if (_disposed) return;
        _registration.Dispose();
        _disposed = true;
    }
}
