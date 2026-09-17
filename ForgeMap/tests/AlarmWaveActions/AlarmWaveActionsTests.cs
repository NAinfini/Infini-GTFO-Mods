using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;

/// <summary>
/// The focused suite for the three scan and wave actions. Every case runs the production handler the way
/// the kernel runs it and asserts two things: the result row the plan would read, and whether the native entry
/// the row claims was really invoked. Native state is the doubles in `GameDoubles.cs` and NOT game-verified.
/// The ladder a start row walks is one method behind the scan row, so its cases are that row's — the two
/// alarm rows the rulings deleted had no write of their own.
/// </summary>
public sealed class AlarmWaveActionsTests
{
    // ---- forge.action.map.scan_state (the `start` operation) ------------------------------------------

    [Fact]
    public void scan_state_start_activates_the_named_scan_through_its_own_interaction_entry()
    {
        var manager = Fixture.Level();
        var scan = Fixture.Puzzle("scan-1", "DoorScan-3F");
        manager.m_instances!.Add(scan);

        var result = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-1") }, new { operation = "start" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(AlarmWaveActions.ScanStartedCode, result.Code);
        Assert.Equal(new[] { ChainedPuzzles.eChainedPuzzleInteraction.Activate }, scan.Interactions);
        Assert.True(scan.IsActive, "The native interaction entry did not run.");
        Assert.Equal(AlarmWaveActions.ScanStartedCode, Fixture.Field(result, "code"));
        Assert.Equal("succeeded", Fixture.Field(result, "status"));
        Assert.Equal(CommitStates.Confirmed, Fixture.Field(result, "committed"));
        Assert.Equal(JsonValueKind.Null, Fixture.Row(result).GetProperty("target").ValueKind);
        Assert.Equal(new[] { "target", "status", "committed", "code" },
            Fixture.Row(result).EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void scan_state_start_carries_only_the_resource_and_the_operation()
    {
        // The row declares no handle, no anchor, no participants and no quorum: the rulings had every port the
        // handler could not apply deleted, so a plan cannot ask for one and be ignored.
        var manager = Fixture.Level();
        var scan = Fixture.Puzzle("scan-2");
        manager.m_instances!.Add(scan);

        var result = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-2") }, new { operation = "start" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(AlarmWaveActions.ScanStartedCode, result.Code);
        Assert.False(result.Outputs.TryGetProperty("scan_handle", out _),
            "The scan row publishes no handle port.");
        Assert.Equal(new[] { ChainedPuzzles.eChainedPuzzleInteraction.Activate }, scan.Interactions);
    }

    [Fact]
    public void scan_state_start_accepts_the_author_name_as_the_resource_id()
    {
        var manager = Fixture.Level();
        var scan = Fixture.Puzzle("scan-3", "PublicScanName");
        manager.m_instances!.Add(scan);

        var result = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "PublicScanName") }, new { operation = "start" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Single(scan.Interactions);
    }

    [Fact]
    public void scan_state_start_reports_an_already_active_scan_as_the_state_it_asked_for()
    {
        var manager = Fixture.Level();
        var scan = Fixture.Puzzle("scan-4", active: true);
        manager.m_instances!.Add(scan);

        var result = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-4") }, new { operation = "start" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal(AlarmWaveActions.AlarmAlreadyActiveCode, result.Code);
        Assert.Empty(scan.Interactions);
    }

    [Fact]
    public void scan_state_start_rejects_a_solved_scan_without_writing()
    {
        var manager = Fixture.Level();
        var scan = Fixture.Puzzle("scan-5", solved: true);
        manager.m_instances!.Add(scan);

        var result = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-5") }, new { operation = "start" });

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal(AlarmWaveActions.AlarmAlreadySolvedCode, result.Code);
        Assert.Empty(scan.Interactions);
    }

    [Fact]
    public void scan_state_start_rejects_an_unknown_scan_and_a_missing_level_manager()
    {
        var manager = Fixture.Level();
        manager.m_instances!.Add(Fixture.Puzzle("scan-6"));

        var unknown = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "nothing-here") }, new { operation = "start" });
        Assert.Equal(CommandStatuses.Rejected, unknown.Status);
        Assert.Equal(AlarmWaveActions.AlarmNotFoundCode, unknown.Code);

        ChainedPuzzles.ChainedPuzzleManager.Current = null;
        var noManager = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-6") }, new { operation = "start" });
        Assert.Equal(AlarmWaveActions.ManagerUnavailableCode, noManager.Code);
    }

    [Fact]
    public void scan_state_start_rejects_an_unreadable_instance_and_a_puzzle_with_no_core()
    {
        var manager = Fixture.Level();
        var destroyed = Fixture.Puzzle("scan-7");
        destroyed.Destroyed = true;
        manager.m_instances!.Add(destroyed);
        var coreless = Fixture.Puzzle("scan-8", cores: 0);
        manager.m_instances.Add(coreless);

        var unavailable = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-7") }, new { operation = "start" });
        Assert.Equal(AlarmWaveActions.AlarmNotFoundCode, unavailable.Code);

        var noCore = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-8") }, new { operation = "start" });
        Assert.Equal(AlarmWaveActions.AlarmNoCoresCode, noCore.Code);
        Assert.Empty(coreless.Interactions);
    }

    [Fact]
    public void scan_state_start_refuses_a_missing_input_a_foreign_kind_and_an_empty_id()
    {
        Fixture.Level();
        var missing = Fixture.Run(AlarmWaveActions.ExecuteScanState, new { }, new { operation = "start" });
        Assert.Equal(AlarmWaveActions.NoScanResourceCode, missing.Code);

        var foreign = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.WaveKind, "1:2") }, new { operation = "start" });
        Assert.Equal(AlarmWaveActions.ResourceNotChainedPuzzleCode, foreign.Code);

        var empty = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "") }, new { operation = "start" });
        Assert.Equal(AlarmWaveActions.ResourceIdEmptyCode, empty.Code);
    }

    [Fact]
    public void scan_state_start_does_not_need_an_attached_half_to_write()
    {
        var manager = Fixture.Level();
        var scan = Fixture.Puzzle("scan-a");
        manager.m_instances!.Add(scan);

        // The scan row publishes no handle, so nothing has to be minted before the write and the write does not
        // depend on the half being attached.
        Fixture.Detach();
        var result = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-a") }, new { operation = "start" });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(AlarmWaveActions.ScanStartedCode, result.Code);
        Assert.Equal(new[] { ChainedPuzzles.eChainedPuzzleInteraction.Activate }, scan.Interactions);
    }

    [Fact]
    public void every_action_refuses_a_non_host_command()
    {
        var manager = Fixture.Level();
        manager.m_instances!.Add(Fixture.Puzzle("scan-9"));
        Fixture.WavePair();

        var startScan = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-9") }, new { operation = "start" }, isHost: false);
        var startWave = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.WaveKind, "7:9") }, isHost: false);
        var stopWave = Fixture.Run(AlarmWaveActions.ExecuteStopWave,
            new { waves = new { } }, new { pending_spawns_policy = "cancel" }, isHost: false);

        foreach (var result in new[] { startScan, startWave, stopWave })
        {
            Assert.Equal(CommandStatuses.Rejected, result.Status);
            Assert.Equal(CommitStates.None, result.CommitState);
            Assert.Equal(AlarmWaveActions.AuthorityCode, result.Code);
            // Every refusal carries the row the plan's `result` port reads, with the same columns a committed
            // row has: a step downstream never sees a different port set because the peer was not the host.
            Assert.Equal(AlarmWaveActions.AuthorityCode, Fixture.Field(result, "code"));
            Assert.Equal("rejected", Fixture.Field(result, "status"));
            Assert.Equal(CommitStates.None, Fixture.Field(result, "committed"));
            Assert.Equal(JsonValueKind.Null, Fixture.Row(result).GetProperty("target").ValueKind);
        }
    }

    [Fact]
    public void scan_state_start_refuses_a_command_dispatched_on_a_non_master_peer()
    {
        var manager = Fixture.Level();
        var scan = Fixture.Puzzle("scan-10");
        manager.m_instances!.Add(scan);
        SNetwork.SNet.IsMaster = false;

        var result = Fixture.Run(AlarmWaveActions.ExecuteScanState,
            new { scan = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "scan-10") }, new { operation = "start" });

        Assert.Equal(AlarmWaveActions.AuthorityCode, result.Code);
        Assert.Empty(scan.Interactions);
    }

    // ---- wave_start (`forge.action.map.wave_start`) ---------------------------------------------------

    [Fact]
    public void wave_start_triggers_one_survival_wave_with_the_two_author_data_block_ids()
    {
        Fixture.Level();
        Fixture.Attach();
        var (settings, population) = Fixture.WavePair(11, 12);
        var master = Mastermind.Current!;

        var result = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.WaveKind, settings + ":" + population) });

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(AlarmWaveActions.WaveStartedCode, result.Code);
        Assert.Single(master.Triggers);
        Assert.Equal(11u, master.Triggers[0].Settings);
        Assert.Equal(12u, master.Triggers[0].Population);
        Assert.Equal(0d, Fixture.Row(result).GetProperty("budget").GetDouble());
        Assert.Equal(new[] { "target", "status", "committed", "code", "budget" },
            Fixture.Row(result).EnumerateObject().Select(p => p.Name).ToArray());
        // The row's own handle port carries the wave it started, which is the value the plan stores and the stop
        // row reads back.
        Assert.NotNull(Fixture.Handle(result, AlarmWaveContract.WaveHandlePort));
    }

    [Fact]
    public void wave_start_reports_the_masterminds_refusal_without_claiming_a_wave()
    {
        Fixture.Level();
        Fixture.WavePair();
        Mastermind.Current!.Accepts = false;

        var result = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.WaveKind, "7:9") });

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(AlarmWaveActions.WaveRejectedCode, result.Code);
        Assert.Empty(Mastermind.Current!.Triggers);
    }

    [Fact]
    public void wave_start_rejects_a_runtime_knob_the_native_entry_cannot_take()
    {
        Fixture.Level();
        Fixture.WavePair();

        foreach (var knob in new[] { "budget", "count", "seed", "interval" })
        {
            var inputs = new Dictionary<string, object?>
            {
                ["wave"] = Fixture.Resource(AlarmWaveContract.WaveKind, "7:9"), [knob] = 4
            };
            var result = Fixture.Run(AlarmWaveActions.ExecuteStartWave, inputs);
            Assert.Equal(CommandStatuses.Rejected, result.Status);
            Assert.Equal(AlarmWaveActions.WaveKnobUnsupportedCode, result.Code);
        }
        Assert.Empty(Mastermind.Current!.Triggers);
    }

    [Fact]
    public void wave_start_rejects_a_wave_the_level_does_not_define()
    {
        Fixture.Level();
        Fixture.WavePair();

        var unknown = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.WaveKind, "999:1000") });
        Assert.Equal(CommandStatuses.Rejected, unknown.Status);
        Assert.Equal(AlarmWaveContract.ResourceUnavailableCode, unknown.Code);
        Assert.Empty(Mastermind.Current!.Triggers);
    }

    [Fact]
    public void wave_start_refuses_a_missing_wave_a_foreign_kind_and_a_half_pair()
    {
        Fixture.Level();
        Fixture.WavePair();

        var missing = Fixture.Run(AlarmWaveActions.ExecuteStartWave, new { });
        Assert.Equal(AlarmWaveActions.NoWaveResourceCode, missing.Code);

        var foreign = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.ChainedPuzzleKind, "7:9") });
        Assert.Equal(AlarmWaveActions.ResourceNotWaveCode, foreign.Code);

        var half = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.WaveKind, "7:9:10") });
        Assert.Equal(AlarmWaveActions.WaveSettingsInvalidCode, half.Code);
    }

    [Fact]
    public void wave_start_refuses_a_level_with_no_mastermind_and_one_with_no_course_node()
    {
        Fixture.Level();
        Fixture.WavePair();
        Mastermind.Current = null;
        var noMaster = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.WaveKind, "7:9") });
        Assert.Equal(AlarmWaveActions.WaveComponentUnavailableCode, noMaster.Code);

        Mastermind.Current = new Mastermind();
        AIGraph.AIG_CourseNode.s_allNodes = new List<AIGraph.AIG_CourseNode>();
        var noNode = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.WaveKind, "7:9") });
        Assert.Equal(AlarmWaveActions.WaveCourseNodeUnavailableCode, noNode.Code);
        Assert.Empty(Mastermind.Current!.Triggers);
    }

    // ---- the stop row ---------------------------------------------------------------------------------

    [Fact]
    public void a_started_wave_hands_back_the_handle_that_stops_it()
    {
        Fixture.Level();
        Fixture.Attach();
        var (settings, population) = Fixture.WavePair(11, 12);

        var start = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.WaveKind, settings + ":" + population) });

        Assert.Equal(CommandStatuses.Succeeded, start.Status);
        var handle = Fixture.Handle(start, AlarmWaveContract.WaveHandlePort);
        Assert.NotNull(handle);
        var master = Mastermind.Current!;
        var registered = Assert.Single(master.Events).Value;

        // The handle the start row published is the value the plan stores under 警报A and hands back to the stop
        // row: the stop resolves it to the very event that start registered.
        var stop = Fixture.Run(AlarmWaveActions.ExecuteStopWave, new { waves = handle!.Value },
            new { pending_spawns_policy = "cancel" });

        Assert.Equal(CommandStatuses.Succeeded, stop.Status);
        Assert.Equal(AlarmWaveActions.WaveStoppedCode, stop.Code);
        Assert.Equal(1, registered.Stops);
    }

    [Fact]
    public void a_wave_the_world_no_longer_holds_is_refused_by_the_handle_it_left_behind()
    {
        Fixture.Level();
        Fixture.Attach();
        var (settings, population) = Fixture.WavePair(11, 12);
        var start = Fixture.Run(AlarmWaveActions.ExecuteStartWave,
            new { wave = Fixture.Resource(AlarmWaveContract.WaveKind, settings + ":" + population) });
        var handle = Fixture.Handle(start, AlarmWaveContract.WaveHandlePort)!.Value;

        // The world moves on: every handle of the ended world names nothing, and the stop says so rather than
        // stopping a wave of the new one.
        Fixture.Kernel.BeginWorld(2);

        var stop = Fixture.Run(AlarmWaveActions.ExecuteStopWave, new { waves = handle },
            new { pending_spawns_policy = "cancel" });
        Assert.Equal(CommandStatuses.Rejected, stop.Status);
        Assert.Equal(AlarmWaveActions.HandleNotLiveCode, stop.Code);
        Assert.Empty(Mastermind.Current!.Events.Values.Where(e => e.Stops != 0));
    }

    [Fact]
    public void wave_stop_refuses_the_pending_spawn_policy_no_native_stop_entry_carries()
    {
        Fixture.Level();
        var finish = Fixture.Run(AlarmWaveActions.ExecuteStopWave,
            new { waves = new { worldEpoch = 1, lifeEpoch = 1, local = 0, provider = 0 } },
            new { pending_spawns_policy = "finish" });
        Assert.Equal(CommandStatuses.Rejected, finish.Status);
        Assert.Equal(AlarmWaveActions.WavePendingFinishUnsupportedCode, finish.Code);

        var unknown = Fixture.Run(AlarmWaveActions.ExecuteStopWave, new { waves = new { } },
            new { pending_spawns_policy = "bogus" });
        Assert.Equal(AlarmWaveActions.PolicyUnknownCode, unknown.Code);
    }

    [Fact]
    public void wave_stop_reports_a_handle_that_resolves_to_nothing_rather_than_stopping_a_wave_by_name()
    {
        Fixture.Level();
        var withoutHandle = Fixture.Run(AlarmWaveActions.ExecuteStopWave, new { },
            new { pending_spawns_policy = "cancel" });
        Assert.Equal(AlarmWaveActions.WaveHandleMissingCode, withoutHandle.Code);

        var withHandle = Fixture.Run(AlarmWaveActions.ExecuteStopWave,
            new { waves = new { worldEpoch = 1, lifeEpoch = 1, local = 0, provider = 0 } },
            new { pending_spawns_policy = "cancel" });
        Assert.Equal(CommandStatuses.Rejected, withHandle.Status);
        Assert.Equal(AlarmWaveActions.HandleNotLiveCode, withHandle.Code);
        // The refusal is not a name-based stop: no native event was touched.
        Assert.Empty(Mastermind.Current!.Triggers);
    }

    // ---- the declaration and the resource providers ---------------------------------------------------

    [Fact]
    public void the_declaration_carries_one_capability_and_one_binding_per_row_with_its_own_permission()
    {
        var capabilities = JsonDocument.Parse(AlarmWaveContract.CapabilitiesJson).RootElement;
        Assert.Equal(3, capabilities.GetArrayLength());
        Assert.Equal(
            new[] { "forge.action.map.scan_state", "forge.action.map.wave_start", "forge.action.map.wave_stop" },
            capabilities.EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray());
        foreach (var capability in capabilities.EnumerateArray())
        {
            Assert.Equal("forge.module.gtfo.map", capability.GetProperty("owner").GetString());
            Assert.Equal("action", capability.GetProperty("kind").GetString());
            Assert.Equal("host", capability.GetProperty("graph").GetProperty("execution").GetString());
        }

        var bindings = AlarmWaveContract.Bindings();
        var support = AlarmWaveContract.Support();
        Assert.Equal(capabilities.GetArrayLength(), bindings.Length);
        Assert.Equal(bindings.Length, support.Length);
        for (int i = 0; i < bindings.Length; i++)
        {
            var binding = JsonDocument.Parse(RuntimeJson.From(bindings[i]).GetRawText()).RootElement;
            Assert.Equal("execute", binding.GetProperty("role").GetString());
            Assert.Equal("implemented", binding.GetProperty("status").GetString());
            Assert.Equal(AlarmWaveContract.Binding(binding.GetProperty("capabilityId").GetString()!), binding.GetProperty("id").GetString());
            Assert.Equal(binding.GetProperty("id").GetString(), support[i].BindingId);
            // `requires` names other bindings a row's closure needs and is empty here; the permission the row's
            // recipients declare is the support row's own list.
            Assert.Empty(binding.GetProperty("requires").EnumerateArray());
            Assert.Single(support[i].RequiredPermissions);
        }

        Assert.Equal(AlarmWaveContract.ScanControlPermission, support[0].RequiredPermissions[0]);
        Assert.Equal(AlarmWaveContract.WaveControlPermission, support[1].RequiredPermissions[0]);
        Assert.Equal(AlarmWaveContract.WaveControlPermission, support[2].RequiredPermissions[0]);
    }

    [Fact]
    public void every_shape_is_the_capabilitys_own_port_set()
    {
        Assert.Equal(new[] { "scan" }, AlarmWaveContract.ScanStateShape.InputPorts);
        Assert.Equal(new[] { "result" }, AlarmWaveContract.ScanStateShape.OutputPorts);
        Assert.Equal(new[] { "operation" }, AlarmWaveContract.ScanStateShape.ParameterIds);
        Assert.Equal(new[] { "wave", "budget", "count", "seed", "interval" }, AlarmWaveContract.WaveStartShape.InputPorts);
        Assert.Equal(new[] { "result", "wave_handle" }, AlarmWaveContract.WaveStartShape.OutputPorts);
        Assert.Equal(new[] { "waves", "reason" }, AlarmWaveContract.WaveStopShape.InputPorts);
        Assert.Equal(new[] { "pending_spawns_policy" }, AlarmWaveContract.WaveStopShape.ParameterIds);
        Assert.Equal(3, AlarmWaveContract.Shapes().Count);
    }

    [Fact]
    public void the_two_resource_kinds_are_owned_by_native_tables_and_answer_a_level_with_nothing_in_it()
    {
        Fixture.Reset();
        var providers = AlarmWaveContract.ResourceProviders(
            AlarmWaveActions.EnumerateChainedPuzzles, AlarmWaveActions.ResolveChainedPuzzle,
            AlarmWaveActions.EnumerateWaves, AlarmWaveActions.ResolveWave);

        Assert.Equal(new[] { AlarmWaveContract.ChainedPuzzleKind, AlarmWaveContract.WaveKind }, providers.Keys.ToArray());
        Assert.Empty(providers[AlarmWaveContract.ChainedPuzzleKind].Enumerate());
        Assert.Null(providers[AlarmWaveContract.ChainedPuzzleKind].Resolve("scan-1"));
        Assert.Empty(providers[AlarmWaveContract.WaveKind].Enumerate());
        Assert.Null(providers[AlarmWaveContract.WaveKind].Resolve("7:9"));
    }

    [Fact]
    public void the_resource_providers_list_the_levels_own_puzzles_and_data_blocks()
    {
        var manager = Fixture.Level();
        manager.m_instances!.Add(Fixture.Puzzle("scan-7"));
        Fixture.WavePair(21, 22);

        var puzzles = AlarmWaveActions.EnumerateChainedPuzzles();
        Assert.Equal("scan-7", Assert.Single(puzzles).ResourceId);
        Assert.Equal(AlarmWaveContract.ChainedPuzzleKind, puzzles[0].ResourceKind);
        Assert.Equal("scan-7", AlarmWaveActions.ResolveChainedPuzzle("scan-7")!.ResourceId);
        Assert.Null(AlarmWaveActions.ResolveChainedPuzzle("missing"));

        var waves = AlarmWaveActions.EnumerateWaves();
        Assert.Equal(2, waves.Count);
        Assert.All(waves, wave => Assert.Equal(AlarmWaveContract.WaveKind, wave.ResourceKind));
        Assert.Equal("21:0", waves[0].ResourceId);
        Assert.Equal("0:22", waves[1].ResourceId);
        Assert.Equal("21:22", AlarmWaveActions.ResolveWave("21:22")!.ResourceId);
        Assert.Null(AlarmWaveActions.ResolveWave("21:999"));
        Assert.Null(AlarmWaveActions.ResolveWave("21:"));
        Assert.Null(AlarmWaveActions.ResolveWave(""));
    }
}
