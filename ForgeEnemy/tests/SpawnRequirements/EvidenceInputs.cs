using System.Security.Cryptography;
using System.Text.Json;
using ForgeEnemy.Spawn;

/// <summary>
/// Test-side translation of the pinned offline evidence into the typed rows the one derivation reads.
/// The evidence file is a fixture: production builds the same rows from the loaded DataBlocks and
/// prefabs (Native/EnemySpawnRequirementSource.cs). A row the pinned build does not have — a disabled
/// block, a reference to a prefab or block the file does not carry — fails closed here instead of
/// reaching the derivation.
/// </summary>
internal static class EvidenceInputs
{
    internal static EnemySpawnInputs Read(byte[] evidenceUtf8, out EnemySpawnRequirementProvenance provenance)
    {
        using var document = JsonDocument.Parse(evidenceUtf8);
        var root = document.RootElement;
        if (Int(root, "schemaVersion") != 1 || Str(root, "kind") != "gtfo-enemy-spawn-space-evidence" || Bool(root, "gameExecuted"))
            throw new InvalidDataException("Unsupported spawn-space evidence.");
        provenance = new EnemySpawnRequirementProvenance(Str(root, "build"),
            Convert.ToHexString(SHA256.HashData(evidenceUtf8)).ToLowerInvariant());
        var agentTypes = Array(root, "navMeshAgentTypes").Select(a => new NavMeshAgentType(Int(a, "agentTypeId"),
            Num(a, "radius"), Num(a, "height"), Num(a, "maxSlope"), Num(a, "stepHeight"))).ToArray();
        var areas = Array(root, "navMeshAreas").Select(a => new NavMeshArea(Int(a, "index"), Str(a, "name"))).ToArray();
        var bases = Array(root, "basePrefabs").Select(b => new EnemyBasePrefabRow(Str(b, "path"),
            b.GetProperty("navMeshAgent").ValueKind == JsonValueKind.Null ? null : Ground(b.GetProperty("navMeshAgent")),
            Bool(b, "airGraphAgent"))).ToArray();
        var enemies = Enabled(root, "enemies").Select(e => new EnemySpawnRow(UInt(e, "id"), Str(e, "name"),
            UInt(e, "movementDataId"), UInt(e, "balancingDataId"),
            Array(e, "basePrefabs").Select(p => p.GetString() ?? throw new InvalidDataException("Base prefab path must be text.")).ToArray(),
            Array(e, "sizeRanges").Select(s => new EnemySizeRange(Num(s, "min"), Num(s, "max"))).ToArray(),
            Array(e, "arenaDimensions").Select(d => d.GetUInt32()).ToArray())).ToArray();
        var movement = Enabled(root, "movementBlocks").Select(b => new EnemyMovementRow(UInt(b, "id"),
            Int(b, "locomotionPathMove"), Bool(b, "allowClimbDownLadders"))).ToArray();
        var balancing = Enabled(root, "balancingBlocks").Select(b => new EnemyBalancingRow(UInt(b, "id"),
            Num(b, "enemyCollisionRadius"), Bool(b, "canBePushed"))).ToArray();
        return new EnemySpawnInputs(agentTypes, areas, enemies, movement, balancing, bases);
    }

    private static IEnumerable<JsonElement> Enabled(JsonElement root, string name) => Array(root, name).Select(row =>
        Bool(row, "internalEnabled") ? row : throw new InvalidDataException("Disabled " + name + " row " + UInt(row, "id") + " in pinned evidence."));

    private static EnemyGroundNavigation Ground(JsonElement agent) => new(Int(agent, "agentTypeId"), Num(agent, "radius"), Num(agent, "height"),
        checked((uint)Get(agent, "walkableMask").GetInt64()), Bool(agent, "autoTraverseOffMeshLink"));

    private static JsonElement Get(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        ? v : throw new InvalidDataException("Missing " + name + ".");
    private static IEnumerable<JsonElement> Array(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.Array } a
        ? a.EnumerateArray() : throw new InvalidDataException(name + " must be an array.");
    private static string Str(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.String } v && !string.IsNullOrWhiteSpace(v.GetString())
        ? v.GetString()! : throw new InvalidDataException(name + " must be non-empty text.");
    private static bool Bool(JsonElement e, string name) => Get(e, name).ValueKind switch
        { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException(name + " must be boolean.") };
    private static int Int(JsonElement e, string name) => Get(e, name).GetInt32();
    private static uint UInt(JsonElement e, string name) => Get(e, name).GetUInt32();
    private static double Num(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.Number } v && double.IsFinite(v.GetDouble())
        ? v.GetDouble() : throw new InvalidDataException(name + " must be a finite number.");
}
