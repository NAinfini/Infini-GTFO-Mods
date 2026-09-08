using System.Collections.Generic;
using System.Reflection;
using CullingSystem;
using Gear;
using HarmonyLib;
using UnityEngine;

namespace InfiniTweaks;

[HarmonyPatch(typeof(PlayerInventoryBase), nameof(PlayerInventoryBase.UpdateFPSFlashlightAlignment))]
internal static class FlashlightAlignmentPatch
{
    private const float AimDistance = 6f;

    [HarmonyPostfix]
    private static void KeepFlashlightForward(PlayerInventoryBase __instance)
    {
        if (!Settings.NoFlashlightSway.Value)
        {
            return;
        }

        var owner = __instance.Owner;
        var networkOwner = owner?.Owner;
        var fpsCamera = owner?.FPSCamera;
        var cLight = __instance.m_flashlightCLight;
        if (owner is null ||
            !owner.IsLocallyOwned ||
            networkOwner is null ||
            networkOwner.IsBot ||
            fpsCamera is null ||
            cLight is null)
        {
            return;
        }

        var camera = ((CameraController)fpsCamera).m_camera;
        var light = ((C_Light)cLight).m_unityLight;
        if (camera is null || light is null)
        {
            return;
        }

        var direction = camera.transform.position + camera.transform.forward * AimDistance - light.transform.position;
        if (direction.sqrMagnitude > 0.0001f)
        {
            light.transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}

[HarmonyPatch(typeof(FPSCamera), nameof(FPSCamera.AddHitReact))]
internal static class AimPunchPatch
{
    [HarmonyPrefix]
    internal static void ApplyMultiplier(ref float punchAmountMul)
    {
        punchAmountMul *= Settings.AimPunchMultiplier.Value;
    }
}

[HarmonyPatch(typeof(Dam_PlayerDamageLimb), nameof(Dam_PlayerDamageLimb.BulletDamage))]
internal static class FriendlyBulletPatch
{
    [HarmonyPrefix]
    internal static bool AllowBulletDamage(Dam_PlayerDamageLimb __instance, Agents.Agent sourceAgent)
    {
        if (!Settings.TeammatesIgnoreBullets.Value || sourceAgent == null ||
            !LocalPlayer.IsHuman(sourceAgent.TryCast<Player.PlayerAgent>()))
        {
            return true;
        }

        var target = __instance.GetBaseAgent()?.TryCast<Player.PlayerAgent>();
        return target == null || target.Pointer == sourceAgent.Pointer;
    }
}

[HarmonyPatch(typeof(PlayerStamina), nameof(PlayerStamina.UseStamina))]
internal static class StaminaCostPatch
{
    [HarmonyPrefix]
    internal static bool ScaleCost(PlayerStamina __instance, ref PlayerStamina.ActionCost cost)
    {
        if (!LocalPlayer.IsHuman(__instance.m_owner))
        {
            return true;
        }

        var multiplier = Settings.StaminaCostMultiplier.Value;
        if (multiplier == 0f)
        {
            __instance.ResetStamina();
            return false;
        }

        // Scale the by-value ActionCost before the game clamps stamina at zero.
        // Negative costs are recovery and must retain their original strength.
        if (cost.baseStaminaCostInCombat > 0f)
        {
            cost.baseStaminaCostInCombat *= multiplier;
        }
        if (cost.baseStaminaCostOutOfCombat > 0f)
        {
            cost.baseStaminaCostOutOfCombat *= multiplier;
        }

        return true;
    }
}

[HarmonyPatch(typeof(PlayerStamina), nameof(PlayerStamina.LateUpdate))]
internal static class InfiniteStaminaPatch
{
    [HarmonyPostfix]
    internal static void KeepFull(PlayerStamina __instance)
    {
        // Combat caps can drain stamina independently of action costs.
        if (Settings.StaminaCostMultiplier.Value == 0f && LocalPlayer.IsHuman(__instance.m_owner))
        {
            __instance.ResetStamina();
        }
    }
}

[HarmonyPatch(typeof(PlayerStamina), nameof(PlayerStamina.UseJumpStamina))]
internal static class JumpCostPatch
{
    [HarmonyPrefix]
    internal static bool AllowJumpCost(PlayerStamina __instance) =>
        !Settings.RemoveJumpCost.Value || !LocalPlayer.IsHuman(__instance.m_owner);
}

[HarmonyPatch]
internal static class MeleeCostPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PlayerStamina), nameof(PlayerStamina.UseWeaponLightSwingStamina));
        yield return AccessTools.Method(typeof(PlayerStamina), nameof(PlayerStamina.UseWeaponChargedSwingStamina));
    }

    [HarmonyPrefix]
    internal static bool AllowMeleeCost(PlayerStamina __instance) =>
        !Settings.RemoveMeleeCost.Value || !LocalPlayer.IsHuman(__instance.m_owner);
}

[HarmonyPatch(typeof(MWS_ChargeUp), nameof(MWS_ChargeUp.Enter))]
internal static class ChargeRecoveryPatch
{
    [HarmonyPostfix]
    internal static void AllowRecovery(MWS_ChargeUp __instance)
    {
        var owner = __instance.m_weapon?.Owner;
        if (Settings.EnableChargeRecovery.Value && LocalPlayer.IsHuman(owner) && owner!.Stamina != null)
        {
            owner.Stamina.AllowRegen = true;
        }
    }
}

internal static class LocalPlayer
{
    internal static bool IsHuman(Player.PlayerAgent? player) =>
        player != null && player.IsLocallyOwned && player.Owner != null && !player.Owner.IsBot;
}
