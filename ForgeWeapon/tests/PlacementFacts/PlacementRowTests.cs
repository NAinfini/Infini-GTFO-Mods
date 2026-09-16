using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeWeapon.Tests.PlacementFacts;

/// <summary>
/// The declaration half of the placement family: the two rows, the ports each one carries and the one enum set
/// that tells a sentry from a mine.
///
/// The rows are asserted from <see cref="WeaponPlacementContract.RowsJson"/> — this package's own artifact, and
/// the fragment the integration batch plants into the runtime's trigger contract — so a case here fails if a port
/// is renamed, a row loses its host tier, or the kind port is dropped from the placement row. That the runtime
/// then accepts facts carrying those ports is <see cref="PlacementFactTests"/>'s half, and the two together are
/// what keeps the declaration and the observation from drifting apart.
/// </summary>
[Trait("Category", "PlacementFacts")]
public sealed class PlacementRowTests
{
    private static readonly string[] DeployPorts = { "next", "actor", "deployed", "equipment_kind", "position" };
    private static readonly string[] RecallPorts = { "next", "actor", "deployed", "equipment_kind" };

    /// <summary>The two rows exist, in the order they are published, and they are the node list's own ids: the
    /// placement row is where `e-sentry-place` and the placement half of `e-mine` land, and the recall row is the
    /// other half of `e-sentry-place`.</summary>
    [Fact]
    public void the_two_rows_are_the_node_lists_placement_and_recall()
    {
        Assert.Equal("forge.trigger.equipment.deploy_completed", WeaponPlacementContract.DeployCapability);
        Assert.Equal("forge.trigger.equipment.recall_completed", WeaponPlacementContract.RecallCapability);
        Assert.Equal(2, WeaponPlacementContract.Rows.Count);
        Assert.Equal(WeaponPlacementContract.DeployCapability, WeaponPlacementContract.Rows[0].Capability);
        Assert.Equal(WeaponPlacementContract.RecallCapability, WeaponPlacementContract.Rows[1].Capability);
        Assert.Equal(ModuleDefinition.DeployCompletedBinding, WeaponPlacementContract.DeployBinding);
        Assert.Equal(ModuleDefinition.RecallCompletedBinding, WeaponPlacementContract.RecallBinding);

        var deploy = Row(WeaponPlacementContract.DeployCapability);
        Assert.Equal("trigger", deploy.GetProperty("kind").GetString());
        Assert.Equal("host", deploy.GetProperty("graph").GetProperty("execution").GetString());
        Assert.Empty(deploy.GetProperty("graph").GetProperty("inputs").EnumerateArray());
        Assert.Equal(new[] { "weapon", "tool", "consumable" },
            deploy.GetProperty("graph").GetProperty("domains").EnumerateArray().Select(d => d.GetString()).ToArray());
        Assert.Equal("authoring-contract-only", deploy.GetProperty("parameters").GetProperty("support").GetString());
    }

    /// <summary>The placement row carries exactly the node list's ports plus the kind, in the order the observer
    /// writes them; the recall row carries the same set without a position, because a device being picked up has no
    /// place to report that its placement did not already report.</summary>
    [Fact]
    public void the_rows_carry_the_ports_the_observer_fills()
    {
        Assert.Equal(DeployPorts, Ports(Row(WeaponPlacementContract.DeployCapability)));
        Assert.Equal(RecallPorts, Ports(Row(WeaponPlacementContract.RecallCapability)));
    }

    /// <summary>The kind port names the framework's own `equipment_kind` set, and it is optional: a machine that
    /// cannot name the shell still publishes the placement it saw rather than dropping the fact.</summary>
    [Fact]
    public void the_kind_port_is_the_frameworks_enum_and_is_optional()
    {
        foreach (var capability in new[] { WeaponPlacementContract.DeployCapability, WeaponPlacementContract.RecallCapability })
        {
            var kind = Port(Row(capability), "equipment_kind");
            Assert.Equal("enum", kind.GetProperty("type").GetString());
            Assert.Equal(WeaponPlacementContract.EquipmentKindSchema, kind.GetProperty("schema").GetString());
            Assert.True(kind.GetProperty("nullable").GetBoolean());
        }
    }

    /// <summary>The placement row's position is the catalog's own optional port, and it stays optional: a device
    /// whose transform cannot be read still publishes the placement without a place.</summary>
    [Fact]
    public void the_placement_position_is_optional_and_is_a_metre_vector()
    {
        var position = Port(Row(WeaponPlacementContract.DeployCapability), "position");
        Assert.Equal("vector3", position.GetProperty("type").GetString());
        Assert.Equal("m", position.GetProperty("unit").GetString());
        Assert.True(position.GetProperty("optional").GetBoolean());
        Assert.DoesNotContain("position", Ports(Row(WeaponPlacementContract.RecallCapability)));
    }

    /// <summary>The three shells this package names, and the compiled index of each. The order is the wire
    /// contract — a fact carries an index, not a name — so the whole list is spelled out here rather than derived:
    /// inserting a member anywhere but the end would shift every index a running plan already reads. The framework
    /// half of the set is registered by integration; this case pins the half this package publishes against.</summary>
    [Fact]
    public void the_kind_table_is_the_compiled_order_of_the_set()
    {
        Assert.Equal(new[] { "sentry_gun", "mine", "glue_gun" }, WeaponPlacementContract.EquipmentKinds.ToArray());
        Assert.Equal(0, WeaponPlacementContract.KindIndex(WeaponPlacementContract.SentryGunKind));
        Assert.Equal(1, WeaponPlacementContract.KindIndex(WeaponPlacementContract.MineKind));
        Assert.Equal(2, WeaponPlacementContract.KindIndex(WeaponPlacementContract.GlueGunKind));
        Assert.Null(WeaponPlacementContract.KindIndex("glue"));
        Assert.Null(WeaponPlacementContract.KindIndex("sentry"));
        Assert.Null(WeaponPlacementContract.KindIndex(""));
        Assert.Null(WeaponPlacementContract.KindIndex(null));
    }

    /// <summary>Every fact this family publishes carries only ports its own row declares, and the two rows are the
    /// only ones it publishes under. The builders below are the ones the native adapter calls, so the check is on
    /// the same outputs a running machine hands the kernel.</summary>
    [Fact]
    public void every_published_fact_carries_only_its_rows_ports()
    {
        var player = new ForgeRuntime.Framework.EntityReference("gtfo.player:a", PlacementWorld.WorldEpoch, 1);
        var device = new ForgeRuntime.Framework.EntityReference("gtfo.equipment:deployed:1", PlacementWorld.WorldEpoch, 1);
        var facts = new (string Capability, System.Text.Json.JsonElement Outputs)[]
        {
            (WeaponPlacementContract.DeployCapability,
                WeaponPlacementContract.DeployOutputs(player, device, 0, new[] { 1d, 2d, 3d })),
            (WeaponPlacementContract.DeployCapability, WeaponPlacementContract.DeployOutputs(null, device, null, null)),
            (WeaponPlacementContract.RecallCapability, WeaponPlacementContract.RecallOutputs(player, device, 1)),
            (WeaponPlacementContract.RecallCapability, WeaponPlacementContract.RecallOutputs(null, device, null))
        };

        var ports = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [WeaponPlacementContract.DeployCapability] = DeployPorts,
            [WeaponPlacementContract.RecallCapability] = RecallPorts
        };
        foreach (var (capability, outputs) in facts)
            foreach (var port in outputs.EnumerateObject())
                Assert.Contains(port.Name, ports[capability]);
    }

    /// <summary>This package's declaration is the one the runtime ends up holding: the placement row's own ports
    /// are on the registered capability once integration has planted the fragment. Before that the framework's
    /// older declaration is what a fact is checked against, and <see cref="PlacementWorld.DeclaresRow"/> says which
    /// world a case is in rather than this case failing for an integration that has not run yet.</summary>
    [Fact]
    public void the_registered_placement_row_is_this_packages_shape()
    {
        using var world = new PlacementWorld();
        if (!world.DeclaresRow) return;
        Assert.Equal(DeployPorts, Ports(world.Row(WeaponPlacementContract.DeployCapability)));
        Assert.Equal(RecallPorts, Ports(world.Row(WeaponPlacementContract.RecallCapability)));
    }

    private static JsonElement Row(string capability)
    {
        foreach (var row in WeaponPlacementContract.RowsJson())
            if (row.GetProperty("id").GetString() == capability) return row;
        throw new InvalidOperationException("The fragment declares no row " + capability + ".");
    }

    private static string[] Ports(JsonElement row)
        => row.GetProperty("graph").GetProperty("outputs").EnumerateArray()
            .Select(port => port.GetProperty("id").GetString()!).ToArray();

    private static JsonElement Port(JsonElement row, string id)
    {
        foreach (var port in row.GetProperty("graph").GetProperty("outputs").EnumerateArray())
            if (port.GetProperty("id").GetString() == id) return port;
        throw new InvalidOperationException("The row declares no port " + id + ".");
    }
}
