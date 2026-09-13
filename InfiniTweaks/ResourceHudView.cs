using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace InfiniTweaks;

// One owned resource row group per native player marker; never rewrites its name
// into a combined name/inventory string. Native rescue indicators remain untouched.
internal sealed class ResourceHudView
{
    private static readonly Dictionary<IntPtr, ResourceHudView> Views = new();
    private readonly NavMarker _marker;
    private readonly TextMeshPro _text;
    private readonly float _nameAlpha, _distanceAlpha;
    private ResourceHudView(NavMarker marker)
    {
        _marker = marker;
        var source = marker.m_playerName;
        _nameAlpha = source.alpha;
        _distanceAlpha = marker.m_distance != null ? marker.m_distance.alpha : 1;
        _text = Object.Instantiate(source, source.transform);
        _text.name = "InfiniTweaks.ResourceHud";
        // Inherit the native name's projection, motion and scaling directly.
        _text.transform.localRotation = Quaternion.identity;
        _text.transform.localScale = Vector3.one;
        _text.rectTransform.anchorMin = _text.rectTransform.anchorMax = source.rectTransform.pivot;
        _text.rectTransform.pivot = new Vector2(.5f, 1);
        _text.margin = Vector4.zero;
        _text.enableAutoSizing = false;
        _text.richText = true;
        // A cloned player name can override rich-text colors or tint their vertices.
        _text.overrideColorTags = false;
        _text.enableVertexGradient = false;
        _text.alignment = TextAlignmentOptions.Top;
        _text.enableWordWrapping = false;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.color = Color.white;
        _text.faceColor = Color.white;
        _text.SetText("");
        _text.gameObject.SetActive(false);
    }
    internal static void Show(PlaceNavMarkerOnGO owner, string text)
    {
        var marker = owner.m_marker;
        if (marker == null || marker.m_playerName == null) return;
        if (Views.TryGetValue(owner.Pointer, out var previous) && previous._marker != marker)
            Remove(owner.Pointer);
        if (!Views.TryGetValue(owner.Pointer, out var view)) Views.Add(owner.Pointer, view = new(marker));
        if (view._text.text != text) view._text.SetText(text);
    }
    internal static void Alpha(PlaceNavMarkerOnGO owner, float resourceAlpha, bool livingGameplay)
    {
        if (Views.TryGetValue(owner.Pointer, out var previous) && previous._marker != owner.m_marker)
        { Remove(owner.Pointer); return; }
        if (!Views.TryGetValue(owner.Pointer, out var view) || view._marker == null || view._text == null) return;
        var name = view._marker.m_playerName;
        if (name == null) return;
        SetAlpha(name, livingGameplay ? Settings.HudNameOpacity.Value : view._nameAlpha);
        if (view._marker.m_distance != null)
            SetAlpha(view._marker.m_distance, livingGameplay ? (Settings.HudDistance.Value ? Settings.HudDistanceOpacity.Value : 0) : view._distanceAlpha);
        bool visible = livingGameplay && view._text.text.Length > 0 && name.gameObject.activeInHierarchy;
        if (view._text.gameObject.activeSelf != visible) view._text.gameObject.SetActive(visible);
        if (!visible) return;
        // Bounds are local to the name mesh; no screen/parent coordinates or
        // icon gutter. Position immediately below the rendered name, not its feet.
        var bounds = name.textBounds;
        var position = new Vector3(bounds.center.x, bounds.min.y - 4, 0);
        if (view._text.transform.localPosition != position) view._text.transform.localPosition = position;
        SetAlpha(view._text, resourceAlpha);
    }
    private static void SetAlpha(TextMeshPro text, float alpha) { if (text.alpha != alpha) text.alpha = alpha; }
    internal static void Remove(IntPtr owner)
    {
        if (!Views.Remove(owner, out var view)) return;
        if (view._marker != null)
        {
            if (view._marker.m_playerName != null) SetAlpha(view._marker.m_playerName, view._nameAlpha);
            if (view._marker.m_distance != null) SetAlpha(view._marker.m_distance, view._distanceAlpha);
        }
        if (view._text != null) Object.Destroy(view._text.gameObject);
    }
    internal static void Clear()
    {
        foreach (var key in new List<IntPtr>(Views.Keys)) Remove(key);
    }
}
