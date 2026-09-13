namespace InfiniTweaks;

internal static class MarkerVisuals
{
    internal static void Apply(NavMarker marker, bool title = true, bool distance = true)
    {
        // Every owned marker uses the same custom-art surface; category selects its texture.
        marker.SetStyle(eNavMarkerStyle.PlayerPingCarryItem);
        var visible = NavMarkerOption.CarryItem | (title ? NavMarkerOption.Title : NavMarkerOption.Empty)
            | (distance ? NavMarkerOption.Distance : NavMarkerOption.Empty);
        marker.SetVisualStates(visible, visible, NavMarkerOption.Empty, NavMarkerOption.Empty);
    }
}
