using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.LoadoutPolicy;

/// <summary>A private temp install whose bytes the suite controls: `plugins/&lt;package&gt;/forge/loadout.json`, the
/// files a policy pins, and the game assembly stand-in at the root. No real profile, no game assembly and no
/// loader is involved — the pin source is the one this class hands in, and it hashes exactly these fixture bytes.</summary>
internal sealed class PolicyInstall : IDisposable
{
    internal const uint Rundown = 12345;
    internal const string Project = "TestProject";
    internal static readonly uint[] StandardIds = { 10001, 10002, 10003, 10004 };
    internal static readonly uint[] SpecialIds = { 10005, 10006, 10007, 10008, 10009 };
    internal static readonly uint[] ClassIds = { 20001, 20002, 20003, 20004, 20005, 20006 };
    private const string MapGuid = "NAinfini.ForgeMap", RuntimeGuid = "NAinfini.ForgeRuntime";
    private readonly string _gameSha, _mapSha, _runtimeSha, _gearSha;
    private readonly Dictionary<string, string> _pluginFiles;

    internal PolicyInstall()
    {
        Root = Path.Combine(Path.GetTempPath(), "forge-loadout-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        GameAssemblyPath = Write("GameAssembly.dll", "game-assembly-fixture");
        _pluginFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MapGuid] = Write("plugin-map.dll", "map-plugin-fixture"),
            [RuntimeGuid] = Write("plugin-runtime.dll", "runtime-plugin-fixture")
        };
        GearSourcePath = Write("plugins/Pack/gear.json", "gear-fixture");
        _gameSha = Hash(GameAssemblyPath);
        _mapSha = Hash(_pluginFiles[MapGuid]);
        _runtimeSha = Hash(_pluginFiles[RuntimeGuid]);
        _gearSha = Hash(GearSourcePath);
    }

    internal string Root { get; }
    internal string GameAssemblyPath { get; }
    internal string GearSourcePath { get; }
    internal string GameSha => _gameSha;
    internal string MapSha => _mapSha;
    internal string RuntimeSha => _runtimeSha;
    internal string GearSha => _gearSha;

    /// <summary>The two pinned plugins, in the ordinal order the canonical form requires (Map before Runtime).</summary>
    internal (string Guid, string Sha)[] PinnedPlugins => new[] { (MapGuid, _mapSha), (RuntimeGuid, _runtimeSha) };
    internal (string Path, string Sha)[] PinnedSources => new[] { ("plugins/Pack/gear.json", _gearSha) };

    /// <summary>What production hands the loader, reduced to fixtures: the game assembly path, a GUID lookup and a
    /// file read. Nothing here knows a profile — which is the point of injecting it.</summary>
    internal LoadoutPinSource Pins() => new(GameAssemblyPath,
        guid => _pluginFiles.TryGetValue(guid, out var path) ? path : null,
        path => File.Exists(path) ? File.ReadAllBytes(path) : null);

    internal string Write(string relative, string content) => WriteBytes(relative, Encoding.UTF8.GetBytes(content));

    internal string WriteBytes(string relative, byte[] bytes)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Writes the canonical text as one package's policy file, bytes untouched: a suite case that needs a
    /// BOM, an indented document or a hand-built variant writes its own bytes with <see cref="WritePolicy"/>.</summary>
    internal void WritePolicy(string package, string text) => WritePolicy(package, Encoding.UTF8.GetBytes(text));

    internal void WritePolicy(string package, byte[] bytes) => WriteBytes("plugins/" + package + "/forge/loadout.json", bytes);

    /// <summary>The canonical policy text, `JSON.stringify(policy) + "\n"`, built here by hand rather than by the
    /// loader's own writer so the two are independent. Every argument defaults to this install's own pinned bytes
    /// and the suite's id sets; a case that wants another value passes it.</summary>
    internal string Canonical(uint rundownId = Rundown, string? gameSha = null, (string Guid, string Sha)[]? plugins = null,
        (string Path, string Sha)[]? sources = null, uint[]? standard = null, uint[]? special = null, uint[]? gearClass = null)
    {
        var text = new StringBuilder(1024);
        text.Append("{\"format\":\"gtfo-forge-loadout\",\"projectId\":\"").Append(Project)
            .Append("\",\"rundownId\":").Append(rundownId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"gameAssemblySha256\":\"").Append(gameSha ?? _gameSha).Append("\",\"plugins\":[");
        Join(text, plugins ?? PinnedPlugins, item => "{\"guid\":\"" + item.Guid + "\",\"sha256\":\"" + item.Sha + "\"}");
        text.Append("],\"sources\":[");
        Join(text, sources ?? PinnedSources, item => "{\"path\":\"" + item.Path + "\",\"sha256\":\"" + item.Sha + "\"}");
        text.Append("],\"slots\":{");
        Slot(text, "GearStandard", standard ?? StandardIds, first: true);
        Slot(text, "GearSpecial", special ?? SpecialIds, first: false);
        Slot(text, "GearClass", gearClass ?? ClassIds, first: false);
        text.Append("}}\n");
        return text.ToString();
    }

    /// <summary>Loads whatever the case has already written under `Root`, exactly as production would: the same
    /// root, the same injected pin source, one diagnostic line per refused file.</summary>
    internal LoadoutPolicySnapshot LoadWritten(out List<string> rejected)
    {
        var lines = new List<string>();
        var snapshot = LoadoutPolicyData.Load(Root, Pins(), lines.Add);
        rejected = lines;
        return snapshot;
    }

    /// <summary>The packet digest the suite's canonical text uses for one id: it only has to be a digest, because
    /// nothing matches on it.</summary>
    internal static string Packet(uint id) => Digest(Encoding.UTF8.GetBytes("packet-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string Hash(string path) => Digest(File.ReadAllBytes(path));

    /// <summary>A directory junction, which is the link kind a non-elevated Windows process can always create.
    /// False when this machine refuses it, in which case the case that needed it is skipped rather than failed.</summary>
    internal static bool TryJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using (var process = Process.Start(start))
        {
            process!.WaitForExit();
            if (process.ExitCode == 0 && Directory.Exists(link)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        try
        {
            // A junction is removed on its own link first: a recursive delete would otherwise walk into the target.
            foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
                if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(directory, false);
            Directory.Delete(Root, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void Join<T>(StringBuilder text, T[] items, Func<T, string> write)
    {
        for (int index = 0; index < items.Length; index++)
        {
            if (index != 0) text.Append(',');
            text.Append(write(items[index]));
        }
    }

    /// <summary>One canonical slot entry, and one whole slot as the canonical text spells it: the suite builds
    /// variants by taking pieces of the canonical document apart, so both spellings live in one place.</summary>
    internal static string Entry(uint id)
        => "{\"offlineGearId\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"packetSha256\":\"" + Packet(id) + "\"}";

    internal static string SlotJson(string name, params uint[] ids)
        => "\"" + name + "\":[" + string.Join(',', Array.ConvertAll(ids, Entry)) + "]";

    private static void Slot(StringBuilder text, string name, uint[] ids, bool first)
    {
        if (!first) text.Append(',');
        text.Append(SlotJson(name, ids));
    }
}
