using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMap.Tests.NativeObjectiveActions;

public sealed class CheckpointSaveTests
{
    public CheckpointSaveTests() => SyntheticWorld.Reset();
    private static EntityReference Player(string id) => new(id, 1, 1);
    private static CommandContext Context(EntityReference[] participants, double[] anchor, bool host = true)
        => Contexts.For(SessionActionContract.CheckpointSaveCapability, new { participants, anchor }, null, host);

    [Fact]
    public void save_uses_the_checkpoint_interaction_once_and_reports_every_participant()
    {
        using var world = SyntheticWorld.Start();
        var action = new SessionActions(() => true, world.Reports.Add);
        var a = Player("p1"); var b = Player("p2");
        var result = action.CheckpointSave(Context(new[] { a, b }, new[] { 1d, 2d, 3d }));

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        var interaction = Assert.Single(CheckpointManager.Interactions);
        Assert.Equal(eCheckpointInteractionType.StoreCheckpoint, interaction.type);
        Assert.Equal(1f, interaction.doorLockPosition.x);
        Assert.Equal(2f, interaction.doorLockPosition.y);
        Assert.Equal(3f, interaction.doorLockPosition.z);
        var rows = result.Outputs.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal(CommandStatuses.Succeeded, row.GetProperty("status").GetString()));
    }

    [Fact]
    public void save_refuses_before_native_write_when_not_authoritative_or_anchor_is_invalid()
    {
        using var world = SyntheticWorld.Start();
        var action = new SessionActions(() => true, world.Reports.Add);
        Assert.Equal(SessionActions.AuthorityCode,
            action.CheckpointSave(Context(new[] { Player("p") }, new[] { 1d, 2d, 3d }, false)).Code);
        Assert.Equal(SessionActions.AnchorCode,
            action.CheckpointSave(Context(new[] { Player("p") }, new[] { double.MaxValue, 2d, 3d })).Code);
        Assert.Empty(CheckpointManager.Interactions);
    }

    [Fact]
    public void save_marks_commit_unknown_when_native_interaction_throws()
    {
        using var world = SyntheticWorld.Start();
        var action = new SessionActions(() => true, world.Reports.Add);
        CheckpointManager.ThrowOnAttemptInteract = new InvalidOperationException("boom");
        var result = action.CheckpointSave(Context(new[] { Player("p") }, new[] { 1d, 2d, 3d }));
        Assert.Equal(CommandStatuses.Failed, result.Status);
        Assert.Equal(CommitStates.Unknown, result.CommitState);
        Assert.Equal(SessionActions.CommitExceptionCode, result.Code);
        Assert.Single(CheckpointManager.Interactions);
        Assert.Single(world.Reports);
    }
}
