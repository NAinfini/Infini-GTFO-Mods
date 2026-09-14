using System.Text.Json;
using ForgeEnemy.Spawn;

/// <summary>
/// Test double for the MAP2 candidate check. Legal spatial solving belongs to ForgeMap; this only
/// proves the Enemy requirement carries enough data for a solver to reject a candidate without
/// guessing a body size, and that unverified clearance is never read as "fits".
/// </summary>
internal sealed record MapCandidate(string Id, IReadOnlyList<int> NavMeshAgentTypes, uint RequiredAreaMask, bool AirGraph,
    bool ClearanceLimited, IReadOnlyList<uint> ArenaDimensions)
{
    internal static Dictionary<string, MapCandidate> Load(JsonElement fixture) =>
        fixture.GetProperty("candidates").EnumerateArray().Select(c => new MapCandidate(c.GetProperty("id").GetString()!,
            c.GetProperty("navMeshAgentTypes").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            c.GetProperty("requiredAreaMask").GetUInt32(), c.GetProperty("airGraph").GetBoolean(),
            c.GetProperty("clearanceLimited").GetBoolean(),
            c.GetProperty("arenaDimensions").EnumerateArray().Select(x => x.GetUInt32()).ToArray())).ToDictionary(c => c.Id);

    internal string Evaluate(EnemySpawnRequirement requirement)
    {
        if (requirement.Movement == EnemyMovementKind.Unresolved) return "requirement-unresolved";
        if (requirement.Movement == EnemyMovementKind.Flying && !AirGraph) return "movement-unsupported";
        if (requirement.GroundNavigation is { } ground)
        {
            if (!NavMeshAgentTypes.Contains(ground.AgentTypeId)) return "navigation-profile-unsupported";
            if ((RequiredAreaMask & ~ground.WalkableAreaMask) != 0) return "area-mask";
        }
        if (requirement.ArenaDimensions.Except(ArenaDimensions).Any()) return "arena-dimension";
        if (ClearanceLimited && requirement.Unverified.Contains("spawn-clearance")) return "clearance-unverified";
        return "accepted";
    }
}
