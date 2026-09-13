using HarmonyLib;
using LevelGeneration;
using Player;

namespace InfiniTweaks;

internal static partial class ItemMarkers
{
    [HarmonyPatch(typeof(LG_ResourceContainer_Sync), nameof(LG_ResourceContainer_Sync.OnStateChange)), HarmonyPostfix]
    private static void ContainerOpened(LG_ResourceContainer_Sync __instance, pResourceContainerItemState newState)
    {
        if (newState.status != eResourceContainerStatus.Open) return;
        // Opening is discovery, including another player's synchronized opening.
        // Contents need no ray to their mesh origin through the container shell.
        // Spawned items may be reparented outside the locker hierarchy.
        foreach (var item in ResourceHandling.Items.Values)
            if (item != null && item.container?.m_core?.TryCast<LG_WeakResourceContainer>()?.m_sync?.Pointer == __instance.Pointer)
                RememberPickup(item, false);
    }

    [HarmonyPatch(typeof(Interact_Timed), nameof(Interact_Timed.OnSelectedChange)), HarmonyPostfix]
    private static void PickupSelected(Interact_Timed __instance, bool __0, PlayerAgent __1)
    {
        if (!__0 || !Settings.Markers.Value || __1 == null || !__1.IsLocallyOwned) return;
        // Native selection has already passed interaction/visibility checks.
        // Match the actual interaction owner, including sibling prefab components.
        foreach (var item in ResourceHandling.Items.Values)
            if (item != null && item.GetPickupInteraction() == __instance)
            { RememberPickup(item, false); return; }
    }

    internal static void PlacementCompleted(ItemInLevel item, pPickupItemState state)
    {
        if (state.status != ePickupItemStatus.PlacedInLevel || state.updateCustomDataOnly) return;
        // Called after native OnStateChange, when GetCurrentState is authoritative.
        var carry = item.TryCast<CarryItemPickup_Core>();
        if (carry != null) { CarryChanged(carry); return; }
        // A new drop is fresh knowledge, not a continuation of an old dismissal.
        if (state.placement.hasBeenPickedUp) Dismissed.Remove(item.GetInstanceID());
        var container = item.container?.m_core?.TryCast<LG_WeakResourceContainer>();
        if (state.placement.hasBeenPickedUp || container != null && ResourceHandling.IsOpen(container)) RememberPickup(item, false);
    }
    internal static void UpdateLabel(ItemInLevel item)
    {
        if (Depleted(item)) { Forget(item.GetInstanceID()); return; }
        if (Pins.TryGetValue(item.GetInstanceID(), out var pin)) Label(pin);
        else
        {
            // Quantity can arrive after a floor drop or an open-container spawn.
            var box = item.container?.m_core?.TryCast<LG_WeakResourceContainer>();
            var state = item.GetSyncComponent()?.GetCurrentState();
            bool dropped = state?.status == ePickupItemStatus.PlacedInLevel && state?.placement.hasBeenPickedUp == true;
            if (dropped || box != null && ResourceHandling.IsOpen(box)) RememberPickup(item, false);
        }
    }
}
