using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Iced.Intel;
using Mono.Cecil;

namespace ForgeWeapon.Tests.NativeEvidence;

// Static evidence only: file identity, interop signatures, dump RVAs and direct x64 call edges.
// Nothing is loaded into or executed by GTFO; virtual dispatch and field writes are not resolved.
// Inputs come from the environment (see ConfiguredInputs); the suite is excluded by the Native trait by default.
[Trait("Category", "Native")]
public sealed class NativeEvidenceTests
{
    private static readonly string BepInEx = Environment.GetEnvironmentVariable("FORGE_WEAPON_BEPINEX")
        ?? Environment.GetEnvironmentVariable("GTFO_BEPINEX_PATH") ?? "";
    private static readonly string GameRoot = Environment.GetEnvironmentVariable("FORGE_GTFO_GAME") ?? "";
    private static readonly string Dump = Environment.GetEnvironmentVariable("FORGE_GTFO_DUMP") ?? "";
    private static readonly string Spec = Environment.GetEnvironmentVariable("FORGE_WEAPON_HOOK_SPEC")
        ?? Path.Combine("ForgeWeapon", "evidence", "w1-native-hooks.json");

    private static void RequireInputs()
    {
        Assert.False(string.IsNullOrWhiteSpace(BepInEx) || string.IsNullOrWhiteSpace(GameRoot) || string.IsNullOrWhiteSpace(Dump),
            "Set FORGE_WEAPON_BEPINEX (or GTFO_BEPINEX_PATH), FORGE_GTFO_GAME and FORGE_GTFO_DUMP to existing local inputs.");
    }

    [Fact]
    public void weapon_static_native_evidence_checks_all_pass()
    {
        RequireInputs();
        var checks = Audit();
        string failed = string.Join(" | ", checks.Where(c => !c.Passed).Select(c => c.Id + ": " + c.Detail));
        Assert.True(checks.Any(c => c.Id == "steam.app"), "The audit did not run to completion: " + failed);
        Assert.True(checks.All(c => c.Id != "audit.error"), "The audit aborted: " + failed);
        Assert.True(checks.Count(c => !c.Passed) == 0, failed);
        Assert.True(checks.Count >= 60, "Expected the full evidence set, observed " + checks.Count + " checks.");
    }

    private static List<EvidenceCheck> Audit()
    {
        var checks = new List<EvidenceCheck>();
        void Check(string id, bool passed, string detail) => checks.Add(new(id, passed, detail));
        string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); }
        bool SameHash(string actual, JsonElement expected) => string.Equals(actual, expected.GetString(), StringComparison.OrdinalIgnoreCase);
        IEnumerable<TypeDefinition> Walk(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Walk));
        ulong Rva(JsonElement row, string key) => Convert.ToUInt64(row.GetProperty(key).GetString()!.Replace("0x", "", StringComparison.Ordinal), 16);
        var assemblies = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Spec));
            var spec = document.RootElement;
            if (spec.GetProperty("schemaVersion").GetInt32() != 1 || spec.GetProperty("hooks").GetArrayLength() == 0)
                throw new InvalidDataException("Unsupported or empty hook specification.");
            var app = File.ReadAllText(Path.GetFullPath(Path.Combine(GameRoot, "..", "..", "appmanifest_493520.acf")));
            string Field(string key) => Regex.Match(app, "\"" + key + "\"\\s+\"([^\"]+)\"").Groups[1].Value;
            Check("steam.app", Field("appid") == "493520", "Steam app ID must be 493520.");
            Check("steam.build", Field("buildid") == spec.GetProperty("buildId").GetString(), "Observed build: " + Field("buildid"));
            string nativePath = Path.Combine(BepInEx, "GameAssembly.dll"), nativeHash = Hash(nativePath), dumpHash = Hash(Dump);
            Check("native.hash", SameHash(nativeHash, spec.GetProperty("gameAssemblySha256")), nativeHash);
            Check("dump.hash", SameHash(dumpHash, spec.GetProperty("dumpSha256")), dumpHash);
            foreach (var expected in spec.GetProperty("assemblies").EnumerateArray())
            {
                string file = expected.GetProperty("file").GetString()!;
                if (Path.GetFileName(file) != file || !file.EndsWith(".dll", StringComparison.Ordinal)) throw new InvalidDataException("Plain DLL filenames only.");
                string path = Path.Combine(BepInEx, "interop", file), hash = Hash(path);
                Check("hash." + file, SameHash(hash, expected.GetProperty("sha256")), hash);
                var assembly = AssemblyDefinition.ReadAssembly(path); assemblies.Add(file, assembly);
                Check("mvid." + file, assembly.MainModule.Mvid.ToString() == expected.GetProperty("mvid").GetString(), assembly.MainModule.Mvid.ToString());
            }

            var dump = File.ReadAllLines(Dump);
            var rvaPattern = new Regex(@"^\s*// RVA: 0x([0-9A-F]+) ", RegexOptions.CultureInvariant);
            var entries = new Dictionary<ulong, int>();
            foreach (var line in dump)
            {
                var match = rvaPattern.Match(line);
                if (match.Success) { ulong rva = Convert.ToUInt64(match.Groups[1].Value, 16); entries[rva] = entries.GetValueOrDefault(rva) + 1; }
            }
            var starts = entries.Keys.OrderBy(x => x).ToArray();
            List<ulong> Declared(string fullName, string name)
            {
                int dot = fullName.LastIndexOf('.');
                string ns = dot < 0 ? "" : fullName[..dot], shortName = dot < 0 ? fullName : fullName[(dot + 1)..];
                var declaration = new Regex(@"^(public|internal|private|protected)[\w ]* class " + Regex.Escape(shortName) + @"( |$)", RegexOptions.CultureInvariant);
                var method = new Regex(@"\s" + Regex.Escape(name) + @"\(", RegexOptions.CultureInvariant);
                var found = new List<ulong>();
                for (int i = 1; i < dump.Length; i++)
                {
                    if (!declaration.IsMatch(dump[i]) || dump[i - 1] != "// Namespace: " + ns) continue;
                    for (int j = i + 1; j < dump.Length && !dump[j].StartsWith("// Namespace:", StringComparison.Ordinal); j++)
                    {
                        if (!method.IsMatch(dump[j])) continue;
                        var rva = rvaPattern.Match(dump[j - 1]);
                        if (rva.Success) found.Add(Convert.ToUInt64(rva.Groups[1].Value, 16));
                    }
                }
                return found;
            }

            using var pe = new PEReader(File.OpenRead(nativePath));
            var section = pe.PEHeaders.SectionHeaders.Single(s => s.Name == spec.GetProperty("codeSection").GetString());
            bool executable = (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0;
            Check("native.code-section", executable, $"{section.Name} rva=0x{section.VirtualAddress:X} vsize=0x{section.VirtualSize:X}");
            var code = pe.GetSectionData(section.VirtualAddress).GetContent(0, Math.Min(section.SizeOfRawData, section.VirtualSize)).ToArray();
            ulong codeStart = (ulong)section.VirtualAddress, codeEnd = codeStart + (ulong)code.Length;

            foreach (var hook in spec.GetProperty("hooks").EnumerateArray())
            {
                string id = hook.GetProperty("hook").GetString()!, typeName = hook.GetProperty("type").GetString()!, name = hook.GetProperty("name").GetString()!;
                var type = assemblies[hook.GetProperty("assembly").GetString()!].MainModule.Types.SelectMany(Walk).SingleOrDefault(t => t.FullName == typeName);
                var matches = type?.Methods.Where(m => m.Name == name).ToArray() ?? Array.Empty<MethodDefinition>();
                Check("signature." + id, matches.Length == 1 && !matches[0].IsStatic && matches[0].FullName == hook.GetProperty("signature").GetString()
                    && matches[0].IsVirtual == hook.GetProperty("isVirtual").GetBoolean(),
                    string.Join(" | ", matches.Select(m => m.FullName + (m.IsVirtual ? " [virtual]" : ""))));
                ulong rva = Rva(hook, "rva");
                var declared = Declared(typeName, name);
                Check("dump.rva." + id, declared.Count == 1 && declared[0] == rva, string.Join(", ", declared.Select(x => "0x" + x.ToString("X"))));
                int sharing = entries.GetValueOrDefault(rva);
                Check("dump.unshared." + id, sharing == 1, "dump entries at 0x" + rva.ToString("X") + ": " + sharing);
                Check("native.executable." + id, executable && rva >= codeStart && rva < codeEnd, "0x" + rva.ToString("X"));
            }

            foreach (var edge in spec.GetProperty("callEdges").EnumerateArray())
            {
                ulong from = Rva(edge, "from"), to = Rva(edge, "to");
                int index = Array.BinarySearch(starts, from);
                ulong end = index >= 0 && index + 1 < starts.Length ? starts[index + 1] : from;
                bool found = false; string detail;
                if (index < 0 || entries[from] != 1 || !entries.ContainsKey(to) || end <= from || end - from > 0x4000 || from < codeStart || end > codeEnd)
                    detail = "caller or callee is not a bounded, unshared dump method";
                else
                {
                    var decoder = Decoder.Create(64, new ByteArrayCodeReader(code, (int)(from - codeStart), (int)(end - from)));
                    decoder.IP = from; int calls = 0;
                    while (decoder.IP < end)
                    {
                        var instruction = decoder.Decode();
                        if (instruction.FlowControl != FlowControl.Call || instruction.Op0Kind != OpKind.NearBranch64) continue;
                        calls++; found |= instruction.NearBranchTarget == to;
                    }
                    detail = $"decoded 0x{from:X}..0x{end:X} (next dump RVA), direct calls={calls}";
                }
                Check("call." + edge.GetProperty("id").GetString(), found, detail + "; " + edge.GetProperty("meaning").GetString());
            }
        }
        catch (Exception error) { Check("audit.error", false, error.GetType().Name + ": " + error.Message); }
        finally { foreach (var assembly in assemblies.Values) assembly.Dispose(); }
        return checks;
    }

    private sealed record EvidenceCheck(string Id, bool Passed, string Detail);
}
