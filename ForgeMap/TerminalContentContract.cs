using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `forge.action.map.terminal_content` row: the running terminal's own content, which the EOS
/// security-door-terminal feature's stopped row folds into (`terminal_override_command`, rulings R1/R2). One
/// capability adds or removes a command on a terminal the level already built, and adds, removes or hides one of
/// its local log files.
///
/// The row writes the terminal's own command interpreter and log table rather than a table of this package's, so
/// a command it adds is typed, completed and answered exactly like a vanilla one and travels to the other
/// machines through the terminal's own replicated state. That is also why the row is a `host` row: the terminal
/// owns its state and the host is the machine that writes it.
///
/// The command's own help line is the row's `text` input, and its code is `number`. The code is the interpreter's
/// own `TERM_Command` member rather than a free string, because that enum is what the interpreter looks a command
/// up by; a request naming a member the enum does not carry is refused before anything is written.
///
/// The EOS feature's four access settings (`accessible_when_locked`, `accessible_when_unlocked`, `accessibility`
/// and `on_puzzle_solved`) are deliberately **not** parameters here. Whether a terminal may be used at all is the
/// interaction-state row's business, and when a door's own puzzle grants the command is the door's own field: a
/// request that wants "the override becomes available once the door is unlocked" writes those two rows, and the
/// command row only decides what the command is.</summary>
public static class TerminalContentContract
{
    public const string CapabilityId = "forge.action.map.terminal_content";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.map.terminal_content";
    public const string HandlerName = "gtfo.map.terminal_content";

    /// <summary>The permission the row's own write needs: it changes what a terminal offers, so a plan declares
    /// that it may write a terminal's content before it can.</summary>
    public const string Permission = "terminal.content";

    public static readonly string[] Domains = { "map", "room", "logic" };

    /// <summary>What kind of content one request edits. A command and a log file are different natives behind
    /// different members, so the request has to say which one it means rather than being guessed at from the
    /// members it happens to carry.</summary>
    public static readonly string[] Kinds = { "command", "log" };

    /// <summary>The four edits: add and remove a command, add, remove, show and hide a log file. `remove` and
    /// `hide` are different natives — one takes the entry out of the terminal's log table, the other only takes it
    /// off the list a player reads — so both are offered.</summary>
    public static readonly string[] Operations = { "add", "remove", "show", "hide" };

    /// <summary>The interpreter's own three rules for what happens to a command once it has been used, in the
    /// generic spelling of what they do.</summary>
    public static readonly string[] Rules = { "normal", "only_once", "only_once_delete" };

    public static HandlerShape Shape { get; } = new HandlerShape()
        .Inputs("terminals", "text").Outputs("result")
        .Parameters("kind", "operation", "slot", "name", "rule", "sound");

    public static IReadOnlyDictionary<string, HandlerShape> Shapes() =>
        new Dictionary<string, HandlerShape>(StringComparer.Ordinal) { [HandlerName] = Shape };

    public static object[] CapabilityRows() => new object[] { Row() };

    public static object[] BindingRows() => new object[] { BindingRow() };

    public static BindingSupport[] Supports() => new[] { Support() };

    private static object Row() => new
    {
        id = CapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "增删终端命令与日志",
        version = "1.0.0",
        parameters = new { description = "在运行期给目标终端增删命令，或增删、显示、隐藏它的本地日志文件。" },
        graph = new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "terminals", type = "entity", cardinality = "many", entityKinds = new[] { MapObjectModule.EntityKind } },
                new { id = "text", type = "string", optional = true }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.map.terminal_content",
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" },
                        new { id = "target_count", type = "integer" }
                    }
                }
            },
            parameters = new object[]
            {
                new { id = "kind", type = "enum", role = "structural", required = true, values = Kinds },
                new { id = "operation", type = "enum", role = "structural", required = true, values = Operations },
                new { id = "slot", type = "integer", role = "structural", required = false },
                new { id = "name", type = "string", role = "structural", required = false },
                new { id = "rule", type = "enum", role = "structural", required = false, values = Rules },
                new { id = "sound", type = "integer", role = "structural", required = false }
            },
            recipients = new
            {
                input = "terminals", target = "entity", cardinality = "many",
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
