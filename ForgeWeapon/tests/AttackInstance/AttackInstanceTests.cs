using System;
using System.Linq;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.AttackInstance;

/// <summary>The slice's own facts, driven through the production <see cref="AttackInstanceModule"/>: a burst
/// sequence has two ends counted by the weapon's own burst length, and the game's own empty-clip path is the
/// dry-fire fact. Nothing here is asserted from a log line: the facts are read from the module's only publication
/// path and the reads are the fixture's, so what is under test is the module's own rules.</summary>
public sealed class AttackInstanceTests
{
    private const string BurstStarted = "forge.module.gtfo.weapon.binding.burst_started";
    private const string BurstEnded = "forge.module.gtfo.weapon.binding.burst_ended";
    private const string DryFire = "forge.module.gtfo.weapon.binding.dry_fire";

    /// <summary>The game's own empty-clip path is what the dry-fire fact comes from, and the row's two ports are
    /// the actor and the equipment.</summary>
    [Fact]
    public void TheGamesOwnEmptyClipPathPublishesDryFire()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Attack.DryFire(equipment.Weapon);

        var fact = world.FactsOf(DryFire).Single();
        AssertPublished(world);
        Assert.Equal(equipment.Reference.Id, AttackWorld.ReferenceOf(fact, "equipment"));
        Assert.Equal(equipment.Owner.Reference.Id, AttackWorld.ReferenceOf(fact, "actor"));
    }

    /// <summary>Every dry pull of one equipment life is its own scope, numbered inside that life.</summary>
    [Fact]
    public void EveryDryFireOfOneLifeIsItsOwnScope()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Attack.DryFire(equipment.Weapon);
        world.Attack.DryFire(equipment.Weapon);

        var scopes = world.FactsOf(DryFire).Select(fact => fact.ScopeId!).ToArray();
        Assert.Equal(2, scopes.Length);
        Assert.NotEqual(scopes[0], scopes[1]);
        Assert.StartsWith("gtfo.weapon.dry-fire:" + equipment.Reference.Id + ":1",
            scopes[0], StringComparison.Ordinal);
    }

    /// <summary>A burst sequence publishes its two ends once each, and `count` is the weapon's own burst length.
    /// The sequence is per equipment life, so a second start while one is open is not a second fact and a second
    /// end is not either.</summary>
    [Fact]
    public void ABurstSequenceHasTwoEndsAndTheWeaponsOwnCount()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon { m_burstMax = 3 }, world.Player());

        world.Attack.Burst(equipment.Weapon, started: true);
        world.Attack.Burst(equipment.Weapon, started: true);
        world.Attack.Burst(equipment.Weapon, started: false);
        world.Attack.Burst(equipment.Weapon, started: false);

        Assert.Single(world.FactsOf(BurstStarted));
        Assert.Single(world.FactsOf(BurstEnded));
        Assert.Equal(3, world.FactsOf(BurstStarted).Single().Outputs.GetProperty("count").GetInt32());
        Assert.Equal(3, world.FactsOf(BurstEnded).Single().Outputs.GetProperty("count").GetInt32());
        Assert.Equal(equipment.Reference.Id, AttackWorld.ReferenceOf(world.FactsOf(BurstStarted).Single(), "equipment"));
    }

    /// <summary>Two equipment lives keep their own sequences: one life's counter never numbers another's
    /// bursts.</summary>
    [Fact]
    public void TwoLivesKeepTheirOwnSequences()
    {
        using var world = Started();
        var first = world.Rig(new Gear.BulletWeapon(), world.Player());
        var second = world.Rig(new Gear.Shotgun(), world.Player());

        world.Attack.Burst(first.Weapon, started: true);
        world.Attack.Burst(second.Weapon, started: true);
        world.Attack.Burst(first.Weapon, started: true);

        var scopes = world.FactsOf(BurstStarted).Select(fact => fact.ScopeId!).ToArray();
        Assert.Equal(2, scopes.Length);
        Assert.StartsWith("gtfo.weapon.burst:" + first.Reference.Id + ":1", scopes[0], StringComparison.Ordinal);
        Assert.StartsWith("gtfo.weapon.burst:" + second.Reference.Id + ":1", scopes[1], StringComparison.Ordinal);
    }

    /// <summary>A sequence end with nothing open publishes nothing: the game reports a sequence, and this module
    /// does not invent the missing half of one. Neither does it publish for an unrecorded life.</summary>
    [Fact]
    public void ASequenceEndWithoutAnOpenSequencePublishesNothing()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon { m_burstMax = 2 }, world.Player());

        world.Attack.Burst(equipment.Weapon, started: false);
        world.Attack.Burst(null, started: true);

        Assert.Empty(world.Facts);
    }

    /// <summary>A negative burst length is not a count a plan can act on, so it is published as zero rather than
    /// as a number the game could not have produced.</summary>
    [Fact]
    public void ANegativeBurstLengthIsPublishedAsZero()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon { m_burstMax = -1 }, world.Player());

        world.Attack.Burst(equipment.Weapon, started: true);

        Assert.Equal(0, world.FactsOf(BurstStarted).Single().Outputs.GetProperty("count").GetInt32());
    }

    /// <summary>A world change takes the recorded lives with it: a burst sequence cannot be continued across
    /// worlds, so the next start is a new sequence rather than a second start of the old one.</summary>
    [Fact]
    public void AWorldChangeClearsTheBurstSequence()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon { m_burstMax = 2 }, world.Player());
        world.Attack.Burst(equipment.Weapon, started: true);

        world.Kernel.BeginWorld(AttackWorld.WorldEpoch + 1);
        world.Kernel.Advance(world.Kernel.CurrentTick + 1, true);
        world.Attack.Burst(equipment.Weapon, started: true);

        Assert.Equal(2, world.FactsOf(BurstStarted).Count);
    }

    /// <summary>A weapon this build has not recorded as an equipment life publishes nothing: a fact without a
    /// life a plan can name is not a fact.</summary>
    [Fact]
    public void AnUnrecordedWeaponPublishesNothing()
    {
        using var world = Started();
        var loose = new Gear.BulletWeapon();

        world.Attack.DryFire(loose);

        Assert.Empty(world.Facts);
        Assert.Contains(world.Infos, line => line.Contains("dry-fire-untracked", StringComparison.Ordinal));
    }

    /// <summary>A non-authoritative machine publishes nothing: the gate refuses before any of the module's reads
    /// run.</summary>
    [Fact]
    public void ANonAuthoritativeWorldPublishesNothing()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());
        world.LoseAuthority();

        world.Attack.DryFire(equipment.Weapon);
        world.Attack.Burst(equipment.Weapon, started: true);

        Assert.Empty(world.Facts);
    }

    private static AttackWorld Started()
    {
        var world = new AttackWorld();
        world.Start();
        return world;
    }

    /// <summary>The last fact was accepted by Runtime rather than refused for its shape. A fact published outside
    /// a dispatch is answered `ignored` — the host's own observation path always runs outside one — so the check
    /// is that the runtime did not refuse it, which is what a drifted payload or an unregistered binding would
    /// produce.</summary>
    private static void AssertPublished(AttackWorld world)
        => Assert.False(world.LastStatus.StartsWith("rejected", StringComparison.Ordinal),
            "the last fact was refused: " + world.LastStatus);
}
