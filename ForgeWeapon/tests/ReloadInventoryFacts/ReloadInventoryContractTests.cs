using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.ReloadInventoryFacts;

/// <summary>The declarative half of this family: the nine capability ids, the binding rows that name them and the
/// shipped rows the runtime's own trigger contract declares for them. These cases are the ones that fail if a row
/// is renamed, a binding loses its capability, or a shipped row drifts from the port list the observers actually
/// fill.</summary>
[Trait("Category", "ReloadInventoryFacts")]
public sealed class ReloadInventoryContractTests
{
    private static readonly IReadOnlyList<string> Capabilities = ReloadInventoryContract.CapabilityIds;

    /// <summary>Each row's output ports, as the catalog declares them and as the fragment is asserted against. A
    /// produced fact may carry fewer of these than the list, because a port the machine cannot read is left out; it
    /// may never carry one that is not here.</summary>
    private static readonly Dictionary<string, string[]> Ports = new()
    {
        [ReloadInventoryContract.ReloadStartedCapability] = new[] { "next", "actor", "equipment" },
        [ReloadInventoryContract.ReloadTransferredCapability] = new[] { "next", "actor", "equipment", "amount" },
        [ReloadInventoryContract.ReloadCompletedCapability] = new[] { "next", "actor", "equipment" },
        [ReloadInventoryContract.RefilledCapability] = new[] { "next", "actor", "equipment", "amount" },
        [ReloadInventoryContract.StackChangedCapability] = new[] { "next", "actor", "item", "count", "delta" },
        [ReloadInventoryContract.PickedUpCapability] = new[] { "next", "actor", "item" },
        [ReloadInventoryContract.DroppedCapability] = new[] { "next", "actor", "item", "position" },
        [ReloadInventoryContract.UseStartedCapability] = new[] { "next", "actor", "equipment" },
        [ReloadInventoryContract.UseFailedCapability] = new[] { "next", "actor", "equipment", "outcome", "reason" }
    };

    /// <summary>The dry-fire row the catalog pairs with this family's refused-use row. They are two different facts
    /// — an empty magazine is the ammunition gate's, any other refusal is this row's — so this family must never be
    /// the one that declares the dry-fire id. The two rows the node list does not carry — an interrupted reload and
    /// a tool's energy change — are asserted absent here, so a re-declared one fails rather than returning quietly.
    /// </summary>
    [Fact]
    public void the_nine_capabilities_are_this_familys_own_ids_and_are_all_distinct()
    {
        Assert.Equal(9, Capabilities.Count);
        Assert.Equal(9, Capabilities.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Capabilities, id => Assert.StartsWith("forge.trigger.", id, StringComparison.Ordinal));
        Assert.DoesNotContain("forge.trigger.combat.dry_fire", Capabilities);
        Assert.DoesNotContain("forge.trigger.combat.reload_cancelled", Capabilities);
        Assert.DoesNotContain("forge.trigger.equipment.energy_changed", Capabilities);
        Assert.Equal("forge.trigger.combat.reload_started", Capabilities[0]);
        Assert.Equal("forge.trigger.equipment.use_failed", Capabilities[8]);
    }

    [Fact]
    public void every_capability_has_exactly_one_binding_and_one_support_row()
    {
        var bindings = ReloadInventoryContract.Bindings();
        var support = ReloadInventoryContract.Support();

        Assert.Equal(Capabilities.Count, bindings.Count);
        Assert.Equal(Capabilities.Count, support.Count);
        foreach (var capability in Capabilities)
        {
            var declared = bindings.Where(row => Capability(row) == capability).ToList();
            Assert.Single(declared);
            var id = BindingId(declared[0]);
            Assert.Single(support, row => row.BindingId == id);
            Assert.StartsWith(ModuleDefinition.ProviderId + ".binding.", id, StringComparison.Ordinal);
        }
        Assert.Equal(bindings.Count, bindings.Select(BindingId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void every_binding_is_observe_only_under_a_read_permission()
    {
        foreach (var row in ReloadInventoryContract.Bindings())
            Assert.Equal("observe", Json(row).GetProperty("role").GetString());
        foreach (var row in ReloadInventoryContract.Support())
        {
            Assert.Equal("implementation-only", row.Verification);
            var only = Assert.Single(row.RequiredPermissions);
            Assert.EndsWith(".read", only, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_shipped_rows_hold_one_row_per_capability_with_the_catalogs_ports()
    {
        var rows = TriggerContracts.Rows(Capabilities);

        Assert.Equal(Capabilities.Count, rows.Count);
        foreach (var row in rows)
        {
            var id = row.GetProperty("id").GetString()!;
            Assert.Contains(id, Capabilities);
            Assert.Equal("forge.contract.trigger", row.GetProperty("owner").GetString());
            Assert.Equal("trigger", row.GetProperty("kind").GetString());
            Assert.Equal("2.0.0", row.GetProperty("version").GetString());
            Assert.Equal("authoring-contract-only", row.GetProperty("parameters").GetProperty("support").GetString());
            var graph = row.GetProperty("graph");
            Assert.Equal("host", graph.GetProperty("execution").GetString());
            Assert.Empty(graph.GetProperty("inputs").EnumerateArray());
            Assert.Empty(graph.GetProperty("parameters").EnumerateArray());
            var ports = graph.GetProperty("outputs").EnumerateArray()
                .Select(port => port.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(Ports[id], ports);
        }
    }

    /// <summary>The dropped row's position is the one port this family declares optional, because a slot clear
    /// usually destroys the instance before the position can be read. A required port is a port a plan must wire, so
    /// this is the difference between a plan that loads and one that cannot.</summary>
    [Fact]
    public void only_the_dropped_rows_position_is_optional()
    {
        foreach (var row in TriggerContracts.Rows(Capabilities))
        {
            var id = row.GetProperty("id").GetString()!;
            foreach (var port in row.GetProperty("graph").GetProperty("outputs").EnumerateArray())
            {
                var name = port.GetProperty("id").GetString()!;
                var optional = port.TryGetProperty("optional", out var flag) && flag.GetBoolean();
                if (optional)
                    Assert.True(id == ReloadInventoryContract.DroppedCapability && name == "position",
                        "unexpected optional port " + id + "." + name);
            }
        }
    }

    /// <summary>Every fact this family can publish is checked against the row it claims: the binding must be one of
    /// the nine, and every port the fact carries must be one the row declares. A fact carrying a port its row does
    /// not have would be the drift this asserts against.</summary>
    [Fact]
    public void a_published_fact_carries_only_ports_its_row_declares()
    {
        var world = new FactsWorld();
        world.Start();
        var (owner, _) = world.Player();
        var item = new FakeItem { Clip = 30 };
        world.Life(item, owner);
        var backpack = world.Backpack(owner, FactsWorld.PooledSlots);
        using var _ = world;
        backpack.Empty("GearStandard");
        world.Inventory.Reconcile(backpack);
        backpack.Hold("GearStandard", FactsWorld.Item(item));
        world.Inventory.Reconcile(backpack);
        world.Reload.FlagChanged(item, reloading: true);
        item.Clip = 31;
        world.Reload.MagazineRead(item);
        world.Reload.FlagChanged(item, reloading: false);
        backpack.Pool["GearStandard"] = 12;
        world.Inventory.Reconcile(backpack);
        world.Inventory.Refused(item);
        backpack.Clear("GearStandard");
        world.Inventory.Reconcile(backpack);

        Assert.NotEmpty(world.Published);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in world.Published)
        {
            var capability = CapabilityFor(fact.BindingId);
            Assert.True(capability != null, "unmapped binding " + fact.BindingId);
            covered.Add(capability!);
            foreach (var port in fact.Outputs.EnumerateObject())
                Assert.Contains(port.Name, Ports[capability!]);
        }
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            ReloadInventoryContract.ReloadStartedCapability,
            ReloadInventoryContract.ReloadTransferredCapability,
            ReloadInventoryContract.ReloadCompletedCapability,
            ReloadInventoryContract.PickedUpCapability,
            ReloadInventoryContract.StackChangedCapability,
            ReloadInventoryContract.RefilledCapability,
            ReloadInventoryContract.UseFailedCapability,
            ReloadInventoryContract.DroppedCapability
        }, covered);
    }

    private static string? CapabilityFor(string binding)
    {
        foreach (var row in ReloadInventoryContract.Bindings())
            if (BindingId(row) == binding) return Capability(row);
        return null;
    }

    private static string Capability(object row) => Json(row).GetProperty("capabilityId").GetString()!;
    private static string BindingId(object row) => Json(row).GetProperty("id").GetString()!;
    private static JsonElement Json(object row) => ForgeRuntime.Framework.RuntimeJson.From(row);
}
