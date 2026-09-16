using System;
using HarmonyLib;
using LevelGeneration;
using SNetwork;

namespace ForgeMap.Native;

// The native readback points for map objects. Each hook is a postfix on the method the game itself runs when
// the object's state changed, and each one only chooses when to read: the callback's own arguments are never
// turned into fact values, because the instance is re-read afterwards and reports the state it is really in.
//
// The three entry points are frozen in `evidence/door-terminal-hooks.json`: `LG_SecurityDoor_Locks.OnDoorState`
// is the door's own lock/state callback and the door scan's transition point as well, `LG_ComputerTerminal.OnStateChange`
// is the terminal's own state replication callback, and `LG_ComputerTerminalManager.WantToSendTerminalCommand` is
// the terminal's command entry — the one place the accepted command is named before the terminal acts on it.
// The generator category's two readbacks are declared with the generator reader in `GeneratorSource.cs`. Each
// class below is listed in `MapNativeHooks.Types`, which is the list the plugin installs.

/// <summary>The scan stage one door status is, or null for a status that is not a scan transition. The lock
/// component announces a chained-puzzle activation and its solution through two `Action` members that this
/// build's interop exposes as delegate properties with no method behind either, so no Harmony patch can reach
/// them; the same component's real `OnDoorState` callback carries the transitions instead.
/// `ChainedPuzzleActivated` is written while the scan runs and `Unlocked` when the door's own lock released,
/// which for a locked-with-chained-puzzle door is the solution. Every other status is a state the door holds,
/// which is why this answers null rather than the nearest stage.</summary>
internal static class DoorScanStatus
{
    internal static DoorScanStage? Stage(int status) => status switch
    {
        8 => DoorScanStage.Activated,
        9 => DoorScanStage.Solved,
        _ => null
    };
}

[HarmonyPatch(typeof(LG_SecurityDoor_Locks), nameof(LG_SecurityDoor_Locks.OnDoorState))]
internal static class DoorStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_SecurityDoor_Locks __instance, pDoorState state)
    {
        if (!SNet.IsMaster) return;
        var door = DoorObservation.Door(__instance);
        if (door == null) return;
        Plugin.Session?.GuardMapObjects(module => module.DoorStateChanged(door));
        Plugin.Session?.GuardMapObjects(module => module.DoorLockChanged(door));
        // The scan transitions ride the same callback: the door's own status is what the lock component wrote,
        // and a status that is not one of the two scan transitions publishes no scan fact at all.
        if (DoorScanStatus.Stage((int)state.status) is not { } stage) return;
        DoorTerminalFacts.Current?.Guard(facts => facts.ScanTransition(__instance, stage));
    }
}

[HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.OnStateChange))]
internal static class TerminalStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_ComputerTerminal __instance)
    {
        if (!SNet.IsMaster) return;
        Plugin.Session?.GuardMapObjects(module => module.TerminalStateChanged(__instance, Actor(__instance)));
    }

    /// <summary>The player at the terminal, read from the terminal's own interaction source. The state
    /// callback does not carry the acting player, so a terminal with no current interaction reports the actor
    /// port absent instead of the last player that touched it.</summary>
    private static object? Actor(LG_ComputerTerminal terminal)
        => terminal.m_hasInteractingPlayer ? terminal.m_syncedInteractionSource : null;
}
