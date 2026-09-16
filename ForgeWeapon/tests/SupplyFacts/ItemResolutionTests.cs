using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;
using GameData;
using Player;

namespace ForgeWeapon.Tests.SupplyFacts;

/// <summary>The item half of the inventory rows: an authored resource id is the official `ItemDataBlock`
/// persistent id as decimal text, and the row that adds a pocket item takes it only when the block itself says
/// the item belongs in the pocket.</summary>
public sealed class ItemResolutionTests
{
    [Fact]
    public void a_decimal_persistent_id_of_a_pocket_item_resolves()
    {
        using var world = new SupplyWorld();
        ItemDataBlock.Blocks[4242] = new ItemDataBlock { persistentID = 4242, inventorySlot = InventorySlot.InPocket };

        Assert.True(WeaponSupplyItems.TryResolve("4242", out uint itemId, out var slot));
        Assert.Equal(4242u, itemId);
        Assert.Equal(InventorySlot.InPocket, slot);
        Assert.True(WeaponSupplyItems.TryResolvePocketItem("4242", out uint pocket));
        Assert.Equal(4242u, pocket);
    }

    [Fact]
    public void an_id_no_block_answers_is_refused()
    {
        using var world = new SupplyWorld();
        ItemDataBlock.Blocks[4242] = new ItemDataBlock { persistentID = 4242 };

        Assert.False(WeaponSupplyItems.TryResolve("4243", out _, out _));
        Assert.False(WeaponSupplyItems.TryResolvePocketItem("4243", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("medkit")]
    [InlineData("0x1092")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData(" 4242")]
    [InlineData("42424242424242")]
    public void a_text_that_is_not_a_decimal_id_is_refused(string resourceId)
    {
        using var world = new SupplyWorld();
        ItemDataBlock.Blocks[4242] = new ItemDataBlock { persistentID = 4242 };

        Assert.False(WeaponSupplyItems.TryResolve(resourceId, out _, out _));
    }

    [Fact]
    public void a_gear_slot_item_resolves_but_is_not_a_pocket_item()
    {
        using var world = new SupplyWorld();
        ItemDataBlock.Blocks[900] = new ItemDataBlock { persistentID = 900, inventorySlot = InventorySlot.ResourcePack };

        Assert.True(WeaponSupplyItems.TryResolve("900", out uint itemId, out var slot));
        Assert.Equal(900u, itemId);
        Assert.Equal(InventorySlot.ResourcePack, slot);
        // The pocket-item rows take this narrow answer, so a resource pack is refused rather than put in a slot
        // the game never reads it from.
        Assert.False(WeaponSupplyItems.TryResolvePocketItem("900", out _));
    }

    [Fact]
    public void a_block_whose_id_differs_from_the_one_asked_for_is_refused()
    {
        using var world = new SupplyWorld();
        // A table that answers the wrong block is a disagreement this resolver refuses to write through.
        ItemDataBlock.Blocks[4242] = new ItemDataBlock { persistentID = 7 };

        Assert.False(WeaponSupplyItems.TryResolve("4242", out _, out _));
    }

    [Fact]
    public void a_table_that_throws_is_a_resource_this_process_cannot_name()
    {
        using var world = new SupplyWorld();
        ItemDataBlock.ThrowOnLookup = new InvalidOperationException("fixture table failure");

        Assert.False(WeaponSupplyItems.TryResolve("4242", out _, out _));
        Assert.False(WeaponSupplyItems.TryResolvePocketItem("4242", out _));
        ItemDataBlock.ThrowOnLookup = null;
    }

    [Fact]
    public void the_zero_id_is_never_an_item()
    {
        using var world = new SupplyWorld();
        ItemDataBlock.Blocks[0] = new ItemDataBlock { persistentID = 0 };

        Assert.False(WeaponSupplyItems.TryResolve("0", out _, out _));
    }

    /// <summary>The give and consume rows are the item half's own capability rows: no module anywhere declares an
    /// `forge.action.inventory.*` capability, so a binding without one cannot register. Each row's ports have to be
    /// exactly the set its handler shape declares, because the registration compares the two.</summary>
    [Fact]
    public void the_give_and_consume_rows_declare_exactly_the_ports_their_handlers_read()
    {
        CommandHandler body = _ => CommandResult.Rejected("never");
        var rows = InventoryActionContract.Capabilities(body, body).Cast<JsonElement>().ToDictionary(
            row => row.GetProperty("id").GetString()!, row => row);
        Assert.Equal(2, rows.Count);
        var expected = new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [InventoryActionContract.GiveCapability] = InventoryActionContract.GiveShape,
            [InventoryActionContract.ConsumeCapability] = InventoryActionContract.ConsumeShape
        };

        foreach (var (capability, shape) in expected)
        {
            Assert.True(rows.ContainsKey(capability), "The row is not declared: " + capability);
            var graph = rows[capability].GetProperty("graph");
            var ports = graph.GetProperty("inputs").EnumerateArray()
                .Select(port => port.GetProperty("id").GetString()!)
                .Where(id => id != "in").ToArray();
            Assert.Equal(shape.InputPorts, ports);
            var parameters = graph.GetProperty("parameters").EnumerateArray()
                .Select(parameter => parameter.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(shape.ParameterIds, parameters);
            Assert.Equal(shape.OutputPorts, graph.GetProperty("outputs").EnumerateArray()
                .Select(port => port.GetProperty("id").GetString()!)
                .Where(id => id != "next").ToArray());
            string binding = capability == InventoryActionContract.GiveCapability
                ? InventoryActionContract.GiveBinding : InventoryActionContract.ConsumeBinding;
            Assert.Equal("implementation-only", InventoryActionContract.Support(binding).Verification);
        }
    }
}
