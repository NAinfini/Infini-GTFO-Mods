using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ForgeRuntime;

internal static class CombatSampling
{
    internal static bool Active;
    internal static void Toggle()
    {
        Active = !Active;
        RuntimeDiagnostics.Report?.Event("combat_test", Active ? "begin" : "end", "local_capture", new() { ["authority"] = SNetwork.SNet.IsMaster ? "host" : "client_observation" });
        Plugin.PluginLog.LogInfo("Forge damage sampling " + (Active ? "started" : "stopped") + ". F10 exports the report.");
    }
}

[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ProcessReceivedDamage))]
internal static class DamageSample
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Dam_EnemyDamageBase __instance, out float? __state)
    {
        float? health = null;
        if (CombatSampling.Active) RuntimeDiagnostics.Safe(() => health = __instance.Health);
        __state = health;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_EnemyDamageBase __instance, float __0, int __6, uint __9, float? __state)
    {
        if (!__state.HasValue) return;
        RuntimeDiagnostics.Safe(() =>
        {
            var after = __instance.Health;
            RuntimeDiagnostics.Report?.Event("damage_sample", "received_damage", __instance.Owner == null ? "unknown_enemy" : __instance.Owner.GetInstanceID().ToString(), new()
            {
                ["enemyDataId"] = __instance.Owner?.EnemyDataID.ToString() ?? "unavailable",
                ["gearCategoryId"] = __9.ToString(), ["limbIndex"] = __6.ToString(),
                ["inputDamage"] = RuntimeDiagnostics.Number(__0), ["healthBefore"] = RuntimeDiagnostics.Number(__state.Value),
                ["healthAfter"] = RuntimeDiagnostics.Number(after),
                ["effectiveDamage"] = RuntimeDiagnostics.Number(Math.Max(0, Math.Max(0, __state.Value) - Math.Max(0, after))),
                ["authority"] = SNetwork.SNet.IsMaster ? "host" : "client_observation", ["frame"] = Time.frameCount.ToString()
            });
        });
    }
}

[HarmonyPatch(typeof(Gear.BulletWeapon), nameof(Gear.BulletWeapon.OnWield))]
internal static class WeaponSample
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Gear.BulletWeapon __instance)
    {
        if (!CombatSampling.Active) return;
        RuntimeDiagnostics.Safe(() =>
        {
            var data = __instance.m_archeType?.m_archetypeData;
            if (data == null) return;
            RuntimeDiagnostics.Report?.Event("effective_weapon", "wield", data.persistentID.ToString(), new()
            {
                ["name"] = __instance.ArchetypeName, ["damage"] = RuntimeDiagnostics.Number(data.Damage),
                ["clipSize"] = __instance.ClipSize.ToString(), ["reloadSeconds"] = RuntimeDiagnostics.Number(__instance.ReloadTime),
                ["scope"] = "Observed native effective values. Per-shot third-party behavior is represented by separate damage samples, not inferred from these fields."
            });
        });
    }
}
