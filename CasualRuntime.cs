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
        TeamStatistics.Initialize();
        ItemMarkers.Initialize();
        ResourceHandling.Initialize();
        LevelAPI.OnBuildStart += ClearWorld;
        LevelAPI.OnEnterLevel += TeamStatistics.Reset;
    }
    private static void ClearWorld()
    {
        ItemMarkers.Clear(); ResourceHandling.Clear(); ResourceHud.Clear();
    }
    [HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.ReloadCheckpoint)), HarmonyPrefix]
    private static void Checkpoint()
    {
        ItemMarkers.Clear(); TeamStatistics.Reset();
    }
    [HarmonyPatch(typeof(GuiManager), nameof(GuiManager.OnLevelCleanup)), HarmonyPrefix]
    private static void Cleanup() => ClearWorld();
    [HarmonyPatch(typeof(GS_AfterLevel), nameof(GS_AfterLevel.Enter)), HarmonyPostfix]
    private static void End() => TeamStatistics.EndAttempt();
}

// Only the optional table and placement cue use IMGUI. World and teammate
// markers stay in the native HUD, retaining its resolution/aspect handling.
public sealed class CasualOverlay : MonoBehaviour
{
    public CasualOverlay(IntPtr pointer) : base(pointer) { }
    private GUIStyle? _text;
    private Vector2 _scroll;
    public void Update()
    {
        TeamStatistics.Tick();
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
        var player = PlayerManager.GetLocalPlayerAgent();
        if (player == null) return;
        ResourceHandling.Tick(player); ItemMarkers.Tick(player); ResourceHud.Tick(player);
    }
    public void OnGUI()
    {
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel && GameStateManager.CurrentStateName != eGameStateName.AfterLevel) return;
        if (!TeamStatistics.Open && string.IsNullOrEmpty(ResourceHandling.Prompt)) return;
        _text ??= new GUIStyle(GUI.skin.label) { richText = false, wordWrap = true };
        float scale = Mathf.Clamp(Screen.height / 1080f, 0.65f, 2f);
        _text.fontSize = Mathf.RoundToInt(18 * scale);
        if (TeamStatistics.Open && Settings.Stats.Value)
        {
            float width = Mathf.Min(720 * scale, Screen.width * 0.85f), height = Mathf.Min(500 * scale, Screen.height * 0.65f);
            var bounds = new Rect(24 * scale, 50 * scale, width, height);
            GUI.Box(bounds, "");
            var viewport = new Rect(bounds.x + 12 * scale, bounds.y + 10 * scale, width - 24 * scale, height - 20 * scale);
            float contentHeight = Mathf.Max(viewport.height, _text.CalcHeight(new GUIContent(TeamStatistics.Text), viewport.width - 24 * scale));
            _scroll = GUI.BeginScrollView(viewport, _scroll, new Rect(0, 0, viewport.width - 24 * scale, contentHeight));
            GUI.Label(new Rect(0, 0, viewport.width - 24 * scale, contentHeight), TeamStatistics.Text, _text);
            GUI.EndScrollView();
        }
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel || FocusStateManager.CurrentState != eFocusState.FPS || !Settings.Deposit.Value) return;
        GUI.Label(new Rect(Screen.width * 0.3f, Screen.height * 0.62f, Screen.width * 0.4f, 65 * scale), ResourceHandling.Prompt, _text);
        var local = PlayerManager.GetLocalPlayerAgent();
        if (local != null && ResourceHandling.PreviewPosition is { } point)
        {
            var camera = ((CameraController)local.FPSCamera).m_camera;
            var screen = camera.WorldToScreenPoint(point);
            if (screen.z > 0) GUI.Label(new Rect(screen.x - 8 * scale, Screen.height - screen.y - 12 * scale, 24 * scale, 24 * scale), "+", _text);
        }
    }
}
