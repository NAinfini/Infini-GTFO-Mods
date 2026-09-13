using GameData;
using GTFO.API;
using UnityEngine;

namespace InfiniTweaks;

internal static class GameDataTweaks
{
    private static bool _applied;
    private const float MinimumFlashlightRange = 0.1f;
    private const float MaximumFlashlightRange = 100f;
    private const float MinimumFlashlightAngle = 1f;
    private const float MaximumFlashlightAngle = 179f;

    internal static void Apply()
    {
        if (_applied)
        {
            return;
        }
        _applied = true;
        GameDataAPI.OnGameDataInitialized -= Apply;

        ApplyFlashlights();
        ApplyHelGun(Settings.HelGunVersion.Value);
        ApplySniper(Settings.SniperVersion.Value);
        ApplyRecoilMultiplier();
    }

    private static void ApplyFlashlights()
    {
        var count = 0;
        foreach (var block in GameDataBlockBase<FlashlightSettingsDataBlock>.GetAllBlocks())
        {
            if (block is null || !block.internalEnabled)
            {
                continue;
            }

            block.range = Mathf.Clamp(
                block.range + Settings.FlashlightRangeAdjustment.Value,
                MinimumFlashlightRange,
                MaximumFlashlightRange);
            block.angle = Mathf.Clamp(
                block.angle + Settings.FlashlightAngleAdjustment.Value,
                MinimumFlashlightAngle,
                MaximumFlashlightAngle);
            count++;
        }

        Plugin.PluginLog.LogInfo(
            $"Adjusted {count} flashlight definitions by {Settings.FlashlightRangeAdjustment.Value:+0.##;-0.##;0} m and {Settings.FlashlightAngleAdjustment.Value:+0.##;-0.##;0} degrees.");
    }

    private static void ApplyHelGun(WeaponPreset preset)
    {
        if (preset == WeaponPreset.Disabled)
        {
            return;
        }

        var block = GameDataBlockBase<ArchetypeDataBlock>.GetBlock(21u);
        if (block is null)
        {
            Plugin.PluginLog.LogWarning("HEL Gun archetype 21 was not found; its preset was not applied.");
            return;
        }

        block.Damage = 16.25f;
        block.DamageFalloff = new Vector2(15f, 90f);
        block.StaggerDamageMulti = 1f;
        block.PrecisionDamageMulti = 0.8f;
        block.DefaultClipSize = 9;
        block.DefaultReloadTime = 3.3f;
        block.CostOfBullet = preset == WeaponPreset.OriginalR6 ? 6.5f : 5.74f;
        block.ShotDelay = 0.1f;
        block.PiercingBullets = true;
        block.PiercingDamageCountLimit = 4;
        block.HipFireSpread = 1.7f;
        block.AimSpread = 0f;
        block.SpecialChargetupTime = preset == WeaponPreset.OriginalR6 ? 0.1f : 0.2f;
        block.SpecialCooldownTime = 0f;
        ApplyCommonWeaponStats(block, false);
        block.RecoilDataID = CreatePresetRecoil(false, preset == WeaponPreset.OriginalR6).persistentID;

        Plugin.PluginLog.LogInfo($"Applied {preset} HEL Gun preset.");
    }

    private static void ApplySniper(WeaponPreset preset)
    {
        if (preset == WeaponPreset.Disabled)
        {
            return;
        }

        var archetype = GameDataBlockBase<ArchetypeDataBlock>.GetBlock(29u);
        if (archetype is null)
        {
            Plugin.PluginLog.LogWarning("Sniper archetype 29 was not found; its preset was not applied.");
            return;
        }

        var r6 = preset == WeaponPreset.OriginalR6;
        archetype.Damage = 40.01f;
        archetype.DamageFalloff = new Vector2(60f, 100f);
        archetype.StaggerDamageMulti = 1f;
        archetype.PrecisionDamageMulti = 2.0025f;
        archetype.DefaultClipSize = r6 ? 3 : 2;
        archetype.DefaultReloadTime = 3.5f;
        archetype.CostOfBullet = r6 ? 15f : 17.5f;
        archetype.ShotDelay = r6 ? 0.5f : 0.8f;
        archetype.PiercingBullets = false;
        archetype.PiercingDamageCountLimit = 5;
        archetype.HipFireSpread = r6 ? 3f : 13f;
        archetype.AimSpread = 0f;
        archetype.SpecialChargetupTime = 0f;
        archetype.SpecialCooldownTime = 0f;
        ApplyCommonWeaponStats(archetype, true);
        archetype.RecoilDataID = CreatePresetRecoil(true, r6).persistentID;

        Plugin.PluginLog.LogInfo($"Applied {preset} Sniper preset.");
    }

    private static void ApplyCommonWeaponStats(ArchetypeDataBlock block, bool sniper)
    {
        block.FireMode = eWeaponFireMode.Semi;
        block.DamageBoosterEffect = sniper ? AgentModifier.SniperDamage : AgentModifier.HELDamage;
        block.EquipTransitionTime = sniper ? 0.8f : 0.6f;
        block.AimTransitionTime = sniper ? 0.4f : 0.35f;
        block.BurstDelay = 0f;
        block.BurstShotCount = 0;
        block.ShotgunBulletCount = 0;
        block.ShotgunConeSize = 0;
        block.ShotgunBulletSpread = 0;
        block.SpecialSemiBurstCountTimeout = 0f;
    }

    private static RecoilDataBlock CreatePresetRecoil(bool sniper, bool r6)
    {
        // Native recoil 9 is shared by several archetypes. Allocate a private
        // datablock so selecting a Sniper preset cannot change another weapon.
        uint highestId = 0;
        foreach (var block in GameDataBlockBase<RecoilDataBlock>.GetAllBlocks())
        {
            if (block != null && block.persistentID > highestId)
            {
                highestId = block.persistentID;
            }
        }

        var recoil = new RecoilDataBlock
        {
            persistentID = checked(highestId + 1),
            name = $"InfiniTweaks_{(sniper ? "Sniper" : "HelGun")}_{(r6 ? "R6" : "R8")}",
            internalEnabled = true,
            power = new MinMaxValue(),
            horizontalScale = new MinMaxValue(),
            verticalScale = new MinMaxValue()
        };
        recoil.power.Min = sniper ? (r6 ? 1.5f : 2f) : 3f;
        recoil.power.Max = sniper ? (r6 ? 2f : 3f) : 4f;
        recoil.spring = 2f;
        recoil.dampening = sniper && !r6 ? 9f : 20f;
        recoil.hipFireCrosshairSizeDefault = sniper ? (r6 ? 100f : 200f) : 60f;
        recoil.hipFireCrosshairRecoilPop = sniper ? (r6 ? 180f : 250f) : 25f;
        recoil.hipFireCrosshairSizeMax = sniper ? (r6 ? 180f : 300f) : 90f;
        recoil.horizontalScale.Min = sniper ? (r6 ? 0.13f : -0.5f) : -0.2f;
        recoil.horizontalScale.Max = sniper ? (r6 ? 0.23f : 0.5f) : 0.1f;
        recoil.verticalScale.Min = sniper ? (r6 ? 0.7f : 1.2f) : 0.6f;
        recoil.verticalScale.Max = sniper ? (r6 ? 0.8f : 1.8f) : 1f;
        recoil.directionalSimilarity = sniper ? 0f : 0.402f;
        recoil.worldToViewSpaceBlendHorizontal = sniper ? 0.02f : 0.2f;
        recoil.worldToViewSpaceBlendVertical = sniper ? 0.05f : 0.2f;
        recoil.recoilPosImpulse = new Vector3(0f, 0f, sniper ? -0.6f : -0.8f);
        recoil.recoilPosShift = new Vector3(0f, sniper ? -0.1f : -0.02f, -0.1f);
        recoil.recoilPosShiftWeight = 1f;
        recoil.recoilPosStiffness = 250f;
        recoil.recoilPosDamping = 25f;
        recoil.recoilPosImpulseWeight = 1f;
        recoil.recoilCameraPosWeight = 1f;
        recoil.recoilAimingWeight = sniper ? 2f : 1.3f;
        recoil.recoilRotImpulse = new Vector3(-1f, 1f, sniper ? 5f : 1f);
        recoil.recoilRotStiffness = 50f;
        recoil.recoilRotDamping = 8f;
        recoil.recoilRotImpulseWeight = 200f;
        recoil.recoilCameraRotWeight = 1f;
        recoil.concussionIntensity = sniper ? 3f : 2f;
        recoil.concussionFrequency = 15f;
        recoil.concussionDuration = sniper ? 0.35f : 0.2f;
        return GameDataBlockBase<RecoilDataBlock>.AddBlock(recoil);
    }

    private static void ApplyRecoilMultiplier()
    {
        var multiplier = Settings.RecoilMultiplier.Value;
        if (Mathf.Approximately(multiplier, 1f))
        {
            return;
        }

        var count = 0;
        foreach (var block in GameDataBlockBase<RecoilDataBlock>.GetAllBlocks())
        {
            if (block is null || !block.internalEnabled)
            {
                continue;
            }

            block.power.Min *= multiplier;
            block.power.Max *= multiplier;
            block.recoilPosImpulse *= multiplier;
            block.recoilPosShift *= multiplier;
            block.recoilRotImpulse *= multiplier;
            block.concussionIntensity *= multiplier;
            count++;
        }

        Plugin.PluginLog.LogInfo($"Applied recoil multiplier {multiplier:0.##} to {count} recoil definitions.");
    }
}
