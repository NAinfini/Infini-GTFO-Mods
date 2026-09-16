using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.ReloadInventoryFacts;

/// <summary>The six equipment rows over doubles. Each case drives the production observer directly against a slot
/// table and a pool the case fills, so what is under test is which row a readback produces and which ports it fills
/// — not the game's player path, which no case here runs.</summary>
[Trait("Category", "ReloadInventoryFacts")]
public sealed class InventoryFactsTests
{
    private const string Refilled = ReloadInventoryContract.RefilledBinding;
    private const string Stack = ReloadInventoryContract.StackChangedBinding;
    private const string PickedUp = ReloadInventoryContract.PickedUpBinding;
    private const string Dropped = ReloadInventoryContract.DroppedBinding;
    private const string UseStarted = ReloadInventoryContract.UseStartedBinding;
    private const string UseFailed = ReloadInventoryContract.UseFailedBinding;

    private const string Standard = "GearStandard";

    /// <summary>A world with one player, one recorded equipment life and an empty pool per pooled slot.</summary>
    private static (FactsWorld World, FakePlayer Owner, EntityReference OwnerReference, FakeItem Item,
        FakeBackpack Backpack) Armed()
    {
        var world = new FactsWorld();
        world.Start();
        var (owner, ownerReference) = world.Player();
        var item = new FakeItem { Clip = 30 };
        world.Life(item, owner);
        var backpack = world.Backpack(owner, FactsWorld.PooledSlots);
        return (world, owner, ownerReference, item, backpack);
    }

    /// <summary>The first readback of a backpack records it and publishes nothing: a table that was never seen did
    /// not change, and a fact for it would report a pickup nobody made.</summary>
    [Fact]
    public void the_first_readback_of_a_backpack_publishes_nothing()
    {
        var (world, _, _, _, backpack) = Armed();
        using var _ = world;
        backpack.Empty(Standard);

        world.Inventory.Reconcile(backpack);

        Assert.Empty(world.Published);
        Assert.Equal(1, world.Inventory.TrackedPlayers);
    }

    [Fact]
    public void an_occupied_slot_becomes_a_pickup_and_a_count_of_one()
    {
        var (world, _, ownerReference, item, backpack) = Armed();
        using var _ = world;
        backpack.Empty(Standard);
        world.Inventory.Reconcile(backpack);

        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);

        var pickup = Assert.Single(world.Facts(PickedUp));
        Assert.Equal(ownerReference.Id, FactsWorld.Reference(pickup, "actor"));
        Assert.Equal(world.ByItem[item.Id].Id, FactsWorld.Reference(pickup, "item"));
        var stack = Assert.Single(world.Facts(Stack));
        Assert.Equal(1, FactsWorld.Number(stack, "count"));
        Assert.Equal(1, FactsWorld.Number(stack, "delta"));
    }

    [Fact]
    public void an_emptied_slot_becomes_a_drop_and_a_count_of_zero()
    {
        var (world, _, _, item, backpack) = Armed();
        using var _ = world;
        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);
        var equipment = world.ByItem[item.Id];

        backpack.Clear(Standard);
        world.Inventory.Reconcile(backpack);

        var drop = Assert.Single(world.Facts(Dropped));
        Assert.Equal(equipment.Id, FactsWorld.Reference(drop, "item"));
        Assert.False(FactsWorld.Has(drop, "position"));
        var stack = Assert.Single(world.Facts(Stack));
        Assert.Equal(0, FactsWorld.Number(stack, "count"));
        Assert.Equal(-1, FactsWorld.Number(stack, "delta"));
    }

    /// <summary>A drop the machine could still read a position for publishes it. The position is the instance's,
    /// read while that instance was still there; a slot clear usually destroys it first, which is why the port is
    /// optional.</summary>
    [Fact]
    public void a_drop_publishes_the_position_the_instance_still_had()
    {
        var (world, _, _, item, backpack) = Armed();
        using var _ = world;
        item.WorldPosition = new[] { 1d, 2d, 3d };
        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);

        backpack.Clear(Standard);
        world.Inventory.Reconcile(backpack);

        var drop = Assert.Single(world.Facts(Dropped));
        var position = FactsWorld.Outputs(drop).GetProperty("position");
        Assert.Equal(1d, position[0].GetDouble());
        Assert.Equal(3d, position[2].GetDouble());
    }

    [Fact]
    public void a_pickup_with_no_live_equipment_identity_is_reported_and_publishes_nothing()
    {
        var (world, _, _, item, backpack) = Armed();
        using var _ = world;
        backpack.Empty(Standard);
        world.Inventory.Reconcile(backpack);
        world.Retire(world.ByItem[item.Id]);

        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);

        Assert.Empty(world.Facts(PickedUp));
        Assert.Empty(world.Facts(Stack));
        Assert.Contains(world.Reports, line => line.Contains("pickup-unresolved", StringComparison.Ordinal));
    }

    /// <summary>A slot whose item was replaced under the same occupancy is neither a pickup nor a drop: the count
    /// did not move, so only the count row is published, with a zero delta.</summary>
    [Fact]
    public void a_replaced_instance_under_the_same_occupancy_is_only_a_count_fact()
    {
        var (world, owner, _, item, backpack) = Armed();
        using var _ = world;
        backpack.Hold(Standard, FactsWorld.Item(item, instance: (IntPtr)9001));
        world.Inventory.Reconcile(backpack);
        var replacement = new FakeItem { Clip = 5 };
        world.Life(replacement, owner);

        backpack.Hold(Standard, FactsWorld.Item(replacement, instance: (IntPtr)9002));
        world.Inventory.Reconcile(backpack);

        Assert.Empty(world.Facts(PickedUp));
        Assert.Empty(world.Facts(Dropped));
        var stack = Assert.Single(world.Facts(Stack));
        Assert.Equal(1, FactsWorld.Number(stack, "count"));
        Assert.Equal(0, FactsWorld.Number(stack, "delta"));
    }

    [Fact]
    public void an_empty_slot_that_stays_empty_publishes_nothing_on_a_later_readback()
    {
        var (world, _, _, _, backpack) = Armed();
        using var _ = world;
        backpack.Empty(Standard);
        world.Inventory.Reconcile(backpack);

        world.Inventory.Reconcile(backpack);

        Assert.Empty(world.Published);
    }

    [Fact]
    public void a_pool_that_grows_is_a_refill_naming_the_equipment_it_filled()
    {
        var (world, _, ownerReference, item, backpack) = Armed();
        using var _ = world;
        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);

        backpack.Pool[Standard] = 40;
        world.Inventory.Reconcile(backpack);

        var refill = Assert.Single(world.Facts(Refilled));
        Assert.Equal(ownerReference.Id, FactsWorld.Reference(refill, "actor"));
        Assert.Equal(world.ByItem[item.Id].Id, FactsWorld.Reference(refill, "equipment"));
        Assert.Equal(40, FactsWorld.Number(refill, "amount"));
    }

    [Fact]
    public void a_pool_that_shrinks_is_not_a_refill()
    {
        var (world, _, _, item, backpack) = Armed();
        using var _ = world;
        backpack.Hold(Standard, FactsWorld.Item(item));
        backpack.Pool[Standard] = 40;
        world.Inventory.Reconcile(backpack);

        backpack.Pool[Standard] = 10;
        world.Inventory.Reconcile(backpack);

        Assert.Empty(world.Facts(Refilled));
    }

    /// <summary>The pool movement inside a reload window is the reload family's transfer, so the refill row stays
    /// silent: both rows read the same pool, and one movement is published once.</summary>
    [Fact]
    public void a_pool_that_grows_inside_a_reload_window_is_the_reload_rows_alone()
    {
        var (world, _, ownerReference, item, backpack) = Armed();
        using var _ = world;
        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);

        world.Reload.FlagChanged(item, reloading: true);
        world.Reload.PoolRead(ownerReference, 40);
        backpack.Pool[Standard] = 40;
        world.Inventory.Reconcile(backpack);

        Assert.Single(world.Facts(ReloadInventoryContract.ReloadTransferredBinding));
        Assert.Empty(world.Facts(Refilled));
    }

    [Fact]
    public void a_pool_that_grows_with_no_live_equipment_is_reported_and_publishes_nothing()
    {
        var (world, _, _, item, backpack) = Armed();
        using var _ = world;
        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);
        world.Retire(world.ByItem[item.Id]);

        backpack.Pool[Standard] = 40;
        world.Inventory.Reconcile(backpack);

        Assert.Empty(world.Facts(Refilled));
        Assert.Contains(world.Reports, line => line.Contains("refill-unresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void a_use_sequence_that_is_not_a_reload_publishes_use_started_once()
    {
        var (world, _, ownerReference, item, _) = Armed();
        using var _ = world;

        world.Inventory.UseStarted(item);

        var fact = Assert.Single(world.Facts(UseStarted));
        Assert.Equal(ownerReference.Id, FactsWorld.Reference(fact, "actor"));
        Assert.Equal(world.ByItem[item.Id].Id, FactsWorld.Reference(fact, "equipment"));
    }

    [Fact]
    public void a_use_that_starts_twice_without_ending_publishes_once()
    {
        var (world, _, _, item, _) = Armed();
        using var _ = world;

        world.Inventory.UseStarted(item);
        world.Inventory.UseStarted(item);

        Assert.Single(world.Facts(UseStarted));
        Assert.Equal(1, world.Inventory.OpenUses);
    }

    [Fact]
    public void a_reload_sequence_publishes_no_use_started()
    {
        var (world, _, _, item, _) = Armed();
        using var _ = world;
        item.Reloading = true;

        world.Inventory.UseStarted(item);

        Assert.Empty(world.Facts(UseStarted));
        Assert.Empty(world.Facts(UseFailed));
    }

    [Fact]
    public void a_use_the_game_refused_carries_the_outcome_and_the_reason()
    {
        var (world, _, ownerReference, item, _) = Armed();
        using var _ = world;

        world.Inventory.Refused(item);

        var fact = Assert.Single(world.Facts(UseFailed));
        Assert.Equal(InventoryObserver.RejectedOutcome, FactsWorld.Text(fact, "outcome"));
        Assert.Equal(InventoryObserver.NotReloadableReason, FactsWorld.Text(fact, "reason"));
        Assert.Equal(ownerReference.Id, FactsWorld.Reference(fact, "actor"));
        Assert.Equal(world.ByItem[item.Id].Id, FactsWorld.Reference(fact, "equipment"));
    }

    [Fact]
    public void an_item_with_no_live_identity_publishes_no_inventory_fact()
    {
        var (world, _, _, _, _) = Armed();
        using var _ = world;
        var stranger = new FakeItem { Clip = 10 };

        world.Inventory.UseStarted(stranger);
        world.Inventory.Refused(stranger);

        Assert.Empty(world.Published);
    }

    /// <summary>An ending this provider cannot report leaves an open use forgotten and publishes nothing: the row
    /// reports refusals, and `cancelled` would be a claim no readback here supports.</summary>
    [Fact]
    public void an_ended_use_publishes_nothing_and_forgets_the_open_use()
    {
        var (world, _, _, item, _) = Armed();
        using var _ = world;
        world.Inventory.UseStarted(item);
        Assert.Equal(1, world.Inventory.OpenUses);

        world.Inventory.UseEnded(item);

        Assert.Equal(0, world.Inventory.OpenUses);
        Assert.Single(world.Facts(UseStarted));
        Assert.Empty(world.Facts(UseFailed));
    }

    [Fact]
    public void a_lost_authority_drops_the_tables_without_a_fact()
    {
        var (world, _, _, item, backpack) = Armed();
        using var _ = world;
        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);
        world.Inventory.UseStarted(item);
        var before = world.Published.Count;

        world.LoseAuthority();
        world.Inventory.Reconcile(backpack);

        Assert.Equal(before, world.Published.Count);
        Assert.Equal(0, world.Inventory.TrackedPlayers);
        Assert.Equal(0, world.Inventory.OpenUses);
        Assert.Contains(world.Infos, line => line.Contains("reason=not-authoritative", StringComparison.Ordinal));
    }

    [Fact]
    public void a_dispose_drops_the_tables_without_a_fact()
    {
        var (world, _, _, item, backpack) = Armed();
        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);
        var before = world.Published.Count;

        world.Inventory.Clear();

        Assert.Equal(before, world.Published.Count);
        Assert.Equal(0, world.Inventory.TrackedPlayers);
        Assert.Contains(world.Infos, line => line.Contains("reason=dispose", StringComparison.Ordinal));
        world.Dispose();
    }

    [Fact]
    public void every_inventory_fact_names_the_epoch_it_was_read_in()
    {
        var (world, _, _, item, backpack) = Armed();
        using var _ = world;
        backpack.Empty(Standard);
        world.Inventory.Reconcile(backpack);
        backpack.Hold(Standard, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);
        world.Inventory.Refused(item);

        Assert.NotEmpty(world.Published);
        Assert.All(world.Published, fact => Assert.Equal(world.WorldEpoch, fact.WorldEpoch));
    }
}
