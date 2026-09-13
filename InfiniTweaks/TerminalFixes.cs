using System.Collections.Generic;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;
using static LevelGeneration.LG_ComputerTerminalManager;

namespace InfiniTweaks;

// Uses the native state machine and command validation. In particular, queued
// commands re-enter native validation instead of directly sending a network packet.
[HarmonyPatch]
internal static class TerminalFixes
{
    private sealed record Watch(LG_ComputerTerminal Terminal, float GraceUntil);
    private static readonly Dictionary<uint, Watch> Active = new();
    private static readonly Dictionary<uint, float> InputReady = new();
    private static readonly Dictionary<uint, Queue<pTerminalCommand>> Commands = new();
    private static readonly List<KeyValuePair<uint, Watch>> Remove = new();
    private static readonly List<KeyValuePair<uint, Watch>> Watches = new();
    private static readonly HashSet<uint> Dispatching = new();
    private static readonly HashSet<uint> ReportedFull = new();
    private static float _nextWatch;

    internal static void Clear()
    { Active.Clear(); InputReady.Clear(); Commands.Clear(); Remove.Clear(); Watches.Clear(); Dispatching.Clear(); ReportedFull.Clear(); _nextWatch = 0; }

    [HarmonyPatch(typeof(LG_ComputerTerminalManager), nameof(LG_ComputerTerminalManager.DoChangeTerminalStateValidation)), HarmonyPrefix]
    private static bool ValidateState(LG_ComputerTerminalManager __instance, pTerminalState data)
    {
        if (!__instance.m_terminals.ContainsKey(data.ID)) return false;
        var terminal = __instance.m_terminals[data.ID];
        // Proximity exit must not put an occupied terminal to sleep.
        return terminal.CurrentStateName != TERM_State.PlayerInteracting || (TERM_State)data.state != TERM_State.Sleeping;
    }
    [HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.SyncChangeState)), HarmonyPostfix]
    private static void StateChanged(LG_ComputerTerminal __instance)
    {
        if (__instance.CurrentStateName == TERM_State.PlayerInteracting)
            Active[__instance.SyncID] = new(__instance, Clock.Time + 0.5f);
    }
    [HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.EnterFPSView)), HarmonyPrefix]
    private static void EnterView(LG_ComputerTerminal __instance)
    {
        if (__instance.m_localInteractionSource != null) InputReady[__instance.SyncID] = Clock.Time + 0.5f;
    }
    [HarmonyPatch(typeof(LG_TERM_PlayerInteracting), nameof(LG_TERM_PlayerInteracting.Enter)), HarmonyPostfix]
    private static void BeginInput(LG_TERM_PlayerInteracting __instance)
    {
        if (__instance.m_terminal != null && InputReady.TryGetValue(__instance.m_terminal.SyncID, out float ready))
            __instance.m_inputTimer = ready;
    }
    [HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.ExitFPSView)), HarmonyPrefix]
    private static void EndInput(LG_ComputerTerminal __instance)
    {
        if (__instance.m_localInteractionSource == null) return;
        var input = __instance.GetState((int)TERM_State.PlayerInteracting).TryCast<LG_TERM_PlayerInteracting>();
        if (input == null) return;
        input.m_inputTimer = Clock.Time + 0.5f;
        if (input.m_lastSyncString == __instance.m_currentLine) return;
        WantToSendTerminalString(__instance.SyncID, __instance.m_currentLine);
        input.m_lastSyncString = __instance.m_currentLine;
    }
    [HarmonyPatch(typeof(LG_ComputerTerminalManager), nameof(LG_ComputerTerminalManager.DoTerminalCommandValidation)), HarmonyPrefix]
    private static bool ValidateCommand(LG_ComputerTerminalManager __instance, pTerminalCommand data)
    {
        if (!SNet.IsMaster) return true;
        if (!__instance.m_terminals.ContainsKey(data.ID)) return false;
        var terminal = __instance.m_terminals[data.ID];
        bool queued = Commands.TryGetValue(data.ID, out var queue) && queue.Count > 0;
        if (terminal.m_command.OnEndOfQueue == null && (Dispatching.Contains(data.ID) || !queued)) return true;
        if (queue == null) Commands[data.ID] = queue = new();
        if (queue.Count < 32) queue.Enqueue(data);
        else if (ReportedFull.Add(data.ID)) Plugin.PluginLog.LogWarning($"Terminal {data.ID} command queue is full; additional command ignored.");
        return false;
    }
    [HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.UpdateTerminalScreen)), HarmonyPostfix]
    private static void DispatchCommand(LG_ComputerTerminalCommandInterpreter __instance)
    {
        if (!SNet.IsMaster || __instance.OnEndOfQueue != null || __instance.m_terminal == null) return;
        uint id = __instance.m_terminal.SyncID;
        if (!Commands.TryGetValue(id, out var queue) || queue.Count == 0) return;
        var command = queue.Dequeue();
        if (queue.Count == 0) Commands.Remove(id);
        ReportedFull.Remove(id);
        Dispatching.Add(id);
        try { Current.DoTerminalCommandValidation(command); }
        finally { Dispatching.Remove(id); }
    }
    [HarmonyPatch(typeof(LG_TERM_Ping), nameof(LG_TERM_Ping.Ping)), HarmonyPostfix]
    private static void PingFinished(LG_TERM_Ping __instance)
    {
        var terminal = __instance.m_terminal;
        if (terminal != null && terminal.m_localInteractionSource == null && terminal.m_syncedInteractionSource == null)
            terminal.ChangeState(TERM_State.Awake);
    }
    internal static void Tick()
    {
        if (Clock.Time < _nextWatch) return;
        _nextWatch = Clock.Time + 0.1f;
        // Native state changes can synchronously call our registration hook.
        // Iterate a reused snapshot, not the dictionary those hooks mutate.
        Watches.Clear(); Watches.AddRange(Active);
        foreach (var pair in Watches)
        {
            var terminal = pair.Value.Terminal;
            if (terminal == null) { Remove.Add(pair); continue; }
            if (terminal.CurrentStateName != TERM_State.PlayerInteracting)
            {
                if (Clock.Time <= pair.Value.GraceUntil && terminal.CurrentStateName == TERM_State.Sleeping && terminal.m_localInteractionSource != null)
                    terminal.ChangeState(TERM_State.PlayerInteracting);
                Remove.Add(pair);
                continue;
            }
            if (Clock.Time < pair.Value.GraceUntil) continue;
            var player = terminal.m_localInteractionSource ?? terminal.m_syncedInteractionSource;
            if (player != null && (player.Locomotion.m_currentStateEnum == PlayerLocomotion.PLOC_State.OnTerminal ||
                (!player.IsLocallyOwned && (player.transform.position - player.Sync.m_locomotionData.Pos).sqrMagnitude <= 0.0001f))) continue;
            terminal.m_localInteractionSource = null;
            terminal.m_syncedInteractionSource = null;
            terminal.ChangeState(TERM_State.Awake);
            Remove.Add(pair);
        }
        foreach (var pair in Remove)
            if (Active.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, pair.Value))
            { Active.Remove(pair.Key); InputReady.Remove(pair.Key); }
        Remove.Clear();
    }
}
