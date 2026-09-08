using BepInEx.Configuration;
using UnityEngine;

namespace InfiniTweaks;

internal static partial class Settings
{
    internal static ConfigEntry<bool> Deposit = null!, Stack = null!, Markers = null!, HideMarkersAiming = null!, ResourceHud = null!, HudBold = null!, Stats = null!, TeamStats = null!, EstimateStats = null!, EndStats = null!, PerfectBoosters = null!, NoBoosterConsume = null!, Farmer = null!;
    internal static ConfigEntry<int> StackUses = null!;
    internal static ConfigEntry<float> MarkerDistance = null!, MarkerOpacity = null!, MarkerScale = null!, HudScale = null!, HudEmphasisScale = null!, FarmerMultiplier = null!;
    internal static ConfigEntry<KeyCode> StatsKey = null!, ClearMarkersKey = null!;
    internal static ConfigEntry<bool> Drop = null!;
    internal static ConfigEntry<KeyCode> DropKey = null!;

    private static void BindCasual(ConfigFile config)
    {
        Drop = config.Bind("Resources", "EnableItemDrop", false, "Put your held resource pack or consumable on nearby solid ground using DropItemKey. Preserves the same item and remaining amount. Weapons/tools cannot be dropped; objective carry items keep their native controls. Do not run another DropItem mod at the same time.");
        DropKey = config.Bind("Resources", "DropItemKey", KeyCode.G, "Drop the held resource pack or consumable while looking at reachable ground. Default G; change this if another mod/action already uses G. Only active in gameplay, never while typing or using menus.");
        Deposit = config.Bind("Resources", "EnableContainerDeposit", false, "Hold the game's Use key while aiming at an empty matching slot in an open box or locker to put back a resource pack or consumable. The preview shows the destination. Existing items and remaining uses are preserved. Do not run another DropItem mod at the same time.");
        Stack = config.Bind("Resources", "EnableResourceStacking", false, "Host only: picking up a matching resource pack combines the remaining amounts. 2 uses + 2 uses = 4 uses. Excess stays in the world; different types still swap normally. Does not stack grenades, glowsticks or objective items.");
        StackUses = config.Bind("Resources", "MaxResourcePackUses", 5, new ConfigDescription("Maximum uses in a combined pack. Excess stays available, never deleted. This release supports the native capacity of up to 5 uses; larger capacities require a separate synchronization change.", new AcceptableValueRange<int>(1, 5)));
        Markers = config.Bind("Item Markers", "EnablePersistentItemMarkers", false, "Remember the exact pickup you ping or identify through a terminal. Teammates need this version with markers enabled to share player item pings; coordinate-only pings from other clients stay vanilla. Never guesses a nearby item or reveals closed-container contents. Display only nearby items; hide carried items and restore them when put down. Re-pinging does not duplicate icons.");
        MarkerDistance = config.Bind("Item Markers", "MarkerMaxDistanceMeters", 30f, new ConfigDescription("Only show remembered items within this distance and in your dimension. Moving farther away hides, but does not forget, the item.", new AcceptableValueRange<float>(5f, 100f)));
        HideMarkersAiming = config.Bind("Item Markers", "HideMarkersWhileAiming", true, "Hide this mod's item markers while aiming. Original objective, enemy and downed-player markers are not changed.");
        MarkerOpacity = config.Bind("Item Markers", "MarkerOpacity", 0.65f, new ConfigDescription("Item marker opacity: 0.1 = faint, 1 = opaque.", new AcceptableValueRange<float>(0.1f, 1f)));
        MarkerScale = config.Bind("Item Markers", "MarkerScale", 0.4f, new ConfigDescription("Item marker icon scale. Uses the game's HUD positioning and screen scaling.", new AcceptableValueRange<float>(0.2f, 1f)));
        ClearMarkersKey = config.Bind("Item Markers", "ClearMarkersKey", KeyCode.F8, "Clear all remembered item pings during gameplay. Hold Shift with this key to clear only the remembered item closest to your crosshair. Does not delete items.");
        ResourceHud = config.Bind("Resource HUD", "EnableResourceHUD", false, "Show compact colored teammate health, infection, ammunition, tools and carried pack uses. Only emphasize the relevant resource while YOU are holding its pack. No resource-pack emphasis when holding a gun, melee weapon or tool.");
        HudScale = config.Bind("Resource HUD", "NormalTextScale", 0.7f, new ConfigDescription("Normal teammate resource text size relative to the game's text. Normal text is never bold.", new AcceptableValueRange<float>(0.5f, 1f)));
        HudEmphasisScale = config.Bind("Resource HUD", "HighlightedTextScale", 1f, new ConfigDescription("Size of the relevant teammate resource while you hold a resource pack. Medical = health; ammo = main/special ammo; tool refill = tools; disinfect = infection.", new AcceptableValueRange<float>(0.7f, 1.5f)));
        HudBold = config.Bind("Resource HUD", "BoldWhenHoldingPack", true, "Bold only the resource relevant to the pack currently in your hands. Switching away immediately removes this emphasis.");
        Stats = config.Bind("Statistics", "EnableStats", false, "Track firearm pellet accuracy and effective enemy damage. Peers using this version with stats enabled share exact accuracy even if the host does not use stats. Complete damage requires a participating host; otherwise it is unavailable. Rejoining starts your accuracy from zero without freezing other players. Piercing pellets count once for accuracy but can damage multiple enemies. Estimates and missing values are labeled.");
        TeamStats = config.Bind("Statistics", "ShowTeamStats", true, "Include teammates in roster order, not ranked by damage. Receiving peers must have this version of Infini Tweaks; standalone StatDisplay does not share this protocol.");
        EstimateStats = config.Bind("Statistics", "ShowEstimatedRemoteStats", true, "When hosting, show observed accuracy for peers without this mod as estimates. Clients without a participating host cannot obtain complete remote damage. Custom projectile weapon accuracy is not supported by this release.");
        EndStats = config.Bind("Statistics", "ShowEndScreenStats", true, "Show the current attempt's table on success/failure screens. Checkpoint restores start a new attempt so retries are not mixed. F7 (or your configured key) dismisses the table.");
        StatsKey = config.Bind("Statistics", "StatsKey", KeyCode.F7, "Toggle the compact statistics table during gameplay. Shift + this key toggles per-weapon details. No automatic chat messages.");
        PerfectBoosters = config.Bind("Boosters", "PerfectBoosterRolls", false, "Use the best allowed roll for existing booster effects from their matching native template. Does not add effects, remove conditions or disable consumption. Unknown historical templates remain unchanged with a warning. Restart required.");
        NoBoosterConsume = config.Bind("Boosters", "PreventBoosterConsumption", false, "Prevent equipped boosters being consumed locally and submitted for session consumption. Separate from perfect rolls. Does not prevent you deliberately discarding a booster. Restart required; does not restore previously spent boosters.");
        Farmer = config.Bind("Boosters", "EnableBoosterFarmer", false, "Multiply normally earned artifact booster currency using BoosterRewardMultiplier. Zero earned reward stays zero. This changes persistent progression, not just the HUD. No automatic missions or fabricated completions.");
        FarmerMultiplier = config.Bind("Boosters", "BoosterRewardMultiplier", 5f, new ConfigDescription("Booster currency multiplier while farming is enabled: 1 = normal, 5 = five times. Not a count of boosters. Result is rounded down and bounded to 100000 currency per calculation.", new AcceptableValueRange<float>(1f, 100f)));
    }
}
