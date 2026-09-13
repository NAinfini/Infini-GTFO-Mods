using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace InfiniTweaks;

// Draw only the pickup prefab's mesh data. Never spawn a second pickup, collider,
// script, light or network object merely to show where an item will be placed.
internal static class PlacementPreview
{
    private readonly record struct Part(Mesh Mesh, Matrix4x4 Local);
    private static readonly List<Part> Parts = new();
    private static Material? _material;
    private static uint _item;
    private static Matrix4x4 _placement;
    private static Vector3 _scale = Vector3.one;
    private static bool _visible;
    private static bool _materialAttempted;

    internal static void Select(uint item, Vector3 position, Quaternion rotation)
    {
        if (_item != item)
        {
            Parts.Clear(); _item = item;
            // The native accessor avoids IL2CPP Dictionary.TryGetValue's broken
            // shared-generic out-value trampoline observed in the live log.
            var variants = ItemSpawnManager.GetItemPrefabs(item, ItemMode.Pickup);
            if (variants != null && variants.Count > 0 && variants[0] != null)
            {
                var prefab = variants[0];
                _scale = prefab.transform.localScale;
                foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && IsVisiblePart(filter, prefab.transform))
                        Parts.Add(new(filter.sharedMesh, prefab.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix));
            }
            if (Parts.Count == 0) Plugin.PluginLog.LogWarning($"Placement preview: no static pickup mesh for item {item}.");
        }
        if (_material == null && !_materialAttempted)
        {
            _materialAttempted = true;
            var shader = Shader.Find("Transparent/Diffuse");
            if (shader == null)
            {
                Plugin.PluginLog.LogWarning("Placement preview: Transparent/Diffuse shader is unavailable.");
                return;
            }
            _material = new Material(shader) { color = new Color(0.3f, 1f, 0.7f, 0.65f) };
        }
        _placement = Matrix4x4.TRS(position, rotation, _scale);
        _visible = true;
    }
    private static bool IsVisiblePart(MeshFilter filter, Transform root)
    {
        var renderer = filter.GetComponent<MeshRenderer>();
        if (renderer == null || !renderer.enabled) return false;
        // Loaded prefab roots may be disabled; inactive variant/FX children are
        // different and must not all be superimposed in the placement preview.
        for (var parent = filter.transform; parent != null && parent != root; parent = parent.parent)
            if (!parent.gameObject.activeSelf) return false;
        return true;
    }
    internal static void Draw(Camera camera)
    {
        if (!_visible || _material == null) return;
        foreach (var part in Parts)
            for (int submesh = 0; submesh < part.Mesh.subMeshCount; submesh++)
                Graphics.DrawMesh(part.Mesh, _placement * part.Local, _material, 0, camera, submesh, null, ShadowCastingMode.Off, false);
    }
    internal static void Hide() => _visible = false;
    internal static void Clear()
    {
        Hide(); Parts.Clear(); _item = 0;
        if (_material != null) Object.Destroy(_material);
        _material = null; _materialAttempted = false;
    }
}
