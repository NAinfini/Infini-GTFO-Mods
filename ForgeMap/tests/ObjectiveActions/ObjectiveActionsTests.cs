using System.Text.Json;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMap.Tests.NativeObjectiveActions;

/// <summary>
/// The objective action layer and its five command handlers, compiled against the doubles the production sources
/// are. Every native entry point here is a managed double: what these cases prove is that a request reaches the
/// member it says it reaches, with the arguments the request resolved, that a request no native entry can carry
/// is refused by name instead of being run in its plain form, and that the result row carries the code the layer
/// decided. No GTFO assembly is loaded and no instance here is game-verified.
///
/// The parameters a case passes are the compiled form's own values — an enum as the index of its member, a
/// number as itself — and `Contexts.For` resolves them through the runtime's own handler-boundary rule, so what
/// a handler reads here is what a dispatch would hand it.
/// </summary>
public sealed class ObjectiveActionsTests
{
    public ObjectiveActionsTests() => SyntheticWorld.Reset();

    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    // The row's own `layer` vocabulary, in declaration order: main, secondary, third.
    private const int MainLayer = 0, SecondaryLayer = 1, ThirdLayer = 2;
    // The `chain` member that means the whole layer. A chain address is never negative, so -1 is the whole-layer
    // form and nothing else.
    private const int WholeLayer = -1;

    private static CommandContext StateContext(object? inputs, object? parameters)
        => Contexts.For(ObjectiveActionContract.StateCapability, inputs, parameters);
    private static CommandContext PhaseContext(object? inputs, object? parameters)
        => Contexts.For(ObjectiveActionContract.PhaseCapability, inputs, parameters);
    private static CommandContext ExtractionContext(object? inputs, object? parameters)
        => Contexts.For(ObjectiveActionContract.ExtractionCapability, inputs, parameters);


    [Fact]
    public void state_start_carries_the_layer_and_the_sub_objective_it_resolved()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.SecondaryLayer, 0, 1);
        var result = world.Handler().State(StateContext(new { state = "UpdateSubObjective" },
            new { layer = SecondaryLayer, chain = 1, sub_objective = (int)eWardenSubObjectiveStatus.GoToZone }));
        Require(result.Status == CommandStatuses.Succeeded && result.Code == ObjectiveActions.SubObjectiveUpdated,
            "The sub-objective member was not issued: " + result.Status + ":" + result.Code);
        var interaction = WardenObjectiveManager.Interactions.Single();
        Require(interaction.type == eWardenObjectiveInteractionType.UpdateSubObjective
            && interaction.inLayer == LG_LayerType.SecondaryLayer
            && interaction.newSubObj == eWardenSubObjectiveStatus.GoToZone,
            "The interaction did not carry the layer and sub-objective that were asked for.");
    }

    [Fact]
    public void state_extra_time_goes_through_the_members_own_entry()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().State(StateContext(new { state = "SetExtraTime" },
            new { layer = MainLayer, chain = 0, extra_time = 45 }));
        Require(result.Status == CommandStatuses.Succeeded && result.Code == ObjectiveActions.ExtraTimeSet,
            "Extra time was not issued: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.ExtraTimes.Count == 1 && WardenObjectiveManager.ExtraTimes[0].Time == 45f,
            "The dedicated extra-time entry was not the one reached.");
        Require(WardenObjectiveManager.Interactions.Count == 0,
            "Extra time was also sent through the general interaction entry.");
    }

    [Fact]
    public void state_refuses_an_objective_this_level_never_built()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().State(StateContext(new { state = "StartObjective" },
            new { layer = MainLayer, chain = 7 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == ObjectiveActions.UnknownTarget,
            "A chain the level never built was asked for: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.Interactions.Count == 0, "A write happened for an objective that does not exist.");
    }

    [Fact]
    public void state_refuses_an_interaction_family_a_sibling_row_owns()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().State(StateContext(new { state = "SolveWardenObjectiveItem" },
            new { layer = MainLayer, chain = 0 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == ObjectiveActions.StateKindUnknown,
            "A member no state mapping carries was accepted: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.Interactions.Count == 0, "The item-solve family was carried out under the state row.");
    }

    [Fact]
    public void state_refuses_an_expected_state_that_names_another_member()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().State(StateContext(new { state = "StartObjective", expected_state = "objective.discovered" },
            new { layer = MainLayer, chain = 0 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == "objective-state-mismatch",
            "An expected state naming another member was accepted: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.Interactions.Count == 0, "A mismatched expectation still wrote.");
    }

    [Fact]
    public void state_accepts_an_expected_state_that_names_the_member_it_asks_for()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().State(StateContext(new { state = "StartObjective", expected_state = "objective.StartObjective" },
            new { layer = MainLayer, chain = 0 }));
        Require(result.Status == CommandStatuses.Succeeded, "A matching expectation refused the request: " + result.Code);
    }

    [Fact]
    public void state_refuses_the_resource_target_port_it_cannot_read()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().State(StateContext(new
        {
            objectives = new[] { new { resourceKind = "objective", resourceId = "objective/main/0" } },
            state = "StartObjective"
        }, new { layer = MainLayer, chain = 0 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == "objective-target-unsupported",
            "A resource target was silently ignored: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.Interactions.Count == 0, "A resource-only request still wrote.");
    }

    [Fact]
    public void state_refuses_when_the_objective_manager_is_not_there()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        WardenObjectiveManager.Current = null;
        var handler = new ObjectiveActionHandler(() => true, () => null, () => ElevatorShaftLanding.Current,
            world.Reports.Add);
        var result = handler.State(StateContext(new { state = "StartObjective" }, new { layer = MainLayer, chain = 0 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == ObjectiveActions.Unavailable,
            "A request without a manager was not refused: " + result.Status + ":" + result.Code);
    }

    [Fact]
    public void state_refuses_a_manager_the_world_has_torn_down()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        world.Objectives.Destroyed = true;
        var result = world.Handler().State(StateContext(new { state = "StartObjective" }, new { layer = MainLayer, chain = 0 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == ObjectiveActions.Unavailable,
            "A collected manager was asked to write: " + result.Status + ":" + result.Code);
    }

    [Fact]
    public void state_refuses_a_layer_the_row_does_not_name()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().State(StateContext(new { state = "StartObjective" }, new { layer = 9, chain = 0 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == "objective-layer-unknown",
            "A layer outside the vocabulary was accepted: " + result.Status + ":" + result.Code);
    }

    // ---------------------------------------------------------------- objective_phase

    [Fact]
    public void phase_event_update_carries_the_chain_and_the_event_step()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0, 3);
        var result = world.Handler().Phase(PhaseContext(new { phase = "EventUpdate" }, new
        {
            layer = MainLayer, chain = 3, transition_policy = 1, event_break_index = 2, event_index = 5
        }));
        Require(result.Status == CommandStatuses.Succeeded && result.Code == ObjectiveActions.PhaseUpdated,
            "The event step was not issued: " + result.Status + ":" + result.Code);
        var interaction = WardenObjectiveManager.Interactions.Single();
        Require(interaction.type == eWardenObjectiveInteractionType.EventUpdate
            && interaction.ownerChainIndexPlusOne == 4
            && interaction.newOnActivateEventBreakIndex == 2
            && interaction.newOnActivateEventIndex == 5,
            "The interaction did not carry the chain byte and the two event indices.");
    }

    [Fact]
    public void phase_complete_chain_uses_the_whole_layer_force_entry()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.ThirdLayer, 0);
        var result = world.Handler().Phase(PhaseContext(new { phase = "CompleteChain" },
            new { layer = ThirdLayer, chain = WholeLayer, transition_policy = 1 }));
        Require(result.Status == CommandStatuses.Succeeded && result.Code == ObjectiveActions.ChainCompleted,
            "The whole-layer completion was not issued: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.ForceCompletions.SequenceEqual(new[] { LG_LayerType.ThirdLayer }),
            "The force entry was not the one reached for the requested layer.");
        Require(WardenObjectiveManager.Interactions.Count == 0,
            "The whole-layer member also went through the interaction entry.");
    }

    [Fact]
    public void phase_refuses_a_strict_transition_policy()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().Phase(PhaseContext(new { phase = "EventUpdate" },
            new { layer = MainLayer, chain = 0, transition_policy = 0 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == "objective-transition-policy-unsupported",
            "A strict policy was served as a forced transition: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.Interactions.Count == 0, "A strict transition still wrote.");
    }

    [Fact]
    public void phase_refuses_an_event_step_outside_one_byte()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().Phase(PhaseContext(new { phase = "EventUpdate" },
            new { layer = MainLayer, chain = 0, transition_policy = 1, event_break_index = 300 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == ObjectiveActions.EventStepUnsupported,
            "An event break index no byte can hold was accepted: " + result.Status + ":" + result.Code);
    }

    [Fact]
    public void phase_refuses_a_chain_address_for_the_whole_layer_member()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().Phase(PhaseContext(new { phase = "CompleteChain" },
            new { layer = MainLayer, chain = 0, transition_policy = 1 }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == ObjectiveActions.TargetShapeUnsupported,
            "A chain member was served by the whole-layer entry: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.ForceCompletions.Count == 0, "The force entry was reached for a chain form.");
    }

    [Fact]
    public void phase_solve_win_condition_asks_the_interaction_entry()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().Phase(PhaseContext(new { phase = "SolveWinCondition" },
            new { layer = MainLayer, chain = 0, transition_policy = 1 }));
        Require(result.Status == CommandStatuses.Succeeded && result.Code == ObjectiveActions.WinConditionSolved,
            "The win-condition member was not issued: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.Interactions.Single().type == eWardenObjectiveInteractionType.SolveWinCondition,
            "The wrong interaction type was sent for the win-condition member.");
    }

    // ---------------------------------------------------------------- extraction_enable

    [Fact]
    public void extraction_enable_arms_the_landings_own_win_condition()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().Extraction(ExtractionContext(new { enabled = true }, new { layer = MainLayer }));
        Require(result.Status == CommandStatuses.Succeeded && result.Code == ObjectiveActions.ExtractionArmed,
            "Arming the exit was not issued: " + result.Status + ":" + result.Code);
        Require(world.Landing.ActivateCalls == 1 && world.Landing.DeactivateCalls == 0,
            "The landing's own arming member was not the one called.");
    }

    [Fact]
    public void extraction_disable_calls_the_disarming_member_and_claims_nothing_more()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().Extraction(ExtractionContext(new { enabled = false }, new { layer = MainLayer }));
        Require(result.Status == CommandStatuses.Succeeded && result.Code == ObjectiveActions.ExtractionDisarmed,
            "Disarming the exit was not issued: " + result.Status + ":" + result.Code);
        Require(world.Landing.DeactivateCalls == 1 && world.Landing.ActivateCalls == 0,
            "The landing's own disarming member was not the one called.");
    }

    [Fact]
    public void extraction_refuses_the_entity_target_ports_it_cannot_resolve()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var target = new[] { new { worldEpoch = 1, lifeEpoch = 1, local = 0, provider = 0 } };
        var byExit = world.Handler().Extraction(ExtractionContext(new { extractions = target, enabled = true },
            new { layer = MainLayer }));
        Require(byExit.Status == CommandStatuses.Rejected && byExit.Code == "extraction-target-unsupported",
            "An exit entity target was silently ignored: " + byExit.Code);
        var byParticipants = world.Handler().Extraction(ExtractionContext(new { enabled = true, participants = target },
            new { layer = MainLayer }));
        Require(byParticipants.Status == CommandStatuses.Rejected && byParticipants.Code == "participants-unsupported",
            "A participants collection was silently ignored: " + byParticipants.Code);
        Require(world.Landing.ActivateCalls == 0, "A refused request still armed the exit.");
    }

    [Fact]
    public void extraction_refuses_a_layer_with_no_objective_data()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var result = world.Handler().Extraction(ExtractionContext(new { enabled = true }, new { layer = ThirdLayer }));
        Require(result.Status == CommandStatuses.Rejected && result.Code == ObjectiveActions.NoObjectiveData,
            "A layer this level has no data for was armed: " + result.Status + ":" + result.Code);
        Require(world.Landing.ActivateCalls == 0, "A layer without objective data still armed the exit.");
    }

    // ---------------------------------------------------------------- the declaration itself

    [Fact]
    public void every_handler_shape_names_ports_and_parameters_its_row_declares()
    {
        // The one check the registration makes of this half: a shape that names a port or a parameter its row does
        // not declare is `shape-port` at registration, and a row whose parameter no handler reads is a promise
        // nothing keeps. Both directions are compared here, so the declaration and the handler cannot drift.
        var rows = new (string Handler, HandlerShape Shape)[]
        {
            (ObjectiveActionContract.StateHandlerName, ObjectiveActionContract.StateShape),
            (ObjectiveActionContract.PhaseHandlerName, ObjectiveActionContract.PhaseShape),
            (ObjectiveActionContract.ExtractionHandlerName, ObjectiveActionContract.ExtractionShape)
        };
        var capabilities = new[]
        {
            ObjectiveActionContract.StateCapability, ObjectiveActionContract.PhaseCapability,
            ObjectiveActionContract.ExtractionCapability
        };
        Require(rows.Length == capabilities.Length, "The three rows and the three handlers do not pair up.");
        for (int index = 0; index < rows.Length; index++)
        {
            var graph = Contexts.Row(capabilities[index]).GetProperty("graph");
            var inputs = graph.GetProperty("inputs").EnumerateArray()
                .Select(port => port.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
            var outputs = graph.GetProperty("outputs").EnumerateArray()
                .Select(port => port.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
            var parameters = graph.GetProperty("parameters").EnumerateArray()
                .Select(parameter => parameter.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
            foreach (var port in rows[index].Shape.InputPorts)
                Require(inputs.Contains(port), rows[index].Handler + " names an input its row does not declare: " + port);
            foreach (var port in rows[index].Shape.OutputPorts)
                Require(outputs.Contains(port), rows[index].Handler + " names an output its row does not declare: " + port);
            foreach (var parameter in rows[index].Shape.ParameterIds)
                Require(parameters.Contains(parameter), rows[index].Handler + " names a parameter its row does not declare: " + parameter);
            foreach (var input in inputs)
                if (input != "in")
                    Require(rows[index].Shape.InputPorts.Contains(input),
                        capabilities[index] + " declares an input no handler reads: " + input);
            foreach (var parameter in parameters)
                Require(rows[index].Shape.ParameterIds.Contains(parameter),
                    capabilities[index] + " declares a parameter no handler reads: " + parameter);
        }
    }

    [Fact]
    public void every_binding_row_and_support_row_pair_up_by_id()
    {
        var bindings = ObjectiveActionContract.Bindings();
        var support = ObjectiveActionContract.Supports();
        Require(bindings.Length == 3 && support.Length == 3, "The declaration does not carry the three rows.");
        var bindingIds = bindings.Select(row => RuntimeJson.From(row).GetProperty("id").GetString()!).ToArray();
        Require(bindingIds.Distinct(StringComparer.Ordinal).Count() == 3, "Two binding rows share an id.");
        foreach (var id in bindingIds)
            Require(support.Any(row => row.BindingId == id), "A binding row has no registration support row: " + id);
        foreach (var row in support)
            Require(bindingIds.Contains(row.BindingId), "A support row names a binding nobody declares: " + row.BindingId);
        foreach (var (row, index) in bindings.Select((row, index) => (RuntimeJson.From(row), index)))
        {
            Require(row.GetProperty("role").GetString() == "execute", "The row at " + index + " is not an execute binding.");
            Require(row.GetProperty("status").GetString() == "implemented", "The row at " + index + " is not declared implemented.");
            Require(row.GetProperty("providerId").GetString() == ModuleDefinition.ProviderId,
                "The row at " + index + " belongs to another provider.");
            Require(ObjectiveActionContract.Graphs.ContainsKey(row.GetProperty("capabilityId").GetString()!),
                "The row at " + index + " implements a capability this contract does not declare.");
        }
    }

    // ---------------------------------------------------------------- authority and result rows

    [Fact]
    public void a_non_host_side_refuses_every_action_before_anything_native_is_read()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var handler = world.Handler(canExecute: false);
        var state = handler.State(StateContext(new { state = "StartObjective" }, new { layer = MainLayer, chain = 0 }));
        var phase = handler.Phase(PhaseContext(new { phase = "CompleteChain" },
            new { layer = MainLayer, chain = WholeLayer, transition_policy = 1 }));
        var extraction = handler.Extraction(ExtractionContext(new { enabled = true }, new { layer = MainLayer }));
        foreach (var (name, result) in new[] { ("state", state), ("phase", phase), ("extraction", extraction) })
            Require(result.Status == CommandStatuses.Rejected && result.Code == "authority-or-phase",
                "A non-host side did not refuse the " + name + " action: " + result.Status + ":" + result.Code);
        Require(WardenObjectiveManager.Interactions.Count == 0 && WardenObjectiveManager.ForceCompletions.Count == 0
            && world.Landing.ActivateCalls == 0,
            "A refused action reached a native entry anyway.");
    }

    [Fact]
    public void every_result_carries_the_rows_the_catalog_schema_declares()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var issued = world.Handler().State(StateContext(new { state = "StartObjective" },
            new { layer = MainLayer, chain = 0 }));
        var rejected = world.Handler().State(StateContext(new { state = "StartObjective" },
            new { layer = MainLayer, chain = 9 }));
        foreach (var (name, result) in new[] { ("issued", issued), ("rejected", rejected) })
        {
            var row = result.Outputs.GetProperty("results").EnumerateArray().Single();
            Require(row.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String,
                "The " + name + " row carries no status column.");
            Require(row.TryGetProperty("committed", out var committed) && committed.ValueKind == JsonValueKind.String,
                "The " + name + " row carries no committed column.");
            Require(row.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String,
                "The " + name + " row carries no code column.");
            Require(row.GetProperty("target_count").GetInt32() == 1, "The " + name + " row does not report its one target.");
        }
        Require(issued.CommitState == CommitStates.Confirmed && issued.Status == CommandStatuses.Succeeded,
            "An issued action did not report a confirmed commit: " + issued.Status + ":" + issued.CommitState);
        Require(rejected.CommitState == CommitStates.None && rejected.Status == CommandStatuses.Rejected,
            "A refused action did not report an empty commit: " + rejected.Status + ":" + rejected.CommitState);
        Require(rejected.Outputs.GetProperty("results").EnumerateArray().Single().GetProperty("code").GetString()
            == ObjectiveActions.UnknownTarget, "The refused row does not carry the code that refused it.");
    }

    // ---------------------------------------------------------------- world epoch

    [Fact]
    public void a_new_world_leaves_the_previous_worlds_objectives_unreachable()
    {
        using var world = SyntheticWorld.Start().WithChain(LG_LayerType.MainLayer, 0);
        var before = world.Handler().State(StateContext(new { state = "StartObjective" },
            new { layer = MainLayer, chain = 0 }));
        Require(before.Status == CommandStatuses.Succeeded, "The first world refused a chain it built: " + before.Code);
        // The next world has its own level: the native objective tables are rebuilt, and the one the previous
        // world built is gone. An address from that world is refused rather than resolved against the new one.
        world.Kernel.BeginWorld(2);
        SyntheticWorld.Reset();
        WardenObjectiveManager.Current = world.Objectives;
        var after = world.Handler().State(StateContext(new { state = "StartObjective" },
            new { layer = MainLayer, chain = 0 }));
        Require(after.Status == CommandStatuses.Rejected && after.Code == ObjectiveActions.UnknownTarget,
            "A chain of the previous world was still resolvable: " + after.Status + ":" + after.Code);
        // The native tables a new world builds are empty until its own level fills them, so the refusal above
        // left the table it would have written to untouched.
        Require(WardenObjectiveManager.Interactions.Count == 0, "The new world wrote an objective it does not have.");
    }
}






