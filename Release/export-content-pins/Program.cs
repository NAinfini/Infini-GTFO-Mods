using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

// Writes the content pins of the Forge base release set: the game assembly the Runtime accepts, and the sha256 of
// every assembly each player package ships. The website copies this file verbatim into
// catalog/forge-release/content-pins.json, and the strict `gtfo-forge-loadout` policy locks an installation to it,
// so nothing here is guessed: the package rows come from Release/release.json, the game root and the packaging
// staging root are named on the command line, and a missing or unsupported file stops the run instead of writing a
// pin that is not true. A pin set is either complete or it does not exist.
if (!TryParse(args, out var options, out var usageError))
{
    Console.Error.WriteLine(usageError);
    return 2;
}
try
{
    return Export(options!);
}
catch (Exception error)
{
    Console.Error.WriteLine("export-content-pins: " + error.Message);
    return 1;
}

static bool TryParse(string[] argv, out Options? options, out string usageError)
{
    options = null;
    usageError = "usage: export-content-pins --release <Release/release.json> --packages-root <staging> "
        + "--game-root <GTFO root>|--bepinex-root <GTFO root/BepInEx> --runtime-manifest <runtime-manifest.json> "
        + "--output <content-pins.json>";
    string? release = null, packagesRoot = null, gameRoot = null, bepinexRoot = null, runtimeManifest = null, output = null;
    for (var index = 0; index < argv.Length; index += 2)
    {
        if (index + 1 >= argv.Length) { usageError = "missing value for " + argv[index]; return false; }
        switch (argv[index])
        {
            case "--release": release = argv[index + 1]; break;
            case "--packages-root": packagesRoot = argv[index + 1]; break;
            case "--game-root": gameRoot = argv[index + 1]; break;
            case "--bepinex-root": bepinexRoot = argv[index + 1]; break;
            case "--runtime-manifest": runtimeManifest = argv[index + 1]; break;
            case "--output": output = argv[index + 1]; break;
            default: usageError = "unknown argument " + argv[index]; return false;
        }
    }
    if (release == null || packagesRoot == null || runtimeManifest == null || output == null
        || gameRoot == null && bepinexRoot == null)
        return false;
    if (gameRoot != null && bepinexRoot != null)
    {
        usageError = "--game-root and --bepinex-root are mutually exclusive: name the GTFO root itself, or the BepInEx directory inside it.";
        return false;
    }
    // The BepInEx root is the game root's own subdirectory, so the game root is derived from it instead of guessed.
    if (gameRoot == null)
        gameRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bepinexRoot!)))
            ?? throw new InvalidDataException("--bepinex-root has no parent directory: " + bepinexRoot);
    options = new Options(Path.GetFullPath(release), Path.GetFullPath(packagesRoot), Path.GetFullPath(gameRoot),
        Path.GetFullPath(runtimeManifest), Path.GetFullPath(output));
    return true;
}

static int Export(Options options)
{
    using var release = JsonDocument.Parse(File.ReadAllBytes(options.ReleasePath));
    var repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(options.ReleasePath))
        ?? throw new InvalidDataException("release.json must live in <repository>/Release/.");
    var binding = ReadHostBinding(repoRoot);
    var manifestBuild = ReadManifestGameBuild(options.RuntimeManifestPath);
    // The build a plan is locked to and the build this pin names are the same number, and the host source is its only
    // source: a manifest exported for another build would pin an installation the host would refuse to start.
    if (manifestBuild != binding.GameBuild)
        throw new InvalidDataException("the runtime manifest is for game build " + manifestBuild
            + ", but the host binding is " + binding.GameBuild + ".");
    var gameAssembly = Path.Combine(options.GameRoot, "GameAssembly.dll");
    if (!File.Exists(gameAssembly))
        throw new FileNotFoundException("no GameAssembly.dll under the game root " + options.GameRoot
            + "; name the GTFO install directory (or its BepInEx directory) with --game-root/--bepinex-root.");
    var gameAssemblySha256 = Sha256(gameAssembly);
    // The host suspends itself on any other hash, so a pin that disagreed would describe a game Forge cannot run in.
    if (!gameAssemblySha256.Equals(binding.GameAssemblySha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("GameAssembly.dll is " + gameAssemblySha256 + ", but the host binding for build "
            + binding.GameBuild + " is " + binding.GameAssemblySha256.ToLowerInvariant() + ".");
    var plugins = new List<PluginPin>();
    foreach (var row in release.RootElement.GetProperty("packages").EnumerateArray())
    {
        // Only the packages a player installs are pinned; an author package is not part of any player install.
        if (row.GetProperty("audience").GetString() != "player") continue;
        var name = Required(row, "packageName");
        var declared = row.GetProperty("files").EnumerateArray().Select(file => file.GetString()!).ToArray();
        // The release identity puts the package's own assemblies first and its plugin assembly first of all, which is
        // what makes files[0] of this pin the assembly the loadout policy locks by plugin GUID.
        if (declared.Length == 0 || !IsAssembly(declared[0]))
            throw new InvalidDataException(name + ".files does not start with the package's own assembly; "
                + "the release identity lists the assembly that carries the plugin GUID first.");
        var files = declared.Where(IsAssembly).Select(relative =>
        {
            var staged = Path.Combine(options.PackagesRoot, name, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(staged))
                throw new FileNotFoundException("the packaging staging root has no " + name + "/" + relative
                    + " (" + staged + "); stage the package's declared files there before pinning them.");
            return new FilePin(relative, Sha256(staged));
        }).ToArray();
        plugins.Add(new PluginPin(name, Required(row, "pluginGuid"), Required(row, "version"), files));
    }
    if (plugins.Count == 0)
        throw new InvalidDataException("release.json declares no audience=player package to pin.");
    // Package rows are ordered by name, like every other list the release set publishes, so the same inputs produce
    // the same bytes no matter what order release.json or the staging directory happens to present them in.
    plugins.Sort(static (left, right) => string.CompareOrdinal(left.PackageName, right.PackageName));
    var pins = new ContentPins(new GameAssemblyPin(binding.GameBuild, gameAssemblySha256), plugins);
    File.WriteAllText(options.OutputPath, Serialize(pins), new UTF8Encoding(false));
    Console.WriteLine("Wrote " + options.OutputPath + ": game build " + binding.GameBuild + " " + gameAssemblySha256
        + ", " + plugins.Count + " player packages, " + plugins.Sum(package => package.Files.Length) + " assemblies.");
    return 0;
}

/// <summary>The build the host supports and the one GameAssembly hash it accepts, read from the host's own source:
/// both numbers live there and nowhere else, so a build bump cannot leave a stale copy in this tool.</summary>
static Binding ReadHostBinding(string repoRoot)
{
    var path = Path.Combine(repoRoot, "ForgeRuntime", "GameBindings", "GameRuntimeBridge.cs");
    if (!File.Exists(path)) throw new FileNotFoundException("no host source to read the supported game binding from: " + path);
    var text = File.ReadAllText(path);
    return new Binding(Constant(text, "GameBuild", path), Constant(text, "GameAssemblySha256", path));
}

static string Constant(string text, string name, string path)
{
    var match = Regex.Match(text, name + "\\s*=\\s*\"([^\"]+)\"");
    return match.Success
        ? match.Groups[1].Value
        : throw new InvalidDataException("no " + name + " constant in " + path);
}

static string ReadManifestGameBuild(string runtimeManifestPath)
{
    using var manifest = JsonDocument.Parse(File.ReadAllBytes(runtimeManifestPath));
    if (!manifest.RootElement.TryGetProperty("runtime", out var runtime) || !runtime.TryGetProperty("gameBuild", out var build)
        || build.GetString() is not { Length: > 0 } value)
        throw new InvalidDataException("the runtime manifest has no runtime.gameBuild: " + runtimeManifestPath);
    return value;
}

static string Required(JsonElement row, string field) =>
    row.TryGetProperty(field, out var value) && value.GetString() is { Length: > 0 } text
        ? text
        : throw new InvalidDataException("release.json has a package without " + field + ".");

static bool IsAssembly(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    using var sha256 = SHA256.Create();
    // Lowercase, because this is the form the website's loadout policy matches hashes in.
    return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
}

/// <summary>Same shape the release set's own writer uses: two-space indent, LF, one trailing newline, UTF-8 without
/// BOM. The bytes are copied into the website unchanged, so they must not depend on the machine that wrote them.</summary>
static string Serialize(ContentPins pins) =>
    JsonSerializer.Serialize(pins, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }).Replace("\r\n", "\n") + "\n";

internal sealed record Options(string ReleasePath, string PackagesRoot, string GameRoot, string RuntimeManifestPath, string OutputPath);
internal sealed record Binding(string GameBuild, string GameAssemblySha256);
internal sealed record GameAssemblyPin(string GameBuild, string Sha256);
internal sealed record FilePin(string Path, string Sha256);
internal sealed record PluginPin(string PackageName, string PluginGuid, string Version, FilePin[] Files);
internal sealed record ContentPins(GameAssemblyPin GameAssembly, List<PluginPin> Plugins);
