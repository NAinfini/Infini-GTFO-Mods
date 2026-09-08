using System.Collections.Generic;
using System.Runtime.InteropServices;
using Gear;
using GTFO.API;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace InfiniTweaks;

[HarmonyPatch]
internal static class ItemMarkers
{
    private sealed class Pin
    {
        internal readonly ItemInLevel Item;
        internal NavMarker? Marker;
        internal bool Visible;
        internal Pin(ItemInLevel item) => Item = item;
    }
    private static readonly Dictionary<int, Pin> Pins = new();
    private static readonly List<int> Dead = new();
    private static float _nextCheck;
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ItemPing { public uint SyncId; }
    private const string PingEvent = "InfiniTweaks.Items.Ping.v1";

    internal static void Initialize()
    {
        NetworkAPI.RegisterEvent<ItemPing>(PingEvent, (sender, ping) =>
        {
            if (!Settings.Markers.Value || GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
            bool participant = false;
            foreach (var player in PlayerManager.PlayerAgentsInLevel)
                if (player != null && player.Owner != null && player.Owner.Lookup == sender) { participant = true; break; }
            if (!participant) return;
            foreach (var item in ResourceHandling.Items.Values)
                if (item != null && item.SyncID == ping.SyncId) { RememberPing(item); return; }
        });
    }

    [HarmonyPatch(typeof(SyncedNavMarkerWrapper), nameof(SyncedNavMarkerWrapper.OnStateChange)), HarmonyPostfix]
    private static void ReceivedPing(pNavMarkerState __1)
    {
        if (__1.status == eNavMarkerStatus.Visible) RememberTerminal(__1.terminalItemId);
    }

    internal static void Clear()
    {
        foreach (var pin in Pins.Values) RemoveVisual(pin);
        Pins.Clear(); Dead.Clear();
    }
    private static void RemoveVisual(Pin pin)
    {
        if (pin.Marker != null && GuiManager.NavMarkerLayer != null) GuiManager.NavMarkerLayer.RemoveMarker(pin.Marker);
        pin.Marker = null;
    }
    internal static void Forget(int id)
    {
        if (!Pins.Remove(id, out var pin)) return;
        RemoveVisual(pin);
    }
    internal static void Transfer(int from, ItemInLevel to)
    {
        if (!Pins.ContainsKey(from)) return;
        Forget(from); Remember(to);
    }
    private static void Remember(ItemInLevel item)
    {
        if (!Settings.Markers.Value || Pins.ContainsKey(item.GetInstanceID())) return;
        Pins.Add(item.GetInstanceID(), new Pin(item));
    }
    private static void RememberPing(ItemInLevel item)
    {
        if (item.GetSyncComponent()?.GetCurrentState().status != ePickupItemStatus.PlacedInLevel) return;
        var box = item.container?.m_core?.TryCast<LG_WeakResourceContainer>();
        if (box != null && !box.ISOpen) return;
        Remember(item);
    }
    private static void RememberTerminal(uint terminalItemId)
    {
        if (!Settings.Markers.Value || terminalItemId == 0) return;
        foreach (var item in ResourceHandling.Items.Values)
        {
            if (item == null) continue;
            var terminal = item.GetComponent<LG_GenericTerminalItem>();
            if (terminal != null && terminal.TerminalItemId == terminalItemId) { RememberPing(item); return; }
        }
    }
    [HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.TriggerMarkerPing)), HarmonyPostfix]
    private static void PlayerPing(PlayerAgent __instance, GameObject __1)
    {
        if (!Settings.Markers.Value || !__instance.IsLocallyOwned || __instance.Owner == null || __instance.Owner.IsBot || __1 == null) return;
        // Use the native selected object's ancestry, never adjacent objects or
        // children of a pinged container. Coordinate-only pings remain native.
        var item = __1.GetComponentInParent<ItemInLevel>();
        if (item == null || item.GetSyncComponent()?.GetCurrentState().status != ePickupItemStatus.PlacedInLevel) return;
        RememberPing(item);
        NetworkAPI.InvokeEvent(PingEvent, new ItemPing { SyncId = item.SyncID }, SNet_ChannelType.GameOrderCritical);
    }
    [HarmonyPatch(typeof(GuiManager), nameof(GuiManager.AttemptSetTerminalPing)), HarmonyPostfix]
    private static void TerminalPing(bool __0, uint __2) { if (__0) RememberTerminal(__2); }

    internal static void UpdateLabel(ItemInLevel item)
    {
        if (!Pins.TryGetValue(item.GetInstanceID(), out var pin)) return;
        var pack = item.TryCast<ResourcePackPickup>();
        float amount = item.GetCustomData().ammo;
        if (pack != null && amount <= 0) { pin.Visible = false; return; }
        string label = item.PublicName;
        if (pack != null) label += $" · {amount / 20f:0.#} uses";
        pin.Marker?.SetTitle(label);
        pin.Marker?.UpdateTrackingDimension();
    }
    internal static void Tick(PlayerAgent player)
    {
        bool aiming = InputMapper.GetButton.Invoke(InputAction.Aim, eFocusState.FPS);
        if (Input.GetKeyDown(Settings.ClearMarkersKey.Value) && FocusStateManager.CurrentState == eFocusState.FPS)
        {
            if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) Clear();
            else
            {
                var cam = ((CameraController)player.FPSCamera).m_camera;
                int selected = 0; float score = 0.97f;
                foreach (var pair in Pins)
                {
                    if (!pair.Value.Visible || pair.Value.Item == null) continue;
                    float dot = Vector3.Dot(cam.transform.forward, (pair.Value.Item.transform.position - cam.transform.position).normalized);
                    if (dot > score) { score = dot; selected = pair.Key; }
                }
                if (selected != 0) Forget(selected);
            }
        }
        // Aim/menu changes are immediate. Distance is checked at 10 Hz, and only
        // for explicitly remembered items; no repeated scene searches.
        bool checkDistance = Time.unscaledTime >= _nextCheck;
        if (checkDistance) _nextCheck = Time.unscaledTime + 0.1f;
        foreach (var pair in Pins)
        {
            var pin = pair.Value;
            if (pin.Item == null) { Dead.Add(pair.Key); continue; }
            if (pin.Item.TryCast<ResourcePackPickup>() != null && pin.Item.GetCustomData().ammo <= 0) { Dead.Add(pair.Key); continue; }
            if (pin.Marker == null && Settings.Markers.Value && GuiManager.NavMarkerLayer != null)
            {
                pin.Marker = GuiManager.NavMarkerLayer.PrepareGenericMarker(pin.Item.gameObject);
                pin.Marker.SetStyle(eNavMarkerStyle.LocationBeacon);
                pin.Marker.SetVisualStates(NavMarkerOption.LootTitleDistance, NavMarkerOption.LootTitleDistance, NavMarkerOption.LootTitleDistance, NavMarkerOption.Empty);
                pin.Marker.SetPinEnabled(false);
                UpdateLabel(pin.Item);
            }
            if (pin.Marker == null) continue;
            if (checkDistance)
            {
                var state = pin.Item.GetSyncComponent()?.GetCurrentState();
                bool sameDimension = pin.Item.CourseNode != null && player.CourseNode != null && pin.Item.CourseNode.m_dimension.DimensionIndex == player.CourseNode.m_dimension.DimensionIndex;
                pin.Visible = CasualRules.ShowMarker(state?.status == ePickupItemStatus.PlacedInLevel, sameDimension, false, false, (pin.Item.transform.position - player.EyePosition).sqrMagnitude, Settings.MarkerDistance.Value);
            }
            bool show = Settings.Markers.Value && pin.Visible && FocusStateManager.CurrentState == eFocusState.FPS && !(aiming && Settings.HideMarkersAiming.Value);
            pin.Marker.SetVisible(show);
            pin.Marker.SetAlpha(Settings.MarkerOpacity.Value);
            pin.Marker.SetIconScale(Settings.MarkerScale.Value);
        }
        foreach (int id in Dead) Forget(id);
        Dead.Clear();
    }
}
