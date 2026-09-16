using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `v-obj` row: the read-only state of one objective layer — where it stands, how far along its chain
/// it is, and the countdown value the objective machine holds (`forge.query.objective.state`).
///
/// **One row, and the layer it is about is an input.** The objective machine keeps exactly three fixed layer slots
/// and no per-instance objective identity (`pWardenObjectiveState` carries `main_*`, `second_*` and `third_*` with
/// no id of its own), so "the current layer's objective" is not a thing the game can answer: a layer is what the
/// author selects. The layer travels as the `objective` resource reference the level-event rows already publish
/// (`layer:main`, `layer:secondary`, `layer:third`), so a plan that reads a state and a plan that reacts to that
/// layer's events name the same place with the same text.
///
/// **Where the timer numbers come from.** One member of `pWardenObjectiveState` is published, and it is read as
/// it stands rather than derived:
///
/// - `GetStartTimeFromLayer(layer)` — published as `start_time_seconds`. The accessor takes the layer, which is
///   the strongest available confirmation that the time field belongs to the objective machine's clock and not to
///   this provider.
///
/// **The countdown member is read by nothing here.** `extraTime` (offset 0x30, `System.Single`) and the engine's
/// two entries for it (`WardenObjectiveManager.GetExtraTime()` / `SetExtraTime(System.Single)`) are recorded in
/// `evidence/level-objective-value.json`, but what the field holds *on the wire* — seconds still to run, seconds
/// already granted, or a deadline on the machine's own clock — is not established: the value that would settle it
/// lives in the il2cpp method bodies. Ruling 124.2 withdrew the port rather than publish a name as if it had been
/// measured; `probes/points.tsv` carries the trace point that settles it in game, and the port comes back once it
/// has. Reading a field the row cannot name would be a second claim with no reader, so the read layer does not
/// make it either.
///
/// **The countdown is not `Survival_TimeToActivate` / `Survival_TimeToSurvive`.** Those two live on
/// `WardenObjectiveDataBlock` — the objective's *data*, not its running state — and the data block has no runtime
/// accessor whose caller count this batch could read. `timed` is therefore derived from the objective type the
/// machine reports for the layer (`Survival`, `TimedTerminalSequence`), which is data the state's own layer lookup
/// answers, and the two `Survival_TimeTo*` values are deliberately **not** published: reading them would mean
/// guessing which of them, if either, seeds the countdown.
///
/// Everything else is a direct member read: the layer's status and sub-objective through
/// `GetLayerStatus` / `GetLayerSubStatus`, the chain position through `GetChainIndexForLayer`, the two flags and
/// the objective-item counters. Nothing here writes the objective machine and nothing here keeps a Forge-side
/// ledger; a field the world cannot answer refuses the whole read by name, because a state reported as zeros would
/// read exactly like an objective that really is at chain 0 with no items solved.</summary>
public static class LevelObjectiveValueContract
{
    /// <summary>The Map package's provider id. Written here rather than read from
    /// <see cref="ModuleDefinition"/> because this file is also compiled by the focused test project, which must
    /// not drag the whole provider declaration in to build one binding id; a case asserts the two agree.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    public const string CapabilityId = "forge.query.objective.state";
    public const string BindingId = ProviderId + ".binding.query.objective.state";
    /// <summary>The read half's own handler name. The write row of the same domain (`forge.action.map.objective_state`)
    /// already owns `gtfo.map.objective_state`, and one registration carries exactly one shape per handler name, so
    /// the value row names its read rather than sharing a name with a handler whose ports are another row's. The
    /// vocabulary is the read permission's: `objective.read`.</summary>
    public const string HandlerName = "gtfo.map.objective_read";

    /// <summary>The permission the read needs: the same objective namespace read the level-event observation rows
    /// already declare, and no write permission, because this row writes nothing.</summary>
    public const string Permission = LevelEventContract.ObjectiveReadPermission;

    /// <summary>The input port: the layer whose objective is read, as the `objective` resource reference.</summary>
    public const string InputPort = "objective";

    /// <summary>The refusal an input of another resource kind gets. The reference names a place, and a map or a
    /// zone reference is not one this provider can read an objective from.</summary>
    public const string InputKindCode = "objective-state-input-kind";
    /// <summary>The refusal an input that is not a layer reference gets: the machine has three layers and no
    /// objective identity of its own, so an id the provider cannot spell as a layer names nothing.</summary>
    public const string InputLayerCode = "objective-state-input-layer";
    /// <summary>The refusal a layer this build has no objective data for gets, read from the game's own
    /// `HasWardenObjectiveDataForLayer` rather than from a Forge-side table.</summary>
    public const string NoObjectiveCode = "objective-state-no-objective";
    /// <summary>The refusal a state the process cannot read gets. It is one code for the whole read because
    /// `pWardenObjectiveState` is one value: a state that cannot be read has no fields to report individually.</summary>
    public const string UnavailableCode = "objective-state-unavailable";

    /// <summary>The one shape of this handler: the layer it reads and the ten facts it answers. Declared here so
    /// the native read layer cannot describe a second layout; the registration composes this into its shape
    /// table. The countdown port is absent by ruling 124.2 — the member's meaning is not established, so the row
    /// does not carry a name for it until `probes/points.tsv` settles it in game.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs(InputPort)
        .Outputs("kind", "timed", "phase", "sub_phase", "chain_index", "start_time_seconds",
            "solve_on_death", "exit_wave_triggered", "items_solved", "required_items");

    /// <summary>The three layer names, in the machine's own slot order. It is the level-event contract's own list
    /// and not a second one: one package spells a layer one way, and a case asserts the two agree.</summary>
    public static IReadOnlyList<string> Layers => LevelEventContract.Layers;

    /// <summary>The resource kind and schema of the input port, the same pair the level-event rows publish an
    /// objective under. Spelled as constants because this contract compiles without the trigger family beside it,
    /// and asserted against the level-event rows by a case.</summary>
    public const string ObjectiveResourceKind = "objective";
    public const string ObjectiveResourceSchema = "forge.resource.objective";

    /// <summary>The layer reference prefix the level-event rows publish an objective under.</summary>
    public const string LayerReferencePrefix = "layer:";

    /// <summary>`eWardenObjectiveStatus`'s members with their own values, in member order. The port publishes the
    /// member's name as text: the enum's values are not an ordinal run (0, 10, 20, 30, 40), so an index-shaped
    /// vocabulary would have to invent the gaps the game does not have.</summary>
    public static readonly IReadOnlyList<(int Value, string Name)> Statuses = Array.AsReadOnly(new[]
    {
        (0, "not_discovered"),
        (10, "discovered"),
        (20, "started"),
        (30, "partially_solved"),
        (40, "item_solved")
    });

    /// <summary>`eWardenSubObjectiveStatus`'s members, name for name, in the enum's own order. The "help" members
    /// are the game's own (they are what the objective's text fragment selection reads), so the vocabulary keeps
    /// every one of them rather than folding the help variants into their base step.</summary>
    public static readonly IReadOnlyList<string> SubStatuses = Array.AsReadOnly(new[]
    {
        "find_location_info", "find_location_info_help", "go_to_zone", "go_to_zone_help", "in_zone_find_item",
        "in_zone_find_item_help", "solve_item", "solve_item_help", "go_to_win_condition", "go_to_win_condition_help"
    });

    /// <summary>`eWardenObjectiveType`'s members, name for name, in the enum's own order. Published as the member
    /// index, which is the order below; the name is what the journal and the evidence carry.</summary>
    public static readonly IReadOnlyList<string> Kinds = Array.AsReadOnly(new[]
    {
        "hsu_find_take_sample", "reactor_startup", "reactor_shutdown", "gather_small_items", "clear_a_path",
        "special_terminal_command", "retrieve_big_items", "power_cell_distribution", "terminal_uplink",
        "central_generator_cluster", "activate_small_hsu", "survival", "gather_terminal",
        "corrupted_terminal_uplink", "empty", "timed_terminal_sequence"
    });

    /// <summary>The two objective types the game builds a countdown for. `timed` is derived from the type alone:
    /// `Survival` runs on its own `Survival_TimeToSurvive` and `TimedTerminalSequence` on the terminal sequence's,
    /// and no other type has a timer to read. The two `Survival_TimeTo*` values themselves are data-block members
    /// this row does not publish (see the type's own summary).</summary>
    public const int SurvivalKind = 11, TimedTerminalSequenceKind = 15;

    public static bool IsTimed(int kind) => kind == SurvivalKind || kind == TimedTerminalSequenceKind;

    /// <summary>The layer name of an `objective` reference id, or null when the id is not one this package
    /// writes. The prefix and the layer list are the level-event rows' own, so "layer:secondary" is a layer
    /// reference wherever it appears and "layer:fourth" is not.</summary>
    public static string? LayerOf(string? referenceId)
    {
        if (referenceId == null || !referenceId.StartsWith(LayerReferencePrefix, StringComparison.Ordinal)) return null;
        var layer = referenceId[LayerReferencePrefix.Length..];
        foreach (var known in Layers) if (string.Equals(known, layer, StringComparison.Ordinal)) return known;
        return null;
    }

    /// <summary>The member name one `eWardenObjectiveStatus` value spells, for the journal and the evidence. A
    /// value outside the five members is a refusal at the contract boundary, because reporting it as the nearest
    /// member would describe an objective state the game never had.</summary>
    public static string StatusName(int value)
    {
        foreach (var (member, name) in Statuses) if (member == value) return name;
        throw new RuntimeContractException("objective-status-unknown", "Not an eWardenObjectiveStatus value: " + value);
    }

    /// <summary>The member name one sub-objective value spells, or a refusal for a value past the enum's own last
    /// member.</summary>
    public static string SubStatusName(int value) => value >= 0 && value < SubStatuses.Count
        ? SubStatuses[value]
        : throw new RuntimeContractException("objective-sub-status-unknown", "Not an eWardenSubObjectiveStatus value: " + value);

    /// <summary>One objective layer exactly as the native read found it. It is handed over as a value because the
    /// game-independent assembly this contract lives in holds no game type, and it holds only fields the read
    /// really made: the caller that cannot read the state answers null instead of filling this in.</summary>
    public readonly record struct LayerSample(
        int Kind, int Status, int SubStatus, int ChainIndex, float StartTime,
        bool SolveOnDeath, bool ExitWaveTriggered, int ItemsSolved, int RequiredItems);

    /// <summary>The one read this row needs from the world: the objective state of one layer, or null when this
    /// process cannot read it. The game-bound half supplies it; a registration that carries none answers every
    /// read with a refusal instead of a state, which is what keeps a row that cannot be read from looking like an
    /// objective that reads as not discovered.</summary>
    public sealed record LayerReader(Func<string, LayerSample?> ReadLayer);

    /// <summary>The evaluator, keyed by handler name: what a registration adds to its evaluator table beside the
    /// selectors'. The input is refused by name when it is not a layer reference of this package, and a layer the
    /// objective machine has no objective for is refused rather than answered with zeros.</summary>
    public static EvaluatorHandler Evaluator(LayerReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return context => Evaluate(context, reader);
    }

    private static JsonElement Evaluate(EvaluationContext context, LayerReader reader)
    {
        var layer = LayerOf(ReferenceId(Input(context, InputPort)));
        if (layer == null)
            throw new RuntimeContractException(InputLayerCode, "The input is not one of this level's objective layers.");
        var sample = reader.ReadLayer(layer)
            ?? throw new RuntimeContractException(NoObjectiveCode, "This layer has no objective this process can read: " + layer);
        // The status is validated before the payload is built, so a value outside the vocabulary is refused rather
        // than shipped as an integer the author's own switch would then have to guess about.
        string phase = StatusName(sample.Status);
        string subPhase = SubStatusName(sample.SubStatus);
        return RuntimeJson.From(new
        {
            kind = sample.Kind,
            timed = IsTimed(sample.Kind),
            phase,
            sub_phase = subPhase,
            chain_index = sample.ChainIndex,
            start_time_seconds = sample.StartTime,
            solve_on_death = sample.SolveOnDeath,
            exit_wave_triggered = sample.ExitWaveTriggered,
            items_solved = sample.ItemsSolved,
            required_items = sample.RequiredItems
        });
    }

    /// <summary>The resource id an `objective` input carries. A frame that has resolved the reference hands over
    /// the `resourceKind`/`resourceId` pair and a compiled frame hands over the id alone, exactly as the zone
    /// reference does; a pair that is not an objective is refused by its own code so a map reference is not read
    /// as a layer.</summary>
    private static string? ReferenceId(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new RuntimeContractException(InputKindCode, "The input is not a resource reference.");
        if (!value.TryGetProperty("resourceKind", out var kind))
            return value.TryGetProperty("id", out var compiled) && compiled.ValueKind == JsonValueKind.String
                ? compiled.GetString()
                : throw new RuntimeContractException("missing-field", InputPort);
        var reference = RuntimeJson.ResourceRefOf(value);
        if (reference.ResourceKind != ObjectiveResourceKind)
            throw new RuntimeContractException(InputKindCode, "This row reads an objective, not a " + reference.ResourceKind);
        return reference.ResourceId;
    }

    /// <summary>One required input port of the resolved frame, refused by name when the plan left it out: an absent
    /// objective answered as "not discovered" would read exactly like a layer whose objective is untouched.</summary>
    private static JsonElement Input(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new RuntimeContractException("missing-field", port);

    /// <summary>The capability row a registration declares. It is this package's own row (the catalog has no
    /// value row for the objective domain yet), so it is spelled here in full: kind `state` for a value row,
    /// execution `query` for an on-demand read, one objective resource in and ten read-only facts out.</summary>
    public const string CapabilityRowJson = """
    {
      "id": "forge.query.objective.state",
      "owner": "forge.module.gtfo.map",
      "kind": "state",
      "label": "目标状态与倒计时",
      "version": "1.0.0",
      "parameters": { "description": "读某一层目标的当前状态、链位置，和状态里带的倒计时数值。" },
      "graph": {
        "domains": ["map", "room", "logic"],
        "execution": "query",
        "inputs": [
          { "id": "objective", "type": "resource", "resourceKind": "objective", "schema": "forge.resource.objective" }
        ],
        "outputs": [
          { "id": "kind", "type": "integer" },
          { "id": "timed", "type": "boolean" },
          { "id": "phase", "type": "string" },
          { "id": "sub_phase", "type": "string" },
          { "id": "chain_index", "type": "integer" },
          { "id": "start_time_seconds", "type": "number", "unit": "s" },
          { "id": "solve_on_death", "type": "boolean" },
          { "id": "exit_wave_triggered", "type": "boolean" },
          { "id": "items_solved", "type": "integer" },
          { "id": "required_items", "type": "integer" }
        ],
        "parameters": [],
        "reads": ["world"]
      }
    }
    """;

    /// <summary>The binding row the same registration declares: an on-demand `query` binding, which is the
    /// `observe` role in this runtime — the same role every other value row's evaluator carries.</summary>
    public const string BindingRowJson = """
    {
      "id": "forge.module.gtfo.map.binding.query.objective.state",
      "capabilityId": "forge.query.objective.state",
      "providerId": "forge.module.gtfo.map",
      "handler": "gtfo.map.objective_read",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;

    /// <summary>The row's own registration support: a read that owns no object and writes nothing, so it declares
    /// the objective read permission and depends on no other binding.</summary>
    public static BindingSupport Support()
        => new(BindingId, "implementation-only", new[] { Permission });

    /// <summary>The one shape the handler is resolved against, keyed by handler name: what a registration composes
    /// into its own shape table beside the selectors'.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes()
        => new Dictionary<string, HandlerShape>(StringComparer.Ordinal) { [HandlerName] = Shape };

    /// <summary>The one evaluator, keyed by handler name. The game-bound half supplies the read; this contract
    /// owns the refusals, so a registration that carries none is a refusal by name and not a state of zeros.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(LayerReader reader)
        => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal) { [HandlerName] = Evaluator(reader) };
}
