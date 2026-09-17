using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Enemies;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the level's wave tuning: it reads the loaded level's own field out of the map data
/// document its package ships, and writes the numbers onto the two components the game keeps them on when a level
/// is there.
///
/// There is no handler and no registration here — the tuning is a field the level is loaded with, not a verb a
/// plan runs, and the row that used to exist (`forge.action.map.wave_tuning`) is deleted with it. The document is
/// `plugins/&lt;package&gt;/projects/rundown.json`, the map data document the website writes a package's level
/// data into; the tuning is `waveTuning` inside the map project of the level this process loaded. The separate
/// per-package `wave-tuning.json` is gone: one level's field belongs in that level's own map data, not in a file
/// that applies to every level of a package.
///
/// The level is selected by the document's own identity for it: the expedition whose map project names the loaded
/// level reference (`native.presetId`, the same `&lt;rundown&gt;:&lt;tier&gt;:&lt;index&gt;` spelling the plans'
/// `level` attachment uses), or — when the document carries exactly one expedition — that one, provided its own
/// `native.tier` is the loaded level's tier. A document that names several levels and matches none of them
/// applies nothing and says so; guessing which project is this level would apply one level's tuning to another.
///
/// The write goes through the components' own public members wherever one exists — `SetCooldownFactor`,
/// `RegisterType` and the static `AllowedTotalCost` property — so nothing here restates a rule the game already
/// implements. The tables that have no setter are assigned whole: a half-written table is a draw the author never
/// described, and the grammar has already refused every length but the game's own.
///
/// Both managers are host-side state: the machine that owns a wave is the one that draws the next type and
/// replicates the spawn, so a client applying the same numbers would write values nothing on that machine reads.
/// A level build where either singleton is still absent applies nothing and reports why; the next level load tries
/// again.
/// </summary>
internal static class LevelWaveTuning
{
    private const string PluginsDirectory = "plugins";
    private const string ProjectDirectory = "projects";
    private const string LevelDataFileName = "rundown.json";
    /// <summary>The map data document's own size cap. It carries a level's rooms, zones and authored native
    /// fields, so it is far larger than a tuning file was; a document past this is a data error rather than a
    /// level.</summary>
    private const int MaximumFileBytes = 16 * 1024 * 1024;

    /// <summary>Applies the loaded level's authored tuning to the level that is now there. Returns how many
    /// documents carried it, which is what the session's own log line reports.</summary>
    internal static int Apply(Action<string> report, string? levelReference)
    {
        ArgumentNullException.ThrowIfNull(report);
        var population = EnemyPopulationManager.Current;
        if (population == null || population.WasCollected)
        {
            report("map.wave-tuning-unavailable: the level's population manager is not there yet");
            return 0;
        }
        List<string> paths;
        try { paths = Discover(BepInEx.Paths.BepInExRootPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            report("map.wave-tuning-scan-failed: " + error.GetType().Name);
            return 0;
        }
        var applied = 0;
        foreach (var path in paths)
        {
            if (!TryReadLevelField(path, levelReference, out var tuning, out var refusedCode, out var reason))
            {
                report("map.wave-tuning-refused: " + path + ": " + refusedCode + ": " + reason);
                continue;
            }
            if (tuning == null) continue;
            try
            {
                Write(population, tuning);
                applied++;
            }
            catch (Exception error)
            {
                report("map.wave-tuning-apply-failed: " + path + ": " + error.GetType().Name);
            }
        }
        return applied;
    }

    /// <summary>Every `plugins/&lt;one directory&gt;/projects/rundown.json`, ordinally sorted so one install always
    /// applies the same way.</summary>
    private static List<string> Discover(string bepInExRoot)
    {
        var hits = new List<string>();
        var pluginsRoot = Path.Combine(bepInExRoot, PluginsDirectory);
        if (!Directory.Exists(pluginsRoot)) return hits;
        foreach (var packageDir in Directory.EnumerateDirectories(pluginsRoot))
        {
            var candidate = Path.Combine(packageDir, ProjectDirectory, LevelDataFileName);
            if (File.Exists(candidate)) hits.Add(candidate);
        }
        hits.Sort(string.CompareOrdinal);
        return hits;
    }

    /// <summary>The loaded level's tuning out of one map data document, or a null tuning when the document names
    /// no field for this level. A document that cannot be read at all is refused with one code.</summary>
    private static bool TryReadLevelField(string path, string? levelReference, out WaveTuningData? tuning,
        out string code, out string reason)
    {
        tuning = null;
        code = "";
        reason = "";
        if (!TryReadText(path, out var text, out code, out reason)) return false;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException error)
        {
            code = "wave-tuning-json";
            reason = error.Message;
            return false;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("expeditions", out var expeditions)
                || expeditions.ValueKind != JsonValueKind.Array)
            {
                code = "wave-tuning-document";
                reason = "the map data document carries no `expeditions` array.";
                return false;
            }
            if (!Match(expeditions, levelReference, out var project, out code, out reason)) return false;
            if (project.ValueKind != JsonValueKind.Object || !project.TryGetProperty(WaveTuningData.FieldName, out var field))
                return true;
            if (WaveTuningData.TryRead(field, out tuning, out var parsedCode, out var parsedReason)) return true;
            code = parsedCode!;
            reason = parsedReason!;
            return false;
        }
    }

    /// <summary>The map project of the loaded level, from the document's own expeditions. The reference is the
    /// loaded level's spelling; a document that carries exactly one expedition is that level's when its own
    /// declared tier agrees.</summary>
    private static bool Match(JsonElement expeditions, string? levelReference, out JsonElement project,
        out string code, out string reason)
    {
        project = default;
        code = "";
        reason = "";
        if (MapLevelReference.TryParse(levelReference) is not { } reference)
        {
            code = "wave-tuning-level-unknown";
            reason = "this process cannot spell the level it is in, so no project can be selected.";
            return false;
        }
        var rows = new List<JsonElement>(expeditions.GetArrayLength());
        foreach (var row in expeditions.EnumerateArray()) rows.Add(row);
        foreach (var row in rows)
        {
            if (!Project(row, out var candidate)) continue;
            if (Named(candidate, "presetId") is { } preset && string.Equals(preset, levelReference, StringComparison.Ordinal))
            {
                project = candidate;
                return true;
            }
        }
        if (rows.Count == 1 && Project(rows[0], out var only) && Named(only, "tier") is { Length: 1 } tier
            && tier[0] == reference.Tier)
        {
            project = only;
            return true;
        }
        code = "wave-tuning-level-absent";
        reason = "the document names no project for level " + levelReference + ".";
        return false;
    }

    /// <summary>One expedition's own map project.</summary>
    private static bool Project(JsonElement row, out JsonElement project)
    {
        project = default;
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("project", out var candidate)
            || candidate.ValueKind != JsonValueKind.Object) return false;
        project = candidate;
        return true;
    }

    /// <summary>One string member of a project's own `native` block, or null when it is absent or another
    /// kind.</summary>
    private static string? Named(JsonElement project, string member)
        => project.TryGetProperty("native", out var native) && native.ValueKind == JsonValueKind.Object
           && native.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>The document's text: the size cap, the byte read, the UTF-8 decode without a BOM, in that
    /// order.</summary>
    private static bool TryReadText(string path, out string text, out string code, out string reason)
    {
        text = "";
        code = "";
        reason = "";
        long length;
        try { length = new FileInfo(path).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            code = "wave-tuning-unreadable";
            reason = error.GetType().Name;
            return false;
        }
        if (length > MaximumFileBytes)
        {
            code = "wave-tuning-size";
            reason = "the document is " + length + " bytes; the cap is " + MaximumFileBytes + ".";
            return false;
        }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            code = "wave-tuning-unreadable";
            reason = error.GetType().Name;
            return false;
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            code = "wave-tuning-bom";
            reason = "the document starts with a UTF-8 BOM.";
            return false;
        }
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            code = "wave-tuning-utf8";
            reason = "the document is not valid UTF-8.";
            return false;
        }
        return true;
    }

    /// <summary>The writes themselves, each field only when the level named it, so two documents that tune
    /// different numbers both land.</summary>
    private static void Write(EnemyPopulationManager population, WaveTuningData document)
    {
        if (document.BaseWeights is { } weights)
            population.m_baseWeightTable = new Il2CppStructArray<float>(weights);
        if (document.HeatAtStart is { } heat)
            population.m_heatTable = new Il2CppStructArray<float>(heat);
        if (document.MaxHeat is { } maxHeat)
            population.m_maxHeat = maxHeat;
        if (document.HeatCooldownSpeed is { } speed)
            population.SetCooldownFactor(speed);
        var costs = EnemyCostManager.Current;
        if (document.TypeCostTowardsCap is { } perType && costs != null && !costs.WasCollected)
            costs.m_enemyTypeCosts = new Il2CppStructArray<float>(perType);
        // The cap itself is read through the game's own property rather than its backing field: the value the draw
        // compares against is whatever that property answers with.
        if (document.AllowedTotalCost is { } allowed)
            EnemyCostManager.AllowedTotalCost = allowed;
    }
}
