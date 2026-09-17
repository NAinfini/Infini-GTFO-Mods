using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.WeaponFacts;

/// <summary>The declaration half of the holder rows: the three owner-tier actions and the registration that makes
/// them executable. The registration is built by the production contract and handed to a real kernel, so these
/// cases assert against what the runtime accepted rather than against the text that asked for it.</summary>
public sealed class HolderRowsTests
{
    /// <summary>Each row's own ports, parameters and result field. The magazine pair writes a magazine and answers
    /// with the clip it left behind; the fire row has no field of its own, because one native `Fire` body is one
    /// shot.</summary>
    private static readonly (string Id, string[] Inputs, string[] Parameters, string? Field)[] Rows = new[]
    {
        (WeaponHolderActionsContract.ReloadCapability,
            new[] { "in", "equipment", "holder", "reload_profile" }, new[] { "chamber_policy", "transfer_policy" }, "clip"),
        (WeaponHolderActionsContract.ClipSetCapability,
            new[] { "in", "equipment", "holder", "amount" }, new[] { "clip_policy" }, "clip"),
        (WeaponHolderActionsContract.AutoFireCapability,
            new[] { "in", "equipment", "holder" }, new[] { "required_state" }, null)
    };

    /// <summary>Every row is declared with the ports and parameters the node list's actions need, and all three
    /// declare the `owner` tier: the whole point of the family is that the write happens on the holder's machine,
    /// so a row that lost its tier would be a reload or a shot the host performs against somebody else's
    /// inventory.</summary>
    [Fact]
    public void BothRowsAreOwnerTierActionsWithTheirOwnPorts()
    {
        var declared = WeaponHolderActionsContract.Capabilities();
        Assert.Equal(Rows.Select(row => row.Id).ToArray(), declared.Select(row => row.GetProperty("id").GetString()).ToArray());
        for (var index = 0; index < Rows.Length; index++)
        {
            var (id, inputs, parameters, _) = Rows[index];
            var graph = declared[index].GetProperty("graph");
            Assert.Equal("owner", graph.GetProperty("execution").GetString());
            Assert.Equal("action", declared[index].GetProperty("kind").GetString());
            Assert.Equal(inputs, graph.GetProperty("inputs").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray());
            Assert.Equal(parameters, graph.GetProperty("parameters").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray());
            // Every action declares its recipient, and this one's subject is the equipment the write lands on.
            Assert.Equal("equipment", graph.GetProperty("recipients").GetProperty("input").GetString());
            Assert.Equal("result", graph.GetProperty("recipients").GetProperty("result").GetString());
            Assert.Equal(id, declared[index].GetProperty("id").GetString());
        }
    }

    /// <summary>The result rows carry the four fixed columns every action result carries, in the kernel's own
    /// order, and then this row's own field when it declares one.</summary>
    [Fact]
    public void BothResultRowsCarryTheFixedColumnsThenTheClip()
    {
        var declared = WeaponHolderActionsContract.Capabilities();
        for (var index = 0; index < Rows.Length; index++)
        {
            var result = declared[index].GetProperty("graph").GetProperty("outputs").EnumerateArray()
                .Single(port => port.GetProperty("id").GetString() == "result");
            var expected = Rows[index].Field is { } own
                ? new[] { "target", "status", "committed", "code", own }
                : new[] { "target", "status", "committed", "code" };
            Assert.Equal(expected,
                result.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("id").GetString()).ToArray());
        }
    }

    /// <summary>One binding per declared row, each in this provider's own namespace, each `execute` and each
    /// naming the canonical capability — which is what the registry checks when the module is handed to it.</summary>
    [Fact]
    public void EveryRowCarriesOneExecuteBinding()
    {
        var bindings = WeaponHolderActionsContract.Bindings();
        Assert.Equal(Rows.Length, bindings.Count);
        foreach (var row in bindings)
        {
            var binding = RuntimeJson.From(row);
            Assert.StartsWith(WeaponHolderChannelContract.ProviderId + ".binding.", binding.GetProperty("id").GetString(), StringComparison.Ordinal);
            Assert.Equal(WeaponHolderChannelContract.ProviderId, binding.GetProperty("providerId").GetString());
            Assert.Equal("execute", binding.GetProperty("role").GetString());
            Assert.Equal("implemented", binding.GetProperty("status").GetString());
            Assert.Contains(binding.GetProperty("capabilityId").GetString(),
                WeaponHolderActionsContract.Capabilities().Select(capability => capability.GetProperty("id").GetString()).ToArray());
        }
    }

    /// <summary>The owner-session table is keyed by capability rather than by provider, and it covers exactly the
    /// rows this registration declares: a row whose resolver is missing is a row the tier can only refuse, and
    /// this registration never declares one. A registration that supplies one body supplies the resolver for both
    /// rows, because the host asks about a step before any body runs.</summary>
    [Fact]
    public void TheRegistrationAnswersForBothRows()
    {
        using var world = new HolderWorld();
        world.Register(_ => Session);
        world.Start();
        Assert.Equal(new[] { WeaponHolderActionsContract.ReloadCapability, WeaponHolderActionsContract.ClipSetCapability,
            WeaponHolderActionsContract.AutoFireCapability },
            WeaponHolderChannelContract.Capabilities.ToArray());
        var module = WeaponHolderActionsContract.Module(_ => Session,
            context => CommandResult.Succeeded(RuntimeJson.EmptyObject),
            context => CommandResult.Succeeded(RuntimeJson.EmptyObject),
            context => CommandResult.Succeeded(RuntimeJson.EmptyObject));
        foreach (var capability in WeaponHolderChannelContract.Capabilities)
            Assert.True(module.OwnerSessions!.ContainsKey(capability), capability + " has no owner-session resolver.");
        Assert.Equal(3, module.Handlers.Count);
        Assert.Equal(3, module.Shapes.Count);
    }

    /// <summary>A registration that supplies one body declares only that row's binding: the other row's shape
    /// would have no handler, which the registry refuses, so a body and its row travel together.</summary>
    [Fact]
    public void OneSuppliedBodyDeclaresOneBinding()
    {
        var one = WeaponHolderActionsContract.Module(_ => Session, context => CommandResult.Succeeded(RuntimeJson.EmptyObject));
        Assert.Single(one.Handlers);
        Assert.Equal(3, one.OwnerSessions!.Count);
        Assert.Single(WeaponHolderActionsContract.Module(_ => Session, null, context => CommandResult.Succeeded(RuntimeJson.EmptyObject)).Handlers);
        Assert.Single(WeaponHolderActionsContract.Module(_ => Session, null, null,
            context => CommandResult.Succeeded(RuntimeJson.EmptyObject)).Handlers);
    }

    /// <summary>The holder provider declares no package dependency: it reads equipment identities through the
    /// kernel's entity resolver at dispatch time, where an absent provider is a named refusal rather than a load
    /// order the registration has to declare.</summary>
    [Fact]
    public void TheHolderProviderDeclaresNoPackageDependency()
    {
        var module = WeaponHolderActionsContract.Module(_ => Session);
        var seed = System.Text.Json.JsonDocument.Parse(module.RegistryJson).RootElement;
        var provider = seed.GetProperty("providers").EnumerateArray().Single();
        Assert.Equal(WeaponHolderChannelContract.ProviderId, provider.GetProperty("id").GetString());
        Assert.Equal("native", provider.GetProperty("kind").GetString());
        Assert.Empty(provider.GetProperty("dependencies").EnumerateArray());
    }

    private const string Session = "2";

    /// <summary>A registration without a resolver declares no owner step it could address, and a registration
    /// without a body keeps the rows but answers neither: the two facts travel independently, so a declaration-only
    /// module is a real shape rather than a half-built one.</summary>
    [Fact]
    public void ARegistrationWithoutResolversOrHandlersIsStillWellFormed()
    {
        var declaredOnly = WeaponHolderActionsContract.Module(null);
        Assert.Empty(declaredOnly.OwnerSessions!);
        Assert.Empty(declaredOnly.Handlers);
        var withResolver = WeaponHolderActionsContract.Module(_ => "2");
        Assert.Equal(3, withResolver.OwnerSessions!.Count);
        Assert.Empty(withResolver.Handlers);
    }

    /// <summary>The registered rows are the ones a plan compiles against: the kernel accepted every binding, every
    /// capability came back with the `owner` tier, and the shapes resolved — a handler whose ports the graph does
    /// not declare is refused at registration, so reaching this point is itself the assertion.</summary>
    [Fact]
    public void TheKernelAcceptsBothRowsAtTheirOwnTier()
    {
        using var world = new HolderWorld();
        world.Register(_ => Session);
        world.Start();
        Assert.Contains(WeaponHolderActionsContract.ReloadBinding, world.RegisteredBindings());
        Assert.Contains(WeaponHolderActionsContract.ClipSetBinding, world.RegisteredBindings());
        Assert.Contains(WeaponHolderActionsContract.AutoFireBinding, world.RegisteredBindings());
        Assert.Equal("owner", world.Row(WeaponHolderActionsContract.ReloadCapability).GetProperty("graph").GetProperty("execution").GetString());
        Assert.Equal("owner", world.Row(WeaponHolderActionsContract.ClipSetCapability).GetProperty("graph").GetProperty("execution").GetString());
        Assert.Equal("owner", world.Row(WeaponHolderActionsContract.AutoFireCapability).GetProperty("graph").GetProperty("execution").GetString());
    }

    /// <summary>The structural parameters are the native behaviour set, not a wish list: `chamber_policy` has the
    /// two members the reload row can refuse between and `clip_policy` the two the clip write implements.</summary>
    [Fact]
    public void TheStructuralParametersNameTheNativeBehaviours()
    {
        var reload = WeaponHolderActionsContract.Capabilities()[0].GetProperty("graph").GetProperty("parameters")
            .EnumerateArray().ToDictionary(port => port.GetProperty("id").GetString()!, port => port);
        Assert.Equal(new[] { "keep", "drop" }, reload["chamber_policy"].GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal(new[] { "magazine", "reserve" }, reload["transfer_policy"].GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.True(reload["chamber_policy"].GetProperty("required").GetBoolean());
        var clip = WeaponHolderActionsContract.Capabilities()[1].GetProperty("graph").GetProperty("parameters")
            .EnumerateArray().Single(port => port.GetProperty("id").GetString() == "clip_policy");
        Assert.Equal(new[] { "set", "fill" }, clip.GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToArray());
    }

    /// <summary>`KindOf` is the contract's own mapping from a capability to the action it is; a capability this
    /// contract does not carry answers null rather than defaulting to one of the two.</summary>
    [Fact]
    public void KindOfAnswersOnlyForItsOwnRows()
    {
        Assert.Equal(WeaponHolderActionsContract.HolderAction.Reload, WeaponHolderActionsContract.KindOf(WeaponHolderActionsContract.ReloadCapability));
        Assert.Equal(WeaponHolderActionsContract.HolderAction.ClipSet, WeaponHolderActionsContract.KindOf(WeaponHolderActionsContract.ClipSetCapability));
        Assert.Null(WeaponHolderActionsContract.KindOf("forge.action.weapon.fire_rate"));
    }
}
