using System;
using System.Collections.Generic;
using Gear;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace InfiniTweaks;

[HarmonyPatch]
internal static partial class ResourceHandling
{
    internal static readonly Dictionary<int, ItemInLevel> Items = new();
    private static readonly Dictionary<int, LG_WeakResourceContainer> Containers = new();
    internal static void Clear()
    {
        ClearSlots(); Items.Clear(); Containers.Clear(); ResetSelection();
        PlacementPreview.Clear();
    }
    internal static bool IsOpen(LG_WeakResourceContainer box) =>
        box.ISOpen || box.m_graphics?.TryCast<LG_WeakResourceContainer_Graphics>()?.m_status == eResourceContainerStatus.Open;

    internal static void RebuildWorld()
    {
        ResetSelection();
        foreach (var box in UnityEngine.Object.FindObjectsOfType<LG_WeakResourceContainer>()) RegisterContainer(box);
        ResetSlotOccupants();
        foreach (var sync in UnityEngine.Object.FindObjectsOfType<LG_PickupItem_Sync>()) RegisterSync(sync);
    }

    [HarmonyPatch(typeof(SNet_SyncManager), nameof(SNet_SyncManager.OnRecallDone)), HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void RecallCompleted() => RebuildWorld();

    [HarmonyPatch(typeof(ItemSpawnManager), nameof(ItemSpawnManager.SpawnItem)), HarmonyPostfix]
    private static void SpawnCompleted(Item __result)
    {
        var item = __result?.TryCast<ItemInLevel>();
        var sync = item?.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        if (item == null || sync == null) return;
        RegisterSync(sync);
        // SpawnItem completes after sync construction. The model's authored pickup
        // node is now available even when the factory placement had no node yet.
        if (sync.GetCurrentState().status == ePickupItemStatus.PlacedInLevel && item.CourseNode == null)
        {
            var node = item.GetComponent<iTerminalItem>()?.SpawnNode ??
                item.transform.parent?.parent?.GetComponentInChildren<LG_PickupItem>()?.SpawnNode;
            if (node != null) item.CourseNode = node;
        }
        ItemMarkers.PlacementCompleted(item, sync.GetCurrentState());
    }

    [HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.Setup)), HarmonyPostfix]
    private static void RegisterSync(LG_PickupItem_Sync __instance)
    {
        var item = __instance.item?.TryCast<ItemInLevel>();
        if (item == null) return;
        Register(item);
        var state = __instance.GetCurrentState();
        var slot = TrackSlot(__instance, state, __instance.transform.position);
        if (slot != null && item.ItemDataBlock?.inventorySlot is InventorySlot.ResourcePack or InventorySlot.Consumable)
            item.container = slot.Box.m_storage?.TryCast<LG_ResourceContainer_Storage>();
        if (state.status == ePickupItemStatus.PlacedInLevel && state.placement.node.TryGet(out var node))
            item.CourseNode = node;
        else if (item.CourseNode == null && item.Get_pItemData().originCourseNode.TryGet(out var origin))
            item.CourseNode = origin;
        if (item.CourseNode == null) item.CourseNode = item.GetComponentInChildren<LG_GenericTerminalItem>()?.SpawnNode;
    }

    [HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.SetStateFromFactory)), HarmonyPostfix]
    private static void RegisterFactoryItem(LG_PickupItem_Sync __instance) => RegisterSync(__instance);

    [HarmonyPatch(typeof(LG_ResourceContainer_Storage), nameof(LG_ResourceContainer_Storage.SetSpawnNode)), HarmonyPostfix]
    private static void SetSpawnNode(GameObject obj, AIGraph.AIG_CourseNode spawnNode)
    {
        foreach (var item in obj.GetComponentsInChildren<ItemInLevel>(true))
        { item.CourseNode = spawnNode; Register(item); }
    }
    internal static void ResetSelection()
    {
        _selected = null;
        PlacementPreview.Hide();
    }
    internal static void Tick(PlayerAgent player)
    {
        bool resource = player.Inventory.WieldedSlot == InventorySlot.ResourcePack;
        if (!player.Alive || (!resource && player.Inventory.WieldedSlot != InventorySlot.Consumable) || FocusStateManager.CurrentState != eFocusState.FPS)
        { ResetSelection(); return; }
        if (Settings.Drop.Value && Input.GetKeyDown(Settings.DropKey.Value))
        {
            var camera = ((CameraController)player.FPSCamera).m_camera;
            if (camera != null && Physics.Raycast(camera.transform.position, (camera.transform.forward + Vector3.down * 0.4f).normalized, out var ground, 3f, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore) && ground.normal.y > 0.6f)
                Place(player, ground.point + Vector3.up * 0.04f, Quaternion.Euler(0, camera.transform.eulerAngles.y, 0), player.CourseNode, true);
        }
        if (_selected != null && !CanDeposit(_selected, player)) ResetSelection();
    }

    internal static void DrawPresentation()
    {
        if (_selected == null || FocusStateManager.CurrentState != eFocusState.FPS) return;
        var local = PlayerManager.GetLocalPlayerAgent();
        if (local?.FPSCamera == null || !CanDeposit(_selected, local)) { ResetSelection(); return; }
        var position = SlotTransform(_selected, local.Inventory.WieldedSlot)!;
        PlacementPreview.Select(local.Inventory.WieldedItem.Get_pItemData().itemID_gearCRC, position.position, position.rotation);
        PlacementPreview.Draw(((CameraController)local.FPSCamera).m_camera);
    }

    private static bool Place(PlayerAgent player, Vector3 position, Quaternion rotation, AIGraph.AIG_CourseNode node, bool floor)
    {
        var held = player.Inventory.WieldedItem;
        if (held == null || node == null || !PlayerBackpackManager.TryGetItemInLevelFromItemData(held.Get_pItemData(), out var world)) return false;
        var item = world.TryCast<ItemInLevel>();
        var sync = item?.GetSyncComponent();
        if (sync == null || !PlayerBackpackManager.TryGetBackpack(player.Owner, out var backpack)) return false;
        var custom = sync.GetCustomData();
        custom.ammo = backpack.AmmoStorage.GetInventorySlotAmmo(player.Inventory.WieldedSlot).AmmoInPack;
        sync.AttemptPickupInteraction(ePickupItemInteractionType.Place, player.Owner, custom, position, rotation, node, floor, false);
        Plugin.PluginLog.LogInfo($"Resource placement requested: {held.PublicName}, amount={custom.ammo}, floor={floor}, position={position}.");
        return true;
    }

    [HarmonyPatch(typeof(ItemInLevel), nameof(ItemInLevel.Setup)), HarmonyPostfix]
    private static void Register(ItemInLevel __instance) => Items[__instance.GetInstanceID()] = __instance;

    [HarmonyPatch(typeof(ItemInLevel), nameof(ItemInLevel.OnDespawn)), HarmonyPrefix]
    private static void Unregister(ItemInLevel __instance)
    {
        var sync = __instance.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        if (sync != null) ReleaseSlot(sync.Pointer);
        Items.Remove(__instance.GetInstanceID()); ItemMarkers.Forget(__instance.GetInstanceID());
    }

    [HarmonyPatch(typeof(LG_WeakResourceContainer), nameof(LG_WeakResourceContainer.Setup)), HarmonyPostfix]
    private static void RegisterContainer(LG_WeakResourceContainer __instance)
    { Containers[__instance.GetInstanceID()] = __instance; RegisterSlots(__instance); }

    [HarmonyPatch(typeof(LG_WeakResourceContainer), nameof(LG_WeakResourceContainer.OnDestroy)), HarmonyPrefix]
    private static void RemoveContainer(LG_WeakResourceContainer __instance)
    { RemoveSlots(__instance); Containers.Remove(__instance.GetInstanceID()); }

    [HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.AttemptInteract)), HarmonyPrefix]
    private static bool ValidatePlacement(LG_PickupItem_Sync __instance, pPickupItemInteraction interaction)
    {
        if (!SNet.IsMaster || __instance.item == null) return true;
        if (Settings.Deposit.Value && interaction.type == ePickupItemInteractionType.Place)
        {
            var slot = FindPlacementSlot(interaction.placement.position);
            if (slot != null) return IsOpen(slot.Box) && (slot.Occupant == IntPtr.Zero || slot.Occupant == __instance.Pointer);
        }
        return true;
    }

    [HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.OnStateChange)), HarmonyPrefix]
    private static void AttachContainer(LG_PickupItem_Sync __instance, pPickupItemState newState)
    {
        var destination = TrackSlot(__instance, newState);
        if (!Settings.Deposit.Value || newState.updateCustomDataOnly) return;
        var item = __instance.item?.TryCast<ItemInLevel>();
        if (item == null || newState.status != ePickupItemStatus.PlacedInLevel) return;
        if (item.ItemDataBlock.inventorySlot != InventorySlot.ResourcePack && item.ItemDataBlock.inventorySlot != InventorySlot.Consumable) return;
        item.container = null;
        if (destination != null)
        {
            var tf = SlotTransform(destination, item.ItemDataBlock.inventorySlot);
            if (tf != null)
            {
                item.container = destination.Box.m_storage?.TryCast<LG_ResourceContainer_Storage>();
                __instance.transform.SetParent(tf, true);
                return;
            }
        }
        if (Builder.CurrentFloor != null) __instance.transform.SetParent(Builder.CurrentFloor.transform, true);
    }

    [HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.OnStateChange)), HarmonyPostfix]
    private static void Moved(LG_PickupItem_Sync __instance, pPickupItemState newState)
    {
        var state = newState;
        var item = __instance.item?.TryCast<ItemInLevel>();
        if (item == null) return;
        Items[item.GetInstanceID()] = item;
        // Only our droppable resources need container/culling repair. Native
        // carry objectives own their attachment, placement and graphics lifecycle.
        if (item.ItemDataBlock.inventorySlot != InventorySlot.ResourcePack && item.ItemDataBlock.inventorySlot != InventorySlot.Consumable) return;
        if (state.status == ePickupItemStatus.PlacedInLevel)
        {
            if (state.placement.node.TryGet(out var node)) item.m_courseNode = node;
            var terminal = item.GetComponentInChildren<LG_GenericTerminalItem>();
            if (terminal != null && item.CourseNode != null)
            {
                terminal.m_spawnNode = item.CourseNode;
                terminal.FloorItemLocation = item.CourseNode.m_zone.NavInfo.GetFormattedText(LG_NavInfoFormat.Full_And_Number_With_Underscore);
            }
        }
        else if (state.status == ePickupItemStatus.PickedUp)
        {
            item.container = null;
            if (__instance.transform.parent == null && Builder.CurrentFloor != null)
                __instance.transform.SetParent(Builder.CurrentFloor.transform, true);
        }
        // GTFO caches item rendering in the cull bucket's command buffer. Moving
        // the transform alone can leave a successfully placed item invisible.
        if (!newState.updateCustomDataOnly)
        {
            var culler = __instance.GetComponent<ItemCuller>();
            if (culler != null)
            {
                culler.UpdateBounds();
                if (culler.CullBucket != null)
                {
                    culler.CullBucket.NeedsShadowRefresh = true;
                    culler.CullBucket.SetDirtyCMDBuffer();
                }
            }
            if (state.status == ePickupItemStatus.PlacedInLevel && GameStateManager.CurrentStateName == eGameStateName.InLevel)
                Plugin.PluginLog.LogInfo($"Resource placement applied: {item.PublicName}, node={item.CourseNode != null}, culler={culler != null}, container={item.container != null}.");
        }
        ItemMarkers.PlacementCompleted(item, state);
        ItemMarkers.UpdateLabel(item);
    }

    [HarmonyPatch(typeof(ResourcePackPickup), nameof(ResourcePackPickup.OnCustomDataUpdated)), HarmonyPostfix]
    private static void AmountChanged(ResourcePackPickup __instance) => ItemMarkers.UpdateLabel(__instance);

    [HarmonyPatch(typeof(ConsumablePickup_Core), nameof(ConsumablePickup_Core.OnCustomDataUpdated)), HarmonyPostfix]
    private static void ConsumableAmountChanged(ConsumablePickup_Core __instance) => ItemMarkers.UpdateLabel(__instance);
}
