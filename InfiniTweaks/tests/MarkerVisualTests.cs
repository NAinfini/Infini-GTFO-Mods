using InfiniTweaks;

// These native enum names compile against real game assemblies in the Release
// build. Values below also match the installed NavMarkerOption flags.
[Flags]
public enum NavMarkerOption : ulong
{
    Empty = 0, Distance = 8, Title = 16, Ammo = 0x400, Health = 0x800,
    Loot = 0x1000, Terminal = 0x2000, HSU = 0x8000, Door = 0x10000,
    ResourceBox = 0x100000, Generator = 0x200000, Disinfection = 0x400000,
    CarryItem = 0x800000, Consumables = 0x8000000, ObjectivePickup = 0x10000000,
    ToolRefill = 0x20000000, BulkheadDC = 0x4000000000
}
public enum eNavMarkerStyle
{
    PlayerPingHealth, PlayerPingAmmo, PlayerPingToolRefill, PlayerPingDisinfection,
    PlayerPingConsumable, PlayerPingCarryItem, PlayerPingPickupObjectiveItem,
    PlayerPingTerminal, PlayerPingGenerator, PlayerPingHSU, PlayerPingBulkheadDC,
    PlayerPingDoor, PlayerPingResourceBox, PlayerPingLoot
}
internal static class MarkerVisualTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(MarkerRules.ResourceColor(0) == "FF3030" && MarkerRules.ResourceColor(0.5f) == "FFFF00" && MarkerRules.ResourceColor(1) == "30FF30", "Supply gradient is red at empty, yellow at half and green at full.");
        check(MarkerRules.ResourceColor(0.375f) != MarkerRules.ResourceColor(0) && MarkerRules.ResourceColor(0.375f) != MarkerRules.ResourceColor(0.5f), "Supply colors interpolate, not switch between three fixed buckets.");
        check(MarkerRules.ResourceColor(0, true) == MarkerRules.ResourceColor(1) && MarkerRules.ResourceColor(1, true) == MarkerRules.ResourceColor(0), "Infection reverses the supply gradient.");
        check(MarkerRules.ResourceColor(float.NaN) == "C8D2DE" && MarkerRules.ResourceColor(0, infinite: true) == MarkerRules.ResourceColor(1), "Unavailable values stay neutral; infinite tools are not presented as depleted.");
        check(MarkerRules.Details(3.99f, 180), "ResourceHelper shows nearby names without facing the item.");
        check(!MarkerRules.Details(4, 180), "At four metres, title display uses the focus cone.");
        check(MarkerRules.Details(30, 10.9f) && !MarkerRules.Details(30, 11), "The 30 m focus cone is strictly 11 degrees.");
        check(MarkerRules.Details(60, 1.9f) && !MarkerRules.Details(60, 2), "The 60 m focus cone matches ResourceHelper without an extra hysteresis path.");
        check(MarkerRules.MarkerOpacity(0, 1) == 0 && Math.Abs(MarkerRules.MarkerOpacity(.1f, 1)-1)<.00001f, "Newly visible markers fade in over 0.1 seconds.");
        check(Math.Abs(MarkerRules.MarkerOpacity(.05f, .5f) - (1-MathF.Cos(MathF.PI/4))*.5f)<.00001f, "Fade-in and 50% ADS dimming compose without hiding the marker.");
        check(MarkerRules.ResourceColor(.2f) == "FF3030" && MarkerRules.ResourceColor(.99f) == "30FF30", "20% HP is bright red and 99% reserves are bright green.");
        check(!MarkerRules.Depleted(true, 7) && MarkerRules.Count(7) == " ×7", "A discovered consumable stack retains its real remaining count.");
        check(MarkerRules.Depleted(true, 0) && MarkerRules.Depleted(true, -1), "Empty consumables and packs cannot retain a marker.");
        check(MarkerRules.Depleted(true, float.NaN), "Invalid counted-item amounts cannot leave an apparently usable marker.");
        check(!MarkerRules.Depleted(false, 0), "Devices, carry objectives and infinite-use items are not mistaken for empty packs.");
        check(MarkerRules.Defaults(MarkerCategory.Consumable).Distance > 0, "Consumable category is enabled by a positive default range.");
        check(MarkerRules.CarryLandmark("CEL_123") == MarkerCategory.Generator && MarkerRules.CarryLandmark("CELL_42") == MarkerCategory.Generator, "Cells use the power icon.");
        check(MarkerRules.CarryLandmark("CRYO_456") == MarkerCategory.HSU && MarkerRules.CarryLandmark("CARGO_789") == MarkerCategory.CarryItem, "Cryos and cargos have distinct objective icons.");
        check(MarkerRules.CarryLandmark("KEY_1") == null && MarkerRules.CarryLandmark("TERMINAL_CARGO") == null, "Only exact carry terminal types gain landmark presentation.");
        check(MarkerRules.CountConsumable(false, true, 5) && !MarkerRules.CountConsumable(false, true, 1) && !MarkerRules.CountConsumable(false, false, 5) && !MarkerRules.CountConsumable(true, true, 5), "Only finite multi-use consumables with native count UI use ammo as a quantity.");
        check(MarkerIconCatalog.Select(MarkerCategory.Consumable, 117, "") == "fog" && MarkerIconCatalog.Select(MarkerCategory.Consumable, 114, "") == "glow" && MarkerIconCatalog.Select(MarkerCategory.Consumable, 99999, "") == "unknown-item", "Fog repeller and glowstick icons are distinct; unrecognized mod items use the new unknown-item icon.");
        check(MarkerIconCatalog.Select(MarkerCategory.Consumable, 174, "") == "glow-yellow", "Yellow glow sticks do not inherit the green artwork.");
        check(MarkerIconCatalog.Select(MarkerCategory.Terminal, 0, "TERMINAL_CARGO") == "terminal" && MarkerIconCatalog.Select(MarkerCategory.CarryItem, 99999, "CELL_42") == "cell", "Terminal and carry item routes choose their own artwork.");
        check(MarkerIconCatalog.Select(MarkerCategory.Objective, 146, "KEY_42") == "bulkhead-key" && MarkerIconCatalog.Select(MarkerCategory.Objective, 27, "KEY_42") == "keycard-red", "Bulkhead pass art does not replace ordinary colored keys.");
        foreach (var pair in new (uint Id, string Name)[] { (131, "cell"), (133, "turbine"), (138, "cargo"), (148, "cryo"), (115, "foam"), (116, "lock"), (30, "flashlight"), (139, "mine"), (144, "foam-mine"), (140, "syringe-health"), (142, "syringe-speed") })
            check(MarkerIconCatalog.Select(MarkerCategory.CarryItem, pair.Id, "") == pair.Name, $"Item {pair.Id} selects {pair.Name}.");
        foreach (var pair in new (MarkerCategory Category, string Name)[] { (MarkerCategory.Health, "health"), (MarkerCategory.Ammo, "ammo"), (MarkerCategory.Tool, "tool"), (MarkerCategory.Disinfection, "disinfection") })
            check(MarkerIconCatalog.Select(pair.Category, 99999, "") == pair.Name, "Resource artwork follows actual resource type even for custom IDs.");
        check(MarkerRules.Defaults(MarkerCategory.Health) == (40f, "#F25B5B") && MarkerRules.Defaults(MarkerCategory.Ammo) == (40f, "#79E68C") && MarkerRules.Defaults(MarkerCategory.Tool) == (40f, "#63B8FF") && MarkerRules.Defaults(MarkerCategory.Consumable).Distance == 10, "Supply colors are red/green/blue; supply and consumable ranges match the checked reference.");
        var native = new NavMarker(); var teammate = new NavMarker();
        check(!MarkerRules.InRange(10000, 20) && !MarkerRules.InRange(401, 20) && MarkerRules.InRange(400, 20), "ResourceHelper distance boundaries also apply inside the same zone.");
        check(!MarkerRules.InRange(1, 0) && !MarkerRules.InRange(10000, 45) && !MarkerRules.InRange(float.NaN, 40), "Zone visibility cannot override a disabled category, objective range or invalid position.");
        NavMarker? owned = null;
        var replaced = new NavMarker(); var replacement = new NavMarker();
        MarkerOwnership.Rebind(ref owned, replaced, true);
        MarkerOwnership.Rebind(ref owned, replacement, true);
        check(replaced.IsVisible && !replacement.IsVisible && owned == replacement, "Rebinding restores the old native marker and suppresses the replacement.");
        replacement.SetVisible(false);
        MarkerOwnership.Rebind(ref owned, replacement, false);
        check(!replacement.IsVisible, "Disabling respects the latest game-requested visibility.");
        MarkerOwnership.Rebind(ref owned, replaced, true);
        MarkerOwnership.Rebind(ref owned, null, false);
        check(replaced.IsVisible && owned == null, "Losing a placer releases the previously owned native marker.");
        MarkerOwnership.Suppress(native);
        check(!native.IsVisible && teammate.IsVisible, "Replacement suppresses only the claimed objective icon/distance.");
        native.SetVisible(true);
        check(!native.IsVisible, "Native visibility updates cannot reintroduce a duplicate.");
        MarkerOwnership.Release(native);
        check(native.IsVisible, "Hiding/disabling replacement restores the native request.");
        MarkerOwnership.Suppress(native); native.SetVisible(false); MarkerOwnership.Release(native);
        check(!native.IsVisible, "A picked-up/hidden native marker must not be resurrected on release.");
        MarkerOwnership.Suppress(native); MarkerOwnership.Suppress(native); MarkerOwnership.Release(native);
        check(!native.IsVisible, "Repeated claims preserve the native hidden state.");
        check(MarkerRules.Discoverable(9, true, true, true), "An interactive device's own cabinet must not occlude its discovery anchor.");
        check(!MarkerRules.Discoverable(9, true, true, false), "An unrelated wall must still block passive discovery.");
        check(!MarkerRules.Discoverable(17, true, false, false) && !MarkerRules.Discoverable(1, false, false, false), "Remote teammate discovery respects range and dimension.");
        check(!MarkerRules.Discoverable(float.NaN, true, false, false), "Invalid geometry cannot reveal a device.");
        var marker = new NavMarker();
        MarkerVisuals.Apply(marker);
        var options = NavMarkerOption.CarryItem | NavMarkerOption.Title | NavMarkerOption.Distance;
        check(marker.Visible == options && marker.Focus == options, "Custom art keeps title and distance in both visible states.");
        check(marker.Edge == NavMarkerOption.Empty && marker.Inactive == NavMarkerOption.Empty, "Custom art does not introduce offscreen or inactive icons.");
        check(marker.Style == eNavMarkerStyle.PlayerPingCarryItem, "All owned markers select the custom-art slot.");
        var view = new MarkerPresentation(marker);
        var color = new UnityEngine.Color(0, 1, 0, 1);
        check(view.Title("MEDIPACK ×3") && !view.Title("MEDIPACK ×3"), "Unchanged resource titles do not dirty native text.");
        view.Apply(true, true, true, color, 1, 0.8f);
        marker.Project(true);
        view.Apply(true, true, true, color, 1, 0.8f);
        check(marker.Title == "MEDIPACK ×3" && marker.Alpha == .8f, "Initial projection receives the desired opacity after native components activate.");
        int before = marker.SetterCalls;
        for (int i = 0; i < 7200; i++) view.Apply(true, true, true, color, 1, 0.8f);
        check(marker.SetterCalls == before + 7200, "Only opacity refreshes on stable visible ticks; styles, colors, scale and titles stay cached.");
        marker.SetAlpha(0);
        view.Apply(false, true, true, color, 1, .8f);
        check(!marker.IsVisible && marker.Alpha == 0, "A faded marker can be hidden without touching inactive component alpha.");
        before = marker.SetterCalls;
        view.Apply(false, true, true, color, 1, .8f);
        check(marker.SetterCalls == before, "Hidden markers avoid redundant setters.");
        view.Apply(true, true, true, color, 1, .8f);
        check(marker.IsVisible && marker.Alpha == .8f, "Re-show activates before SetAlpha, which skips inactive components in GTFO.");
        marker.Project(false);
        marker.Alpha = 0;
        view.Apply(true, true, true, color, 1, .8f);
        check(marker.Alpha == 0, "Offscreen components ignore alpha, matching native behavior.");
        marker.Project(true);
        view.Apply(true, true, true, color, 1, .8f);
        check(marker.Alpha == .8f, "Returning onscreen restores opacity even when desired values never changed.");
        before = marker.VisualChanges;
        view.Apply(true, false, true, color, 1, .8f);
        check(marker.Title == "" && marker.VisualChanges == before && marker.ComponentsActive, "Looking away clears text without resetting the native visual state or hiding the icon.");
        view.Title("MEDIPACK ×2");
        check(marker.Title == "", "Quantity refresh stays folded until focused.");
        view.Apply(true, true, true, color, 1, .8f);
        check(marker.Title == "MEDIPACK ×2" && marker.VisualChanges == before, "Focus restores current quantity without rebuilding native state.");
        marker.SetVisible(false);
        view.Apply(true, true, true, color, 1, .8f);
        check(marker.IsVisible && marker.Alpha == .8f, "Actual native visibility overrides stale desired-state assumptions.");
        view.Apply(true, true, true, color, 1.2f, .8f);
        marker.Project(true);
        view.Apply(true, true, true, color, 1.2f, .8f);
        check(marker.Style == eNavMarkerStyle.PlayerPingCarryItem && marker.IconScale == 1.2f && marker.Title == "MEDIPACK ×2" && marker.Alpha == .8f, "Changing scale preserves custom-art style, content and projection opacity.");
        Console.WriteLine("Marker regression covers native inactive-component alpha, hide/show, offscreen return and focus transitions; not an in-game rendering test.");
    }
}
