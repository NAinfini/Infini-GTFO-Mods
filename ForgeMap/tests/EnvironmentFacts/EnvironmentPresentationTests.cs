using ForgeMap;
using ForgeMap.Native;
using GameData;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// The five presented rows: the sound event, the paired stop of a looping sound, the warden-intel line, the
/// nearest-player dialogue and the player's own voice line. Every one of them runs on the client the step was
/// addressed to, so the cases here check both halves of the tier: the local entry was reached with the right
/// arguments, and the result commits nothing — which is the invariant the kernel itself refuses a handler for
/// breaking.
/// </summary>
public sealed class EnvironmentPresentationTests
{
    private static object Viewers(params object[] viewers) => new { viewers };

    private static object OneViewer() => Viewers(EnvironmentWorld.Player(1));

    [Fact]
    public void ASoundPlaysLocallyWithItsIdAndItsComputedSubtitle()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.AudioCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) }, sound = 42, subtitle = "反应堆已接通 3 / 7" }, null,
            isHost: false);

        var result = world.Presentation.HandleAudio(context);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        var data = EnvironmentWorld.LastEvent!;
        Assert.Equal(eWardenObjectiveEventType.PlaySound, data.Type);
        Assert.Equal(42u, data.SoundID);
        Assert.Equal("反应堆已接通 3 / 7", data.SoundSubtitle!.UntranslatedText);
    }

    [Fact]
    public void ASoundWithoutAnIdIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.AudioCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) } }, null, isHost: false);

        Assert.Equal(EnvironmentPresentation.SoundRequiredCode, world.Presentation.HandleAudio(context).Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void StoppingASoundRunsTheStopEventThePickedLoopIsPairedWith()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.AudioStopCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) }, sound = 151, filter = "Reactor" }, null,
            isHost: false);

        var result = world.Presentation.HandleAudioStop(context);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        var data = EnvironmentWorld.LastEvent!;
        // The game stops a loop with the stop event its bank pairs with that loop, and that event runs through the
        // same PlaySound entry the play row uses: the row carries the id and nothing else.
        Assert.Equal(eWardenObjectiveEventType.PlaySound, data.Type);
        Assert.Equal(151u, data.SoundID);
        Assert.Null(data.SoundSubtitle);
        Assert.Equal("Reactor", data.WorldEventObjectFilter);
    }

    [Fact]
    public void AStopWithoutAnIdIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.AudioStopCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) } }, null, isHost: false);

        Assert.Equal(EnvironmentPresentation.SoundRequiredCode, world.Presentation.HandleAudioStop(context).Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void TheIntelLineCarriesFreeTextComputedByThePlan()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.IntelCapability,
            new { viewers = new[] { EnvironmentWorld.Player(2) }, text = "安全门 B3 已解锁" }, null, isHost: false);

        var result = world.Presentation.HandleIntel(context);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        var data = EnvironmentWorld.LastEvent!;
        Assert.Equal(eWardenObjectiveEventType.None, data.Type);
        Assert.Equal("安全门 B3 已解锁", data.WardenIntel!.UntranslatedText);
    }

    [Fact]
    public void AnEmptyIntelLineIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.IntelCapability,
            new { viewers = new[] { EnvironmentWorld.Player(2) }, text = "" }, null, isHost: false);

        Assert.Equal(EnvironmentPresentation.TextRequiredCode, world.Presentation.HandleIntel(context).Code);
    }

    [Fact]
    public void AnOverlongIntelLineIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.IntelCapability,
            new { viewers = new[] { EnvironmentWorld.Player(2) }, text = new string('x', 4096) }, null, isHost: false);

        Assert.Equal(EnvironmentPresentation.SubtitleCode, world.Presentation.HandleIntel(context).Code);
    }

    [Fact]
    public void TheNearestPlayerLineCarriesItsDialogueAndItsObjectFilter()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.DialogueCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) }, dialogue = 12, filter = "WE_Keycard_Door" }, null,
            isHost: false);

        Assert.Equal("succeeded", world.Presentation.HandleDialogue(context).Status);
        var data = EnvironmentWorld.LastEvent!;
        Assert.Equal(eWardenObjectiveEventType.DialogueOnClosest, data.Type);
        Assert.Equal(12u, data.DialogueID);
        Assert.Equal("WE_Keycard_Door", data.WorldEventObjectFilter);
    }

    [Fact]
    public void ADialogueWithoutAnIdIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.DialogueCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) } }, null, isHost: false);

        Assert.Equal(EnvironmentPresentation.DialogueRequiredCode, world.Presentation.HandleDialogue(context).Code);
    }

    [Fact]
    public void APlayerLineGoesThroughTheVoiceManagerForTheSpeakersOwnSlot()
    {
        using var world = EnvironmentWorld.Start();
        var speaker = EnvironmentWorld.Player(2, 4242, 2);
        var context = Contexts.Command(EnvironmentContract.PlayerVoiceCapability,
            new { viewers = new[] { speaker }, speaker, voice = 91 }, null, isHost: false);

        var result = world.Presentation.HandlePlayerVoice(context);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        // The slot is the speaker's own, read off the life the entity resolves to: the plan named a player, not a
        // number, and this machine asked the identity for the number the game's entry takes.
        Assert.Equal((2, 91u), global::Player.PlayerVoiceManager.Said[^1]);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void APlayerLineForASpeakerThisProcessCannotNameIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.PlayerVoiceCapability,
            new { viewers = new[] { EnvironmentWorld.Player(2) }, speaker = EnvironmentWorld.UnknownPlayer(77), voice = 91 },
            null, isHost: false);

        Assert.Equal(EnvironmentPresentation.SpeakerCode, world.Presentation.HandlePlayerVoice(context).Code);
        Assert.Empty(global::Player.PlayerVoiceManager.Said);
    }

    [Fact]
    public void APlayerLineWhoseSpeakerIsNotAPlayerEntityIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.PlayerVoiceCapability,
            new { viewers = new[] { EnvironmentWorld.Player(2) }, speaker = EnvironmentWorld.Foreign(), voice = 91 },
            null, isHost: false);

        Assert.Equal(EnvironmentPresentation.SpeakerCode, world.Presentation.HandlePlayerVoice(context).Code);
        Assert.Empty(global::Player.PlayerVoiceManager.Said);
    }

    [Fact]
    public void ARequestThatNamedNoViewersIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.IntelCapability,
            new { viewers = System.Array.Empty<object>(), text = "x" }, null, isHost: false);

        Assert.Equal(EnvironmentPresentation.ViewerRequiredCode, world.Presentation.HandleIntel(context).Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void AnAudienceThatIsNotPlayersIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.IntelCapability,
            new { viewers = new[] { EnvironmentWorld.Foreign() }, text = "x" }, null, isHost: false);

        Assert.Equal(EnvironmentPresentation.ViewerCode, world.Presentation.HandleIntel(context).Code);
    }

    [Fact]
    public void APresentedRowReportsNoCommit()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.AudioCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) }, sound = 1 }, null, isHost: false);

        var result = world.Presentation.HandleAudio(context);

        // The tier's own invariant: a presentation write commits nothing a replica would have to replicate.
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.NotEqual(CommandStatuses.Rejected, result.Status);
    }

    [Fact]
    public void ATornDownLevelRefusesAPresentedRowToo()
    {
        using var world = EnvironmentWorld.Start();
        WorldEventManager.Current = null;
        var context = Contexts.Command(EnvironmentContract.DialogueCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) }, dialogue = 3 }, null, isHost: false);

        Assert.Equal(EnvironmentPresentation.UnavailableCode, world.Presentation.HandleDialogue(context).Code);
    }
}
