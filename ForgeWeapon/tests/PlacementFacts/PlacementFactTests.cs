using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.PlacementFacts;

/// <summary>
/// The observation half of the placement family: what one published placement and one published recall carry.
///
/// The facts here are built by the contract's own output builders — the same two the native adapter calls — so a
/// case fails if a port the observer fills is not the one the row declares, if a kind is published as a name
/// instead of its compiled index, or if a port this machine could not read is carried as null rather than left out.
/// The runtime resolves that index back to its member name at the handler boundary, which is why the index is what
/// a fact carries and the name is what a plan reads.
///
/// What these cases cannot do without a plan is make the kernel itself validate the payload against the row it
/// claims: a fact is checked only where a plan consumes it, and this fixture registers no plan. The port set is
/// therefore asserted against the declaration directly, port for port, in both directions.
/// </summary>
[Trait("Category", "PlacementFacts")]
public sealed class PlacementFactTests
{
    /// <summary>The declared value ports of the two rows: `next` is execution flow and carries no value, so a fact
    /// never writes it and it is left out of every comparison here.</summary>
    private static readonly string[] DeployPorts = { "actor", "deployed", "equipment_kind", "position" };
    private static readonly string[] RecallPorts = { "actor", "deployed", "equipment_kind" };

    /// <summary>A placement names its actor, its device and its kind, and the kind is the member's compiled index:
    /// `0` is `sentry_gun` and `1` is `mine`, which is the whole reason the port exists — one node, one vocabulary,
    /// two shells.</summary>
    [Fact]
    public void a_placement_names_its_actor_its_device_and_its_kind()
    {
        var player = Reference("gtfo.player:a");
        var device = Reference("gtfo.equipment:deployed:1");

        var outputs = WeaponPlacementContract.DeployOutputs(player, device,
            WeaponPlacementContract.KindIndex(WeaponPlacementContract.SentryGunKind), new[] { 1d, 2d, 3d });

        Assert.Equal(player.Id, Reference(outputs, "actor"));
        Assert.Equal(device.Id, Reference(outputs, "deployed"));
        Assert.Equal(0, outputs.GetProperty("equipment_kind").GetInt32());
        Assert.Equal("sentry_gun", WeaponPlacementContract.EquipmentKinds[0]);
        Assert.Equal(new[] { 1d, 2d, 3d },
            outputs.GetProperty("position").EnumerateArray().Select(value => value.GetDouble()).ToArray());
    }

    /// <summary>The mine's placement is the same ports under its own member. Nothing about the row changes, which
    /// is what lets an author read one node and switch on the shell.</summary>
    [Fact]
    public void a_mine_placement_is_the_same_shape_under_its_own_member()
    {
        var outputs = WeaponPlacementContract.DeployOutputs(null, Reference("gtfo.equipment:deployed:2"),
            WeaponPlacementContract.KindIndex(WeaponPlacementContract.MineKind), null);

        Assert.Equal(1, outputs.GetProperty("equipment_kind").GetInt32());
        Assert.Equal("mine", WeaponPlacementContract.EquipmentKinds[1]);
        // The actor and the place are the two ports a machine can genuinely not have: an ownerless device acts for
        // nobody, and a placement that reported no place has none. Both are absent rather than null.
        Assert.False(outputs.TryGetProperty("actor", out _));
        Assert.False(outputs.TryGetProperty("position", out _));
    }

    /// <summary>A recall is the same three ports without a place: the actor who picked the device up and the device
    /// that left the world.</summary>
    [Fact]
    public void a_recall_names_its_actor_its_device_and_its_kind()
    {
        var player = Reference("gtfo.player:a");
        var device = Reference("gtfo.equipment:deployed:1");

        var outputs = WeaponPlacementContract.RecallOutputs(player, device,
            WeaponPlacementContract.KindIndex(WeaponPlacementContract.MineKind));

        Assert.Equal(player.Id, Reference(outputs, "actor"));
        Assert.Equal(device.Id, Reference(outputs, "deployed"));
        Assert.Equal(1, outputs.GetProperty("equipment_kind").GetInt32());
        Assert.False(outputs.TryGetProperty("position", out _));
    }

    /// <summary>The two output builders write exactly the ports their rows declare: no more, which would be a port
    /// a plan cannot read; no fewer, which would be a declared port nothing ever fills.</summary>
    [Fact]
    public void each_builder_writes_exactly_its_rows_ports()
    {
        var player = Reference("gtfo.player:a");
        var device = Reference("gtfo.equipment:deployed:1");
        var full = WeaponPlacementContract.DeployOutputs(player, device, 0, new[] { 1d, 2d, 3d });
        var bare = WeaponPlacementContract.DeployOutputs(null, device, null, null);
        var recall = WeaponPlacementContract.RecallOutputs(player, device, 0);

        Assert.Equal(DeployPorts, Names(full));
        Assert.Equal(new[] { "deployed" }, Names(bare));
        Assert.Equal(RecallPorts, Names(recall));
        // A recall with neither an actor nor a kind is still a recall: the device is the one port it always has.
        Assert.Equal(new[] { "deployed" }, Names(WeaponPlacementContract.RecallOutputs(null, device, null)));
    }

    /// <summary>A device is what a placement is about, so it is the one port every fact carries. The builder
    /// refuses a fact without one rather than publishing a fact a plan could not act on.</summary>
    [Fact]
    public void a_fact_without_its_device_cannot_be_built()
    {
        Assert.Throws<ArgumentNullException>(() => WeaponPlacementContract.DeployOutputs(null, null!, null, null));
        Assert.Throws<ArgumentNullException>(() => WeaponPlacementContract.RecallOutputs(null, null!, null));
    }

    /// <summary>Both rows are observe-only bindings under the deployable read permission, and the fixture's
    /// registration is accepted: this package commands no sentry and no mine, and a fact row that claimed a handler
    /// would be a promise without a body.</summary>
    [Fact]
    public void both_rows_are_observe_bindings_under_the_deployable_read()
    {
        using var world = new PlacementWorld();
        var manifest = JsonDocument.Parse(world.Kernel.ExportManifest()).RootElement;
        var bindings = manifest.GetProperty("registry").GetProperty("bindings").EnumerateArray().ToArray();
        var support = manifest.GetProperty("bindingSupport").EnumerateArray().ToArray();

        Assert.Equal(2, bindings.Length);
        foreach (var binding in bindings)
        {
            Assert.Equal("observe", binding.GetProperty("role").GetString());
            Assert.Equal("implemented", binding.GetProperty("status").GetString());
            Assert.Equal(ModuleDefinition.ProviderId, binding.GetProperty("providerId").GetString());
            var row = support.Single(row => row.GetProperty("bindingId").GetString() == binding.GetProperty("id").GetString());
            Assert.Equal("implementation-only", row.GetProperty("verification").GetString());
            Assert.Equal(new[] { WeaponPlacementContract.DeployableReadPermission },
                row.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ToArray());
        }
        Assert.Equal(new[] { WeaponPlacementContract.DeployBinding, WeaponPlacementContract.RecallBinding },
            bindings.Select(binding => binding.GetProperty("id").GetString()).ToArray());
    }

    /// <summary>The fixture registers the contract's own rows and the kernel accepts them: the registration is
    /// itself the declaration check, so reaching a started runtime means both rows are well formed. The recall row
    /// is always this package's own; the placement row is re-declared only until integration plants the fragment,
    /// because the framework's trigger contract already owns that id and a second declaration is a conflict.</summary>
    [Fact]
    public void the_registration_is_accepted_and_the_rows_are_the_contracts()
    {
        using var world = new PlacementWorld();
        world.Start();

        var registered = JsonDocument.Parse(world.Kernel.ExportManifest()).RootElement
            .GetProperty("registry").GetProperty("capabilities").EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()).ToArray();
        Assert.Contains(WeaponPlacementContract.DeployCapability, registered);
        Assert.Contains(WeaponPlacementContract.RecallCapability, registered);
        Assert.Equal(RecallPorts, Ports(world.Row(WeaponPlacementContract.RecallCapability)));
    }

    private static string[] Names(JsonElement outputs)
        => outputs.EnumerateObject().Select(property => property.Name).ToArray();

    /// <summary>The declared value ports of a row: `next` is the execution continuation a fact never writes.</summary>
    private static string[] Ports(JsonElement row)
        => row.GetProperty("graph").GetProperty("outputs").EnumerateArray()
            .Select(port => port.GetProperty("id").GetString()!)
            .Where(id => id != "next").ToArray();

    private static string Reference(JsonElement outputs, string port)
        => outputs.GetProperty(port).GetProperty("id").GetString()!;

    private static EntityReference Reference(string id) => new(id, PlacementWorld.WorldEpoch, 1);
}
