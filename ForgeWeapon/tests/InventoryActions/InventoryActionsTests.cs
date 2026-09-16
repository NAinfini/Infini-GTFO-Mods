using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.InventoryActions;

/// <summary>The inventory actions over doubles: host execution, the non-host refusal, a dead world, the target
/// that is not a player life, the resource that cannot be bound, the native refusal paths, and the result rows.
/// Every case drives the production adapter through the SDK's own command context and reads what it did through
/// the game hooks; no GTFO assembly is loaded and no hook is installed, so these cases prove the adapter's
/// decisions, not the game's inventory behaviour.</summary>
[Trait("Category", "InventoryActions")]
public sealed class InventoryActionsTests
{
    /// <summary>The game's static surface is process-wide, so a case starts from an untouched one.</summary>
    private static void ResetGame()
    {
        Player.PlayerBackpackManager.Reset();
        SNetwork.SNet.Reset();
    }

    private static CommandResult Give(InventoryActionsWorld world, EntityReference[] recipients,
        string resource = InventoryActionsWorld.KnownResource, int? quantity = null, int? charges = null,
        string capacityPolicy = "reject")
    {
        var inputs = new List<(string, object?)>
        {
            (InventoryActionContract.GiveShape.InputPorts[0], recipients),
            ("item", InventoryActionsWorld.Item(resource))
        };
        if (quantity.HasValue) inputs.Add(("quantity", quantity.Value));
        if (charges.HasValue) inputs.Add(("charges", charges.Value));
        return world.Adapter().HandleGive(world.Context(InventoryActionContract.GiveBinding,
            new { capacity_policy = capacityPolicy }, inputs.ToArray()));
    }

    private static CommandResult Consume(InventoryActionsWorld world, EntityReference[] recipients,
        string resource = InventoryActionsWorld.KnownResource, int? count = null)
    {
        var inputs = new List<(string, object?)>
        {
            (InventoryActionContract.ConsumeShape.InputPorts[0], recipients),
            ("item", InventoryActionsWorld.Item(resource))
        };
        if (count.HasValue) inputs.Add(("count", count.Value));
        return world.Adapter().HandleConsume(world.Context(InventoryActionContract.ConsumeBinding, new { }, inputs.ToArray()));
    }

    // ---- host execution ----------------------------------------------------------------------------------

    [Fact]
    public void give_adds_the_item_the_resource_bound_and_reads_it_back()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (player, backpack, reference) = world.PlayerRef(1);
        Player.PlayerBackpackManager.OnAdd = (target, data) =>
            target.Slots![(int)data.slot] = new Player.BackpackItem { ItemID = data.itemID_gearCRC };

        var result = Give(world, new[] { reference }, quantity: 1, charges: 3);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        var request = Assert.Single(Player.PlayerBackpackManager.AddRequests);
        Assert.Equal(InventoryActionsWorld.KnownItemId, request.data.itemID_gearCRC);
        Assert.Equal((byte)Player.InventorySlot.InPocket, (byte)request.data.slot);
        Assert.Equal(3f, request.data.custom.ammo);
        Assert.Same(player, request.owningPlayer.Player);
        Assert.Equal(InventoryActionsWorld.KnownItemId, backpack.Slots![InventoryActionAdapter.PocketSlot]!.ItemID);
        var row = InventoryActionsWorld.Row(result);
        Assert.Equal(InventoryActionContract.GiveCapability, "forge.action.inventory.give");
        Assert.Equal(reference, RuntimeJson.Entity(row.GetProperty("target")));
        Assert.Equal("succeeded", row.GetProperty("status").GetString());        Assert.Equal(CommitStates.Confirmed, row.GetProperty("committed").GetString());
        Assert.Equal(InventoryActionAdapter.CommittedCode, row.GetProperty("code").GetString());
        Assert.Equal(1, row.GetProperty("quantity").GetInt32());
        Assert.Equal(1, row.GetProperty("target_count").GetInt32());
    }

    [Fact]
    public void a_quantity_beyond_the_one_slot_the_native_add_fills_is_refused()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(2);
        Player.PlayerBackpackManager.OnAdd = (target, data) =>
            target.Slots![(int)data.slot] = new Player.BackpackItem { ItemID = data.itemID_gearCRC };

        // A pocket item occupies its own slot in this build, so there is no stack a second unit could join; a
        // request for more than one is refused instead of silently handing out one.
        var result = Give(world, new[] { reference }, quantity: 2);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(InventoryActionAdapter.QuantityCode, result.Code);
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
    }

    [Fact]
    public void consume_spends_the_count_and_reports_what_it_spent()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, backpack, reference) = world.PlayerRef(3);
        backpack.Pocket[InventoryActionsWorld.KnownItemId] = 5;
        Player.PlayerBackpackManager.OnRemovePocket = (target, itemId) =>
            target.Pocket[itemId] = target.CountPocketItem(itemId) - 1;

        var result = Consume(world, new[] { reference }, count: 3);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(3, Player.PlayerBackpackManager.PocketRemovals.Count);
        Assert.Equal(2, backpack.Pocket[InventoryActionsWorld.KnownItemId]);
        var row = InventoryActionsWorld.Row(result);
        Assert.Equal(3, row.GetProperty("count").GetInt32());
        Assert.Equal(InventoryActionAdapter.CommittedCode, row.GetProperty("code").GetString());
    }

    // ---- authority ---------------------------------------------------------------------------------------

    [Fact]
    public void a_non_authoritative_session_refuses_before_any_native_call()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(5);
        world.LoseAuthority();

        foreach (var result in new[] { Give(world, new[] { reference }), Consume(world, new[] { reference }) })
        {
            Assert.Equal(CommandStatuses.Rejected, result.Status);
            Assert.Equal(CommitStates.None, result.CommitState);
            Assert.Equal(InventoryActionAdapter.AuthorityCode, result.Code);
        }
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
        Assert.Empty(Player.PlayerBackpackManager.PocketRemovals);
    }

    [Fact]
    public void a_machine_that_is_not_the_game_master_refuses_before_any_native_call()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(6);
        SNetwork.SNet.IsMaster = false;

        Assert.Equal(InventoryActionAdapter.AuthorityCode, Give(world, new[] { reference }).Code);
        Assert.Equal(InventoryActionAdapter.AuthorityCode, Consume(world, new[] { reference }).Code);
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
    }

    // ---- target expiry -----------------------------------------------------------------------------------

    [Fact]
    public void a_reference_of_another_kind_is_refused_by_kind()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var other = world.Other("a");

        var result = Give(world, new[] { other });

        var row = InventoryActionsWorld.Row(result);
        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(InventoryActionAdapter.TargetKindCode, row.GetProperty("code").GetString());
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
    }

    [Fact]
    public void a_retired_player_life_is_refused_as_stale()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(7);
        world.LivePlayers.Remove(reference);

        var result = Give(world, new[] { reference });

        Assert.Equal(InventoryActionAdapter.StaleCode, InventoryActionsWorld.Row(result).GetProperty("code").GetString());
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
    }

    [Fact]
    public void a_reference_from_an_old_world_is_refused_as_stale()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(8);

        var result = Give(world, new[] { reference with { WorldEpoch = InventoryActionsWorld.WorldEpoch + 1 } });

        Assert.Equal(InventoryActionAdapter.StaleCode, InventoryActionsWorld.Row(result).GetProperty("code").GetString());
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
    }

    [Fact]
    public void a_player_without_a_backpack_is_refused_by_name()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(9, withBackpack: false);

        var result = Consume(world, new[] { reference });

        Assert.Equal(InventoryActionAdapter.NoBackpackCode, InventoryActionsWorld.Row(result).GetProperty("code").GetString());
        Assert.Empty(Player.PlayerBackpackManager.PocketRemovals);
    }

    [Fact]
    public void a_throwing_player_lookup_is_refused_as_stale()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(10);
        world.LookupThrows = true;
        var adapter = world.Adapter();

        var outcome = adapter.Give(reference, InventoryActionsWorld.KnownItemId, 1, 1);

        // A native table mid-teardown is a stale answer, not a crash: the refusal is the stale one and nothing was
        // submitted, so the caller cannot mistake a failed lookup for a failed write.
        Assert.Equal(CommandStatuses.Rejected, outcome.Status);
        Assert.Equal(CommitStates.None, outcome.CommitState);
        Assert.Equal(InventoryActionAdapter.StaleCode, outcome.Code);
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
    }

    [Fact]
    public void a_reference_this_session_cannot_name_is_refused_as_stale()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(34);
        // The reference is current for its own kind, but this session has no way to name the native player behind
        // it — the direction the kernel's tables do not have. Nothing is written from a guessed player.
        world.CanNamePlayers = false;

        var result = Consume(world, new[] { reference });

        Assert.Equal(InventoryActionAdapter.StaleCode, InventoryActionsWorld.Row(result).GetProperty("code").GetString());
        Assert.Empty(Player.PlayerBackpackManager.PocketRemovals);
    }

    // ---- request refusals --------------------------------------------------------------------------------

    [Fact]
    public void an_item_resource_this_environment_cannot_bind_refuses_the_whole_command()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, first) = world.PlayerRef(11);
        var (_, _, second) = world.PlayerRef(12);

        foreach (var result in new[]
        {
            Give(world, new[] { first, second }, InventoryActionsWorld.UnknownResource),
            Consume(world, new[] { first, second }, InventoryActionsWorld.UnknownResource)
        })
        {
            Assert.Equal(CommandStatuses.Rejected, result.Status);
            Assert.Equal(InventoryActionAdapter.ResourceCode, result.Code);
        }
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
        Assert.Empty(Player.PlayerBackpackManager.PocketRemovals);
    }

    [Fact]
    public void a_quantity_outside_the_bound_is_refused_before_any_recipient()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(13);
        Player.PlayerBackpackManager.OnAdd = (target, data) =>
            target.Slots![(int)data.slot] = new Player.BackpackItem { ItemID = data.itemID_gearCRC };

        Assert.Equal(InventoryActionAdapter.QuantityCode, Give(world, new[] { reference }, quantity: 0).Code);
        Assert.Equal(InventoryActionAdapter.QuantityCode,
            Give(world, new[] { reference }, quantity: InventoryActionAdapter.MaximumQuantity + 1).Code);
        Assert.Equal(InventoryActionAdapter.QuantityCode, Consume(world, new[] { reference }, count: 0).Code);
        // An absent quantity is the action's own default of one, and this recipient's slot is empty, so the
        // request itself is not what gets refused.
        Assert.Equal(CommandStatuses.Succeeded, Give(world, new[] { reference }).Status);
        Assert.Single(Player.PlayerBackpackManager.AddRequests);
    }

    [Fact]
    public void a_charge_count_this_build_cannot_carry_is_refused()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(14);

        Assert.Equal(InventoryActionAdapter.ChargesCode,
            Give(world, new[] { reference }, charges: (int)InventoryActionAdapter.MaximumCharges + 1).Code);
        // `consume` has no charge port at all, so a request that supplies one is asking for a port the row does
        // not declare.
        var inputs = new (string, object?)[]
        {
            (InventoryActionContract.ConsumeShape.InputPorts[0], new[] { reference }),
            ("item", InventoryActionsWorld.Item(InventoryActionsWorld.KnownResource)),
            ("charges", 2)
        };
        var result = world.Adapter().HandleConsume(world.Context(InventoryActionContract.ConsumeBinding, new { }, inputs));
        Assert.Equal(InventoryActionAdapter.ChargesCode, result.Code);
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
    }

    [Fact]
    public void an_occupied_pocket_slot_is_refused_instead_of_overwritten()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, backpack, reference) = world.PlayerRef(15);
        InventoryActionsWorld.Occupy(backpack, InventoryActionAdapter.PocketSlot, 99);

        var result = Give(world, new[] { reference });

        var row = InventoryActionsWorld.Row(result);
        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(InventoryActionAdapter.StateChangedCode, row.GetProperty("code").GetString());
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
        Assert.Equal(99u, backpack.Slots![InventoryActionAdapter.PocketSlot]!.ItemID);
    }

    [Fact]
    public void a_backpack_with_no_pocket_slot_is_refused_by_name()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(16, slots: 4);

        var result = Give(world, new[] { reference });

        Assert.Equal(InventoryActionAdapter.SlotCode, InventoryActionsWorld.Row(result).GetProperty("code").GetString());
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
    }

    // ---- native refusal and readback paths ---------------------------------------------------------------

    [Fact]
    public void a_native_add_that_leaves_the_slot_unchanged_is_an_unknown_commit()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(22);
        // The game's own body sends its packet before it applies anything; a body that sent and applied nothing
        // is exactly the uncertainty the row has to report instead of a success.
        var adapter = world.Adapter();

        var outcome = adapter.Give(reference, InventoryActionsWorld.KnownItemId, 1, 1);

        Assert.Equal(CommandStatuses.Failed, outcome.Status);
        Assert.Equal(CommitStates.Unknown, outcome.CommitState);
        Assert.Equal(InventoryActionAdapter.StateChangedCode, outcome.Code);
        Assert.Single(Player.PlayerBackpackManager.AddRequests);
    }

    [Fact]
    public void a_throwing_native_add_is_an_unknown_commit_and_is_reported()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(23);
        Player.PlayerBackpackManager.ThrowOnAdd = new InvalidOperationException("fixture native failure");
        var adapter = world.Adapter();

        var outcome = adapter.Give(reference, InventoryActionsWorld.KnownItemId, 1, 1);

        Assert.Equal(CommandStatuses.Failed, outcome.Status);
        Assert.Equal(CommitStates.Unknown, outcome.CommitState);
        Assert.Equal(InventoryActionAdapter.CommitExceptionCode, outcome.Code);
        Assert.Contains(world.Reports, report => report.Contains("weapon.give-commit-exception", StringComparison.Ordinal));
    }

    [Fact]
    public void a_throwing_native_removal_is_an_unknown_commit_and_is_reported()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, backpack, reference) = world.PlayerRef(24);
        backpack.Pocket[InventoryActionsWorld.KnownItemId] = 2;
        Player.PlayerBackpackManager.ThrowOnRemove = new InvalidOperationException("fixture native failure");
        var adapter = world.Adapter();

        var outcome = adapter.Consume(reference, InventoryActionsWorld.KnownItemId, 1);

        Assert.Equal(CommandStatuses.Failed, outcome.Status);
        Assert.Equal(CommitStates.Unknown, outcome.CommitState);
        Assert.Equal(InventoryActionAdapter.CommitExceptionCode, outcome.Code);
        Assert.Contains(world.Reports, report => report.Contains("weapon.consume-commit-exception", StringComparison.Ordinal));
    }

    [Fact]
    public void a_consume_that_runs_out_mid_run_keeps_what_it_spent_and_stays_unknown()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, backpack, reference) = world.PlayerRef(26);
        backpack.Pocket[InventoryActionsWorld.KnownItemId] = 2;
        Player.PlayerBackpackManager.OnRemovePocket = (target, itemId) =>
            target.Pocket[itemId] = target.CountPocketItem(itemId) - 1;

        var result = Consume(world, new[] { reference }, count: 5);

        // Two units were really removed and five were asked for, so the run is an unknown commit carrying the
        // two it spent; reporting a plain refusal would hide the removal, and reporting a success would overstate.
        var row = InventoryActionsWorld.Row(result);
        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal(InventoryActionAdapter.StateChangedCode, row.GetProperty("code").GetString());
        Assert.Equal(2, row.GetProperty("count").GetInt32());
        Assert.Equal(2, Player.PlayerBackpackManager.PocketRemovals.Count);
    }

    [Fact]
    public void an_unknown_commit_stops_the_remaining_recipients_of_a_give()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, first) = world.PlayerRef(27);
        var (_, _, second) = world.PlayerRef(28);
        Player.PlayerBackpackManager.ThrowOnAdd = new InvalidOperationException("fixture native failure");

        var result = Give(world, new[] { first, second });

        var rows = InventoryActionsWorld.Rows(result);
        Assert.Equal(2, rows.Length);
        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal(InventoryActionAdapter.CommitExceptionCode, rows[0].GetProperty("code").GetString());
        // The second recipient is not attempted after an unknown commit, and says so in its own row. The add
        // request list stays empty because the game's own body is what threw, before it recorded anything.
        Assert.Equal("not-attempted-after-unknown-commit", rows[1].GetProperty("code").GetString());
        Assert.Empty(Player.PlayerBackpackManager.AddRequests);
        Assert.Contains(world.Reports, report => report.Contains("weapon.give-commit-exception", StringComparison.Ordinal));
    }

    // ---- rows and aggregation ----------------------------------------------------------------------------

    [Fact]
    public void one_row_is_written_per_recipient_in_the_plans_order()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, first) = world.PlayerRef(29);
        var (_, _, second) = world.PlayerRef(30);
        var (_, _, third) = world.PlayerRef(35);
        Player.PlayerBackpackManager.OnAdd = (target, data) =>
            target.Slots![(int)data.slot] = new Player.BackpackItem { ItemID = data.itemID_gearCRC };

        var result = Give(world, new[] { first, first, second, third });

        var rows = InventoryActionsWorld.Rows(result);
        Assert.Equal(4, rows.Length);
        Assert.Equal(first, RuntimeJson.Entity(rows[0].GetProperty("target")));
        Assert.Equal(first, RuntimeJson.Entity(rows[1].GetProperty("target")));
        Assert.Equal(second, RuntimeJson.Entity(rows[2].GetProperty("target")));
        Assert.Equal(third, RuntimeJson.Entity(rows[3].GetProperty("target")));
        Assert.All(rows, row => Assert.Equal(4, row.GetProperty("target_count").GetInt32()));
        // The same recipient twice is two rows, and the second one is refused because its slot now holds the
        // first item, not deduped away.
        Assert.Equal(CommandStatuses.Partial, result.Status);
        Assert.Equal(3, Player.PlayerBackpackManager.AddRequests.Count);
    }

    [Fact]
    public void all_refused_names_the_shared_reason_and_a_mixed_run_is_partial()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, first) = world.PlayerRef(31);
        var (_, backpack, second) = world.PlayerRef(32);
        InventoryActionsWorld.Occupy(backpack, InventoryActionAdapter.PocketSlot, 5);
        var adapter = world.Adapter();

        // Two recipients with two different reasons are not one code, so the run reports its own: the first row is
        // a reference that is not current any more, the second a slot the add cannot use.
        var mixed = InventoryActionAdapter.Aggregate("give", new[]
        {
            adapter.Give(first with { LifeEpoch = 9 }, InventoryActionsWorld.KnownItemId, 1, 1),
            adapter.Give(second, InventoryActionsWorld.KnownItemId, 1, 1)
        });
        Assert.Equal("give-all-rejected", mixed.Code);
        Assert.Equal(CommandStatuses.Rejected, mixed.Status);
        Assert.Equal(CommitStates.None, mixed.CommitState);

        // Nothing submitted at all is still a refusal with the one shared code when the recipients agree.
        var agreed = InventoryActionAdapter.Aggregate("give", new[]
        {
            adapter.Give(second, InventoryActionsWorld.KnownItemId, 1, 1),
            adapter.Give(second, InventoryActionsWorld.KnownItemId, 1, 1)
        });
        Assert.Equal(InventoryActionAdapter.StateChangedCode, agreed.Code);
    }

    [Fact]
    public void the_row_schema_fields_and_port_names_are_the_catalogs()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var (_, _, reference) = world.PlayerRef(33);
        Player.PlayerBackpackManager.OnAdd = (target, data) =>
            target.Slots![(int)data.slot] = new Player.BackpackItem { ItemID = data.itemID_gearCRC };
        // The handler reads the port the row actually declares; a request that wires the recipients somewhere
        // else is not the row's own shape.
        var wrong = world.Context(InventoryActionContract.GiveBinding, new { capacity_policy = "reject" },
            ("recipients", new[] { reference }), ("item", InventoryActionsWorld.Item(InventoryActionsWorld.KnownResource)));
        Assert.ThrowsAny<Exception>(() => world.Adapter().HandleGive(wrong));

        var row = InventoryActionsWorld.Row(Give(world, new[] { reference }));
        Assert.Equal(reference, RuntimeJson.Entity(row.GetProperty("target")));
        Assert.Equal(1, row.GetProperty("quantity").GetInt32());
        Assert.Equal(1, row.GetProperty("target_count").GetInt32());
    }

    [Fact]
    public void the_contract_rows_and_shapes_agree_with_the_runtime_registration()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();
        var adapter = world.Adapter();

        // The rows this provider declares: give and consume. `drop` declares none and has no body: the node list
        // has no drop node, so its binding row was deleted (ruling 110.5) and the implementation went with it
        // (ruling 133.3).
        var rows = InventoryActionContract.Rows(adapter.HandleGive, adapter.HandleConsume);
        Assert.Equal(2, rows.Count);
        var handlers = InventoryActionContract.Handlers(adapter.HandleGive, adapter.HandleConsume);
        var shapes = InventoryActionContract.Shapes(adapter.HandleGive, adapter.HandleConsume);
        var support = InventoryActionContract.Support(adapter.HandleGive, adapter.HandleConsume);
        Assert.Equal(2, handlers.Count);
        Assert.Equal(2, shapes.Count);
        Assert.Equal(2, support.Count);
        foreach (var handler in handlers.Keys)
        {
            Assert.True(shapes.ContainsKey(handler), "A declared handler has no shape: " + handler);
            Assert.Contains(support,
                row => row.RequiredPermissions.Contains(InventoryActionContract.WritePermission, StringComparer.Ordinal));
        }
        // Every binding row resolves to a capability and a handler the tables really carry, which is the check the
        // runtime itself performs at registration.
        var json = RuntimeJson.From(rows[0]).GetRawText();
        Assert.Contains(InventoryActionContract.GiveBinding, json, StringComparison.Ordinal);
        Assert.Contains(InventoryActionContract.GiveCapability, json, StringComparison.Ordinal);
        Assert.Contains(InventoryActionContract.GiveHandler, json, StringComparison.Ordinal);
    }

    [Fact]
    public void a_session_with_no_resolver_is_refused_at_construction()
    {
        ResetGame();
        using var world = new InventoryActionsWorld();
        world.Start();

        Assert.Throws<ArgumentNullException>(() => new InventoryActionAdapter(world.Kernel, () => true, null!, world.PlayerOf, world.Reports.Add));
        Assert.Throws<ArgumentNullException>(() => new InventoryActionAdapter(null!, () => true, (string _, out uint id) => { id = 0; return true; }, world.PlayerOf, world.Reports.Add));
        Assert.Throws<ArgumentNullException>(() => new InventoryActionAdapter(world.Kernel, () => true, (string _, out uint id) => { id = 0; return true; }, null!, world.Reports.Add));
    }
}
