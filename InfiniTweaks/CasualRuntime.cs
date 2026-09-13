using System;
using GTFO.API;
using HarmonyLib;
using Player;
using UnityEngine;

namespace InfiniTweaks;

[HarmonyPatch]
internal static class CasualRuntime
{
    internal static void Initialize()
    {
        ItemMarkers.Initialize();
        LevelAPI.OnBuildStart += ClearWorld;
        LevelAPI.OnEnterLevel += EnterLevel;
    }
    private static void EnterLevel()
    {
        ResourceHandling.RebuildWorld();
        ItemMarkers.RebuildWorld();
        ResourceHud.Clear();
    }
    private static void ClearWorld()
    {
        InteractionFixes.Clear();
        TerminalFixes.Clear();
        ItemMarkers.Clear(); ResourceHandling.Clear(); ResourceHud.ClearWorld();
    }
    [HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.ReloadCheckpoint)), HarmonyPrefix]
    private static void Checkpoint()
    {
        InteractionFixes.Clear();
        TerminalFixes.Clear();
        ItemMarkers.ClearPins(); ResourceHandling.ResetSelection(); ResourceHud.Clear();
    }
    [HarmonyPatch(typeof(GuiManager), nameof(GuiManager.OnLevelCleanup)), HarmonyPrefix]
    private static void Cleanup() => ClearWorld();
}

// Placement prompts and player markers stay in the native HUD;
// placement previews render pickup meshes. Statistics use native TMP text only.
public sealed class CasualOverlay : MonoBehaviour
{
    public CasualOverlay(IntPtr pointer) : base(pointer) { }
    public void Update()
    {
        using (Telemetry.Measure("Statistics")) CombatStatistics.Tick();
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
        using (Telemetry.Measure("TerminalMaintenance")) TerminalFixes.Tick();
        var player = PlayerManager.GetLocalPlayerAgent();
        if (player == null) return;
        using (Telemetry.Measure("ResourceHandling")) ResourceHandling.Tick(player);
        using (Telemetry.Measure("ItemMarkers")) ItemMarkers.Tick(player);
        using (Telemetry.Measure("ResourceHud")) ResourceHud.Tick(player);
    }
    public void LateUpdate()
    {
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
        using (Telemetry.Measure("PlacementPresentation")) ResourceHandling.DrawPresentation();
        var player = PlayerManager.GetLocalPlayerAgent();
        if (player != null)
            using (Telemetry.Measure("ResourceHudPresentation")) ResourceHud.UpdateOpacity(player);
    }
}
