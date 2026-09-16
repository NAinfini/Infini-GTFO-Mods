using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// The two halves of `a-hud` that are about *whose* value is drawn: `placement=teammate_overhead`, which puts the
/// line under a teammate's own name marker exactly the way `InfiniTweaks ResourceHud` does, and `audience=self`,
/// which is the host sending one value per player and each machine drawing only its own.
///
/// The game's own hook is not installed here — this project has no Harmony and no game — so a case calls the same
/// body the postfix calls (`TeammateOverhead.Bind` for a marker the game just built, `TeammateOverhead.Remove` for
/// one it destroyed), which is the split the production files already keep: the patch decides when, this model
/// decides what.
/// </summary>
public sealed class EnvironmentOverheadTests
{
    /// <summary>The text the overhead row drew under one player's name, or null when it drew none.</summary>
    private static string? Line(int number)
    {
        var text = UnityEngine.Object.Created
            .Select(x => x.GetComponent<TMPro.TextMeshPro>())
            .FirstOrDefault(x => x != null && x.text.Length != 0);
        return text?.text;
    }

    /// <summary>The one marker the game built for a player, handed to the same body the postfix calls.</summary>
    private static void Placed(int number)
    {
        var marker = EnvironmentWorld.Agent(number)!.NavMarker!;
        TeammateOverhead.Bind(new TeammateOverhead.NativeMarker(marker));
    }

    private static CommandContext Overhead(EntityReference[] viewers, double value, bool visible = true,
        int audience = 0, string? color = null, double maximum = 100)
    {
        // The colour is a structural parameter, not an input port: the value travels per dispatch, the form of
        // the readout is part of the row.
        object parameters = color == null
            ? new { placement = 2, form = 1, audience }
            : new { placement = 2, form = 1, audience, color };
        return Contexts.Command(HudContract.ValueCapability, new { viewers, value, visible, maximum }, parameters,
            isHost: false);
    }

    [Fact]
    public void TheOverheadLineIsClonedUnderATeammatesNameAndCarriesTheValue()
    {
        using var world = EnvironmentWorld.Start();
        var teammate = EnvironmentWorld.Player(2);
        EnvironmentWorld.Local(1);

        var result = world.Hud.Value(Overhead(new[] { teammate }, 42));

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal("42 / 100", Line(2));
        // The line is one object of its own under the name mesh, which is the one shape the game's marker system
        // leaves for a second row.
        var clone = UnityEngine.Object.Created.Single();
        Assert.Equal("ForgeHudTeammateValue", clone.name);
        Assert.Same(EnvironmentWorld.Agent(2)!.NavMarker!.m_marker!.m_playerName!.transform, clone.transform.parent);
    }

    [Fact]
    public void AValueThatArrivesBeforeTheMarkerDoesIsDrawnWhenTheGameBuildsIt()
    {
        using var world = EnvironmentWorld.Start();
        // A player whose agent has spawned but whose marker the game has not built yet.
        var teammate = EnvironmentWorld.Player(2, 2002, 1, marker: false);
        EnvironmentWorld.Local(1);

        Assert.Equal("succeeded", world.Hud.Value(Overhead(new[] { teammate }, 42)).Status);
        Assert.Empty(UnityEngine.Object.Created);

        // The marker appears later, which is what the game's own refresh does, and the value that was computed
        // before it existed is what is drawn on it.
        var agent = EnvironmentWorld.Agent(2)!;
        var marker = new PlaceNavMarkerOnGO(agent.Owner!.Pointer) { Player = agent };
        marker.m_marker = new NavMarker { m_playerName = EnvironmentWorld.NameText("LateName") };
        agent.NavMarker = marker;
        Placed(2);

        Assert.Equal("42 / 100", Line(2));
    }

    [Fact]
    public void TheLocalPlayersOwnMarkerIsNeverDrawnOn()
    {
        using var world = EnvironmentWorld.Start();
        var local = EnvironmentWorld.Player(1, 4242, 0, bot: false, locallyOwned: true);
        EnvironmentWorld.Local(1);

        Assert.Equal("succeeded", world.Hud.Value(Overhead(new[] { local }, 42)).Status);
        Placed(1);

        Assert.Null(Line(1));
    }

    [Fact]
    public void AHiddenLineTakesTheCloneAndTheNativeRowAway()
    {
        using var world = EnvironmentWorld.Start();
        var teammate = EnvironmentWorld.Player(2);
        EnvironmentWorld.Local(1);
        var marker = EnvironmentWorld.Agent(2)!.NavMarker!;

        Assert.Equal("succeeded", world.Hud.Value(Overhead(new[] { teammate }, 42)).Status);
        marker.m_extraInfoVisible = true;
        Placed(2);
        var drawn = UnityEngine.Object.Created.Single();
        Assert.True(marker.m_extraInfoVisible);

        Assert.Equal("succeeded", world.Hud.Value(Overhead(new[] { teammate }, 42, visible: false)).Status);

        Assert.Contains(drawn, UnityEngine.Object.Destroyed);
        // The visibility the line took the shared extra-information row from is what goes back to it.
        Assert.False(marker.m_extraInfoVisible);
    }

    [Fact]
    public void AChangedValueIsDrawnOnTheMarkerThatIsAlreadyPlaced()
    {
        using var world = EnvironmentWorld.Start();
        var teammate = EnvironmentWorld.Player(2);
        EnvironmentWorld.Local(1);

        world.Hud.Value(Overhead(new[] { teammate }, 10));
        Placed(2);
        Assert.Equal("10 / 100", Line(2));

        world.Hud.Value(Overhead(new[] { teammate }, 20));

        Assert.Equal("20 / 100", Line(2));
        Assert.Single(UnityEngine.Object.Created);
    }

    [Fact]
    public void AMarkerTheGameDestroyedStopsBeingDrawnOn()
    {
        using var world = EnvironmentWorld.Start();
        var teammate = EnvironmentWorld.Player(2);
        EnvironmentWorld.Local(1);
        world.Hud.Value(Overhead(new[] { teammate }, 10));
        Placed(2);
        var first = UnityEngine.Object.Created.Single();

        TeammateOverhead.Remove(EnvironmentWorld.Agent(2)!.NavMarker!.Pointer);

        // The marker's own clone went with it, and the value that arrives before the game rebuilds the marker is
        // stored again rather than written through the object that is gone.
        Assert.Contains(first, UnityEngine.Object.Destroyed);
        Assert.Equal("succeeded", world.Hud.Value(Overhead(new[] { teammate }, 30)).Status);
        Assert.Equal(2, UnityEngine.Object.Created.Count);
    }

    [Fact]
    public void ANewWorldTakesEveryOverheadLineWithIt()
    {
        using var world = EnvironmentWorld.Start();
        var teammate = EnvironmentWorld.Player(2);
        EnvironmentWorld.Local(1);
        world.Hud.Value(Overhead(new[] { teammate }, 10));
        Placed(2);
        var first = UnityEngine.Object.Created.Single();

        // The same world epoch is one world; a command from the next one releases everything the last one drew.
        var next = Contexts.Command(HudContract.ValueCapability,
            new { viewers = new[] { teammate }, value = 1d, visible = true },
            new { placement = 0, form = 0, audience = 0 }, isHost: false, worldEpoch: 2);
        Assert.Equal("succeeded", world.Hud.Value(next).Status);

        Assert.Contains(first, UnityEngine.Object.Destroyed);
    }

    [Fact]
    public void TheOverheadPlacementRefusesABarBecauseAMarkerHasNoSprite()
    {
        using var world = EnvironmentWorld.Start();
        var teammate = EnvironmentWorld.Player(2);
        EnvironmentWorld.Local(1);
        var context = Contexts.Command(HudContract.ValueCapability,
            new { viewers = new[] { teammate }, value = 1d, visible = true, maximum = 100d },
            new { placement = 2, form = 3, audience = 0 }, isHost: false);

        Assert.Equal(HudActions.FormCode, world.Hud.HandleValue(context).Code);
        Assert.Empty(UnityEngine.Object.Created);
    }

    [Fact]
    public void TheOverheadColourIsWrittenIntoTheLine()
    {
        using var world = EnvironmentWorld.Start();
        var teammate = EnvironmentWorld.Player(2);
        EnvironmentWorld.Local(1);

        world.Hud.Value(Overhead(new[] { teammate }, 42, color: "#3366ff"));
        Placed(2);

        var text = UnityEngine.Object.Created.Single().GetComponent<TMPro.TextMeshPro>()!;
        Assert.Equal(0x33 / 255f, text.color.r, 3);
        Assert.Equal(0x66 / 255f, text.color.g, 3);
        Assert.Equal(1f, text.color.b, 3);
    }

    /// <summary>
    /// The checklist's own acceptance for `audience=self`, driven through the status bar: four players carry four
    /// different values, every machine is handed the one value that belongs to it, and each machine's own readout
    /// ends up carrying its own number and nobody else's. The four machines differ in the one fact that decides
    /// this — which player is sitting at the keyboard — because that is the whole audience question.
    /// </summary>
    [Fact]
    public void EveryMachineDrawsOnlyItsOwnValueWhenFourPlayersCarryFourValues()
    {
        using var world = EnvironmentWorld.Start();
        var players = new[] { 1, 2, 3, 4 };
        var values = new[] { 10d, 20d, 30d, 40d };
        var lived = new[]
        {
            EnvironmentWorld.Player(1, 1111, 0),
            EnvironmentWorld.Player(2, 2222, 1),
            EnvironmentWorld.Player(3, 3333, 2),
            EnvironmentWorld.Player(4, 4444, 3)
        };

        for (var machine = 0; machine < players.Length; machine++)
        {
            EnvironmentWorld.Local(players[machine]);
            var context = Contexts.Command(HudContract.ValueCapability,
                new { viewers = new[] { lived[machine] }, value = values[machine], visible = true },
                new { placement = 0, form = 0, audience = 1 }, isHost: false);

            var result = world.Hud.Value(context, Session(machine));

            Assert.Equal("succeeded", result.Status);
            Assert.Equal((float)values[machine], Shield);
        }
    }

    /// <summary>The address an `audience=self` command is sent to: the game's own player slot, which is the one
    /// session spelling the routing and the local-session comparison share. The account id is not a session and is
    /// never formatted into one.</summary>
    private static string Session(int machine) => machine.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static float? Shield => GuiManager.Current!.m_playerLayer!.m_playerStatus!.Shield;

    [Fact]
    public void AMachineThatIsNotTheValuesOwnerRefusesTheCommandByItsOwnName()
    {
        using var world = EnvironmentWorld.Start();
        var owner = EnvironmentWorld.Player(2, 2222, 1);
        EnvironmentWorld.Local(3);

        var context = Contexts.Command(HudContract.ValueCapability,
            new { viewers = new[] { owner }, value = 20d, visible = true },
            new { placement = 0, form = 0, audience = 1 }, isHost: false);

        // The command was addressed to the value's own machine — the owner's slot — and this machine is another
        // player's, so the self audience is somebody else's session and the row refuses it by name.
        Assert.Equal(HudActions.AddressCode, world.Hud.Value(context, "1").Code);
        Assert.Null(Shield);
    }

    [Fact]
    public void AMachineNoValueWasAddressedToRefusesTheSelfAudience()
    {
        using var world = EnvironmentWorld.Start();
        var owner = EnvironmentWorld.Player(2, 2222, 1);
        EnvironmentWorld.Local(2);

        var context = Contexts.Command(HudContract.ValueCapability,
            new { viewers = new[] { owner }, value = 20d, visible = true },
            new { placement = 0, form = 0, audience = 1 }, isHost: false);

        Assert.Equal(HudActions.AddressCode, world.Hud.Value(context, null).Code);
        Assert.Null(Shield);
    }
}
