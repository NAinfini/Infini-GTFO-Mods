using HarmonyLib;
using Gear;
using Player;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace InfiniTweaks;

// Keep a valid timed interaction selected. Suppression belongs to this FixedUpdate
// call only; restoring the prior flags avoids disabling interactions after an exception.
[HarmonyPatch(typeof(PlayerInteraction), nameof(PlayerInteraction.FixedUpdate))]
internal static class InteractionFixes
{
    internal struct State
    {
        internal bool Changed, Camera, Sphere, Ladder;
        internal Interact_Timed? Selected;
    }
    private static Interact_Timed? _target;
    private static Il2CppArrayBase<Collider>? _colliders;
    [HarmonyPrefix]
    private static void Before(PlayerInteraction __instance, out State __state)
    {
        __state = default;
        var owner = __instance.m_owner;
        if (owner == null || !owner.IsLocallyOwned || !owner.Alive || owner.Inventory == null) return;
        var inventory = owner.Inventory;
        var pack = inventory.WieldedItem?.TryCast<ResourcePackFirstPerson>();
        var carry = inventory.WieldedItem?.TryCast<CarryItemEquippableFirstPerson>();
        bool itemInteraction = pack != null && (pack.m_interactApplyResource.TimerIsActive || pack.FireButton || pack.AimButtonHeld) ||
            carry != null && (carry.m_interactDropItem.TimerIsActive || carry.m_interactInsertItem.TimerIsActive);
        var selected = __instance.m_bestSelectedInteract?.TryCast<Interact_Timed>();
        if (!itemInteraction && (selected == null || inventory.WieldedItem?.AllowPlayerInteraction != true || !Valid(__instance, selected))) return;
        __state = new State { Changed = true, Camera = PlayerInteraction.CameraRayInteractionEnabled,
            Sphere = PlayerInteraction.SphereCheckInteractionEnabled, Ladder = PlayerInteraction.LadderInteractionEnabled,
            Selected = itemInteraction ? null : selected };
        PlayerInteraction.CameraRayInteractionEnabled = false;
        PlayerInteraction.SphereCheckInteractionEnabled = false;
        PlayerInteraction.LadderInteractionEnabled = false;
        if (pack != null && itemInteraction && __instance.HasWorldInteraction)
        {
            __instance.UnSelectCurrentBestInteraction();
            pack.m_interactApplyResource.OnSelectedChange(true, owner, true);
            pack.m_interactApplyResource.OnTimerUpdate(pack.m_interactApplyResource.InteractionTimerRel);
        }
    }
    [HarmonyPostfix]
    private static void After(PlayerInteraction __instance, State __state)
    { if (__state.Changed) __instance.m_bestInteractInCurrentSearch = __state.Selected; }
    [HarmonyFinalizer]
    private static void Restore(State __state)
    {
        if (!__state.Changed) return;
        PlayerInteraction.CameraRayInteractionEnabled = __state.Camera;
        PlayerInteraction.SphereCheckInteractionEnabled = __state.Sphere;
        PlayerInteraction.LadderInteractionEnabled = __state.Ladder;
    }
    private static bool Valid(PlayerInteraction interaction, Interact_Timed target)
    {
        var owner = interaction.m_owner;
        if (!target.TimerIsActive || !target.IsActive || !target.PlayerCanInteract(owner) || owner.FPSCamera == null) return false;
        if (_target != target) { _target = target; _colliders = target.GetComponentsInChildren<Collider>(); }
        float radius = interaction.m_searchRadius + Mathf.Min(Mathf.Abs(owner.TargetLookDir.y), 0.5f);
        bool nearby = false;
        if (_colliders != null)
            foreach (var collider in _colliders)
                if (collider != null && collider.enabled && (collider.transform.position - owner.CamPos).sqrMagnitude <= radius * radius) { nearby = true; break; }
        if (!nearby) return false;
        var camera = owner.FPSCamera;
        var direction = target.transform.position - owner.CamPos;
        bool own(GameObject hit) => hit == target.gameObject || hit.transform.IsChildOf(target.transform);
        bool looking = camera.CameraRayObject != null && own(camera.CameraRayObject);
        if (!looking && Physics.Raycast(camera.m_camRay, out var aim, radius, LayerManager.MASK_PLAYER_INTERACT_SPHERE)) looking = own(aim.collider.gameObject);
        if (target.OnlyActiveWhenLookingStraightAt && !looking) return false;
        var screen = camera.m_camera.WorldToScreenPoint(target.transform.position);
        if (!looking && (screen.z <= 0 || !GuiManager.IsOnScreen(screen))) return false;
        if (target.RequireCollisionCheck && Physics.Linecast(owner.CamPos, target.transform.position, out var blocker, LayerManager.MASK_WORLD | LayerManager.MASK_PLAYER_INTERACT_SPHERE) && !own(blocker.collider.gameObject)) return false;
        if (direction.y < 2) direction.y = 0;
        if (direction.sqrMagnitude <= interaction.m_proximityRadius * interaction.m_proximityRadius) interaction.AddToProximity(target);
        else interaction.RemoveFromProximity(target);
        return true;
    }
    internal static void Clear() { _target = null; _colliders = null; }

    [HarmonyPatch(typeof(MineDeployerFirstPerson), nameof(MineDeployerFirstPerson.OnStickyMineSpawned))]
    private static class MinePickup
    {
        [HarmonyPostfix]
        private static void Spawned(ISyncedItem item)
        {
            var mine = item.TryCast<MineDeployerInstance>();
            if (mine?.PickupInteraction != null)
                mine.PickupInteraction.transform.Translate(0, 0, -.01f);
        }
    }

    [HarmonyPatch(typeof(Interact_Timed), nameof(Interact_Timed.CheckSoundPlayer))]
    private static class InteractionSound
    {
        [HarmonyPrefix]
        private static bool EnsurePosition(Interact_Timed __instance)
        {
            if (__instance.m_sound != null || __instance.transform != null) return true;
            var player = PlayerManager.GetLocalPlayerAgent();
            if (player != null) __instance.m_sound = new CellSoundPlayer(player.Position);
            return false;
        }
    }
}
