using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;
using SNetwork;

namespace ForgeMapTests.TerminalActionFacts;

// The two terminal action rows, driven the way the kernel drives them: a request frame at the ports the
// capability declares, the node's own structural parameters, and the result the handler returns. The native
// members behind the action are doubles, so what these cases prove is that the action asks the member it says it
// asks with the arguments it resolved, refuses by name what it cannot carry, and reports each outcome as a row
// the catalog's own field list can read. No GTFO assembly is loaded and no instance here is game-verified.
public sealed class TerminalObjectActionsTests
{
    private const string AuthorityCode = "authority-or-phase";

    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    private static JsonElement Terminals(World world, params LG_ComputerTerminal[] terminals)
        => World.Request(("terminals", terminals.Select(t => World.Entity(world.ReferenceOf(t))).ToArray()));

    private static JsonElement Command(World world, string? command, string? arguments, params LG_ComputerTerminal[] terminals)
        => World.Request(
            ("terminals", terminals.Select(t => World.Entity(world.ReferenceOf(t))).ToArray()),
            ("command", command),
            ("arguments", arguments));

    private static JsonElement Print(World world, string? text, int? lifetime, params LG_ComputerTerminal[] terminals)
    {
        var frame = new List<(string, object?)>
        {
            ("terminals", terminals.Select(t => World.Entity(world.ReferenceOf(t))).ToArray()),
            ("text", text)
        };
        if (lifetime.HasValue) frame.Add(("lifetime", lifetime.Value));
        return World.Request(frame.ToArray());
    }

    private static JsonElement Severity(string severity) => World.Parameters(("severity", severity));

    // ---- the two rows themselves -------------------------------------------------------------------------

    [Fact]
    public void the_two_rows_are_the_action_rows_the_registry_accepts()
    {
        using var world = new World();
        world.Start();
        var rows = TerminalObjectContract.Rows().Select(row => RuntimeJson.From(row)).ToArray();

        Require(rows.Length == 2
            && rows[0].GetProperty("id").GetString() == TerminalObjectContract.CommandCapability
            && rows[1].GetProperty("id").GetString() == TerminalObjectContract.OutputCapability,
            "The contract does not declare the two rows in their catalog order.");
        foreach (var row in rows)
        {
            Require(row.GetProperty("owner").GetString() == ModuleDefinition.ProviderId
                && row.GetProperty("kind").GetString() == "action"
                && row.GetProperty("parameters").GetProperty("description").GetString()!.Length > 0,
                "A row is not owned by this provider or is not an action row: " + row);
            var graph = row.GetProperty("graph");
            Require(graph.GetProperty("execution").GetString() == "host"
                && graph.GetProperty("domains").EnumerateArray().Select(d => d.GetString())
                    .SequenceEqual(new[] { "map", "room", "logic" }),
                "A row's execution tier or domains are not the catalog's: " + graph);
            // The recipient block is the catalog's own and is mandatory for an action row: the collection port,
            // the entity target with many cardinality, the row's permission and the result port.
            var recipients = graph.GetProperty("recipients");
            Require(recipients.GetProperty("input").GetString() == "terminals"
                && recipients.GetProperty("target").GetString() == "entity"
                && recipients.GetProperty("cardinality").GetString() == "many"
                && recipients.GetProperty("result").GetString() == "result"
                && recipients.GetProperty("requires").EnumerateArray().Select(p => p.GetString()).Count() == 1,
                "A row's recipient contract is not the catalog's: " + recipients);
            var result = graph.GetProperty("outputs").EnumerateArray()
                .Single(port => port.GetProperty("id").GetString() == "result");
            Require(result.GetProperty("type").GetString() == "result"
                && result.GetProperty("fields").GetArrayLength() == (row.GetProperty("id").GetString() ==
                    TerminalObjectContract.CommandCapability ? 5 : 6),
                "A row's result port does not carry the catalog's field list: " + result);
            Require(result.GetProperty("fields")[0].GetProperty("id").GetString() == "target"
                && result.GetProperty("fields")[1].GetProperty("id").GetString() == "status"
                && result.GetProperty("fields")[2].GetProperty("id").GetString() == "committed"
                && result.GetProperty("fields")[3].GetProperty("id").GetString() == "code",
                "A result row does not start with the four fixed columns: " + result.GetProperty("fields"));
        }
        Require(rows[1].GetProperty("graph").GetProperty("parameters").EnumerateArray()
                .Single().GetProperty("id").GetString() == "severity",
            "The printed-line row's one structural parameter is not the catalog's severity.");
    }

    // ---- the command row ---------------------------------------------------------------------------------

    [Fact]
    public void command_asks_the_terminals_own_parser_and_the_managers_own_entry()
    {
        using var world = new World();
        world.Start();
        var zone = world.AddZone();
        var terminal = world.Terminal(zone, TERM_Command.Open);

        var result = world.Command(Command(world, "open", "door_1", terminal));

        Require(result.Status == CommandStatuses.Succeeded && result.CommitState == CommitStates.Confirmed,
            "A whitelisted command on one live terminal did not commit: " + result.Status + " " + result.Code);
        var sent = LG_ComputerTerminalManager.Sent.Single();
        Require(sent.TerminalId == terminal.SyncID && sent.Command == TERM_Command.Open,
            "The manager's own entry was not asked for this terminal's sync id and parsed command: " + sent);
        Require(sent.Input == "open door_1" && sent.Param1 == "door_1" && sent.Param2 == "",
            "The input line the interpreter parsed was not the one the row composed: " + sent);
        Require(terminal.m_command!.ParseCalls == 1 && terminal.m_command.LastInput == "open door_1",
            "The terminal's own interpreter was not the parser asked for the line.");
    }

    [Fact]
    public void command_row_is_the_rows_the_catalog_declares()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Ping);

        var result = world.Command(Command(world, "ping", null, terminal));

        var row = World.Row(result);
        Require(row.GetProperty("target").GetProperty("id").GetString() == world.ReferenceOf(terminal).Id
            && row.GetProperty("target").GetProperty("worldEpoch").GetInt64() == World.WorldEpoch
            && row.GetProperty("status").GetString() == "succeeded"
            && row.GetProperty("committed").GetString() == CommitStates.Confirmed
            && row.GetProperty("code").GetString() == TerminalActions.CommandSent
            && row.GetProperty("target_count").GetInt32() == 1,
            "The command row is not the canonical row shape: " + row);
    }

    [Fact]
    public void command_writes_one_row_per_terminal_in_the_plans_order()
    {
        using var world = new World();
        world.Start();
        var zone = world.AddZone();
        var first = world.Terminal(zone, TERM_Command.Open);
        var second = world.Terminal(zone, TERM_Command.Open);

        var result = world.Command(Command(world, "open", null, first, second));

        var rows = World.Rows(result);
        Require(rows.Length == 2 && rows[0].GetProperty("target_count").GetInt32() == 2
            && rows[1].GetProperty("target_count").GetInt32() == 2
            && rows[0].GetProperty("code").GetString() == TerminalActions.CommandSent
            && rows[1].GetProperty("code").GetString() == TerminalActions.CommandSent,
            "Two terminals did not produce two committed rows: " + result.Outputs);
        Require(LG_ComputerTerminalManager.Sent.Count == 2
            && LG_ComputerTerminalManager.Sent[0].TerminalId == first.SyncID
            && LG_ComputerTerminalManager.Sent[1].TerminalId == second.SyncID,
            "The terminals were not asked in the plan's own order.");
    }

    [Fact]
    public void command_refuses_a_client_before_any_native_read()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);
        world.LoseAuthority();

        var result = world.Command(Command(world, "open", null, terminal));

        Require(result.Status == CommandStatuses.Rejected && result.Code == AuthorityCode,
            "A non-authoritative side was not refused by name: " + result.Status + " " + result.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0 && terminal.m_command!.ParseCalls == 0,
            "A refused request still reached the terminal's parser or the manager's entry.");
    }

    [Fact]
    public void command_refuses_on_a_peer_the_game_does_not_call_master()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);
        SNet.IsMaster = false;

        var result = world.Command(Command(world, "open", null, terminal));

        Require(result.Status == CommandStatuses.Rejected && result.Code == AuthorityCode,
            "A peer the game does not call master was not refused: " + result.Status + " " + result.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A non-master request reached the command entry.");
    }

    [Fact]
    public void command_refuses_a_terminal_the_kernel_no_longer_answers_for()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);
        var retired = world.ReferenceOf(terminal);
        world.Retire(terminal);

        var result = world.Command(World.Request(("terminals", new[] { World.Entity(retired) }), ("command", "open")));

        Require(result.Status == CommandStatuses.Rejected && result.Code == TerminalObjectActions.StaleCode,
            "A retired terminal was not refused as stale: " + result.Status + " " + result.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A retired terminal was written to anyway.");
    }

    [Fact]
    public void command_refuses_a_reference_from_an_earlier_world()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);

        var result = world.Command(World.Request(
            ("terminals", new[] { World.Entity(world.StaleWorldReference(terminal)) }), ("command", "open")));

        Require(result.Status == CommandStatuses.Rejected && result.Code == TerminalObjectActions.StaleCode,
            "A reference from an earlier world was not refused: " + result.Status + " " + result.Code);
    }

    [Fact]
    public void command_refuses_another_kind_and_another_category_of_its_own_kind()
    {
        using var world = new World();
        world.Start();
        var door = world.DoorReference();
        var foreign = world.ForeignReference();

        var doorResult = world.Command(World.Request(("terminals", new[] { World.Entity(door) }), ("command", "open")));
        var foreignResult = world.Command(World.Request(("terminals", new[] { World.Entity(foreign) }), ("command", "open")));

        Require(doorResult.Status == CommandStatuses.Rejected && doorResult.Code == TerminalObjectActions.KindCode,
            "A door was not refused by category: " + doorResult.Status + " " + doorResult.Code);
        Require(foreignResult.Status == CommandStatuses.Rejected && foreignResult.Code == TerminalObjectActions.KindCode,
            "Another kind was not refused by kind: " + foreignResult.Status + " " + foreignResult.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A refused kind reached the command entry.");
    }

    [Fact]
    public void command_refuses_an_instance_the_kernels_own_lookup_does_not_agree_with()
    {
        using var world = new World();
        world.Start();
        var zone = world.AddZone();
        var terminal = world.Terminal(zone, TERM_Command.Open);
        var other = world.Terminal(zone, TERM_Command.Open);
        // The instance now answers for the other terminal's reference, so the lookup the action verifies its
        // reference against disagrees with it.
        var asked = world.Alias(terminal, world.ReferenceOf(other));

        var result = world.Command(World.Request(
            ("terminals", new[] { World.Entity(asked) }), ("command", "open")));

        Require(result.Status == CommandStatuses.Rejected && result.Code == TerminalObjectActions.MismatchCode,
            "An instance that resolves to another reference was not refused as a mismatch: "
                + result.Status + " " + result.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A mismatched instance was written to.");
    }

    [Fact]
    public void command_refuses_a_lookup_that_throws()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);
        world.LookupThrows = true;

        var result = world.Command(Command(world, "open", null, terminal));

        Require(result.Status == CommandStatuses.Rejected && result.Code == TerminalObjectActions.StaleCode,
            "A provider whose own table is mid-teardown was not refused: " + result.Status + " " + result.Code);
        Require(world.Reports.Any(r => r.StartsWith("map.terminal-resolve-exception", StringComparison.Ordinal)),
            "The refused lookup was not reported.");
    }

    [Fact]
    public void command_refuses_an_empty_command_and_a_command_the_terminal_does_not_know()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);

        var empty = world.Command(Command(world, "   ", null, terminal));
        var unknown = world.Command(Command(world, "shutdown", null, terminal));

        Require(empty.Status == CommandStatuses.Rejected && empty.Code == TerminalActions.CommandEmpty,
            "An empty command was not refused: " + empty.Status + " " + empty.Code);
        Require(unknown.Status == CommandStatuses.Rejected && unknown.Code == TerminalActions.CommandUnknown,
            "A line the terminal's own parser does not know was not refused: " + unknown.Status + " " + unknown.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A refused line reached the command entry.");
    }

    [Fact]
    public void command_refuses_a_parsed_command_outside_the_whitelist()
    {
        using var world = new World();
        world.Start();
        // The interpreter's own vocabulary is the enum member names, so `EmptyLine` is what its parser answers
        // for the one line a player would type as `empty_line`: the double mirrors the parse table, not the
        // spelling a player uses.
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Exit, TERM_Command.EmptyLine);

        var session = world.Command(Command(world, "exit", null, terminal));
        var marker = world.Command(Command(world, "EmptyLine", null, terminal));

        Require(session.Status == CommandStatuses.Rejected && session.Code == TerminalActions.CommandNotAllowed,
            "A sitting player's own screen command was not refused by name: " + session.Status + " " + session.Code);
        Require(marker.Status == CommandStatuses.Rejected && marker.Code == TerminalActions.CommandNotAllowed,
            "An interpreter marker was not refused by name: " + marker.Status + " " + marker.Code);
        Require(terminal.m_command!.ParseCalls == 2, "The terminal's own parser was not the parser consulted.");
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A command outside the whitelist reached the entry.");
    }

    [Fact]
    public void command_refuses_when_no_manager_or_interpreter_can_be_asked()
    {
        using var world = new World();
        world.Start();
        var zone = world.AddZone();
        var terminal = world.Terminal(zone, TERM_Command.Open);
        var bare = world.Bare(zone);
        world.StandDownManager();

        var noManager = world.Command(Command(world, "open", null, terminal));
        Require(noManager.Code == TerminalActions.ManagerUnavailable,
            "A missing manager was not reported: " + noManager.Status + " " + noManager.Code);
        Require(LG_ComputerTerminalManager.Sent.Count == 0, "A refused request reached the command entry.");

        LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager();
        var noInterpreter = world.Command(Command(world, "open", null, bare));
        Require(noInterpreter.Code == TerminalActions.InterpreterUnavailable,
            "A terminal with no interpreter was not reported: " + noInterpreter.Code);
    }

    [Fact]
    public void command_reports_a_partial_run_and_keeps_the_plan_order()
    {
        using var world = new World();
        world.Start();
        var zone = world.AddZone();
        var good = world.Terminal(zone, TERM_Command.Open);
        var bad = world.Terminal(zone, TERM_Command.Open);
        var goodReference = world.ReferenceOf(good);
        var badReference = world.ReferenceOf(bad);
        world.Retire(bad);

        var result = world.Command(World.Request(
            ("terminals", new[] { World.Entity(goodReference), World.Entity(badReference) }), ("command", "open")));

        Require(result.Status == CommandStatuses.Partial && result.CommitState == CommitStates.Confirmed,
            "One committed row beside one rejected row was not reported as partial: "
                + result.Status + " " + result.CommitState);
        var rows = World.Rows(result);
        Require(rows[0].GetProperty("code").GetString() == TerminalActions.CommandSent
            && rows[1].GetProperty("code").GetString() == TerminalObjectActions.StaleCode,
            "The rows did not report each terminal's own outcome: " + result.Outputs);
    }

    [Fact]
    public void command_refuses_a_request_that_names_no_terminal()
    {
        using var world = new World();
        world.Start();

        var result = world.Command(World.Request(("command", "open")));

        Require(result.Status == CommandStatuses.Rejected && result.Code == TerminalObjectActions.NoTargetsCode,
            "An empty recipient set was not refused: " + result.Status + " " + result.Code);
    }

    // ---- the printed-line row ----------------------------------------------------------------------------

    [Fact]
    public void output_prints_the_severity_it_was_asked_for_on_the_terminals_own_line_kinds()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);

        var info = world.Output(Print(world, "line one", null, terminal), Severity("info"));
        var warning = world.Output(Print(world, "line two", null, terminal), Severity("warning"));
        var error = world.Output(Print(world, "line three", null, terminal), Severity("error"));

        Require(info.Status == CommandStatuses.Succeeded && warning.Status == CommandStatuses.Succeeded
            && error.Status == CommandStatuses.Succeeded,
            "A printed line did not commit: " + info.Status + " " + warning.Status + " " + error.Status);
        var types = terminal.Lines.Select(l => l.Type).ToArray();
        Require(types.SequenceEqual(new[] { TerminalLineType.Normal, TerminalLineType.Warning, TerminalLineType.Fail }),
            "The three severities did not map onto the terminal's own line kinds: " + string.Join(", ", types));
        Require(terminal.Lines.Select(l => l.Text).SequenceEqual(new[] { "line one", "line two", "line three" })
            && terminal.Lines.All(l => l.Time == 0f),
            "The lines were not the text asked for with the member's own zero time argument.");
    }

    [Fact]
    public void output_row_is_the_rows_the_catalog_declares_and_says_the_line_is_local()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);

        var result = world.Output(Print(world, "a line", null, terminal), Severity("info"));

        var row = World.Row(result);
        Require(row.GetProperty("target").GetProperty("id").GetString() == world.ReferenceOf(terminal).Id
            && row.GetProperty("status").GetString() == "succeeded"
            && row.GetProperty("committed").GetString() == CommitStates.Confirmed
            && row.GetProperty("code").GetString() == TerminalObjectActions.LinePrintedCode
            && row.GetProperty("target_count").GetInt32() == 1,
            "The printed-line row is not the canonical row shape: " + row);
        Require(TerminalObjectActions.LinePrintedCode.Contains("local", StringComparison.Ordinal),
            "The row's code must say the write is the host's own copy of the terminal.");
    }

    [Fact]
    public void output_refuses_a_lifetime_it_cannot_keep()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);

        var refused = world.Output(Print(world, "a line", 30, terminal), Severity("info"));
        var zero = world.Output(Print(world, "a line", 0, terminal), Severity("info"));

        Require(refused.Status == CommandStatuses.Rejected && refused.Code == TerminalObjectActions.LifetimeCode,
            "A non-zero lifetime was not refused: " + refused.Status + " " + refused.Code);
        Require(zero.Status == CommandStatuses.Succeeded, "A zero lifetime is the member's own default and must print.");
        Require(terminal.Lines.Count == 1, "A refused lifetime still printed a line.");
    }

    [Fact]
    public void output_refuses_an_unknown_severity_and_empty_text()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);

        var severity = world.Output(Print(world, "a line", null, terminal), Severity("critical"));
        var missing = world.Output(Print(world, "a line", null, terminal), World.Parameters());
        var empty = world.Output(Print(world, "", null, terminal), Severity("info"));

        Require(severity.Status == CommandStatuses.Rejected && severity.Code == TerminalObjectActions.SeverityCode,
            "An unknown severity was not refused: " + severity.Status + " " + severity.Code);
        Require(missing.Status == CommandStatuses.Rejected && missing.Code == TerminalObjectActions.SeverityCode,
            "A missing severity was not refused: " + missing.Status + " " + missing.Code);
        Require(empty.Status == CommandStatuses.Rejected && empty.Code == TerminalActions.TextEmpty,
            "Empty text was not refused: " + empty.Status + " " + empty.Code);
        Require(terminal.Lines.Count == 0, "A refused printed line reached the terminal's line buffer.");
    }

    [Fact]
    public void output_refuses_what_the_command_row_refuses_about_the_recipient()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);
        var door = world.DoorReference();

        var wrongKind = world.Output(World.Request(
            ("terminals", new[] { World.Entity(door) }), ("text", "a line")), Severity("info"));
        Require(wrongKind.Status == CommandStatuses.Rejected && wrongKind.Code == TerminalObjectActions.KindCode,
            "A door was not refused by the printed-line row: " + wrongKind.Status + " " + wrongKind.Code);

        var none = world.Output(World.Request(("text", "a line")), Severity("info"));
        Require(none.Status == CommandStatuses.Rejected && none.Code == TerminalObjectActions.NoTargetsCode,
            "An empty recipient set was not refused: " + none.Status + " " + none.Code);

        var retired = world.ReferenceOf(terminal);
        world.Retire(terminal);
        var stale = world.Output(World.Request(("terminals", new[] { World.Entity(retired) }), ("text", "a line")),
            Severity("info"));
        Require(stale.Status == CommandStatuses.Rejected && stale.Code == TerminalObjectActions.StaleCode,
            "A retired terminal was not refused: " + stale.Status + " " + stale.Code);
        Require(terminal.Lines.Count == 0, "A refused printed line reached the terminal's line buffer.");
    }

    [Fact]
    public void output_refuses_a_client_before_any_native_write()
    {
        using var world = new World();
        world.Start();
        var terminal = world.Terminal(world.AddZone(), TERM_Command.Open);
        world.LoseAuthority();

        var client = world.Output(Print(world, "a line", null, terminal), Severity("info"));

        Require(client.Status == CommandStatuses.Rejected && client.Code == AuthorityCode,
            "A client was not refused by the host gate: " + client.Status + " " + client.Code);
        Require(terminal.Lines.Count == 0, "A refused printed line reached the terminal's line buffer.");
    }
}
