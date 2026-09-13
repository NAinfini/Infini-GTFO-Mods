using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace InfiniTweaks;

internal static partial class Settings
{
    internal static ConfigEntry<bool> Deposit = null!, Markers = null!, ResourceHud = null!, HudBold = null!, PerfectBoosters = null!, NoBoosterConsume = null!, Farmer = null!;
    internal static ConfigEntry<float> HudEmphasisScale = null!, FarmerMultiplier = null!, HudAimOpacity = null!;
    internal static ConfigEntry<bool> HudDynamicOpacity = null!;
    internal const float MarkerScale = 0.72f;
    internal static ConfigEntry<bool> HudDistance = null!;
    internal static ConfigEntry<float> HudNameOpacity = null!, HudDistanceOpacity = null!;
    internal sealed class MarkerCategorySettings
    {
        internal readonly ConfigEntry<float> Distance;
        internal Color Color;
        internal MarkerCategorySettings(ConfigFile config, MarkerCategory category, float distance, string color)
        {
            string section = "Markers - " + category;
            Distance = config.Bind(section, "DistanceMeters", distance, new ConfigDescription("Display range for discovered objects in this category. Zero disables this category, including explicit pings. Does not discover objects through walls or closed containers.", new AcceptableValueRange<float>(0f, 100f)));
            ColorUtility.TryParseHtmlString(color, out Color);
        }
    }
    internal static readonly Dictionary<MarkerCategory, MarkerCategorySettings> MarkerCategories = new();
    internal static ConfigEntry<KeyCode> ClearMarkersKey = null!;
    internal static ConfigEntry<bool> Drop = null!;
    internal static ConfigEntry<KeyCode> DropKey = null!;

    private static void BindCasual(ConfigFile config)
    {
        Drop = config.Bind("Resources", "EnableItemDrop", false, "Put your held resource pack or consumable on nearby solid ground using DropItemKey. Preserves the same item and remaining amount. Weapons/tools cannot be dropped; objective carry items keep their native controls. Do not run another DropItem mod at the same time.");
        DropKey = config.Bind("Resources", "DropItemKey", KeyCode.G, "Drop the held resource pack or consumable while looking at reachable ground. Default G; change this if another mod/action already uses G. Only active in gameplay, never while typing or using menus.");
        Deposit = config.Bind("Resources", "EnableContainerDeposit", false, "Hold the game's Use key while aiming at an empty matching slot in an open box or locker to put back a resource pack or consumable. The preview shows the destination. Existing items and remaining uses are preserved. Do not run another DropItem mod at the same time.");
        Markers = config.Bind("Item Markers", "EnablePersistentItemMarkers", false, "Remember discovered pickups and interactive devices, not lockers/boxes. Discovery uses nearby living teammates, terminal interaction or targeted QUERY/PING. Never reveals closed-container contents. Resource pickups disappear; carried objectives follow their teammate and reanchor on drop. One icon/distance per carry item; native objective visibility resumes when our replacement is hidden.");
        foreach (var category in Enum.GetValues<MarkerCategory>())
        {
            var (distance, color) = MarkerRules.Defaults(category);
            MarkerCategories[category] = new(config, category, distance, color);
        }
        ClearMarkersKey = config.Bind("Item Markers", "ClearMarkersKey", KeyCode.F8, "Clear all remembered item pings during gameplay. Hold Shift with this key to clear only the remembered item closest to your crosshair. Does not delete items.");
        ResourceHud = config.Bind("Resource HUD", "EnableResourceHUD", false, "Keep normal native HUD names and icons. Show additional teammate percentages ONLY while YOU hold a matching resource pack: medical = health, ammo = main/special, tool refill = tool, disinfect = infection. No extra percentages while holding a gun, melee weapon or tool, even if teammates are low.");
        HudEmphasisScale = config.Bind("Resource HUD", "HighlightedTextScale", 1.2f, new ConfigDescription("Size of the relevant teammate resource while you hold a resource pack. Medical = health; ammo = main/special ammo; tool refill = tools; disinfect = infection.", new AcceptableValueRange<float>(0.7f, 1.5f)));
        HudBold = config.Bind("Resource HUD", "BoldWhenHoldingPack", true, "Bold only the resource relevant to the pack currently in your hands. Switching away immediately removes this emphasis.");
        HudAimOpacity = config.Bind("Resource HUD", "AimOpacity", 0.15f, new ConfigDescription("Opacity of resource rows while aiming. Names, distances and downed-player markers are independent. 1 disables aim fading.", new AcceptableValueRange<float>(0.05f, 1f)));
        HudDynamicOpacity = config.Bind("Resource HUD", "DynamicOpacity", true, "Slightly fade resource rows away from your crosshair, never below 85% opacity; looking toward them restores full brightness. Does not fade teammate names, distances or downed-player indicators.");
        HudNameOpacity = config.Bind("Resource HUD", "NameOpacity", 1f, new ConfigDescription("Living teammate name opacity, independent of resource rows and aiming. Rescue indicators are untouched.", new AcceptableValueRange<float>(0.1f, 1f)));
        HudDistance = config.Bind("Resource HUD", "ShowTeammateDistance", true, "Show native living-teammate distance independently of names and resource rows. False hides only the distance text; downed-player information is untouched.");
        HudDistanceOpacity = config.Bind("Resource HUD", "DistanceOpacity", 1f, new ConfigDescription("Living teammate distance opacity when ShowTeammateDistance is enabled; independent of resource-row fading.", new AcceptableValueRange<float>(0.1f, 1f)));
        PerfectBoosters = config.Bind("Boosters", "PerfectBoosterRolls", false, "Use the best allowed roll for existing booster effects from an exact current or authored historical template match. Does not add effects, remove conditions or disable consumption. Unmatched templates remain unchanged with a warning. Inventory and active-booster updates log checked/changed counts. Restart required.");
        NoBoosterConsume = config.Bind("Boosters", "PreventBoosterConsumption", false, "Prevent equipped boosters being consumed locally and submitted for session consumption. Separate from perfect rolls. Does not prevent you deliberately discarding a booster. Restart required; does not restore previously spent boosters.");
        Farmer = config.Bind("Boosters", "EnableBoosterFarmer", false, "Multiply earned artifact currency independently for each quality. Zero earnings remain zero. Changes persistent progression; native conversion and inventory/server limits still apply.");
        FarmerMultiplier = config.Bind("Boosters", "BoosterRewardMultiplier", 100000f, new ConfigDescription("Booster currency multiplier while farming is enabled: 1 = normal, 100000 = one hundred thousand times earned currency. This is not a booster count. Zero earned stays zero; result is rounded down and bounded to 100000 currency per calculation.", new AcceptableValueRange<float>(1f, 100000f)));
    }
}
