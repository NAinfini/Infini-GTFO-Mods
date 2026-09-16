using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

/// <summary>
/// The focused suite for the level-object rows this slice owns: the two scan facts, the progress row, the
/// container row and the level item row. The generators are a map-object category of their own (ruling 148.4),
/// so their facts, their address and their value row are asserted in `MapObjectObservation`.
///
/// Every case exercises the production publication half through the registration the runtime really resolves,
/// and asserts the three things a row is allowed to claim: that it was published (the event reached the
/// kernel), which ports it carried, and what it does when the world cannot answer — an absent port, a refusal
/// by name, or nothing at all. Identity, epoch and world cleanup are asserted beside them, because a fact that
/// survives its world is worse than a fact that was never published.
/// </summary>
public sealed class LevelObjectFactsTests
{
    // ---- contract rows ------------------------------------------------------------------------------

    [Fact]
    public void every_row_the_module_publishes_has_its_own_capability_and_binding()
    {
        var bindings = LevelObjectContract.Bindings;
        Assert.Equal(bindings.Length, bindings.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(6, bindings.Length);
        Assert.Equal(6, LevelObjectContract.BindingRows().Length);
        Assert.Equal(LevelObjectContract.BindingRows().Length, LevelObjectContract.Support().Count);
        // Every binding is the capability's own suffix under the Map provider, which is what lets either side
        // of a row name the counterpart of the other without a second table.
        foreach (var binding in bindings)
            Assert.StartsWith(LevelObjectContract.ProviderId + ".binding.", binding, StringComparison.Ordinal);
    }

    [Fact]
    public void the_capability_array_is_the_six_rows_the_bindings_name()
    {
        using var document = JsonDocument.Parse(LevelObjectContract.CapabilitiesJson);
        var ids = document.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[]
        {
            LevelObjectContract.ScanStartedCapability, LevelObjectContract.ScanProgressCapability,
            LevelObjectContract.ScanCompletedCapability, LevelObjectContract.ContainerStateCapability,
            LevelObjectContract.ItemPickupCapability, LevelObjectContract.ScanStateCapability
        }, ids);
    }

    [Fact]
    public void the_registration_accepts_the_contract_as_declared()
    {
        using var world = LevelObjectWorld.Start();
        Assert.True(world.Registration.IsRegistered);
        Assert.True(world.Module.IsRegistered);
    }

    // ---- scan ---------------------------------------------------------------------------------------

    [Fact]
    public void scan_progress_publishes_the_fraction_the_native_callback_computed()
    {
        using var world = LevelObjectWorld.Start();
        var player = LevelObjectWorld.Player();

        world.ObserveScanProgress("scan-a", 0.5, new[] { player });

        Assert.Equal(1, world.Facts.Count);
        var fact = world.Last;
        Assert.Equal(LevelObjectContract.ScanProgressBinding, fact.BindingId);
        Assert.Equal(LevelObjectModule.EntityKind + ":" + LevelObjectContract.ScanCategory + "/scan-a",
            ReadEntity(fact.Outputs.GetProperty("scan")).Id);
        Assert.Equal(0.5, fact.Outputs.GetProperty("progress").GetDouble());
        Assert.Equal(1, fact.Outputs.GetProperty("count").GetInt32());
        Assert.Equal(player, ReadEntity(fact.Outputs.GetProperty("participants")[0]));
        Assert.Equal("gtfo.world:1", fact.ScopeId);
    }

    [Fact]
    public void scan_progress_at_or_below_zero_is_remembered_but_never_published()
    {
        using var world = LevelObjectWorld.Start();

        world.ObserveScanProgress("scan-a", 0, Array.Empty<EntityReference>());

        Assert.Equal(0, world.Facts.Count);
        Assert.Equal(0, world.ScanProgress("scan-a"));
    }

    [Fact]
    public void a_repeated_scan_progress_reading_has_no_second_transition()
    {
        using var world = LevelObjectWorld.Start();

        world.ObserveScanProgress("scan-a", 0.25, Array.Empty<EntityReference>());
        world.ObserveScanProgress("scan-a", 0.25, Array.Empty<EntityReference>());
        world.ObserveScanProgress("scan-a", 0.75, Array.Empty<EntityReference>());

        Assert.Equal(2, world.Facts.Count);
    }

    [Fact]
    public void a_scan_with_no_uid_publishes_nothing()
    {
        using var world = LevelObjectWorld.Start();

        world.ObserveScanState("", active: true, solved: false);
        world.ObserveScanProgress("", 0.5, Array.Empty<EntityReference>());

        Assert.Equal(0, world.Facts.Count);
    }

    [Fact]
    public void scan_started_and_completed_are_the_two_readings_of_one_instance()
    {
        using var world = LevelObjectWorld.Start();

        world.ObserveScanState("scan-a", active: true, solved: false);
        world.ObserveScanState("scan-a", active: true, solved: false);
        world.ObserveScanState("scan-a", active: true, solved: true);
        world.ObserveScanState("scan-a", active: false, solved: false);

        Assert.Equal(2, world.Facts.Count);
        Assert.Equal(LevelObjectContract.ScanCompletedBinding, world.Last.BindingId);
        Assert.Empty(world.Reports);
    }

    // ---- container and item -------------------------------------------------------------------------

    [Fact]
    public void a_container_state_change_publishes_the_game_status_name()
    {
        using var world = LevelObjectWorld.Start();

        world.Module.ContainerStateChanged("container/0/0/1/3", "open");

        Assert.Equal(1, world.Facts.Count);
        Assert.Equal(LevelObjectContract.ContainerStateBinding, world.Last.BindingId);
        Assert.Equal("open", world.Last.Outputs.GetProperty("state").GetString());
    }

    [Fact]
    public void a_container_status_the_row_does_not_declare_is_not_published()
    {
        using var world = LevelObjectWorld.Start();

        world.Module.ContainerStateChanged("container/0/0/1/3", "exploded");

        Assert.Equal(0, world.Facts.Count);
    }

    [Fact]
    public void a_level_item_reports_the_direction_and_the_actor()
    {
        using var world = LevelObjectWorld.Start();
        var actor = LevelObjectWorld.Player();

        world.Module.ItemStateChanged("item/0/0/1/12", pickedUp: true, actor);

        Assert.Equal(1, world.Facts.Count);
        var outputs = world.Last.Outputs;
        Assert.Equal(LevelObjectContract.ItemPickupBinding, world.Last.BindingId);
        Assert.True(outputs.GetProperty("picked_up").GetBoolean());
        Assert.Equal(actor, ReadEntity(outputs.GetProperty("actor")));
    }

    [Fact]
    public void an_item_put_back_down_is_a_second_fact_of_the_same_item()
    {
        using var world = LevelObjectWorld.Start();

        world.Module.ItemStateChanged("item/0/0/1/12", pickedUp: true, null);
        world.Module.ItemStateChanged("item/0/0/1/12", pickedUp: false, null);

        Assert.Equal(2, world.Facts.Count);
        Assert.False(world.Last.Outputs.GetProperty("picked_up").GetBoolean());
    }

    [Fact]
    public void an_absent_actor_leaves_the_port_out_instead_of_publishing_null()
    {
        using var world = LevelObjectWorld.Start();

        world.Module.ItemStateChanged("item/0/0/1/12", pickedUp: true, null);

        Assert.False(world.Last.Outputs.TryGetProperty("actor", out _));
    }

    // ---- resources and value rows -------------------------------------------------------------------

    [Fact]
    public void a_scan_the_value_row_cannot_read_is_refused_by_name()
    {
        using var world = LevelObjectWorld.Start();
        world.Module.ScanStateReader = null;

        var error = Assert.Throws<RuntimeContractException>(() => world.ScanState("scan-a"));

        Assert.Equal("scan-unavailable", error.Code);
    }

    [Fact]
    public void a_scan_state_the_game_has_no_status_for_is_refused_by_name()
    {
        using var world = LevelObjectWorld.Start();

        // `timed-out` is the catalog member no native chained-puzzle status carries, so the row refuses it
        // instead of comparing it and answering `false`.
        var error = Assert.Throws<RuntimeContractException>(() => world.ScanState("scan-a", "timed-out"));

        Assert.Equal("scan-state-unknown", error.Code);
    }

    [Fact]
    public void the_scan_value_row_answers_the_state_and_the_last_published_progress()
    {
        using var world = LevelObjectWorld.Start();
        world.Module.ScanStateReader = (uid, progress) => RuntimeJson.From(new { state = "active", progress });

        world.ObserveScanProgress("scan-a", 0.5, Array.Empty<EntityReference>());
        var state = world.ScanState("scan-a", "active");

        Assert.True(state.GetProperty("value").GetBoolean());
        Assert.Equal("active", state.GetProperty("state").GetString());
        Assert.Equal(0.5, state.GetProperty("progress").GetDouble());
    }

    [Fact]
    public void a_reading_of_another_state_is_not_the_state_the_row_asked_about()
    {
        using var world = LevelObjectWorld.Start();
        world.Module.ScanStateReader = (uid, progress) => RuntimeJson.From(new { state = "solved", progress });

        var state = world.ScanState("scan-a", "active");

        Assert.False(state.GetProperty("value").GetBoolean());
        Assert.Equal("solved", state.GetProperty("state").GetString());
    }

    // ---- lifecycle ----------------------------------------------------------------------------------

    [Fact]
    public void a_repeated_reading_of_one_state_is_not_a_second_fact()
    {
        using var world = LevelObjectWorld.Start();

        world.Module.ContainerStateChanged("container/0/0/1/3", "open");
        world.Module.ContainerStateChanged("container/0/0/1/3", "open");

        // The state key is the reading itself, so the same container reporting the same status again has no new
        // transition and the module hands the kernel nothing.
        Assert.Equal(1, world.Facts.Count);
    }

    [Fact]
    public void a_fact_published_in_a_new_world_carries_the_new_epoch()
    {
        using var world = LevelObjectWorld.Start();
        world.Module.ContainerStateChanged("container/0/0/1/3", "open");
        world.NextWorld();

        world.Module.ContainerStateChanged("container/0/0/1/3", "open");

        Assert.Equal(world.Kernel.WorldEpoch, world.Last.WorldEpoch);
        Assert.Equal("gtfo.world:" + world.Kernel.WorldEpoch, world.Last.ScopeId);
    }

    [Fact]
    public void a_disposed_module_publishes_nothing_further()
    {
        var world = LevelObjectWorld.Start();
        world.Dispose();

        Assert.Equal(0, world.Facts.Count);
    }

    [Fact]
    public void every_fact_id_is_the_namespace_plus_the_fact_plus_the_world_the_subject_and_the_transition()
    {
        using var world = LevelObjectWorld.Start();
        world.Module.ContainerStateChanged("container/0/0/1/3", "open");
        world.Module.ContainerStateChanged("container/0/0/1/3", "closed");

        var identifiers = world.Facts.Select(fact => fact.EventId.Split(':')[0]).Distinct().ToArray();
        Assert.Equal(new[] { LevelObjectModule.EntityKind + ".container.state" }, identifiers);
        Assert.NotEqual(world.Facts[0].EventId, world.Facts[1].EventId);
    }

    private static EntityReference ReadEntity(JsonElement value)
        => new(value.GetProperty("id").GetString()!, value.GetProperty("worldEpoch").GetInt64(),
            value.GetProperty("lifeEpoch").GetInt64());
}
