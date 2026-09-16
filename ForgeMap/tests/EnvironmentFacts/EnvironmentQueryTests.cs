using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using SNetwork;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// The read-only environment row (`v-env`). A case checks the two facts it answers with, the two parameters that
/// address them, and the refusals that keep an unknown coordinate from being read as a real one.
/// </summary>
public sealed class EnvironmentQueryTests
{
    [Fact]
    public void TheRowAnswersTheDimensionsFogAndTheZonesLights()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentStateManager.FogId = 175;
        EnvironmentStateManager.LightOn = true;
        var context = Contexts.Evaluation(EnvironmentContract.EnvironmentStateCapability, null,
            new { dimension = 2, layer = 1, zone = 4 });

        var answer = EnvironmentQuery.Evaluate(context);

        Assert.Equal(175, answer.GetProperty("fog").GetInt64());
        Assert.True(answer.GetProperty("light").GetBoolean());
        Assert.Equal(2, EnvironmentStateManager.FogReads[^1]);
        Assert.Equal((2, 1, 4), EnvironmentStateManager.LightReads[^1]);
    }

    [Fact]
    public void TheRowAnswersOffAsOff()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentStateManager.FogId = 0;
        EnvironmentStateManager.LightOn = false;
        var context = Contexts.Evaluation(EnvironmentContract.EnvironmentStateCapability, null,
            new { dimension = 0, layer = 0, zone = 0 });

        var answer = EnvironmentQuery.Evaluate(context);

        Assert.Equal(0, answer.GetProperty("fog").GetInt64());
        Assert.False(answer.GetProperty("light").GetBoolean());
    }

    [Fact]
    public void ADimensionOutsideTheGamesOwnEnumIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Evaluation(EnvironmentContract.EnvironmentStateCapability, null,
            new { dimension = 99, layer = 0, zone = 0 });

        var error = Assert.Throws<RuntimeContractException>(() => EnvironmentQuery.Evaluate(context));
        Assert.Equal(EnvironmentQuery.DimensionCode, error.Code);
        Assert.Empty(EnvironmentStateManager.FogReads);
    }

    [Fact]
    public void ALayerOutsideTheGamesOwnThreeIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Evaluation(EnvironmentContract.EnvironmentStateCapability, null,
            new { dimension = 0, layer = "logic", zone = 0 });

        var error = Assert.Throws<RuntimeContractException>(() => EnvironmentQuery.Evaluate(context));
        Assert.Equal(EnvironmentQuery.LayerCode, error.Code);
    }

    [Fact]
    public void AZoneOutsideTheGamesOwnEnumIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Evaluation(EnvironmentContract.EnvironmentStateCapability, null,
            new { dimension = 0, layer = 0, zone = 99 });

        var error = Assert.Throws<RuntimeContractException>(() => EnvironmentQuery.Evaluate(context));
        Assert.Equal(EnvironmentQuery.ZoneCode, error.Code);
        Assert.Empty(EnvironmentStateManager.LightReads);
    }

    [Fact]
    public void ALevelThatIsGoneIsRefusedRatherThanAnsweredAsZero()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentStateManager.Current = null;
        var context = Contexts.Evaluation(EnvironmentContract.EnvironmentStateCapability, null,
            new { dimension = 0, layer = 0, zone = 0 });

        var error = Assert.Throws<RuntimeContractException>(() => EnvironmentQuery.Evaluate(context));
        Assert.Equal(EnvironmentQuery.UnavailableCode, error.Code);
    }

    [Fact]
    public void TheRowDeclaresTheWorldReadItPerforms()
    {
        var row = Contexts.Row(EnvironmentContract.EnvironmentStateCapability);
        var graph = row.GetProperty("graph");

        Assert.Equal("state", row.GetProperty("kind").GetString());
        Assert.Equal("query", graph.GetProperty("execution").GetString());
        var reads = graph.GetProperty("reads").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "world" }, reads);
        Assert.Empty(graph.GetProperty("inputs").EnumerateArray());
    }

    [Fact]
    public void AStepThatNamesNoPlayerItCanReachIsRefusedRatherThanWidenedToEveryone()
    {
        using var world = EnvironmentWorld.Start();

        // Nothing is named and no identity half is wired, so this process cannot name a session: the answer is
        // null, which the runtime's routing refuses by name instead of presenting the step to every machine.
        Assert.Null(PlayerSessions.SessionsOf(null));
        Assert.Null(PlayerSessions.SessionsOf(Array.Empty<EntityReference>()));
        Assert.Null(PlayerSessions.SessionsOf(new[] { new EntityReference("gtfo.player:1", 1, 1) }));
    }

    // ---- `v-zone-lights`: how many lights a zone holds and whether its own lights are on ----------------------

    [Fact]
    public void TheZoneLightsRowAnswersTheZonesOwnLightCountAndMode()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 4), (0, 1, 7, 0));
        EnvironmentStateManager.LightOn = true;

        var answer = EnvironmentQuery.ZoneLights(Contexts.Evaluation(EnvironmentContract.ZoneLightsCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3) }, null, Contexts.Session(EnvironmentWorld.Epoch)));

        Assert.Equal(4, answer.GetProperty("count").GetInt64());
        Assert.True(answer.GetProperty("on").GetBoolean());
        // The mode is read for the same zone the count came from, which is the whole point of one row.
        Assert.Equal((0, 0, 3), EnvironmentStateManager.LightReads[^1]);
    }

    [Fact]
    public void AZoneWithNoLightObjectsAnswersZeroRatherThanRefusing()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 0));
        EnvironmentStateManager.LightOn = false;

        var answer = EnvironmentQuery.ZoneLights(Contexts.Evaluation(EnvironmentContract.ZoneLightsCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3) }, null, Contexts.Session(EnvironmentWorld.Epoch)));

        Assert.Equal(0, answer.GetProperty("count").GetInt64());
        Assert.False(answer.GetProperty("on").GetBoolean());
    }

    [Fact]
    public void AZoneTheLevelDoesNotHaveIsRefusedRatherThanAnsweredAsEmpty()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 4));

        var error = Assert.Throws<RuntimeContractException>(() => EnvironmentQuery.ZoneLights(
            Contexts.Evaluation(EnvironmentContract.ZoneLightsCapability,
                new { zone = EnvironmentWorld.Zone(0, 0, 9) }, null, Contexts.Session(EnvironmentWorld.Epoch))));

        Assert.Equal(EnvironmentQuery.ZoneMissingCode, error.Code);
        Assert.Empty(EnvironmentStateManager.LightReads);
    }

    [Fact]
    public void AZoneOfAnotherWorldIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 4));

        var error = Assert.Throws<RuntimeContractException>(() => EnvironmentQuery.ZoneLights(
            Contexts.Evaluation(EnvironmentContract.ZoneLightsCapability,
                new { zone = EnvironmentWorld.Zone(0, 0, 3) }, null, Contexts.Session(EnvironmentWorld.Epoch + 1))));

        Assert.Equal(EnvironmentQuery.StaleZoneCode, error.Code);
    }

    [Fact]
    public void AnInputThatIsNotAZoneAddressIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        EnvironmentWorld.Level((0, 0, 3, 4));

        var error = Assert.Throws<RuntimeContractException>(() => EnvironmentQuery.ZoneLights(
            Contexts.Evaluation(EnvironmentContract.ZoneLightsCapability,
                new { zone = EnvironmentWorld.Foreign() }, null, Contexts.Session(EnvironmentWorld.Epoch))));

        Assert.Equal(EnvironmentQuery.ZoneInputCode, error.Code);
    }

    [Fact]
    public void TheZoneLightsRowDeclaresTheWorldReadItPerforms()
    {
        var row = Contexts.Row(EnvironmentContract.ZoneLightsCapability);
        var graph = row.GetProperty("graph");

        Assert.Equal("state", row.GetProperty("kind").GetString());
        Assert.Equal("query", graph.GetProperty("execution").GetString());
        Assert.Equal(new[] { "world" }, graph.GetProperty("reads").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(new[] { "zone" }, graph.GetProperty("inputs").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).ToArray());
        Assert.Equal(new[] { "on", "count" }, graph.GetProperty("outputs").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).ToArray());
    }
}
