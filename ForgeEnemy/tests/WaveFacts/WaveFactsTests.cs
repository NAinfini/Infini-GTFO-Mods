using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>One fact per case, over the world in <see cref="WaveFactsWorld"/>. The published frames are read back
/// through the recording action a plan wires them into, so a case asserts the ports a plan actually receives —
/// including the one port that is never there.</summary>
public sealed class WaveFactsTests
{
    [Fact]
    public void WaveStartedPublishesWithTheWaveScopeAndNoHandlePort()
    {
        using var world = new WaveFactsWorld();
        world.LoadPlan("test.wave.started", EnemyWaveContract.WaveStartedBinding);
        world.Start();
        var wave = world.Wave(42);

        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        world.Tick(1);

        var record = Assert.Single(world.Records);
        Assert.Equal("gtfo.wave:7:42", record.ScopeId);
        // The `wave` handle port is declared optional and is never published; the kernel refuses to wire an
        // optional event port at all, so a plan can neither receive it nor be built as if it would. The only
        // input the frame carries is the compiled reference the recorder's own recipient needs.
        Assert.False(record.Inputs.TryGetProperty("wave", out _));
        Assert.Equal(new[] { "wave_resource" }, record.Inputs.EnumerateObject().Select(p => p.Name));
        Assert.Empty(world.Reports);
    }

    [Fact]
    public void WaveStartedIsPublishedOncePerInstance()
    {
        using var world = new WaveFactsWorld();
        world.LoadPlan("test.wave.started.once", EnemyWaveContract.WaveStartedBinding);
        world.Start();
        var wave = world.Wave(9);

        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        // A second spawn callback for the instance already in the table opens nothing new.
        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        world.Tick(1);

        Assert.Single(world.Records);
    }

    [Fact]
    public void WaveSpawnedCarriesTheBatchMembers()
    {
        using var world = new WaveFactsWorld();
        world.LoadMembersPlan("test.wave.spawned", EnemyWaveContract.WaveSpawnedBinding, ("spawned", "targets"));
        world.Start();
        var wave = world.Wave(3);
        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        var first = world.Group(world.Enemy(11), world.Enemy(12));
        var second = world.Group(world.Enemy(13));
        world.SetActiveGroups(first, second);

        world.RunBatch(wave, first, second);
        world.Tick(1);

        var record = Assert.Single(world.Records);
        var members = record.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        Assert.Equal(new[] { "gtfo.enemy:11", "gtfo.enemy:12", "gtfo.enemy:13" }, members.Select(m => m.Id));
        Assert.Empty(world.Reports);
    }

    [Fact]
    public void WaveSpawnedCarriesTheBatchCount()
    {
        using var world = new WaveFactsWorld();
        world.LoadPlan("test.wave.spawned.count", EnemyWaveContract.WaveSpawnedBinding, ("count", "count"));
        world.Start();
        var wave = world.Wave(15);
        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        var group = world.Group(world.Enemy(14), world.Enemy(16));
        world.SetActiveGroups(group);

        world.RunBatch(wave, group);
        world.Tick(1);

        var record = Assert.Single(world.Records);
        Assert.Equal(2, record.Inputs.GetProperty("count").GetInt32());
        Assert.Equal("gtfo.wave:7:15", record.ScopeId);
    }

    [Fact]
    public void WaveSpawnedLeavesOutAMemberWithNoLiveLife()
    {
        using var world = new WaveFactsWorld();
        world.LoadMembersPlan("test.wave.spawned.stale", EnemyWaveContract.WaveSpawnedBinding, ("spawned", "targets"));
        world.Start();
        var wave = world.Wave(4);
        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        var gone = world.Enemy(21);
        var group = world.Group(gone, world.Enemy(22));
        world.SetActiveGroups(group);

        // The member despawns while the batch is still open, so the frame is built after its life is gone.
        world.Facts.BeforeWaveGroupStep(wave);
        world.Facts.AfterWaveGroup(wave, group);
        world.Retire(gone);
        world.Facts.AfterWaveGroupStep(wave);
        world.Tick(1);

        var record = Assert.Single(world.Records);
        var members = record.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        Assert.Equal(new[] { "gtfo.enemy:22" }, members.Select(m => m.Id));
        Assert.Empty(world.Reports);
    }

    [Fact]
    public void WaveExhaustedCarriesTheRegisteredGroupCount()
    {
        using var world = new WaveFactsWorld();
        world.LoadPlan("test.wave.exhausted", EnemyWaveContract.WaveExhaustedBinding, ("count", "count"));
        world.Start();
        var wave = world.Wave(7);
        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        var first = world.Group(world.Enemy(51));
        world.SetActiveGroups(first);
        world.RunBatch(wave, first);

        // The wave's own completion test answering false is not a fact.
        world.Facts.AfterWaveEndTest(wave, false);
        world.Tick(1);
        Assert.Empty(world.Records);

        var second = world.Group(world.Enemy(52));
        world.SetActiveGroups(first, second);
        world.RunBatch(wave, second);
        world.Facts.AfterWaveEndTest(wave, true);
        world.Tick(2);

        var record = Assert.Single(world.Records);
        Assert.Equal(2, record.Inputs.GetProperty("count").GetInt32());
        Assert.Equal("gtfo.wave:7:7", record.ScopeId);
    }

    [Fact]
    public void WaveClearedFiresWhenNoGroupOfTheWaveIsStillActive()
    {
        using var world = new WaveFactsWorld();
        world.LoadPlan("test.wave.cleared", EnemyWaveContract.WaveClearedBinding);
        world.Start();
        var wave = world.Wave(8);
        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        var ours = world.Group(world.Enemy(61));
        var theirs = world.Group(world.Enemy(62));

        world.SetActiveGroups(ours);
        world.RunBatch(wave, ours);
        world.Facts.AfterGroupMaintenance();
        world.Tick(1);
        Assert.Empty(world.Records);

        // Another wave's group is still active; nothing of this wave's is.
        world.SetActiveGroups(theirs);
        world.Facts.AfterGroupMaintenance();
        world.Tick(2);

        var record = Assert.Single(world.Records);
        Assert.Equal("gtfo.wave:7:8", record.ScopeId);
        // Once cleared, a later maintenance pass is not a second fact.
        world.Facts.AfterGroupMaintenance();
        world.Tick(3);
        Assert.Single(world.Records);
        Assert.Empty(world.Reports);
    }

    [Fact]
    public void AWaveThatNeverRegisteredAGroupIsNotCleared()
    {
        using var world = new WaveFactsWorld();
        world.LoadPlan("test.wave.cleared.empty", EnemyWaveContract.WaveClearedBinding);
        world.Start();
        var wave = world.Wave(10);
        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));

        world.SetActiveGroups();
        world.Facts.AfterGroupMaintenance();
        world.Tick(1);

        Assert.Empty(world.Records);
    }

    [Fact]
    public void DespawnRetiresWithoutPublishing()
    {
        using var world = new WaveFactsWorld();
        world.LoadPlan("test.wave.despawn", EnemyWaveContract.WaveClearedBinding);
        world.Start();
        var wave = world.Wave(11);
        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        var group = world.Group(world.Enemy(71));
        world.SetActiveGroups(group);
        world.RunBatch(wave, group);

        // A teardown takes the same path as level cleanup, so it is not a clear.
        world.Facts.BeforeWaveDespawn(wave);
        world.SetActiveGroups();
        world.Facts.AfterGroupMaintenance();
        world.Tick(1);
        Assert.Empty(world.Records);

        // The retired wave is gone from the table: a later completion test on it is not a fact either.
        world.Facts.AfterWaveEndTest(wave, true);
        world.Tick(2);
        Assert.Empty(world.Records);
    }

    [Fact]
    public void ANewWorldDropsEveryTrackedWave()
    {
        using var world = new WaveFactsWorld();
        world.LoadPlan("test.wave.epoch.cleared", EnemyWaveContract.WaveClearedBinding);
        world.LoadPlan("test.wave.epoch.started", EnemyWaveContract.WaveStartedBinding);
        world.Start();
        var wave = world.Wave(12);
        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));
        var group = world.Group(world.Enemy(81));
        world.SetActiveGroups(group);
        world.RunBatch(wave, group);

        world.Kernel.BeginWorld(WaveFactsWorld.WorldEpoch + 1);
        // Everything from the previous epoch is gone: its EventID space means nothing in the new world.
        world.Facts.AfterWaveEndTest(wave, true);
        world.SetActiveGroups();
        world.Facts.AfterGroupMaintenance();
        world.Tick(1);
        Assert.Empty(world.Records);

        var next = world.Wave(12);
        world.Facts.AfterWaveSpawn(next, world.Facts.BeforeWaveSpawn(next));
        world.Tick(2);
        var record = Assert.Single(world.Records);
        Assert.Equal("gtfo.wave:8:12", record.ScopeId);
    }

    [Fact]
    public void NoSubscriberTracksNothing()
    {
        using var world = new WaveFactsWorld();
        world.Start();
        var wave = world.Wave(13);

        Assert.Null(world.Facts.BeforeWaveSpawn(wave));
        world.Facts.AfterWaveSpawn(wave, null);
        world.Facts.AfterWaveGroup(wave, world.Group(world.Enemy(91)));
        world.Facts.AfterGroupMaintenance();
        world.Tick(1);

        Assert.Empty(world.Records);
        Assert.Empty(world.Reports);
    }

    [Fact]
    public void ANonAuthoritativeWorldIsRefusedAndReported()
    {
        using var world = new WaveFactsWorld();
        world.LoadPlan("test.wave.host", EnemyWaveContract.WaveStartedBinding);
        world.Start();
        world.TickAsClient(0);
        var wave = world.Wave(14);

        world.Facts.AfterWaveSpawn(wave, world.Facts.BeforeWaveSpawn(wave));

        Assert.Empty(world.Records);
        Assert.Contains(world.Reports, report => report.Contains("not-host", StringComparison.Ordinal));
    }
}
