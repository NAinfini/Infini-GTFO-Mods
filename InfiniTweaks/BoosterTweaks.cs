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
        __instance.OnBoosterImplantInventoryChanged += (Action)ApplyRolls;
        __instance.OnActiveBoosterImplantsChanged += (Action)ApplyRolls;
    }

    private static void ApplyRolls()
    {
        if (!Settings.PerfectBoosters.Value) return;
        var inventory = PersistentInventoryManager.Current?.m_boosterImplantInventory;
        if (inventory == null) return;
        int checkedCount = 0, changedCount = 0;
        foreach (var category in inventory.Categories)
        foreach (var entry in category.Inventory)
        {
            checkedCount++;
            if (Apply(entry.Implant)) changedCount++;
        }
        Plugin.PluginLog.LogInfo($"Booster rolls: checked={checkedCount}, changed={changedCount}, unmatchedTemplates={Unknown.Count}.");
    }

    private static bool Apply(BoosterImplant implant)
    {
        var effects = implant.Effects;
        var ids = new uint[effects.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = effects[i].Id;
        float[] values = Array.Empty<float>();
        bool matched = false;
        var template = implant.Template;
        if (template != null && template.ImplantCategory == implant.Category)
        {
            var fixedEffects = new List<BoosterRollRules.Roll>();
            foreach (var effect in template.Effects) fixedEffects.Add(new(effect.BoosterImplantEffect, effect.MaxValue));
            var slots = new List<BoosterRollRules.Roll[]>();
            foreach (var group in template.RandomEffects)
            {
                var options = new BoosterRollRules.Roll[group.Count];
                for (int i = 0; i < options.Length; i++) options[i] = new(group[i].BoosterImplantEffect, group[i].MaxValue);
                slots.Add(options);
            }
            matched = BoosterRollRules.TryMatch(ids, fixedEffects, slots, out values);
        }
        // Saved boosters outlive datablock updates. Match their exact existing effects
        // against authored historical variants, never another booster's unrelated effects.
        if (!matched)
            foreach (var historical in HistoricalBoosterTemplates.All)
                if (historical.Id == implant.TemplateId && historical.Category == (int)implant.Category &&
                    BoosterRollRules.TryMatch(ids, historical.Effects, historical.Slots, out values))
                { matched = true; break; }
        if (!matched)
        {
            if (Unknown.Add(implant.TemplateId))
                Plugin.PluginLog.LogWarning($"Booster template {implant.TemplateId}, effects=[{string.Join(",", ids)}] has no exact current/historical match; values retained.");
            return false;
        }
        bool changed = false;
        for (int i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            if (effect.Value == values[i]) continue;
            effect.Value = values[i]; effects[i] = effect; changed = true;
        }
        if (changed) implant.Effects = effects;
        return changed;
    }

    [HarmonyPatch(typeof(DropServerManager), nameof(DropServerManager.NewGameSession)), HarmonyPrefix]
    private static void Session(ref Il2CppStructArray<uint>? __3)
    {
        if (!Settings.NoBoosterConsume.Value) return;
        Plugin.PluginLog.LogInfo($"Booster consumption protection: excluding {__3?.Length ?? 0} equipped IDs from session consumption.");
        __3 = null;
    }
    [HarmonyPatch(typeof(DropServerGameSession), nameof(DropServerGameSession.ConsumeBoosters)), HarmonyPrefix]
    private static bool ConsumeSession() => AllowConsumption("session");
    [HarmonyPatch(typeof(PersistentInventoryManager), nameof(PersistentInventoryManager.ConsumeActiveBoostersLocally)), HarmonyPrefix]
    private static bool ConsumeLocal() => AllowConsumption("active inventory");
    [HarmonyPatch(typeof(PersistentInventoryManager), nameof(PersistentInventoryManager.ConsumeBooster)), HarmonyPrefix]
    private static bool ConsumeCategory() => AllowConsumption("inventory category");
    private static bool AllowConsumption(string source)
    {
        if (!Settings.NoBoosterConsume.Value) return true;
        Plugin.PluginLog.LogInfo($"Booster consumption prevented: {source}.");
        return false;
    }
    [HarmonyPatch(typeof(DropServer.BoosterImplants.BoosterUtils), nameof(DropServer.BoosterImplants.BoosterUtils.BoosterCurrencyFromHeatAndArtifactCount)), HarmonyPostfix]
    private static void Reward(DropServer.BoosterImplants.BoosterImplantCategory category, ref int __result)
    {
        if (!Settings.Farmer.Value) return;
        int original = __result;
        __result = CasualRules.Reward(original, Settings.FarmerMultiplier.Value);
        int converted = DropServer.BoosterImplants.BoosterUtils.BoosterCountFromCurrency(__result);
        Plugin.PluginLog.LogInfo($"Booster reward: category={category}, original={original}, multiplier={Settings.FarmerMultiplier.Value}, currency={__result}, nativeConversion={converted}. Conversion is not a delivery receipt; native inventory/server limits still apply.");
    }
}
