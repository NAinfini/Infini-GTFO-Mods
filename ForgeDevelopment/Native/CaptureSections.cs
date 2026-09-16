namespace ForgeDevelopment.Native;

/// <summary>The section names a snapshot uses. They are constants because three places agree on them: the collector
/// that fills them, the change pass that diffs them and the field table the tests check against.</summary>
internal static class CaptureSections
{
    internal const string Level = "level";
    internal const string Zones = "zones";
    internal const string Doors = "doors";
    internal const string Terminals = "terminals";
    internal const string Generators = "generators";
    internal const string Containers = "containers";
    internal const string Pickups = "pickups";
    internal const string Objectives = "objectives";
    internal const string ChainedPuzzles = "chainedPuzzles";
    internal const string Players = "players";
    internal const string Enemies = "enemies";
    internal const string NavMarkers = "navMarkers";
    internal const string HudText = "hudText";
    internal const string Environment = "environment";
    internal const string DataBlocks = "datablocks";
    internal const string Session = "session";
}
