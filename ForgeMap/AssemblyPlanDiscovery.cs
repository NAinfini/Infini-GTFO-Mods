using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

// Package-level rejection (`assembly.package-layout`): the package cannot be checked at all, so no plan of it
// gets a diagnostic. `assembly.plan-file` and `assembly.duplicate-level-layout` are per-file diagnostics instead.
public sealed record AssemblyDiscoveryRejection(string Code, string Path);

// One `*.assembly.json` file under the package's `forge/maps/`. A rejected plan carries the contract code and
// its `$`-rooted schema path; an accepted plan carries the deduplicated, ordinal-sorted blockers instead, which
// is never a generation success.
public sealed record AssemblyPlanDiagnostic(string Path, string? PlanId, string? Code, string? ErrorPath,
    IReadOnlyList<string> Blockers)
{
    public bool Passed => Code is null;
}

// One read-only discovery pass. `PackagePath` is the single `<dir>` that owns `forge/maps/`, or null when the
// plugins root has none (nothing to do) or when the layout was rejected before a package could be chosen.
public sealed record AssemblyDiscovery(string? PackagePath, AssemblyDiscoveryRejection? Rejection,
    IReadOnlyList<AssemblyPlanDiagnostic> Plans);

/// <summary>
/// G0 (I-MAP-PLAN) package discovery for the D-013 transition: enumerate `<pluginsRoot>/*/` for the one
/// directory holding `forge/maps/`, read `rooms.descriptors.json` and every `<planId>.assembly.json`, run the
/// existing G0-G6 static checks and report one diagnostic per plan. It is read-only: no generation, no game
/// state, no provider registration, no network, and a rejected plan never stops another plan.
/// </summary>
/// <remarks>
/// Every reported path is relative to the BepInEx root and `/`-separated, so `pluginsRelativeRoot` is the
/// caller's spelling of `pluginsRoot` from there (normally `"plugins"`). Contract points that are not written
/// are decided here and listed in ForgeMap/VALIDATION.md rather than invented per call site: a plugins root
/// without `forge/maps/` is a silent no-op (same as the I-PACK plan discovery), more than one package rejects
/// them all (`assembly.package-layout`), a package without `rooms.descriptors.json` rejects
/// (`assembly.package-layout`, the same code and path both fixture checkers use), and a file that cannot be
/// read is reported under the phase that would have read it (`assembly.schema` for a plan document,
/// `descriptor.schema` for the descriptor document).
/// </remarks>
public static class AssemblyPlanDiscovery
{
    public const string PackageLayoutCode = "assembly.package-layout";
    public const string PlanFileCode = "assembly.plan-file";
    public const string DuplicateLevelLayoutCode = "assembly.duplicate-level-layout";

    private const string PlanSuffix = ".assembly.json";
    private const string DescriptorsName = "rooms.descriptors.json";
    private const string MapsRelativePath = "forge/maps";
    private const string DescriptorsErrorPath = "$descriptors";
    private const string RootErrorPath = "$";

    private static readonly AssemblyDiscovery Nothing = new(null, null, Array.Empty<AssemblyPlanDiagnostic>());

    public static AssemblyDiscovery Discover(string pluginsRoot, string pluginsRelativeRoot)
    {
        ArgumentNullException.ThrowIfNull(pluginsRelativeRoot);
        if (string.IsNullOrWhiteSpace(pluginsRoot)) return Nothing;
        string root;
        try { root = Path.GetFullPath(pluginsRoot); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return Nothing; }
        if (!Directory.Exists(root)) return Nothing;

        string[] packages;
        try
        {
            // Only the directory name is inspected for a package: a directory without `forge/maps/` is skipped
            // without opening a single file in it, so packages that do not use Forge are never touched.
            packages = Directory.EnumerateDirectories(root)
                .Where(directory => Directory.Exists(Path.Combine(directory, "forge", "maps")))
                .Select(directory => Relative(root, directory))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Nothing; }
        if (packages.Length == 0) return Nothing;
        if (packages.Length > 1)
            return new AssemblyDiscovery(null, new AssemblyDiscoveryRejection(PackageLayoutCode, pluginsRelativeRoot),
                Array.Empty<AssemblyPlanDiagnostic>());

        var packagePath = packages[0];
        var packageDirectory = Path.Combine(root, Local(packagePath));
        var mapsDirectory = Path.Combine(packageDirectory, "forge", "maps");
        var mapsPath = Join(pluginsRelativeRoot, packagePath + "/" + MapsRelativePath);
        var descriptorsFile = Path.Combine(mapsDirectory, DescriptorsName);
        if (!File.Exists(descriptorsFile))
            return new AssemblyDiscovery(packagePath, new AssemblyDiscoveryRejection(PackageLayoutCode, mapsPath),
                Array.Empty<AssemblyPlanDiagnostic>());

        // The descriptor document is read once and shared by every plan of the package; it is validated per plan
        // as check #1 of the rejection order, exactly like the Python and C# fixture checkers.
        var descriptors = ReadBytes(descriptorsFile);
        string[] planFiles;
        try
        {
            planFiles = Directory.EnumerateFiles(mapsDirectory)
                .Where(file => Path.GetFileName(file).EndsWith(PlanSuffix, StringComparison.Ordinal))
                .Select(file => Relative(root, file))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new AssemblyDiscovery(packagePath, new AssemblyDiscoveryRejection(PackageLayoutCode, mapsPath),
                Array.Empty<AssemblyPlanDiagnostic>());
        }

        // A level layout carries exactly one plan, so the second file declaring the same `levelLayoutId` is the
        // duplicate; the first file keeps the registration even when its own document is rejected.
        var layouts = new Dictionary<long, string>();
        var plans = new List<AssemblyPlanDiagnostic>(planFiles.Length);
        foreach (var relative in planFiles)
            plans.Add(CheckPlan(Join(pluginsRelativeRoot, relative), Path.Combine(root, Local(relative)), descriptors, layouts));
        return new AssemblyDiscovery(packagePath, null, plans);
    }

    private static AssemblyPlanDiagnostic CheckPlan(string relativePath, string file, byte[]? descriptors,
        Dictionary<long, string> layouts)
    {
        var bytes = ReadBytes(file);
        if (bytes is null) return Reject(relativePath, null, "assembly.schema", RootErrorPath);

        JsonDocument document;
        try { document = JsonDocument.Parse(bytes); }
        catch (JsonException) { return Reject(relativePath, null, "assembly.schema", RootErrorPath); }

        using (document)
        {
            string? planId = null;
            long? levelLayoutId = null;
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("planId", out var id) && id.ValueKind == JsonValueKind.String)
                    planId = id.GetString();
                if (document.RootElement.TryGetProperty("levelLayoutId", out var level)
                    && level.ValueKind == JsonValueKind.Number && level.TryGetInt64(out var parsed))
                    levelLayoutId = parsed;
            }

            // File identity first, then the per-document rejection order of section 3.2: the two package-level
            // codes below are not part of that order and are checked in the same sequence as both checkers.
            if (planId is null || Path.GetFileName(file) != planId + PlanSuffix)
                return Reject(relativePath, planId, PlanFileCode, relativePath);
            if (levelLayoutId is not null && layouts.ContainsKey(levelLayoutId.Value))
                return Reject(relativePath, planId, DuplicateLevelLayoutCode, relativePath);
            if (levelLayoutId is not null) layouts[levelLayoutId.Value] = relativePath;
            if (descriptors is null) return Reject(relativePath, planId, "descriptor.schema", DescriptorsErrorPath);
            try
            {
                return new AssemblyPlanDiagnostic(relativePath, planId, null, null,
                    AssemblyPlanChecks.Validate(document.RootElement, descriptors).Blockers);
            }
            catch (AssemblyPlanException error)
            {
                return Reject(relativePath, planId, error.Code, error.Path);
            }
        }
    }

    private static AssemblyPlanDiagnostic Reject(string path, string? planId, string code, string errorPath) =>
        new(path, planId, code, errorPath, Array.Empty<string>());

    private static byte[]? ReadBytes(string file)
    {
        try { return File.ReadAllBytes(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Local(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);
    private static string Join(string prefix, string relative) => prefix.Length == 0 ? relative : prefix + "/" + relative;
}
