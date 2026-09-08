using System;
using System.Collections.Generic;
using GameData;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace InfiniTweaks;

[HarmonyPatch]
internal static class BoosterTweaks
{
    private static readonly HashSet<uint> Unknown = new();

    [HarmonyPatch(typeof(PersistentInventoryManager), nameof(PersistentInventoryManager.Awake)), HarmonyPostfix]
    private static void ObserveInventory(PersistentInventoryManager __instance)
    {
        // The native inventory event avoids detouring the SDK's by-ref data
        // structure, and runs whenever the game replaces its inventory model.
        __instance.OnBoosterImplantInventoryChanged += (Action)ApplyRolls;
    }

    private static void ApplyRolls()
    {
        if (!Settings.PerfectBoosters.Value) return;
        var inventory = PersistentInventoryManager.Current?.m_boosterImplantInventory;
        if (inventory == null) return;
        foreach (var category in inventory.Categories)
        foreach (var entry in category.Inventory)
        {
            var implant = entry.Implant;
            var template = implant.Template;
            if (template == null || template.ImplantCategory != implant.Category) { Warn(implant.TemplateId); continue; }
            var effects = implant.Effects;
            var ids = new uint[effects.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = effects[i].Id;
            var fixedEffects = new List<BoosterRollRules.Roll>();
            foreach (var effect in template.Effects) fixedEffects.Add(new(effect.BoosterImplantEffect, effect.MaxValue));
            var slots = new List<BoosterRollRules.Roll[]>();
            foreach (var group in template.RandomEffects)
            {
                var options = new BoosterRollRules.Roll[group.Count];
                for (int i = 0; i < options.Length; i++) options[i] = new(group[i].BoosterImplantEffect, group[i].MaxValue);
                slots.Add(options);
            }
            if (!BoosterRollRules.TryMatch(ids, fixedEffects, slots, out var values)) { Warn(implant.TemplateId); continue; }
            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                // MaxValue is the template's authored best roll, not necessarily
                // the numerically larger end of a reduction effect's range.
                effect.Value = values[i];
                effects[i] = effect;
            }
            implant.Effects = effects;
        }
    }
    private static void Warn(uint id)
    {
        if (Unknown.Add(id)) Plugin.PluginLog.LogWarning($"Booster template {id} could not be matched exactly; retaining its original effects and conditions.");
    }
    [HarmonyPatch(typeof(DropServerManager), nameof(DropServerManager.NewGameSession)), HarmonyPrefix]
    private static void Session(ref Il2CppStructArray<uint> __3)
    {
        if (Settings.NoBoosterConsume.Value) __3 = new Il2CppStructArray<uint>(0);
    }
    [HarmonyPatch(typeof(DropServerGameSession), nameof(DropServerGameSession.ConsumeBoosters)), HarmonyPrefix]
    private static bool ConsumeSession() => !Settings.NoBoosterConsume.Value;
    [HarmonyPatch(typeof(PersistentInventoryManager), nameof(PersistentInventoryManager.ConsumeActiveBoostersLocally)), HarmonyPrefix]
    private static bool ConsumeLocal() => !Settings.NoBoosterConsume.Value;
    [HarmonyPatch(typeof(PersistentInventoryManager), nameof(PersistentInventoryManager.ConsumeBooster)), HarmonyPrefix]
    private static bool ConsumeCategory() => !Settings.NoBoosterConsume.Value;
    [HarmonyPatch(typeof(DropServer.BoosterImplants.BoosterUtils), nameof(DropServer.BoosterImplants.BoosterUtils.BoosterCurrencyFromHeatAndArtifactCount)), HarmonyPostfix]
    private static void Reward(ref int __result)
    {
        if (Settings.Farmer.Value) __result = CasualRules.Reward(__result, Settings.FarmerMultiplier.Value);
    }
}
