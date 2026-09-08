using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GTFO.API;
using HarmonyLib;

namespace InfiniTweaks;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("dev.gtfomodding.gtfo-api", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.InfiniTweaks";
    public const string PluginName = "Infini Tweaks";
    public const string PluginVersion = "2.1.1";

    internal static ManualLogSource PluginLog { get; private set; } = null!;

    private Harmony? _harmony;

    public override void Load()
    {
        PluginLog = Log;
        Settings.Bind(Config);

        GameDataAPI.OnGameDataInitialized += GameDataTweaks.Apply;
        BetterMaps.Initialize();
        CasualRuntime.Initialize();
        AddComponent<CasualOverlay>();

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
    }

    // Datablocks and map prefabs are changed for the lifetime of the process.
    // BepInEx must not report a successful hot unload without restoring them.
    public override bool Unload() => false;
}

internal enum WeaponPreset
{
    Disabled,
    OriginalR6,
    R8Current
}

internal static partial class Settings
{
    internal static ConfigEntry<float> FlashlightRangeAdjustment { get; private set; } = null!;
    internal static ConfigEntry<float> FlashlightAngleAdjustment { get; private set; } = null!;
    internal static ConfigEntry<bool> NoFlashlightSway { get; private set; } = null!;

    internal static ConfigEntry<float> RecoilMultiplier { get; private set; } = null!;
    internal static ConfigEntry<float> AimPunchMultiplier { get; private set; } = null!;
    internal static ConfigEntry<bool> TeammatesIgnoreBullets { get; private set; } = null!;

    internal static ConfigEntry<float> StaminaCostMultiplier { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableChargeRecovery { get; private set; } = null!;
    internal static ConfigEntry<bool> RemoveMeleeCost { get; private set; } = null!;
    internal static ConfigEntry<bool> RemoveJumpCost { get; private set; } = null!;

    internal static ConfigEntry<bool> EnableBetterMaps { get; private set; } = null!;
    internal static ConfigEntry<float> MapBlurScale { get; private set; } = null!;
    internal static ConfigEntry<float> MapOutlineScale { get; private set; } = null!;

    internal static ConfigEntry<WeaponPreset> HelGunVersion { get; private set; } = null!;
    internal static ConfigEntry<WeaponPreset> SniperVersion { get; private set; } = null!;

    internal static void Bind(ConfigFile config)
    {
        FlashlightRangeAdjustment = config.Bind(
            "Flashlight",
            "RangeAdjustmentMeters",
            10f,
            new ConfigDescription(
                "Adds metres to each flashlight's original range; this is not the final range.\n10 = 10 metres farther, -5 = 5 metres shorter, 0 = vanilla range. Final range is limited to 0.1-100 metres.\nRestart the game after changing this setting.",
                new AcceptableValueRange<float>(-100f, 100f)));
        FlashlightAngleAdjustment = config.Bind(
            "Flashlight",
            "AngleAdjustmentDegrees",
            35f,
            new ConfigDescription(
                "Adds degrees to each flashlight's original cone angle; this is not the final angle.\n35 = 35 degrees wider, -20 = 20 degrees narrower, 0 = vanilla angle. Final angle is limited to 1-179 degrees.\nRestart the game after changing this setting.",
                new AcceptableValueRange<float>(-178f, 178f)));
        NoFlashlightSway = config.Bind(
            "Flashlight",
            "NoSway",
            true,
            "true = keep your flashlight aimed with the camera while moving; false = vanilla flashlight movement.\nOnly changes the beam direction, not weapon sway or firing animations. Never turns the flashlight on or off.");

        RecoilMultiplier = config.Bind(
            "Combat",
            "RecoilMultiplier",
            1f,
            new ConfigDescription(
                "Scales firing recoil for all guns: 1 = original strength, 0.5 = half, 0 = no procedural recoil.\nApplies after any selected HEL Gun/Sniper preset. Includes aiming kick, weapon recoil impulses and firing concussion; does not change spread, accuracy, recovery speed or baked firing animations.\nRestart the game after changing this setting.",
                new AcceptableValueRange<float>(0f, 1f)));
        AimPunchMultiplier = config.Bind(
            "Combat",
            "AimPunchMultiplier",
            1f,
            new ConfigDescription(
                "Controls how much your view is knocked off target when you take damage, not recoil from firing.\n1 = vanilla aim punch, 0.5 = half, 0.1 = 10% of vanilla. Does not reduce the damage you take.",
                new AcceptableValueRange<float>(0.1f, 1f)));
        TeammatesIgnoreBullets = config.Bind(
            "Combat",
            "TeammatesIgnoreBullets",
            false,
            "true = your bullets cannot damage teammates, including bots; false = normal outgoing bullet damage.\nDoes not protect you from teammates' bullets: they must also enable this to stop their outgoing friendly fire. Does not make bullets pass through teammates or change enemy damage.");

        BindStamina(config);

        EnableBetterMaps = config.Bind(
            "Map",
            "EnableBetterMaps",
            false,
            "true = show a clearer map with inaccessible areas filtered out, corrected icon orientation and important icons drawn above minor ones; false = vanilla map.\nUses BlurScale and OutlineScale below. Does not reveal unexplored rooms or change level geometry.\nRestart the game after changing this setting.");
        MapBlurScale = config.Bind(
            "Map",
            "BlurScale",
            0.15f,
            new ConfigDescription(
                "Map blur sampling distance, used only when EnableBetterMaps is true.\nLower values reduce blur; higher values soften the map more. 0.15 is the recommended starting point. This does not change map zoom.\nRestart the game after changing this setting.",
                new AcceptableValueRange<float>(0f, 2f)));
        MapOutlineScale = config.Bind(
            "Map",
            "OutlineScale",
            0.05f,
            new ConfigDescription(
                "Map outline sampling distance, used only when EnableBetterMaps is true.\nControls how far the outline filter samples around map edges, not map zoom. 0.05 is the recommended starting point; large changes may hide small details.\nRestart the game after changing this setting.",
                new AcceptableValueRange<float>(0f, 2f)));

        HelGunVersion = config.Bind(
            "Weapon Presets",
            "HelGunVersion",
            WeaponPreset.Disabled,
            "Choose the HEL Gun combat stats and recoil preset: Disabled = leave loaded weapon stats untouched; OriginalR6 = original Rundown 6; R8Current = current Rundown 8.\nR6: 9 rounds, 0.1 s charge, ammo cost 6.5. R8: 9 rounds, 0.2 s charge, ammo cost 5.74. Ammo cost is an internal reserve-budget cost, not rounds fired per shot.\nCurrent models/animations remain. RecoilMultiplier still applies even with Disabled. Restart the game after changing this setting.");
        SniperVersion = config.Bind(
            "Weapon Presets",
            "SniperVersion",
            WeaponPreset.Disabled,
            "Choose the Sniper combat stats and recoil preset: Disabled = leave loaded weapon stats untouched; OriginalR6 = original Rundown 6; R8Current = current Rundown 8.\nR6: 3 rounds, 0.5 s shot delay, ammo cost 15. R8: 2 rounds, 0.8 s shot delay, ammo cost 17.5. Ammo cost is an internal reserve-budget cost, not rounds fired per shot.\nCurrent models/animations remain. RecoilMultiplier still applies even with Disabled. Restart the game after changing this setting.");

        BindCasual(config);
        foreach (var pair in config)
        {
            if (pair.Value is not ConfigEntry<float> entry) continue;
            void ValidateFinite()
            {
                if (float.IsFinite(entry.Value)) return;
                Plugin.PluginLog.LogWarning($"{entry.Definition.Key} must be finite; restoring its default value.");
                entry.Value = (float)entry.DefaultValue;
            }
            entry.SettingChanged += (_, _) => ValidateFinite();
            ValidateFinite();
        }
    }

    private static void BindStamina(ConfigFile config)
    {
        StaminaCostMultiplier = config.Bind(
            "Stamina",
            "StaminaCostMultiplier",
            1f,
            new ConfigDescription(
                "Scales your stamina consumption: 1 = vanilla costs, 0.5 = half costs, 0 = infinite stamina (keeps stamina full, including in combat).\nWith a value above 0, normal recovery strength and combat stamina limits remain. Affects only your player, not teammates or bots; the HUD uses the game's actual stamina/BPM.\nAt 0, the three stamina switches below provide no additional stamina benefit.",
                new AcceptableValueRange<float>(0f, 1f)));
        EnableChargeRecovery = config.Bind(
            "Stamina",
            "EnableChargeRecovery",
            false,
            "true = allow your stamina to recover while holding a charged melee attack; false = vanilla charging recovery rules.\nThis does not remove the stamina cost of the eventual swing; use RemoveMeleeCost for that. Only useful when StaminaCostMultiplier is above 0.");
        RemoveMeleeCost = config.Bind(
            "Stamina",
            "RemoveMeleeCost",
            false,
            "true = your light and charged melee swings consume no stamina; false = their costs follow StaminaCostMultiplier.\nDoes not remove melee push/shove costs or change damage or charge speed. Only useful when StaminaCostMultiplier is above 0.");
        RemoveJumpCost = config.Bind(
            "Stamina",
            "RemoveJumpCost",
            false,
            "true = jumping consumes no stamina; false = jump costs follow StaminaCostMultiplier.\nDoes not change jump height or movement speed. Only useful when StaminaCostMultiplier is above 0.");
    }
}
