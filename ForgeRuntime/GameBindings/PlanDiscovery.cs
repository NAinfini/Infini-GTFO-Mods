using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ForgeRuntime.Framework;

namespace ForgeRuntime.GameBindings;

/// <summary>I-PACK plan discovery. A pure function of the BepInEx root: it only touches the filesystem it is
/// given, so tests can point it at a temp directory instead of a real BepInEx install. The caller decides whether to
/// scan at all (the Runtime.Mode gate lives in <see cref="GameRuntimeBridge"/>, since Off mode never reaches this far);
/// this type only knows how to enumerate and read `.plan.json` candidates once asked to.</summary>
internal static class PlanDiscovery
{
    /// <summary>Combined cap across every discovered file (distinct from the kernel's own 128-plan cap, which applies
    /// after parsing).</summary>
    internal const int MaximumFileCount = 256;
    internal const long MaximumCombinedBytes = 64L * 1024 * 1024;

    internal static IReadOnlyList<PlanCandidate> Scan(string bepInExRoot) => Scan(bepInExRoot, MaximumFileCount, MaximumCombinedBytes);

    /// <summary>Overload used by tests to exercise the combined-budget tail-rejection rule without writing 64 MiB of
    /// fixtures to disk. Production always goes through the single-argument overload above.</summary>
    internal static IReadOnlyList<PlanCandidate> Scan(string bepInExRoot, int maximumFileCount, long maximumCombinedBytes)
    {
        var pluginsRoot = Path.Combine(bepInExRoot, "plugins");
        if (!Directory.Exists(pluginsRoot)) return Array.Empty<PlanCandidate>();
        var hits = new List<(string Relative, long Length)>();
        foreach (var pluginDir in Directory.EnumerateDirectories(pluginsRoot))
        {
            var plansDir = Path.Combine(pluginDir, "forge", "plans");
            if (!Directory.Exists(plansDir)) continue; // Silent skip: no read, no error, no log for packages without plans.
            foreach (var file in Directory.EnumerateFiles(plansDir))
            {
                if (!file.EndsWith(".plan.json", StringComparison.Ordinal)) continue; // Other files are silently ignored; plans/ is not recursed.
                string relative = Path.GetRelativePath(bepInExRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                long length;
                try { length = new FileInfo(file).Length; }
                catch (IOException) { continue; } // Vanished between enumeration and stat; nothing to report.
                hits.Add((relative, length));
            }
        }
        hits.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));

        // Per-file cap first: an oversized file is rejected on its own and never counts toward the combined budget below.
        var underBudget = new List<int>(hits.Count);
        for (int i = 0; i < hits.Count; i++)
            if (hits[i].Length <= FrameworkFiles.MaximumPlanBytes) underBudget.Add(i);

        // Combined budget is tail-first: while the remaining set still exceeds the file-count or byte cap, drop the
        // ordinally-last remaining file and recheck. A file that survives this pass is never displaced by a later,
        // smaller one — unlike a forward greedy scan, which could accept a later file after an earlier one overflowed.
        long combinedBytes = 0; foreach (var index in underBudget) combinedBytes += hits[index].Length;
        var overBudget = new HashSet<int>();
        int survivingCount = underBudget.Count;
        while (survivingCount > maximumFileCount || combinedBytes > maximumCombinedBytes)
        {
            survivingCount--;
            int index = underBudget[survivingCount];
            combinedBytes -= hits[index].Length;
            overBudget.Add(index);
        }

        var candidates = new List<PlanCandidate>(hits.Count);
        for (int i = 0; i < hits.Count; i++)
        {
            var (relative, length) = hits[i];
            if (length > FrameworkFiles.MaximumPlanBytes)
            {
                candidates.Add(PlanCandidate.Rejected(relative, "json-size", "Plan file exceeds 4 MiB."));
                continue;
            }
            if (overBudget.Contains(i))
            {
                candidates.Add(PlanCandidate.Rejected(relative, "plan-budget", "Combined discovery budget (256 files / 64 MiB) exceeded."));
                continue;
            }
            string absolute;
            try { absolute = FrameworkFiles.Resolve(bepInExRoot, relative); }
            catch (InvalidDataException ex)
            {
                // Link/escape checks only run once the plans/ chain is known to exist, and only on this chain and file.
                candidates.Add(PlanCandidate.Rejected(relative, "plan-path", ex.Message));
                continue;
            }
            try
            {
                using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                candidates.Add(PlanCandidate.Loaded(relative, reader.ReadToEnd()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                candidates.Add(PlanCandidate.Rejected(relative, "invalid-json", ex.Message));
            }
        }
        return candidates;
    }
}
