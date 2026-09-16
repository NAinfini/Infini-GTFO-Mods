using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ForgeMap;
using BepInEx;

namespace ForgeMap.Native;

/// <summary>
/// The trigger zones this install authored, read once at plugin start from the package documents that declare them.
///
/// A package writes one document at `plugins/&lt;package&gt;/forge/trigger-zones.json` — the same one-level package
/// directory rule plans and the other authored content files use. The data is the author's own placement, so the
/// reader is strict and all-or-nothing per document: a document that does not parse is refused whole with one
/// diagnostic, never half-applied. Nothing here reads a native object: the volume is the exported data, which is
/// what makes every machine build the same zones and the same blocking colliders.
///
/// The snapshot is immutable once loaded: there is no watch and no reload, because two machines disagreeing about
/// the volume a player is standing in would be a different world on each of them.
/// </summary>
internal static class TriggerZoneData
{
    /// <summary>Cap on one discovered document, enforced on the file's own length before it is read.</summary>
    internal const int MaximumFileBytes = 256 * 1024;

    /// <summary>Every zone every package of this install declares, in ordinal document order and, inside one
    /// document, in the order the author wrote them. A zone id claimed by two documents is withdrawn whole: which
    /// volume an id names never depends on the order the files happened to be found in.</summary>
    internal static IReadOnlyList<TriggerZone> Load(string bepInExRoot, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        ArgumentNullException.ThrowIfNull(rejected);
        var parsed = new List<(string Relative, TriggerZone Zone)>();
        foreach (var (relative, path, length) in Discover(bepInExRoot))
        {
            if (length > MaximumFileBytes)
            {
                rejected(Diagnostic(relative, "trigger-zone-file-size", "The document is " + length + " bytes; the cap is " + MaximumFileBytes + "."));
                continue;
            }
            string text;
            try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                rejected(Diagnostic(relative, "trigger-zone-unreadable", error.Message));
                continue;
            }
            if (!TriggerZoneManifest.TryParse(text, out var zones, out var code, out var reason))
            {
                rejected(Diagnostic(relative, code!, reason!));
                continue;
            }
            foreach (var zone in zones!) parsed.Add((relative, zone));
        }
        var claims = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, zone) in parsed)
            claims[zone.Id] = claims.TryGetValue(zone.Id, out var count) ? count + 1 : 1;
        var loaded = new List<TriggerZone>(parsed.Count);
        var withdrawn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relative, zone) in parsed)
        {
            if (claims[zone.Id] == 1) { loaded.Add(zone); continue; }
            if (withdrawn.Add(zone.Id))
                rejected(Diagnostic(relative, "trigger-zone-duplicate-id", "Zone id `" + zone.Id + "` is claimed by "
                    + claims[zone.Id] + " documents; the zone is left out of this install."));
        }
        if (loaded.Count > TriggerZoneModule.MaximumZones)
        {
            rejected("trigger-zone-limit: this install declares " + loaded.Count + " zones; the cap is "
                + TriggerZoneModule.MaximumZones + ". No zone was loaded.");
            return Array.Empty<TriggerZone>();
        }
        return Array.AsReadOnly(loaded.ToArray());
    }

    /// <summary>The default install root, so the one caller that has no argument of its own names the same path the
    /// plan scanner does.</summary>
    internal static IReadOnlyList<TriggerZone> Load(Action<string> rejected) => Load(Paths.BepInExRootPath, rejected);

    /// <summary>Every `plugins/&lt;one directory&gt;/forge/trigger-zones.json`, ordinally sorted so one install always
    /// scans the same way. `plugins/` missing is not an error, and the directory is never recursed: a package
    /// directory is exactly one level deep, like the plan scanner's.</summary>
    private static List<(string Relative, string Path, long Length)> Discover(string bepInExRoot)
    {
        var hits = new List<(string Relative, string Path, long Length)>();
        var pluginsRoot = Path.Combine(bepInExRoot, "plugins");
        if (!Directory.Exists(pluginsRoot)) return hits;
        foreach (var pluginDir in Directory.EnumerateDirectories(pluginsRoot))
        {
            var document = Path.Combine(pluginDir, "forge", TriggerZoneManifest.FileNameOnly);
            if (!File.Exists(document)) continue;
            var relative = Path.GetRelativePath(bepInExRoot, document).Replace(Path.DirectorySeparatorChar, '/');
            long length;
            try { length = new FileInfo(document).Length; }
            catch (IOException) { continue; }
            hits.Add((relative, document, length));
        }
        hits.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        return hits;
    }

    /// <summary>One refusal in the shape every package-content diagnostic uses: the code, the document it came
    /// from and the first thing that could not be read.</summary>
    internal static string Diagnostic(string relative, string code, string reason)
        => code + " file=" + relative + ": " + reason;
}
