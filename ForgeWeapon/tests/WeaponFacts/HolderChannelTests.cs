using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.WeaponFacts;

/// <summary>
/// The channel half: the holder provider's registration and the `owner` tier it declares, exercised through a
/// real kernel. Registration is itself the contract check — a row whose shape does not resolve, a binding whose
/// handler has no shape, a support row without a binding or an owner-session entry for a capability the module
/// does not declare are all refused by the registry — so reaching a manifest here means the registered shape is
/// the declared one, and the cases below read that manifest back for the facts that matter to a plan author.
/// </summary>
public sealed class HolderChannelTests
{
    /// <summary>Every row is registered at the `owner` tier and every binding is this provider's. A row that lost
    /// its tier would be a reload or a shot the host performs against another player's inventory, which is the one
    /// failure the tier exists to prevent.</summary>
    [Fact]
    public void BothRowsAreRegisteredAtTheOwnerTier()
    {
        using var world = new HolderWorld();
        world.Register(_ => "2");
        world.Start();
        Assert.True(world.ProviderRegistered());
        Assert.Equal("owner", world.Row(WeaponHolderActionsContract.ReloadCapability).GetProperty("graph").GetProperty("execution").GetString());
        Assert.Equal("owner", world.Row(WeaponHolderActionsContract.ClipSetCapability).GetProperty("graph").GetProperty("execution").GetString());
        Assert.Equal("owner", world.Row(WeaponHolderActionsContract.AutoFireCapability).GetProperty("graph").GetProperty("execution").GetString());
        var bindings = world.RegisteredBindings();
        Assert.Contains(WeaponHolderActionsContract.ReloadBinding, bindings);
        Assert.Contains(WeaponHolderActionsContract.ClipSetBinding, bindings);
        Assert.Contains(WeaponHolderActionsContract.AutoFireBinding, bindings);
        Assert.Equal(3, bindings.Count(id => id.StartsWith(WeaponHolderChannelContract.ProviderId + ".binding.", StringComparison.Ordinal)));
    }

    /// <summary>The owner-session table is the provider's own answer about the subject a step names, so it is
    /// keyed by capability and its keys are exactly the rows the module declares: a declaration without its
    /// resolver is a row the tier could only refuse.</summary>
    [Fact]
    public void TheOwnerSessionTableCoversEveryDeclaredRow()
    {
        var module = WeaponHolderActionsContract.Module(_ => "2", _ => CommandResult.Succeeded(RuntimeJson.EmptyObject),
            _ => CommandResult.Succeeded(RuntimeJson.EmptyObject),
            _ => CommandResult.Succeeded(RuntimeJson.EmptyObject));
        var declared = WeaponHolderActionsContract.Capabilities()
            .Select(row => row.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(declared, module.OwnerSessions!.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(module.OwnerSessions!.Count, module.Handlers.Count);
        Assert.Equal(module.OwnerSessions!.Count, module.Shapes.Count);
    }

    /// <summary>A registration with no resolver is a real shape rather than a half-built one: the rows and the
    /// bodies are declared, the tier is still `owner`, and the kernel accepts it because nothing about a
    /// declaration depends on an answer the process cannot give yet.</summary>
    [Fact]
    public void ARegistrationWithoutAResolverStillDeclaresBothRows()
    {
        using var world = new HolderWorld();
        world.Register(null);
        world.Start();
        var declared = WeaponHolderActionsContract.Module(null);
        Assert.Empty(declared.OwnerSessions!);
        Assert.Equal(3, WeaponHolderActionsContract.Capabilities().Count);
        Assert.Equal("owner", world.Row(WeaponHolderActionsContract.ReloadCapability).GetProperty("graph").GetProperty("execution").GetString());
    }

    /// <summary>Disposing the provider takes its rows and its owner-session answers with it: the same kernel that
    /// accepted the registration no longer carries the provider, which is what makes a world change or a package
    /// unload leave no route behind.</summary>
    [Fact]
    public void DisposingTheProviderRemovesItFromTheManifest()
    {
        var world = new HolderWorld();
        world.Register(_ => "2");
        world.Start();
        Assert.True(world.ProviderRegistered());
        world.Dispose();
        Assert.False(world.ProviderRegistered());
    }

    /// <summary>The tier's own entry points refuse what they cannot serve, by name: an owner command for a plan
    /// this machine does not hold, and an owner command naming a step index no plan has. Neither reaches a
    /// handler, which is what the call counter proves.</summary>
    [Fact]
    public void OwnerCommandsAreRefusedByNameBeforeAnyHandlerRuns()
    {
        using var world = new HolderWorld();
        world.Register(_ => "2");
        world.Start();
        var inputs = RuntimeJson.From(new Dictionary<string, object?>
        {
            ["equipment"] = world.Equipment(),
            ["holder"] = world.Player("gtfo.player:1")
        });
        var unknownPlan = world.Kernel.ExecuteOwnerCommand("no.such.plan", 1, "c1", "e1", "gtfo.world:4", 1, inputs);
        Assert.Equal(CommandStatuses.Rejected, unknownPlan.Status);
        Assert.Equal("plan-unloaded", unknownPlan.Code);
        var unknownStep = world.Kernel.ExecuteOwnerCommand("no.such.plan", 9, "c1", "e1", "gtfo.world:4", 1, inputs);
        Assert.Equal("plan-unloaded", unknownStep.Code);
        Assert.Empty(world.Calls);
    }

    /// <summary>Every refusal this channel owns is a distinct, stable code: they are what a plan author reads, so
    /// a collision between two of them would report one failure as another.</summary>
    [Fact]
    public void TheRefusalCodesAreDistinct()
    {
        var codes = new[]
        {
            WeaponHolderChannelContract.NoHolderCode, WeaponHolderChannelContract.NotAddressedCode,
            WeaponHolderChannelContract.UnknownActionCode, WeaponHolderChannelContract.StaleEquipmentCode,
            WeaponHolderChannelContract.ClipOverCapacityCode, WeaponHolderChannelContract.UnsupportedPolicyCode,
            WeaponHolderChannelContract.ReloadRefusedCode
        };
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", code));
    }

    /// <summary>The capability ids the resolver table is built from are exactly the rows the contract declares, in
    /// declaration order, and a capability it does not declare answers no kind: the table and the rows cannot
    /// drift.</summary>
    [Fact]
    public void TheResolverListAndTheRowsAreTheSameSet()
    {
        var declared = WeaponHolderActionsContract.Capabilities()
            .Select(row => row.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(declared, WeaponHolderChannelContract.Capabilities.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(WeaponHolderActionsContract.ReloadCapability, WeaponHolderChannelContract.Capabilities[0]);
        Assert.Equal(WeaponHolderActionsContract.ClipSetCapability, WeaponHolderChannelContract.Capabilities[1]);
        Assert.Equal(WeaponHolderActionsContract.AutoFireCapability, WeaponHolderChannelContract.Capabilities[2]);
    }
}
