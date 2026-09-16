using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.LoadoutPolicy;

/// <summary>The projection: which of the gears the game offers for a slot survive a policy, what falls back, and
/// what is reported. Each policy under test comes from the loader itself, so the filter runs against the same
/// object production would hand it — and no native gear, no game and no hook is involved.</summary>
public sealed class GearLoadoutFilterTests
{
    /// <summary>A stand-in for one offered gear: exactly the two facts the filter is allowed to read.</summary>
    private sealed class Gear
    {
        internal Gear(string? record, uint category) { Record = record; Category = category; }
        internal string? Record { get; }
        internal uint Category { get; }
        internal static GearLoadoutIdentity Identity(Gear gear) => new(gear.Record, gear.Category);
    }

    /// <summary>A one-entry-per-slot policy unless a case asks for another set: the other two slots carry ids no
    /// case offers, so each case drives exactly the slot it names.</summary>
    private static ForgeLoadoutPolicy Policy(PolicyInstall install, uint[] standard, uint[]? special = null, uint[]? gearClass = null)
    {
        install.WritePolicy("Pack", install.Canonical(PolicyInstall.Rundown,
            standard: standard, special: special ?? new uint[] { 30001 }, gearClass: gearClass ?? new uint[] { 30002 }));
        var snapshot = install.LoadWritten(out var rejected);
        Assert.True(snapshot.TryGet(PolicyInstall.Rundown, out var policy),
            "The fixture policy was not accepted: " + string.Join(" | ", rejected));
        return policy;
    }

    private static GearLoadoutDecision<Gear> Filter(LoadoutSlot slot, IReadOnlyList<Gear> offered, ForgeLoadoutPolicy policy)
        => GearLoadoutFilter.Filter(slot, offered, Gear.Identity, policy);

    /// <summary>The offline-record rule, on the same case data the `gear-block` mount's own suite uses: the plain
    /// decimal spelling of a block id, and nothing else.</summary>
    [Fact]
    public void the_gear_block_text_rule_matches_only_its_own_spelling()
    {
        var cases = new (string? Record, string? Id)[]
        {
            ("OfflineGear_ID_1", "1"),
            ("OfflineGear_ID_10001", "10001"),
            ("OfflineGear_ID_4294967295", "4294967295"),
            ("OfflineGear_ID_4294967296", null),
            ("OfflineGear_ID_010001", null),
            ("OfflineGear_ID_+10001", null),
            ("OfflineGear_ID_-10001", null),
            ("OfflineGear_ID_10001.0", null),
            ("OfflineGear_ID_10001 ", null),
            ("OfflineGear_ID_ 10001", null),
            ("OfflineGear_ID_0x10", null),
            ("OfflineGear_ID_", null),
            ("offlinegear_id_10001", null),
            ("10001", null),
            (null, null)
        };
        foreach (var (record, id) in cases)
            Assert.Equal(id, GearLoadoutFilter.OfflineGearBlockId(record));
    }

    [Fact]
    public void an_offline_record_matches_only_its_own_block_and_everything_else_is_dropped()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, new uint[] { 10001 });
        var offered = new List<Gear>
        {
            new("OfflineGear_ID_010001", 0),
            new("OfflineGear_ID_+10001", 0),
            new("OfflineGear_ID_10001.0", 0),
            new("OfflineGear_ID_10001 ", 0),
            new("OfflineGear_ID_10001", 0),
            new("10001", 0),
            new("OfflineGear_ID_", 0),
            new(null, 0)
        };

        var decision = Filter(LoadoutSlot.GearStandard, offered, policy);

        Assert.Equal(GearLoadoutOutcome.Applied, decision.Outcome);
        Assert.Same(offered[4], Assert.Single(decision.Keep));
        Assert.Contains("duplicates=0", decision.Diagnostics[0], StringComparison.Ordinal);
    }

    /// <summary>A workshop gear carries its own block id in the category component, which is the second route the
    /// audit names; a gear whose record and component name the same entry is still one entry.</summary>
    [Fact]
    public void a_category_component_is_an_identity_of_its_own()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, new uint[] { 10001 });
        var offered = new List<Gear> { new(null, 10001), new("OfflineGear_ID_777", 777), new(null, 0), new("OfflineGear_ID_10001", 10001) };

        var decision = Filter(LoadoutSlot.GearStandard, offered, policy);

        Assert.Equal(GearLoadoutOutcome.Applied, decision.Outcome);
        Assert.Equal(new[] { offered[0], offered[3] }, decision.Keep);
        Assert.Contains("kept=2 duplicates=1", decision.Diagnostics[0], StringComparison.Ordinal);
    }

    [Fact]
    public void an_offer_keeps_the_games_own_order_and_reports_what_it_dropped()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, PolicyInstall.StandardIds);
        var offered = new List<Gear>
        {
            new("OfflineGear_ID_34", 0),
            new("OfflineGear_ID_10003", 0),
            new("OfflineGear_ID_31", 0),
            new(null, 10001),
            new("OfflineGear_ID_10004", 10004),
            new("OfflineGear_ID_10002", 0)
        };

        var decision = Filter(LoadoutSlot.GearStandard, offered, policy);

        Assert.Equal(GearLoadoutOutcome.Applied, decision.Outcome);
        Assert.Equal(new[] { offered[1], offered[3], offered[4], offered[5] }, decision.Keep);
        Assert.Equal(new[]
        {
            "weapon.loadout-slot slot=GearStandard offered=6 kept=4 duplicates=0 dropped=2 ids=10003,10001,10004,10002",
            "weapon.loadout-vanilla-dropped count=2"
        }, decision.Diagnostics);
    }

    /// <summary>Two offers of one entry — an offline row and an account instance copy — are kept as two: which one
    /// the player owns is not this filter's decision, so it only counts them.</summary>
    [Fact]
    public void a_duplicated_offer_is_counted_and_never_removed()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, new uint[] { 10001 });
        var offered = new List<Gear> { new("OfflineGear_ID_10001", 0), new(null, 10001), new("OfflineGear_ID_10001", 10001) };

        var decision = Filter(LoadoutSlot.GearStandard, offered, policy);

        Assert.Equal(GearLoadoutOutcome.Applied, decision.Outcome);
        Assert.Equal(new[] { offered[0], offered[1], offered[2] }, decision.Keep);
        Assert.Contains("kept=3 duplicates=2 dropped=0", decision.Diagnostics[0], StringComparison.Ordinal);
    }

    [Fact]
    public void a_slot_outside_the_policy_is_returned_untouched()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, PolicyInstall.StandardIds);
        var offered = new List<Gear> { new("OfflineGear_ID_34", 0), new(null, 10001) };

        var decision = GearLoadoutFilter.Filter((LoadoutSlot)4, offered, Gear.Identity, policy);

        Assert.Equal(GearLoadoutOutcome.Returned, decision.Outcome);
        Assert.Equal(new[] { offered[0], offered[1] }, decision.Keep);
        Assert.Empty(decision.Diagnostics);
        Assert.False(decision.Filtered);
    }

    /// <summary>The safety property: one policy entry the offer does not contain withdraws the whole slot, so a
    /// partial list can never reach the game.</summary>
    [Fact]
    public void an_unrecognised_policy_entry_falls_the_whole_slot_back()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, new uint[] { 10001, 10002 });
        var offered = new List<Gear> { new("OfflineGear_ID_10001", 0), new("OfflineGear_ID_34", 0) };

        var decision = Filter(LoadoutSlot.GearStandard, offered, policy);

        Assert.Equal(GearLoadoutOutcome.Unmatched, decision.Outcome);
        Assert.Equal(new[] { offered[0], offered[1] }, decision.Keep);
        Assert.Equal("weapon.loadout-policy-unmatched slot=GearStandard missing=10002", Assert.Single(decision.Diagnostics));
    }

    /// <summary>A policy never produces an empty slot: an offer with nothing recognised hands the game's own list
    /// back, which for an empty offer is still that empty offer.</summary>
    [Fact]
    public void an_empty_offer_falls_back_rather_than_publishing_an_empty_slot()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, new uint[] { 10001 });
        var offered = new List<Gear>();

        var decision = Filter(LoadoutSlot.GearStandard, offered, policy);

        Assert.Equal(GearLoadoutOutcome.Unmatched, decision.Outcome);
        Assert.Empty(decision.Keep);
        Assert.Equal("weapon.loadout-policy-unmatched slot=GearStandard missing=10001", Assert.Single(decision.Diagnostics));
    }

    [Fact]
    public void a_failed_identity_read_returns_the_offer_and_names_the_reason()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, new uint[] { 10001 });
        var offered = new List<Gear> { new("OfflineGear_ID_10001", 0) };

        var decision = GearLoadoutFilter.Filter(LoadoutSlot.GearStandard, offered,
            _ => throw new InvalidOperationException("boom"), policy);

        Assert.Equal(GearLoadoutOutcome.Failed, decision.Outcome);
        Assert.Equal(new[] { offered[0] }, decision.Keep);
        Assert.Equal("weapon.loadout-policy-failed: InvalidOperationException: boom", Assert.Single(decision.Diagnostics));
    }

    [Fact]
    public void projecting_an_already_projected_offer_changes_nothing()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, new uint[] { 10001, 10002 });
        var offered = new List<Gear> { new("OfflineGear_ID_10001", 0), new("OfflineGear_ID_34", 0), new("OfflineGear_ID_10002", 0) };
        var first = Filter(LoadoutSlot.GearStandard, offered, policy);

        var again = Filter(LoadoutSlot.GearStandard, first.Keep.ToList(), policy);

        Assert.Equal(GearLoadoutOutcome.Applied, first.Outcome);
        Assert.Equal(GearLoadoutOutcome.Applied, again.Outcome);
        Assert.Equal(first.Keep, again.Keep);
    }

    /// <summary>Only the two weapon slots report the vanilla entries they dropped; a tool slot dropping an entry is
    /// not the "original primary and special weapons are zero" reading.</summary>
    [Fact]
    public void only_the_weapon_slots_report_dropped_vanilla_entries()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, new uint[] { 10001 }, gearClass: new uint[] { 10003 });
        var offered = new List<Gear> { new("OfflineGear_ID_10003", 0), new("OfflineGear_ID_9", 0) };

        var decision = Filter(LoadoutSlot.GearClass, offered, policy);

        Assert.Equal(GearLoadoutOutcome.Applied, decision.Outcome);
        Assert.Equal("weapon.loadout-slot slot=GearClass offered=2 kept=1 duplicates=0 dropped=1 ids=10003",
            Assert.Single(decision.Diagnostics));
    }

    /// <summary>The state a policy narrowing replaces: a captured list is written into in place, and the capture
    /// puts the original items back in their original order.</summary>
    [Fact]
    public void a_narrowed_list_can_be_restored_to_its_original_contents_and_order()
    {
        using var install = new PolicyInstall();
        var policy = Policy(install, new uint[] { 10001, 10002 });
        var live = new List<string> { "OfflineGear_ID_10001", "OfflineGear_ID_34", "OfflineGear_ID_10002" };
        var captured = GearLoadoutSlotSnapshot<string>.Capture(live);

        var decision = GearLoadoutFilter.Filter(LoadoutSlot.GearStandard, live,
            text => new GearLoadoutIdentity(text, 0), policy);
        Assert.Equal(GearLoadoutOutcome.Applied, decision.Outcome);
        decision.ApplyTo(live);
        Assert.Equal(new[] { "OfflineGear_ID_10001", "OfflineGear_ID_10002" }, live);

        captured.RestoreInto(live);

        Assert.Equal(new[] { "OfflineGear_ID_10001", "OfflineGear_ID_34", "OfflineGear_ID_10002" }, live);
        Assert.Equal(3, captured.Count);
    }
}
