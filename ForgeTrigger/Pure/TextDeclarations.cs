using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>The text family: the `g-text` row, which joins one template sentence and one value into a line of text.
/// The row is `pure`, so it reads nothing and the host that runs the plan evaluates it.
///
/// The value is a plain input rather than a variadic slot: the catalog declares no variadic string template, and a
/// message with more values is a chain of these rows. The format — integer, a fixed decimal count, percent — is the
/// template's own format item, which is why the row declares no format parameter either.</summary>
internal static class TextDeclarations
{
    /// <summary>The catalog declares this row's domains with `session` beside the shared logic list, because a line
    /// of text is what a HUD or a terminal answer carries.</summary>
    private static readonly string[] TextDomains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "session", "logic" };

    internal static IReadOnlyList<PureNode> Nodes { get; } = new[]
    {
        PureModule.OperatorRow("forge.modifier.value.text", "拼接文字", "把一段模板文字和一个数值拼成一句话。",
            new HandlerShape().Inputs("template", "value").Outputs("value"), PureModule.Text)
    };
}
