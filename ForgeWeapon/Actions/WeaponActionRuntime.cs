using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>What one weapon action decided, before the native half has written anything. The four numeric rows
/// all have the same life: read the request's own ports, turn them into a field set, and let the applier write
/// that set. An outcome that carries no fields is a refusal, and the code names which of the row's ports or
/// which native destination was the reason — never a silent success.
///
/// The status and commit-state spellings are the wire's own (`CommandStatuses`/`CommitStates`), because a refusal
/// reported here is the same refusal the registration answers with; the native adapter is the one place that
/// turns this into a `CommandResult`.</summary>
public sealed record WeaponActionOutcome(
    EntityReference? Equipment,
    string Status,
    string CommitState,
    string Code,
    IReadOnlyList<WeaponOverrideField> Fields)
{
    /// <summary>A request this action can carry: the field set the applier is asked to write.</summary>
    public static WeaponActionOutcome Ready(EntityReference equipment, IReadOnlyList<WeaponOverrideField> fields)
        => new(equipment, CommandStatuses.Succeeded, CommitStates.Confirmed, "", fields ?? Array.Empty<WeaponOverrideField>());

    /// <summary>A request this action will not carry, with the code that says why. The code is the detail a plan
    /// reads; a refusal without one would be indistinguishable from a success with no fields.</summary>
    public static WeaponActionOutcome Refuse(EntityReference? equipment, string code)
        => new(equipment, CommandStatuses.Rejected, CommitStates.None, code, Array.Empty<WeaponOverrideField>());
}

/// <summary>
/// The three numerical `forge.action.weapon.*` rows this slice owns, as pure functions from a dispatched command
/// to the instance values it asks for: `fire_rate`, `spread` and `recoil`. The port names below are the catalog's
/// own, port for port; a port this build cannot serve is refused by name rather than dropped, so a plan author is
/// told which input the game has no destination for.
///
/// What every one of these rows has in common is ruling 84's answer to "how does a runtime weapon value change
/// at all": this instance gets its own copy of the block it points at, the copy's fields are changed, and the
/// instance is pointed at the copy. Nothing here writes the shared block and nothing here builds a Forge-only
/// override layer that the player's own trigger would not honour.
/// </summary>
public static class WeaponActionRuntime
{
    /// <summary>The refusal for a row's own structural choice that this build's native entry point does not
    /// implement — a spread pattern the cone has no variant of.</summary>
    public const string ModeUnsupportedCode = "weapon-mode-unsupported";
    /// <summary>The refusal for an input this build cannot honour: a spread seed nothing consumes.</summary>
    public const string InputUnsupportedCode = "input-unsupported";

    /// <summary>Seconds between shots, the native `ArchetypeDataBlock.ShotDelay`, from a rate in shots per
    /// second. The catalog declares `rate` with no unit, so the reading is stated here and nowhere else: a rate
    /// is a frequency, and the native field it becomes is its reciprocal. The ledger stores the rate and the
    /// native half converts at the write, so `add` on `fire_rate` means "two more shots per second" and not
    /// "two more seconds between shots".</summary>
    public static double ShotDelayFromRate(double shotsPerSecond)
        => shotsPerSecond > 0 ? 1d / shotsPerSecond : 0d;

    /// <summary>Shots per second from the native seconds-between-shots value. The native half reads an
    /// instance's own `ShotDelay` through this when it has to resolve an `add` or a `subtract` against the
    /// value the block held before this package touched it.</summary>
    public static double RateFromShotDelay(double shotDelay)
        => shotDelay > 0 ? 1d / shotDelay : 0d;

    /// <summary>The weapon families that decide which of the field set's destinations exist on one instance.
    /// `shotgun` owns the cone and the per-pellet spread reads; every other bullet family reads the two spread
    /// values but no cone. `burst` owns the burst length field — the other archetype classes have no such field
    /// at all, so a burst length is not something they can hold.</summary>
    public enum WeaponFamily
    {
        Bullet,
        Shotgun,
        Burst
    }

    /// <summary>
    /// Whether one field has a destination on this instance, and which of the two destination groups it belongs
    /// to. This is the check that makes a refusal leave the weapon untouched: the applier asks it for every field
    /// of a request before the first write, so a request that names one value this instance cannot hold changes
    /// nothing at all.
    ///
    /// The three families are the shipped code's own: the cone and the per-pellet spread are read only by the
    /// shotgun fire path, the burst length field exists only on `BWA_Burst`/`BWA_SemiBurst`, and everything else
    /// is a value every bullet archetype reads every shot.
    /// </summary>
    public static bool HasDestination(string field, WeaponFamily family, out bool needsRecoil)
    {
        needsRecoil = false;
        switch (field)
        {
            case "fire_rate":
            case "spread_movement_scale":
            case "spread_aim_scale":
                return true;
            case "spread_cone":
                // A weapon whose fire path never reads the cone would take the write and show nothing.
                return family == WeaponFamily.Shotgun;
            case "burst_count":
                return family == WeaponFamily.Burst;
            case "recoil_horizontal":
            case "recoil_vertical":
            case "recoil_recovery":
            case "recoil_camera_kick":
                needsRecoil = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// `forge.action.weapon.fire_rate`. Ports: `equipment`, `source`, `rate`, `duration`. The `source` port is
    /// read as the request's own identity is read — it is the writer the merge order sorts by — and it is not
    /// copied anywhere: nothing in this package creates a player or an entity reference.
    /// </summary>
    public static WeaponActionOutcome FireRate(CommandContext context)
    {
        var equipment = Equipment(context);
        if (equipment == null) return WeaponActionOutcome.Refuse(null, WeaponOverrideLedger.StaleEquipmentCode);
        if (!TryNumber(context.Inputs, "rate", out var rate))
            return WeaponActionOutcome.Refuse(equipment, ModeUnsupportedCode);
        if (rate <= 0)
            return WeaponActionOutcome.Refuse(equipment, InputUnsupportedCode);
        return WeaponActionOutcome.Ready(equipment, new[]
        {
            new WeaponOverrideField("fire_rate", rate)
        });
    }

    /// <summary>
    /// `forge.action.weapon.property`: the generic form of the three rows above, and the one every "change a
    /// weapon number for a while" card lands on. Ports: `equipment`, `value`; structural parameters `field` (a
    /// name from <see cref="WeaponOverrideLedger.Fields"/>) and `operation` (`set`, `add`, `subtract`).
    ///
    /// The value is in the unit the field's own name states — the same unit the specific row for that field uses
    /// — and the native half is what converts it to the member the game reads. A field this build has no write
    /// point for is refused with the ledger's own `override-field-unknown`, so an author is told which name is
    /// not served rather than getting a silently ignored write. The effect handle the row declares is the
    /// kernel's: this row answers with the target and the accepted value, the plan's own `effect` block times the
    /// change, and the module's restore callback is what undoes the write when that effect ends.
    /// </summary>
    public static WeaponActionOutcome Property(CommandContext context)
    {
        var equipment = Equipment(context);
        if (equipment == null) return WeaponActionOutcome.Refuse(null, WeaponOverrideLedger.StaleEquipmentCode);
        var name = Parameter(context.Parameters, "field");
        if (name == null) return WeaponActionOutcome.Refuse(equipment, ModeUnsupportedCode);
        if (!WeaponOverrideLedger.IsKnownField(name))
            return WeaponActionOutcome.Refuse(equipment, WeaponOverrideLedger.UnknownFieldCode);
        if (!WeaponOverrideLedger.TryParseOperation(Parameter(context.Parameters, "operation"), out var operation))
            return WeaponActionOutcome.Refuse(equipment, ModeUnsupportedCode);
        if (!TryNumber(context.Inputs, "value", out var value))
            return WeaponActionOutcome.Refuse(equipment, InputUnsupportedCode);
        // Every destination behind these names is a non-negative physical quantity, so a negative `set` is a
        // value the game cannot hold rather than an unusual one. A negative `add` is a `subtract` spelled the
        // other way and is left alone.
        if (operation == WeaponOverrideOperation.Set && value < 0)
            return WeaponActionOutcome.Refuse(equipment, InputUnsupportedCode);
        return WeaponActionOutcome.Ready(equipment, new[] { new WeaponOverrideField(name, value, operation) });
    }

    /// <summary>
    /// `forge.action.weapon.spread`. Ports: `equipment`, `cone`, `seed`, `movement_scale`, `aim_scale`, and the
    /// structural `pattern`.
    ///
    /// The cone is the shot-spread cone the shotgun path reads per pellet; `movement_scale` and `aim_scale` are
    /// the two spread values a bullet weapon reads per shot, scaled by the inputs. Two of the row's own ports
    /// have no destination and are refused rather than half-served: `spiral` is a pattern the native cone has no
    /// variant of, and `seed` is a randomness control for a spread the native path computes deterministically.
    /// `random` and `fixed` are both accepted because both describe what the native cone actually is — the same
    /// cone, the same sampling, no per-instance randomness to seed.
    /// </summary>
    public static WeaponActionOutcome Spread(CommandContext context)
    {
        var equipment = Equipment(context);
        if (equipment == null) return WeaponActionOutcome.Refuse(null, WeaponOverrideLedger.StaleEquipmentCode);
        var pattern = Parameter(context.Parameters, "pattern");
        if (pattern != null && !string.Equals(pattern, "random", StringComparison.Ordinal)
            && !string.Equals(pattern, "fixed", StringComparison.Ordinal))
            return WeaponActionOutcome.Refuse(equipment, ModeUnsupportedCode);
        if (Present(context.Inputs, "seed"))
            return WeaponActionOutcome.Refuse(equipment, InputUnsupportedCode);

        var fields = new List<WeaponOverrideField>(3);
        if (TryNumber(context.Inputs, "cone", out var cone))
        {
            if (cone < 0) return WeaponActionOutcome.Refuse(equipment, InputUnsupportedCode);
            fields.Add(new WeaponOverrideField("spread_cone", cone));
        }
        if (TryNumber(context.Inputs, "movement_scale", out var movement))
            fields.Add(new WeaponOverrideField("spread_movement_scale", movement));
        if (TryNumber(context.Inputs, "aim_scale", out var aim))
            fields.Add(new WeaponOverrideField("spread_aim_scale", aim));
        return fields.Count == 0
            ? WeaponActionOutcome.Refuse(equipment, InputUnsupportedCode)
            : WeaponActionOutcome.Ready(equipment, fields);
    }

    /// <summary>
    /// `forge.action.weapon.recoil`. Ports: `equipment`, `horizontal`, `vertical`, `recovery`, `camera_kick`.
    /// The four are the four values the recoil block's own application path reads: the two scale ranges, the
    /// spring stiffness and the impulse power. The row carries no `profile` resource and no construction
    /// parameters — ruling 84 removed both — so a recoil request is always a change to this instance's block.
    /// </summary>
    public static WeaponActionOutcome Recoil(CommandContext context)
    {
        var equipment = Equipment(context);
        if (equipment == null) return WeaponActionOutcome.Refuse(null, WeaponOverrideLedger.StaleEquipmentCode);
        var fields = new List<WeaponOverrideField>(4);
        if (TryNumber(context.Inputs, "horizontal", out var horizontal))
            fields.Add(new WeaponOverrideField("recoil_horizontal", horizontal));
        if (TryNumber(context.Inputs, "vertical", out var vertical))
            fields.Add(new WeaponOverrideField("recoil_vertical", vertical));
        if (TryNumber(context.Inputs, "recovery", out var recovery))
            fields.Add(new WeaponOverrideField("recoil_recovery", recovery));
        if (TryNumber(context.Inputs, "camera_kick", out var kick))
            fields.Add(new WeaponOverrideField("recoil_camera_kick", kick));
        return fields.Count == 0
            ? WeaponActionOutcome.Refuse(equipment, InputUnsupportedCode)
            : WeaponActionOutcome.Ready(equipment, fields);
    }

    /// <summary>The `equipment` input every one of these rows addresses. A row whose equipment input is absent is
    /// a plan the loader already rejected; a row whose reference is present but not current is refused here,
    /// because an override on a dead instance would be a write to a block nothing reads.
    ///
    /// The reference is never invented from the holder: the `source` port of `fire_rate` names the writer, not
    /// the weapon, and the weapon is the port this action is attached to.</summary>
    private static EntityReference? Equipment(CommandContext context)
    {
        if (!context.Inputs.TryGetProperty("equipment", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object) return null;
        var reference = RuntimeJson.Entity(value);
        return string.IsNullOrEmpty(reference.Id) ? null : reference;
    }

    /// <summary>One node parameter as text, or null when the plan did not set it. A structural parameter is the
    /// plan's constant, so it is read from the node's own bag and never from the request frame.</summary>
    private static string? Parameter(JsonElement parameters, string id)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(id, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A numeric input that the caller actually supplied. Absent and null are the same "not asked for":
    /// a row may name any subset of its own ports, and the ones it leaves out keep the instance's own value.</summary>
    private static bool TryNumber(JsonElement inputs, string id, out double value)
    {
        value = 0;
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(id, out var element)) return false;
        if (element.ValueKind == JsonValueKind.Null) return false;
        if (element.ValueKind != JsonValueKind.Number) return false;
        value = element.GetDouble();
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>Whether the plan actually supplied an input: absent and a present null are the same "not asked
    /// for", which is how a port this build cannot honour is told from one the author left out.</summary>
    private static bool Present(JsonElement inputs, string id)
        => inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(id, out var value)
           && value.ValueKind != JsonValueKind.Null;
}
