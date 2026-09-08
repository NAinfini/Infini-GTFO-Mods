using System;
using System.Collections.Generic;
using Gear;
using GTFO.API;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace InfiniTweaks;

[HarmonyPatch]
internal static class ResourceHandling
{
    internal static readonly Dictionary<int, ItemInLevel> Items = new();
    private static readonly Dictionary<int, LG_WeakResourceContainer> Containers = new();
    private static Transform? _slot;
    private static LG_WeakResourceContainer? _box;
    private static float _heldSince = -1, _nextSearch;
    private static bool _submitted;
    internal static string Prompt = "";
    internal static Vector3? PreviewPosition => _slot == null ? null : _slot.position;
    private struct MergePin { public uint From, To; }
    private const string MergePinEvent = "InfiniTweaks.Resources.MergePin.v1";

    internal static void Initialize()
    {
        NetworkAPI.RegisterEvent<MergePin>(MergePinEvent, (sender, transfer) =>
        {
            if (!SNet.HasMaster || sender != SNet.Master.Lookup || GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
            ItemInLevel? from = null, to = null;
            foreach (var item in Items.Values)
            {
                if (item == null) continue;
                if (item.SyncID == transfer.From) from = item;
                if (item.SyncID == transfer.To) to = item;
            }
            if (from != null && to != null) ItemMarkers.Transfer(from.GetInstanceID(), to);
        });
    }

    internal static void Clear()
    {
        Items.Clear(); Containers.Clear(); ResetSelection();
    }
    private static void ResetSelection()
    {
        _slot = null; _box = null; _heldSince = -1; _submitted = false; Prompt = "";
    }
    internal static void Tick(PlayerAgent player)
    {
        bool resource = player.Inventory.WieldedSlot == InventorySlot.ResourcePack;
        if (!player.Alive || (!resource && player.Inventory.WieldedSlot != InventorySlot.Consumable) || FocusStateManager.CurrentState != eFocusState.FPS)
        { ResetSelection(); return; }
        if (Settings.Drop.Value && Input.GetKeyDown(Settings.DropKey.Value))
        {
            var camera = ((CameraController)player.FPSCamera).m_camera;
            if (camera != null && Physics.Raycast(camera.transform.position, (camera.transform.forward + Vector3.down * 0.4f).normalized, out var ground, 3f) && ground.normal.y > 0.6f)
                Place(player, ground.point + Vector3.up * 0.04f, Quaternion.Euler(0, camera.transform.eulerAngles.y, 0), player.CourseNode, true);
        }
        if (!Settings.Deposit.Value) { ResetSelection(); return; }
        if (Time.unscaledTime >= _nextSearch)
        {
            _nextSearch = Time.unscaledTime + 0.1f;
            Transform? best = null;
            LG_WeakResourceContainer? box = null;
            var camera = ((CameraController)player.FPSCamera).m_camera;
            if (camera != null && Physics.Raycast(camera.transform.position, camera.transform.forward, out var hit, 3f))
            {
                box = hit.collider.GetComponentInParent<LG_WeakResourceContainer>();
                if (box != null && box.ISOpen)
                {
                    var storage = box.m_storageComp?.TryCast<LG_ResourceContainer_Storage>();
                    float score = float.MaxValue;
                    if (storage != null)
                        foreach (var slot in storage.m_storageSlots)
                        {
                            var tf = resource ? slot.ResourcePack : slot.Consumable;
                            if (tf == null || Occupied(tf.position)) continue;
                            float distance = (tf.position - hit.point).sqrMagnitude;
                            if (distance < score) { best = tf; score = distance; }
                        }
                }
            }
            if (best != _slot) { _heldSince = -1; _submitted = false; }
            _slot = best; _box = box;
        }
        if (_slot == null || _box == null) { Prompt = ""; return; }
        var held = player.Inventory.WieldedItem;
        if (held == null) { ResetSelection(); return; }
        Prompt = $"Hold {InputMapper.GetBindingName(InputAction.Use)}: put back {held.PublicName}";
        if (!InputMapper.GetButton.Invoke(InputAction.Use, eFocusState.FPS)) { _heldSince = -1; _submitted = false; return; }
        if (_submitted) return;
        if (_heldSince < 0) _heldSince = Time.unscaledTime;
        if (Time.unscaledTime - _heldSince < 0.35f) return;
        if (Occupied(_slot.position)) { ResetSelection(); return; }
        if (!Place(player, _slot.position, _slot.rotation, _box.SpawnNode, false)) return;
        _submitted = true;
        Prompt = "Placement requested";
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
        return true;
    }

    private static bool Occupied(Vector3 position, Item? except = null)
    {
        foreach (var item in Items.Values)
        {
            if (item == null || item == except) continue;
            var sync = item.GetSyncComponent();
            if (sync != null && sync.GetCurrentState().status == ePickupItemStatus.PlacedInLevel && (item.transform.position - position).sqrMagnitude < 0.09f) return true;
        }
        return false;
    }

    [HarmonyPatch(typeof(ItemInLevel), nameof(ItemInLevel.Setup)), HarmonyPostfix]
    private static void Register(ItemInLevel __instance) => Items[__instance.GetInstanceID()] = __instance;

    [HarmonyPatch(typeof(ItemInLevel), nameof(ItemInLevel.OnDespawn)), HarmonyPrefix]
    private static void Unregister(ItemInLevel __instance)
    {
        Items.Remove(__instance.GetInstanceID()); ItemMarkers.Forget(__instance.GetInstanceID());
    }

    [HarmonyPatch(typeof(LG_WeakResourceContainer), nameof(LG_WeakResourceContainer.Setup)), HarmonyPostfix]
    private static void RegisterContainer(LG_WeakResourceContainer __instance) => Containers[__instance.GetInstanceID()] = __instance;

    [HarmonyPatch(typeof(LG_WeakResourceContainer), nameof(LG_WeakResourceContainer.OnDestroy)), HarmonyPrefix]
    private static void RemoveContainer(LG_WeakResourceContainer __instance) => Containers.Remove(__instance.GetInstanceID());

    [HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.AttemptInteract)), HarmonyPrefix]
    private static bool Interact(LG_PickupItem_Sync __instance, ref pPickupItemInteraction interaction)
    {
        if (!SNet.IsMaster || __instance.item == null) return true;
        if (Settings.Deposit.Value && interaction.type == ePickupItemInteractionType.Place)
        {
            foreach (var box in Containers.Values)
            {
                if (box == null) continue;
                var storage = box.m_storageComp?.TryCast<LG_ResourceContainer_Storage>();
                if (storage == null) continue;
                foreach (var slot in storage.m_storageSlots)
                    foreach (var transform in new[] { slot.ResourcePack, slot.Consumable })
                        if (transform != null && (transform.position - interaction.placement.position).sqrMagnitude < 0.001f)
                            return box.ISOpen && !Occupied(interaction.placement.position, __instance.item);
            }
        }
        var pickup = __instance.item.TryCast<ResourcePackPickup>();
        if (pickup == null) return true;
        if (!Settings.Stack.Value || interaction.type != ePickupItemInteractionType.Pickup || __instance.GetCurrentState().status != ePickupItemStatus.PlacedInLevel) return true;
        if (!interaction.pPlayer.TryGetPlayer(out var owner) || !PlayerBackpackManager.TryGetBackpack(owner, out var backpack) || !backpack.TryGetBackpackItem(InventorySlot.ResourcePack, out var held)) return true;
        if (held.Instance == null || held.ItemID != pickup.ItemDataBlock.persistentID) return true;
        float heldAmount = backpack.AmmoStorage.ResourcePackAmmo.AmmoInPack;
        var data = __instance.GetCustomData();
        if (!CasualRules.TryMerge(heldAmount, data.ammo, Settings.StackUses.Value * 20f, out float combined, out float remainder)) return true;

        // Native pickup handles transfer/ownership and leaves the old carried pack
        // in the world on overflow. Keep amounts in native units, not rounded uses.
        if (remainder == 0 && PlayerBackpackManager.TryGetItemInLevelFromItemData(held.Instance.Get_pItemData(), out var oldItem))
        {
            ItemMarkers.Transfer(oldItem.GetInstanceID(), pickup);
            NetworkAPI.InvokeEvent(MergePinEvent, new MergePin { From = oldItem.SyncID, To = pickup.SyncID }, SNet_ChannelType.GameOrderCritical);
        }
        if (remainder == 0)
            PlayerBackpackManager.MasterRemoveItem(held.Instance, owner);
        else
            backpack.AmmoStorage.SetAmmo(AmmoType.ResourcePackRel, remainder);
        data.ammo = combined;
        interaction.custom = data;
        __instance.SetCustomData(data, true);
        return true;
    }

    [HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.OnStateChange)), HarmonyPostfix]
    private static void Moved(LG_PickupItem_Sync __instance)
    {
        var state = __instance.GetCurrentState();
        var item = __instance.item?.TryCast<ItemInLevel>();
        if (item == null) return;
        Items[item.GetInstanceID()] = item;
        if (state.status == ePickupItemStatus.PlacedInLevel)
        {
            if (state.placement.node.TryGet(out var node)) item.m_courseNode = node;
            var terminal = item.GetComponent<LG_GenericTerminalItem>();
            if (terminal != null) terminal.m_spawnNode = item.CourseNode;
        }
        ItemMarkers.UpdateLabel(item);
    }

    [HarmonyPatch(typeof(ResourcePackPickup), nameof(ResourcePackPickup.OnCustomDataUpdated)), HarmonyPostfix]
    private static void AmountChanged(ResourcePackPickup __instance) => ItemMarkers.UpdateLabel(__instance);
}
