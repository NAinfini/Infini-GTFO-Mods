using System.Collections.Generic;
using System.Text;
using Gear;
using HarmonyLib;
using Player;

namespace InfiniTweaks;

[HarmonyPatch]
internal static class ResourceHud
{
    private static readonly Dictionary<int, PlaceNavMarkerOnGO> Displays = new();
    private static eResourceContainerSpawnType? _held;
    private static bool _enabled;
    internal static void Clear() { Displays.Clear(); _held = null; }

    internal static void Tick(PlayerAgent local)
    {
        var pack = local.Alive && local.Inventory.WieldedSlot == InventorySlot.ResourcePack ? local.Inventory.WieldedItem?.TryCast<ResourcePackFirstPerson>() : null;
        eResourceContainerSpawnType? held = pack?.m_packType;
        if (_held == held && _enabled == Settings.ResourceHud.Value) return;
        _held = held; _enabled = Settings.ResourceHud.Value;
        foreach (var display in Displays.Values)
            if (display != null) { display.SetExtraInfoVisible(_enabled); display.UpdateExtraInfo(); display.m_isInfoDirty = true; }
    }

    [HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.SetExtraInfoVisible)), HarmonyPrefix]
    private static void ShowCompactInfo(PlaceNavMarkerOnGO __instance, ref bool __0)
    {
        if (Settings.ResourceHud.Value && __instance.Player != null && !__instance.Player.IsLocallyOwned) __0 = true;
    }

    [HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.UpdateExtraInfo)), HarmonyPostfix]
    private static void Render(PlaceNavMarkerOnGO __instance)
    {
        var player = __instance.Player;
        if (player == null || player.IsLocallyOwned) return;
        Displays.TryAdd(__instance.GetInstanceID(), __instance);
        if (!Settings.ResourceHud.Value) return;
        var backpack = __instance.m_playerBackpack;
        if (backpack == null || player.Damage == null) return;
        var text = new StringBuilder(320);
        void Line(string name, float amount, eResourceContainerSpawnType type, bool infection = false)
        {
            bool highlight = _held == type;
            float size = highlight ? Settings.HudEmphasisScale.Value : Settings.HudScale.Value;
            bool low = infection ? amount > 0.2f : amount < 0.3f;
            string color = low ? "FFC276" : "CCCCCC";
            text.Append($"<font-weight=400><size={size * 100:0}%><color=#{color}>");
            if (highlight && Settings.HudBold.Value) text.Append("<b>");
            text.Append($"{name} {amount * 100:0}%");
            if (highlight && Settings.HudBold.Value) text.Append("</b>");
            text.Append("</color></size></font-weight>\n");
        }
        Line("HP", player.Damage.GetHealthRel(), eResourceContainerSpawnType.Health);
        if (player.Damage.Infection > 0 || _held == eResourceContainerSpawnType.Disinfection)
            Line("Infection", player.Damage.Infection, eResourceContainerSpawnType.Disinfection, true);
        Line("Main", backpack.AmmoStorage.StandardAmmo.RelInPack, eResourceContainerSpawnType.AmmoWeapon);
        Line("Special", backpack.AmmoStorage.SpecialAmmo.RelInPack, eResourceContainerSpawnType.AmmoWeapon);
        Line("Tool", backpack.AmmoStorage.ClassAmmo.RelInPack, eResourceContainerSpawnType.AmmoTool);
        if (backpack.TryGetBackpackItem(InventorySlot.ResourcePack, out var item))
            text.Append($"<font-weight=400><size={Settings.HudScale.Value * 100:0}%>{item.Name}: {backpack.AmmoStorage.ResourcePackAmmo.AmmoInPack / 20:0.#} uses</size></font-weight>");
        __instance.m_extraInfo = text.ToString();
        __instance.m_extraInfoVisible = true;
        __instance.m_isInfoDirty = true;
    }
    [HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.OnDestroy)), HarmonyPrefix]
    private static void Remove(PlaceNavMarkerOnGO __instance) => Displays.Remove(__instance.GetInstanceID());
}
