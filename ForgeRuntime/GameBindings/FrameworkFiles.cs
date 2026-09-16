using System;
using System.IO;
using System.Text;

namespace ForgeRuntime.GameBindings;

internal static class FrameworkFiles
{
    /// <summary>Cap on one discovered plan file; enforced by <see cref="PlanDiscovery"/> before it reads a candidate's bytes.</summary>
    internal const int MaximumPlanBytes = 4 * 1024 * 1024;
    internal static void WriteManifest(string root, string json)
    {
        const string relative = "ForgeRuntime/runtime-manifest.json";
        var path = Resolve(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        path = Resolve(root, relative);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Framework path must be relative to BepInEx.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Framework path escapes BepInEx.");
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Framework paths cannot traverse links or junctions.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }
}
