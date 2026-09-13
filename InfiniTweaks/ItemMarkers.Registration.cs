using System;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace InfiniTweaks;

// Native setup/teardown events share the existing marker registry and discovery flow.
internal static partial class ItemMarkers
{
    // All discovery routes use the registered owner, including terminal screens
    // whose collider and terminal-item component are siblings in the prefab.
    private static LG_GenericTerminalItem? ResolveTerminal(Component target)
    {
        for (var current = target.transform; current != null; current = current.parent)
            if (DiscoveryOwners.TryGetValue(current.gameObject.GetInstanceID(), out var terminal) && terminal != null)
                return terminal;
        return null;
    }
    private static void DeviceFor(LG_GenericTerminalItem? terminal, Component owner, MarkerCategory category, Func<bool> available, Func<string>? detail = null, Component? anchor = null)
    {
        if (terminal == null) return;
        RegisterTerminal(terminal);
        if (Devices.TryGetValue(terminal.GetInstanceID(), out var previous) && previous.Owner != null && previous.Owner != owner)
            RemoveDiscoveryOwner(previous.Owner, terminal);
        if (previous != null && previous.Owner == owner && previous.Anchor == (anchor ?? owner) && previous.Category == category) return;
        Devices[terminal.GetInstanceID()] = new(owner, anchor ?? owner, category, available, detail);
        DiscoveryOwners[owner.gameObject.GetInstanceID()] = terminal;
        // Setup can arrive after QUERY or generic terminal Setup. Rebind that pin
        // immediately instead of leaving it as a generic world object until reload.
        if (Pins.TryGetValue(terminal.GetInstanceID(), out var pin))
        { bool explicitDiscovery = pin.Explicit; Forget(pin.Id); RememberTerminalObject(terminal, explicitDiscovery); }
    }

    [HarmonyPatch(typeof(LG_GenericTerminalItem), nameof(LG_GenericTerminalItem.Setup)), HarmonyPostfix]
    private static void RegisterTerminal(LG_GenericTerminalItem __instance)
    {
        int id = __instance.GetInstanceID();
        if (TerminalIds.TryGetValue(id, out uint previous) && previous != __instance.TerminalItemId &&
            Terminals.TryGetValue(previous, out var old) && old == __instance) Terminals.Remove(previous);
        TerminalIds[id] = __instance.TerminalItemId;
        if (__instance.TerminalItemId != 0) Terminals[__instance.TerminalItemId] = __instance;
        // Some custom-geometry devices omit the native node. Use their actual
        // containing area, never the local player's area or a nearest-node guess.
        if (__instance.SpawnNode == null)
        {
            var node = __instance.GetComponentInParent<LG_Area>()?.m_courseNode;
            // The IL2CPP setter dereferences its argument; null is not a no-op.
            if (node != null) __instance.SpawnNode = node;
        }
        DiscoveryOwners[__instance.gameObject.GetInstanceID()] = __instance;
        if (Pins.TryGetValue(id, out var pin)) Label(pin);
    }

    private static void RemoveDiscoveryOwner(Component owner, LG_GenericTerminalItem terminal)
    {
        int id = owner.gameObject.GetInstanceID();
        if (DiscoveryOwners.TryGetValue(id, out var registered) && registered == terminal) DiscoveryOwners.Remove(id);
    }

    [HarmonyPatch(typeof(LG_GenericTerminalItem), nameof(LG_GenericTerminalItem.OnDestroy)), HarmonyPrefix]
    private static void RemoveTerminal(LG_GenericTerminalItem __instance)
    {
        if (TerminalIds.Remove(__instance.GetInstanceID(), out uint id) && Terminals.TryGetValue(id, out var registered) && registered == __instance)
            Terminals.Remove(id);
        RemoveDiscoveryOwner(__instance, __instance);
        if (Devices.TryGetValue(__instance.GetInstanceID(), out var device) && device.Owner != null)
            RemoveDiscoveryOwner(device.Owner, __instance);
        Devices.Remove(__instance.GetInstanceID());
        Forget(__instance.GetInstanceID());
    }


    [HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.Setup)), HarmonyPostfix]
    private static void RegisterComputer(LG_ComputerTerminal __instance)
    {
        // Match ItemMarker's actual terminal identity. m_terminalItemComp is not
        // the authoritative terminal interface used by command/ping registration.
        var terminal = __instance.m_terminalItem?.TryCast<LG_GenericTerminalItem>();
        if (terminal == null) return;
        // The native interaction camera is in front of the screen. Testing line
        // of sight to the cabinet's floor-level origin can hit the terminal itself.
        Component anchor = __instance.m_CameraAlign != null ? __instance.m_CameraAlign : __instance;
        DeviceFor(terminal, __instance, MarkerCategory.Terminal,
            () => __instance != null, anchor: anchor);
        foreach (var collider in __instance.GetComponentsInChildren<Collider>(true))
            if (collider.GetComponent<PlayerPingTarget>() == null)
                collider.gameObject.AddComponent<PlayerPingTarget>().m_pingTargetStyle = eNavMarkerStyle.PlayerPingTerminal;
    }
    [HarmonyPatch(typeof(LG_WardenObjective_Reactor), nameof(LG_WardenObjective_Reactor.OnBuildDone)), HarmonyPostfix]
    private static void RegisterReactor(LG_WardenObjective_Reactor __instance)
    {
        var computer = __instance.m_terminal;
        if (computer == null) return;
        var node = __instance.SpawnNode;
        var terminal = computer.m_terminalItem?.TryCast<LG_GenericTerminalItem>();
        if (terminal != null && terminal.SpawnNode == null && node != null) terminal.SpawnNode = node;
        var zoneTerminals = node?.m_zone?.TerminalsSpawnedInZone;
        if (zoneTerminals != null && !zoneTerminals.Contains(computer)) zoneTerminals.Add(computer);
        // Registration is not discovery: no marker is revealed until an existing
        // proximity, interaction, QUERY or ping path discovers this terminal.
        RegisterComputer(computer);
    }
}
