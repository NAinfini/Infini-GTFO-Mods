using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ForgeEnemy.Spawn;

public enum EnemyMovementKind { Ground, Flying, Unresolved }

/// <summary>Row of the Unity project NavMesh agent-type table (NavMeshProjectSettings).</summary>
public sealed record NavMeshAgentType(int AgentTypeId, double Radius, double Height, double MaxSlope, double StepHeight);

/// <summary>Named Unity NavMesh area; bit <c>Index</c> of a walkable mask.</summary>
public sealed record NavMeshArea(int Index, string Name);

/// <summary>Base-prefab NavMeshAgent serialized values. Runtime overrides and scaling are unverified.</summary>
public sealed record EnemyGroundNavigation(int AgentTypeId, double AgentRadius, double AgentHeight, uint WalkableAreaMask, bool AutoTraverseOffMeshLink);

public sealed record EnemySizeRange(double Min, double Max);

/// <summary>
/// One enabled EnemyDataBlock row. Raw values only: the source reads them, the derivation below
/// interprets them. Base prefabs are referenced by path so the prefab facts stay a separate row type.
/// </summary>
public sealed record EnemySpawnRow(
    uint Id,
    string Name,
    uint MovementDataId,
    uint BalancingDataId,
    IReadOnlyList<string> BasePrefabPaths,
    IReadOnlyList<EnemySizeRange> ModelSizeRanges,
    IReadOnlyList<uint> ArenaDimensions);

/// <summary>One enabled EnemyMovementDataBlock row.</summary>
public sealed record EnemyMovementRow(uint Id, int LocomotionPathMove, bool AllowClimbDownLadders);

/// <summary>One enabled EnemyBalancingDataBlock row.</summary>
public sealed record EnemyBalancingRow(uint Id, double EnemyCollisionRadius, bool CanBePushed);

/// <summary>
/// One base prefab, reduced to the two facts the requirement table uses. A prefab without a
/// NavMeshAgent has <see cref="NavMeshAgent"/> null, exactly like the offline evidence records it.
/// </summary>
public sealed record EnemyBasePrefabRow(string Path, EnemyGroundNavigation? NavMeshAgent, bool AirGraphAgent);

/// <summary>
/// Every typed row the derivation reads, whatever produced them: the pinned offline evidence in
/// tests, the loaded DataBlocks and prefabs in the running game. Deliberately not JSON.
/// </summary>
public sealed record EnemySpawnInputs(
    IReadOnlyList<NavMeshAgentType> AgentTypes,
    IReadOnlyList<NavMeshArea> Areas,
    IReadOnlyList<EnemySpawnRow> Enemies,
    IReadOnlyList<EnemyMovementRow> MovementBlocks,
    IReadOnlyList<EnemyBalancingRow> BalancingBlocks,
    IReadOnlyList<EnemyBasePrefabRow> BasePrefabs);

/// <summary>
/// Where a written contract came from: the game build it describes and the SHA-256 of the exact
/// bytes it was derived from (the pinned evidence file). Provenance is not part of the table
/// itself, so a runtime table built from loaded DataBlocks carries none.
/// </summary>
public sealed record EnemySpawnRequirementProvenance(string GameBuild, string EvidenceSha256)
{
    internal void Validate()
    {
        if (GameBuild.Length == 0 || !GameBuild.All(c => c is >= '0' and <= '9')) throw new InvalidDataException("Game build must be a Steam build id.");
        if (EvidenceSha256.Length != 64 || !EvidenceSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Evidence hash must be lowercase SHA-256.");
    }
}

/// <summary>
/// Map-facing spawn requirement for one EnemyDataBlock. It is plain data: no native object, no
/// guessed clearance. <see cref="Unverified"/> names every semantic the offline data cannot prove;
/// a Map solver must treat those as unknown rather than as zero or as a default body size.
/// </summary>
public sealed record EnemySpawnRequirement(
    uint EnemyDataBlockId,
    string Name,
    EnemyMovementKind Movement,
    string? UnresolvedReason,
    EnemyGroundNavigation? GroundNavigation,
    bool? LadderDescent,
    double? CollisionRadius,
    bool? CanBePushed,
    IReadOnlyList<EnemySizeRange> ModelSizeRanges,
    IReadOnlyList<uint> ArenaDimensions,
    IReadOnlyList<string> Unverified);

public sealed class EnemySpawnRequirementCatalog
{
    public const string Format = "gtfo-forge-enemy-spawn-requirements";
    public const int Version = 1;

    // ES_StateEnum constants frozen in evidence/native-api-20403457.json (metadata-verified).
    private const int PathMove = 2, PathMoveFlyer = 28;

    public static readonly IReadOnlyList<string> UnresolvedReasons = new[]
    {
        "locomotion-navigation-conflict", "locomotion-state-unmapped", "movement-datablock-absent",
    };

    /// <summary>Closed vocabulary; each code needs an in-game readback before it may be dropped.</summary>
    public static readonly IReadOnlyList<string> UnverifiedCodes = new[]
    {
        "air-graph-clearance", "arena-dimension-requirement", "base-prefab-resolution", "collision-radius-semantics",
        "datablock-overrides", "runtime-navmesh-agent-profile", "size-multiplier-effect", "spawn-clearance",
    };

    private readonly Dictionary<uint, EnemySpawnRequirement> _byId;

    private EnemySpawnRequirementCatalog(IReadOnlyList<NavMeshAgentType> agentTypes,
        IReadOnlyList<NavMeshArea> areas, IReadOnlyList<EnemySpawnRequirement> requirements)
    {
        AgentTypes = agentTypes; Areas = areas; Requirements = requirements;
        _byId = new Dictionary<uint, EnemySpawnRequirement>();
        Validate();
        foreach (var requirement in requirements) _byId.Add(requirement.EnemyDataBlockId, requirement);
    }

    public IReadOnlyList<NavMeshAgentType> AgentTypes { get; }
    public IReadOnlyList<NavMeshArea> Areas { get; }
    public IReadOnlyList<EnemySpawnRequirement> Requirements { get; }

    /// <summary>Exact lookup. A missing id is an error; no nearest or default body is substituted.</summary>
    public EnemySpawnRequirement Get(uint enemyDataBlockId) =>
        _byId.TryGetValue(enemyDataBlockId, out var requirement)
            ? requirement
            : throw new KeyNotFoundException("No spawn requirement for EnemyDataBlock "
                + enemyDataBlockId.ToString(CultureInfo.InvariantCulture)
                + ": the running game did not load that row, or its internalEnabled is false, or the id does not exist.");

    /// <summary>
    /// Single derivation: interprets typed rows into the requirement table. The same entry serves the
    /// offline evidence in tests and the DataBlocks loaded at runtime, so the two cannot drift.
    /// A row referencing a movement or balancing block, or a base prefab, that the inputs do not
    /// carry is an error: nothing is guessed and no default body size is substituted.
    /// </summary>
    public static EnemySpawnRequirementCatalog Build(EnemySpawnInputs inputs)
    {
        if (inputs == null) throw new ArgumentNullException(nameof(inputs));
        var bases = Unique(inputs.BasePrefabs, prefab => prefab.Path, "base prefab");
        var movement = Unique(inputs.MovementBlocks, block => block.Id, "movement");
        var balancing = Unique(inputs.BalancingBlocks, block => block.Id, "balancing");
        var requirements = new List<EnemySpawnRequirement>();
        foreach (var enemy in inputs.Enemies)
        {
            string label = "EnemyDataBlock " + enemy.Id.ToString(CultureInfo.InvariantCulture) + " ";
            var prefabs = enemy.BasePrefabPaths.Select(path => bases.TryGetValue(path, out var prefab) ? prefab
                : throw new InvalidDataException(label + "references base prefab " + path + ", which the source does not provide.")).ToArray();
            var agents = prefabs.Where(p => p.NavMeshAgent != null).Select(p => p.NavMeshAgent!).ToArray();
            bool airGraph = prefabs.Any(p => p.AirGraphAgent);

            var kind = EnemyMovementKind.Unresolved; string? reason; bool? ladder = null; EnemyGroundNavigation? ground = null;
            if (enemy.MovementDataId == 0) reason = "movement-datablock-absent";
            else
            {
                var block = Row(movement, enemy.MovementDataId, "movement", label);
                ladder = block.AllowClimbDownLadders;
                int pathMove = block.LocomotionPathMove;
                if (pathMove == PathMove && agents.Length == 1 && !airGraph)
                {
                    kind = EnemyMovementKind.Ground; reason = null; ground = agents[0];
                }
                else if (pathMove == PathMoveFlyer && agents.Length == 0 && airGraph) { kind = EnemyMovementKind.Flying; reason = null; }
                else reason = pathMove is PathMove or PathMoveFlyer ? "locomotion-navigation-conflict" : "locomotion-state-unmapped";
            }

            double? radius = null; bool? pushed = null;
            if (enemy.BalancingDataId != 0)
            {
                var block = Row(balancing, enemy.BalancingDataId, "balancing", label);
                radius = block.EnemyCollisionRadius; pushed = block.CanBePushed;
            }
            var arenas = enemy.ArenaDimensions;
            var unverified = new SortedSet<string>(StringComparer.Ordinal)
                { "base-prefab-resolution", "datablock-overrides", "size-multiplier-effect", "spawn-clearance" };
            if (radius != null) unverified.Add("collision-radius-semantics");
            if (kind == EnemyMovementKind.Ground) unverified.Add("runtime-navmesh-agent-profile");
            if (kind == EnemyMovementKind.Flying) unverified.Add("air-graph-clearance");
            if (arenas.Count > 0) unverified.Add("arena-dimension-requirement");
            requirements.Add(new EnemySpawnRequirement(enemy.Id, enemy.Name, kind, reason, ground, ladder, radius, pushed,
                enemy.ModelSizeRanges, arenas, unverified.ToArray()));
        }
        return new EnemySpawnRequirementCatalog(inputs.AgentTypes, inputs.Areas, requirements.OrderBy(r => r.EnemyDataBlockId).ToArray());
    }

    /// <summary>Strict reader for the Map-facing contract; unknown or missing fields are rejected.</summary>
    public static EnemySpawnRequirementCatalog Parse(string json, out EnemySpawnRequirementProvenance provenance)
    {
        using var document = JsonDocument.Parse(json ?? throw new ArgumentNullException(nameof(json)));
        var root = Exact(document.RootElement, "format", "version", "gameBuild", "evidenceSha256", "navMeshAgentTypes", "navMeshAreas", "requirements");
        if (Str(root, "format") != Format || Int(root, "version") != Version) throw new InvalidDataException("Unsupported spawn requirement contract.");
        provenance = new EnemySpawnRequirementProvenance(Str(root, "gameBuild"), Str(root, "evidenceSha256"));
        provenance.Validate();
        var agentTypes = Array(root, "navMeshAgentTypes").Select(a => Exact(a, "agentTypeId", "radius", "height", "maxSlope", "stepHeight"))
            .Select(a => new NavMeshAgentType(Int(a, "agentTypeId"), Num(a, "radius"), Num(a, "height"), Num(a, "maxSlope"), Num(a, "stepHeight"))).ToArray();
        var areas = Array(root, "navMeshAreas").Select(a => Exact(a, "index", "name")).Select(a => new NavMeshArea(Int(a, "index"), Str(a, "name"))).ToArray();
        var requirements = Array(root, "requirements").Select(r =>
        {
            Exact(r, "enemyDataBlockId", "name", "movement", "unresolvedReason", "groundNavigation", "ladderDescent",
                "collisionRadius", "canBePushed", "modelSizeRanges", "arenaDimensions", "unverified");
            var groundElement = r.GetProperty("groundNavigation");
            EnemyGroundNavigation? ground = groundElement.ValueKind == JsonValueKind.Null ? null : ReadGround(Exact(groundElement,
                "agentTypeId", "agentRadius", "agentHeight", "walkableAreaMask", "autoTraverseOffMeshLink"));
            return new EnemySpawnRequirement(UInt(r, "enemyDataBlockId"), Str(r, "name"), Str(r, "movement") switch
                {
                    "ground" => EnemyMovementKind.Ground, "flying" => EnemyMovementKind.Flying, "unresolved" => EnemyMovementKind.Unresolved,
                    var other => throw new InvalidDataException("Unknown movement kind " + other + "."),
                },
                NullableStr(r, "unresolvedReason"), ground, NullableBool(r, "ladderDescent"), NullableNum(r, "collisionRadius"),
                NullableBool(r, "canBePushed"),
                Array(r, "modelSizeRanges").Select(s => Exact(s, "min", "max")).Select(s => new EnemySizeRange(Num(s, "min"), Num(s, "max"))).ToArray(),
                Array(r, "arenaDimensions").Select(d => d.GetUInt32()).ToArray(),
                Array(r, "unverified").Select(u => u.GetString() ?? throw new InvalidDataException("Unverified code must be text.")).ToArray());
        }).ToArray();
        return new EnemySpawnRequirementCatalog(agentTypes, areas, requirements);
    }

    public string ToJson(EnemySpawnRequirementProvenance provenance)
    {
        if (provenance == null) throw new ArgumentNullException(nameof(provenance));
        provenance.Validate();
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format); w.WriteNumber("version", Version);
            w.WriteString("gameBuild", provenance.GameBuild); w.WriteString("evidenceSha256", provenance.EvidenceSha256);
            w.WriteStartArray("navMeshAgentTypes");
            foreach (var a in AgentTypes)
            {
                w.WriteStartObject(); w.WriteNumber("agentTypeId", a.AgentTypeId); w.WriteNumber("radius", a.Radius);
                w.WriteNumber("height", a.Height); w.WriteNumber("maxSlope", a.MaxSlope); w.WriteNumber("stepHeight", a.StepHeight); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("navMeshAreas");
            foreach (var a in Areas) { w.WriteStartObject(); w.WriteNumber("index", a.Index); w.WriteString("name", a.Name); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("requirements");
            foreach (var r in Requirements)
            {
                w.WriteStartObject();
                w.WriteNumber("enemyDataBlockId", r.EnemyDataBlockId); w.WriteString("name", r.Name);
                w.WriteString("movement", r.Movement.ToString().ToLowerInvariant());
                if (r.UnresolvedReason == null) w.WriteNull("unresolvedReason"); else w.WriteString("unresolvedReason", r.UnresolvedReason);
                if (r.GroundNavigation is { } g)
                {
                    w.WriteStartObject("groundNavigation");
                    w.WriteNumber("agentTypeId", g.AgentTypeId); w.WriteNumber("agentRadius", g.AgentRadius); w.WriteNumber("agentHeight", g.AgentHeight);
                    w.WriteNumber("walkableAreaMask", g.WalkableAreaMask); w.WriteBoolean("autoTraverseOffMeshLink", g.AutoTraverseOffMeshLink);
                    w.WriteEndObject();
                }
                else w.WriteNull("groundNavigation");
                if (r.LadderDescent is { } ladder) w.WriteBoolean("ladderDescent", ladder); else w.WriteNull("ladderDescent");
                if (r.CollisionRadius is { } radius) w.WriteNumber("collisionRadius", radius); else w.WriteNull("collisionRadius");
                if (r.CanBePushed is { } pushed) w.WriteBoolean("canBePushed", pushed); else w.WriteNull("canBePushed");
                w.WriteStartArray("modelSizeRanges");
                foreach (var s in r.ModelSizeRanges) { w.WriteStartObject(); w.WriteNumber("min", s.Min); w.WriteNumber("max", s.Max); w.WriteEndObject(); }
                w.WriteEndArray();
                w.WriteStartArray("arenaDimensions"); foreach (var d in r.ArenaDimensions) w.WriteNumberValue(d); w.WriteEndArray();
                w.WriteStartArray("unverified"); foreach (var u in r.Unverified) w.WriteStringValue(u); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private void Validate()
    {
        if (AgentTypes.Count == 0 || AgentTypes.Select(a => a.AgentTypeId).Distinct().Count() != AgentTypes.Count
            || AgentTypes.Any(a => !Positive(a.Radius) || !Positive(a.Height)))
            throw new InvalidDataException("NavMesh agent types must be unique with positive dimensions.");
        if (Areas.Any(a => a.Index is < 0 or > 31 || string.IsNullOrWhiteSpace(a.Name)) || Areas.Select(a => a.Index).Distinct().Count() != Areas.Count)
            throw new InvalidDataException("NavMesh areas must be unique mask bits 0-31.");
        var seen = new HashSet<uint>();
        foreach (var r in Requirements)
        {
            string label = "EnemyDataBlock " + r.EnemyDataBlockId.ToString(CultureInfo.InvariantCulture) + ": ";
            if (!seen.Add(r.EnemyDataBlockId) || string.IsNullOrWhiteSpace(r.Name)) throw new InvalidDataException(label + "duplicate or unnamed.");
            if ((r.Movement == EnemyMovementKind.Unresolved) != (r.UnresolvedReason != null)
                || (r.UnresolvedReason != null && !UnresolvedReasons.Contains(r.UnresolvedReason)))
                throw new InvalidDataException(label + "unresolved movement needs exactly one known reason.");
            if ((r.Movement == EnemyMovementKind.Ground) != (r.GroundNavigation != null))
                throw new InvalidDataException(label + "ground navigation belongs to ground movement only.");
            if (r.GroundNavigation is { } g && (!AgentTypes.Any(a => a.AgentTypeId == g.AgentTypeId) || !Positive(g.AgentRadius) || !Positive(g.AgentHeight)))
                throw new InvalidDataException(label + "ground navigation references an unknown agent type or invalid size.");
            if (r.Movement != EnemyMovementKind.Unresolved && r.LadderDescent == null)
                throw new InvalidDataException(label + "resolved movement must carry its ladder rule.");
            if ((r.CollisionRadius == null) != (r.CanBePushed == null) || (r.CollisionRadius is { } radius && !(radius >= 0 && double.IsFinite(radius))))
                throw new InvalidDataException(label + "balancing values must be present together and finite.");
            if (r.ModelSizeRanges.Count == 0 || r.ModelSizeRanges.Any(s => !Positive(s.Min) || !double.IsFinite(s.Max) || s.Max < s.Min))
                throw new InvalidDataException(label + "model size ranges must be positive and ordered.");
            if (r.Unverified.Count == 0 || !r.Unverified.SequenceEqual(r.Unverified.Distinct().OrderBy(u => u, StringComparer.Ordinal))
                || r.Unverified.Any(u => !UnverifiedCodes.Contains(u)) || !r.Unverified.Contains("spawn-clearance"))
                throw new InvalidDataException(label + "unverified codes must be sorted, known and keep spawn-clearance open.");
        }
    }

    private static Dictionary<TKey, T> Unique<T, TKey>(IReadOnlyList<T> rows, Func<T, TKey> key, string kind) where TKey : notnull
    {
        var byKey = new Dictionary<TKey, T>();
        foreach (var row in rows)
            if (!byKey.TryAdd(key(row), row))
                throw new InvalidDataException("Duplicate " + kind + " row " + Convert.ToString(key(row), CultureInfo.InvariantCulture) + " in the spawn requirement source.");
        return byKey;
    }

    private static T Row<T>(Dictionary<uint, T> rows, uint id, string kind, string label) =>
        rows.TryGetValue(id, out var row) ? row
            : throw new InvalidDataException(label + "references " + kind + " block " + id.ToString(CultureInfo.InvariantCulture)
                + ", which the source does not provide as an enabled row.");

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static EnemyGroundNavigation ReadGround(JsonElement g) => new(Int(g, "agentTypeId"), Num(g, "agentRadius"), Num(g, "agentHeight"),
        g.GetProperty("walkableAreaMask").GetUInt32(), Bool(g, "autoTraverseOffMeshLink"));
    private static JsonElement Exact(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an object.");
        var actual = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length || !names.All(actual.Contains))
            throw new InvalidDataException("Expected exactly: " + string.Join(", ", names) + ".");
        return element;
    }
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
    private static string? NullableStr(JsonElement e, string name) => Get(e, name).ValueKind == JsonValueKind.Null ? null : Str(e, name);
    private static bool? NullableBool(JsonElement e, string name) => Get(e, name).ValueKind == JsonValueKind.Null ? null : Bool(e, name);
    private static double? NullableNum(JsonElement e, string name) => Get(e, name).ValueKind == JsonValueKind.Null ? null : Num(e, name);
}
