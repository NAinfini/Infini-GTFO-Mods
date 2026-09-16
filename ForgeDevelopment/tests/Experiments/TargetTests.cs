using System.Text.Json;
using ForgeDevelopment.Native;

internal static class TargetTests
{
    internal static void Run(Suite suite)
    {
        foreach (var accepted in new[]
        {
            "localPlayer", "localPlayerStatus", "guiManager", "navMarkerLayer",
            "lookedAtEnemy", "lookedAtDoor", "lookedAtTerminal", "lookedAtGenerator",
            "nearestDoor", "nearestGenerator", "nearestTerminal",
            "zone:0/MainLayer/4", "zone:1/SecondaryLayer/20",
            "static:LevelGeneration.WorldEventManager", "allOfType:Enemies.EnemyAgent", "allOfType:LevelGeneration.LG_*Door"
        })
            suite.Check("target.accept." + accepted, ExperimentTargetSpec.TryParse(accepted, out _));

        foreach (var rejected in new[]
        {
            "", "player", "lookedAtEnemy:maxDistance=0", "lookedAtEnemy:maxDistance=x", "lookedAtEnemy:depth=2",
            "zone:0/MainLayer", "zone:0/MainLayer/99", "zone:x/MainLayer/1", "zone:0/Middle/1",
            "static:", "allOfType:", "static:No Such Type", "zone:0/MainLayer/1/2"
        })
            suite.Check("target.reject." + (rejected.Length == 0 ? "<empty>" : rejected), !ExperimentTargetSpec.TryParse(rejected, out _));

        ExperimentTargetSpec.TryParse("zone:0/MainLayer/4", out var zone);
        suite.Equal("target.zoneKind", ExperimentTargetKind.Zone, zone.Kind);
        suite.Equal("target.zoneLocalIndex", "4", zone.LocalIndex);
        ExperimentTargetSpec.TryParse("nearestDoor:maxDistance=30,layer=SecondaryLayer", out var nearest);
        suite.Equal("target.optionDistance", 30f, nearest.MaximumDistance);
        suite.Equal("target.optionLayer", "SecondaryLayer", nearest.LayerFilter);
        suite.Check("target.nearestKind", nearest.Kind == ExperimentTargetKind.NearestDoor);
        ExperimentTargetSpec.TryParse("allOfType:LevelGeneration.LG_*Door", out var all);
        suite.Check("target.wildcard", all.Kind == ExperimentTargetKind.AllOfType && all.TypeName.EndsWith("*Door"));
    }
}
