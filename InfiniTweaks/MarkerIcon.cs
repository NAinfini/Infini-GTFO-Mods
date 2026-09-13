using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace InfiniTweaks;

// Shared, embedded textures live for the plugin lifetime; per-marker state dies with its native marker.
internal sealed class MarkerIcon
{
    private static readonly Dictionary<string, Sprite> Sprites = new();
    private readonly SpriteRenderer _renderer;
    private readonly Sprite _sprite;

    private MarkerIcon(NavMarker marker, Sprite sprite)
    {
        _sprite = sprite;
        // The CarryItem slot is the only custom-art surface (as in ResourceHelper).
        _renderer = marker.m_carryitem.GetComponentInChildren<SpriteRenderer>(true);
    }

    internal static MarkerIcon Create(NavMarker marker, MarkerCategory category, uint itemId, string key)
    {
        var name = MarkerIconCatalog.Select(category, itemId, key);
        if (!Sprites.TryGetValue(name, out var sprite))
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"InfiniTweaks.MarkerIcons.{name}.png")
                ?? throw new InvalidOperationException($"Missing embedded marker texture: {name}");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var texture = new Texture2D(2, 2)
            {
                name = "InfiniTweaks." + name, hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (!ImageConversion.LoadImage(texture, bytes.ToArray(), true))
            { UnityEngine.Object.Destroy(texture); throw new InvalidOperationException($"Invalid marker texture: {name}"); }
            var rect = new Rect(0, 0, texture.width, texture.height);
            // Previous exports were 64px at 64 pixels/unit. More pixels must not enlarge the HUD.
            sprite = Sprite.Create(texture, rect, new Vector2(.5f, .5f), Math.Max(texture.width, texture.height));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Sprites.Add(name, sprite);
        }
        return new(marker, sprite);
    }

    internal void Show()
    {
        // Reassert opaque artwork after native tint/alpha updates. Visibility is owned
        // by the marker GameObject; name/distance fading must not wash out the icon.
        if (_renderer == null) return;
        if (_renderer.sprite != _sprite) _renderer.sprite = _sprite;
        var color = _renderer.color;
        if (color.r != 1 || color.g != 1 || color.b != 1 || color.a != 1)
            _renderer.color = new Color(1, 1, 1, 1);
    }
}
