using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using GameData;
using LevelGeneration;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// The five hosted environment rows: lights, fog, the repeating-fog pair, the navigation marker and the scene
/// animation trigger. Every case drives the production handler and reads the one event it handed the game's own
/// level-event entry, because that event — its type member and its field values — is the whole of what the row
/// decides.
/// </summary>
public sealed class EnvironmentActionTests
{
    private static object Zones(params object[] zones) => new { zones };

    [Fact]
    public void ZoneLightsRunTheZoneEntryOncePerNamedZone()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            Zones(EnvironmentWorld.Zone(0, 0, 3), EnvironmentWorld.Zone(0, 1, 4)),
            new { scope = 0, mode = 1 });

        var result = world.Host.HandleLighting(context);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(2, EnvironmentWorld.EventCount);
        var first = WorldEventManager.Executed[0].Data;
        Assert.Equal(eWardenObjectiveEventType.LightsInZone, first.Type);
        Assert.False(first.Enabled);
        Assert.Equal((LG_LayerType)0, first.Layer);
        Assert.Equal((eLocalZoneIndex)3, first.LocalIndex);
        Assert.Equal(EnvironmentActions.NoCondition, first.Condition.ConditionIndex);
        Assert.Equal((eLocalZoneIndex)4, WorldEventManager.Executed[1].Data.LocalIndex);
    }

    [Fact]
    public void ZoneLightsCarryTheTransitionPositionAndCount()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, transition = 750, position = new[] { 1.0, 0.0, 1000.0 }, count = 5 },
            new { scope = 0, mode = 0 });

        Assert.Equal("succeeded", world.Host.HandleLighting(context).Status);
        var data = EnvironmentWorld.LastEvent!;
        Assert.Equal(eWardenObjectiveEventType.LightsInZone, data.Type);
        Assert.True(data.Enabled);
        // The port is declared in ticks and the native field takes seconds: 750 ticks is 12.5 s.
        Assert.Equal(12.5f, data.Duration);
        Assert.Equal(5, data.Count);
        Assert.Equal(1f, data.Position.x);
        Assert.Equal(1000f, data.Position.z);
    }

    [Fact]
    public void AbsentOptionalPortsLeaveTheEventsOwnDefaults()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            Zones(EnvironmentWorld.Zone(2, 2, 1)), new { scope = 0, mode = 2 });

        Assert.Equal("succeeded", world.Host.HandleLighting(context).Status);
        var data = EnvironmentWorld.LastEvent!;
        Assert.Equal(eWardenObjectiveEventType.LightsInZoneToggle, data.Type);
        Assert.Equal(0f, data.Duration);
        Assert.Equal(0, data.Count);
        Assert.Equal((eDimensionIndex)2, data.DimensionIndex);
    }

    [Fact]
    public void ExpeditionLightsRunTheLevelWideEntryOnce()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            Zones(EnvironmentWorld.Zone(0, 0, 3), EnvironmentWorld.Zone(0, 1, 4)), new { scope = 1, mode = 1 });

        Assert.Equal("succeeded", world.Host.HandleLighting(context).Status);
        Assert.Equal(1, EnvironmentWorld.EventCount);
        Assert.Equal(eWardenObjectiveEventType.AllLightsOff, EnvironmentWorld.LastEvent!.Type);
    }

    [Fact]
    public void ExpeditionToggleIsRefusedBecauseTheEventTableHasNoLevelWideFlip()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            Zones(EnvironmentWorld.Zone(0, 0, 3)), new { scope = 1, mode = 2 });

        var result = world.Host.HandleLighting(context);

        Assert.Equal("rejected", result.Status);
        Assert.Equal(EnvironmentActions.ModeCode, result.Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void AnEmptyTargetCollectionIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            new { zones = System.Array.Empty<object>() }, new { scope = 0, mode = 0 });

        var result = world.Host.HandleLighting(context);

        Assert.Equal("rejected", result.Status);
        Assert.Equal(EnvironmentActions.NoTargetCode, result.Code);
    }

    [Fact]
    public void AnEntityOfAnotherKindIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            Zones(EnvironmentWorld.Foreign()), new { scope = 0, mode = 0 });

        var result = world.Host.HandleLighting(context);

        Assert.Equal(EnvironmentActions.TargetCode, result.Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void AReferenceFromAnotherWorldIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var stale = new ForgeRuntime.Framework.EntityReference("gtfo.zone:0:0:3", 9, 1);
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            Zones(stale), new { scope = 0, mode = 0 });

        var result = world.Host.HandleLighting(context);

        Assert.Equal(EnvironmentActions.StaleTargetCode, result.Code);
    }

    [Fact]
    public void AMalformedPositionIsRefusedRatherThanNarrowed()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            new { zones = new[] { EnvironmentWorld.Zone(0, 0, 3) }, position = new[] { 1.0, 2.0 } },
            new { scope = 0, mode = 0 });

        var result = world.Host.HandleLighting(context);

        Assert.Equal(EnvironmentActions.PositionCode, result.Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void AClientCannotHostAnEnvironmentRow()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            Zones(EnvironmentWorld.Zone(0, 0, 3)), new { scope = 0, mode = 0 }, isHost: false);

        var result = world.Host.HandleLighting(context);

        Assert.Equal(EnvironmentActions.AuthorityCode, result.Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void AnUnreadySessionRefusesBeforeTheEntry()
    {
        using var world = EnvironmentWorld.Start(canExecute: false);
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            Zones(EnvironmentWorld.Zone(0, 0, 3)), new { scope = 0, mode = 0 });

        Assert.Equal(EnvironmentActions.AuthorityCode, world.Host.HandleLighting(context).Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void ATornDownLevelRefusesRatherThanCallingTheEntry()
    {
        using var world = EnvironmentWorld.Start();
        WorldEventManager.Current = null;
        var context = Contexts.Command(EnvironmentContract.FogCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3), fog = 12 }, null);

        Assert.Equal(EnvironmentActions.UnavailableCode, world.Host.HandleFog(context).Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void FogCarriesThePresetTheDurationAndTheZonesDimension()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.FogCapability,
            new { zone = EnvironmentWorld.Zone(2, 1, 4), fog = 175, transition = 1800 }, null);

        var result = world.Host.HandleFog(context);

        Assert.Equal("succeeded", result.Status);
        var data = EnvironmentWorld.LastEvent!;
        Assert.Equal(eWardenObjectiveEventType.SetFogSetting, data.Type);
        Assert.Equal(175u, data.FogSetting);
        // 1800 ticks is the 30 s the native transition field takes.
        Assert.Equal(30f, data.FogTransitionDuration);
        Assert.Equal((eDimensionIndex)2, data.DimensionIndex);
        Assert.Equal((eLocalZoneIndex)4, data.LocalIndex);
    }

    [Fact]
    public void FogWithoutAPresetIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.FogCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3) }, null);

        Assert.Equal(EnvironmentActions.FogRequiredCode, world.Host.HandleFog(context).Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void TheRepeatingFogStartsOneSustainedSlotWithItsOwnLoopParameters()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.FogCycleCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3), fog = 176, transition = 300, state_duration = 2700, start_delay = 1800, sound = 7 },
            new { mode = 0, slot = 2, states = -1 });

        var result = world.Host.HandleFogCycle(context);

        Assert.Equal("succeeded", result.Status);
        var data = WorldEventManager.Executed[^1].Data;
        Assert.Equal(eWardenObjectiveEventType.StartRepeatingFog, data.Type);
        Assert.Equal(2, data.SustainedEventSlotIndex);
        Assert.Equal(-1, data.SustainedEventStateCount);
        // Every one of these ports is ticks: 2700 ticks is 45 s, 1800 ticks is 30 s.
        Assert.Equal(45f, data.SustainedEventStateDuration);
        Assert.Equal(30f, data.SustainedEventDelay);
        Assert.Equal(176u, data.FogSetting);
        Assert.Equal(7u, data.SoundID);
    }

    [Fact]
    public void TheRepeatingFogStopsTheSameSlotAndReadsNoLoopFields()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.FogCycleCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3), fog = 176, state_duration = 45.0 },
            new { mode = 1, slot = 2 });

        Assert.Equal("succeeded", world.Host.HandleFogCycle(context).Status);
        var data = WorldEventManager.Executed[^1].Data;
        Assert.Equal(eWardenObjectiveEventType.StopSustainedEvent, data.Type);
        Assert.Equal(2, data.SustainedEventSlotIndex);
        Assert.Equal(0f, data.SustainedEventStateDuration);
        Assert.Equal(0u, data.FogSetting);
    }

    [Fact]
    public void ARepeatingFogStartWithoutAPresetIsRefusedButAStopIsNot()
    {
        using var world = EnvironmentWorld.Start();
        var start = Contexts.Command(EnvironmentContract.FogCycleCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3) }, new { mode = 0, slot = 1 });
        var stop = Contexts.Command(EnvironmentContract.FogCycleCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3) }, new { mode = 1, slot = 1 });

        Assert.Equal(EnvironmentActions.FogRequiredCode, world.Host.HandleFogCycle(start).Code);
        Assert.Equal("succeeded", world.Host.HandleFogCycle(stop).Status);
    }

    [Fact]
    public void ARepeatingFogWithoutASlotIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.FogCycleCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3), fog = 176 }, new { mode = 0 });

        Assert.Equal(EnvironmentActions.SlotRequiredCode, world.Host.HandleFogCycle(context).Code);
    }

    [Fact]
    public void TheNavMarkerCarriesTheObjectNameAndTheFlag()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.NavMarkerCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3), filter = "WE_Terminal_nav_Z5_01", enabled = true }, null);

        Assert.Equal("succeeded", world.Host.HandleNavMarker(context).Status);
        var data = EnvironmentWorld.LastEvent!;
        Assert.Equal(eWardenObjectiveEventType.SetNavMarker, data.Type);
        Assert.Equal("WE_Terminal_nav_Z5_01", data.WorldEventObjectFilter);
        Assert.True(data.Enabled);
    }

    [Fact]
    public void AMarkerWithoutAnObjectNameIsRefused()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.NavMarkerCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3), enabled = true }, null);

        Assert.Equal(EnvironmentActions.FilterRequiredCode, world.Host.HandleNavMarker(context).Code);
        Assert.Equal(0, EnvironmentWorld.EventCount);
    }

    [Fact]
    public void AnAnimationTriggerResetsWithTheSameEntryWhenTheFlagIsFalse()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.AnimationCapability,
            new { zone = EnvironmentWorld.Zone(1, 0, 2), filter = "Evt_LightsOnTrigger", enabled = false }, null);

        Assert.Equal("succeeded", world.Host.HandleAnimation(context).Status);
        var data = EnvironmentWorld.LastEvent!;
        Assert.Equal(eWardenObjectiveEventType.AnimationTrigger, data.Type);
        Assert.False(data.Enabled);
        Assert.Equal((eDimensionIndex)1, data.DimensionIndex);
    }

    [Fact]
    public void AnEntryThatThrowsIsReportedAsAnUnknownCommitAndNotAsASuccess()
    {
        using var world = EnvironmentWorld.Start();
        WorldEventManager.Throw = new System.InvalidOperationException("boom");
        var context = Contexts.Command(EnvironmentContract.AnimationCapability,
            new { zone = EnvironmentWorld.Zone(0, 0, 3), filter = "Evt", enabled = true }, null);

        var result = world.Host.HandleAnimation(context);

        Assert.Equal("failed", result.Status);
        Assert.Equal(ForgeRuntime.Framework.CommitStates.Unknown, result.CommitState);
        Assert.Contains(world.Reports, line => line.StartsWith("map.environment-event-failed", System.StringComparison.Ordinal));
    }

    [Fact]
    public void AHostRowAnswersARowPerNamedTarget()
    {
        using var world = EnvironmentWorld.Start();
        var context = Contexts.Command(EnvironmentContract.LightingCapability,
            Zones(EnvironmentWorld.Zone(0, 0, 3), EnvironmentWorld.Zone(0, 0, 4)), new { scope = 0, mode = 0 });

        var result = world.Host.HandleLighting(context);

        var rows = JsonDocument.Parse(result.Outputs.GetRawText()).RootElement.GetProperty("results");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("gtfo.zone:0:0:4", rows[1].GetProperty("target").GetProperty("id").GetString());
        Assert.Equal(ForgeRuntime.Framework.CommitStates.Confirmed, rows[0].GetProperty("committed").GetString());
    }
}
