using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ForgeMap.TestFixtures;

/// <summary>
/// The smallest legal G0 corpus (FORGE-FRAMEWORK.md section 3.2, schemaVersion 1): one package room
/// descriptor and one single-zone plan whose only placement is the entry. Synthetic data only; it proves the
/// static checks accept a document, never that a map can be generated. Shared by the managed discovery tests
/// and the native adapter wiring test so both read the same fixture shape.
/// </summary>
public static class PlanFixtures
{
    public const string RevisionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string EvidenceA = "1111111111111111111111111111111111111111111111111111111111111111";
    public const string RoomA = "forge.native.room:room-a";

    /// <summary>One descriptor document, pinned by byte sha256 in every plan.</summary>
    public static string DescriptorsJson() => $$"""
{
  "schemaVersion": 1,
  "kind": "forge-map-resource-descriptors",
  "evidence": "fixture-synthetic",
  "sharedBytes": [],
  "descriptors": [
    {
      "reference": { "id": "{{RoomA}}", "revision": "{{RevisionA}}" },
      "adapter": { "profile": "gtfo.complex-resource-geomorph", "profileRevision": 1 },
      "source": {
        "kind": "native",
        "sourceId": "room-a",
        "contentSha256": "{{RevisionA}}",
        "evidence": { "path": "site/preview/map-rooms/room-a-0123456789ab.json", "sha256": "{{EvidenceA}}" },
        "assetPath": "Assets/Rooms/room-a.prefab",
        "object": { "file": "sharedassets1.assets", "pathId": "100" }
      },
      "authorization": { "reviewRef": "map-room-review:room-a", "runtimeUse": "game-content", "redistributeBytes": false, "dependency": null },
      "runtimeLocator": { "loadedBy": "complex-resource-set", "assetPath": "Assets/Rooms/room-a.prefab", "requiresLoaded": true },
      "hierarchy": {
        "space": "unity-left-handed-meters",
        "nodes": [
          { "id": "sharedassets1.assets:100", "parent": null, "name": "Root",
            "source": { "file": "sharedassets1.assets", "pathId": "100" },
            "gameObject": { "file": "sharedassets1.assets", "pathId": "200" },
            "local": { "position": [0, 0, 0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] },
            "active": true, "meshes": [] }
        ]
      },
      "areas": [ { "id": "Area_room-a", "node": "sharedassets1.assets:100" } ],
      "connectors": [
        { "id": "plug-a-side", "node": "sharedassets1.assets:100", "area": "Area_room-a", "expanderType": "plug",
          "position": [10, 0, 0], "outward": [1, 0, 0], "doubleSided": false }
      ],
      "colliders": { "status": "unknown", "reason": "static extraction does not reconstruct colliders", "items": [] },
      "navigation": { "status": "unknown", "reason": "static extraction does not reconstruct navigation", "items": [] },
      "occlusion": { "status": "unknown", "reason": "static extraction does not reconstruct occlusion portals", "items": [] }
    }
  ]
}
""";

    /// <summary>One legal plan: one zone, the entry placement with the identity transform, no pairs.</summary>
    public static string PlanJson(string planId, long levelLayoutId, long seed, string descriptorsSha256) => $$"""
{
  "schemaVersion": 1,
  "kind": "forge-map-assembly-plan",
  "planId": "{{planId}}",
  "levelLayoutId": {{levelLayoutId}},
  "seed": {{seed}},
  "descriptors": { "path": "forge/maps/rooms.descriptors.json", "sha256": "{{descriptorsSha256}}" },
  "zones": [ { "dimension": 0, "layer": 0, "localIndex": 0, "parentLocalIndex": null } ],
  "entry": { "placementId": "room:entry" },
  "placements": [
    { "placementId": "room:entry", "room": { "id": "{{RoomA}}", "revision": "{{RevisionA}}" },
      "locator": { "dimension": 0, "layer": 0, "localZoneIndex": 0 },
      "transform": { "position": [0, 0, 0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] } }
  ],
  "pairs": []
}
""";

    public static byte[] DescriptorsBytes() => Encoding.UTF8.GetBytes(DescriptorsJson());

    /// <summary>A legal plan whose `descriptors` lock matches the bytes it will sit next to.</summary>
    public static string LockedPlanJson(string planId, long levelLayoutId, long seed) =>
        PlanJson(planId, levelLayoutId, seed, Sha256Hex(DescriptorsBytes()));

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Writes `plugins/<package>/forge/maps/<name>` and returns the absolute file path.</summary>
    public static string WriteMapsFile(string pluginsRoot, string package, string name, string content)
    {
        var maps = Path.Combine(pluginsRoot, package, "forge", "maps");
        Directory.CreateDirectory(maps);
        var file = Path.Combine(maps, name);
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(content));
        return file;
    }
}
