using System;
using System.Collections.Generic;
using HarmonyLib;
using LevelGeneration;
using Localization;
using Player;
using UnityEngine;

namespace InfiniTweaks;

internal static partial class ResourceHandling
{
    private sealed record DepositSlot(LG_WeakResourceContainer Box, StorageSlot Storage, GameObject Object, BoxCollider Bounds, Interact_Timed Interaction)
    { internal IntPtr Occupant; }
    private static readonly Dictionary<int, List<DepositSlot>> Slots = new();
    private static readonly Dictionary<IntPtr, List<DepositSlot>> ContainerSlots = new();
    private static readonly Dictionary<IntPtr, DepositSlot> Occupants = new();
    private static DepositSlot? _selected;

    // Stock container compartment measurements, checked against DropItemPlus 1.0.1.
    // Interaction volumes cover the shelf, not the tiny pickup attachment point.
    private static readonly (Vector3 Position, Vector3 Size)[] LockerVolumes =
    {
        (new(-.724f, -.252f, 1.754f), new(.42f, .433f, .3f)),
        (new(-.672f, -.252f, 1.454f), new(.5f, .433f, .25f)),
        (new(-.672f, -.252f, 1.154f), new(.5f, .433f, .25f)),
        (new(-.672f, -.252f, .854f), new(.5f, .433f, .25f)),
        (new(-.672f, -.252f, .354f), new(.5f, .433f, .67f)),
        (new(-.276f, -.252f, 1.754f), new(.42f, .433f, .3f))
    };
    private static readonly (Vector3 Position, Vector3 Size)[] BoxVolumes =
    {
        (new(.009f, 0, .163f), new(.33f, .54f, .323f)),
        (new(-.343f, 0, .163f), new(.34f, .54f, .323f)),
        (new(.358f, 0, .163f), new(.33f, .54f, .323f))
    };

    private static Transform? SlotTransform(DepositSlot slot, InventorySlot type) => type switch
    { InventorySlot.ResourcePack => slot.Storage.ResourcePack, InventorySlot.Consumable => slot.Storage.Consumable, _ => null };

    private static bool CanDeposit(DepositSlot slot, PlayerAgent player) =>
        Settings.Deposit.Value && slot.Box != null && slot.Occupant == IntPtr.Zero && IsOpen(slot.Box) &&
        player != null && player.Alive && player.Inventory?.WieldedItem != null &&
        SlotTransform(slot, player.Inventory.WieldedSlot) != null && slot.Box.SpawnNode != null &&
        player.CourseNode != null && slot.Box.SpawnNode.m_dimension.DimensionIndex == player.CourseNode.m_dimension.DimensionIndex;

    private static void RegisterSlots(LG_WeakResourceContainer box)
    {
        int id = box.GetInstanceID();
        if (Slots.ContainsKey(id)) return;
        var storage = box.m_storage?.TryCast<LG_ResourceContainer_Storage>();
        if (storage?.m_storageSlots == null) return;
        var volumes = box.m_isLocker ? LockerVolumes : BoxVolumes;
        if (storage.m_storageSlots.Length != volumes.Length)
        {
            Plugin.PluginLog.LogWarning($"Deposit: unsupported container layout {box.name}, {storage.m_storageSlots.Length} slots; no guessed slot volumes created.");
            Slots[id] = new(); return;
        }
        var entries = new List<DepositSlot>(); Slots[id] = entries;
        var sync = box.m_sync?.TryCast<LG_ResourceContainer_Sync>();
        if (sync != null) ContainerSlots[sync.Pointer] = entries;
        for (int i = 0; i < volumes.Length; i++)
        {
            var native = storage.m_storageSlots[i];
            if (native.ResourcePack == null && native.Consumable == null) continue;
            var obj = new GameObject("InfiniTweaks.DepositSlot." + i) { layer = LayerManager.LAYER_INTERACTION };
            obj.transform.SetParent(box.transform, false);
            obj.transform.localPosition = volumes[i].Position;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;
            var bounds = obj.AddComponent<BoxCollider>(); bounds.size = volumes[i].Size;
            var interaction = obj.AddComponent<Interact_Timed>();
            var entry = new DepositSlot(box, native, obj, bounds, interaction); entries.Add(entry);
            interaction.m_colliderToOwn = bounds;
            interaction.InteractDuration = .4f;
            interaction.InteractionMessage = "";
            interaction.OnlyActiveWhenLookingStraightAt = true;
            interaction.ExternalPlayerCanInteract = (Func<PlayerAgent, bool>)(player => CanDeposit(entry, player));
            interaction.OnInteractionSelected = (Action<PlayerAgent, bool>)((player, selected) =>
            {
                if (player == null || !player.IsLocallyOwned) return;
                if (!selected) { if (_selected == entry) ResetSelection(); return; }
                _selected = entry; DepositPrompt(player);
            });
            interaction.OnInteractionTriggered = (Action<PlayerAgent>)(player =>
            {
                if (player == null || !player.IsLocallyOwned || !CanDeposit(entry, player)) return;
                var target = SlotTransform(entry, player.Inventory.WieldedSlot)!;
                if (Place(player, target.position, target.rotation, box.SpawnNode, false)) ResetSelection();
            });
            interaction.SetActive(IsOpen(box));
        }
    }

    private static void DepositPrompt(PlayerAgent player)
    {
        if (GuiManager.InteractionLayer == null || player.Inventory.WieldedItem == null) return;
        GuiManager.InteractionLayer.SetInteractPrompt(string.Format(Text.Get(864u), player.Inventory.WieldedItem.PublicName),
            string.Format(Text.Get(827u), InputMapper.GetBindingName(InputAction.Use)), ePUIMessageStyle.Default);
        GuiManager.InteractionLayer.InteractPromptVisible = true;
    }

    [HarmonyPatch(typeof(PlayerInventoryLocal), nameof(PlayerInventoryLocal.DoWieldItem)), HarmonyPostfix]
    private static void DepositWieldChanged(PlayerInventoryLocal __instance)
    {
        if (_selected == null) return;
        if (CanDeposit(_selected, __instance.Owner)) DepositPrompt(__instance.Owner);
        else ResetSelection();
    }

    [HarmonyPatch(typeof(LG_ResourceContainer_Sync), nameof(LG_ResourceContainer_Sync.OnStateChange)), HarmonyPostfix]
    private static void DepositContainerChanged(LG_ResourceContainer_Sync __instance)
    {
        if (!ContainerSlots.TryGetValue(__instance.Pointer, out var slots)) return;
        foreach (var slot in slots)
            if (slot.Box != null) slot.Interaction.SetActive(IsOpen(slot.Box) && slot.Occupant == IntPtr.Zero);
    }

    private static DepositSlot? FindSlot(Vector3 position)
    {
        foreach (var list in Slots.Values)
            foreach (var slot in list)
                if (slot.Box != null && slot.Bounds != null)
                {
                    // Collider.bounds becomes empty while disabled (occupied/closed).
                    var local = slot.Object.transform.InverseTransformPoint(position) - slot.Bounds.center;
                    var half = slot.Bounds.size * .5f;
                    if (Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z) return slot;
                }
        return null;
    }

    private static DepositSlot? FindPlacementSlot(Vector3 position)
    {
        var slot = FindSlot(position);
        if (slot == null) return null;
        bool Matches(Transform? point) => point != null && (point.position - position).sqrMagnitude < .001f;
        return Matches(slot.Storage.ResourcePack) || Matches(slot.Storage.Consumable) ? slot : null;
    }

    private static void ReleaseSlot(IntPtr occupant)
    {
        if (!Occupants.Remove(occupant, out var slot)) return;
        slot.Occupant = IntPtr.Zero;
        if (slot.Box != null) slot.Interaction.SetActive(IsOpen(slot.Box));
    }
    private static void ResetSlotOccupants()
    {
        Occupants.Clear();
        foreach (var list in Slots.Values)
            foreach (var slot in list) { slot.Occupant = IntPtr.Zero; slot.Interaction.SetActive(IsOpen(slot.Box)); }
    }
    private static DepositSlot? TrackSlot(LG_PickupItem_Sync sync, pPickupItemState state, Vector3? initialPosition = null)
    {
        if (state.updateCustomDataOnly) return Occupants.TryGetValue(sync.Pointer, out var current) ? current : null;
        ReleaseSlot(sync.Pointer);
        if (state.status != ePickupItemStatus.PlacedInLevel) return null;
        var destination = FindSlot(initialPosition ?? state.placement.position);
        if (destination == null) return null;
        if (destination.Occupant != IntPtr.Zero && destination.Occupant != sync.Pointer)
        {
            Plugin.PluginLog.LogWarning("Deposit: native state contains multiple items in one compartment; retaining first occupant.");
            return null;
        }
        Occupants[sync.Pointer] = destination;
        destination.Occupant = sync.Pointer; destination.Interaction.SetActive(false);
        return destination;
    }

    private static void RemoveSlots(LG_WeakResourceContainer box)
    {
        if (!Slots.Remove(box.GetInstanceID(), out var list)) return;
        var sync = box.m_sync?.TryCast<LG_ResourceContainer_Sync>();
        if (sync != null) ContainerSlots.Remove(sync.Pointer);
        foreach (var slot in list)
        {
            if (slot.Occupant != IntPtr.Zero) Occupants.Remove(slot.Occupant);
            if (_selected == slot) ResetSelection();
            if (slot.Object != null) UnityEngine.Object.Destroy(slot.Object);
        }
    }
    private static void ClearSlots()
    {
        ResetSelection();
        foreach (var list in Slots.Values)
            foreach (var slot in list) if (slot.Object != null) UnityEngine.Object.Destroy(slot.Object);
        Slots.Clear(); ContainerSlots.Clear(); Occupants.Clear();
    }
}
