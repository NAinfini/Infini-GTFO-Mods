using System;
using System.Collections.Generic;
using System.Text;
using CellMenu;
using HarmonyLib;
using Player;
using SNetwork;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace InfiniTweaks;

internal static class CombatStatsView
{
    private static readonly List<TextMeshPro> Hud = new();
    private static readonly List<RectTransform> HudRows = new();
    private static readonly List<RectTransform> HudAnchors = new();
    private sealed record Report(CM_PageSuccess_PrisonerEvaluation Target, SNet_Player Player, string Gear, string Evaluation);
    private static readonly List<Report> Reports = new();
    private sealed record FailureReport(TextMeshPro Text, SNet_Player Player);
    private static readonly List<FailureReport> FailureReports = new();
    private static readonly InventorySlot[] GearSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearClass, InventorySlot.GearMelee };
    private static bool Include(SNet_Player player) => CombatStatistics.Team.Value || player.IsLocal;
    private static void Set(TextMeshPro text, string value) { if (text.text != value) text.SetText(value); }
    private static void Active(TextMeshPro text, bool visible) { if (text.gameObject.activeSelf != visible) text.gameObject.SetActive(visible); }
    private static TextMeshPro Clone(TextMeshPro source, Transform parent)
    {
        var text = Object.Instantiate(source, parent);
        text.name = "InfiniTweaks.CombatStats"; text.richText = true;
        text.enableAutoSizing = false; text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow; text.alignment = TextAlignmentOptions.TopLeft;
        text.color = Color.white; text.enabled = true; Set(text, ""); return text;
    }
    private static string PlainName(string value) => value.Replace("<", "").Replace(">", "").Replace('\r', ' ').Replace('\n', ' ');
    private static string Name(SNet_Player player, bool compact)
    {
        string name = compact && !CombatStatistics.FullNames.Value ? player.CharacterIndex switch
        { 0 => "RED", 1 => "GRE", 2 => "BLU", 3 => "PUR", _ => "P" + (player.CharacterIndex + 1) } : PlainName(player.NickName);
        var colors = PlayerManager.Current?.m_playerColors;
        string color = colors != null && player.CharacterIndex >= 0 && player.CharacterIndex < colors.Length ? ColorUtility.ToHtmlStringRGB(colors[player.CharacterIndex]) : "FFFFFF";
        return "<#" + color + ">" + name + "</color>";
    }
    private static string Values(ulong player, StatSlot slot, bool compact = false) => CombatStatsFormat.Render(
        compact ? CombatStatistics.HudFormat.Value : CombatStatistics.ResultFormat.Value, CombatStatistics.Data, player, slot);
    internal static void Visibility()
    {
        bool visible = CombatStatistics.Enabled.Value && CombatStatistics.HasAttempt && GameStateManager.CurrentStateName == eGameStateName.InLevel && FocusStateManager.CurrentState == eFocusState.FPS;
        for (int i = 0; i < Hud.Count; i++)
            if (Hud[i] != null && HudRows[i] != null)
            {
                bool show = visible && Hud[i].text.Length != 0;
                Active(Hud[i], show);
                if (HudRows[i].gameObject.activeSelf != show) HudRows[i].gameObject.SetActive(show);
            }
        foreach (var report in FailureReports)
            if (report.Text != null) Active(report.Text, CombatStatistics.Enabled.Value && CombatStatistics.EndScreen.Value && GameStateManager.CurrentStateName == eGameStateName.ExpeditionFail);
    }
    private static bool Ensure(int index)
    {
        if (index < Hud.Count && Hud[index] != null && HudRows[index] != null && HudAnchors[index] != null) return true;
        var inventory = GuiManager.Current?.m_playerLayer?.Inventory; if (inventory == null) return false;
        RectTransform? anchor = null;
        foreach (var rect in inventory.m_iconDisplay.GetComponentsInChildren<RectTransform>(true))
            if (rect.name == "Background Fade") { anchor = rect; break; }
        if (anchor == null) return false;
        var source = inventory.m_inventorySlots[InventorySlot.GearMelee].m_slim_archetypeName;
        // Keep the native inventory row's RectTransform ancestry, not just its
        // text. The same offset in the row's parent is a different coordinate space.
        var row = Object.Instantiate(anchor.gameObject, anchor.parent).GetComponent<RectTransform>();
        row.name = "InfiniTweaks.CombatStatsRow";
        row.pivot = new Vector2(1, row.pivot.y);
        foreach (var child in row.GetComponentsInChildren<Transform>(true))
            if (child.name == "TimerShowObject") child.gameObject.SetActive(false);
        var content = new GameObject("InfiniTweaks.CombatStatsContent") { layer = 5 };
        content.transform.SetParent(row, false);
        var text = Clone(source, content.transform);
        // A wider centered text box moves its right edge off-screen. Keep both
        // pivots at the native row's right edge so values grow towards the left.
        text.alignment = TextAlignmentOptions.TopRight;
        text.rectTransform.pivot = new Vector2(1, text.rectTransform.pivot.y);
        text.m_width = source.m_width * 2;
        text.rectTransform.anchoredPosition = new Vector2(-5, 9);
        row.gameObject.SetActive(true);
        if (index < Hud.Count)
        {
            if (HudRows[index] != null) Object.Destroy(HudRows[index].gameObject);
            Hud[index] = text; HudRows[index] = row; HudAnchors[index] = anchor;
        }
        else { Hud.Add(text); HudRows.Add(row); HudAnchors.Add(anchor); }
        return true;
    }
    internal static void Refresh()
    {
        if (SNet.Slots?.PlayerSlots == null) return;
        int index = 0;
        foreach (var slot in SNet.Slots.PlayerSlots)
        {
            var player = slot?.player;
            if (player == null || player.CharacterIndex < 0 || !Include(player)) continue;
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel || !CombatStatistics.Enabled.Value) break;
            if (!Ensure(index)) break;
            var text = Hud[index];
            var anchor = HudAnchors[index];
            HudRows[index].localScale = Vector3.one * CombatStatistics.Scale.Value;
            // Read the native boundary again after resolution/HUD layout changes.
            // Keep the configured offset, but never let it push the row past it.
            float right = anchor.localPosition.x + anchor.rect.xMax * anchor.localScale.x;
            HudRows[index].localPosition = new Vector3(right + Math.Min(0, -70 + CombatStatistics.OffsetX.Value), -62 + CombatStatistics.OffsetY.Value - index * 35 * CombatStatistics.Scale.Value, 0);
            Set(text, Name(player, true) + ": " + Values(player.Lookup, StatSlot.All, true)); index++;
        }
        for (int i = index; i < Hud.Count; i++) if (Hud[i] != null) Set(Hud[i], "");
        foreach (var report in Reports)
        {
            if (report.Target == null) continue;
            if (!CombatStatistics.Enabled.Value || !CombatStatistics.EndScreen.Value)
            { Set(report.Target.m_gear, report.Gear); Set(report.Target.m_eval, report.Evaluation); continue; }
            Set(report.Target.m_gear, GearLines(report.Player));
            Set(report.Target.m_eval, report.Evaluation + "\n<size=80%>Total: " + Values(report.Player.Lookup, StatSlot.All) + "</size>");
        }
        foreach (var report in FailureReports)
        {
            if (report.Text == null) continue;
            Set(report.Text, Name(report.Player, false) + "\n" + Values(report.Player.Lookup, StatSlot.All) +
                "\n\n<size=80%>" + GearLines(report.Player, true) + "</size>");
        }
        Visibility();
    }
    private static string GearLines(SNet_Player player, bool stacked = false)
    {
        var output = new StringBuilder();
        var page = MainMenuGuiLayer.Current?.PageExpeditionSuccess;
        if (page == null || !PlayerBackpackManager.TryGetBackpack(player, out var backpack)) return "";
        foreach (var slot in GearSlots)
        {
            if (!page.TryGetArchetypeName(backpack, slot, out string name)) continue;
            var kind = CombatStatistics.Slot(slot);
            string value = kind is StatSlot.Main or StatSlot.Special ? Values(player.Lookup, kind) :
                CombatStatsFormat.Render("{Damage} (<#FFFF00>{DamageCrit}</color>)", CombatStatistics.Data, player.Lookup, kind);
            output.Append(PlainName(name)).Append(stacked ? "\n" : ": ").AppendLine(value);
        }
        return output.ToString().TrimEnd();
    }
    internal static void Clear()
    {
        foreach (var report in Reports)
            if (report.Target != null) { Set(report.Target.m_gear, report.Gear); Set(report.Target.m_eval, report.Evaluation); }
        Reports.Clear();
        foreach (var report in FailureReports) if (report.Text != null) Object.Destroy(report.Text.gameObject);
        FailureReports.Clear();
        for (int i = 0; i < Hud.Count; i++)
        {
            if (Hud[i] != null) { Set(Hud[i], ""); Active(Hud[i], false); }
            if (HudRows[i] != null) HudRows[i].gameObject.SetActive(false);
        }
    }
    [HarmonyPatch(typeof(CM_PageExpeditionSuccess), nameof(CM_PageExpeditionSuccess.OnEnable))]
    private static class Success
    {
        [HarmonyPrefix] private static void Before() => Clear();
        [HarmonyPostfix]
        private static void After(CM_PageExpeditionSuccess __instance)
        {
            CombatStatistics.Finish();
            if (!CombatStatistics.Enabled.Value || !CombatStatistics.EndScreen.Value || !CombatStatistics.HasAttempt || SNet.Slots?.PlayerSlots == null || __instance.m_playerReports == null) return;
            for (int i = 0; i < Math.Min(__instance.m_playerReports.Length, SNet.Slots.PlayerSlots.Length); i++)
            {
                var player = SNet.Slots.PlayerSlots[i]?.player; var report = __instance.m_playerReports[i];
                if (player != null && Include(player) && report != null) Reports.Add(new(report, player, report.m_gear.text, report.m_eval.text));
            }
            Refresh();
        }
    }
    [HarmonyPatch(typeof(CM_PageExpeditionFail), nameof(CM_PageExpeditionFail.OnEnable))]
    private static class Fail
    {
        [HarmonyPrefix] private static void Before() => Clear();
        [HarmonyPostfix]
        private static void After(CM_PageExpeditionFail __instance)
        {
            CombatStatistics.Finish();
            if (!CombatStatistics.Enabled.Value || !CombatStatistics.EndScreen.Value || !CombatStatistics.HasAttempt || SNet.Slots?.PlayerSlots == null) return;
            var prefab = MainMenuGuiLayer.Current?.PageExpeditionSuccess?.m_playerReportPrefab;
            var source = prefab?.GetComponent<CM_PageSuccess_PrisonerEvaluation>()?.m_gear;
            if (source == null || __instance.m_staticContentHolder == null) return;
            // Use the game's report font and menu coordinate system. Four short columns,
            // not a long vertical log; native artifact information is left untouched.
            for (int i = 0; i < SNet.Slots.PlayerSlots.Length; i++)
            {
                var player = SNet.Slots.PlayerSlots[i]?.player;
                if (player == null || !Include(player)) continue;
                var text = Clone(source, __instance.m_staticContentHolder.transform);
                text.transform.localPosition = new Vector3(-900 + 600 * i, -250, 0);
                text.transform.localRotation = Quaternion.identity;
                text.transform.localScale = Vector3.one * 0.8f;
                text.rectTransform.sizeDelta = new Vector2(650, 500);
                FailureReports.Add(new(text, player));
            }
            Refresh();
        }
    }
}
