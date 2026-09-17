using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.WeaponOverride;

/// <summary>
/// The four numerical rows as decisions: which port becomes which value, what the value becomes in the native
/// field, which ports are refused because this build has no destination for them, and what a request with
/// nothing in it does.
///
/// The cases drive the same entry points the native adapter calls, over a request frame built the way the loader
/// builds one, so a renamed port or a changed conversion fails here. The write itself is the applier's and is not
/// covered here: it needs the game's own allocator, and a case that faked one would be asserting against the fake.
/// </summary>
[Trait("Category", "WeaponOverride")]
public sealed class WeaponActionTests
{
    private static readonly EntityReference Equipment = OverrideWorld.Equipment();

    private static ForgeWeapon.WeaponActionOutcome FireRate(params (string Port, object? Value)[] inputs)
        => WeaponActionRuntime.FireRate(OverrideWorld.Context(new { }, inputs));

    private static ForgeWeapon.WeaponActionOutcome Spread(object parameters, params (string Port, object? Value)[] inputs)
        => WeaponActionRuntime.Spread(OverrideWorld.Context(parameters, inputs));

    private static ForgeWeapon.WeaponActionOutcome Recoil(params (string Port, object? Value)[] inputs)
        => WeaponActionRuntime.Recoil(OverrideWorld.Context(new { }, inputs));

    private static ForgeWeapon.WeaponActionOutcome Property(object parameters,
        params (string Port, object? Value)[] inputs)
        => WeaponActionRuntime.Property(OverrideWorld.Context(parameters, inputs));

    [Fact]
    public void a_rate_is_a_frequency_and_the_native_field_is_its_reciprocal()
    {
        var outcome = FireRate(("equipment", OverrideWorld.Entity(Equipment)), ("rate", 8d), ("duration", 40));
        Assert.Equal("", outcome.Code);
        Assert.Equal(Equipment, outcome.Equipment);
        Assert.Equal("fire_rate", outcome.Fields.Single().Name);
        // The ledger stores the frequency the plan asked for, the way every other row's value travels: the
        // reciprocal the block actually holds is produced at the write, which is the half that knows the field is
        // `ShotDelay` and the only half that can resolve an `add` against the instance's own rate.
        Assert.Equal(8d, outcome.Fields.Single().Value, 12);
        Assert.Equal(WeaponOverrideOperation.Set, outcome.Fields.Single().Operation);
        Assert.Equal(0.125, WeaponActionRuntime.ShotDelayFromRate(8d), 12);
        Assert.Equal(8d, WeaponActionRuntime.RateFromShotDelay(0.125), 12);
    }

    [Fact]
    public void a_rate_that_is_not_a_frequency_is_refused()
    {
        Assert.Equal(WeaponActionRuntime.InputUnsupportedCode,
            FireRate(("equipment", OverrideWorld.Entity(Equipment)), ("rate", 0d)).Code);
        Assert.Equal(WeaponActionRuntime.InputUnsupportedCode,
            FireRate(("equipment", OverrideWorld.Entity(Equipment)), ("rate", -2d)).Code);
        // A missing rate port is a plan the loader already refuses; the decision still answers with a code
        // instead of a field set, because a handler is reachable on its own too.
        Assert.Equal(WeaponActionRuntime.ModeUnsupportedCode,
            FireRate(("equipment", OverrideWorld.Entity(Equipment))).Code);
        Assert.Equal(WeaponOverrideLedger.StaleEquipmentCode, FireRate(("rate", 4d)).Code);
    }

    [Fact]
    public void all_four_recoil_values_become_recoil_fields()
    {
        var outcome = Recoil(("equipment", OverrideWorld.Entity(Equipment)), ("horizontal", 1.5),
            ("vertical", 2.5), ("recovery", 30d), ("camera_kick", 0.5));
        Assert.Equal("", outcome.Code);
        Assert.Equal(new[] { "recoil_horizontal", "recoil_vertical", "recoil_recovery", "recoil_camera_kick" },
            outcome.Fields.Select(field => field.Name));
    }

    [Fact]
    public void a_recoil_request_that_names_no_value_is_refused_rather_than_applied_as_a_no_op()
    {
        var outcome = Recoil(("equipment", OverrideWorld.Entity(Equipment)));
        Assert.Equal(WeaponActionRuntime.InputUnsupportedCode, outcome.Code);
        Assert.Empty(outcome.Fields);
    }

    [Fact]
    public void a_spread_request_carries_the_cone_and_the_two_scales()
    {
        var outcome = Spread(new { pattern = "fixed" }, ("equipment", OverrideWorld.Entity(Equipment)),
            ("cone", 12d), ("movement_scale", 1.25), ("aim_scale", 0.5));
        Assert.Equal("", outcome.Code);
        Assert.Equal(new[] { "spread_cone", "spread_movement_scale", "spread_aim_scale" },
            outcome.Fields.Select(field => field.Name));
    }

    [Fact]
    public void the_spread_ports_this_build_has_no_destination_for_are_refused_by_name()
    {
        // A spiral cone is a pattern the shipped cone has no variant of, so the row refuses the choice instead of
        // serving a different one.
        Assert.Equal(WeaponActionRuntime.ModeUnsupportedCode,
            Spread(new { pattern = "spiral" }, ("equipment", OverrideWorld.Entity(Equipment)), ("cone", 5d)).Code);
        // The native spread is computed, not sampled from a seed, so a seeded request is refused rather than
        // accepted and ignored.
        Assert.Equal(WeaponActionRuntime.InputUnsupportedCode,
            Spread(new { pattern = "random" }, ("equipment", OverrideWorld.Entity(Equipment)), ("seed", 3)).Code);
    }

    [Fact]
    public void the_random_and_fixed_patterns_are_the_same_native_cone()
    {
        foreach (var pattern in new[] { "random", "fixed" })
        {
            var outcome = Spread(new { pattern }, ("equipment", OverrideWorld.Entity(Equipment)), ("cone", 9d));
            Assert.Equal("", outcome.Code);
            Assert.Equal(9d, outcome.Fields.Single().Value);
        }
    }

    [Fact]
    public void a_negative_cone_is_refused()
    {
        Assert.Equal(WeaponActionRuntime.InputUnsupportedCode,
            Spread(new { pattern = "fixed" }, ("equipment", OverrideWorld.Entity(Equipment)), ("cone", -1d)).Code);
    }

    [Theory]
    [InlineData("fire_rate", WeaponActionRuntime.WeaponFamily.Bullet, true)]
    [InlineData("fire_rate", WeaponActionRuntime.WeaponFamily.Shotgun, true)]
    [InlineData("spread_cone", WeaponActionRuntime.WeaponFamily.Shotgun, true)]
    [InlineData("spread_cone", WeaponActionRuntime.WeaponFamily.Bullet, false)]
    [InlineData("spread_movement_scale", WeaponActionRuntime.WeaponFamily.Shotgun, true)]
    [InlineData("burst_count", WeaponActionRuntime.WeaponFamily.Burst, true)]
    [InlineData("burst_count", WeaponActionRuntime.WeaponFamily.Bullet, false)]
    [InlineData("recoil_horizontal", WeaponActionRuntime.WeaponFamily.Shotgun, true)]
    [InlineData("spread_pattern", WeaponActionRuntime.WeaponFamily.Shotgun, false)]
    [InlineData("not_a_field", WeaponActionRuntime.WeaponFamily.Bullet, false)]
    public void a_field_has_a_destination_exactly_where_the_shipped_code_reads_one(string field,
        WeaponActionRuntime.WeaponFamily family, bool expected)
    {
        Assert.Equal(expected, WeaponActionRuntime.HasDestination(field, family, out _));
    }

    [Fact]
    public void the_recoil_fields_are_the_ones_that_need_the_recoil_block()
    {
        Assert.True(WeaponActionRuntime.HasDestination("recoil_recovery", WeaponActionRuntime.WeaponFamily.Bullet, out var recoil));
        Assert.True(recoil);
        Assert.True(WeaponActionRuntime.HasDestination("fire_rate", WeaponActionRuntime.WeaponFamily.Bullet, out var plain));
        Assert.False(plain);
    }

    [Theory]
    [InlineData("fire_rate", "add")]
    [InlineData("burst_count", "subtract")]
    [InlineData("spread_cone", "set")]
    [InlineData("recoil_camera_kick", "add")]
    public void a_property_request_carries_the_named_field_and_its_operation(string field, string operation)
    {
        var outcome = Property(new { field, operation }, ("equipment", OverrideWorld.Entity(Equipment)),
            ("value", 2d), ("duration", 60));
        Assert.Equal("", outcome.Code);
        var accepted = outcome.Fields.Single();
        Assert.Equal(field, accepted.Name);
        Assert.Equal(2d, accepted.Value, 12);
        Assert.True(WeaponOverrideLedger.TryParseOperation(operation, out var expected));
        Assert.Equal(expected, accepted.Operation);
    }

    [Fact]
    public void a_property_field_with_no_write_point_is_refused_by_name()
    {
        // `damage` is a real block member and still not a served name: the shipped code's read of it is not
        // decoded, so this row refuses the name instead of writing a value nothing re-reads.
        Assert.Equal(WeaponOverrideLedger.UnknownFieldCode,
            Property(new { field = "damage", operation = "set" }, ("equipment", OverrideWorld.Entity(Equipment)),
                ("value", 5d)).Code);
        Assert.Equal(WeaponOverrideLedger.UnknownFieldCode,
            Property(new { field = "not_a_field", operation = "add" }, ("equipment", OverrideWorld.Entity(Equipment)),
                ("value", 5d)).Code);
    }

    [Fact]
    public void a_property_request_without_a_field_or_with_an_operation_outside_the_three_is_refused()
    {
        // A missing `field` and an operation the write path has no arithmetic for are both the row's own mode,
        // so both are refused under it rather than as a bad value.
        Assert.Equal(WeaponActionRuntime.ModeUnsupportedCode,
            Property(new { operation = "set" }, ("equipment", OverrideWorld.Entity(Equipment)), ("value", 1d)).Code);
        Assert.Equal(WeaponActionRuntime.ModeUnsupportedCode,
            Property(new { field = "fire_rate", operation = "multiply" },
                ("equipment", OverrideWorld.Entity(Equipment)), ("value", 1d)).Code);
        Assert.Equal(WeaponActionRuntime.ModeUnsupportedCode,
            Property(new { field = "fire_rate" }, ("equipment", OverrideWorld.Entity(Equipment)), ("value", 1d)).Code);
    }

    [Fact]
    public void a_property_value_that_is_not_a_number_and_a_negative_set_are_refused()
    {
        Assert.Equal(WeaponActionRuntime.InputUnsupportedCode,
            Property(new { field = "fire_rate", operation = "set" },
                ("equipment", OverrideWorld.Entity(Equipment))).Code);
        Assert.Equal(WeaponActionRuntime.InputUnsupportedCode,
            Property(new { field = "fire_rate", operation = "set" },
                ("equipment", OverrideWorld.Entity(Equipment)), ("value", -1d)).Code);
        // The same negative value is a subtraction when it is spelled as one, which is a change the block can
        // hold, so the operation is what decides and not the sign.
        var subtract = Property(new { field = "spread_cone", operation = "subtract" },
            ("equipment", OverrideWorld.Entity(Equipment)), ("value", 3d));
        Assert.Equal("", subtract.Code);
        Assert.Equal(WeaponOverrideOperation.Subtract, subtract.Fields.Single().Operation);
        Assert.Equal(WeaponOverrideLedger.StaleEquipmentCode,
            Property(new { field = "fire_rate", operation = "set" }, ("value", 1d)).Code);
    }

    [Fact]
    public void every_field_of_the_vocabulary_has_a_destination_on_at_least_one_family()
    {
        // `spread_cone` is the shotgun's own destination and `burst_count` the burst archetypes'. Every name in
        // the vocabulary therefore has to be a destination of at least one family, and the ones that are not
        // universal have to be refused by the families that cannot hold them.
        foreach (var name in WeaponOverrideLedger.Fields)
        {
            var families = Enum.GetValues<WeaponActionRuntime.WeaponFamily>()
                .Where(family => WeaponActionRuntime.HasDestination(name, family, out _)).ToArray();
            Assert.True(families.Length > 0, name);
        }
        Assert.Equal(new[] { WeaponActionRuntime.WeaponFamily.Shotgun },
            Enum.GetValues<WeaponActionRuntime.WeaponFamily>()
                .Where(family => WeaponActionRuntime.HasDestination("spread_cone", family, out _)));
        Assert.Equal(new[] { WeaponActionRuntime.WeaponFamily.Burst },
            Enum.GetValues<WeaponActionRuntime.WeaponFamily>()
                .Where(family => WeaponActionRuntime.HasDestination("burst_count", family, out _)));
    }
}
