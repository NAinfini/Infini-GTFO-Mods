using System.Text;
using System;
using System.Collections.Generic;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;

namespace InfiniTweaks;

[HarmonyPatch]
internal static class ResourceHud
{
    private static eResourceContainerSpawnType? _held;
    private static bool _enabled;
    private static readonly Dictionary<IntPtr, SentryGunInstance> Sentries = new();
    private static readonly Dictionary<IntPtr, (PlaceNavMarkerOnGO Marker, bool Visible)> NativeVisibility = new();
    private static bool _restoring;
    private static readonly StringBuilder Text = new(192);
    internal static void Clear() { ResourceHudView.Clear(); RestoreNative(); _held = null; _enabled = false; }
    internal static void ClearWorld() { Clear(); Sentries.Clear(); }
    private static void RestoreNative()
    {
        _restoring = true;
        try
        {
            foreach (var entry in NativeVisibility.Values)
            {
                if (entry.Marker == null) continue;
                entry.Marker.SetExtraInfoVisible(entry.Visible);
                entry.Marker.UpdateExtraInfo();
            }
        }
        finally { NativeVisibility.Clear(); _restoring = false; }
    }

    [HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.OnDestroy)), HarmonyPrefix]
    private static void RemoveMarker(PlaceNavMarkerOnGO __instance)
    { ResourceHudView.Remove(__instance.Pointer); NativeVisibility.Remove(__instance.Pointer); }

    [HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.PlaceMarker)), HarmonyPostfix]
    private static void MarkerPlaced(PlaceNavMarkerOnGO __instance) => Render(__instance);

    [HarmonyPatch(typeof(SentryGunInstance), nameof(SentryGunInstance.OnSpawn)), HarmonyPostfix]
    private static void SentryPlaced(SentryGunInstance __instance)
    {
        if (__instance.Owner != null) Sentries[__instance.Owner.Pointer] = __instance;
    }
    [HarmonyPatch(typeof(SentryGunInstance), nameof(SentryGunInstance.OnDespawn)), HarmonyPrefix]
    private static void SentryRemoved(SentryGunInstance __instance)
    {
        if (__instance.Owner != null && Sentries.TryGetValue(__instance.Owner.Pointer, out var current) && current == __instance)
            Sentries.Remove(__instance.Owner.Pointer);
    }

    private static eResourceContainerSpawnType? HeldPack()
    {
        var local = PlayerManager.GetLocalPlayerAgent();
        return local != null && local.Alive && local.Inventory != null && local.Inventory.WieldedSlot == InventorySlot.ResourcePack
            ? local.Inventory.WieldedItem?.TryCast<ResourcePackFirstPerson>()?.m_packType : null;
    }

    internal static void Tick(PlayerAgent local)
    {
        if (!Settings.ResourceHud.Value)
        {
            if (_enabled || NativeVisibility.Count > 0) Clear();
            return;
        }
        var held = HeldPack();
        if (_held == held && _enabled == Settings.ResourceHud.Value) return;
        _held = held; _enabled = Settings.ResourceHud.Value;
        // Use the authoritative roster; an unseen marker may never have called UpdateExtraInfo.
        foreach (var player in PlayerManager.PlayerAgentsInLevel)
        {
            if (player == null || player.IsLocallyOwned || player.NavMarker == null) continue;
            NativeVisibility.TryAdd(player.NavMarker.Pointer, (player.NavMarker, player.NavMarker.m_extraInfoVisible));
            player.NavMarker.UpdateExtraInfo();
        }
        Plugin.PluginLog.LogInfo($"Resource HUD: held={held?.ToString() ?? "none"}, enabled={_enabled}.");
    }

    [HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.SetExtraInfoVisible)), HarmonyPrefix]
    private static void ShowContext(PlaceNavMarkerOnGO __instance, ref bool __0)
    {
        if (!_restoring && Settings.ResourceHud.Value && __instance.Player != null && !__instance.Player.IsLocallyOwned)
        {
            NativeVisibility[__instance.Pointer] = (__instance, __0);
            __0 = HeldPack().HasValue;
        }
    }

    [HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.UpdateExtraInfo)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Render(PlaceNavMarkerOnGO __instance)
    {
        using var _ = Telemetry.Measure("ResourceHudRender");
        var player = __instance.Player;
        if (_restoring || !Settings.ResourceHud.Value || player == null || player.IsLocallyOwned) return;
        NativeVisibility.TryAdd(__instance.Pointer, (__instance, __instance.m_extraInfoVisible));
        var held = HeldPack();
        var text = Text;
        text.Clear();
        void Line(string name, float amount, bool infection = false, bool infinite = false)
        {
            string color = MarkerRules.ResourceColor(amount, infection, infinite);
            text.Append($"<font-weight=400><size={Settings.HudEmphasisScale.Value * 100:0}%><color=#{color}>");
            if (Settings.HudBold.Value) text.Append("<b>");
            text.Append(name).Append(' ').Append(infinite ? "INF" : float.IsFinite(amount) ? $"{amount * 100:0}%" : "—");
            if (Settings.HudBold.Value) text.Append("</b>");
            text.Append("</color></size></font-weight>\n");
        }
        var backpack = __instance.m_playerBackpack;
        void AmmoLine(InventorySlot slot, float amount)
        {
            if (backpack == null || !backpack.TryGetBackpackItem(slot, out var item) || item?.Instance == null) return;
            var equipment = item.Instance.TryCast<ItemEquippable>();
            if (equipment == null) return;
            var data = equipment.ItemDataBlock;
            bool infinite = data != null && (data.GUIShowAmmoInfinite || !data.GUIShowAmmoTotalRel);
            Line(equipment.ArchetypeName, amount, infinite: infinite);
        }
        if (held == eResourceContainerSpawnType.Health && player.Damage != null)
            Line("HP", player.Damage.GetHealthRel());
        if (held == eResourceContainerSpawnType.Disinfection && player.Damage != null)
            Line("Infection", player.Damage.Infection, true);
        if (backpack != null && held == eResourceContainerSpawnType.AmmoWeapon)
        {
            AmmoLine(InventorySlot.GearStandard, backpack.AmmoStorage.StandardAmmo.RelInPack);
            AmmoLine(InventorySlot.GearSpecial, backpack.AmmoStorage.SpecialAmmo.RelInPack);
        }
        if (backpack != null && held == eResourceContainerSpawnType.AmmoTool)
        {
            float amount = backpack.AmmoStorage.ClassAmmo.RelInPack;
            if (Sentries.TryGetValue(player.Pointer, out var sentry) && sentry != null && sentry.AmmoMaxCap > 0)
                amount = sentry.Ammo / sentry.AmmoMaxCap;
            AmmoLine(InventorySlot.GearClass, amount);
        }
        __instance.m_extraInfo = "";
        // Preserve native resource refresh scheduling while our separate view owns the text.
        __instance.m_extraInfoVisible = held.HasValue;
        // Resource rows own separate native clones, not the player's name mesh.
        __instance.UpdateName(__instance.m_nameToShow, "");
        ResourceHudView.Show(__instance, text.ToString().TrimEnd());
        // Native resource-change events own scheduling; rendering must not dirty itself.
    }

    internal static void UpdateOpacity(PlayerAgent local)
    {
        if (!Settings.ResourceHud.Value || local.FPSCamera == null) return;
        bool gameplay = FocusStateManager.CurrentState == eFocusState.FPS;
        bool aiming = gameplay && InputMapper.GetButton.Invoke(InputAction.Aim, eFocusState.FPS);
        var forward = ((CameraController)local.FPSCamera).m_camera.transform.forward;
        foreach (var player in PlayerManager.PlayerAgentsInLevel)
        {
            if (player == null || player.IsLocallyOwned || player.NavMarker?.m_marker == null) continue;
            // Never fade rescue indicators or menus along with informational HUD text.
            float alpha = !gameplay || !player.Alive || !local.Alive ? 1f : MarkerRules.HudOpacity(
                Vector3.Angle(forward, player.EyePosition - local.EyePosition), aiming,
                Settings.HudAimOpacity.Value, Settings.HudDynamicOpacity.Value);
            NativeVisibility.TryAdd(player.NavMarker.Pointer, (player.NavMarker, player.NavMarker.m_extraInfoVisible));
            ResourceHudView.Alpha(player.NavMarker, alpha, gameplay && player.Alive && local.Alive);
        }
    }
}
