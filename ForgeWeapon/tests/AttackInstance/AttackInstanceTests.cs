using System;
using System.Linq;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.AttackInstance;

/// <summary>The slice's own facts, driven through the production <see cref="AttackInstanceModule"/>: one attack
/// opens at the request, is accepted only when the native body registered a shot, closes at the end of that body
/// with a completion, and is reported as a miss when no hit candidate was published inside it. Nothing here is
/// asserted from a log line: the facts are read from the module's only publication path and the counters are the
/// fixture's, so what is under test is the scope's own rules.</summary>
public sealed class AttackInstanceTests
{
    private const string Requested = "forge.module.gtfo.weapon.binding.attack_requested";
    private const string Accepted = "forge.module.gtfo.weapon.binding.attack_accepted";
    private const string Completed = "forge.module.gtfo.weapon.binding.attack_completed";
    private const string Missed = "forge.module.gtfo.weapon.binding.attack_missed";
    private const string BurstStarted = "forge.module.gtfo.weapon.binding.burst_started";
    private const string BurstEnded = "forge.module.gtfo.weapon.binding.burst_ended";
    private const string DryFire = "forge.module.gtfo.weapon.binding.dry_fire";
    private const string HitCandidate = "forge.module.gtfo.weapon.binding.hit_candidate";

    /// <summary>One firing body that registered a shot and hit something publishes the request, the acceptance and
    /// the completion, in that order, and no miss: the scope saw a hit candidate.</summary>
    [Fact]
    public void OneFiringBodyThatHitsPublishesRequestAcceptanceAndCompletion()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Fire(equipment, registersShot: true, hits: 1);

        Assert.Equal(new[] { Requested, HitCandidate, Accepted, Completed },
            world.Facts.Select(fact => fact.BindingId).ToArray());
        AssertPublished(world);
        var accepted = world.FactsOf(Accepted).Single();
        Assert.Equal(equipment.Reference.Id, AttackWorld.ReferenceOf(accepted, "equipment"));
        Assert.Equal(equipment.Owner.Reference.Id, AttackWorld.ReferenceOf(accepted, "source"));
    }

    /// <summary>The request's `phase` port is the catalog's `command_phase` member for a request, and the row's
    /// equipment port carries the recorded life's own id rather than a bare pointer.</summary>
    [Fact]
    public void TheRequestCarriesTheRequestedPhase()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Fire(equipment);

        var request = world.FactsOf(Requested).Single();
        Assert.Equal("requested", AttackWorld.Text(request, "phase"));
        Assert.True(AttackWorld.Has(request, "equipment"));
        Assert.Equal(equipment.Owner.Reference.Id, AttackWorld.ReferenceOf(request, "source"));
    }

    /// <summary>The acceptance is the native body's own shot registration and nothing else: a pull the weapon
    /// refused leaves the life's shot counter where it was, so no acceptance is published — while the scope still
    /// completes and is still reported as a miss, because the request did happen.</summary>
    [Fact]
    public void ARefusedPullPublishesNoAcceptance()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Fire(equipment, registersShot: false, hits: 0);

        Assert.Equal(new[] { Requested, Completed, Missed }, world.Facts.Select(fact => fact.BindingId).ToArray());
        AssertPublished(world);
    }

    /// <summary>A body that registered a shot but never published a hit candidate is a miss: the close reads the
    /// equipment life's own hit-candidate counter, so "it hit something" is the hit observer's evidence and not a
    /// second reading of the same hit.</summary>
    [Fact]
    public void AScopeWithoutAHitCandidateIsAMiss()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Fire(equipment, registersShot: true, hits: 0);

        Assert.Single(world.FactsOf(Missed));
        Assert.Equal(equipment.Reference.Id, AttackWorld.ReferenceOf(world.FactsOf(Missed).Single(), "equipment"));
    }

    /// <summary>A hit candidate inside the scope cancels that scope's miss, and a body that hits nothing after an
    /// earlier hit is still a miss: the counter is read at the close and compared against the scope's own opening
    /// value, never against zero.</summary>
    [Fact]
    public void TheMissRuleComparesAgainstTheScopesOwnOpeningCount()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Fire(equipment, registersShot: true, hits: 2);
        Assert.Single(world.FactsOf(Completed));
        Assert.Empty(world.FactsOf(Missed));

        world.Fire(equipment, registersShot: true, hits: 0);

        Assert.Equal(2, world.FactsOf(Completed).Count);
        Assert.Single(world.FactsOf(Missed));
    }

    /// <summary>A hit candidate published after the scope closed is not this scope's hit.</summary>
    [Fact]
    public void AHitPublishedAfterTheCloseDoesNotCancelTheMiss()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Fire(equipment, registersShot: true, hits: 0);
        world.PublishHit(equipment);

        Assert.Single(world.FactsOf(Missed));
    }

    /// <summary>Each attack of one equipment life is its own scope, numbered inside that life.</summary>
    [Fact]
    public void EachAttackIsItsOwnScope()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Fire(equipment);
        world.Fire(equipment);

        var scopes = world.FactsOf(Requested).Select(fact => fact.ScopeId!).ToArray();
        Assert.Equal(2, scopes.Length);
        Assert.NotEqual(scopes[0], scopes[1]);
        Assert.StartsWith("gtfo.weapon.attack:" + equipment.Reference.Id + ":", scopes[0], StringComparison.Ordinal);
    }

    /// <summary>Two equipment lives keep their own attack sequences and their own scopes: one life's counter never
    /// numbers another's attacks.</summary>
    [Fact]
    public void TwoLivesKeepTheirOwnSequences()
    {
        using var world = Started();
        var first = world.Rig(new Gear.BulletWeapon(), world.Player());
        var second = world.Rig(new Gear.Shotgun(), world.Player());

        world.Fire(first);
        world.Fire(second);
        world.Fire(first);

        var scopes = world.FactsOf(Requested).Select(fact => fact.ScopeId!).ToArray();
        Assert.Equal(3, scopes.Length);
        Assert.StartsWith("gtfo.weapon.attack:" + first.Reference.Id + ":1", scopes[0], StringComparison.Ordinal);
        Assert.StartsWith("gtfo.weapon.attack:" + second.Reference.Id + ":1", scopes[1], StringComparison.Ordinal);
        Assert.StartsWith("gtfo.weapon.attack:" + first.Reference.Id + ":2", scopes[2], StringComparison.Ordinal);
    }

    /// <summary>A weapon this build has not recorded as an equipment life publishes nothing: an attack without a
    /// life a plan can name is not a fact. The same gate answers `Complete` and `DryFire`, so a body that returns
    /// for such a weapon closes nothing and an empty clip on it reports nothing.</summary>
    [Fact]
    public void AnUnrecordedWeaponPublishesNothing()
    {
        using var world = Started();
        var loose = new Gear.BulletWeapon();

        world.Attack.Request(loose, AttackInstanceModule.AttackMode.Ranged);
        world.Attack.Complete(loose);
        world.Attack.DryFire(loose);

        Assert.Empty(world.Facts);
        Assert.Contains(world.Infos, line => line.Contains("attack-untracked", StringComparison.Ordinal));
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

        world.Fire(equipment);

        Assert.Empty(world.Facts);
    }

    /// <summary>The game's own empty-clip path is what the dry-fire fact comes from, and the row's two ports are
    /// the actor and the equipment.</summary>
    [Fact]
    public void TheGamesOwnEmptyClipPathPublishesDryFire()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());
        var archetype = new Gear.BWA_Auto { m_weapon = equipment.Weapon };

        world.Attack.DryFire(archetype.m_weapon);

        var fact = world.FactsOf(DryFire).Single();
        AssertPublished(world);
        Assert.Equal(equipment.Reference.Id, AttackWorld.ReferenceOf(fact, "equipment"));
        Assert.Equal(equipment.Owner.Reference.Id, AttackWorld.ReferenceOf(fact, "actor"));
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

    /// <summary>An open scope with no close is closed by the next request and reported, so a scope can never be
    /// silently lost; the melee path is the other way into the same two methods and follows the same rules.</summary>
    [Fact]
    public void AReenteredRequestClosesTheOpenAttack()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Attack.Request(equipment.Weapon, AttackInstanceModule.AttackMode.Ranged);
        world.Attack.Request(equipment.Weapon, AttackInstanceModule.AttackMode.Melee);

        Assert.Equal(2, world.FactsOf(Requested).Count);
        Assert.Single(world.FactsOf(Completed));
        Assert.Single(world.FactsOf(Missed));
        Assert.Contains(world.Reports, line => line.Contains("attack-reentered", StringComparison.Ordinal));
    }

    /// <summary>A close with no open scope publishes nothing and says so: the hook fires on a native body that
    /// returned, and this module does not invent the attack it would belong to.</summary>
    [Fact]
    public void ACloseWithoutAnOpenScopePublishesNothing()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());

        world.Attack.Complete(equipment.Weapon);

        Assert.Empty(world.Facts);
        Assert.Contains(world.Infos, line => line.Contains("attack-close-without-open", StringComparison.Ordinal));
    }

    /// <summary>An equipment life that ends between the request and the close is dropped without a completion or a
    /// miss: neither could be attributed to a life a plan can still name.</summary>
    [Fact]
    public void AnAttackWhoseLifeIsGoneIsDroppedWithoutAFact()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());
        world.Attack.Request(equipment.Weapon, AttackInstanceModule.AttackMode.Ranged);
        world.Forget(equipment);

        world.Attack.Complete(equipment.Weapon);

        Assert.Single(world.FactsOf(Requested));
        Assert.Empty(world.FactsOf(Completed));
        Assert.Empty(world.FactsOf(Missed));
        Assert.Contains(world.Reports, line => line.Contains("attack-equipment-lost", StringComparison.Ordinal));
    }

    /// <summary>A world change drops the open scope and takes the recorded lives with it: the attack can no
    /// longer be attributed to a life a plan could name, so the close publishes neither a completion nor a miss.
    /// The scope is dropped before the life lookup is even reached, which is why the diagnostic is the
    /// close-without-open one.</summary>
    [Fact]
    public void AWorldChangeDropsTheOpenScope()
    {
        using var world = Started();
        var equipment = world.Rig(new Gear.BulletWeapon(), world.Player());
        world.Attack.Request(equipment.Weapon, AttackInstanceModule.AttackMode.Ranged);
        var atStart = world.Facts.Count;

        world.Kernel.BeginWorld(AttackWorld.WorldEpoch + 1);
        world.Kernel.Advance(world.Kernel.CurrentTick + 1, true);
        world.Forget(equipment);
        world.Attack.Complete(equipment.Weapon);

        Assert.Equal(atStart, world.Facts.Count);
        Assert.Contains(world.Infos, line => line.Contains("attack-close-without-open", StringComparison.Ordinal));
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
