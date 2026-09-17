using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>
/// The one checklist row this batch owns that draws a number on the player's own screen: `a-hud`. The catalog
/// has a single HUD row (`forge.action.presentation.hud_message`) and it carries a message, not a value: no
/// numeric port, no placement, no form, no colour and no audience. The checklist's `q-hud` decision row recorded
/// exactly that gap, and this row is the answer to it — one capability that shows one value at one of the three
/// places the checklist names, in one of four forms, in one colour, to one audience.
///
/// The row is a `presentation` capability, which is the kernel's own tier for "the host owns the decision, the
/// recipient owns the write": the host resolves which step runs and for which players, each addressed client
/// draws locally with its own GUI objects, and nothing here commits world state. That split is what the game
/// itself does — the local player's status bar, its shield readout and its teammate markers are per-machine
/// presentation objects (`GuiManager.m_playerLayer`, `PUI_LocalPlayerStatus`, `PlaceNavMarkerOnGO`), and the
/// numbers they show are computed on the host and read locally.
///
/// The audience is what makes the row's own value per-player. `self` means the value belongs to one player, and
/// that player's session travels with the command: the machine that is not that player refuses it by name
/// (`hud-audience-not-addressed`) instead of drawing somebody else's number. The one combination that is refused
/// outright is `self` with `placement=teammate_overhead`, because the local player has no overhead marker — the
/// line can only ever go on a teammate's head, and a self-audience row would have nowhere of its own to draw.
/// </summary>
public static class HudContract
{
    public const string ValueCapability = "forge.action.presentation.hud_value";
    public const string ValueBindingId = ModuleDefinition.ProviderId + ".binding.hud_value";
    public const string ValueHandler = "gtfo.map.hud_value";

    /// <summary>The permission this row writes under: it draws on a player's own screen, so a plan declares that
    /// it may present to that player's HUD before it can draw.</summary>
    public const string HudPermission = "presentation.hud";

    /// <summary>The three placements the checklist names, in its own order. `status_bar` is the local player's
    /// own status readout, `screen` is this provider's own text line, `teammate_overhead` is the extra
    /// information line above a teammate's head.</summary>
    public static readonly string[] Placements = { "status_bar", "screen", "teammate_overhead" };

    /// <summary>The four forms the checklist names. `bar` writes the game's own bar and no text — only the status
    /// bar has a sprite to write, which the handler refuses for the other two placements; the other three render
    /// text, so the same value can be read as a number, as number-of-maximum or as a percentage.</summary>
    public static readonly string[] Forms = { "number", "number_of_max", "percent", "bar" };

    /// <summary>Audience members: `team` draws for every addressed client, `self` draws only on the machine of
    /// the one player the value belongs to, refused by name on any other machine.</summary>
    public static readonly string[] Audiences = { "team", "self" };

    /// <summary>The one shape of this handler: the recipients the request declares, the value and its optional
    /// maximum and label, the visibility flag, and the five structural parameters the checklist asks for.</summary>
    public static readonly HandlerShape ValueShape = new HandlerShape()
        .Inputs("viewers", "value", "maximum", "label", "visible").Outputs("result")
        .Parameters("placement", "form", "audience", "color", "key");

    public static string ValueCapabilityJson => RuntimeJson.From(CapabilityRow()).GetRawText();

    /// <summary>The one execute binding row, in the same shape every other Map action row uses.</summary>
    public static object BindingRow() => EnvironmentContract.Row(ValueBindingId, ValueCapability, ValueHandler, "execute");

    public static BindingSupport Support() => new(ValueBindingId, "implementation-only", new[] { HudPermission });

    public static IReadOnlyDictionary<string, HandlerShape> Shapes()
        => new Dictionary<string, HandlerShape>(StringComparer.Ordinal) { [ValueHandler] = ValueShape };

    /// <summary>The one capability row: a value readout the plan drives, at a placement and in a form the author
    /// picks, shown to the audience the plan declares. It is public because a registration and a focused test both
    /// read the row the handler is resolved against rather than restating its ports.</summary>
    public static object CapabilityRow() => new
    {
        id = ValueCapability,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "在玩家界面显示数值",
        version = "1.0.0",
        parameters = new { description = "在玩家界面上显示一个数值，位置、形式、颜色和观众都由作者选择。" },
        graph = new
        {
            domains = new[] { "map", "room", "enemy", "weapon", "tool", "consumable", "player" },
            execution = "presentation",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "viewers", type = "entity", cardinality = "many", entityKinds = new[] { "gtfo.player" } },
                new { id = "value", type = "number" },
                new { id = "maximum", type = "number", optional = true },
                new { id = "label", type = "string", optional = true },
                new { id = "visible", type = "boolean" }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.presentation.hud_value",
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" }
                    }
                }
            },
            parameters = new object[]
            {
                new { id = "placement", type = "enum", role = "structural", required = true, values = Placements },
                new { id = "form", type = "enum", role = "structural", required = true, values = Forms },
                new { id = "audience", type = "enum", role = "structural", required = true, values = Audiences },
                new { id = "color", type = "string", role = "structural", required = false },
                new { id = "key", type = "string", role = "structural", required = false }
            },
            recipients = new
            {
                input = "viewers", target = "entity", cardinality = "many",
                requires = new[] { HudPermission }, result = "result"
            }
        }
    };
}
