using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.WeaponFacts;

/// <summary>The declaration half of the melee-hit row: the one row the node list's `e-w-melee` adds, with the
/// ports the swing's own hit entry really reads. The two ports that are nullable here are the two the game really
/// leaves open — a damageable that is not a body part, and a swing whose state is neither of the light nor the
/// charged hit states — and the required ones are the ones a hit cannot be named without.</summary>
public sealed class MeleeHitRowsTests
{
    private static readonly string[] ExpectedOutputs = { "next", "source", "target", "limb", "damage", "charged" };

    /// <summary>The row is a host-executed trigger of the weapon provider with the combat family's actor ports:
    /// `source` is the attacker and `target` the thing that was hit, exactly as `hit_candidate` spells them.</summary>
    [Fact]
    public void TheMeleeHitRowCarriesItsOwnPorts()
    {
        var row = WeaponMeleeHitContract.Row();
        Assert.Equal(WeaponMeleeHitContract.MeleeHitCapability, row.GetProperty("id").GetString());
        Assert.Equal(ModuleDefinition.ProviderId, row.GetProperty("owner").GetString());
        Assert.Equal("trigger", row.GetProperty("kind").GetString());
        var graph = row.GetProperty("graph");
        Assert.Equal("host", graph.GetProperty("execution").GetString());
        Assert.Empty(graph.GetProperty("inputs").EnumerateArray());
        Assert.Empty(graph.GetProperty("parameters").EnumerateArray());
        Assert.Equal(ExpectedOutputs,
            graph.GetProperty("outputs").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray());
        Assert.Equal(WeaponMeleeHitContract.Domains,
            graph.GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()).ToArray());
    }

    /// <summary>The damage port is the hit's own damage in hit points and is always present; the limb index and
    /// the charge flag are the two the native half can really omit, and an absent optional port stays absent
    /// rather than carrying a placeholder.</summary>
    [Fact]
    public void TheDamagePortIsRequiredAndTheTwoOpenPortsAreNot()
    {
        var outputs = WeaponMeleeHitContract.Row().GetProperty("graph").GetProperty("outputs");
        var damage = outputs.EnumerateArray().Single(port => port.GetProperty("id").GetString() == "damage");
        Assert.Equal("number", damage.GetProperty("type").GetString());
        Assert.Equal("hp", damage.GetProperty("unit").GetString());
        Assert.False(damage.TryGetProperty("nullable", out _));
        foreach (var id in new[] { "limb", "charged" })
        {
            var port = outputs.EnumerateArray().Single(candidate => candidate.GetProperty("id").GetString() == id);
            Assert.True(port.GetProperty("nullable").GetBoolean());
        }
        Assert.Equal("integer",
            outputs.EnumerateArray().Single(port => port.GetProperty("id").GetString() == "limb").GetProperty("type").GetString());
        Assert.Equal("boolean",
            outputs.EnumerateArray().Single(port => port.GetProperty("id").GetString() == "charged").GetProperty("type").GetString());
    }

    /// <summary>The row is observe-only and carries the combat read permission: this package commands no melee
    /// swing, and a fact row that claimed a handler would be a promise without a body.</summary>
    [Fact]
    public void TheRowIsAnObserveBindingWithTheCombatReadPermission()
    {
        var bindings = WeaponMeleeHitContract.Bindings();
        var binding = RuntimeJson.From(Assert.Single(bindings));
        Assert.Equal(WeaponMeleeHitContract.MeleeHitBinding, binding.GetProperty("id").GetString());
        Assert.Equal(WeaponMeleeHitContract.MeleeHitCapability, binding.GetProperty("capabilityId").GetString());
        Assert.Equal(ModuleDefinition.ProviderId, binding.GetProperty("providerId").GetString());
        Assert.Equal(WeaponMeleeHitContract.MeleeHitHandler, binding.GetProperty("handler").GetString());
        Assert.Equal("observe", binding.GetProperty("role").GetString());
        Assert.Equal("implemented", binding.GetProperty("status").GetString());
        var support = Assert.Single(WeaponMeleeHitContract.Support());
        Assert.Equal(WeaponMeleeHitContract.MeleeHitBinding, support.BindingId);
        Assert.Equal(new[] { WeaponMeleeHitContract.CombatReadPermission }, support.RequiredPermissions.ToArray());
        // The permission is the package's own combat read rather than a literal, so a renamed constant fails here.
        Assert.Equal(ModuleDefinition.CombatReadPermission, WeaponMeleeHitContract.CombatReadPermission);
    }

    /// <summary>The declared port list and the helper a plan-side reader uses are the same order: a drift between
    /// the document and `OutputPorts` is what would silently rewire a plan.</summary>
    [Fact]
    public void ThePortHelperAgreesWithTheDocument()
    {
        Assert.Equal(new[] { "next:execution", "source:entity", "target:entity", "limb:integer?", "damage:number:hp", "charged:boolean?" },
            WeaponMeleeHitContract.OutputPorts().ToArray());
    }

    /// <summary>The wrapped capability list carries the same row the accessor parses — same id, same ports, same
    /// order — so the integration batch appends one object and the site converges on one shape. The wrapper
    /// re-serializes the document, so the comparison is by value rather than by the literal's own whitespace.</summary>
    [Fact]
    public void TheCapabilityListCarriesTheSameRow()
    {
        var wrapped = RuntimeJson.From(Assert.Single(WeaponMeleeHitContract.Capabilities()));
        var row = WeaponMeleeHitContract.Row();
        Assert.Equal(row.GetProperty("id").GetString(), wrapped.GetProperty("id").GetString());
        Assert.Equal(WeaponMeleeHitContract.MeleeHitCapability, WeaponMeleeHitContract.CapabilityId);
        Assert.Equal(
            row.GetProperty("graph").GetProperty("outputs").EnumerateArray()
                .Select(port => port.GetProperty("id").GetString()).ToArray(),
            wrapped.GetProperty("graph").GetProperty("outputs").EnumerateArray()
                .Select(port => port.GetProperty("id").GetString()).ToArray());
    }
}
