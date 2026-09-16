using ForgeWeapon.Native;
using ForgeWeapon.Tests.LoadoutPolicy;
using Gear;
using Globals;
using Player;

namespace ForgeWeapon.Tests.NativeAdapter;

/// <summary>The loadout policy on the live gear pool, without a game: a real policy file read by the production
/// loader, the game's own offer and pool as fixture lists, and the session's two hook bodies called directly. What a
/// case asserts is what the hooks publish — the projected offer and the contents of the game's own pool lists.
///
/// The fixture never loads an IL2CPP assembly and never allocates an interop array: the two list types the
/// production hooks read are represented by <see cref="GearPoolList"/> for the pool the narrowing rewrites in place
/// and by a plain array for the offer the projection reads, which is the same data the hook copies out of the
/// game's array. The hook's own targets, priorities and patched signatures are frozen in
/// `tests/NativeLayout` against the shipped build.</summary>
public sealed class LoadoutRuntimeTests
{
    // Every case rebuilds the process-wide state the game keeps in statics — the gear pool, the loaded rundown and
    // the expedition gate — so no case ever inherits another one's world.
    public LoadoutRuntimeTests() => LoadoutWorld.Reset();

    [Fact]
    public void the_offer_is_projected_onto_the_policy_in_the_games_own_order()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();

        var projected = session.Offer(InventorySlot.GearStandard, LoadoutWorld.Offer(LoadoutWorld.OfferedStandard));

        Assert.NotNull(projected);
        Assert.Equal(LoadoutWorld.PolicyStandard, Blocks(projected!));
        Assert.Contains(world.Infos, line => line.StartsWith("weapon.loadout-policy-active rundown=", StringComparison.Ordinal));
        Assert.Contains(world.Infos, line => line == "weapon.loadout-slot slot=GearStandard offered=6 kept=4 duplicates=0 dropped=2 ids=10001,10002,10003,10004");
    }

    [Fact]
    public void a_slot_no_policy_covers_is_not_the_projections_business()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();

        Assert.Null(session.Offer(InventorySlot.GearMelee, LoadoutWorld.Offer(11, 12)));
        Assert.Null(session.Offer(InventorySlot.HackingTool, LoadoutWorld.Offer(2)));
        Assert.Empty(world.Infos);
    }

    [Fact]
    public void an_offer_that_does_not_carry_every_policy_entry_leaves_the_whole_slot_alone()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        // The game's offer is missing the policy's last entry, so no part of the projection may be published.
        var offer = LoadoutWorld.Offer(31, 34, 10001, 10002, 10003);

        var projected = session.Offer(InventorySlot.GearStandard, offer);

        Assert.NotNull(projected);
        Assert.Equal(offer, projected);
        Assert.Contains(world.Infos, line => line == "weapon.loadout-policy-unmatched slot=GearStandard missing=10004");
        Assert.DoesNotContain(world.Infos, line => line.StartsWith("weapon.loadout-slot ", StringComparison.Ordinal));
    }

    [Fact]
    public void an_offer_with_nothing_the_policy_names_is_not_published_as_an_empty_slot()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        var offer = LoadoutWorld.Offer(31, 34, 33);

        var projected = session.Offer(InventorySlot.GearStandard, offer);

        Assert.NotNull(projected);
        Assert.Equal(offer, projected);
        Assert.Contains(world.Infos, line => line == "weapon.loadout-policy-unmatched slot=GearStandard missing=10001,10002,10003,10004");
    }

    [Fact]
    public void projecting_an_already_projected_offer_changes_nothing()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();

        var once = session.Offer(InventorySlot.GearSpecial, LoadoutWorld.Offer(LoadoutWorld.OfferedSpecial));
        var twice = session.Offer(InventorySlot.GearSpecial, once);

        Assert.NotNull(once);
        Assert.NotNull(twice);
        Assert.Equal(LoadoutWorld.PolicySpecial, Blocks(once!));
        Assert.Equal(Blocks(once!), Blocks(twice!));
    }

    [Fact]
    public void a_workshop_gear_is_recognised_by_the_block_id_it_carries_in_its_category()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        // The website writes a craftable gear's own block id into the category component, so a copy of it is
        // recognised wherever it is met — including the copy of a gear this client never listed itself. This one
        // carries no offline record at all, which is the identity the record text alone could never find.
        var workshop = LoadoutWorld.Workshop(10002);
        var offer = new[] { LoadoutWorld.Offline(31), workshop, LoadoutWorld.Offline(10001),
            LoadoutWorld.Offline(10003), LoadoutWorld.Offline(10004) };

        var projected = session.Offer(InventorySlot.GearStandard, offer);

        Assert.NotNull(projected);
        // The offer's order is kept, and the gear the policy recognised by its category is the very gear that was
        // offered — not a replacement the filter built.
        Assert.Equal(new[] { workshop, offer[2], offer[3], offer[4] }, projected);
        Assert.Equal(new uint[] { 0, 10001, 10003, 10004 }, projected.Select(BlockOrZero).ToArray());    }

    [Fact]
    public void every_policy_block_id_has_exactly_one_spelling()
    {
        // The same rule the `gear-block` mount uses: only the game's own `OfflineGear_ID_<canonical decimal>` text
        // names a block, so no second spelling of one id can ever match.
        Assert.Equal("10001", GearLoadoutFilter.OfflineGearBlockId("OfflineGear_ID_10001"));
        Assert.Null(GearLoadoutFilter.OfflineGearBlockId("OfflineGear_ID_010001"));
        Assert.Null(GearLoadoutFilter.OfflineGearBlockId("OfflineGear_ID_+10001"));
        Assert.Null(GearLoadoutFilter.OfflineGearBlockId("OfflineGear_ID_10001.0"));
        Assert.Null(GearLoadoutFilter.OfflineGearBlockId("OfflineGear_ID_10001 "));
        Assert.Null(GearLoadoutFilter.OfflineGearBlockId("OfflineGear_ID_"));
        Assert.Null(GearLoadoutFilter.OfflineGearBlockId("offlinegear_id_10001"));
        Assert.Null(GearLoadoutFilter.OfflineGearBlockId(null));

        var world = LoadoutWorld.Policy();
        var session = world.Session();
        // A gear whose record text is a near miss is a gear the policy does not name: the projection falls back
        // whole rather than publishing three of the four entries.
        var nearMiss = GearIDRange.Create();
        nearMiss.PlayfabItemInstanceId = "OfflineGear_ID_010004";
        var offer = new[] { LoadoutWorld.Offline(10001), LoadoutWorld.Offline(10002), LoadoutWorld.Offline(10003), nearMiss };
        var projected = session.Offer(InventorySlot.GearStandard, offer);

        Assert.Equal(offer, projected);
        Assert.Contains(world.Infos, line => line == "weapon.loadout-policy-unmatched slot=GearStandard missing=10004");
    }

    [Fact]
    public void an_identity_read_that_throws_leaves_the_slot_alone_and_latches_the_session()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        // A gear whose component read throws is what a destroyed gear looks like: the whole slot stays the game's
        // own, because a list built from a rule that has just failed is no list at all.
        var broken = LoadoutWorld.Offline(10002);
        broken.ThrowOnRead = true;
        var offer = new[] { LoadoutWorld.Offline(31), broken, LoadoutWorld.Offline(10001) };

        var projected = session.Offer(InventorySlot.GearStandard, offer);

        Assert.Equal(offer, projected);
        Assert.Contains(world.Infos, line => line == "weapon.loadout-policy-failed: NullReferenceException: fixture gear-component read failure");
        // The policy stops taking part until restart: a later, perfectly good offer is not projected either.
        Assert.Null(session.Offer(InventorySlot.GearStandard, LoadoutWorld.Offer(LoadoutWorld.OfferedStandard)));
    }

    [Fact]
    public void a_gear_with_no_identity_at_all_is_simply_not_one_of_the_policys()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        // A null entry reads as a gear with neither an offline record nor a category, so it is neither a match nor a
        // failure: the projection is decided by the entries the policy does name.
        var offer = new GearIDRange[] { LoadoutWorld.Offline(31), null!, LoadoutWorld.Offline(10001), LoadoutWorld.Offline(10002),
            LoadoutWorld.Offline(10003), LoadoutWorld.Offline(10004) };

        var projected = session.Offer(InventorySlot.GearStandard, offer);

        Assert.NotNull(projected);
        Assert.Equal(LoadoutWorld.PolicyStandard, Blocks(projected!));
        Assert.Empty(world.Reports);
    }

    [Fact]
    public void narrowing_leaves_a_pool_whose_slot_the_policy_cannot_project()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        // One entry of the standard slot's policy is missing from the game's pool, so the whole pool — every slot —
        // stays exactly as the game built it, vanilla rows and all.
        world.Drop(InventorySlot.GearStandard, 10004);
        var standard = world.Blocks(InventorySlot.GearStandard);

        var narrowing = session.Narrow(world.Live());

        Assert.Equal(standard, world.Blocks(InventorySlot.GearStandard));
        Assert.Equal(LoadoutWorld.OfferedSpecial, world.Blocks(InventorySlot.GearSpecial));
        Assert.Equal(LoadoutWorld.OfferedClass, world.Blocks(InventorySlot.GearClass));
        Assert.All(narrowing.Applied, slot => Assert.Null(slot));
        Assert.Contains(world.Infos, line => line == "weapon.loadout-policy-unmatched slot=GearStandard missing=10004");
    }

    [Fact]
    public void narrowing_rewrites_the_games_own_pool_lists_in_place()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        var standard = world.Pool(InventorySlot.GearStandard);

        var narrowing = session.Narrow(world.Live());

        Assert.Same(standard, world.Manager.m_gearPerSlot[(int)InventorySlot.GearStandard]);
        Assert.Equal(LoadoutWorld.PolicyStandard, world.Blocks(InventorySlot.GearStandard));
        Assert.Equal(LoadoutWorld.PolicySpecial, world.Blocks(InventorySlot.GearSpecial));
        Assert.Equal(LoadoutWorld.PolicyClass, world.Blocks(InventorySlot.GearClass));
        Assert.Equal(LoadoutWorld.PolicyStandard, Blocks(narrowing.Applied[0]!));
        Assert.Equal(LoadoutWorld.OfferedStandard, narrowing.Items[0].Select(Block).ToArray());
    }

    [Fact]
    public void a_pool_the_game_has_not_built_yet_is_left_alone()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        world.Manager.m_gearPerSlot[(int)InventorySlot.GearSpecial] = null;

        session.Narrow(world.Live());

        Assert.Equal(LoadoutWorld.OfferedStandard, world.Blocks(InventorySlot.GearStandard));
        Assert.Equal(LoadoutWorld.OfferedClass, world.Blocks(InventorySlot.GearClass));
    }

    [Fact]
    public void narrowing_keeps_every_tool_beside_the_craftable_ones()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        // The game's own tool slot offers a row the policy does not name, so the allow-list has something to remove.
        Assert.Contains(2u, LoadoutWorld.OfferedClass);

        session.Narrow(world.Live());

        // The tool slot is an allow-list like the others, and it makes no guess about what a tool is: every vanilla
        // tool the policy lists stays, the row it does not name goes, and the craftable tools the policy adds are
        // offered beside them.
        Assert.Equal(LoadoutWorld.PolicyClass, world.Blocks(InventorySlot.GearClass));
        Assert.Contains(6u, world.Blocks(InventorySlot.GearClass));
        Assert.Contains(25u, world.Blocks(InventorySlot.GearClass));
        Assert.Contains(20006u, world.Blocks(InventorySlot.GearClass));
        Assert.DoesNotContain(2u, world.Blocks(InventorySlot.GearClass));
    }

    [Fact]
    public void a_saved_vanilla_gear_no_longer_matches_the_narrowed_pool()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        // What a favorites file on this machine actually stores: the offline record text of a vanilla weapon. After
        // the narrowing that name is in no pool, so the game's own fallback equips the narrowed pool's first entry
        // instead — which is the policy's, not the saved vanilla weapon.
        const string savedStandard = "OfflineGear_ID_34";
        Assert.Equal(34u, Block(world.Equipped(InventorySlot.GearStandard, savedStandard)));
        world.Infos.Clear();

        session.Narrow(world.Live());

        Assert.Equal(LoadoutWorld.PolicyStandard[0], Block(world.Equipped(InventorySlot.GearStandard, savedStandard)));
        Assert.Equal(LoadoutWorld.PolicySpecial[0], Block(world.Equipped(InventorySlot.GearSpecial, "OfflineGear_ID_33")));
        // A saved name the policy does carry still selects that gear, so the fallback is the game's own and not a
        // side effect of the narrowing.
        Assert.Equal(10003u, Block(world.Equipped(InventorySlot.GearStandard, "OfflineGear_ID_10003")));
    }

    [Fact]
    public void a_rundown_with_no_policy_gets_the_games_own_pool_back()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        session.Narrow(world.Live());
        Assert.Equal(LoadoutWorld.PolicyStandard, world.Blocks(InventorySlot.GearStandard));
        world.Infos.Clear();

        // The rundown this process loads changed to one no policy is installed for: the pool goes back to exactly
        // the contents the game built, in the game's own order.
        Global.RundownIdToLoad = 99999;
        session.Narrow(world.Live());

        Assert.Equal(LoadoutWorld.OfferedStandard, world.Blocks(InventorySlot.GearStandard));
        Assert.Equal(LoadoutWorld.OfferedSpecial, world.Blocks(InventorySlot.GearSpecial));
        Assert.Equal(LoadoutWorld.OfferedClass, world.Blocks(InventorySlot.GearClass));
        Assert.Contains(world.Infos, line => line == "weapon.loadout-policy-inactive reason=rundown-mismatch rundown=99999");
        // The policy is forgotten with the pool, so loading the policy's own rundown again narrows it again.
        Global.RundownIdToLoad = PolicyInstall.Rundown;
        session.Narrow(world.Live());
        Assert.Equal(LoadoutWorld.PolicyStandard, world.Blocks(InventorySlot.GearStandard));
    }

    [Fact]
    public void a_pool_read_that_throws_leaves_every_slot_alone_and_latches_the_session()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        world.Pool(InventorySlot.GearClass).ThrowOnRead = true;

        var narrowing = session.Narrow(world.Live());

        Assert.Equal(LoadoutWorld.OfferedStandard, world.Blocks(InventorySlot.GearStandard));
        Assert.Equal(LoadoutWorld.OfferedSpecial, world.Blocks(InventorySlot.GearSpecial));
        Assert.All(narrowing.Applied, slot => Assert.Null(slot));
        Assert.Contains(world.Reports, line => line.StartsWith("weapon.loadout-policy-failed: NullReferenceException: ", StringComparison.Ordinal));
        // A fault is latched for the process: the pool is never narrowed again, and an offer is never projected.
        world.Pool(InventorySlot.GearClass).ThrowOnRead = false;
        session.Narrow(world.Live());
        Assert.Equal(LoadoutWorld.OfferedStandard, world.Blocks(InventorySlot.GearStandard));
        Assert.Null(session.Offer(InventorySlot.GearStandard, LoadoutWorld.Offer(LoadoutWorld.OfferedStandard)));
    }

    [Fact]
    public void an_install_with_no_policy_never_touches_the_pool_or_the_offer()
    {
        var world = LoadoutWorld.WithoutPolicy();
        var session = world.Session();
        var offer = LoadoutWorld.Offer(LoadoutWorld.OfferedStandard);

        var narrowing = session.Narrow(world.Live());

        // No policy is in force, so the projection answers nothing at all and the game's own offer is the one the
        // caller publishes.
        Assert.Null(session.Offer(InventorySlot.GearStandard, offer));
        Assert.Equal(LoadoutWorld.OfferedStandard, world.Blocks(InventorySlot.GearStandard));
        Assert.All(narrowing.Applied, slot => Assert.Null(slot));
        Assert.Contains(world.Infos, line => line == "weapon.loadout-policy-inactive reason=rundown-mismatch rundown=" + PolicyInstall.Rundown);
    }

    [Fact]
    public void an_expedition_keeps_the_projection_out_of_the_games_state()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        GameStateManager.IsInExpedition = true;

        // The projection steps aside inside a level: a gear already carried stays visible to the in-level inventory
        // flow, which builds from the same list the lobby picker does.
        var offer = LoadoutWorld.Offer(LoadoutWorld.OfferedStandard);
        Assert.Null(session.Offer(InventorySlot.GearStandard, offer));

        // The pool is built once, before any level is entered, so the loading hook's own end is not gated on the
        // expedition flag: it narrows here exactly as it does in the lobby.
        var narrowing = session.Narrow(world.Live());
        Assert.Equal(LoadoutWorld.PolicyStandard, world.Blocks(InventorySlot.GearStandard));
        Assert.Equal(LoadoutWorld.PolicyStandard, Blocks(narrowing.Applied[0]!));
    }

    [Fact]
    public void the_summary_names_every_vanilla_weapon_the_policy_removed()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();

        session.Offer(InventorySlot.GearStandard, LoadoutWorld.Offer(LoadoutWorld.OfferedStandard));
        session.Offer(InventorySlot.GearClass, LoadoutWorld.Offer(LoadoutWorld.OfferedClass));

        Assert.Contains(world.Infos, line => line == "weapon.loadout-vanilla-dropped count=2");
        // The tool slot drops rows as well, but a tool is not a weapon: the line that reads "vanilla weapons are
        // gone" is written for the two weapon slots only.
        Assert.Single(world.Infos, line => line.StartsWith("weapon.loadout-vanilla-dropped ", StringComparison.Ordinal));
    }

    [Fact]
    public void disposing_the_session_puts_the_pool_back()
    {
        var world = LoadoutWorld.Policy();
        var session = world.Session();
        session.Narrow(world.Live());

        session.Dispose();

        Assert.Equal(LoadoutWorld.OfferedStandard, world.Blocks(InventorySlot.GearStandard));
        Assert.Equal(LoadoutWorld.OfferedSpecial, world.Blocks(InventorySlot.GearSpecial));
        Assert.Equal(LoadoutWorld.OfferedClass, world.Blocks(InventorySlot.GearClass));
    }

    [Fact]
    public void a_policy_is_read_by_its_own_content_pins_and_refused_when_one_of_them_moves()
    {
        // The content pins the website writes into `forge/loadout.json`: the game assembly digest, the pinned
        // plugins and the pinned source files. The fixture's install is a real temp directory with real bytes, read
        // by the production loader exactly as a plugin's Load would read it — no Release artifact is touched.
        var install = new PolicyInstall();
        var canonical = install.Canonical(standard: LoadoutWorld.PolicyStandard, special: LoadoutWorld.PolicySpecial,
            gearClass: LoadoutWorld.PolicyClass);
        install.WritePolicy("Pack", canonical);
        var accepted = install.LoadWritten(out var acceptedRejections);
        Assert.Empty(acceptedRejections);
        Assert.Equal(1, accepted.PolicyCount);
        Assert.True(accepted.TryGet(PolicyInstall.Rundown, out var policy));
        Assert.Equal("plugins/Pack/forge/loadout.json", policy.RelativePath);
        Assert.Equal(install.GameSha, policy.GameAssemblySha256);
        Assert.Equal(install.GearSha, policy.Sources.Single().Sha256);
        Assert.Equal(install.PinnedPlugins.Select(pin => pin.Guid).OrderBy(x => x, StringComparer.Ordinal),
            policy.Plugins.Select(pin => pin.Guid).OrderBy(x => x, StringComparer.Ordinal));

        // One pinned file changes: its own policy is refused whole and by name, and nothing else takes effect.
        install.Write("plugins/Pack/gear.json", "gear-fixture-moved");
        var refused = install.LoadWritten(out var refusedRejections);
        Assert.Equal(0, refused.PolicyCount);
        Assert.Single(refusedRejections);
        Assert.StartsWith("weapon.loadout-policy-source-mismatch file=plugins/Pack/forge/loadout.json:", refusedRejections[0], StringComparison.Ordinal);
    }

    [Fact]
    public void a_policy_whose_rundown_is_not_the_loaded_one_never_narrows_the_pool()
    {
        // The same file, with a rundown id the game is not loading: an install can hold several packages' policies,
        // and only the one the game is actually running may take effect. The file is still accepted — it is a
        // perfectly good policy for another rundown — and it simply never activates here.
        var install = new PolicyInstall();
        var canonical = install.Canonical(rundownId: PolicyInstall.Rundown + 1, standard: LoadoutWorld.PolicyStandard,
            special: LoadoutWorld.PolicySpecial, gearClass: LoadoutWorld.PolicyClass);
        var world = LoadoutWorld.Policy(canonical);
        var session = world.Session();
        var offer = LoadoutWorld.Offer(LoadoutWorld.OfferedStandard);

        Assert.Equal(1, world.Policies.PolicyCount);
        Assert.Null(session.Offer(InventorySlot.GearStandard, offer));
        session.Narrow(world.Live());
        Assert.Equal(LoadoutWorld.OfferedStandard, world.Blocks(InventorySlot.GearStandard));
        Assert.Contains(world.Infos, line => line == "weapon.loadout-policy-inactive reason=rundown-mismatch rundown=" + PolicyInstall.Rundown);
    }

    private static uint[] Blocks(IReadOnlyList<GearIDRange> gears) => gears.Select(Block).ToArray();

    private static uint Block(GearIDRange gear) => LoadoutWorld.Block(gear);

    /// <summary>The same block id, or zero for a gear that carries no offline record — a craftable gear, whose
    /// identity is its category component instead.</summary>
    private static uint BlockOrZero(GearIDRange gear) => LoadoutWorld.BlockOrZero(gear);
}
