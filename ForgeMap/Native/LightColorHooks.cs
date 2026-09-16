using HarmonyLib;
using Player;

namespace ForgeMap.Native;

/// <summary>
/// The one Harmony patch of the light-colour row: after the local player's own fixed update, one frame of every
/// light transition in flight.
///
/// The frame is the game's own — the same call the trigger-zone tick hangs off — so a transition advances with the
/// tick the rest of the level advances on instead of on a clock this package keeps. Only the local player's copy
/// of the call drives it: every other player's copy on this machine is another body's, and advancing the same
/// transitions once per player would run them several times in one frame.
///
/// The world comes from the session. A frame with no session drops the table, and one whose world is not the
/// table's is the level teardown between them — `LightColorFades` owns both rules, so a light object the level
/// already took is never written at.
/// </summary>
[HarmonyPatch(typeof(PlayerInteraction), nameof(PlayerInteraction.FixedUpdate))]
internal static class LightColorTick
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInteraction __instance)
    {
        var owner = __instance.m_owner;
        if (owner == null || !owner.IsLocallyOwned) return;
        if (Plugin.Session is not { } session) { LightColorFades.Clear(); return; }
        LightColorFades.Tick(session.WorldEpoch, UnityEngine.Time.deltaTime);
    }
}
