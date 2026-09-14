using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ForgeMap;

// G0 assembly plan (FORGE-FRAMEWORK.md section 3.2 I-MAP-PLAN schemaVersion 1) as fixed by the website
// exporter. Same `assembly.<code>` codes and `$`-rooted paths as
// ForgeMap/tools/verify_assembly_plan_fixtures.py; there is no per-plan budget field, only the static
// schemaVersion 1 limits below.
public sealed class AssemblyPlanException : Exception
{
    public string Code { get; }
    public string Path { get; }
    public AssemblyPlanException(string code, string path) : base(code + ": " + path)
    {
        Code = code;
        Path = path;
    }
}

public static class AssemblyPlanLimits
{
    public const string Kind = "forge-map-assembly-plan";
    public const int SchemaVersion = 1;
    public const string DescriptorsPath = "forge/maps/rooms.descriptors.json";
    public const int Zones = 64;
    public const int Placements = 256;
    public const int Pairs = 512;
    public const int PerZonePlacements = 32;
    public const int Descriptors = 256;
    // G4: both endpoints within a plan-frame distance and facing each other (double math on float32 inputs).
    public const double ConnectorPositionTolerance = 1e-3;
    public const double ConnectorOutwardDot = -0.9999;
    // 0.7071067690849304 is the double spelling of Math.fround(Math.SQRT1_2), per the contract's table.
    public static readonly (double X, double Y, double Z, double W)[] CanonicalRotations =
    {
        (0.0, 0.0, 0.0, 1.0),
        (0.0, -0.7071067690849304, 0.0, 0.7071067690849304),
        (0.0, 1.0, 0.0, 0.0),
        (0.0, 0.7071067690849304, 0.0, 0.7071067690849304),
    };
    public static readonly Regex PlanId = new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$", RegexOptions.Compiled);
    public static readonly Regex PlacementId = new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$", RegexOptions.Compiled);
    public static readonly Regex RoomId = new(@"^forge\.native\.room:[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);
    public static readonly Regex RoomRevision = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
}

public sealed record AssemblyDescriptorsLock(string Path, string Sha256);
public sealed record AssemblyZone(long Dimension, long Layer, long LocalIndex, long? ParentLocalIndex);
public sealed record AssemblyEntry(string PlacementId);
public sealed record AssemblyRoomReference(string Id, string Revision);
public sealed record AssemblyLocator(long Dimension, long Layer, long LocalZoneIndex);
public sealed record AssemblyTransform(double[] Position, double[] Rotation, double[] Scale);
public sealed record AssemblyPlacement(string PlacementId, AssemblyRoomReference Room, AssemblyLocator Locator,
    AssemblyTransform Transform);
public sealed record AssemblyEndpoint(string PlacementId, string ConnectorId);
public sealed record AssemblyDoorReference(string DoorId);
public sealed record AssemblyPair(string PairId, AssemblyEndpoint A, AssemblyEndpoint B, AssemblyDoorReference? Door);

public sealed record AssemblyPlan(string PlanId, long LevelLayoutId, long Seed, AssemblyDescriptorsLock Descriptors,
    IReadOnlyList<AssemblyZone> Zones, AssemblyEntry Entry, IReadOnlyList<AssemblyPlacement> Placements,
    IReadOnlyList<AssemblyPair> Pairs);

// `Blockers` is what G0 reports for a plan that is legal but cannot be generated yet; v1 always contains
// `dimension-bounds-unknown` and "static checks passed" is never "generation succeeded".
public sealed record AssemblyPlanValidation(AssemblyPlan Plan, IReadOnlyList<string> Blockers);
