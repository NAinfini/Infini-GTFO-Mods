using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>One row of `forge.result.combat.impulse`: the receiver, the conclusion, the commit state, the code, the
/// strength this command wrote and how many recipients the command had. `committed` and `target_count` carry the
/// schema's own spelling, because the row is the schema's and not a C# name of it.</summary>
internal sealed record GenericImpulseRow(EntityReference? Target, string Status,
    [property: JsonPropertyName("committed")] string CommitState, string Code,
    double Strength, [property: JsonPropertyName("target_count")] int TargetCount);

/// <summary>One row of `forge.result.player.stamina_change`: the same columns, with the change the receiver's own
/// readback showed as the amount.</summary>
internal sealed record GenericStaminaRow(EntityReference? Target, string Status,
    [property: JsonPropertyName("committed")] string CommitState, string Code,
    double Amount, [property: JsonPropertyName("target_count")] int TargetCount);

/// <summary>One row of `forge.result.presentation.camera_shake`: which viewer's camera was shaken, the conclusion,
/// and the amplitude that reached it after the distance falloff.</summary>
internal sealed record GenericCameraShakeRow(EntityReference? Target, string Status,
    [property: JsonPropertyName("committed")] string CommitState, string Code,
    double Amplitude, [property: JsonPropertyName("target_count")] int TargetCount);

/// <summary>One row of `forge.result.presentation.screen_liquid`: which viewer's viewport was splashed and with
/// which preset.</summary>
internal sealed record GenericScreenLiquidRow(EntityReference? Target, string Status,
    [property: JsonPropertyName("committed")] string CommitState, string Code,
    string Preset, [property: JsonPropertyName("target_count")] int TargetCount);

/// <summary>The bodies the generic-player batch adds to the Map provider, on build 20403457:
///
/// <list type="bullet">
/// <item>`forge.action.combat.impulse` — one directional push at a player, through the player's own external-push
/// channel (`PlayerLocomotion.AddExternalPushForce`, dump.cs 656528, with `m_externalPushForce` at 656239 and the
/// readback at 656531). The row pushes and never damages: no damage entry is entered, so no health, limb or
/// hitreact state is touched.</item>
/// <item>`forge.action.player.stamina_change` — one immediate write to `PlayerStamina.Stamina` (dump.cs 541458, a
/// `0..1` value whose ceiling is the private constant `MaxStamina = 1` at 541437).</item>
/// <item>`forge.action.presentation.camera_shake` — `FPSCamera.Shake(duration, amplitude, frequency,
/// worldDirection)` (dump.cs 530638) on each viewer whose camera this machine holds.</item>
/// <item>`forge.action.presentation.screen_liquid` — `ScreenLiquidManager.Apply(setting, position, direction)`
/// (dump.cs 588852), the native entry that queues one liquid job for the local viewport.</item>
/// <item>`forge.query.player.movement_state` — the native `PlayerLocomotion.m_currentStateEnum` (the eighteen
/// members are dumped at 656698) and the machine's own state-entry time.</item>
/// </list>
///
/// The impulse row's recipient set is one set, as the ruling requires, and the handler dispatches on the reference
/// kind. The player arm is the one implemented here; the enemy arm is refused by name because this provider cannot
/// turn a `gtfo.enemy` reference into the native `EnemyAgent`: the enemy package owns that reference table, the
/// runtime publishes references and snapshots and no native instance, and a second enemy identity table built in
/// this half is the duplicate path the framework forbids. The report carries the missing interface.</summary>
internal static class GenericPlayerActions
{
    internal const string AuthorityCode = "authority-or-phase";
    internal const string NoTargetsCode = "no-targets";
    internal const string TooManyTargetsCode = "too-many-targets";
    internal const string StaleCode = "stale-or-unsupported-recipient";
    internal const string CommitExceptionCode = "native-commit-exception";

    internal const string ImpulseStrengthRequiredCode = "impulse-strength-required";
    internal const string ImpulseStrengthRangeCode = "impulse-strength-out-of-range";
    internal const string ImpulseDirectionCode = "impulse-direction-invalid";
    internal const string ImpulseTargetKindCode = "impulse-target-kind";

    internal const string AmountRangeCode = "amount-out-of-range";
    internal const string OperationCode = "stamina-operation-unsupported";
    internal const string StaminaTargetKindCode = "stamina-target-kind";

    internal const string CameraDurationRequiredCode = "camera-shake-duration-required";
    internal const string CameraDurationRangeCode = "camera-shake-duration-out-of-range";
    internal const string CameraAmplitudeRequiredCode = "camera-shake-amplitude-required";
    internal const string CameraAmplitudeRangeCode = "camera-shake-amplitude-out-of-range";
    internal const string CameraFrequencyRangeCode = "camera-shake-frequency-out-of-range";
    internal const string CameraRadiusCode = "camera-shake-radius-invalid";
    internal const string CameraDirectionCode = "camera-shake-direction-invalid";
    internal const string CameraViewerKindCode = "camera-shake-viewer-kind";
    internal const string CameraOutOfRangeCode = "camera-shake-out-of-range";
    internal const string CameraUnavailableCode = "camera-unavailable";

    internal const string LiquidPresetRequiredCode = "screen-liquid-preset-required";
    internal const string LiquidPresetUnknownCode = "screen-liquid-preset-unknown";
    internal const string LiquidViewerKindCode = "screen-liquid-viewer-kind";
    internal const string LiquidViewerCode = "screen-liquid-viewer-unsupported";
    internal const string LiquidNotAppliedCode = "screen-liquid-not-applied";
    internal const string ViewersRequiredCode = "viewers-required";
    internal const string ViewerCode = "viewers-unsupported";

    internal const string ImpulseAppliedCode = "impulse-applied";
    internal const string StaminaChangedCode = "stamina-changed";
    internal const string CameraIssuedCode = "camera-shake-issued";
    internal const string LiquidAppliedCode = "screen-liquid-applied";

    /// <summary>The bound on an authored impulse, in metres per second. It exists so a plan cannot hand the
    /// locomotion channel a number no body could carry; it is not a claim about the game's own ceiling.</summary>
    internal const double MaximumStrength = 100;

    /// <summary>The bounds on an authored shake: a duration no plan should hold a camera for, an amplitude far past
    /// anything the game's own events use, and a frequency ceiling. `duration` is a tick count like every other
    /// time port, so the ceiling is the same minute of plan time in ticks. Frequency `0` is a still offset the
    /// native entry accepts.</summary>
    internal const double MaximumDurationTicks = 60 / SecondsPerTick, MaximumAmplitude = 10, MaximumFrequency = 100;

    /// <summary>The seconds one tick of plan time is. Every time an author writes on this file's rows is ticks —
    /// the runtime's own simulation unit — and the native camera entry takes seconds, so the conversion happens
    /// once at the call.</summary>
    internal const double SecondsPerTick = 1.0 / 60.0;

    /// <summary>The epsilon a readback is compared with: the native members are floats, so a write is confirmed as
    /// "at least what was asked for", never bit for bit.</summary>
    internal const float ReadbackEpsilon = 0.001f;

    /// <summary>The two reference kinds the impulse row dispatches on.</summary>
    internal const string PlayerKind = PlayerStateContract.EntityKind;
    internal const string EnemyKind = "gtfo.enemy";

    private static readonly string[] Operations = { "set", "add", "subtract" };

    /// <summary>The execute bodies this half supplies, keyed by the handler names their contracts declare.</summary>
    internal static IReadOnlyDictionary<string, CommandHandler> Handlers() => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
    {
        [CombatImpulseContract.HandlerName] = Impulse,
        [PlayerStaminaContract.HandlerName] = StaminaChange,
        [PresentationActionContract.CameraShakeHandlerName] = CameraShake,
        [PresentationActionContract.ScreenLiquidHandlerName] = ScreenLiquid
    };

    /// <summary>The observe body this half supplies: the movement-state row, built from the one native read that
    /// can answer it.</summary>
    internal static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [PlayerMovementStateContract.HandlerName] =
            PlayerMovementStateContract.Evaluator(new PlayerMovementStateContract.MovementReaders(ReadMovement))
    };

    /// <summary>One support row per binding this half answers, in the same order as the handler table.</summary>
    internal static IReadOnlyList<BindingSupport> Support() => new[]
    {
        CombatImpulseContract.Support(),
        PlayerStaminaContract.Support(),
        PlayerMovementStateContract.Support()
    }.Concat(PresentationActionContract.Supports()).ToArray();

    /// <summary>The port layouts of every handler this half answers, composed so a registration that supplies
    /// these bodies alone declares the same shapes the Map registration does.</summary>
    internal static IReadOnlyDictionary<string, HandlerShape> Shapes() => CombatImpulseContract.Shapes()
        .Concat(PlayerStaminaContract.Shapes())
        .Concat(PlayerMovementStateContract.Shapes())
        .Concat(PresentationActionContract.Shapes())
        .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    // ------------------------------------------------------------------ impulse

    internal static CommandResult Impulse(CommandContext context) => Impulse(context.Inputs);

    /// <summary>`forge.action.combat.impulse`: applies one pure push vector to each player recipient. Direction
    /// normalization, falloff and target/source filtering are composed before this Outcome; this handler receives
    /// only the direction and scalar strength it must commit.</summary>
    internal static CommandResult Impulse(JsonElement inputs)
    {
        if (!inputs.TryGetProperty("strength", out var strengthElement) || strengthElement.ValueKind != JsonValueKind.Number)
            return CommandResult.Rejected(ImpulseStrengthRequiredCode);
        double strength = strengthElement.GetDouble();
        if (!double.IsFinite(strength) || strength <= 0 || strength > MaximumStrength)
            return CommandResult.Rejected(ImpulseStrengthRangeCode);
        if (!TryVector(inputs, "direction", out var direction) || !Finite(direction) || direction.sqrMagnitude <= 0f)
            return CommandResult.Rejected(ImpulseDirectionCode);
        var force = direction.normalized * (float)strength;
        if (!Finite(force)) return CommandResult.Rejected(ImpulseDirectionCode);

        var targets = Targets(inputs, "targets");
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TooManyTargetsCode);

        var players = PlayerIdentityModule.Current;
        var rows = new List<GenericImpulseRow>(targets.Length);
        int committed = 0;
        foreach (var target in targets)
        {
            GenericImpulseRow Row(string status, string state, string code, double strength = 0)
                => new(target, status, state, code, strength, targets.Length);

            if (players == null || !players.CanCommit) { rows.Add(Row("rejected", CommitStates.None, AuthorityCode)); continue; }
            if (!IsKind(target, PlayerKind)) { rows.Add(Row("rejected", CommitStates.None, ImpulseTargetKindCode)); continue; }
            var agent = players.CurrentAgent(target);
            if (agent == null) { rows.Add(Row("rejected", CommitStates.None, StaleCode)); continue; }
            PlayerLocomotion? locomotion;
            try
            {
                locomotion = agent.Locomotion;
            }
            catch (Exception) { rows.Add(Row("rejected", CommitStates.None, StaleCode)); continue; }
            if (locomotion == null || locomotion.Pointer == IntPtr.Zero)
            { rows.Add(Row("rejected", CommitStates.None, StaleCode)); continue; }

            float applied = force.magnitude;
            float readback;
            try
            {
                locomotion.AddExternalPushForce(force);
                readback = locomotion.GetExternalPushForce().magnitude;
            }
            catch (Exception) { rows.Add(Row("unknown", CommitStates.Unknown, CommitExceptionCode, applied)); continue; }
            // The channel accumulates, so the row claims a confirmed write only when it holds at least what this
            // command put there; less than that means the push did not reach the body.
            bool held = readback + ReadbackEpsilon >= applied;
            rows.Add(Row(held ? "succeeded" : "unknown", held ? CommitStates.Confirmed : CommitStates.Unknown,
                held ? ImpulseAppliedCode : CommitExceptionCode, applied));
            if (held) committed++;
        }
        return Aggregate(committed, rows.Count, rows[rows.Count - 1].Code, RuntimeJson.From(new { results = rows }));
    }

    // ------------------------------------------------------------------ stamina

    internal static CommandResult StaminaChange(CommandContext context) => StaminaChange(context.Parameters, context.Inputs);

    /// <summary>`forge.action.player.stamina_change`: the player's own stamina value, as an absolute write or a
    /// signed amount. The value is read before the write and read back after it, and the row reports the change the
    /// receiver's own readback showed, never the one that was asked for.</summary>
    internal static CommandResult StaminaChange(JsonElement parameters, JsonElement inputs)
    {
        string? operation = Text(parameters, "operation");
        if (operation == null || Array.IndexOf(Operations, operation) < 0) return CommandResult.Rejected(OperationCode);
        if (!TryAuthored(inputs, "amount", out double amount)) return CommandResult.Rejected(AmountRangeCode);
        if (Math.Abs(amount) > 1) return CommandResult.Rejected(AmountRangeCode);

        var targets = Targets(inputs, "targets");
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TooManyTargetsCode);

        var players = PlayerIdentityModule.Current;
        var rows = new List<GenericStaminaRow>(targets.Length);
        int committed = 0;
        foreach (var target in targets)
        {
            GenericStaminaRow Row(string status, string state, string code, double delta = 0)
                => new(target, status, state, code, delta, targets.Length);

            if (players == null || !players.CanCommit) { rows.Add(Row("rejected", CommitStates.None, AuthorityCode)); continue; }
            if (!IsKind(target, PlayerKind)) { rows.Add(Row("rejected", CommitStates.None, StaminaTargetKindCode)); continue; }
            var agent = players.CurrentAgent(target);
            if (agent == null) { rows.Add(Row("rejected", CommitStates.None, StaleCode)); continue; }
            PlayerStamina? stamina;
            float current;
            try
            {
                stamina = agent.Stamina;
                if (stamina == null || stamina.Pointer == IntPtr.Zero) { rows.Add(Row("rejected", CommitStates.None, StaleCode)); continue; }
                current = stamina.Stamina;
            }
            catch (Exception) { rows.Add(Row("rejected", CommitStates.None, StaleCode)); continue; }
            if (!float.IsFinite(current) || current < 0f || current > 1f)
            { rows.Add(Row("rejected", CommitStates.None, StaleCode)); continue; }

            double wanted = operation switch
            {
                "set" => amount,
                "add" => current + amount,
                _ => current - amount
            };
            var next = (float)Math.Clamp(wanted, 0d, 1d);
            float readback;
            try
            {
                stamina.Stamina = next;
                readback = stamina.Stamina;
            }
            catch (Exception) { rows.Add(Row("unknown", CommitStates.Unknown, CommitExceptionCode, 0)); continue; }
            bool held = Math.Abs(readback - next) <= ReadbackEpsilon;
            rows.Add(Row(held ? "succeeded" : "unknown", held ? CommitStates.Confirmed : CommitStates.Unknown,
                held ? StaminaChangedCode : CommitExceptionCode, readback - current));
            if (held) committed++;
        }
        return Aggregate(committed, rows.Count, rows[rows.Count - 1].Code, RuntimeJson.From(new { results = rows }));
    }

    // ------------------------------------------------------------------ camera shake

    internal static CommandResult CameraShake(CommandContext context) => CameraShake(context.Parameters, context.Inputs);

    /// <summary>`forge.action.presentation.camera_shake`: one shake per viewer whose camera this machine holds. The
    /// distance falloff is this row's own arithmetic over the authored radii — the native entry takes one amplitude
    /// and no radius (dump.cs 530638) — applied here so every machine shakes its own camera by the same rule.</summary>
    internal static CommandResult CameraShake(JsonElement parameters, JsonElement inputs)
    {
        if (!Audience(inputs, out var refusal)) return refusal;
        if (!TryAuthored(parameters, "duration", out double ticks) || ticks < 0 || ticks > MaximumDurationTicks)
            return CommandResult.Rejected(CameraDurationRangeCode);
        if (ticks == 0) return CommandResult.Rejected(CameraDurationRequiredCode);
        double duration = ticks * SecondsPerTick;
        if (!TryAuthored(parameters, "amplitude", out double amplitude) || amplitude < 0 || amplitude > MaximumAmplitude)
            return CommandResult.Rejected(CameraAmplitudeRangeCode);
        if (amplitude == 0) return CommandResult.Rejected(CameraAmplitudeRequiredCode);
        if (!TryAuthored(parameters, "frequency", out double frequency) || frequency < 0 || frequency > MaximumFrequency)
            return CommandResult.Rejected(CameraFrequencyRangeCode);
        if (!TryAuthored(parameters, "inner_radius", out double inner) || inner < 0) return CommandResult.Rejected(CameraRadiusCode);
        if (!TryAuthored(parameters, "radius", out double radius) || radius < 0) return CommandResult.Rejected(CameraRadiusCode);
        if (radius > 0 && inner > radius) return CommandResult.Rejected(CameraRadiusCode);

        var direction = Vector3.zero;
        if (parameters.TryGetProperty("direction", out var directionPort) && directionPort.ValueKind != JsonValueKind.Null)
        {
            if (!TryVector(directionPort, out direction) || !Finite(direction)) return CommandResult.Rejected(CameraDirectionCode);
        }
        bool hasCenter = Present(inputs, "center");
        var center = Vector3.zero;
        if (hasCenter && (!TryVector(inputs, "center", out center) || !Finite(center)))
            return CommandResult.Rejected(CameraRadiusCode);

        var viewers = Targets(inputs, "viewers");
        var players = PlayerIdentityModule.Current;
        var rows = new List<GenericCameraShakeRow>(viewers.Length);
        foreach (var viewer in viewers)
        {
            GenericCameraShakeRow Row(string status, string code, double reached = 0)
                => new(viewer, status, CommitStates.None, code, reached, viewers.Length);

            var agent = players?.CurrentAgent(viewer);
            if (agent == null) { rows.Add(Row("rejected", StaleCode)); continue; }
            FPSCamera? camera;
            float scale = 1f, distance = 0f;
            try
            {
                camera = agent.FPSCamera;
                if (camera == null || camera.Pointer == IntPtr.Zero) { rows.Add(Row("rejected", CameraUnavailableCode)); continue; }
                if (radius > 0)
                {
                    distance = hasCenter ? Vector3.Distance(center, agent.EyePosition) : 0f;
                    scale = Falloff(distance, (float)inner, (float)radius);
                    if (scale <= 0f) { rows.Add(Row("rejected", CameraOutOfRangeCode)); continue; }
                }
                camera.Shake((float)duration, (float)(amplitude * scale), (float)frequency, direction);
            }
            catch (Exception) { rows.Add(Row("failed", CommitExceptionCode)); continue; }
            rows.Add(Row("succeeded", CameraIssuedCode, amplitude * scale));
        }
        return Presented(rows);
    }

    // ------------------------------------------------------------------ screen liquid

    internal static CommandResult ScreenLiquid(CommandContext context) => ScreenLiquid(context.Parameters, context.Inputs);

    /// <summary>`forge.action.presentation.screen_liquid`: one native liquid job for each viewer whose viewport this
    /// machine draws. A viewer of another machine is refused by name: the native entry queues the job against the
    /// one local liquid system (dump.cs 588824), so applying it for somebody else's camera would splash this
    /// machine's screen with their position.</summary>
    internal static CommandResult ScreenLiquid(JsonElement parameters, JsonElement inputs)
    {
        if (!Audience(inputs, out var refusal)) return refusal;
        string? preset = Text(parameters, "preset");
        if (preset == null) return CommandResult.Rejected(LiquidPresetRequiredCode);
        int index = Array.IndexOf(PresentationActionContract.LiquidPresets, preset);
        if (index < 0) return CommandResult.Rejected(LiquidPresetUnknownCode);

        var viewers = Targets(inputs, "viewers");
        var players = PlayerIdentityModule.Current;
        var rows = new List<GenericScreenLiquidRow>(viewers.Length);
        foreach (var viewer in viewers)
        {
            GenericScreenLiquidRow Row(string status, string code)
                => new(viewer, status, CommitStates.None, code, preset, viewers.Length);

            var agent = players?.CurrentAgent(viewer);
            if (agent == null) { rows.Add(Row("rejected", StaleCode)); continue; }
            bool applied;
            try
            {
                // The presentation belongs to the machine that draws the viewport, so a viewer of another machine
                // is not this half's to splash.
                if (!agent.IsLocallyOwned) { rows.Add(Row("rejected", LiquidViewerCode)); continue; }
                applied = ScreenLiquidManager.Apply((ScreenLiquidSettingName)index, agent.EyePosition, agent.Forward);
            }
            catch (Exception) { rows.Add(Row("failed", CommitExceptionCode)); continue; }
            rows.Add(applied ? Row("succeeded", LiquidAppliedCode) : Row("rejected", LiquidNotAppliedCode));
        }
        return Presented(rows);
    }

    // ------------------------------------------------------------------ movement-state read

    /// <summary>The one native read behind `forge.query.player.movement_state`: the agent's own locomotion machine,
    /// its current member and the time it entered that state. A player this process cannot resolve, or an agent
    /// whose locomotion half is gone, is answered as null — the row's refusal, never a substituted state.</summary>
    internal static PlayerMovementStateContract.MovementSample? ReadMovement(EntityReference reference)
    {
        var agent = PlayerIdentityModule.Current?.CurrentAgent(reference);
        if (agent == null) return null;
        PlayerLocomotion? locomotion;
        try { locomotion = agent.Locomotion; }
        catch (Exception) { return null; }
        if (locomotion == null || locomotion.Pointer == IntPtr.Zero) return null;
        int index;
        float entered;
        try
        {
            index = (int)locomotion.m_currentStateEnum;
            entered = locomotion.m_changeStateTime;
        }
        catch (Exception) { return null; }
        // A native value outside the declared members is handed on as it is: the contract refuses it by name rather
        // than mapping it onto the nearest state.
        if (index < 0 || index >= PlayerMovementStateContract.States.Length)
            return new PlayerMovementStateContract.MovementSample(string.Empty, 0, index);
        double since = Time.time - entered;
        if (!double.IsFinite(since) || since < 0) since = 0;
        return new PlayerMovementStateContract.MovementSample(PlayerMovementStateContract.States[index], since, index);
    }

    // ------------------------------------------------------------------ shared plumbing

    /// <summary>The command-level conclusion of a run of recipients, by the same rule this provider's other action
    /// halves use: every recipient landed is a success, none is a failure carrying the first code, and anything in
    /// between is a partial.</summary>
    private static CommandResult Aggregate(int committed, int total, string code, JsonElement outputs)
    {
        if (total > 0 && committed == total) return CommandResult.Succeeded(outputs);
        if (committed == 0) return CommandResult.Create(CommandStatuses.Failed, CommitStates.None, code, "", outputs);
        return CommandResult.Partial(outputs, CommitStates.None);
    }

    /// <summary>The presentation aggregate: the tier commits nothing, so a run of rows is one succeeded
    /// non-committing result carrying them.</summary>
    private static CommandResult Presented<T>(IReadOnlyList<T> rows) where T : class
    {
        if (rows.Count == 0) return CommandResult.Rejected(NoTargetsCode);
        if (rows.Count > CommandResult.MaximumFacts) return CommandResult.Rejected(TooManyTargetsCode);
        return CommandResult.Create(CommandStatuses.Succeeded, CommitStates.None, PresentedCode(rows[0]), "",
            RuntimeJson.From(new { results = rows }));
    }

    private static string PresentedCode<T>(T row) => row switch
    {
        GenericCameraShakeRow camera => camera.Code,
        GenericScreenLiquidRow liquid => liquid.Code,
        _ => "presented"
    };

    /// <summary>The audience check the presentation rows of this package share: the plan names viewers, and every
    /// member of that set is a player reference. A step addressed to something else cannot be presented to it.</summary>
    private static bool Audience(JsonElement inputs, out CommandResult refusal)
    {
        refusal = CommandResult.Rejected(ViewersRequiredCode);
        var viewers = Targets(inputs, "viewers");
        if (viewers.Length == 0) return false;
        foreach (var viewer in viewers)
        {
            if (!IsKind(viewer, PlayerKind)) { refusal = CommandResult.Rejected(ViewerCode); return false; }
        }
        return true;
    }

    /// <summary>One recipient collection of the resolved frame, in the plan's own order.</summary>
    private static EntityReference[] Targets(JsonElement inputs, string port)
    {
        if (!inputs.TryGetProperty(port, out var value) || value.ValueKind != JsonValueKind.Array) return Array.Empty<EntityReference>();
        var targets = new List<EntityReference>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray()) targets.Add(RuntimeJson.Entity(item));
        return targets.ToArray();
    }

    /// <summary>A structural number of the row's own parameters or inputs: absent or null is the zero the row
    /// documents, and a present non-number is refused by the caller.</summary>
    private static bool TryAuthored(JsonElement source, string id, out double value)
    {
        value = 0;
        if (!source.TryGetProperty(id, out var port) || port.ValueKind == JsonValueKind.Null) return true;
        if (port.ValueKind != JsonValueKind.Number || !port.TryGetDouble(out double number) || !double.IsFinite(number)) return false;
        value = number;
        return true;
    }

    private static string? Text(JsonElement source, string id)
        => source.TryGetProperty(id, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement parameters, string id)
        => parameters.TryGetProperty(id, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool Present(JsonElement source, string port)
        => source.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null;

    /// <summary>The three coordinates of a vector3 port, which the runtime carries as an array of three numbers.
    /// The overload without a port id is what the `direction` parameter goes through: a parameter is a value, not a
    /// frame, so it is read directly.</summary>
    private static bool TryVector(JsonElement source, string port, out Vector3 value)
    {
        value = Vector3.zero;
        return source.TryGetProperty(port, out var element) && TryVector(element, out value);
    }

    private static bool TryVector(JsonElement element, out Vector3 value)
    {
        value = Vector3.zero;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3) return false;
        var coordinates = element.EnumerateArray().ToArray();
        if (!TryCoordinate(coordinates[0], out float x) || !TryCoordinate(coordinates[1], out float y) || !TryCoordinate(coordinates[2], out float z))
            return false;
        value = new Vector3(x, y, z);
        return true;
    }

    private static bool TryCoordinate(JsonElement value, out float result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number)) return false;
        if (number < -float.MaxValue || number > float.MaxValue) return false;
        result = (float)number;
        return true;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    /// <summary>Whether an entity reference belongs to one kind, by the one rule every provider of this package
    /// already uses: the id's namespace.</summary>
    private static bool IsKind(EntityReference reference, string kind)
        => reference.Id != null && reference.Id.StartsWith(kind + ":", StringComparison.Ordinal);

    /// <summary>The distance falloff the shake uses: one inside the inner radius, nothing beyond the outer one, and
    /// the linear ramp between them. Both radii are the author's own numbers, so this is the row's arithmetic and
    /// not a claim about a native curve.</summary>
    private static float Falloff(float distance, float inner, float radius)
    {
        if (distance <= inner) return 1f;
        if (distance >= radius) return 0f;
        return (radius - distance) / (radius - inner);
    }

    /// <summary>Where a recipient is, for the source-to-recipient direction. Only a player of this machine's own
    /// table can be asked; any other reference — including the enemy references this row cannot resolve — answers
    /// false and is refused by the caller.</summary>
    private static bool TryPosition(EntityReference reference, out Vector3 position)
    {
        position = Vector3.zero;
        if (!IsKind(reference, PlayerKind)) return false;
        var agent = PlayerIdentityModule.Current?.CurrentAgent(reference);
        if (agent == null) return false;
        try { position = agent.Position; }
        catch (Exception) { return false; }
        return Finite(position);
    }
}
