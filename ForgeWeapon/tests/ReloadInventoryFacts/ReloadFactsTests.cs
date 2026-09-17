using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.ReloadInventoryFacts;

/// <summary>The three combat reload rows over doubles. Each case drives the production observer directly, so what
/// is under test is the decision — which row one observation produces, and that one observation never produces two
/// — rather than the game's player path, which no case here runs.</summary>
[Trait("Category", "ReloadInventoryFacts")]
public sealed class ReloadFactsTests
{
    private const string Started = ReloadInventoryContract.ReloadStartedBinding;
    private const string Completed = ReloadInventoryContract.ReloadCompletedBinding;
    private const string Transferred = ReloadInventoryContract.ReloadTransferredBinding;

    /// <summary>A world with one player and one recorded equipment life of 30 rounds.</summary>
    private static (FactsWorld World, FakePlayer Owner, EntityReference OwnerReference, FakeItem Weapon) Armed()
    {
        var world = new FactsWorld();
        world.Start();
        var (owner, ownerReference) = world.Player();
        var weapon = new FakeItem { Clip = 30 };
        world.Life(weapon, owner);
        return (world, owner, ownerReference, weapon);
    }

    [Fact]
    public void a_reload_that_moves_rounds_publishes_started_transferred_and_completed()
    {
        var (world, _, ownerReference, weapon) = Armed();
        using var _ = world;

        world.Reload.FlagChanged(weapon, reloading: true);
        weapon.Clip = 45;
        world.Reload.MagazineRead(weapon);
        world.Reload.FlagChanged(weapon, reloading: false);

        var started = Assert.Single(world.Facts(Started));
        Assert.Equal(ownerReference.Id, FactsWorld.Reference(started, "actor"));
        Assert.Equal(world.Published[0].EventId, started.EventId);
        var transferred = Assert.Single(world.Facts(Transferred));
        Assert.Equal(15, FactsWorld.Number(transferred, "amount"));
        var completed = Assert.Single(world.Facts(Completed));
        Assert.Equal(FactsWorld.Reference(started, "equipment"), FactsWorld.Reference(completed, "equipment"));
        Assert.Equal(0, world.Reload.OpenCount);
    }

    /// <summary>A closed transfer row does not stop the life from moving: the transfer fact is not built, but the
    /// amount was still added to the life, which is why the ending is still published — a life that had moved
    /// nothing publishes no ending at all. The gate is read before the event value, not after it.</summary>
    [Fact]
    public void a_closed_transfer_row_publishes_no_transfer_but_the_life_still_moves()
    {
        var (world, _, _, weapon) = Armed();
        using var _ = world;

        world.Reload.FlagChanged(weapon, reloading: true);
        weapon.Clip = 45;
        world.Closed.Add(Transferred);
        world.Reload.MagazineRead(weapon);
        world.Reload.FlagChanged(weapon, reloading: false);

        Assert.Single(world.Facts(Started));
        Assert.Empty(world.Facts(Transferred));
        Assert.Single(world.Facts(Completed));
        Assert.Equal(0, world.Reload.OpenCount);
    }

    /// <summary>A reload that moved no ammunition closes without a fact: the node list's `e-w-reload` is start and
    /// completion, so an interrupted reload is not a node and the life is simply dropped.</summary>
    [Fact]
    public void a_reload_that_moves_nothing_publishes_no_ending_at_all()
    {
        var (world, _, _, weapon) = Armed();
        using var _ = world;

        world.Reload.FlagChanged(weapon, reloading: true);
        world.Reload.FlagChanged(weapon, reloading: false);

        Assert.Single(world.Facts(Started));
        Assert.Empty(world.Facts(Completed));
        Assert.Empty(world.Facts(Transferred));
        Assert.Equal(0, world.Reload.OpenCount);
    }

    [Fact]
    public void the_entry_point_and_the_flag_are_one_reload_and_open_one_life()
    {
        var (world, _, _, weapon) = Armed();
        using var _ = world;

        world.Reload.Requested(weapon);
        world.Reload.FlagChanged(weapon, reloading: true);
        world.Reload.Requested(weapon);
        weapon.Clip = 31;
        world.Reload.MagazineRead(weapon);
        world.Reload.FlagChanged(weapon, reloading: false);

        Assert.Single(world.Facts(Started));
        Assert.Single(world.Facts(Transferred));
        Assert.Single(world.Facts(Completed));
        Assert.Equal(1, FactsWorld.Number(world.Facts(Transferred)[0], "amount"));
    }

    [Fact]
    public void the_entry_point_alone_starts_nothing_when_the_flag_stays_false()
    {
        var (world, _, _, weapon) = Armed();
        using var _ = world;

        world.Reload.Requested(weapon);

        Assert.Empty(world.Facts(Started));
        Assert.Equal(0, world.Reload.OpenCount);
    }

    [Fact]
    public void a_second_readback_of_the_same_transfer_publishes_nothing_more()
    {
        var (world, _, _, weapon) = Armed();
        using var _ = world;

        world.Reload.FlagChanged(weapon, reloading: true);
        weapon.Clip = 38;
        world.Reload.MagazineRead(weapon);
        world.Reload.MagazineRead(weapon);
        weapon.Clip = 40;
        world.Reload.MagazineRead(weapon);
        world.Reload.FlagChanged(weapon, reloading: false);

        var transfers = world.Facts(Transferred);
        Assert.Equal(2, transfers.Count);
        Assert.Equal(8, FactsWorld.Number(transfers[0], "amount"));
        Assert.Equal(2, FactsWorld.Number(transfers[1], "amount"));
        Assert.Single(world.Facts(Completed));
    }

    [Fact]
    public void a_pool_gain_inside_a_reload_window_is_a_transfer_with_the_pool_as_its_source()
    {
        var (world, _, ownerReference, weapon) = Armed();
        using var _ = world;

        world.Reload.FlagChanged(weapon, reloading: true);
        world.Reload.PoolRead(ownerReference, 15);
        world.Reload.FlagChanged(weapon, reloading: false);

        var transferred = Assert.Single(world.Facts(Transferred));
        Assert.Equal(15, FactsWorld.Number(transferred, "amount"));
        Assert.Single(world.Facts(Completed));
        Assert.Contains(world.Infos, line => line.Contains("source=pool", StringComparison.Ordinal));
    }

    [Fact]
    public void a_pool_gain_outside_a_reload_window_publishes_nothing()
    {
        var (world, _, ownerReference, _) = Armed();
        using var _ = world;

        world.Reload.PoolRead(ownerReference, 15);

        Assert.Empty(world.Facts(Transferred));
        Assert.Empty(world.Facts(Started));
    }

    [Fact]
    public void a_magazine_read_outside_an_open_life_publishes_nothing()
    {
        var (world, _, _, weapon) = Armed();
        using var _ = world;

        weapon.Clip = 45;
        world.Reload.MagazineRead(weapon);

        Assert.Empty(world.Facts(Transferred));
    }

    [Fact]
    public void an_item_with_no_live_identity_publishes_no_reload_fact()
    {
        var (world, _, _, _) = Armed();
        using var _ = world;
        var stranger = new FakeItem { Clip = 10, Reloading = true };

        world.Reload.FlagChanged(stranger, reloading: true);
        stranger.Clip = 20;
        world.Reload.MagazineRead(stranger);
        world.Reload.FlagChanged(stranger, reloading: false);

        Assert.Empty(world.Published);
    }

    [Fact]
    public void a_life_retired_mid_reload_takes_its_transfer_with_it()
    {
        var (world, _, ownerReference, weapon) = Armed();
        using var _ = world;
        var equipment = world.ByItem[weapon.Id];

        world.Reload.FlagChanged(weapon, reloading: true);
        world.Retire(equipment);
        world.Reload.SyncWorld();
        weapon.Clip = 45;
        world.Reload.MagazineRead(weapon);
        world.Reload.PoolRead(ownerReference, 15);
        world.Reload.FlagChanged(weapon, reloading: false);

        Assert.Single(world.Facts(Started));
        Assert.Empty(world.Facts(Transferred));
        Assert.Empty(world.Facts(Completed));
    }

    [Fact]
    public void a_lost_authority_drops_every_open_life_without_an_ending_fact()
    {
        var (world, _, _, weapon) = Armed();
        using var _ = world;

        world.Reload.FlagChanged(weapon, reloading: true);
        world.LoseAuthority();
        world.Reload.FlagChanged(weapon, reloading: false);

        Assert.Single(world.Facts(Started));
        Assert.Empty(world.Facts(Completed));
        Assert.Equal(0, world.Reload.OpenCount);
        Assert.Contains(world.Infos, line => line.Contains("reason=not-authoritative", StringComparison.Ordinal));
    }

    /// <summary>An authority loss drops the open life without a fact, and a reload that runs after the machine is
    /// authoritative again opens its own life in the new world: it never closes the one the transition took, and
    /// the transfer it reports is measured from its own baseline.</summary>
    [Fact]
    public void a_life_that_does_not_survive_an_authority_transition_never_finishes_the_old_one()
    {
        var (world, owner, _, weapon) = Armed();
        using var _ = world;

        world.Reload.FlagChanged(weapon, reloading: true);
        world.LoseAuthority();
        world.Reload.FlagChanged(weapon, reloading: false);
        Assert.Single(world.Facts(Started));
        Assert.Empty(world.Facts(Completed));

        world.ReenterWorld();
        var reborn = new FakeItem { Clip = 30 };
        world.Life(reborn, owner);
        world.Reload.FlagChanged(reborn, reloading: true);
        reborn.Clip = 45;
        world.Reload.MagazineRead(reborn);
        world.Reload.FlagChanged(reborn, reloading: false);

        Assert.Equal(2, world.Facts(Started).Count);
        var transferred = Assert.Single(world.Facts(Transferred));
        Assert.Equal(15, FactsWorld.Number(transferred, "amount"));
        Assert.Single(world.Facts(Completed));
        Assert.Equal(FactsWorld.InitialWorldEpoch + 1, world.Facts(Started)[1].WorldEpoch);
    }

    [Fact]
    public void a_dispose_publishes_nothing_for_an_open_life()
    {
        var (world, _, _, weapon) = Armed();

        world.Reload.FlagChanged(weapon, reloading: true);
        world.Reload.Clear();

        Assert.Single(world.Facts(Started));
        Assert.Empty(world.Facts(Completed));
        Assert.Contains(world.Infos, line => line.Contains("reason=dispose", StringComparison.Ordinal));
        world.Dispose();
    }

    [Fact]
    public void the_open_reload_answer_names_the_player_the_life_belongs_to()
    {
        var (world, _, ownerReference, weapon) = Armed();
        using var _ = world;
        var (other, otherReference) = world.Player();

        world.Reload.FlagChanged(weapon, reloading: true);

        Assert.True(world.Reload.OpenFor(ownerReference));
        Assert.False(world.Reload.OpenFor(otherReference));
        Assert.False(world.Reload.OpenFor(null));
    }

    [Fact]
    public void every_reload_fact_is_published_under_the_epoch_the_life_opened_in()
    {
        var (world, _, _, weapon) = Armed();
        using var _ = world;

        world.Reload.FlagChanged(weapon, reloading: true);
        weapon.Clip = 31;
        world.Reload.MagazineRead(weapon);
        world.Reload.FlagChanged(weapon, reloading: false);

        Assert.All(world.Published, fact => Assert.Equal(world.WorldEpoch, fact.WorldEpoch));
        Assert.All(world.Published,
            fact => Assert.Equal("gtfo.equipment:" + world.ByItem[weapon.Id].Id, fact.ScopeId));
    }
}
