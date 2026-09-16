using System;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The two terminal action rows this provider implements for the one `gtfo.map_object` namespace: a
/// whitelisted command run through the terminal's own interpreter, and one localisable line printed onto a
/// terminal. Both are declared here — capability shape, binding row, registration support and handler shape —
/// so the game-independent declaration, the runtime registration and the website catalog cannot describe the
/// same row three different ways.
///
/// Both rows are `execute` bindings inside this package rather than a second provider: the terminal is a map
/// object this package already addresses, its native entry points live in the same assembly as the door and
/// terminal observation, and a plan that pins either binding resolves against the Map provider's own registry.
///
/// The capability shapes are the catalog's, port for port: the recipient collection, the command with its
/// argument string, and the optional actor for the command row; the collection, the text and the lifetime for
/// the output row, whose one structural parameter is the line's severity. Nothing here invents a port the
/// catalog does not carry, and a port the native side cannot honour is refused by name at execution time
/// rather than declared as a promise this package cannot keep.</summary>
public static class TerminalObjectContract
{
    public const string CommandCapability = "forge.action.map.terminal_command";
    public const string VisibilityCapability = "forge.action.map.terminal_command_visibility";
    public const string OutputCapability = "forge.action.map.terminal_output";

    /// <summary>The permission the catalog's own recipient contract names for the command row. It is the whole
    /// permission a plan pinning this binding declares, because this binding requires no other row.</summary>
    public const string CommandPermission = "terminal.command";
    /// <summary>The permission the command-visibility row declares. It is the command row's own permission: the
    /// two rows write the same terminal state — whether a command is offered — and a plan that may run a command
    /// is a plan that may show or hide one.</summary>
    public const string VisibilityPermission = "terminal.command";
    /// <summary>The permission the catalog's recipient contract names for the printed-line row.</summary>
    public const string OutputPermission = "terminal.output";

    /// <summary>The catalog's domain list for these two rows: a terminal is a map object that rooms and logic
    /// graphs act on, and both rows name the same set.</summary>
    internal static readonly string[] Domains = { "map", "room", "logic" };

    public const string CommandBindingId = ModuleDefinition.ProviderId + ".binding.action.map.terminal_command";
    public const string VisibilityBindingId = ModuleDefinition.ProviderId + ".binding.action.map.terminal_command_visibility";
    public const string OutputBindingId = ModuleDefinition.ProviderId + ".binding.action.map.terminal_output";
    public const string CommandHandlerName = "gtfo.map_object.terminal_command";
    public const string VisibilityHandlerName = "gtfo.map_object.terminal_command_visibility";
    public const string OutputHandlerName = "gtfo.map_object.terminal_output";

    /// <summary>The command row's own ports, in the catalog's order: the recipient collection, the actor a
    /// request may name, the command text, the unique-command slot that may name the command instead, and its
    /// argument string. `arguments` is a string because the row composes one input line for the terminal's own
    /// interpreter, which is what parses a typed line. The row runs the command and nothing else: whether a
    /// command appears on the terminal at all is the visibility row below, because a structural switch inside
    /// one node's parameter bag is two different actions an author cannot read off the graph.</summary>
    public static readonly HandlerShape CommandShape = new HandlerShape()
        .Inputs("terminals", "actor", "command", "slot", "arguments").Outputs("result");

    /// <summary>The command-visibility row's ports: the same recipient collection and the same command or slot
    /// the run row names, and the one structural switch that says which state is asked for. It carries no
    /// argument string: showing a command is not running it.</summary>
    public static readonly HandlerShape VisibilityShape = new HandlerShape()
        .Inputs("terminals", "command", "slot").Outputs("result").Parameters("visible");

    /// <summary>The two members of the visibility row's switch. They are the row's own vocabulary rather than a
    /// shared set, which is what a structural enum with no shared set is for; the terminal's own state member
    /// they write is `pComputerTerminalState.RemovedCommands`, read back through `CommandIsHidden`.</summary>
    internal static readonly string[] VisibilityModes = { "shown", "hidden" };

    /// <summary>The printed-line row's ports: the same recipient collection, the text, and the lifetime the
    /// catalog declares. The one structural parameter is the line's severity.</summary>
    public static readonly HandlerShape OutputShape = new HandlerShape()
        .Inputs("terminals", "text", "lifetime").Outputs("result").Parameters("severity");

    /// <summary>One action row: the catalog's id, label, description, domains, execution, ports and recipient
    /// contract. The rows whose shape the framework already owns are not restated anywhere; these two are this
    /// provider's own because the catalog is the only other place that carries them.
    ///
    /// The recipient block is mandatory for an action and is the catalog's own: the collection port is the
    /// recipient input, its target is `entity` with `many` cardinality, the permission is the row's, and the
    /// result port is where each recipient's own row goes.</summary>
    internal static object Row(string capability, string label, string description, object[] inputs, object[] outputs,
        object[] parameters, string recipient, string permission, string result)
        => new
        {
            id = capability, owner = ModuleDefinition.ProviderId, kind = "action", label, version = "1.0.0",
            parameters = new { description },
            graph = new
            {
                domains = Domains, execution = "host", inputs, outputs, parameters,
                recipients = new { input = recipient, target = "entity", cardinality = "many",
                    requires = new[] { permission }, result }
            }
        };

    /// <summary>The result port both rows declare. The four fixed columns are the framework's result row for
    /// every action; the row's own columns follow them in the order the catalog lists them, which is the order
    /// the result writer emits.</summary>
    internal static object ResultPort(string schema, params object[] fields)
        => new { id = "result", type = "result", schema, fields };

    internal static object Port(string id, string type) => new { id, type };
    internal static object Many(string id, string type) => new { id, type, cardinality = "many" };
    internal static object Field(string id, string type) => new { id, type };
    internal static object EnumField(string id, string schema) => new { id, type = "enum", schema };
    internal static object UnitField(string id, string type, string unit) => new { id, type, unit };

    /// <summary>The command row, spelled exactly as the catalog carries it.</summary>
    public static object CommandRow() => Row(CommandCapability, "执行白名单的关卡终端动作", "执行白名单里的终端动作。",
        new object[]
        {
            Port("in", "execution"),
            Many("terminals", "entity"),
            Port("actor", "entity"),
            Port("command", "string"),
            UnitField("slot", "integer", "index"),
            Port("arguments", "string")
        },
        new object[]
        {
            Port("next", "execution"),
            ResultPort("forge.result.map.terminal_command",
                Field("target", "entity"), EnumField("status", "execution_outcome"),
                EnumField("committed", "commit_state"), Field("code", "string"),
                Field("target_count", "integer"))
        },
        Array.Empty<object>(), "terminals", CommandPermission, "result");

    /// <summary>The command-visibility row: the checklist's "输入 A 以后终端上才出现指令 B". It names the same
    /// terminal collection and the same command or slot, and its one structural switch says whether the command
    /// is to appear or disappear; the terminal's own synchronized setters are what every peer reads back. The
    /// actor port is absent because the native setters carry no player, and the result row is the same one the
    /// run row answers with, so a plan reads a visibility request and a run the same way.</summary>
    public static object VisibilityRow() => Row(VisibilityCapability, "显示或隐藏终端指令",
        "让一条指令在终端上出现或消失。",
        new object[]
        {
            Port("in", "execution"),
            Many("terminals", "entity"),
            Port("command", "string"),
            UnitField("slot", "integer", "index")
        },
        new object[]
        {
            Port("next", "execution"),
            ResultPort("forge.result.map.terminal_command",
                Field("target", "entity"), EnumField("status", "execution_outcome"),
                EnumField("committed", "commit_state"), Field("code", "string"),
                Field("target_count", "integer"))
        },
        new object[]
        {
            new { id = "visible", type = "enum", role = "structural", required = true, values = VisibilityModes }
        }, "terminals", VisibilityPermission, "result");

    /// <summary>The printed-line row, spelled exactly as the catalog carries it.</summary>
    public static object OutputRow() => Row(OutputCapability, "向指定终端输出本地化文本", "往终端上打一段可本地化的文字。",
        new object[]
        {
            Port("in", "execution"),
            Many("terminals", "entity"),
            Port("text", "string"),
            UnitField("lifetime", "integer", "tick")
        },
        new object[]
        {
            Port("next", "execution"),
            ResultPort("forge.result.map.terminal_output",
                Field("target", "entity"), EnumField("status", "execution_outcome"),
                EnumField("committed", "commit_state"), Field("code", "string"),
                UnitField("lifetime", "integer", "tick"), Field("target_count", "integer"))
        },
        new object[]
        {
            new { id = "severity", type = "enum", role = "structural", required = true,
                values = new[] { "info", "warning", "error" } }
        }, "terminals", OutputPermission, "result");

    /// <summary>The three capability rows in the order this module declares them. They travel with
    /// <see cref="Bindings"/>: the runtime refuses an implemented binding whose capability nothing in the same
    /// module declares, so the rows and the bindings are one declaration and either both are registered or
    /// neither is.</summary>
    public static object[] Rows() => new object[] { CommandRow(), VisibilityRow(), OutputRow() };

    /// <summary>One execute binding row: this provider's own id, the canonical capability, the handler the
    /// native half supplies, and no dependencies — the closure of a plan that pins it is the row itself.</summary>
    public static object BindingRow(string bindingId, string capability, string handler) => new
    {
        id = bindingId,
        capabilityId = capability,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    /// <summary>The three binding rows in the same order as <see cref="Rows"/>.</summary>
    public static object[] Bindings() => new object[]
    {
        BindingRow(CommandBindingId, CommandCapability, CommandHandlerName),
        BindingRow(VisibilityBindingId, VisibilityCapability, VisibilityHandlerName),
        BindingRow(OutputBindingId, OutputCapability, OutputHandlerName)
    };

    /// <summary>One binding's registration support: the one permission the catalog's recipient contract names
    /// for the row. A terminal is a map object whose own command and screen a plan reaches through this
    /// provider, so the permission is the row's, not the whole map-object namespace's read permission.</summary>
    public static BindingSupport Support(string bindingId, string permission)
        => new(bindingId, "implementation-only", new[] { permission });

    /// <summary>The three registration support rows, in the same order as <see cref="Bindings"/>.</summary>
    public static BindingSupport[] Supports() => new[]
    {
        Support(CommandBindingId, CommandPermission),
        Support(VisibilityBindingId, VisibilityPermission),
        Support(OutputBindingId, OutputPermission)
    };
}
