using System.Collections.Generic;
using System;
using System.Runtime.InteropServices;
using AIGraph;
using Gear;
using GTFO.API;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace InfiniTweaks;

[HarmonyPatch]
internal static partial class ItemMarkers
{
    private sealed class Pin
    {
        internal readonly Component Target;
        internal readonly ItemInLevel? Item;
        internal readonly LG_GenericTerminalItem? Terminal;
        internal readonly Device? Device;
        internal readonly MarkerCategory Category;
        internal readonly LG_PickupItem_Sync? PickupSync;
        internal readonly CarryItemPickup_Core? Carry;
        internal float ForcedUntil;
        internal bool Explicit => Time.unscaledTime < ForcedUntil;
        internal PlayerAgent? Carrier;
        internal NavMarker? Marker, NativeMarker;
        internal MarkerCategory? CarryLandmark => Carry != null
            ? MarkerRules.CarryLandmark(Terminal?.TerminalItemKey ?? Item!.PublicName) : null;
        internal MarkerPresentation? Presentation;
        internal MarkerIcon? ItemIcon;
        internal bool Visible;
        internal int? TrackingDimension;
        internal float ShownAt = -1;
        internal bool NotPlaced, MissingNode;
        internal Pin(Component target, ItemInLevel? item, LG_GenericTerminalItem? terminal, Device? device = null)
        {
            Id = item != null ? item.GetInstanceID() : terminal!.GetInstanceID();
            Target = device?.Anchor ?? target; Item = item; Terminal = terminal; Device = device;
            Category = device?.Category ?? Classify(item, terminal);
            PickupSync = item?.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
            Carry = item?.TryCast<CarryItemPickup_Core>();
        }
        internal readonly int Id;
        internal AIG_CourseNode? Node => Carrier != null ? Carrier.CourseNode : Item != null ? Item.CourseNode : Terminal?.SpawnNode;
        internal Component TrackingTarget => Carrier != null ? Carrier : Target;
    }
    private sealed record Device(Component Owner, Component Anchor, MarkerCategory Category, Func<bool> Available, Func<string>? Detail = null);
    private static readonly Dictionary<int, Device> Devices = new();
    private static readonly Dictionary<int, LG_GenericTerminalItem> DiscoveryOwners = new();
    private static Il2CppReferenceArray<Collider> DiscoveryHits = new(64);
    private static readonly HashSet<int> DiscoveryVisited = new();
    private static long _spatialQueries, _spatialHits, _bufferGrowths;
    private static readonly Dictionary<int, Pin> Pins = new();
    private static readonly Dictionary<uint, LG_GenericTerminalItem> Terminals = new();
    private static readonly Dictionary<int, uint> TerminalIds = new();
    private static readonly HashSet<int> Dismissed = new();
    private static readonly List<int> Dead = new();
    private sealed record SavedPin(uint Id, bool Terminal, bool Explicit, bool Hidden = false);
    private static readonly Dictionary<eBufferType, List<SavedPin>> Checkpoints = new();
    private static readonly Dictionary<IntPtr, (NavMarker Marker, int PinId)> TransientPings = new();
    private sealed class PendingPing
    {
        internal readonly NavMarker Marker;
        internal readonly Vector3 Position;
        internal float NextAt;
        internal int Remaining = 7;
        internal PendingPing(NavMarker marker, Vector3 position) { Marker = marker; Position = position; }
    }
    private static readonly Dictionary<IntPtr, PendingPing> PendingPings = new();
    private static readonly List<IntPtr> CompletedPings = new();
    private static float _nextCheck, _nextDiscovery;
    private static float _aimOpacity = 1;
    private static long _uiChanges, _labelChecks, _discoveryChecks, _linecasts;
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
                if (item != null && item.SyncID == ping.SyncId) { RememberPickup(item, true); return; }
        });
    }

    internal static void RebuildWorld()
    {
        foreach (var terminal in UnityEngine.Object.FindObjectsOfType<LG_GenericTerminalItem>()) RegisterTerminal(terminal);
        foreach (var device in UnityEngine.Object.FindObjectsOfType<LG_ComputerTerminal>()) RegisterComputer(device);
        foreach (var reactor in UnityEngine.Object.FindObjectsOfType<LG_WardenObjective_Reactor>()) RegisterReactor(reactor);
        foreach (var value in Resources.FindObjectsOfTypeAll(Il2CppType.Of<CarryItemPickup_Core>()))
        {
            var carry = value.TryCast<CarryItemPickup_Core>();
            if (carry != null && carry.PickupItemStatus == ePickupItemStatus.PickedUp && carry.PickedUpByPlayer != null) CarryChanged(carry);
        }
        foreach (var device in UnityEngine.Object.FindObjectsOfType<LG_PowerGenerator_Core>())
            RegisterGenerator(device);
        foreach (var device in UnityEngine.Object.FindObjectsOfType<LG_DisinfectionStation>())
            RegisterDisinfection(device);
        foreach (var device in UnityEngine.Object.FindObjectsOfType<LG_HSUActivator_Core>())
            RegisterActivator(device);
        foreach (var device in UnityEngine.Object.FindObjectsOfType<LG_HSU>())
            RegisterHsu(device);
        foreach (var device in UnityEngine.Object.FindObjectsOfType<LG_BulkheadDoorController_Core>())
            RegisterController(device);
        // Refresh any discoveries made during level construction after device metadata is available.
        var remembered = new List<Pin>(Pins.Values);
        foreach (var pin in remembered)
            if (pin.Item == null && pin.Terminal != null)
            { Forget(pin.Id); RememberTerminalObject(pin.Terminal, pin.Explicit); }
        Plugin.PluginLog.LogInfo($"Marker registry: {ResourceHandling.Items.Count} pickups, {Terminals.Count} terminal objects.");
    }

    [HarmonyPatch(typeof(LG_PowerGenerator_Core), nameof(LG_PowerGenerator_Core.Setup)), HarmonyPostfix]
    private static void RegisterGenerator(LG_PowerGenerator_Core __instance) =>
        DeviceFor(__instance.m_terminalItem?.TryCast<LG_GenericTerminalItem>(), __instance, MarkerCategory.Generator,
            () => __instance != null && __instance.m_powerCellInteraction?.TryCast<LG_GenericCarryItemInteractionTarget>()?.isActiveAndEnabled == true && __instance.m_graphics?.m_gfxSlot?.activeSelf == true,
            anchor: __instance.m_powerCellInteraction?.TryCast<LG_GenericCarryItemInteractionTarget>());
    [HarmonyPatch(typeof(LG_DisinfectionStation), nameof(LG_DisinfectionStation.Setup)), HarmonyPostfix]
    private static void RegisterDisinfection(LG_DisinfectionStation __instance) =>
        DeviceFor(__instance.m_terminalItem?.TryCast<LG_GenericTerminalItem>(), __instance, MarkerCategory.DisinfectionStation,
            () => __instance != null && __instance.m_interact?.IsActive == true, anchor: __instance.m_interact);
    [HarmonyPatch(typeof(LG_HSU), nameof(LG_HSU.Setup)), HarmonyPostfix]
    private static void RegisterHsu(LG_HSU __instance) =>
        DeviceFor(__instance.m_terminalItem?.TryCast<LG_GenericTerminalItem>(), __instance, MarkerCategory.HSU,
            () => __instance != null && __instance.m_pickupSampleInteraction?.IsActive == true, anchor: __instance.m_pickupSampleInteraction);
    [HarmonyPatch(typeof(LG_HSUActivator_Core), nameof(LG_HSUActivator_Core.SetupAsWardenObjective)), HarmonyPostfix]
    private static void RegisterActivator(LG_HSUActivator_Core __instance) =>
        DeviceFor(__instance.m_terminalItem?.TryCast<LG_GenericTerminalItem>(), __instance, MarkerCategory.HSUActivator,
            () => __instance != null && __instance.m_insertHSUInteraction?.TryCast<LG_GenericCarryItemInteractionTarget>()?.isActiveAndEnabled == true,
            anchor: __instance.m_insertHSUInteraction?.TryCast<LG_GenericCarryItemInteractionTarget>());
    [HarmonyPatch(typeof(LG_HSUActivator_Core), nameof(LG_HSUActivator_Core.SetupFromCustomGeomorph)), HarmonyPostfix]
    private static void RegisterCustomActivator(LG_HSUActivator_Core __instance) => RegisterActivator(__instance);
    [HarmonyPatch(typeof(LG_BulkheadDoorController_Core), nameof(LG_BulkheadDoorController_Core.Setup)), HarmonyPostfix]
    private static void RegisterController(LG_BulkheadDoorController_Core __instance) =>
        DeviceFor(__instance.m_terminalItem?.TryCast<LG_GenericTerminalItem>(), __instance, MarkerCategory.BulkheadController,
            () => __instance != null && __instance.m_stateReplicator.State.status != eBulkheadDCStatus.InactiveNoMoreInteraction);
    [HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.OnProximityEnter)), HarmonyPostfix]
    private static void DiscoveredTerminal(LG_ComputerTerminal __instance) => RememberComputer(__instance, false);

    private static void RememberComputer(LG_ComputerTerminal computer, bool interacted)
    {
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
        RegisterComputer(computer);
        var terminal = computer.m_terminalItem?.TryCast<LG_GenericTerminalItem>();
        if (terminal != null) RememberTerminalObject(terminal, interacted);
    }
    [HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.OnInteract)), HarmonyPostfix]
    private static void UsedTerminal(LG_ComputerTerminal __instance, bool __result)
    {
        if (__result) RememberComputer(__instance, true);
    }
    [HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.SyncChangeState)), HarmonyPostfix]
    private static void TeammateUsedTerminal(LG_ComputerTerminal __instance, PlayerAgent __1)
    {
        // This is the native synchronized terminal interaction, not a custom
        // message requiring the teammate to install our plugin.
        if (__1 != null) RememberComputer(__instance, true);
    }

    [HarmonyPatch(typeof(SyncedNavMarkerWrapper), nameof(SyncedNavMarkerWrapper.OnStateChange)), HarmonyPostfix]
    private static void ReceivedPing(SyncedNavMarkerWrapper __instance, pNavMarkerState __1)
    {
        if (__instance.m_marker != null && TransientPings.Remove(__instance.m_marker.Pointer, out var old)) MarkerOwnership.Release(old.Marker);
        if (!Settings.Markers.Value || __1.status != eNavMarkerStatus.Visible || __instance.m_marker == null) return;
        PendingPings[__instance.m_marker.Pointer] = new(__instance.m_marker, __1.worldPos);
        Pin? matched = null;
        if (__1.terminalItemId != 0 && Terminals.TryGetValue(__1.terminalItemId, out var terminal) && terminal != null)
        {
            RememberTerminalObject(terminal, true);
            var item = terminal.GetComponentInParent<ItemInLevel>() ?? terminal.GetComponentInParent<LG_PickupItem_Sync>()?.item?.TryCast<ItemInLevel>();
            Pins.TryGetValue(item != null ? item.GetInstanceID() : terminal.GetInstanceID(), out matched);
        }
        if (matched == null)
        {
            // Same exact-impact collider lookup as ItemMarker, not nearest-item guessing.
            foreach (var collider in Physics.OverlapSphere(__1.worldPos, 0.001f, LayerManager.MASK_PING_TARGET))
            {
                matched = DiscoverTarget(collider, true);
                if (matched != null) break;
            }
        }
        if (matched != null) TransientPings[__instance.m_marker.Pointer] = (__instance.m_marker, matched.Id);
    }

    private static void RefreshPendingPings()
    {
        // ResourceHelper revisits a ping neighbourhood seven times at 0.5 s intervals.
        // Only already discovered pickups gain the temporary range; nearby sealed
        // or unseen loot is not turned into a new discovery by this neighbourhood pass.
        foreach (var pair in PendingPings)
        {
            var ping = pair.Value;
            if (Time.unscaledTime < ping.NextAt) continue;
            ping.NextAt = Time.unscaledTime + .5f;
            Pin? nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (var collider in Physics.OverlapSphere(ping.Position, 2f,
                LayerManager.MASK_PLAYER_INTERACT_SPHERE | LayerManager.MASK_PING_TARGET))
            {
                var item = ResolvePickup(collider);
                if (item == null || !Pins.TryGetValue(item.GetInstanceID(), out var pin) || !Available(item)) continue;
                pin.ForcedUntil = Time.unscaledTime + 15f;
                float distance = (collider.ClosestPoint(ping.Position) - ping.Position).sqrMagnitude;
                if (distance < nearestDistance) { nearest = pin; nearestDistance = distance; }
            }
            if (nearest != null && ping.Marker != null && !TransientPings.ContainsKey(ping.Marker.Pointer))
                TransientPings[ping.Marker.Pointer] = (ping.Marker, nearest.Id);
            if (--ping.Remaining == 0) CompletedPings.Add(pair.Key);
        }
        foreach (var key in CompletedPings) PendingPings.Remove(key);
        CompletedPings.Clear();
    }

    // Release before native OnStateChange so a reused ping marker gets a fresh
    // requested visibility state. Native sound, callbacks and lifetime still run.
    [HarmonyPatch(typeof(SyncedNavMarkerWrapper), nameof(SyncedNavMarkerWrapper.OnStateChange)), HarmonyPrefix]
    private static void ReusingPing(SyncedNavMarkerWrapper __instance)
    {
        if (__instance.m_marker != null) PendingPings.Remove(__instance.m_marker.Pointer);
        if (__instance.m_marker != null && TransientPings.Remove(__instance.m_marker.Pointer, out var old)) MarkerOwnership.Release(old.Marker);
    }
    private static void OwnTransientPings(int id, bool own)
    {
        foreach (var entry in TransientPings.Values)
            if (entry.PinId == id && entry.Marker != null)
            { if (own) MarkerOwnership.Suppress(entry.Marker); else MarkerOwnership.Release(entry.Marker); }
    }

    [HarmonyPatch(typeof(SNet_Capture), nameof(SNet_Capture.OnBufferCommand)), HarmonyPrefix]
    private static void StoreMarkers(pBufferCommand command)
    {
        if (command.operation != eBufferOperationType.StoreGameState) return;
        var saved = new List<SavedPin>();
        foreach (var pin in Pins.Values)
            if (pin.Item != null) saved.Add(new(pin.Item.SyncID, false, pin.Explicit));
            else if (pin.Terminal != null) saved.Add(new(pin.Terminal.TerminalItemId, true, pin.Explicit));
        foreach (int id in Dismissed)
            if (ResourceHandling.Items.TryGetValue(id, out var item) && item != null) saved.Add(new(item.SyncID, false, false, true));
            else if (TerminalIds.TryGetValue(id, out uint terminalId)) saved.Add(new(terminalId, true, false, true));
        Checkpoints[command.type] = saved;
    }
    [HarmonyPatch(typeof(SNet_SyncManager), nameof(SNet_SyncManager.OnRecallDone)), HarmonyPostfix]
    private static void RestoreMarkers(eBufferType bufferType)
    {
        ClearPins();
        RebuildWorld();
        if (!Checkpoints.TryGetValue(bufferType, out var saved)) return;
        foreach (var entry in saved)
        {
            if (entry.Terminal)
            {
                if (Terminals.TryGetValue(entry.Id, out var terminal) && terminal != null)
                {
                    if (entry.Hidden) { Forget(terminal.GetInstanceID()); Dismissed.Add(terminal.GetInstanceID()); }
                    else RememberTerminalObject(terminal, entry.Explicit);
                }
            }
            else
                foreach (var item in ResourceHandling.Items.Values)
                    if (item != null && item.SyncID == entry.Id)
                    {
                        if (entry.Hidden) { Forget(item.GetInstanceID()); Dismissed.Add(item.GetInstanceID()); }
                        else RememberPickup(item, entry.Explicit);
                        break;
                    }
        }
        Plugin.PluginLog.LogInfo($"Restored {Pins.Count} discovered markers from {bufferType}; post-checkpoint discoveries discarded.");
    }

    internal static void Clear()
    {
        ClearPins(); Terminals.Clear(); TerminalIds.Clear(); Devices.Clear(); DiscoveryOwners.Clear(); Checkpoints.Clear();
    }
    internal static void ClearPins()
    {
        foreach (var pin in Pins.Values) RemoveVisual(pin);
        foreach (var entry in TransientPings.Values) if (entry.Marker != null) MarkerOwnership.Release(entry.Marker);
        TransientPings.Clear(); PendingPings.Clear(); CompletedPings.Clear();
        Pins.Clear(); Dead.Clear(); Dismissed.Clear();
        _nextCheck = _nextDiscovery = 0; _aimOpacity = 1;
    }
    private static void OwnCarryMarker(Pin pin, bool own)
        => MarkerOwnership.Rebind(ref pin.NativeMarker, pin.Carry?.m_navMarkerPlacer?.m_marker, own);
    private static void RemoveVisual(Pin pin)
    {
        OwnTransientPings(pin.Id, false);
        if (pin.Marker != null)
        {
            pin.Marker.SetVisible(false);
            pin.Marker.SetOptions(NavMarkerOption.Empty);
            pin.Marker.SetAlpha(0);
            if (GuiManager.NavMarkerLayer != null) GuiManager.NavMarkerLayer.RemoveMarker(pin.Marker);
        }
        pin.Marker = null;
        pin.Presentation = null;
        pin.ItemIcon = null;
        pin.Visible = false;
        OwnCarryMarker(pin, false);
    }
    [HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.OnStateChange)), HarmonyPrefix]
    private static void PickupChanged(LG_PickupItem_Sync __instance, pPickupItemState newState)
    {
        if (newState.updateCustomDataOnly || newState.status == ePickupItemStatus.PlacedInLevel) return;
        // A pickup can exchange/deactivate its world Item during OnStateChange.
        // Match the retained sync identity BEFORE that swap, not sync.item afterwards.
        foreach (var pair in Pins)
            if (pair.Value.PickupSync != null && pair.Value.PickupSync.Pointer == __instance.Pointer &&
                !(newState.status == ePickupItemStatus.PickedUp && pair.Value.Category == MarkerCategory.CarryItem)) Dead.Add(pair.Key);
        foreach (int id in Dead) Forget(id);
        Dead.Clear();
    }
    [HarmonyPatch(typeof(ItemInLevel), nameof(ItemInLevel.OnPickedUp)), HarmonyPostfix]
    private static void PickedUp(ItemInLevel __instance, PlayerAgent player)
    {
        if (__instance.TryCast<CarryItemPickup_Core>() == null) Forget(__instance.GetInstanceID());
    }
    [HarmonyPatch(typeof(CarryItemPickup_Core), nameof(CarryItemPickup_Core.OnSyncStateChange)), HarmonyPostfix]
    private static void CarryChanged(CarryItemPickup_Core __instance)
    {
        int id = __instance.GetInstanceID();
        var status = __instance.PickupItemStatus;
        if (__instance.ObjectiveItemSolved || (status != ePickupItemStatus.PickedUp && status != ePickupItemStatus.PlacedInLevel))
        { Forget(id); return; }
        var player = status == ePickupItemStatus.PickedUp ? __instance.PickedUpByPlayer : null;
        if (!Settings.Markers.Value) return;
        if (!Pins.TryGetValue(id, out var pin))
        {
            // Spawning or rebuilding an unseen carry objective is not discovery.
            // A real pickup establishes knowledge, including a late-joined carrier.
            if (player == null) return;
            pin = new Pin(__instance, __instance, __instance.GetComponentInChildren<LG_GenericTerminalItem>(true));
            Add(pin, true);
        }
        RemoveVisual(pin);
        pin.Carrier = player;
        if (player != null) pin.ForcedUntil = Time.unscaledTime + 15f;
        _nextCheck = 0;
    }
    internal static void Forget(int id)
    {
        if (Pins.Remove(id, out var pin)) RemoveVisual(pin);
    }
    private static void Add(Pin pin, bool explicitDiscovery)
    {
        int id = pin.Id;
        if (!Settings.Markers.Value) return;
        if (Pins.TryGetValue(id, out var existing)) { if (explicitDiscovery) existing.ForcedUntil = Time.unscaledTime + 15f; return; }
        if (explicitDiscovery) Dismissed.Remove(id);
        else if (Dismissed.Contains(id)) return;
        if (explicitDiscovery) pin.ForcedUntil = Time.unscaledTime + 15f;
        Pins.Add(id, pin);
        _nextCheck = 0;
    }
    private static bool Available(ItemInLevel item)
    {
        // World graphics can be culled/deactivated while the item remains placed.
        // The pickup state, not renderer activity, owns its marker lifetime.
        if (item.GetSyncComponent()?.GetCurrentState().status != ePickupItemStatus.PlacedInLevel) return false;
        var carry = item.TryCast<CarryItemPickup_Core>();
        if (carry != null && (carry.ObjectiveItemSolved || carry.IsLinkedToMachine || !carry.IsInteractable)) return false;
        if (Depleted(item)) return false;
        var box = item.container?.m_core?.TryCast<LG_WeakResourceContainer>();
        return box == null || ResourceHandling.IsOpen(box);
    }
    private static void RememberPickup(ItemInLevel item, bool explicitDiscovery)
    {
        if (Pins.TryGetValue(item.GetInstanceID(), out var existing)) { if (explicitDiscovery) existing.ForcedUntil = Time.unscaledTime + 15f; return; }
        if (!Available(item)) return;
        Add(new Pin(item, item, item.GetComponentInChildren<LG_GenericTerminalItem>()), explicitDiscovery);
    }
    private static void RememberTerminalObject(LG_GenericTerminalItem terminal, bool explicitDiscovery)
    {
        var item = terminal.GetComponentInParent<ItemInLevel>() ?? terminal.GetComponentInParent<LG_PickupItem_Sync>()?.item?.TryCast<ItemInLevel>();
        if (item != null) { RememberPickup(item, explicitDiscovery); return; }
        Devices.TryGetValue(terminal.GetInstanceID(), out var device);
        // A pickup's terminal component can be reparented into a held model.
        // Never turn an unrecognized component into a second, generic resource pin.
        if (device == null) return;
        // Apply to every discovery route, including QUERY and explicit player pings.
        bool storage = terminal.FloorItemType == eFloorInventoryObjectType.Storage || terminal.GetComponentInParent<LG_WeakResourceContainer>() != null;
        bool door = terminal.FloorItemType == eFloorInventoryObjectType.Passage || terminal.GetComponentInParent<LG_WeakDoor>() != null || terminal.GetComponentInParent<LG_SecurityDoor>() != null;
        if (!MarkerRules.TerminalAllowed(storage, door)) return;
        Add(new Pin(terminal, null, terminal, device), explicitDiscovery);
    }
    private static void RememberTerminal(uint id)
    {
        if (id != 0 && Terminals.TryGetValue(id, out var terminal) && terminal != null)
            RememberTerminalObject(terminal, true);
    }
    [HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.TriggerMarkerPing)), HarmonyPostfix]
    private static void PlayerPing(PlayerAgent __instance, GameObject __1)
    {
        if (!Settings.Markers.Value || !__instance.IsLocallyOwned || __instance.Owner == null || __instance.Owner.IsBot || __1 == null) return;
        var item = DiscoverTarget(__1.transform, true)?.Item;
        if (item == null) return;
        if (SNet.Slots?.PlayerSlots != null)
            foreach (var slot in SNet.Slots.PlayerSlots)
                if (slot?.player is { } player && !player.IsLocal && !player.IsBot)
                    NetworkAPI.InvokeEvent(PingEvent, new ItemPing { SyncId = item.SyncID }, player, SNet_ChannelType.GameOrderCritical);
    }
    [HarmonyPatch(typeof(GuiManager), nameof(GuiManager.AttemptSetTerminalPing)), HarmonyPostfix]
    private static void TerminalPing(bool __0, uint __2) { if (__0) RememberTerminal(__2); }

    [HarmonyPatch(typeof(LG_GenericTerminalItem), nameof(LG_GenericTerminalItem.PlayPing)), HarmonyPostfix]
    private static void TerminalPlayedPing(LG_GenericTerminalItem __instance) => RememberTerminalObject(__instance, true);

    private static ItemInLevel? ResolvePickup(Component target) =>
        target.GetComponentInParent<LG_PickupItem_Sync>()?.item?.TryCast<ItemInLevel>() ?? target.GetComponentInParent<ItemInLevel>();

    private static Pin? DiscoverTarget(Component target, bool explicitDiscovery)
    {
        var item = ResolvePickup(target);
        if (item != null)
        {
            RememberPickup(item, explicitDiscovery);
            return Pins.TryGetValue(item.GetInstanceID(), out var pin) ? pin : null;
        }
        var terminal = ResolveTerminal(target);
        if (terminal == null) return null;
        RememberTerminalObject(terminal, explicitDiscovery);
        return Pins.TryGetValue(terminal.GetInstanceID(), out var devicePin) ? devicePin : null;
    }

    private static bool Depleted(ItemInLevel item)
    {
        var data = item.ItemDataBlock;
        bool counted = item.TryCast<ResourcePackPickup>() != null ||
            (data?.inventorySlot == InventorySlot.Consumable &&
             MarkerRules.CountConsumable(data.GUIShowAmmoInfinite, data.GUIShowAmmoTotalRel, data.ConsumableAmmoMax));
        return MarkerRules.Depleted(counted, counted ? item.GetCustomData().ammo : 0);
    }
    private static void Label(Pin pin)
    {
        if (pin.Presentation == null) return;
        _labelChecks++;
        string label = pin.Item != null ? pin.Item.PublicName : pin.Terminal?.TerminalItemKey ?? "";
        if (pin.CarryLandmark.HasValue && pin.Terminal != null && !string.IsNullOrEmpty(pin.Terminal.TerminalItemKey))
            label = pin.Terminal.TerminalItemKey;
        if (pin.Item?.TryCast<ResourcePackPickup>() != null)
            label += MarkerRules.Count(pin.Item.GetCustomData().ammo / 20f);
        else if (pin.Item?.ItemDataBlock is { } data && data.inventorySlot == InventorySlot.Consumable &&
            MarkerRules.CountConsumable(data.GUIShowAmmoInfinite, data.GUIShowAmmoTotalRel, data.ConsumableAmmoMax))
            label += MarkerRules.Count(pin.Item.GetCustomData().ammo);
        if (pin.Device?.Detail != null) label += " → " + pin.Device.Detail();
        if (pin.Carrier?.Owner != null) label += " · " + MarkerRules.SafeName(pin.Carrier.Owner.NickName);
        if (pin.Presentation.Title(label)) _uiChanges++;
    }
    private static MarkerCategory Classify(ItemInLevel? item, LG_GenericTerminalItem? terminal)
    {
        var pack = item?.TryCast<ResourcePackPickup>();
        if (pack != null) return pack.m_packType switch
        {
            eResourceContainerSpawnType.Health => MarkerCategory.Health,
            eResourceContainerSpawnType.AmmoWeapon => MarkerCategory.Ammo,
            eResourceContainerSpawnType.AmmoTool => MarkerCategory.Tool,
            eResourceContainerSpawnType.Disinfection => MarkerCategory.Disinfection,
            _ => MarkerCategory.Other
        };
        if (item != null)
        {
            if (item.TryCast<CarryItemPickup_Core>() != null) return MarkerCategory.CarryItem;
            if (item.ItemDataBlock?.inventorySlot == InventorySlot.Consumable) return MarkerCategory.Consumable;
            if (item.ItemDataBlock?.inventorySlot == InventorySlot.InLevelCarry) return MarkerCategory.CarryItem;
            return MarkerCategory.Objective;
        }
        return MarkerCategory.Other;
    }
    private static bool Seen(PlayerAgent viewer, Component target, AIG_CourseNode? node, Component? owner = null, Collider? surface = null)
    {
        _discoveryChecks++;
        if (node == null || viewer.CourseNode == null || node.m_dimension.DimensionIndex != viewer.CourseNode.m_dimension.DimensionIndex) return false;
        var point = surface != null ? surface.ClosestPoint(viewer.EyePosition) : target.transform.position;
        var delta = point - viewer.EyePosition;
        if (delta.sqrMagnitude > 16f) return false;
        _linecasts++;
        bool blocked = Physics.Linecast(viewer.EyePosition, point, out var hit, LayerManager.MASK_WORLD);
        bool ownSurface = blocked && hit.collider != null && hit.collider.transform.IsChildOf((owner ?? target).transform);
        var box = target.TryCast<ItemInLevel>()?.container?.m_core?.TryCast<LG_WeakResourceContainer>();
        if (box != null && ResourceHandling.IsOpen(box)) ownSurface |= blocked && hit.collider != null && hit.collider.transform.IsChildOf(box.transform);
        return MarkerRules.Discoverable(delta.sqrMagnitude, true, blocked, ownSurface);
    }
    private static void Discover()
    {
        using var scope = Telemetry.Measure("MarkerDiscovery");
        // ResourceHelper scans a 2 m local sphere and a short 1.5 m teammate capsule.
        // Grow and repeat a saturated non-alloc query: silently truncating misses items.
        foreach (var viewer in PlayerManager.PlayerAgentsInLevel)
        {
            if (viewer == null || !viewer.Alive) continue;
            int count;
            while (true)
            {
                _spatialQueries++;
                var origin = viewer.IsLocallyOwned ? viewer.EyePosition : viewer.transform.position + Vector3.up;
                int mask = LayerManager.MASK_PLAYER_INTERACT_SPHERE | LayerManager.MASK_PING_TARGET;
                count = viewer.IsLocallyOwned
                    ? Physics.OverlapSphereNonAlloc(origin, 2f, DiscoveryHits, mask, QueryTriggerInteraction.Collide)
                    : Physics.OverlapCapsuleNonAlloc(origin, origin + viewer.transform.forward * .5f, 1.5f,
                        DiscoveryHits, mask, QueryTriggerInteraction.Collide);
                if (count < DiscoveryHits.Length) break;
                DiscoveryHits = new(checked(DiscoveryHits.Length * 2)); _bufferGrowths++;
            }
            _spatialHits += count;
            DiscoveryVisited.Clear();
            for (int i = 0; i < count; i++)
            {
                var collider = DiscoveryHits[i]; DiscoveryHits[i] = null!;
                if (collider == null) continue;
                var item = ResolvePickup(collider);
                if (item != null)
                {
                    int id = item.GetInstanceID();
                    if (!Pins.ContainsKey(id) && !Dismissed.Contains(id) && Seen(viewer, item, item.CourseNode, surface: collider)) RememberPickup(item, false);
                    continue;
                }
                var terminal = ResolveTerminal(collider);
                if (terminal != null)
                {
                    int id = terminal.GetInstanceID();
                    if (DiscoveryVisited.Add(id) && !Pins.ContainsKey(id) && !Dismissed.Contains(id) && terminal.FloorItemType != eFloorInventoryObjectType.Storage)
                    {
                        Devices.TryGetValue(id, out var device);
                        if (Seen(viewer, device?.Anchor ?? terminal, terminal.SpawnNode, device?.Owner)) RememberTerminalObject(terminal, false);
                    }
                }
            }
        }
    }
    internal static void Tick(PlayerAgent player)
    {
        if (Settings.Markers.Value && Time.unscaledTime >= _nextDiscovery)
        { _nextDiscovery = Time.unscaledTime + 0.1f; Discover(); }
        if (Settings.Markers.Value) RefreshPendingPings();
        bool aiming = InputMapper.GetButton.Invoke(InputAction.Aim, eFocusState.FPS);
        if (Input.GetKeyDown(Settings.ClearMarkersKey.Value) && FocusStateManager.CurrentState == eFocusState.FPS)
        {
            if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
            {
                foreach (var pair in Pins) { Dismissed.Add(pair.Key); RemoveVisual(pair.Value); }
                Pins.Clear();
            }
            else
            {
                var cam = ((CameraController)player.FPSCamera).m_camera;
                int selected = 0; float score = 0.97f;
                foreach (var pair in Pins)
                {
                    if (!pair.Value.Visible || pair.Value.TrackingTarget == null) continue;
                    float dot = Vector3.Dot(cam.transform.forward, (pair.Value.TrackingTarget.transform.position - cam.transform.position).normalized);
                    if (dot > score) { score = dot; selected = pair.Key; }
                }
                if (selected != 0) { Dismissed.Add(selected); Forget(selected); }
            }
        }
        bool checkDistance = Time.unscaledTime >= _nextCheck;
        bool showAllowed = Settings.Markers.Value && FocusStateManager.CurrentState == eFocusState.FPS;
        _aimOpacity = Mathf.MoveTowards(_aimOpacity, aiming ? .5f : 1f, Time.unscaledDeltaTime * 5f);
        // Projection/opacity runs every frame; world validity and discovery remain bounded.
        if (checkDistance) _nextCheck = Time.unscaledTime + 0.1f;
        using var refresh = Telemetry.Measure("MarkerRefresh");
        foreach (var pair in Pins)
        {
            var pin = pair.Value;
            if (pin.Target == null) { Dead.Add(pair.Key); continue; }
            if (pin.Item != null && Depleted(pin.Item)) { Dead.Add(pair.Key); continue; }
            if (checkDistance)
            {
                if (pin.Carry != null)
                {
                    if (pin.Carry.ObjectiveItemSolved) { Dead.Add(pair.Key); continue; }
                    var carrier = pin.Carry.PickupItemStatus == ePickupItemStatus.PickedUp ? pin.Carry.PickedUpByPlayer : null;
                    if (pin.Carrier != carrier) { RemoveVisual(pin); pin.Carrier = carrier; }
                }
                var node = pin.Node;
                int? dimension = node != null ? (int)node.m_dimension.DimensionIndex : null;
                if (pin.TrackingDimension != dimension)
                { pin.TrackingDimension = dimension; pin.Marker?.UpdateTrackingDimension(); }
                bool placed = (pin.Carrier != null || pin.Item == null || Available(pin.Item)) && (pin.Device == null || pin.Device.Available());
                pin.NotPlaced = !placed; pin.MissingNode = node == null;
                bool sameDimension = node != null && player.CourseNode != null && node.m_dimension.DimensionIndex == player.CourseNode.m_dimension.DimensionIndex;
                float baseRange = Settings.MarkerCategories[pin.Category].Distance.Value;
                float range = MarkerRules.Range(baseRange, pin.ForcedUntil, Time.unscaledTime);
                pin.Visible = placed && sameDimension && MarkerRules.InRange(
                    (pin.TrackingTarget.transform.position - player.EyePosition).sqrMagnitude, range);
                if (pin.Visible) Label(pin);
            }
            bool show = showAllowed && pin.Visible && pin.Carrier?.IsLocallyOwned != true;
            if (!show)
            {
                pin.ShownAt = -1;
                OwnTransientPings(pin.Id, false);
                if (pin.Presentation != null) _uiChanges += pin.Presentation.Apply(false, true, true, default, 1, 0);
                // Keep the default hidden through range/aim transitions; disabling restores it.
                OwnCarryMarker(pin, Settings.Markers.Value && pin.Presentation != null);
                continue;
            }
            if (pin.Marker == null && GuiManager.NavMarkerLayer != null)
            {
                pin.Marker = GuiManager.NavMarkerLayer.PrepareGenericMarker(pin.TrackingTarget.gameObject);
                pin.Presentation = new MarkerPresentation(pin.Marker);
                pin.ItemIcon = MarkerIcon.Create(pin.Marker, pin.Category,
                    pin.Item?.ItemDataBlock?.persistentID ?? 0, pin.Terminal?.TerminalItemKey ?? "");
                pin.Marker.SetPinEnabled(false);
                pin.Marker.UpdateTrackingDimension();
                Label(pin);
            }
            if (pin.Marker == null) continue;
            var direction = pin.TrackingTarget.transform.position - player.EyePosition;
            float distance = direction.magnitude;
            direction.y += distance * .015f;
            bool details = MarkerRules.Details(distance,
                Vector3.Angle(((CameraController)player.FPSCamera).m_camera.transform.forward, direction));
            if (pin.ShownAt < 0) pin.ShownAt = Time.unscaledTime;
            OwnTransientPings(pin.Id, true);
            OwnCarryMarker(pin, true);
            _uiChanges += pin.Presentation!.Apply(true,
                details, true,
                Settings.MarkerCategories[pin.Category].Color,
                Settings.MarkerScale, MarkerRules.MarkerOpacity(Time.unscaledTime - pin.ShownAt, _aimOpacity));
            pin.ItemIcon?.Show();
        }
        foreach (int id in Dead) Forget(id);
        Dead.Clear();
    }
    internal static void LogPerformance()
    {
        int visible = 0, allocated = 0, missingNode = 0, notPlaced = 0;
        foreach (var pin in Pins.Values)
        { if (pin.Visible) visible++; if (pin.Marker != null) allocated++; if (pin.MissingNode) missingNode++; if (pin.NotPlaced) notPlaced++; }
        Telemetry.Write("marker_summary", ("remembered", Pins.Count.ToString()), ("in_range", visible.ToString()),
            ("allocated_markers", allocated.ToString()), ("registered_pickups", ResourceHandling.Items.Count.ToString()),
            ("registered_terminals", Terminals.Count.ToString()), ("dismissed", Dismissed.Count.ToString()),
            ("missing_node", missingNode.ToString()), ("not_placed", notPlaced.ToString()),
            ("ui_change_groups", _uiChanges.ToString()), ("label_checks", _labelChecks.ToString()),
            ("discovery_checks", _discoveryChecks.ToString()), ("linecasts", _linecasts.ToString()));
        Telemetry.Write("marker_spatial", ("queries", _spatialQueries.ToString()), ("collider_hits", _spatialHits.ToString()),
            ("buffer_growths", _bufferGrowths.ToString()), ("buffer_capacity", DiscoveryHits.Length.ToString()));
        _spatialQueries = _spatialHits = _bufferGrowths = 0;
        _uiChanges = _labelChecks = _discoveryChecks = _linecasts = 0;
    }
}
