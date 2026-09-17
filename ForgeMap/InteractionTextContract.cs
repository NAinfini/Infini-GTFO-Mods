using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `forge.action.presentation.interaction_text` row: the one interaction-prompt row of this provider,
/// which the EOS security-door feature's two stopped rows fold into (`door_interact_text` and `door_text_glitch`,
/// rulings R1/R2). One capability writes one object's prompt — replace it, wrap it, break it up, or put the game's
/// own wording back — so an author never has to name two rows to say "this door reads wrong now".
///
/// The row is a `presentation` tier row, which is the kernel's own tier for "the host owns the decision, the
/// recipient owns the write": the prompt is drawn by the machine the player sits at (`Interact_Base.
/// InteractionMessage`), so every machine has to hold the rule for its own prompt rather than the host holding it
/// for all of them. The host decides when the step runs; each addressed client writes its own copy.
///
/// The EOS feature's two glitch styles keep their behaviour and lose their names: `style1`/`style2` become the
/// generic `hex`/`decrypt`, which is what the styles really are. Everything else the feature carried — the
/// replace text, the prefix and the postfix — is one `text` input and three modes, because the three edits are
/// mutually exclusive in the prompt the player reads and a request that carried all of them at once would be
/// three orders for one line of text.
///
/// Nothing here writes door state: a rewritten prompt cannot unlock, open or alarm anything. That is also why the
/// row declares no `host` authority: a presentation write commits no world state, and the result rows say so.</summary>
public static class InteractionTextContract
{
    public const string CapabilityId = "forge.action.presentation.interaction_text";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.presentation.interaction_text";
    public const string HandlerName = "gtfo.presentation.interaction_text";

    /// <summary>The permission the prompt's own edit needs. It is the presentation tier's permission for text the
    /// game draws, so a plan declares that it may rewrite what a player reads before it can.</summary>
    public const string Permission = "presentation.interaction_text";

    /// <summary>The domains the row is filtered by in the editors: a prompt is authored on a map object, from a
    /// room or from an event chain.</summary>
    public static readonly string[] Domains = { "map", "room", "logic" };

    /// <summary>The four edits plus the one structural mode that puts the game's own prompt back. `clear` is the
    /// inverse of the other four, which is what makes the row usable from an event chain: a chain that breaks a
    /// door's prompt has to be able to un-break it without restating the game's localized text.</summary>
    public static readonly string[] Modes = { "replace", "prefix", "suffix", "glitch", "clear" };

    /// <summary>The two glitch presentations, in the generic spelling of what they draw.</summary>
    public static readonly string[] Styles = { "hex", "decrypt" };

    /// <summary>The recipients of the row: map objects, which is the one entity namespace a prompt belongs to.
    /// The category is not narrowed further, because the prompt is the interaction layer's own member and every
    /// interactable this provider addresses draws it the same way.</summary>
    public const string TargetEntityKind = MapObjectModule.EntityKind;

    /// <summary>The handler's own port set: the map objects it addresses and the text it writes, in the row's own
    /// order. The written text is a real input the handler reads, so it is declared here as well — a capability
    /// port the shape does not cover is a row the registry refuses.</summary>
    public static HandlerShape Shape { get; } = new HandlerShape()
        .Inputs("targets", "text").Outputs("result").Parameters("mode", "style", "refresh_interval");

    public static IReadOnlyDictionary<string, HandlerShape> Shapes() =>
        new Dictionary<string, HandlerShape>(StringComparer.Ordinal) { [HandlerName] = Shape };

    public static object[] CapabilityRows() => new object[] { Row() };

    public static object[] BindingRows() => new object[] { BindingRow() };

    public static BindingSupport[] Supports() => new[] { Support() };

    /// <summary>The one capability row, built from the members above rather than restated.</summary>
    private static object Row() => new
    {
        id = CapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "改写交互提示文本",
        version = "1.0.0",
        parameters = new { description = "把目标对象的交互提示换成作者写的文本、加上前后缀，或改成故障样式显示；`clear` 放回游戏自己的提示。" },
        graph = new
        {
            domains = Domains,
            execution = "presentation",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "targets", type = "entity", cardinality = "many", entityKinds = new[] { TargetEntityKind } },
                new { id = "text", type = "string", optional = true }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.presentation.interaction_text",
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        // The four fixed columns come first in the runtime's own order; `committed` is always
                        // `none` for this tier, because a rewritten prompt is not world state.
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" },
                        new { id = "text", type = "string" },
                        new { id = "target_count", type = "integer" }
                    }
                }
            },
            parameters = new object[]
            {
                new { id = "mode", type = "enum", role = "structural", required = true, values = Modes },
                new { id = "style", type = "enum", role = "structural", required = false, values = Styles },
                new { id = "refresh_interval", type = "number", role = "structural", required = false, unit = "tick" }
            },
            recipients = new
            {
                input = "targets", target = "entity", cardinality = "many",
                requires = new[] { Permission }, result = "result"
            }
        }
    };

    private static object BindingRow() => new
    {
        id = BindingId,
        capabilityId = CapabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler = HandlerName,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    private static BindingSupport Support()
        => new(BindingId, "implementation-only", new[] { Permission });
}
