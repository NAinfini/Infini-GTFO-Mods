using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// The light-colour row: the one environment action the game has no entry for, so every case reads the light
/// objects themselves rather than an event. What a case checks is the three facts the row decides — which lights a
/// request drives, what value a transition is at on a given frame, and which requests are refused before any light
/// is touched — because those are the whole of what this package writes.
/// </summary>
public sealed class EnvironmentLightColorTests
{
    private static object Zones(params object[] zones) => new { zones };

    private static int Rows(CommandResult result) => result.Outputs.GetProperty("results").GetArrayLength();

    /// <summary>One refusal: the request is handed to the production handler and the code it answers with is the
    /// whole of what the case checks, because a refused request must not have written anything either.</summary>
    private static void Refuses(EnvironmentWorld world, string code, object inputs, object parameters)
    {
        var result = world.Host.HandleLightColor(
            Contexts.Command(EnvironmentContract.LightColorCapability, inputs, parameters));
        Assert.Equal("rejected", result.Status);
        Assert.Equal(code, result.Code);
        Assert.Equal(CommitStates.None, result.CommitState);
        Assert.Equal(0, LightColorFades.Count);
        Assert.True(EnvironmentWorld.Lights(0)[0].m_color.SameAs(UnityEngine.Color.white));
    }

    [Fact]
    public void ColourAndBrightnessAreSpreadOverTheNamedTransition()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 2));
        var context = Contexts.Command(EnvironmentContract.LightColorCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 1.0, 0.0, 0.0 }, brightness = 0.5, transition = 2.0 },
            new { scope = 0 });

        var result = world.Host.HandleLightColor(context);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(CommitStates.Confirmed, result.CommitState);
        Assert.Equal(1, Rows(result));
        Assert.Equal(1, LightColorFades.Count);
        // Nothing is written when the transition is scheduled: the frames that follow are the write.
        Assert.Equal(1f, EnvironmentWorld.Lights(0)[0].Intensity);
        Assert.True(EnvironmentWorld.Lights(0)[0].m_color.SameAs(UnityEngine.Color.white));

        LightColorFades.Tick(1, 1f);

        var light = EnvironmentWorld.Lights(0)[0];
        Assert.Equal(1f, light.m_color.r, 5);
        Assert.Equal(0.5f, light.m_color.g, 5);
        Assert.Equal(0.5f, light.m_color.b, 5);
        Assert.Equal(0.75f, light.Intensity, 5);
        Assert.Equal(1, LightColorFades.Count);

        LightColorFades.Tick(1, 1f);

        Assert.Equal(1f, light.m_color.r, 5);
        Assert.Equal(0f, light.m_color.g, 5);
        Assert.Equal(0f, light.m_color.b, 5);
        Assert.Equal(0.5f, light.Intensity, 5);
        // A transition that reached its own end leaves the table, so no later frame writes it again.
        Assert.Equal(0, LightColorFades.Count);
    }

    [Fact]
    public void ASecondRequestContinuesFromWhatTheLightShowsAndReplacesTheFirst()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 1));
        var first = Contexts.Command(EnvironmentContract.LightColorCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 1.0, 0.0, 0.0 }, transition = 2.0 },
            new { scope = 0 });
        Assert.Equal("succeeded", world.Host.HandleLightColor(first).Status);
        LightColorFades.Tick(1, 1f);
        var light = EnvironmentWorld.Lights(0)[0];
        Assert.Equal(0.5f, light.m_color.g, 5);

        // The second request arrives mid-transition: it starts where the light is now, not where the first one
        // started, and the first one stops driving the light rather than dragging it back.
        var second = Contexts.Command(EnvironmentContract.LightColorCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 0.0, 1.0, 0.0 }, transition = 1.0 },
            new { scope = 0 });
        Assert.Equal("succeeded", world.Host.HandleLightColor(second).Status);
        Assert.Equal(1, LightColorFades.Count);

        LightColorFades.Tick(1, 1f);

        Assert.Equal(0f, light.m_color.r, 5);
        Assert.Equal(1f, light.m_color.g, 5);
        Assert.Equal(0f, light.m_color.b, 5);
        Assert.Equal(0, LightColorFades.Count);
    }

    [Fact]
    public void ARequestWithNoLengthIsTheWriteItself()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 1));
        var context = Contexts.Command(EnvironmentContract.LightColorCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 0.0, 0.0, 1.0 } },
            new { scope = 0 });

        var result = world.Host.HandleLightColor(context);

        Assert.Equal("succeeded", result.Status);
        var light = EnvironmentWorld.Lights(0)[0];
        Assert.Equal(1f, light.m_color.b, 5);
        Assert.Equal(1f, light.Intensity, 5);
        Assert.Equal(0, LightColorFades.Count);
    }

    [Fact]
    public void TheCategoryNarrowsTheLightsTheTransitionDrives()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 2));
        var lights = EnvironmentWorld.Lights(0);
        lights[0].m_category = LG_Light.LightCategory.General;
        lights[1].m_category = LG_Light.LightCategory.Door;
        var context = Contexts.Command(EnvironmentContract.LightColorCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 1.0, 0.0, 0.0 } },
            new { scope = 0, category = 4 });

        Assert.Equal("succeeded", world.Host.HandleLightColor(context).Status);

        Assert.True(lights[0].m_color.SameAs(UnityEngine.Color.white));
        Assert.Equal(1f, lights[1].m_color.r, 5);
        Assert.Equal(0f, lights[1].m_color.g, 5);
    }

    [Fact]
    public void ExpeditionScopeDrivesEveryZoneTheLevelHolds()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 1), (0, 1, 4, 2));
        var context = Contexts.Command(EnvironmentContract.LightColorCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 0.0, 1.0, 0.0 } },
            new { scope = 1 });

        var result = world.Host.HandleLightColor(context);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(2, Rows(result));
        Assert.All(EnvironmentWorld.Lights(0), light => Assert.Equal(1f, light.m_color.g, 5));
        Assert.All(EnvironmentWorld.Lights(1), light => Assert.Equal(1f, light.m_color.g, 5));
    }

    [Fact]
    public void ARequestNamingNeitherColourNorBrightnessIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 1));

        Refuses(world, EnvironmentActions.LightColorCode,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, transition = 5.0 }, new { scope = 0 });
    }

    [Fact]
    public void AColourOutsideTheUnitRangeIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 1));

        Refuses(world, EnvironmentActions.ColorCode,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 2.0, 0.0, 0.0 } }, new { scope = 0 });
    }

    [Fact]
    public void AnUnknownCategoryIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 1));

        // The port is a structural enum, so a dispatch carries a member's name; a name outside the vocabulary is
        // refused by name instead of being read as "every category", which is what an absent port means.
        Refuses(world, EnvironmentActions.CategoryCode,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 1.0, 0.0, 0.0 } },
            new { scope = 0, category = "spotlight" });
    }

    [Fact]
    public void ANegativeBrightnessOrTransitionIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 1));

        Refuses(world, EnvironmentActions.BrightnessCode,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, brightness = -0.5 }, new { scope = 0 });
        Refuses(world, EnvironmentActions.TransitionCode,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 1.0, 0.0, 0.0 }, transition = -1.0 },
            new { scope = 0 });
    }

    [Fact]
    public void AZoneTheLevelDoesNotHaveAndAZoneWithNoSuchLightAreRefusedByName()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 1));

        Refuses(world, EnvironmentQuery.ZoneMissingCode,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 5) }, color = new[] { 1.0, 0.0, 0.0 } }, new { scope = 0 });
        Refuses(world, EnvironmentActions.NoLightsCode,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 1.0, 0.0, 0.0 } },
            new { scope = 0, category = 4 });

        EnvironmentWorld.Level((0, 1, 4, 0));
        var empty = world.Host.HandleLightColor(Contexts.Command(EnvironmentContract.LightColorCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 1, 4) }, color = new[] { 1.0, 0.0, 0.0 } }, new { scope = 0 }));
        Assert.Equal(EnvironmentActions.NoLightsCode, empty.Code);
    }

    [Fact]
    public void ARequestFromANonAuthoritativeSessionIsRefused()
    {
        using var world = EnvironmentWorld.Start(canExecute: false);
        EnvironmentWorld.Level((0, 0, 3, 1));
        var context = Contexts.Command(EnvironmentContract.LightColorCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 1.0, 0.0, 0.0 } },
            new { scope = 0 }, isHost: false);

        var result = world.Host.HandleLightColor(context);

        Assert.Equal("rejected", result.Status);
        Assert.Equal(EnvironmentActions.AuthorityCode, result.Code);
        Assert.Equal(0, LightColorFades.Count);
    }

    [Fact]
    public void AFrameFromAnotherWorldDropsTheTransitionsInsteadOfWritingThem()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 1));
        var context = Contexts.Command(EnvironmentContract.LightColorCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, color = new[] { 1.0, 0.0, 0.0 }, transition = 2.0 },
            new { scope = 0 });
        Assert.Equal("succeeded", world.Host.HandleLightColor(context).Status);

        LightColorFades.Tick(2, 1f);

        Assert.Equal(0, LightColorFades.Count);
        Assert.True(EnvironmentWorld.Lights(0)[0].m_color.SameAs(UnityEngine.Color.white));
    }

    [Fact]
    public void TheRowDeclaresExactlyThePortsTheHandlerReads()
    {
        var graph = Contexts.Graph(EnvironmentContract.LightColorCapability);
        var inputs = graph.GetProperty("inputs").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray();
        var parameters = graph.GetProperty("parameters").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray();

        Assert.Equal(new[] { "in", "zones", "color", "brightness", "transition" }, inputs);
        Assert.Equal(new[] { "scope", "category" }, parameters);
        Assert.Equal("host", graph.GetProperty("execution").GetString());
        Assert.Equal("zones", graph.GetProperty("recipients").GetProperty("input").GetString());
        // The vocabulary and the game's own enum are one list: the member an author picks is the index the game
        // indexes a light's `m_category` with.
        Assert.Equal(EnvironmentContract.LightCategories.Length, Enum.GetValues<LG_Light.LightCategory>().Length);
    }
}
