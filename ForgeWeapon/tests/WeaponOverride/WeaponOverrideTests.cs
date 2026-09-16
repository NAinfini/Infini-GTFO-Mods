using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.WeaponOverride;

/// <summary>
/// The instance-override ledger and the field vocabulary it is the gate for. Every case here is about one of
/// ruling 84's own requirements: an override is a per-instance decision, a request that cannot be carried out
/// changes nothing, a second writer is ordered by `(plan, node, sequence)` and by nothing else, and every way a
/// life can end puts the instance's own block back.
///
/// Nothing in this file writes a game type: the ledger's only outside contact is <see cref="IWeaponOverrideSink"/>,
/// which the fixture implements as a recorder, so what a case asserts is the decision and not the write.
/// </summary>
[Trait("Category", "WeaponOverride")]
public sealed class WeaponOverrideLedgerTests
{
    [Fact]
    public void the_field_vocabulary_is_the_write_points_this_build_has()
    {
        // Spelled out rather than read from the same list that is being checked: a name silently added or removed
        // by an edit has to fail here, because the native half writes exactly these and nothing else.
        Assert.Equal(new[]
        {
            "fire_rate", "burst_count", "spread_cone", "spread_movement_scale", "spread_aim_scale",
            "recoil_horizontal", "recoil_vertical", "recoil_recovery", "recoil_camera_kick"
        }, WeaponOverrideLedger.Fields);
        Assert.All(WeaponOverrideLedger.Fields, name => Assert.True(WeaponOverrideLedger.IsKnownField(name), name));
        Assert.False(WeaponOverrideLedger.IsKnownField("spread_pattern"));
        Assert.False(WeaponOverrideLedger.IsKnownField(null));
    }

    [Fact]
    public void a_request_that_names_one_unknown_field_writes_nothing_at_all()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        var request = OverrideWorld.Request(equipment, OverrideWorld.Source(), 0,
            ("fire_rate", 0.05), ("not_a_field", 1));

        Assert.False(world.Ledger.Begin(request, 10, out var code));
        Assert.Equal(WeaponOverrideLedger.UnknownFieldCode, code);
        Assert.Empty(world.WriteBack.Applications);
        Assert.Equal(0, world.Ledger.Count);
    }

    [Fact]
    public void an_empty_or_oversized_field_list_is_refused_as_a_budget()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();

        Assert.False(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 0), 0, out var empty));
        Assert.Equal(WeaponOverrideLedger.StaleEquipmentCode, empty);

        var many = Enumerable.Range(0, WeaponOverrideLedger.MaximumOverrideFieldsPerRequest + 1)
            .Select(index => ("fire_rate", (double)index)).ToArray();
        Assert.False(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 0, many), 0, out var oversized));
        Assert.Equal(WeaponOverrideLedger.BudgetCode, oversized);
        Assert.Empty(world.WriteBack.Applications);
    }

    [Fact]
    public void the_world_budget_is_counted_in_instances()
    {
        var world = new OverrideWorld();
        for (var index = 0; index < WeaponOverrideLedger.MaximumOverriddenEquipmentPerWorld; index++)
            Assert.True(world.Ledger.Begin(
                OverrideWorld.Request(OverrideWorld.Equipment("gtfo.equipment:" + index), OverrideWorld.Source(), 0,
                    ("fire_rate", 0.1)), 0, out _));

        Assert.False(world.Ledger.Begin(
            OverrideWorld.Request(OverrideWorld.Equipment("gtfo.equipment:overflow"), OverrideWorld.Source(), 0,
                ("fire_rate", 0.1)), 0, out var code));
        Assert.Equal(WeaponOverrideLedger.BudgetCode, code);
        Assert.Equal(WeaponOverrideLedger.MaximumOverriddenEquipmentPerWorld, world.Ledger.Count);
    }

    [Fact]
    public void a_later_writer_wins_a_field_and_leaves_the_others_alone()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        var first = OverrideWorld.Source("a", "n1", 0);
        var later = OverrideWorld.Source("a", "n2", 0);

        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, first, 0,
            ("fire_rate", 0.1), ("recoil_horizontal", 1)), 0, out _));
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, later, 0, ("fire_rate", 0.2)), 0, out _));

        Assert.Equal(0.2, world.Written(equipment, 1, "fire_rate"));
        // The second write is the merged absolute set, not the delta: the recoil value the first writer set is
        // still there, because a merge that dropped it would silently undo an accepted request.
        Assert.Equal(1, world.Written(equipment, 1, "recoil_horizontal"));
        Assert.Equal(1, world.Ledger.Count);
    }

    [Fact]
    public void an_earlier_writer_cannot_take_a_field_back_from_a_later_one()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        var later = OverrideWorld.Source("a", "n2", 0);
        var earlier = OverrideWorld.Source("a", "n1", 0);

        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, later, 0, ("fire_rate", 0.2)), 0, out _));
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, earlier, 0, ("fire_rate", 0.9)), 0, out _));

        Assert.Equal(0.2, world.Written(equipment, 1, "fire_rate"));
    }

    [Fact]
    public void the_sequence_inside_one_node_orders_its_own_writes()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();

        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source("a", "n", 7), 0,
            ("fire_rate", 0.2)), 0, out _));
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source("a", "n", 4), 0,
            ("fire_rate", 0.9)), 0, out _));

        Assert.Equal(0.2, world.Written(equipment, 1, "fire_rate"));
    }

    [Fact]
    public void a_refused_write_is_not_remembered()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        world.WriteBack.RefuseApply = true;
        world.WriteBack.RefusalCode = WeaponOverrideLedger.BlockMissingCode;

        Assert.False(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 0,
            ("fire_rate", 0.1)), 0, out var code));
        Assert.Equal(WeaponOverrideLedger.BlockMissingCode, code);
        Assert.Equal(0, world.Ledger.Count);
        Assert.False(world.Ledger.IsOverridden(equipment));
        Assert.Empty(world.Ledger.Pending(equipment));
    }

    [Fact]
    public void pending_is_what_a_gear_spawn_replays()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 0,
            ("fire_rate", 0.1), ("spread_aim_scale", 2)), 0, out _));

        var pending = world.Ledger.Pending(equipment);
        Assert.Equal(new[] { "fire_rate", "spread_aim_scale" }, pending.Select(field => field.Name));
        Assert.Equal(0.1, pending.Single(field => field.Name == "fire_rate").Value);
        // An instance nobody overrode replays nothing rather than a default set.
        Assert.Empty(world.Ledger.Pending(OverrideWorld.Equipment("gtfo.equipment:other")));
    }

    [Fact]
    public void a_duration_is_measured_from_the_tick_it_was_accepted_at()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 30,
            ("fire_rate", 0.1)), 100, out _));

        world.Ledger.Sweep(129);
        Assert.True(world.Ledger.IsOverridden(equipment));
        Assert.Empty(world.WriteBack.Restored);

        world.Ledger.Sweep(130);
        Assert.False(world.Ledger.IsOverridden(equipment));
        Assert.Equal(new[] { equipment.Id }, world.WriteBack.Restored);
    }

    [Fact]
    public void reverting_an_instance_that_carries_nothing_is_a_success()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        Assert.True(world.Ledger.Revert(equipment, out var code));
        Assert.Equal("", code);

        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 0,
            ("fire_rate", 0.1)), 0, out _));
        Assert.True(world.Ledger.Revert(equipment, out _));
        Assert.False(world.Ledger.IsOverridden(equipment));
        Assert.Single(world.WriteBack.Restored);
    }

    [Fact]
    public void a_world_boundary_reverts_everything_and_keeps_nothing()
    {
        var world = new OverrideWorld();
        var first = OverrideWorld.Equipment("gtfo.equipment:1");
        var second = OverrideWorld.Equipment("gtfo.equipment:2");
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(first, OverrideWorld.Source(), 0, ("fire_rate", 0.1)), 0, out _));
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(second, OverrideWorld.Source(), 0, ("recoil_vertical", 3)), 0, out _));

        Assert.True(world.Ledger.BeginWorld(OverrideWorld.WorldEpoch + 1));
        Assert.Equal(0, world.Ledger.Count);
        Assert.Empty(world.Ledger.Pending(first));
        Assert.False(world.Ledger.IsOverridden(second));
        Assert.Contains(first.Id, world.WriteBack.Restored);
        Assert.Contains(second.Id, world.WriteBack.Restored);

        // The same epoch twice is not a new world: nothing is given up for it, so the native half is not asked to
        // restore anything. The fixture opened this case's first world, which is the one restore call behind the
        // boundary's own.
        Assert.False(world.Ledger.BeginWorld(OverrideWorld.WorldEpoch + 1));
        Assert.Equal(2, world.WriteBack.RestoreAllCalls);
        Assert.Equal(2, world.WriteBack.Restored.Count);
    }

    [Fact]
    public void a_world_that_is_the_same_one_gives_nothing_up()
    {
        // `BeginWorld` with the epoch already in force is the shape a repeated world notification has, and it
        // must not revert: the instances it would restore are the ones the running world is using.
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 0,
            ("fire_rate", 0.1)), 0, out _));

        Assert.False(world.Ledger.BeginWorld(OverrideWorld.WorldEpoch));
        Assert.True(world.Ledger.IsOverridden(equipment));
        Assert.Empty(world.WriteBack.Restored);
    }

    [Fact]
    public void ending_a_world_reverts_even_when_no_new_epoch_follows()
    {
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 0,
            ("fire_rate", 0.1)), 0, out _));

        world.Ledger.EndWorld();
        Assert.Equal(0, world.Ledger.Count);
        Assert.Contains(equipment.Id, world.WriteBack.Restored);
    }

    [Fact]
    public void a_checkpoint_reload_gives_every_entry_up_without_ending_the_world()
    {
        // A checkpoint reload rebuilds the level's gear from the game's own data: the instances a plan overrode are
        // gone, so the ledger gives every entry up and the native half restores what it still holds — and unlike a
        // world boundary it keeps the epoch it is in, because the same expedition continues (ruling 133.4).
        var world = new OverrideWorld();
        var equipment = OverrideWorld.Equipment();
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 0,
            ("fire_rate", 0.1)), 0, out _));

        world.Ledger.RestoreAll();

        Assert.Equal(0, world.Ledger.Count);
        Assert.False(world.Ledger.IsOverridden(equipment));
        Assert.Empty(world.Ledger.Pending(equipment));
        Assert.Contains(equipment.Id, world.WriteBack.Restored);
        // The world the ledger is in has not moved: a request about an instance the rebuilt level offers again is
        // accepted, which is what a reload that kept the epoch means.
        Assert.False(world.Ledger.BeginWorld(OverrideWorld.WorldEpoch));
        Assert.True(world.Ledger.Begin(OverrideWorld.Request(equipment, OverrideWorld.Source(), 0,
            ("recoil_vertical", 2)), 0, out _));
    }
}
