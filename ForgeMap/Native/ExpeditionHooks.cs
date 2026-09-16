using HarmonyLib;
using SNetwork;

namespace ForgeMap.Native;

// The native readback point for the end of an expedition. The game itself reports the end through this one call
// and names which of its three ends it was (`ExpeditionEndState`), so the hook is a postfix that only chooses
// when to publish: the argument names the outcome, and nothing is inferred from a game state, a player count or
// a level teardown order.
//
// The entry point is frozen in `evidence/map-hooks.json` (`RundownManager.OnExpeditionEnded`, build 20403457,
// RVA 0x13E2100). The class is listed in `MapNativeHooks.Types`, which is the list the plugin installs.
//
// Only the host publishes: the outcome is one fact about one expedition, and a client's own copy of it would be
// a second publisher of a session fact the kernel answers per host.

[HarmonyPatch(typeof(RundownManager), nameof(RundownManager.OnExpeditionEnded))]
internal static class ExpeditionEndedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ExpeditionEndState endState)
    {
        if (!SNet.IsMaster) return;
        Plugin.Session?.GuardMapObjects(module => { module.Expeditions.Ended((int)endState); });
    }
}
