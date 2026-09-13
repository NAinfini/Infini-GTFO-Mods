using System;
using System.Collections.Generic;
using HarmonyLib;

namespace InfiniTweaks;

// Own the objective's native placer through replacement visibility transitions.
// Game-owned markers are not destroyed; player/enemy/rescue markers are untouched.
[HarmonyPatch]
internal static class MarkerOwnership
{
    private static readonly Dictionary<IntPtr, bool> Requests = new();

    internal static void Rebind(ref NavMarker? previous, NavMarker? current, bool suppress)
    {
        if (previous != current)
        {
            if (previous != null) Release(previous);
            previous = current;
        }
        if (current == null) return;
        if (suppress) Suppress(current);
        else Release(current);
    }

    internal static void Suppress(NavMarker marker)
    {
        if (Requests.ContainsKey(marker.Pointer)) return;
        bool requested = marker.IsVisible;
        marker.SetVisible(false);
        Requests.Add(marker.Pointer, requested);
    }

    internal static void Release(NavMarker marker)
    {
        if (Requests.Remove(marker.Pointer, out bool requested)) marker.SetVisible(requested);
    }

    [HarmonyPatch(typeof(NavMarker), nameof(NavMarker.SetVisible)), HarmonyPrefix]
    internal static void Visibility(NavMarker __instance, ref bool __0)
    {
        if (!Requests.ContainsKey(__instance.Pointer)) return;
        Requests[__instance.Pointer] = __0;
        __0 = false;
    }

    [HarmonyPatch(typeof(NavMarker), nameof(NavMarker.OnDestroy)), HarmonyPrefix]
    private static void Destroyed(NavMarker __instance) => Requests.Remove(__instance.Pointer);
}
