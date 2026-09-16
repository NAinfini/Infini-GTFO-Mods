using System;
using HarmonyLib;
using Player;
using TMPro;

namespace ForgeMap.Native;

/// <summary>
/// The Harmony patches behind `a-hud`'s `teammate_overhead` placement. Every rule lives in
/// <see cref="TeammateOverhead"/>; the three patches below only decide *when* that model is asked to draw.
///
/// They are declared here rather than added to `MapNativeHooks.cs`, which is a shared registration point this
/// slice must not edit: the integration batch adds the four types below to `MapNativeHooks.Types` from
/// `integration.json`. That is also why the model answers both "the game just rebuilt this marker" and "this
/// marker is gone" as calls into the same table instead of holding a dictionary of its own per patch.
///
/// `UpdateExtraInfo` is the refresh point the game itself uses for a player's marker — it runs when the player's
/// own info changes, and `InfiniTweaks ResourceHud` is the shipped precedent for hanging a per-teammate row on
/// exactly this member — so the postfix is where a remembered line is (re-)applied. `OnDestroy` is where a marker
/// that no longer exists is dropped. `SetExtraInfoVisible` is intercepted as well, because that one member drives
/// both the game's own rescue/resource hint row and this line's row: while a line is showing, the row is this
/// row's and the game's own visibility change is deferred; once the line goes away the game's own state is what
/// the model puts back.
/// </summary>
[HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.UpdateExtraInfo))]
internal static class TeammateOverheadRender
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlaceNavMarkerOnGO __instance)
    {
        if (__instance == null) return;
        Plugin.Session?.Guard(_ => TeammateOverhead.Bind(new TeammateOverhead.NativeMarker(__instance)));
    }
}

/// <summary>A marker the game destroyed is a teammate nobody can draw a line over any more, so its entry leaves
/// the table with it instead of being written through on a later refresh.</summary>
[HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.OnDestroy))]
internal static class TeammateOverheadRemoved
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlaceNavMarkerOnGO __instance)
    {
        if (__instance == null) return;
        Plugin.Session?.Guard(_ => TeammateOverhead.Remove(__instance.Pointer));
    }
}

/// <summary>The native extra-information row's own visibility. While a line is showing under this teammate's
/// name, the row belongs to this line and a native change to it is deferred; a marker no line owns passes
/// through untouched, which is what keeps the game's rescue and resource hints exactly as they were.</summary>
[HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.SetExtraInfoVisible))]
internal static class TeammateOverheadVisibility
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Prefix(PlaceNavMarkerOnGO __instance, ref bool visible)
    {
        if (__instance == null || !visible) return;
        if (Plugin.Session?.Module is null) return;
        if (TeammateOverhead.HiddenByLine(new TeammateOverhead.NativeMarker(__instance))) visible = false;
    }
}
