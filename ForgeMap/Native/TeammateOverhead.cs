using System;
using System.Collections.Generic;
using Player;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeMap.Native;

/// <summary>
/// The teammate-overhead half of `a-hud`: one extra line under a teammate's own name marker. The line is the one
/// shape the game's marker system supports for it — a `NavMarker` carries four text meshes (title, player name,
/// distance, sign info) and no more — so a second line is a clone of the name mesh parented to the name, which is
/// the shape `InfiniTweaks ResourceHud`/`ResourceHudView` already ships. This file holds the whole model and the
/// whole write; the Harmony postfix that decides *when* a marker is (re-)rendered is
/// <see cref="TeammateOverheadHooks"/>, and it only calls in here.
///
/// Three rules are the reason this is not folded into the handler. A marker is created by the game when a player
/// agent spawns, so a value that arrives before the marker exists is remembered and applied when the marker is
/// placed. A marker is re-rendered by the game whenever that player's own info changes, so the line is re-applied
/// from the stored value rather than from the plan. And the native extra-information row is shared with the game's
/// own rescue and resource hints: this line saves that row's own visibility the first time it takes the row over
/// and puts the value back when the line goes away, when the world is replaced and when the hooks are removed. It
/// never writes `m_extraInfo` and never rewrites the name the game shows.
///
/// Only teammates are addressed. The local player has no overhead marker of its own, which is why `self` combined
/// with this placement is refused by name in the handler rather than drawn somewhere else.
/// </summary>
internal sealed class TeammateOverhead
{
    /// <summary>One live teammate marker, as this model uses it: who it belongs to, whether that player is the
    /// one at this keyboard, and the one native text mesh the line is cloned from and parented to. The interface
    /// exists so the placement's whole decision table runs without a Unity runtime; the production implementation
    /// is the adapter at the bottom of this file.</summary>
    internal interface IMarker
    {
        IntPtr Pointer { get; }
        bool IsLocalPlayer { get; }
        bool ExtraInfoVisible { get; set; }
        /// <summary>The name mesh, or null when the marker has none to clone.</summary>
        TextMeshPro? NameText { get; }
        /// <summary>Whether the name mesh is drawn right now; the line follows it rather than overriding it.</summary>
        bool NameVisible { get; }
    }

    private sealed class Entry
    {
        internal Entry(IMarker? marker) => Marker = marker;
        /// <summary>The teammate's marker, or null while the game has not built one for that player yet. The line
        /// is stored either way; a marker that is not there yet is what <see cref="Bind"/> fills in.</summary>
        internal IMarker? Marker { get; set; }
        internal string Line { get; set; } = "";
        internal UnityEngine.Color? Color { get; set; }
        internal TextMeshPro? Text { get; set; }
        /// <summary>The visibility this line took the native row from, or null while it does not own that row.</summary>
        internal bool? NativeVisibility { get; set; }
    }

    private static readonly Dictionary<IntPtr, Entry> Lines = new();

    /// <summary>The line one teammate's marker should show. The value is stored before the marker exists as well as
    /// after it, because a marker is placed by the game when the agent spawns and a value that was computed a step
    /// earlier is still that teammate's value. An empty line means "this teammate shows nothing", which is a
    /// request about the one teammate the plan named and not a sweep of the others.</summary>
    internal static void Show(IntPtr teammate, IMarker? marker, string line, Color? color)
    {
        if (teammate == IntPtr.Zero) return;
        if (!Lines.TryGetValue(teammate, out var entry)) Lines.Add(teammate, entry = new Entry(marker));
        else if (marker != null) entry.Marker = marker;
        entry.Line = line ?? "";
        entry.Color = color;
        Render(entry);
    }

    /// <summary>Forgets one teammate entirely, which is what a marker the game destroyed is: the clone it owned
    /// goes with it, so a later value for the same pointer does not write through a destroyed marker.</summary>
    internal static void Remove(IntPtr teammate)
    {
        if (!Lines.Remove(teammate, out var entry)) return;
        Release(entry);
    }

    /// <summary>Whether a line this model stored is currently showing under one teammate's name. It is the one
    /// question the native visibility patch asks, and it is answered from the drawn state rather than from the
    /// table, so a line that is stored but not drawn does not hold the game's own row.</summary>
    internal static bool Owns(IntPtr teammate)
        => Lines.TryGetValue(teammate, out var entry) && entry.Text != null && entry.Text.gameObject.activeSelf;

    /// <summary>Applies the stored line to one marker the game just built or rebuilt, which is what the postfix
    /// calls. A marker the table does not know, and the local player's own marker, are left untouched: this row
    /// only ever draws what a plan asked it to draw, on somebody else's head.</summary>
    internal static void Bind(IMarker marker)
    {
        if (!Lines.TryGetValue(marker.Pointer, out var entry) || marker.IsLocalPlayer) return;
        entry.Marker = marker;
        Render(entry);
    }

    /// <summary>Nothing is drawn any more and every native row this line took over is put back. Called when the
    /// world the lines belonged to is replaced and when the hooks are removed.</summary>
    internal static void Clear()
    {
        foreach (var entry in new List<Entry>(Lines.Values)) Release(entry);
        Lines.Clear();
    }

    /// <summary>Whether the native extra-information row belongs to a line this model drew, which is the one
    /// question the visibility patch asks before it defers a native change.</summary>
    internal static bool HiddenByLine(IMarker marker) => Owns(marker.Pointer);
    /// <summary>One teammate's line, written or taken away. It is drawn only while the native name row itself is
    /// drawn: edge clamping, a downed teammate, the local player's focus state and the marker's own hide/show are
    /// the game's business, and this row follows them rather than overriding them. An empty line is a line this
    /// plan took away, so its clone goes with it instead of staying in the marker's object list.</summary>
    private static void Render(Entry entry)
    {
        if (entry.Marker is not { } marker) return;
        if (marker.IsLocalPlayer) return;
        if (entry.Line.Length == 0)
        {
            Release(entry);
            return;
        }
        if (!marker.NameVisible)
        {
            if (entry.Text != null) entry.Text.gameObject.SetActive(false);
            RestoreNative(entry);
            return;
        }
        var text = entry.Text ??= Clone(marker);
        if (text == null) return;
        if (text.text != entry.Line) text.SetText(entry.Line);
        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
        if (entry.Color is { } color) text.color = color;
        entry.NativeVisibility ??= marker.ExtraInfoVisible;
        if (!marker.ExtraInfoVisible) marker.ExtraInfoVisible = true;
        Position(text, marker.NameText);
    }

    /// <summary>Takes the line away for good: the clone is destroyed and the native row gets back the visibility
    /// this line took from it.</summary>
    private static void Release(Entry entry)
    {
        if (entry.Text != null) Object.Destroy(entry.Text.gameObject);
        entry.Text = null;
        RestoreNative(entry);
    }

    private static void RestoreNative(Entry entry)
    {
        if (entry.NativeVisibility is not { } visible || entry.Marker is not { } marker) return;
        marker.ExtraInfoVisible = visible;
        entry.NativeVisibility = null;
    }

    /// <summary>The one text mesh this row owns for a marker: a clone of the marker's own name mesh, so the font,
    /// material, canvas and layer transform come from the game rather than from a second UI stack. It is parented
    /// to the name mesh itself — the shape `InfiniTweaks ResourceHudView` ships — which is also the space the
    /// position below is computed in. Unity's own clone of a component returns a component on a fresh object, so
    /// the clone's own `gameObject` and `transform` are what every line below reads.</summary>
    private static TextMeshPro? Clone(IMarker marker)
    {
        var source = marker.NameText;
        if (source == null) return null;
        var clone = Object.Instantiate(source.gameObject, source.transform);
        var text = clone.GetComponent<TextMeshPro>();
        if (text == null)
        {
            Object.Destroy(clone);
            return null;
        }
        clone.name = "ForgeHudTeammateValue";
        text.transform.localRotation = Quaternion.identity;
        text.transform.localScale = Vector3.one;
        var rect = text.rectTransform;
        rect.anchorMin = rect.anchorMax = source.rectTransform.pivot;
        rect.pivot = new Vector2(0.5f, 1f);
        text.margin = Vector4.zero;
        text.enableAutoSizing = false;
        text.richText = true;
        // A cloned player name can carry its own colour grammar or a vertex gradient; neither one is this row's.
        text.overrideColorTags = false;
        text.enableVertexGradient = false;
        text.alignment = TextAlignmentOptions.Top;
        text.overflowMode = TextOverflowModes.Overflow;
        text.SetText("");
        return text;
    }

    /// <summary>Directly under the rendered name, in the name mesh's own local space: `textBounds` is local to that
    /// mesh, so no screen or parent coordinate is involved and no icon gutter is assumed.</summary>
    private static void Position(TextMeshPro text, TextMeshPro? name)
    {
        if (name == null) return;
        var bounds = name.textBounds;
        var position = new Vector3(bounds.center.x, bounds.min.y - 4f, 0f);
        if (text.transform.localPosition != position) text.transform.localPosition = position;
    }

    /// <summary>The production adapter over the game's own marker component. Every member is a read or a write on
    /// the one object the game created.</summary>
    internal sealed class NativeMarker : IMarker
    {
        private readonly PlaceNavMarkerOnGO _marker;

        internal NativeMarker(PlaceNavMarkerOnGO marker) => _marker = marker;

        public IntPtr Pointer => _marker.Pointer;

        public bool IsLocalPlayer
        {
            get
            {
                var player = _marker.Player;
                return player != null && player.IsLocallyOwned;
            }
        }

        public bool ExtraInfoVisible
        {
            get => _marker.m_extraInfoVisible;
            set => _marker.m_extraInfoVisible = value;
        }

        public TextMeshPro? NameText => _marker.m_marker?.m_playerName;

        public bool NameVisible
        {
            get
            {
                var name = NameText;
                return name != null && name.gameObject.activeInHierarchy;
            }
        }
    }
}
