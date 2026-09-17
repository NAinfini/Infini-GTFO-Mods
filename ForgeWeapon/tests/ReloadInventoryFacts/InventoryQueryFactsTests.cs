using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.ReloadInventoryFacts;

/// <summary>The two read rows and the two new fact behaviours over doubles. The read rows are answered from a
/// source double — the native source is the only game-bound part of them — and the carried-item row and the count
/// row are driven through the production observer against a slot table the case fills, exactly as the rest of this
/// family is: what is under test is which row a readback produces and which ports it fills, not the game's player
/// path, which no case here runs.</summary>
[Trait("Category", "ReloadInventoryFacts")]
public sealed class InventoryQueryFactsTests
{
    private const string Carried = ReloadInventoryContract.CarriedItemChangedBinding;
    private const string Stack = ReloadInventoryContract.StackChangedBinding;
    private const string Pocket = "ResourcePack";

    /// <summary>The equipment row answers the three numbers the source read, and a source that refuses keeps its
    /// own code: a plan must never be answered a zero the game did not report.</summary>
    [Fact]
    public void the_equipment_row_answers_the_source_and_keeps_its_refusal_code()
    {
        var reference = new EntityReference("gtfo.equipment:1", 1, 1);
        var reads = new StubSource { Ammo = new EquipmentAmmo(7, 12, 30) };

        var answer = InventoryQueryReads.Ammo(reads, reference);

        Assert.Equal(7, answer.GetProperty("clip").GetInt32());
        Assert.Equal(12, answer.GetProperty("clip_max").GetInt32());
        Assert.Equal(30, answer.GetProperty("reserve").GetInt32());

        var refused = new StubSource { AmmoCode = "stale-equipment" };
        var failure = Assert.Throws<RuntimeContractException>(() => InventoryQueryReads.Ammo(refused, reference));
        Assert.Equal("stale-equipment", failure.Code);
    }

    /// <summary>The condition compares the count the read answered against the count the node asked for, so one
    /// native read serves every threshold; a refusal travels as the read's own code.</summary>
    [Fact]
    public void the_item_condition_compares_the_count_the_read_answered()
    {
        var holder = new EntityReference("gtfo.player:1", 1, 1);
        var reads = new StubSource { Held = 2 };

        Assert.True(InventoryQueryReads.Held(reads, holder, "55", 2).GetProperty("value").GetBoolean());
        Assert.False(InventoryQueryReads.Held(reads, holder, "55", 3).GetProperty("value").GetBoolean());
        Assert.True(InventoryQueryReads.Held(reads, holder, "55", 1).GetProperty("value").GetBoolean());

        var refused = new StubSource { HeldCode = "no-such-item" };
        var failure = Assert.Throws<RuntimeContractException>(() => InventoryQueryReads.Held(refused, holder, "55", 1));
        Assert.Equal("no-such-item", failure.Code);
    }

    /// <summary>The count the condition asks for is a required input: absent and below one are both refused by
    /// their own code rather than answered as a default of one.</summary>
    [Fact]
    public void the_item_condition_refuses_a_count_that_is_missing_or_below_one()
    {
        using var missing = JsonDocument.Parse("{}");
        Assert.Equal(InventoryQueryReads.MissingFieldCode,
            Assert.Throws<RuntimeContractException>(() => InventoryQueryReads.Wanted(missing.RootElement)).Code);
        using var zero = JsonDocument.Parse("{\"count\":0}");
        Assert.Equal(InventoryQueryReads.CountOutOfRangeCode,
            Assert.Throws<RuntimeContractException>(() => InventoryQueryReads.Wanted(zero.RootElement)).Code);
        using var asked = JsonDocument.Parse("{\"count\":4}");
        Assert.Equal(4, InventoryQueryReads.Wanted(asked.RootElement));
    }

    /// <summary>The count row reports the backpack's own pocket count of that item id — the number that groups the
    /// slots holding the id into one stack — and not the slot's occupancy.</summary>
    [Fact]
    public void the_count_row_reports_the_pocket_group_count()
    {
        var world = new FactsWorld();
        using var _ = world;
        world.Start();
        var (owner, _) = world.Player();
        var item = new FakeItem { ItemId = FactsWorld.ArmedItemId };
        world.Life(item, owner);
        var backpack = world.Backpack(owner);
        backpack.Empty(Pocket);
        world.Inventory.Reconcile(backpack);
        backpack.Stacks[FactsWorld.ArmedItemId] = 5;

        backpack.Hold(Pocket, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);

        var stack = Assert.Single(world.Facts(Stack));
        Assert.Equal(5, FactsWorld.Number(stack, "count"));
        Assert.Equal(1, FactsWorld.Number(stack, "delta"));
    }

    /// <summary>An item the backpack counts no pocket group for — a weapon, a pack, the large item in the carry
    /// slot — publishes no count row at all rather than a zero, while its pickup is still published.</summary>
    [Fact]
    public void an_item_with_no_pocket_count_publishes_no_count_row()
    {
        var world = new FactsWorld();
        using var _ = world;
        world.Start();
        var (owner, _) = world.Player();
        var item = new FakeItem { ItemId = FactsWorld.ArmedItemId };
        world.Life(item, owner);
        var backpack = world.Backpack(owner);
        backpack.Empty(Pocket);
        world.Inventory.Reconcile(backpack);

        backpack.Hold(Pocket, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);

        Assert.Single(world.Facts(ReloadInventoryContract.PickedUpBinding));
        Assert.Empty(world.Facts(Stack));
    }

    /// <summary>The carried-item row is the carry slot's own change: an item appearing there publishes both ports,
    /// and the slot emptying publishes the same row with a null item rather than refusing — that port is the one
    /// nullable port in this family.</summary>
    [Fact]
    public void the_carry_slot_publishes_the_item_and_a_null_on_removal()
    {
        var world = new FactsWorld();
        using var _ = world;
        world.Start();
        var (owner, ownerReference) = world.Player();
        var item = new FakeItem { ItemId = FactsWorld.ArmedItemId };
        world.Life(item, owner);
        var backpack = world.Backpack(owner);
        backpack.Empty(InventoryObserver.CarriedSlotName);
        world.Inventory.Reconcile(backpack);

        backpack.Hold(InventoryObserver.CarriedSlotName, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);

        var entered = Assert.Single(world.Facts(Carried));
        Assert.Equal(ownerReference.Id, FactsWorld.Reference(entered, "player"));
        Assert.Equal(world.ByItem[item.Id].Id, FactsWorld.Reference(entered, "item"));

        backpack.Clear(InventoryObserver.CarriedSlotName);
        world.Inventory.Reconcile(backpack);

        var left = world.Facts(Carried)[1];
        Assert.Equal(ownerReference.Id, FactsWorld.Reference(left, "player"));
        Assert.Equal(JsonValueKind.Null, FactsWorld.Outputs(left).GetProperty("item").ValueKind);
    }

    /// <summary>A carry slot whose item is replaced by one this machine can no longer name publishes nothing and
    /// reports it: the row's `item` port is nullable for an emptied slot, not for an unreadable one.</summary>
    [Fact]
    public void a_carry_slot_holding_an_unnamed_item_is_reported_and_publishes_nothing()
    {
        var world = new FactsWorld();
        using var _ = world;
        world.Start();
        var (owner, _) = world.Player();
        var item = new FakeItem { ItemId = FactsWorld.ArmedItemId };
        world.Life(item, owner);
        var backpack = world.Backpack(owner);
        backpack.Empty(InventoryObserver.CarriedSlotName);
        world.Inventory.Reconcile(backpack);
        backpack.Hold(InventoryObserver.CarriedSlotName, FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);
        var entered = world.Facts(Carried).Count;

        // The same slot, the same item object, a new instance, and an identity this machine no longer holds.
        world.Retire(world.ByItem[item.Id]);
        backpack.Hold(InventoryObserver.CarriedSlotName, FactsWorld.Item(item, instance: (IntPtr)9100));
        world.Inventory.Reconcile(backpack);

        Assert.Equal(entered, world.Facts(Carried).Count);
        Assert.Contains(world.Reports, line => line.Contains("carried-item-unresolved", StringComparison.Ordinal));
    }

    /// <summary>The two reads the rows ask for, answered from fields a case sets: no code means the read succeeds,
    /// and any other code is what the row refuses by.</summary>
    private sealed class StubSource : IInventoryQuerySource
    {
        internal EquipmentAmmo Ammo { get; set; } = new(0, 0, 0);
        internal string? AmmoCode { get; set; }
        internal int Held { get; set; }
        internal string? HeldCode { get; set; }

        public bool TryEquipmentAmmo(EntityReference equipment, out EquipmentAmmo ammo, out string code)
        {
            ammo = Ammo;
            code = AmmoCode ?? "";
            return AmmoCode == null;
        }

        public bool TryHeldCount(EntityReference holder, string resourceId, out int count, out string code)
        {
            count = Held;
            code = HeldCode ?? "";
            return HeldCode == null;
        }
    }
}
