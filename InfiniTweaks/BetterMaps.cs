using System;
using System.Collections.Generic;
using AIGraph;
using CellMenu;
using GTFO.API;
using HarmonyLib;
using LevelGeneration;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace InfiniTweaks;

internal static class BetterMaps
{
    internal static void Initialize()
    {
        if (!Settings.EnableBetterMaps.Value)
        {
            return;
        }

        AssetAPI.OnImplReady += ApplyIconPriorities;
        LevelAPI.OnBuildDone += RebuildMap;
    }

    private static void ApplyIconPriorities()
    {
        try
        {
            var zonePrefab = GuiManager.MainMenuLayer.PageMap.m_zoneGUIPrefab;
            var areaPrefab = zonePrefab.GetComponentInChildren<CM_MapZoneGUIItem>().m_areaGUIPrefab;
            var area = areaPrefab.GetComponentInChildren<CM_MapAreaGUIItem>();

            SetSortingOrder(area.m_doorGUIPrefab, 1);
            SetSortingOrder(area.m_resourceLockerGUIPrefab, 2);
            SetSortingOrder(area.m_resourceBoxGUIPrefab, 2);
            SetSortingOrder(area.m_computerTerminalGUIPrefab, 2);
            SetSortingOrder(area.m_ladderGUIPrefab, 0);
            SetSortingOrder(area.m_signGUIPrefab, 0);
            SetSortingOrder(area.m_disinfectionStationGUIPrefab, 2);
            SetSortingOrder(area.m_powerGeneratorGUIPrefab, 2);
            SetSortingOrder(area.m_bulkheadDoorControllerGUIPrefab, 2);
        }
        catch (Exception error)
        {
            Plugin.PluginLog.LogError($"Could not apply Better Maps icon priorities: {error}");
        }
    }

    private static void SetSortingOrder(GameObject prefab, int order)
    {
        foreach (var renderer in prefab.GetComponentsInChildren<SpriteRenderer>(true))
        {
            renderer.sortingOrder = order;
        }

        foreach (var label in prefab.GetComponentsInChildren<TextMeshPro>(true))
        {
            label.sortingOrder = order;
        }
    }

    private static void RebuildMap()
    {
        using var _ = Telemetry.Measure("BetterMapsBuild");
        var map = MapDetails.Current;
        if (map == null)
        {
            Plugin.PluginLog.LogError("Better Maps: MapDetails was not created for this level.");
            return;
        }

        try
        {
            map.m_mapResolution = Mathf.Min(map.m_mapResolution, map.m_mapRenderResolution);
            map.m_navmesh = CreateAccessibleNavMesh();
            map.SetupMeshRendering();

            // Interop exposes Bounds as a value-returning property.
            var bounds = map.m_mapMeshRenderer.bounds;
            bounds.Expand(new Vector3(64f, 0f, 64f));
            map.m_bounds = bounds;
            map.s_boundsCenter = map.m_bounds.center;
            map.s_boundsExtentsX = map.m_bounds.extents.x;
            map.s_boundsExtentsZ = map.m_bounds.extents.z;

            RenderMap(map);
        }
        catch (Exception error)
        {
            Plugin.PluginLog.LogError($"Could not rebuild the Better Maps texture: {error}");
        }
        finally
        {
            if (map.m_mapMeshRenderer != null)
            {
                map.m_mapMeshRenderer.enabled = false;
                UnityEngine.Object.Destroy(map.m_mapMeshRenderer.gameObject);
                map.m_mapMeshRenderer = null;
            }
            if (map.m_navmesh != null)
            {
                UnityEngine.Object.Destroy(map.m_navmesh);
                map.m_navmesh = null;
            }
        }
    }

    private static Mesh CreateAccessibleNavMesh()
    {
        var triangulation = NavMesh.CalculateTriangulation();
        var vertices = triangulation.vertices;
        var indices = triangulation.indices;
        var keptIndices = new List<int>();

        for (var index = 0; index < indices.Length; index += 3)
        {
            var a = indices[index];
            var b = indices[index + 1];
            var c = indices[index + 2];
            var pointA = vertices[a];
            var pointB = vertices[b];
            var pointC = vertices[c];
            var center = (pointA + pointB + pointC) / 3f;
            var heightDifference = Mathf.Max(pointA.y, Mathf.Max(pointB.y, pointC.y)) - Mathf.Min(pointA.y, Mathf.Min(pointB.y, pointC.y));
            var dimension = Dimension.GetDimensionFromPos(center);

            if (dimension is null || !IsAccessible(dimension.DimensionIndex, center, Mathf.Max(1.5f, heightDifference * 1.5f)))
            {
                continue;
            }

            keptIndices.Add(c);
            keptIndices.Add(b);
            keptIndices.Add(a);
        }

        if (keptIndices.Count == 0)
        {
            throw new InvalidOperationException("Better Maps found no accessible navmesh triangles.");
        }

        var mesh = new Mesh();
        try
        {
            mesh.indexFormat = vertices.Length >= 65534 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetTriangles(keptIndices.ToArray(), 0);
            mesh.RecalculateNormals();
            return mesh;
        }
        catch
        {
            UnityEngine.Object.Destroy(mesh);
            throw;
        }
    }

    private static bool IsAccessible(eDimensionIndex dimension, Vector3 position, float tolerance)
    {
        return AIG_GeomorphNodeVolume.TryGetGeomorphVolume(0, dimension, position, out var volume) &&
               volume.m_voxelNodeVolume.TryGetPillar(position, out var pillar) &&
               pillar.TryGetVoxelNode(position.y - tolerance, position.y + tolerance, out var node) &&
               AIG_NodeCluster.TryGetNodeCluster(node.ClusterID, out _);
    }

    private static void RenderMap(MapDetails map)
    {
        var width = (int)map.m_bounds.size.x * map.m_mapRenderResolution;
        var height = (int)map.m_bounds.size.z * map.m_mapRenderResolution;
        map.GetClampedRes(1024, ref width, ref height, out _, out _, out var downscale);

        var samplingProperty = Shader.PropertyToID("_SamplingScale");
        var factor = 0.005f * downscale;
        var outline = factor * Settings.MapOutlineScale.Value;
        var blur = factor * Settings.MapBlurScale.Value;
        map.m_findMapOutline_Material.SetFloat(samplingProperty, outline);
        map.m_SDF_Material.SetFloat(samplingProperty, outline);
        map.m_blurX.SetFloat(samplingProperty, blur);
        map.m_blurY.SetFloat(samplingProperty, blur);

        RenderTexture? mapSource = null;
        RenderTexture? outlined = null;
        RenderTexture? blurred = null;
        CommandBuffer? commands = null;
        try
        {
            mapSource = RenderTexture.GetTemporary(width, height, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            outlined = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            blurred = RenderTexture.GetTemporary(map.m_mapTexture.width, map.m_mapTexture.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            mapSource.filterMode = outlined.filterMode = blurred.filterMode = FilterMode.Trilinear;

            commands = new CommandBuffer();
            commands.SetRenderTarget(mapSource);
            commands.ClearRenderTarget(true, true, Color.black);
            commands.SetProjectionMatrix(map.m_camera.projectionMatrix);
            commands.SetViewMatrix(map.m_camera.worldToCameraMatrix);
            commands.DrawRenderer(map.m_mapMeshRenderer, map.m_navmeshMaterial);
            commands.Blit(mapSource, outlined, map.m_findMapOutline_Material);
            commands.Blit(outlined, map.m_mapTexture, map.m_SDF_Material);
            commands.Blit(map.m_mapTexture, blurred, map.m_blurX);
            commands.Blit(blurred, map.m_mapTexture, map.m_blurY);
            commands.SetRenderTarget(map.m_mapTexture);

            map.m_mapMeshRenderer.enabled = true;
            Graphics.ExecuteCommandBuffer(commands);
        }
        finally
        {
            commands?.Dispose();
            if (mapSource != null) RenderTexture.ReleaseTemporary(mapSource);
            if (outlined != null) RenderTexture.ReleaseTemporary(outlined);
            if (blurred != null) RenderTexture.ReleaseTemporary(blurred);
        }
    }
}

[HarmonyPatch(typeof(CM_MapGUIItemBase), nameof(CM_MapGUIItemBase.PlaceInGUI))]
internal static class MapOrientationPatch
{
    [HarmonyPatch(new Type[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(bool) })]
    [HarmonyPrefix]
    private static void CorrectOrientation(ref bool adjustUpSideDown)
    {
        if (Settings.EnableBetterMaps.Value)
        {
            adjustUpSideDown = true;
        }
    }
}
