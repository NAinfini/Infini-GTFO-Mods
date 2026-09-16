using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// The `a-hud` row. A case checks three things: which text the forms render, which of the game's own readouts was
/// written, and that every object this row created is destroyed with the world it belonged to. The refusals are
/// checked by name because two of them are limits this row declares rather than hides: a `bar` outside the status
/// bar has no sprite, and `self` combined with the overhead placement has no marker. The audience half — one value
/// per player, drawn only on that player's own machine — has its own file, because the point there is the routing
/// and not the row.
/// </summary>
public sealed class EnvironmentHudTests
{
    /// <summary>The input bag of a `team` request: the one player this case's value belongs to is also the one
    /// viewer it names, which is the smallest audience a team row can carry.</summary>
    private static object Inputs(double value, bool visible, double? maximum = null, string? label = null)
    {
        if (maximum is { } bound && label != null)
            return new { viewers = new[] { EnvironmentWorld.Player(1) }, value, visible, maximum = bound, label };
        if (maximum is { } only)
            return new { viewers = new[] { EnvironmentWorld.Player(1) }, value, visible, maximum = only };
        if (label != null)
            return new { viewers = new[] { EnvironmentWorld.Player(1) }, value, visible, label };
        return new { viewers = new[] { EnvironmentWorld.Player(1) }, value, visible };
    }

    private static PUI_LocalPlayerStatus Status => GuiManager.Current!.m_playerLayer!.m_playerStatus!;

    [Fact]
    public void TheStatusBarReadsTheShieldValueAndWritesTheNumberBesideIt()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(45, visible: true, maximum: 100),
            new { placement = 0, form = 1, audience = 0 }, isHost: false);

        var result = world.Hud.HandleValue(context);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal(45f, Status.Shield);
        Assert.Equal("45 / 100", Status.m_shieldText!.text);
        Assert.True(Status.m_shieldUIParent!.activeSelf);
    }

    [Fact]
    public void TheLabelIsPrefixedToTheRenderedValue()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(45, visible: true, maximum: 100, label: "护盾"),
            new { placement = 0, form = 1, audience = 0 }, isHost: false);

        Assert.Equal("succeeded", world.Hud.HandleValue(context).Status);
        Assert.Equal("护盾 45 / 100", Status.m_shieldText!.text);
    }

    [Fact]
    public void ThePercentFormRoundsTheShareOfTheMaximum()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(1, visible: true, maximum: 3),
            new { placement = 0, form = 2, audience = 0 }, isHost: false);

        Assert.Equal("succeeded", world.Hud.HandleValue(context).Status);
        Assert.Equal("33%", Status.m_shieldText!.text);
    }

    [Fact]
    public void TheNumberFormWithoutAMaximumRendersTheValueAlone()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(7.5, visible: true),
            new { placement = 0, form = 0, audience = 0 }, isHost: false);

        Assert.Equal("succeeded", world.Hud.HandleValue(context).Status);
        Assert.Equal("7.5", Status.m_shieldText!.text);
    }

    [Fact]
    public void TheBarFormWritesTheGamesOwnBarAndNoText()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(30, visible: true, maximum: 100),
            new { placement = 0, form = 3, audience = 0 }, isHost: false);

        Assert.Equal("succeeded", world.Hud.HandleValue(context).Status);
        Assert.Equal(30f, Status.Shield);
        Assert.Equal("", Status.m_shieldText!.text);
    }

    [Fact]
    public void TheColourIsParsedIntoTheTextColour()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(5, visible: true),
            new { placement = 0, form = 0, audience = 0, color = "#3366ff" }, isHost: false);

        Assert.Equal("succeeded", world.Hud.HandleValue(context).Status);
        var colour = Status.m_shieldText!.color;
        Assert.Equal(0x33 / 255f, colour.r, 3);
        Assert.Equal(0x66 / 255f, colour.g, 3);
        Assert.Equal(1f, colour.b, 3);
    }

    [Fact]
    public void HidingAStatusReadoutTakesTheGamesOwnGroupAway()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(45, visible: false),
            new { placement = 0, form = 0, audience = 0 }, isHost: false);

        Assert.Equal("succeeded", world.Hud.HandleValue(context).Status);
        Assert.False(Status.m_shieldUIParent!.activeSelf);
        Assert.Null(Status.Shield);
    }

    [Fact]
    public void TheScreenPlacementClonesOneTextAndHidesItAgain()
    {
        using var world = EnvironmentWorld.Start();
        var show = Contexts.Command(HudContract.ValueCapability, Inputs(12, visible: true, maximum: 20),
            new { placement = 1, form = 1, audience = 0, key = "shield" }, isHost: false);
        var hide = Contexts.Command(HudContract.ValueCapability, Inputs(12, visible: false),
            new { placement = 1, form = 1, audience = 0, key = "shield" }, isHost: false);

        Assert.Equal("succeeded", world.Hud.HandleValue(show).Status);
        Assert.Single(UnityEngine.Object.Created);
        Assert.Equal("12 / 20", UnityEngine.Object.Created[0].GetComponent<TMPro.TextMeshPro>()!.text);

        Assert.Equal("succeeded", world.Hud.HandleValue(hide).Status);
        Assert.Contains(UnityEngine.Object.Created[0], UnityEngine.Object.Destroyed);
    }

    [Fact]
    public void DrawingTheSameKeyTwiceReusesTheOneTextObject()
    {
        using var world = EnvironmentWorld.Start();
        for (int value = 1; value <= 2; value++)
        {
            var context = Contexts.Command(HudContract.ValueCapability, Inputs(value, visible: true),
                new { placement = 1, form = 0, audience = 0, key = "shield" }, isHost: false);
            Assert.Equal("succeeded", world.Hud.HandleValue(context).Status);
        }

        Assert.Single(UnityEngine.Object.Created);
        Assert.Equal("2", UnityEngine.Object.Created[0].GetComponent<TMPro.TextMeshPro>()!.text);
    }

    [Fact]
    public void ANewWorldDestroysTheReadoutsOfTheOneBeforeIt()
    {
        using var world = EnvironmentWorld.Start();
        var first = Contexts.Command(HudContract.ValueCapability, Inputs(1, visible: true),
            new { placement = 1, form = 0, audience = 0, key = "shield" }, isHost: false, worldEpoch: 1);
        Assert.Equal("succeeded", world.Hud.HandleValue(first).Status);
        var readout = UnityEngine.Object.Created[0];

        var second = Contexts.Command(HudContract.ValueCapability, Inputs(2, visible: true),
            new { placement = 1, form = 0, audience = 0, key = "shield" }, isHost: false, worldEpoch: 2);
        Assert.Equal("succeeded", world.Hud.HandleValue(second).Status);

        Assert.Contains(readout, UnityEngine.Object.Destroyed);
        Assert.Equal(2, UnityEngine.Object.Created.Count);
    }

    [Fact]
    public void TheSelfAudienceWithoutASessionIsRefusedRatherThanDrawnForEveryone()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(1, visible: true),
            new { placement = 0, form = 0, audience = 1 }, isHost: false);

        Assert.Equal(HudActions.AddressCode, world.Hud.Value(context, null).Code);
        Assert.Null(Status.Shield);
    }

    [Fact]
    public void TheSelfAudienceRefusesACombinationWithNoMarkerOfItsOwn()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(1, visible: true),
            new { placement = 2, form = 0, audience = 1 }, isHost: false);

        // The local player has no overhead marker, so the two members cannot be true of the same row and the
        // refusal names the reason rather than drawing the line on a teammate's head.
        Assert.Equal(HudActions.SelfOverheadCode, world.Hud.Value(context, "4242").Code);
    }

    [Fact]
    public void ABarOutsideTheStatusBarHasNoSpriteToWrite()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(1, visible: true),
            new { placement = 1, form = 3, audience = 0 }, isHost: false);

        Assert.Equal(HudActions.FormCode, world.Hud.HandleValue(context).Code);
        Assert.Empty(UnityEngine.Object.Created);
    }

    [Fact]
    public void AColourThatIsNotAHexTripletIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(1, visible: true),
            new { placement = 0, form = 0, audience = 0, color = "blue" }, isHost: false);

        Assert.Equal(HudActions.ColorCode, world.Hud.HandleValue(context).Code);
    }

    [Fact]
    public void AValueOrAVisibilityFlagThatIsMissingIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var noValue = Contexts.Command(HudContract.ValueCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) }, visible = true },
            new { placement = 0, form = 0, audience = 0 }, isHost: false);
        var noVisibility = Contexts.Command(HudContract.ValueCapability,
            new { viewers = new[] { EnvironmentWorld.Player(1) }, value = 1 },
            new { placement = 0, form = 0, audience = 0 }, isHost: false);

        Assert.Equal(HudActions.ValueCode, world.Hud.HandleValue(noValue).Code);
        Assert.Equal(HudActions.VisibleCode, world.Hud.HandleValue(noVisibility).Code);
    }

    [Fact]
    public void AnAudienceThatIsNotPlayersIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(HudContract.ValueCapability,
            new { viewers = new[] { EnvironmentWorld.Foreign() }, value = 1, visible = true },
            new { placement = 0, form = 0, audience = 0 }, isHost: false);

        Assert.Equal(HudActions.ViewerCode, world.Hud.HandleValue(context).Code);
    }

    [Fact]
    public void AClientWithoutItsOwnHudLayerIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        GuiManager.Current = null;
        var context = Contexts.Command(HudContract.ValueCapability, Inputs(1, visible: true),
            new { placement = 0, form = 0, audience = 0 }, isHost: false);

        Assert.Equal(HudActions.NoLayerCode, world.Hud.HandleValue(context).Code);
    }

    [Fact]
    public void TheRenderedLineIsTheOneTheChecklistAsksFor()
    {
        Assert.Equal("45", HudActions.Text("number", 45, 100, null));
        Assert.Equal("45 / 100", HudActions.Text("number_of_max", 45, 100, null));
        Assert.Equal("45%", HudActions.Text("percent", 45, 100, null));
        Assert.Equal("护盾 45 / 100", HudActions.Text("number_of_max", 45, 100, "护盾"));
        // A maximum nobody named cannot be divided by, so the two bounded forms fall back to the number alone.
        Assert.Equal("45", HudActions.Text("percent", 45, 0, null));
    }

    [Fact]
    public void TheValueRowIsDeclaredAsThePresentationTier()
    {
        var graph = Contexts.Graph(HudContract.ValueCapability);
        Assert.Equal("presentation", graph.GetProperty("execution").GetString());
        Assert.Equal(JsonValueKind.Object, graph.GetProperty("recipients").ValueKind);
    }
}
