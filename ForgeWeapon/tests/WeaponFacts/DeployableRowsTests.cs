using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.WeaponFacts;

/// <summary>The declaration half of the deployed-device facts the node list adds: one shot, one depletion and one
/// detonation. Every port asserted here is one the native half really reads, and the enum schemas are asserted
/// against the framework's own set table, so a member this build cannot produce fails here rather than at a plan.</summary>
public sealed class DeployableRowsTests
{
    private static readonly (string Id, string[] Outputs)[] Rows = new[]
    {
        (WeaponDeployableFactsContract.FiredCapability, new[] { "next", "device", "actor", "equipment_kind", "equipment_action", "ammo" }),
        (WeaponDeployableFactsContract.AmmoDepletedCapability, new[] { "next", "device", "actor" }),
        (WeaponDeployableFactsContract.DetonatedCapability, new[] { "next", "device", "position" })
    };

    /// <summary>The three rows, their ports in order, and the fact that each is a host-executed trigger with no
    /// inputs: a fact row is observed on the machine that owns the world and never walked into.</summary>
    [Fact]
    public void TheThreeRowsCarryTheirOwnPorts()
    {
        var declared = WeaponDeployableFactsContract.Capabilities();
        Assert.Equal(Rows.Select(row => row.Id).ToArray(),
            declared.Select(row => row.GetProperty("id").GetString()).ToArray());
        for (var index = 0; index < Rows.Length; index++)
        {
            var graph = declared[index].GetProperty("graph");
            Assert.Equal("host", graph.GetProperty("execution").GetString());
            Assert.Equal("trigger", declared[index].GetProperty("kind").GetString());
            Assert.Empty(graph.GetProperty("inputs").EnumerateArray());
            Assert.Equal(Rows[index].Outputs,
                graph.GetProperty("outputs").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray());
            Assert.Equal(new[] { "weapon", "tool", "consumable" },
                graph.GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()).ToArray());
            Assert.Equal(ModuleDefinition.ProviderId, declared[index].GetProperty("owner").GetString());
        }
    }

    /// <summary>The two enum ports name schema sets the framework carries, and the fired row's own kind set is
    /// the two shells this package hooks — nothing declared here is a shell no fact can report.</summary>
    [Fact]
    public void TheEnumPortsNameRealSetsWithTheMembersThisBuildProduces()
    {
        var fired = WeaponDeployableFactsContract.Capabilities()[0].GetProperty("graph").GetProperty("outputs");
        var kind = fired.EnumerateArray().Single(port => port.GetProperty("id").GetString() == "equipment_kind");
        Assert.Equal(WeaponDeployableFactsContract.EquipmentKindSchema, kind.GetProperty("schema").GetString());
        Assert.Equal(new[] { "sentry_gun", "glue_gun" }, WeaponDeployableFactsContract.EquipmentKinds.ToArray());
        var action = fired.EnumerateArray().Single(port => port.GetProperty("id").GetString() == "equipment_action");
        // The action port is the catalog's own `equipment_action` set, and `primary` is the member a trigger pull
        // is; the schema name is written out rather than read back from the same document, so a renamed set fails
        // here instead of resolving to a member nothing carries.
        Assert.Equal("equipment_action", action.GetProperty("schema").GetString());
        Assert.True(action.GetProperty("nullable").GetBoolean());
    }

    /// <summary>Every row is observe-only and carries the deployable read permission: this package commands no
    /// sentry, no mine and no glue gun, and a fact row that claimed a handler would be a promise without a body.</summary>
    [Fact]
    public void EveryRowIsAnObserveBinding()
    {
        var bindings = WeaponDeployableFactsContract.Bindings();
        Assert.Equal(3, bindings.Count);
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = RuntimeJson.From(bindings[index]);
            Assert.Equal(Rows[index].Id, binding.GetProperty("capabilityId").GetString());
            Assert.Equal(WeaponDeployableFactsContract.Rows[index].Binding, binding.GetProperty("id").GetString());
            Assert.Equal(ModuleDefinition.ProviderId, binding.GetProperty("providerId").GetString());
            Assert.Equal("observe", binding.GetProperty("role").GetString());
            Assert.StartsWith(ModuleDefinition.ProviderId + ".binding.", binding.GetProperty("id").GetString(), StringComparison.Ordinal);
            var support = WeaponDeployableFactsContract.Support()[index];
            Assert.Equal(WeaponDeployableFactsContract.Rows[index].Binding, support.BindingId);
            Assert.Equal(new[] { ModuleDefinition.DeployableReadPermission }, support.RequiredPermissions.ToArray());
        }
    }

    /// <summary>An absent optional port stays absent rather than carrying a placeholder: the detonation row's
    /// position is required because a device that detonated always has one, while the shot's actor and ammunition
    /// are genuinely optional — a glue gun spends no ammunition and an ownerless device acts for nobody.</summary>
    [Fact]
    public void TheOptionalPortsAreTheOnesTheNativeHalfCanReallyOmit()
    {
        var fired = WeaponDeployableFactsContract.Capabilities()[0].GetProperty("graph").GetProperty("outputs");
        Assert.True(fired.EnumerateArray().Single(port => port.GetProperty("id").GetString() == "actor").GetProperty("nullable").GetBoolean());
        Assert.True(fired.EnumerateArray().Single(port => port.GetProperty("id").GetString() == "ammo").GetProperty("nullable").GetBoolean());
        var depleted = WeaponDeployableFactsContract.Capabilities()[1].GetProperty("graph").GetProperty("outputs");
        Assert.True(depleted.EnumerateArray().Single(port => port.GetProperty("id").GetString() == "actor").GetProperty("nullable").GetBoolean());
        var detonated = WeaponDeployableFactsContract.Capabilities()[2].GetProperty("graph").GetProperty("outputs");
        var position = detonated.EnumerateArray().Single(port => port.GetProperty("id").GetString() == "position");
        Assert.Equal("vector3", position.GetProperty("type").GetString());
        Assert.Equal("m", position.GetProperty("unit").GetString());
        Assert.False(position.TryGetProperty("nullable", out _));
    }
}
