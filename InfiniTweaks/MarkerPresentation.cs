using UnityEngine;

namespace InfiniTweaks;

// Native projection owns active components. We own content and requested visibility.
internal sealed class MarkerPresentation
{
    private readonly NavMarker _marker;
    private string _title = "", _shownTitle = "";
    private Color? _color;
    private float? _scale;
    private bool _showTitle = true;
    private bool? _distance;
    internal MarkerPresentation(NavMarker marker) => _marker = marker;
    internal bool Title(string title)
    {
        if (_title == title) return false;
        _title = title;
        return UpdateTitle();
    }
    private bool UpdateTitle()
    {
        string text = _showTitle ? _title : "";
        if (_shownTitle == text) return false;
        _marker.SetTitle(text); _shownTitle = text; return true;
    }
    internal int Apply(bool visible, bool title, bool distance, Color color, float scale, float alpha)
    {
        if (!visible)
        {
            if (!_marker.IsVisible) return 0;
            _marker.SetVisible(false);
            return 1;
        }
        int changes = 0;
        if (_distance != distance)
        {
            // Keep the title component enabled; folding text must not reset the
            // native state to Inactive, as SetVisualStates does in this build.
            MarkerVisuals.Apply(_marker, true, distance);
            _distance = distance; _color = null; _scale = null;
            _shownTitle = "\0";
            changes++;
        }
        _showTitle = title;
        if (UpdateTitle()) changes++;
        if (!_color.HasValue || !_color.Value.Equals(color)) { _marker.SetColor(color); _color = color; changes++; }
        if (_scale != scale) { _marker.SetIconScale(scale); _scale = scale; changes++; }
        if (!_marker.IsVisible) { _marker.SetVisible(true); changes++; }
        // SetAlpha skips inactive native components. Apply AFTER activation and
        // refresh while visible so offscreen/focus/aim transitions cannot retain
        // stale zero opacity. ResourceHelper uses the same continuous refresh.
        _marker.SetAlpha(alpha); changes++;
        return changes;
    }
}
