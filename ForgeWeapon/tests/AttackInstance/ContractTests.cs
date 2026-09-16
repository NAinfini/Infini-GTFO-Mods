using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.AttackInstance;

/// <summary>The declaration half of the slice: every row the catalog publishes is carried as its own document,
/// and the integration batch is what moves them into the framework's trigger contract and registers the binding
/// rows. These cases assert the declaration against the catalog's own port list, so a drifted port fails
/// here.</summary>
public sealed class ContractTests
{
    /// <summary>The three ids the slice's row in `slices.tsv` names, in the order it names them, each with the
    /// output ports the catalog declares. The ports are written out rather than re-read from the same documents
    /// they are declared in: a case that compared the contract with itself would prove nothing.</summary>
    private static readonly (string Id, string[] Ports)[] CatalogRows =
    {
        ("forge.trigger.combat.burst_started", new[] { "next", "source", "equipment", "count" }),
        ("forge.trigger.combat.burst_ended", new[] { "next", "source", "equipment", "count" }),
        ("forge.trigger.combat.dry_fire", new[] { "next", "actor", "equipment" })
    };

    [Fact]
    public void EveryCatalogRowIsDeclaredWithTheCatalogsOwnPorts()
    {
        Assert.Equal(CatalogRows.Select(row => row.Id).ToArray(), AttackInstanceContract.CapabilityIds.ToArray());
        foreach (var (id, ports) in CatalogRows)
        {
            var declared = AttackInstanceContract.OutputPorts(id);
            Assert.Equal(ports, declared.Select(port => port.Split(':')[0]).ToArray());
        }
    }

    /// <summary>The three ports that carry more than a type — the equipment port and the burst count — are
    /// declared as the catalog declares them.</summary>
    [Fact]
    public void TheRowsCarryTheCatalogsOwnCarriers()
    {
        Assert.Equal(new[] { "next:execution", "source:entity", "equipment:entity", "count:integer" },
            AttackInstanceContract.OutputPorts(AttackInstanceContract.BurstStartedCapability));
        Assert.Equal(new[] { "next:execution", "source:entity", "equipment:entity", "count:integer" },
            AttackInstanceContract.OutputPorts(AttackInstanceContract.BurstEndedCapability));
        Assert.Equal(new[] { "next:execution", "actor:entity", "equipment:entity" },
            AttackInstanceContract.OutputPorts(AttackInstanceContract.DryFireCapability));
    }

    /// <summary>One binding per declared row: the declaration set and the binding set are the same set, and a
    /// binding with no body would advertise a promise this package does not keep.</summary>
    [Fact]
    public void EveryDeclaredRowCarriesExactlyOneBinding()
    {
        Assert.Equal(AttackInstanceContract.CapabilityIds.Count, AttackInstanceContract.Bindings().Count);
        Assert.Equal(AttackInstanceContract.CapabilityIds.Count, AttackInstanceContract.Support().Count);
        Assert.Equal(AttackInstanceContract.CapabilityIds.OrderBy(id => id, StringComparer.Ordinal),
            AttackInstanceContract.Bindings()
                .Select(row => RuntimeJson.From(row).GetProperty("capabilityId").GetString()!)
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>Every binding names the provider's own namespace, the catalog capability and an `observe` role,
    /// which is what the registry checks at registration.</summary>
    [Fact]
    public void EveryBindingIsAnObserveRowOfThisProvider()
    {
        foreach (var row in AttackInstanceContract.Bindings())
        {
            var binding = RuntimeJson.From(row);
            var id = binding.GetProperty("id").GetString();
            Assert.StartsWith(ModuleDefinition.ProviderId + ".binding.", id, StringComparison.Ordinal);
            Assert.Equal(ModuleDefinition.ProviderId, binding.GetProperty("providerId").GetString());
            Assert.Contains(binding.GetProperty("capabilityId").GetString(),
                AttackInstanceContract.CapabilityIds);
            Assert.Equal("observe", binding.GetProperty("role").GetString());
            Assert.Equal("implemented", binding.GetProperty("status").GetString());
        }
    }

    /// <summary>Every support row requires the one combat-read permission and names a registered binding: the
    /// registry refuses a support row without a binding and a binding without a support row.</summary>
    [Fact]
    public void EverySupportRowAsksForTheCombatReadPermissionItUses()
    {
        var bindings = AttackInstanceContract.Bindings()
            .Select(row => RuntimeJson.From(row).GetProperty("id").GetString()!).ToArray();
        var support = AttackInstanceContract.Support();
        Assert.Equal(bindings.OrderBy(id => id, StringComparer.Ordinal),
            support.Select(row => row.BindingId).OrderBy(id => id, StringComparer.Ordinal));
        foreach (var row in support)
        {
            Assert.Equal("implementation-only", row.Verification);
            Assert.Equal(new[] { ModuleDefinition.CombatReadPermission }, row.RequiredPermissions);
        }
    }

    /// <summary>The declared documents are the catalog rows, and each one is a JSON document whose own `id` is the
    /// capability it is carried under.</summary>
    [Fact]
    public void EveryDocumentIsARowOfItsOwnId()
    {
        Assert.Equal(AttackInstanceContract.CapabilityIds.Count, AttackInstanceContract.Documents.Length);
        foreach (var id in AttackInstanceContract.CapabilityIds)
        {
            var row = AttackInstanceContract.Row(id);
            Assert.Equal(id, row.GetProperty("id").GetString());
            Assert.Equal(ModuleDefinition.ProviderId, row.GetProperty("owner").GetString());
            Assert.Equal("trigger", row.GetProperty("kind").GetString());
            Assert.Equal("host", row.GetProperty("graph").GetProperty("execution").GetString());
            Assert.Empty(row.GetProperty("graph").GetProperty("inputs").EnumerateArray());
        }
    }
}
