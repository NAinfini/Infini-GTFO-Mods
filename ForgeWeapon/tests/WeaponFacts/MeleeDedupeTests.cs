using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests;

/// <summary>The rule that keeps one landed melee hit published once: which of the row's native sources may
/// publish, and which observation is the same hit seen twice. Both are pure decisions, so they are driven here
/// without a game in the process.</summary>
public sealed class MeleeDedupeTests
{
    private static EntityReference Reference(string id) => new(id, 3, 1);

    [Fact]
    public void one_hit_is_claimed_once_inside_the_window()
    {
        var ledger = new MeleeHitLedger();
        var source = Reference("gtfo.player:1");
        var target = Reference("gtfo.enemy:2");

        Assert.True(ledger.TryClaim(source, target, 4, tick: 10, world: 1));
        Assert.False(ledger.TryClaim(source, target, 4, tick: 10, world: 1));
        Assert.False(ledger.TryClaim(source, target, 4, tick: 11, world: 1));
        Assert.False(ledger.TryClaim(source, target, 4, tick: 10 + MeleeHitLedger.WindowTicks - 1, world: 1));
        Assert.True(ledger.TryClaim(source, target, 4, tick: 10 + MeleeHitLedger.WindowTicks, world: 1));
    }

    [Fact]
    public void a_different_limb_target_or_attacker_is_a_different_hit()
    {
        var ledger = new MeleeHitLedger();
        var source = Reference("gtfo.player:1");
        var other = Reference("gtfo.player:2");
        var target = Reference("gtfo.enemy:2");
        var otherTarget = Reference("gtfo.enemy:3");

        Assert.True(ledger.TryClaim(source, target, 0, 5, 1));
        Assert.True(ledger.TryClaim(source, target, 1, 5, 1));
        Assert.True(ledger.TryClaim(source, otherTarget, 0, 5, 1));
        Assert.True(ledger.TryClaim(other, target, 0, 5, 1));
        Assert.Equal(4, ledger.Count);
    }

    [Fact]
    public void a_claim_never_outlives_the_world_it_was_made_in()
    {
        var ledger = new MeleeHitLedger();
        var source = Reference("gtfo.player:1");
        var target = Reference("gtfo.enemy:2");

        Assert.True(ledger.TryClaim(source, target, 0, 5, world: 1));
        // The same ticks in the next world are a different hit: a claim is per world, not per tick.
        Assert.True(ledger.TryClaim(source, target, 0, 5, world: 2));
        Assert.False(ledger.TryClaim(source, target, 0, 6, world: 2));
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void a_hit_without_a_nameable_attacker_or_target_is_never_claimed()
    {
        var ledger = new MeleeHitLedger();

        Assert.False(ledger.TryClaim(null, Reference("gtfo.enemy:2"), 0, 1, 1));
        Assert.False(ledger.TryClaim(Reference("gtfo.player:1"), null, 0, 1, 1));
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void the_receive_half_publishes_only_a_remote_players_swing()
    {
        // The host's own player's swing and a bot's swing are published by the hit entry that performed them.
        Assert.True(MeleeHitLedger.PublishesRemote(attackerIsLocal: false, attackerIsBot: false));
        Assert.False(MeleeHitLedger.PublishesRemote(attackerIsLocal: true, attackerIsBot: false));
        Assert.False(MeleeHitLedger.PublishesRemote(attackerIsLocal: false, attackerIsBot: true));
        Assert.False(MeleeHitLedger.PublishesRemote(attackerIsLocal: true, attackerIsBot: true));
    }

    [Fact]
    public void the_ledger_keeps_a_bounded_number_of_claims()
    {
        var ledger = new MeleeHitLedger();
        var source = Reference("gtfo.player:1");
        for (int index = 0; index <= MeleeHitLedger.Capacity; index++)
            ledger.TryClaim(source, Reference("gtfo.enemy:" + index), 0, index, 1);

        Assert.True(ledger.Count <= MeleeHitLedger.Capacity);
    }
}
