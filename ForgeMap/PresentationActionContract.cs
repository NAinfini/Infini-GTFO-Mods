using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The two `forge.action.presentation.*` rows of the generic-player batch that have a native entry on
/// build 20403457: the camera shake and the screen liquid.
///
/// Both are `presentation` tier rows: what they write is one machine's own viewport, so a plan that shakes a camera
/// or splashes a viewer's screen is not changing the world any other machine can see. The tier is the runtime's own
/// (`RuntimeGraphContracts.ExecutionTiers`), and it is what keeps a local visual from being dispatched as a world
/// write.
///
/// The third row the ruling grouped here, the numbered HUD timer, is not declared: the game has exactly one timer
/// widget (`PUI_ObjectiveTimer`, dump.cs 555371) and its value belongs to `WardenObjectiveManager`, which is the
/// objective-timer row this provider already publishes. A second, numbered timer has no native carrier, so the row
/// is reported as needing evidence instead of being built on a widget the mod would have to invent. The report
/// carries the same note.</summary>
public static class PresentationActionContract
{
    public const string CameraShakeCapabilityId = "forge.action.presentation.camera_shake";
    public const string CameraShakeHandlerName = "gtfo.presentation.camera_shake";
    public const string CameraShakeBindingId = ModuleDefinition.ProviderId + ".binding.presentation.camera_shake";

    public const string ScreenLiquidCapabilityId = "forge.action.presentation.screen_liquid";
    public const string ScreenLiquidHandlerName = "gtfo.presentation.screen_liquid";
    public const string ScreenLiquidBindingId = ModuleDefinition.ProviderId + ".binding.presentation.screen_liquid";

    /// <summary>The permission a camera shake writes under, in the presentation namespace the environment rows
    /// already use.</summary>
    public const string CameraPermission = "presentation.camera";
    /// <summary>The permission a screen liquid writes under.</summary>
    public const string ScreenPermission = "presentation.screen";

    public static readonly string[] Domains = { "map", "room", "weapon", "tool", "consumable", "player", "enemy" };

    /// <summary>The sixteen members of the native `ScreenLiquidSettingName` enum (dump.cs 543674-543693), spelled
    /// the way the card layer names them. An index in this array is the member's native value.</summary>
    public static readonly string[] LiquidPresets =
    {
        "enemy_blood_big_blood_bomb", "enemy_blood_small_random_streak", "enemy_blood_squirt", "shooter_goo",
        "spitter_jizz", "elevator_rain", "water_drizzle", "water_drip", "player_blood",
        "disinfection_pack_apply", "disinfection_station_apply", "infection_sweat", "player_blood_small_damage",
        "player_blood_big_damage", "player_blood_downed", "anemone_goo"
    };

    public static readonly string[] CameraShakeCodes =
    {
        "no-targets",
        "too-many-targets",
        "viewers-required",
        "viewers-unsupported",
        "camera-shake-duration-required",
        "camera-shake-duration-out-of-range",
        "camera-shake-amplitude-required",
        "camera-shake-amplitude-out-of-range",
        "camera-shake-frequency-out-of-range",
        "camera-shake-radius-invalid",
        "camera-shake-direction-invalid",
        "camera-shake-viewer-kind",
        "camera-shake-out-of-range",
        "stale-or-unsupported-recipient",
        "camera-unavailable",
        "native-commit-exception"
    };

    public static readonly string[] ScreenLiquidCodes =
    {
        "no-targets",
        "too-many-targets",
        "viewers-required",
        "viewers-unsupported",
        "screen-liquid-preset-required",
        "screen-liquid-preset-unknown",
        "screen-liquid-viewer-kind",
        "screen-liquid-viewer-unsupported",
        "screen-liquid-not-applied",
        "stale-or-unsupported-recipient",
        "native-commit-exception"
    };

    /// <summary>The camera-shake shape: the viewers it reaches, the optional centre the distance falloff is
    /// measured from, and the six structural parameters of the shake itself. `direction` is a parameter and not an
    /// input because a shake's direction is a fixed authored vector in the game's own API
    /// (`FPSCamera.Shake(duration, amplitude, frequency, worldDirection)`, dump.cs 530638), not a per-recipient
    /// computed value.</summary>
    public static readonly HandlerShape CameraShakeShape = new HandlerShape()
        .Inputs("viewers", "center").Outputs("result")
        .Parameters("amplitude", "frequency", "duration", "inner_radius", "radius", "direction");

    /// <summary>The screen-liquid shape: the viewers whose own viewport is splashed, and the preset's name. The
    /// native entry takes no range, no opacity and no fade (`ScreenLiquidManager.Apply(setting, position,
    /// direction)`, dump.cs 588849), so the draft's `range` and `fade_out` are not declared: a port no native call
    /// can honour is not written down as if it could.</summary>
    public static readonly HandlerShape ScreenLiquidShape = new HandlerShape()
        .Inputs("viewers").Outputs("result").Parameters("preset");

    public static IReadOnlyList<object> CapabilityRows() => new object[] { CameraShakeRow(), ScreenLiquidRow() };

    public static IReadOnlyList<object> BindingRows() => new object[]
    {
        Binding(CameraShakeBindingId, CameraShakeCapabilityId, CameraShakeHandlerName),
        Binding(ScreenLiquidBindingId, ScreenLiquidCapabilityId, ScreenLiquidHandlerName)
    };

    public static IReadOnlyList<BindingSupport> Supports() => new[]
    {
        new BindingSupport(CameraShakeBindingId, "implementation-only", new[] { CameraPermission }),
        new BindingSupport(ScreenLiquidBindingId, "implementation-only", new[] { ScreenPermission })
    };

    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [CameraShakeHandlerName] = CameraShakeShape,
        [ScreenLiquidHandlerName] = ScreenLiquidShape
    };

    private static object CameraShakeRow() => new
    {
        id = CameraShakeCapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "镜头震动",
        version = "1.0.0",
        parameters = new { description = "给范围内的玩家镜头加一次震动，按距离衰减。" },
        graph = new
        {
            domains = Domains,
            execution = "presentation",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "viewers", type = "entity", cardinality = "many", entityKinds = new[] { "gtfo.player" } },
                new { id = "center", type = "vector3", unit = "m", optional = true }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.presentation.camera_shake",
                    codes = CameraShakeCodes,
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" },
                        new { id = "amplitude", type = "number" },
                        new { id = "target_count", type = "integer" }
                    }
                }
            },
            parameters = new object[]
            {
                new { id = "amplitude", type = "number", role = "structural", required = true },
                new { id = "frequency", type = "number", role = "structural", required = false, unit = "hz" },
                new { id = "duration", type = "number", role = "structural", required = true, unit = "tick" },
                new { id = "inner_radius", type = "number", role = "structural", required = false, unit = "m" },
                new { id = "radius", type = "number", role = "structural", required = false, unit = "m" },
                new { id = "direction", type = "vector3", role = "structural", required = false }
            },
            recipients = new
            {
                input = "viewers", target = "entity", cardinality = "many",
                requires = new[] { CameraPermission }, result = "result"
            }
        }
    };

    private static object ScreenLiquidRow() => new
    {
        id = ScreenLiquidCapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "屏幕液体",
        version = "1.0.0",
        parameters = new { description = "在一个玩家自己的视口上播放一段原生液体表现。" },
        graph = new
        {
            domains = Domains,
            execution = "presentation",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "viewers", type = "entity", cardinality = "many", entityKinds = new[] { "gtfo.player" } }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.presentation.screen_liquid",
                    codes = ScreenLiquidCodes,
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" },
                        new { id = "preset", type = "string" },
                        new { id = "target_count", type = "integer" }
                    }
                }
            },
            parameters = new object[]
            {
                new { id = "preset", type = "enum", role = "structural", required = true, values = LiquidPresets }
            },
            recipients = new
            {
                input = "viewers", target = "entity", cardinality = "many",
                requires = new[] { ScreenPermission }, result = "result"
            }
        }
    };

    private static object Binding(string bindingId, string capabilityId, string handler) => new
    {
        id = bindingId,
        capabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };
}
