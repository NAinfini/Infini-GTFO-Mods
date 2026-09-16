using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ForgeDevelopment.Native;

var failures = 0;
var checks = 0;
var root = Path.Combine(Path.GetTempPath(), "forge-runtime-project-checks-" + Guid.NewGuid().ToString("N"));
var reports = Path.Combine(root, "reports");
Directory.CreateDirectory(reports);

void Check(bool condition, string message)
{
    checks++;
    if (condition) return;
    failures++;
    Console.Error.WriteLine("FAIL: " + message);
}

bool SnapshotRunning() => ProjectChecks.SourceSnapshotRunning;

void WaitForSources() => Check(
    SpinWait.SpinUntil(() => !SnapshotRunning(), TimeSpan.FromSeconds(30)),
    "background source snapshot finishes within the test timeout");

void ResetEnvironment()
{
    ProjectChecks.CancelSourceSnapshot("Test environment reset.");
    if (!SpinWait.SpinUntil(() => !SnapshotRunning(), TimeSpan.FromSeconds(30)))
        throw new TimeoutException("Previous source snapshot did not stop.");
    Paths.BepInExRootPath = root;
    Paths.ConfigPath = Path.Combine(root, "config");
    Paths.PluginPath = Path.Combine(root, "plugins");
    ClearDirectory(Paths.ConfigPath);
    ClearDirectory(Paths.PluginPath);
    Directory.CreateDirectory(Paths.ConfigPath);
    Directory.CreateDirectory(Paths.PluginPath);
    WriteBaselineSources();
    IL2CPPChainloader.Instance.Plugins.Clear();
    Settings.ProjectManifest.Value = "";
}

JsonDocument Export(DiagnosticsReport report, string name) =>
    JsonDocument.Parse(File.ReadAllText(report.Export(Path.Combine(reports, name + ".json"), "tested")));

string WriteManifest(string name, object value)
{
    var path = Path.Combine(root, name);
    File.WriteAllText(path, JsonSerializer.Serialize(value));
    Settings.ProjectManifest.Value = name;
    return path;
}

string WriteRawManifest(string name, string text)
{
    var path = Path.Combine(root, name);
    File.WriteAllText(path, text, new UTF8Encoding(false));
    Settings.ProjectManifest.Value = name;
    return path;
}

string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

bool HasCheck(JsonElement document, string kind, string status, string? subject = null) =>
    document.GetProperty("checks").EnumerateArray().Any(entry =>
        entry.GetProperty("kind").GetString() == kind &&
        entry.GetProperty("status").GetString() == status &&
        (subject == null || entry.GetProperty("subject").GetString() == subject));

JsonElement References(JsonElement document) => document.GetProperty("objectReferences");

string Verification(JsonElement document) => References(document).GetProperty("sourceVerification").GetString()!;

string ScanStatus(JsonElement document) => References(document).GetProperty("scanStatus").GetString()!;

string? Metadata(JsonElement document, string key) =>
    document.GetProperty("metadata").TryGetProperty(key, out var value) ? value.GetString() : null;

// An empty reference array still exercises the authoritative parser without declaring a target.
object[] NoReferences() => Array.Empty<object>();

string WriteSource(string relative, string content)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    return path;
}

void WriteBaselineSources()
{
    WriteSource("config/source.cfg", "value=42\n");
    WriteSource("plugins/LGTuner/layouts/layout.jsonc",
        "{\n  // LGTuner tolerates comments and trailing commas\n  \"LevelLayoutID\": 10,\n  \"TileOverrides\": [{ \"a\": 1 },],\n}\n");
}

void ClearDirectory(string path)
{
    if (!Directory.Exists(path)) return;
    foreach (var entry in Directory.EnumerateFileSystemEntries(path))
    {
        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
        else File.Delete(entry);
    }
}

static object Zone(string expedition, string author, uint layout, int localIndex) => new
{
    expeditionId = expedition,
    kind = "zone",
    authorId = author,
    locator = new { kind = "zone", layoutId = layout, dimension = 0, layer = 0, localIndex }
};

WriteBaselineSources();
var sourceHash = Hash(Path.Combine(root, "config", "source.cfg"));
var tunerHash = Hash(Path.Combine(root, "plugins", "LGTuner", "layouts", "layout.jsonc"));

var dependencies = "resource/room-a@aaaa,resource/room-b@bbbb";

object ValidManifest(string projectId) => new
{
    format = "gtfo-forge-project",
    projectId,
    experiment = new
    {
        packageVersion = "1.2.3",
        authoringSha256 = new string('a', 64),
        dependencies
    },
    requiredPlugins = new[] { new { guid = "mod.present", version = "1.2.0" } },
    sources = new[] { new { path = "config/source.cfg", sha256 = sourceHash, kind = "DataBlock" } },
    objectReferences = new object[] { Zone("expedition-a", "zone-author", 10, 1) }
};

void WriteSourceOnlyManifest(string name, string projectId, object sources, string dependencies = "resource/room-a@aaaa", object? requiredPlugins = null) => WriteManifest(name, new
{
    format = "gtfo-forge-project",
    projectId,
    experiment = new { packageVersion = "1.0.0", authoringSha256 = new string('a', 64), dependencies },
    requiredPlugins = requiredPlugins ?? Array.Empty<object>(),
    sources,
    objectReferences = NoReferences()
});

try
{
    D1InspectionReviewTests.Run(Check, reports);
    // A complete manifest: every field is published, sources are verified, references are attached.
    ResetEnvironment();
    IL2CPPChainloader.Instance.Plugins["mod.present"] = new PluginInfo
    {
        Metadata = new() { GUID = "mod.present", Name = "Present Mod", Version = new SemanticVersioning.Version("1.2.0") }
    };
    var manifestPath = WriteManifest("valid.json", ValidManifest("validation-project"));
    var valid = new DiagnosticsReport("valid");
    var validScan = ProjectChecks.Load(valid, 7, 11);
    WaitForSources();
    using (var json = Export(valid, "valid"))
    {
        var document = json.RootElement;
        Check(Metadata(document, "projectId") == "validation-project", "projectId is published only after the whole manifest is accepted");
        Check(Metadata(document, "projectManifestHash") == Hash(manifestPath), "project manifest hash is the sha256 of the exact manifest bytes that were parsed");
        Check(Metadata(document, "experiment:packageVersion") == "1.2.3", "experiment.packageVersion is published");
        Check(Metadata(document, "experiment:authoringSha256") == new string('a', 64), "experiment.authoringSha256 is published");
        Check(Metadata(document, "experiment:dependencies") == dependencies, "the exporter's dependency revision string is published verbatim");
        Check(HasCheck(document, "project_manifest", "parsed", "configuration"), "an accepted manifest reports parsed");
        Check(HasCheck(document, "dependency_version", "version_satisfied", "mod.present"), "an installed plugin exactly matching the required version is satisfied");
        Check(HasCheck(document, "source_verification", "matched", "config/source.cfg"), "a declared source that matches its authored hash is matched");
        var receipt = References(document);
        Check(receipt.GetProperty("worldEpoch").GetInt64() == 7, "the reference receipt carries the caller world epoch");
        Check(receipt.GetProperty("simulationTick").GetInt64() == 11, "the reference receipt carries the caller simulation tick");
        Check(receipt.GetProperty("groups").GetArrayLength() == 1, "the declared object reference is published as one group");
        Check(receipt.GetProperty("groups")[0].GetProperty("authorId").GetString() == "zone-author", "the published group is the declared reference");
        Check(Verification(document) == "matched", "a fully matching run publishes sourceVerification=matched");
        Check(Metadata(document, "sourceCoverageStatus") == "complete", "sampling coverage is reported separately from verification");
        Check(HasCheck(document, "source_coverage", "complete", "files"), "bounded sampling reports complete coverage");
        Check(document.GetProperty("events").EnumerateArray().Any(entry =>
                entry.GetProperty("category").GetString() == "source" && entry.GetProperty("stage").GetString() == "file_snapshot" &&
                entry.GetProperty("subject").GetString() == "config/source.cfg" && entry.GetProperty("fields").GetProperty("sha256").GetString() == sourceHash),
            "declared sources are sampled with a lowercase sha256");
        Check(document.GetProperty("events").EnumerateArray().Any(entry =>
                entry.GetProperty("stage").GetString() == "file_snapshot" && entry.GetProperty("subject").GetString() == "plugins/LGTuner/layouts/layout.jsonc"),
            "automatic discovery still samples LGTuner jsonc inputs");
        Check(document.GetProperty("events").EnumerateArray().All(entry =>
                entry.GetProperty("stage").GetString() != "file_snapshot" ||
                entry.GetProperty("fields").GetProperty("sha256").GetString()!.All(character => character is not (>= 'A' and <= 'F'))),
            "every sampled sha256 is lowercase");
    }

    // The loader returns the same run-owned scan the report serializes, not a second receipt.
    Check(validScan != null, "accepted manifest returns its attached scan to the native observer");
    var previousExportHash = Hash(Path.Combine(reports, "valid.json"));
    validScan!.Start(new[] { new ProjectLayoutKey(10, 0, 0) }, 11);
    validScan.ObserveZone(new ProjectZoneCandidate(321, 10, 0, 0, 1));
    validScan.Complete(12);
    using (var json = Export(valid, "valid-observed"))
        Check(ScanStatus(json.RootElement) == "complete" &&
              References(json.RootElement).GetProperty("groups")[0].GetProperty("status").GetString() == "matched" &&
              References(json.RootElement).GetProperty("groups")[0].GetProperty("candidates")[0].GetProperty("instanceId").GetInt32() == 321,
            "observations through the returned scan update this report's receipt");
    Check(Hash(Path.Combine(reports, "valid.json")) == previousExportHash, "later observations cannot rewrite an earlier exported snapshot");

    // A missing required plugin fails without invalidating the manifest.
    ResetEnvironment();
    WriteManifest("missing-plugin.json", ValidManifest("missing-plugin"));
    var missing = new DiagnosticsReport("missing-plugin");
    ProjectChecks.Load(missing, 7, null);
    WaitForSources();
    using (var json = Export(missing, "missing-plugin"))
        Check(HasCheck(json.RootElement, "dependency_version", "failed", "mod.present"), "a missing required plugin fails explicitly");

    // I-RELEASE precise versions (D-018): a plan pins an exact base-package version, so an
    // installed plugin newer than the requirement must fail, not be treated as "satisfies a
    // minimum". Otherwise the site would report the dependency met while the Runtime's own plan
    // lock still rejects the plan for that exact same version mismatch.
    ResetEnvironment();
    IL2CPPChainloader.Instance.Plugins["mod.present"] = new PluginInfo
    {
        Metadata = new() { GUID = "mod.present", Name = "Present Mod", Version = new SemanticVersioning.Version("1.2.0") }
    };
    WriteSourceOnlyManifest("newer-installed.json", "newer-installed", Array.Empty<object>(),
        requiredPlugins: new object[] { new { guid = "mod.present", version = "1.1.0" } });
    var newerInstalled = new DiagnosticsReport("newer-installed");
    ProjectChecks.Load(newerInstalled, 7, null);
    WaitForSources();
    using (var json = Export(newerInstalled, "newer-installed"))
        Check(HasCheck(json.RootElement, "dependency_version", "failed", "mod.present"),
            "an installed plugin newer than the required exact version fails instead of being treated as satisfied");

    // Legacy and unknown shapes are rejected as a whole, never migrated.
    var rejects = new (string Name, string Text, string Why)[]
    {
        ("schema-version", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\",\"schemaVersion\":1}", "schemaVersion is rejected instead of migrated"),
        ("expected-objects", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\",\"expectedObjects\":[]}", "expectedObjects is rejected instead of migrated"),
        ("unknown-root", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\",\"notes\":\"x\"}", "an unknown root field rejects the manifest"),
        ("duplicate-root", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\",\"projectId\":\"q\"}", "a duplicate root field rejects the manifest"),
        ("missing-root", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\"}", "a missing root field rejects the manifest"),
        ("wrong-format", "{\"format\":\"gtfo-forge-project-other\",\"projectId\":\"p\"}", "another project format is rejected"),
        ("unknown-experiment", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\",\"experiment\":{\"packageVersion\":\"1\",\"authoringSha256\":\"" + new string('a', 64) + "\",\"dependencies\":[],\"channel\":\"beta\"}}", "an unknown experiment field rejects the manifest"),
        ("comments", "{\"format\":\"gtfo-forge-project\",/*note*/\"projectId\":\"p\"}", "JSON comments are rejected"),
        ("trailing-comma", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\",}", "trailing commas are rejected"),
        ("deep", "{\"format\":" + new string('[', 40) + new string(']', 40) + "}", "JSON depth beyond 32 is rejected"),
        ("dependencies-array", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\",\"experiment\":{\"packageVersion\":\"1.0.0\",\"authoringSha256\":\"" + new string('a', 64) + "\",\"dependencies\":[{\"id\":\"room-a\",\"revision\":\"" + new string('b', 64) + "\"}]}}", "an array experiment.dependencies is rejected instead of being migrated"),
        ("dependencies-null", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\",\"experiment\":{\"packageVersion\":\"1.0.0\",\"authoringSha256\":\"" + new string('a', 64) + "\",\"dependencies\":null}}", "a null experiment.dependencies is rejected"),
        ("projectid-blank", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"   \"}", "a blank projectId is rejected"),
        ("projectid-long", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"" + new string('p', 129) + "\"}", "a projectId beyond 128 characters is rejected"),
        ("projectid-control", "{\"format\":\"gtfo-forge-project\",\"projectId\":\"p\\u0001q\"}", "a projectId with control characters is rejected")
    };
    foreach (var (name, text, why) in rejects)
    {
        ResetEnvironment();
        WriteRawManifest(name + ".json", text);
        var rejected = new DiagnosticsReport(name);
        ProjectChecks.Load(rejected, 7, null);
        using var json = Export(rejected, name);
        var document = json.RootElement;
        Check(HasCheck(document, "project_manifest", "failed", "configuration"), why);
        Check(Metadata(document, "projectId") == null, "a rejected manifest publishes no projectId: " + name);
        Check(!document.GetProperty("checks").EnumerateArray().Any(entry => entry.GetProperty("kind").GetString() == "source_verification"),
            "a rejected manifest verifies no source: " + name);
        Check(Verification(document) == "unavailable" && ScanStatus(document) == "rejected",
            $"a rejected manifest publishes an empty, rejected, source-unavailable receipt: {name} (scan={ScanStatus(document)} verification={Verification(document)})");
        Check(References(document).GetProperty("groups").GetArrayLength() == 0, "a rejected manifest publishes no object references: " + name);
    }

    // A rejected manifest publishes nothing at all: neither into a run of its own nor over the
    // receipt of the run that is still loaded.
    ResetEnvironment();
    WriteManifest("seed-valid.json", ValidManifest("seed"));
    var seed = new DiagnosticsReport("seed");
    ProjectChecks.Load(seed, 7, null);
    WaitForSources();
    WriteManifest("seed-bad.json", new { format = "gtfo-forge-project", projectId = "seed-bad", schemaVersion = 1 });
    var rejectedReport = new DiagnosticsReport("seed-bad");
    var rejectedScan = ProjectChecks.Load(rejectedReport, 8, null);
    Check(rejectedScan?.Snapshot().ScanStatus == ProjectScanStatus.Rejected, "rejected manifest returns the actual rejected scan");
    using (var json = Export(rejectedReport, "seed-bad"))
    {
        var document = json.RootElement;
        var manifestChecks = document.GetProperty("checks").EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == "project_manifest").ToArray();
        var receipt = References(document);
        Check(HasCheck(document, "project_manifest", "failed", "configuration"), "the second manifest is rejected");
        Check(manifestChecks.Length == 1 && manifestChecks[0].GetProperty("status").GetString() == "failed",
            "a rejected manifest writes exactly one failed check and no parsed check");
        Check(Metadata(document, "projectId") == null && Metadata(document, "projectManifestHash") == null &&
              Metadata(document, "experiment:packageVersion") == null,
            "a rejected manifest publishes no half-read metadata of its own");
        Check(!document.GetProperty("checks").EnumerateArray().Any(entry => entry.GetProperty("kind").GetString() == "source_verification"),
            "a rejected manifest verifies no source");
        Check(receipt.GetProperty("groups").GetArrayLength() == 0 && receipt.GetProperty("worldEpoch").GetInt64() == 8 &&
              ScanStatus(document) == "rejected" && Verification(document) == "unavailable",
            "a rejected run publishes an empty rejected receipt at its own epoch");
    }
    using (var json = Export(seed, "seed-still-loaded"))
    {
        var document = json.RootElement;
        Check(Metadata(document, "projectId") == "seed" && HasCheck(document, "project_manifest", "parsed", "configuration"),
            "an accepted run keeps its own metadata and parsed check after a later rejection");
        Check(References(document).GetProperty("worldEpoch").GetInt64() == 7 && Verification(document) == "matched",
            "an accepted run keeps its own epoch and matched sources after a later rejection");
        Check(HasCheck(document, "source_verification", "matched", "config/source.cfg"),
            "a later rejection does not rewrite the accepted run's source checks");
    }

    // The 4 MiB manifest limit is a byte limit, not a parse result. The padded whitespace sits
    // inside the last string of an otherwise valid document, so a missing size guard would parse.
    ResetEnvironment();
    var boundary = "{\"format\":\"gtfo-forge-project\",\"projectId\":\"boundary\"," +
        "\"experiment\":{\"packageVersion\":\"1.0.0\",\"authoringSha256\":\"" + new string('a', 64) + "\",\"dependencies\":\"\"}," +
        "\"requiredPlugins\":[],\"sources\":[],\"objectReferences\":[]}";
    var atLimit = boundary + new string(' ', 4 * 1024 * 1024 - Encoding.UTF8.GetByteCount(boundary));
    Check(Encoding.UTF8.GetByteCount(atLimit) == 4 * 1024 * 1024, "the boundary fixture is exactly 4 MiB");
    WriteRawManifest("at-limit.json", atLimit);
    var accepted = new DiagnosticsReport("at-limit");
    ProjectChecks.Load(accepted, 7, null);
    using (var json = Export(accepted, "at-limit"))
        Check(Metadata(json.RootElement, "projectId") == "boundary" && !HasCheck(json.RootElement, "project_manifest", "failed", "configuration"),
            $"a manifest of exactly 4 MiB is read (id={Metadata(json.RootElement, "projectId")}, issues={string.Join(";", json.RootElement.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("message").GetString()))})");
    // An oversized document is rejected for its size alone, not for its shape.
    WriteRawManifest("over-limit.json", atLimit + new string(' ', 1024 * 1024));
    var oversized = new DiagnosticsReport("over-limit");
    ProjectChecks.Load(oversized, 7, null);
    using (var json = Export(oversized, "over-limit"))
    {
        var document = json.RootElement;
        Check(HasCheck(document, "project_manifest", "failed", "configuration"), "a manifest above 4 MiB is rejected before parsing");
        Check(document.GetProperty("issues").EnumerateArray().Any(issue =>
                issue.GetProperty("type").GetString() == "project_manifest_invalid" && issue.GetProperty("message").GetString()!.Contains("4 MiB")),
            "the manifest size rejection names the 4 MiB limit");
        Check(Verification(document) == "unavailable", "an oversized manifest publishes an unavailable receipt");
    }

    // Source paths are canonical authored identities.
    var pathRejects = new (string Name, string Why, Func<object> Sources)[]
    {
        ("traversal", "parent directory traversal is rejected",
            () => new object[] { new { path = "../outside.cfg", sha256 = sourceHash, kind = "DataBlock" } }),
        ("backslash", "a backslash source path is rejected",
            () => new object[] { new { path = "config\\source.cfg", sha256 = sourceHash, kind = "DataBlock" } }),
        ("absolute", "an absolute source path is rejected",
            () => new object[] { new { path = "/config/source.cfg", sha256 = sourceHash, kind = "DataBlock" } }),
        ("drive", "a drive-qualified source path is rejected",
            () => new object[] { new { path = "C:/config/source.cfg", sha256 = sourceHash, kind = "DataBlock" } }),
        ("current", "a current-directory segment is rejected",
            () => new object[] { new { path = "./config/source.cfg", sha256 = sourceHash, kind = "DataBlock" } }),
        ("empty-segment", "an empty source path segment is rejected",
            () => new object[] { new { path = "config//source.cfg", sha256 = sourceHash, kind = "DataBlock" } }),
        ("duplicate", "a case-insensitive duplicate source path is rejected",
            () => new object[]
            {
                new { path = "config/source.cfg", sha256 = sourceHash, kind = "DataBlock" },
                new { path = "CONFIG/Source.cfg", sha256 = sourceHash, kind = "DataBlock" }
            }),
        ("unknown-kind", "an unsupported source kind is rejected",
            () => new object[] { new { path = "config/source.cfg", sha256 = sourceHash, kind = "Script" } })
    };
    foreach (var (name, why, sources) in pathRejects)
    {
        ResetEnvironment();
        WriteSourceOnlyManifest(name + ".json", "paths", sources());
        var report = new DiagnosticsReport(name);
        ProjectChecks.Load(report, 7, null);
        using var json = Export(report, name);
        Check(HasCheck(json.RootElement, "project_manifest", "failed", "configuration") && Metadata(json.RootElement, "projectId") == null, why);
    }

    // An uppercase authored hash is not a sha256 in this contract.
    ResetEnvironment();
    WriteManifest("uppercase.json", new
    {
        format = "gtfo-forge-project",
        projectId = "uppercase",
        experiment = new { packageVersion = "1.0.0", authoringSha256 = new string('A', 64), dependencies = "resource/room-a@aaaa" },
        requiredPlugins = Array.Empty<object>(),
        sources = Array.Empty<object>(),
        objectReferences = NoReferences()
    });
    var uppercase = new DiagnosticsReport("uppercase");
    ProjectChecks.Load(uppercase, 7, null);
    using (var json = Export(uppercase, "uppercase"))
        Check(HasCheck(json.RootElement, "project_manifest", "failed", "configuration"), "an uppercase sha256 is rejected");

    // The unified boundary sizes.
    ResetEnvironment();
    var longProjectId = new string('p', 128);
    WriteManifest("project-id-limit.json", new
    {
        format = "gtfo-forge-project",
        projectId = longProjectId,
        experiment = new { packageVersion = "1.0.0", authoringSha256 = new string('a', 64), dependencies = "resource/room-a@aaaa" },
        requiredPlugins = Array.Empty<object>(),
        sources = Array.Empty<object>(),
        objectReferences = NoReferences()
    });
    var longId = new DiagnosticsReport("project-id-limit");
    ProjectChecks.Load(longId, 7, null);
    using (var json = Export(longId, "project-id-limit"))
        Check(Metadata(json.RootElement, "projectId") == longProjectId, "a projectId of exactly 128 characters is accepted");

    ResetEnvironment();
    var longGuid = new string('g', 128);
    WriteSourceOnlyManifest("guid-limit.json", "guid-limit", Array.Empty<object>(), requiredPlugins: new object[]
    {
        new { guid = longGuid, version = "1.0.0" },
        new { guid = new string('h', 129), version = "1.0.0" }
    });
    var guidLimit = new DiagnosticsReport("guid-limit");
    ProjectChecks.Load(guidLimit, 7, null);
    using (var json = Export(guidLimit, "guid-limit"))
        Check(HasCheck(json.RootElement, "project_manifest", "failed", "configuration"), "a plugin GUID beyond 128 characters is rejected");

    var versionRejects = new (string Name, string Version, string Why)[]
    {
        ("version-two-part", "1.2", "a two-part plugin version is rejected"),
        ("version-prerelease", "1.2.3-rc.1", "a prerelease plugin version is rejected"),
        ("version-prefixed", "v1.2.3", "a v-prefixed plugin version is rejected"),
        ("version-leading-zero", "01.2.3", "a leading-zero plugin version is rejected")
    };
    foreach (var (name, version, why) in versionRejects)
    {
        ResetEnvironment();
        WriteSourceOnlyManifest(name + ".json", "versions", Array.Empty<object>(),
            requiredPlugins: new object[] { new { guid = "mod.present", version = version } });
        var report = new DiagnosticsReport(name);
        ProjectChecks.Load(report, 7, null);
        using var json = Export(report, name);
        Check(HasCheck(json.RootElement, "project_manifest", "failed", "configuration"), why);
    }

    ResetEnvironment();
    WriteSourceOnlyManifest("plugin-count.json", "plugin-count", Array.Empty<object>(), requiredPlugins:
        Enumerable.Range(0, 129).Select(index => (object)new { guid = "mod." + index, version = "1.0.0" }).ToArray());
    var pluginCount = new DiagnosticsReport("plugin-count");
    ProjectChecks.Load(pluginCount, 7, null);
    using (var json = Export(pluginCount, "plugin-count"))
        Check(HasCheck(json.RootElement, "project_manifest", "failed", "configuration"), "more than 128 required plugins is rejected");

    ResetEnvironment();
    WriteSourceOnlyManifest("source-count.json", "source-count", Enumerable.Range(0, 257)
        .Select(index => (object)new { path = "config/bulk-" + index + ".cfg", sha256 = new string('a', 64), kind = "DataBlock" }).ToArray());
    var sourceCount = new DiagnosticsReport("source-count");
    ProjectChecks.Load(sourceCount, 7, null);
    using (var json = Export(sourceCount, "source-count"))
        Check(HasCheck(json.RootElement, "project_manifest", "failed", "configuration"), "more than 256 declared sources is rejected");

    // The exporter writes "" when a project has no dependencies.
    ResetEnvironment();
    WriteSourceOnlyManifest("empty-dependencies.json", "empty-dependencies", Array.Empty<object>(), dependencies: "");
    var emptyDependencies = new DiagnosticsReport("empty-dependencies");
    ProjectChecks.Load(emptyDependencies, 7, null);
    WaitForSources();
    using (var json = Export(emptyDependencies, "empty-dependencies"))
    {
        Check(Metadata(json.RootElement, "projectId") == "empty-dependencies", "an empty dependency string is accepted");
        Check(Metadata(json.RootElement, "experiment:dependencies") == "", "an empty dependency string is recorded verbatim");
    }

    // Source verification statuses.
    ResetEnvironment();
    WriteSourceOnlyManifest("mismatch.json", "mismatch",
        new object[] { new { path = "config/source.cfg", sha256 = new string('0', 64), kind = "ModConfig" } });
    var mismatched = new DiagnosticsReport("mismatch");
    ProjectChecks.Load(mismatched, 7, null);
    WaitForSources();
    using (var json = Export(mismatched, "mismatch"))
    {
        var document = json.RootElement;
        Check(HasCheck(document, "source_verification", "mismatch", "config/source.cfg"), "a known hash difference is a mismatch");
        Check(Verification(document) == "mismatch", "a mismatch restricts the reference receipt to source_mismatch");
        Check(Metadata(document, "sourceCoverageStatus") == "complete", "a mismatch still reports sampling coverage separately");
    }

    // A declared source whose bytes cannot be read is unavailable, never matched.
    ResetEnvironment();
    var unreadable = WriteSource("config/unreadable.cfg", "placeholder\n");
    File.Delete(unreadable);
    Directory.CreateDirectory(unreadable);
    WriteSourceOnlyManifest("unavailable.json", "unavailable",
        new object[] { new { path = "config/unreadable.cfg", sha256 = sourceHash, kind = "DataBlock" } });
    var unavailable = new DiagnosticsReport("unavailable");
    ProjectChecks.Load(unavailable, 7, null);
    WaitForSources();
    using (var json = Export(unavailable, "unavailable"))
    {
        var document = json.RootElement;
        Check(Verification(document) == "unavailable", "an unreadable declared source makes verification unavailable");
        Check(HasCheck(document, "source_verification", "unavailable", "config/unreadable.cfg"),
            "an unreadable declared source is reported unavailable, never matched");
        Check(document.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("type").GetString() == "source_hash_failed"),
            "an unreadable declared source is reported explicitly");
    }

    // A source root that cannot be sampled is a coverage limit, not a verification failure of the
    // declared file that was still readable.
    ResetEnvironment();
    Paths.ConfigPath = Path.Combine(root, "config", "unused-scope");
    Check(!Directory.Exists(Paths.ConfigPath), "the unavailable source root cannot be enumerated as a directory");
    WriteSourceOnlyManifest("unsampled-root.json", "unsampled-root",
        new object[] { new { path = "config/source.cfg", sha256 = sourceHash, kind = "DataBlock" } });
    var unsampledRoot = new DiagnosticsReport("unsampled-root");
    ProjectChecks.Load(unsampledRoot, 7, null);
    WaitForSources();
    using (var json = Export(unsampledRoot, "unsampled-root"))
    {
        var document = json.RootElement;
        Check(Metadata(document, "sourceCoverageStatus") == "partial", "an unsampled source root is reported as partial coverage");
        Check(Verification(document) == "matched" && HasCheck(document, "source_verification", "matched", "config/source.cfg"),
            "a declared source that was read and matched is not downgraded by an unsampled root");
        Check(document.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("type").GetString() == "source_hash_failed"),
            "an unsampled source root is reported explicitly");
    }

    // Cancellation is terminal for the run that owns the request.
    ResetEnvironment();
    WriteManifest("cancelled.json", ValidManifest("cancelled"));
    var cancelled = new DiagnosticsReport("cancelled");
    ProjectChecks.Load(cancelled, 7, null);
    ProjectChecks.CancelSourceSnapshot("Test cancellation.");
    WaitForSources();
    using (var json = Export(cancelled, "cancelled"))
    {
        var document = json.RootElement;
        Check(Verification(document) == "cancelled", "a cancelled run publishes sourceVerification=cancelled");
        Check(HasCheck(document, "source_coverage", "cancelled", "files"), "a cancelled run records cancelled coverage");
        Check(!HasCheck(document, "source_coverage", "complete", "files"), "a cancelled run cannot later report successful coverage");
        Check(!HasCheck(document, "source_verification", "matched", "config/source.cfg"), "a cancelled run cannot report a matched source");
    }

    // A superseded late worker cannot write the next run.
    ResetEnvironment();
    var bulk = Path.Combine(Paths.ConfigPath, "late");
    Directory.CreateDirectory(bulk);
    for (var i = 0; i < 256; i++) File.WriteAllText(Path.Combine(bulk, "bulk-" + i.ToString("D3") + ".cfg"), new string('x', 64 * 1024));
    var firstManifest = WriteManifest("first.json", ValidManifest("first-run"));
    var first = new DiagnosticsReport("late-first");
    ProjectChecks.Load(first, 7, null);
    WriteManifest("second.json", ValidManifest("second-run"));
    var second = new DiagnosticsReport("late-second");
    ProjectChecks.Load(second, 8, null);
    WaitForSources();
    using (var json = Export(second, "late-second"))
    {
        var document = json.RootElement;
        Check(Metadata(document, "projectId") == "second-run", "the second run publishes its own projectId");
        Check(Metadata(document, "projectManifestHash") == Hash(Path.Combine(root, "second.json")), "the second run hashes its own manifest bytes");
        Check(Metadata(document, "projectManifestHash") != Hash(firstManifest), "the second run cannot inherit the first run's manifest hash");
        Check(Verification(document) == "matched" && HasCheck(document, "source_verification", "matched", "config/source.cfg"),
            "the second run verifies its own sources");
    }
    using (var json = Export(second, "late-second-settled"))
        Check(Metadata(json.RootElement, "projectId") == "second-run" && Verification(json.RootElement) == "matched",
            "a settled run keeps its receipt stable across exports");
    using (var json = Export(first, "late-first"))
    {
        var document = json.RootElement;
        Check(Metadata(document, "projectId") == "first-run", "the superseded run keeps its own identity");
        Check(Metadata(document, "projectManifestHash") == Hash(firstManifest), "the superseded run keeps its own manifest hash");
        Check(Verification(document) != "matched", "the later worker cannot verify the superseded run");
    }

    // A manifest without declared sources cannot claim authored verification.
    ResetEnvironment();
    WriteSourceOnlyManifest("no-sources.json", "no-sources", Array.Empty<object>());
    var noSources = new DiagnosticsReport("no-sources");
    ProjectChecks.Load(noSources, 7, null);
    WaitForSources();
    using (var json = Export(noSources, "no-sources"))
    {
        var document = json.RootElement;
        Check(Verification(document) == "not_provided", "a manifest without declared sources reports not_provided (actual=" + Verification(document) + ")");
        Check(!document.GetProperty("checks").EnumerateArray().Any(entry => entry.GetProperty("kind").GetString() == "source_verification"),
            "discovered files never become authored source verification");
        Check(HasCheck(document, "source_coverage", "complete", "files"),
            "discovery still reports sampling coverage: " + string.Join("|", document.GetProperty("checks").EnumerateArray()
                .Where(entry => entry.GetProperty("kind").GetString() == "source_coverage").Select(entry => entry.GetProperty("status").GetString() + ":" + entry.GetProperty("detail").GetString())));
    }

    // An absent manifest publishes nothing and attaches no receipt.
    ResetEnvironment();
    Settings.ProjectManifest.Value = "";
    var absent = new DiagnosticsReport("manifest-absent");
    Check(ProjectChecks.Load(absent, 7, null) == null, "unconfigured project returns no authored project scan");
    using (var json = Export(absent, "manifest-absent"))
    {
        var document = json.RootElement;
        Check(Metadata(document, "projectManifest") == "none", "an unconfigured manifest is recorded as absent");
        Check(!document.GetProperty("checks").EnumerateArray().Any(entry =>
                entry.GetProperty("kind").GetString() is "project_manifest" or "source_verification" or "source_coverage"),
            "an absent manifest makes no project, source or coverage claim");
    }

    ResetEnvironment();
    Settings.ProjectManifest.Value = "\0invalid";
    var invalidPath = new DiagnosticsReport("invalid-manifest-path");
    ProjectChecks.Load(invalidPath, 7, null);
    using (var json = Export(invalidPath, "invalid-manifest-path"))
    {
        var document = json.RootElement;
        Check(HasCheck(document, "project_manifest", "failed", "configuration"), "an invalid manifest path fails instead of escaping Load");
        Check(document.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("type").GetString() == "project_manifest_invalid"),
            "an invalid manifest path records a manifest issue");
        Check(Verification(document) == "unavailable" && ScanStatus(document) == "rejected", "an invalid manifest path publishes a rejected receipt");
    }

    // The independent JSONC LGTuner check is not manifest parsing.
    ResetEnvironment();
    WriteSourceOnlyManifest("tuner.json", "tuner",
        new object[] { new { path = "plugins/LGTuner/layouts/layout.jsonc", sha256 = tunerHash, kind = "LGTuner" } });
    var tuner = new DiagnosticsReport("tuner");
    ProjectChecks.Load(tuner, 7, null);
    WaitForSources();
    using (var json = Export(tuner, "tuner"))
    {
        var document = json.RootElement;
        Check(HasCheck(document, "lgtuner_layout", "input_snapshot", "10"), "a declared LGTuner source is still parsed as JSONC");
        Check(document.GetProperty("events").EnumerateArray().Any(entry =>
                entry.GetProperty("category").GetString() == "layout_request" && entry.GetProperty("subject").GetString() == "10/0"),
            "LGTuner layout requests are recorded as input snapshots");
        Check(HasCheck(document, "source_verification", "matched", "plugins/LGTuner/layouts/layout.jsonc"), "an authored LGTuner source is verified like any other source");
    }
}
finally
{
    ProjectChecks.CancelSourceSnapshot("Tests complete.");
    SpinWait.SpinUntil(() => !SnapshotRunning(), TimeSpan.FromSeconds(30));
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

Console.WriteLine($"Forge Runtime project checks: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
