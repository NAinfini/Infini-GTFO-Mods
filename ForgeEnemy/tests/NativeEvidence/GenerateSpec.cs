using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mono.Cecil;

// Regenerates the frozen API specification instead of hand-editing it. The mechanical fields come from the
// interop assemblies the specification locks (declaring type, signature, assembly, visibility flags) and from
// the shipped plugin IL (forge hooks, forge callers); the reviewed fields of an existing entry (id, area,
// evidenceLevel, nativeCallPhase, nativeRva, dataEvidence) are carried over untouched. A game member the plugin
// starts using needs an entry in ObservedMembers below: an unlisted member fails the run instead of being frozen
// with a guessed area or a guessed data evidence pointer.
internal static class GenerateSpec
{
    private sealed record Observed(string Type, string Member, string Area)
    {
        // A read member is named as the plugin reads it (get_m_ai); the record names the native field it declares
        // (m_ai), which is the declaration's last word.
        internal string Field => Member.StartsWith("get_", StringComparison.Ordinal) ? Member[4..] : Member;
        // A member the plugin calls rather than patches or reads as a field is reviewed by the damage window
        // record, which freezes its declaration, RVA and call sites; the behaviour record does not describe it.
        internal string Evidence { get; init; } = BehaviourEvidence;
    }

    // The behaviour observation batch reads these members; the independent record of the hooks, the read fields
    // and their offsets is evidence/enemy-behavior-hooks.json, which these entries cite as data evidence. The
    // area follows the evidence group the type already belongs to (EnemyDetection/EnemyAI/EnemyBehaviour and the
    // target types it samples read as ai-perception, the locomotion state machine as movement-space, the scout
    // scream and registration as birthing-scout). readFields documents a native field declaration with its
    // offset; hookMembers documents a method body the evidence reviewed by RVA. Adding a member the plugin reads
    // means adding its area here; its evidence pointer is looked up from the type and member name, so a member
    // that record does not describe fails the run instead of freezing a pointer to nothing.
    private static readonly Observed[] ObservedMembers =
    {
        new("Enemies.EnemyDetection", "UpdateTargets", "ai-perception"),
        new("Enemies.EnemyDetection", "get_m_ai", "ai-perception"),
        new("Enemies.EnemyDetection", "get_m_biggestDetectionBuildup", "ai-perception"),
        new("Enemies.EnemyAI", "get_m_behaviour", "ai-perception"),
        new("Enemies.EnemyAI", "get_m_detection", "ai-perception"),
        new("Enemies.EnemyAI", "get_m_locomotion", "movement-space"),
        new("Enemies.EnemyBehaviour", "get_m_ai", "ai-perception"),
        new("Enemies.EnemyBehaviour", "get_m_currentStateName", "ai-perception"),
        new("Enemies.EnemyLocomotion", "get_ScoutScream", "movement-space"),
        new("Enemies.ES_ScoutScream", "get_m_state", "birthing-scout"),
        new("ES_ScoutDetection", "OnTargetRegistered", "birthing-scout"),
        new("ES_ScoutDetection", "get_m_owner", "birthing-scout"),
        new("Agents.AgentAI", "get_IsTargetValid", "ai-perception"),
        new("Agents.AgentAI", "get_Target", "ai-perception"),
        new("Agents.AgentTarget", "get_m_position", "ai-perception"),
        new("Agents.AgentTarget", "get_m_agent", "ai-perception"),
        // The one damage entry point a Forge hit is submitted through; the damage window record carries its
        // declaration, RVA and call sites.
        new("Dam_EnemyDamageBase", "BulletDamage", "damage-limbs-death") { Evidence = DamageWindowEvidence },
    };

    private const string BehaviourEvidence = "enemy-behavior-hooks.json";
    private const string DamageWindowEvidence = "e10-damage-window.json";
    private static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(string[] args)
    {
        try { return Generate(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"GENERATE ERROR: {error.GetType().Name}: {error.Message}");
            return 2;
        }
    }

    private static int Generate(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: NativeEvidence --generate <BepInEx> <ForgeEnemy.Native.dll> <frozen spec.json> <output.json>");
            return 2;
        }
        string bep = Path.GetFullPath(args[0]);
        string specPath = Path.GetFullPath(args[2]);
        string output = Path.GetFullPath(args[3]);
        string specDirectory = Path.GetDirectoryName(specPath)!;
        var root = JsonNode.Parse(File.ReadAllText(specPath))?.AsObject()
            ?? throw new InvalidDataException("Empty specification.");
        if (root["schemaVersion"]!.GetValue<int>() != 2)
            throw new InvalidDataException("Only schema 2 specifications can be regenerated.");

        // Every entry this generator can add cites a record of the behaviour observation batch, so the pointers are
        // resolved against that record before anything is written: a member the record does not describe exactly
        // once has no traceable source and stops the run.
        var evidence = ReviewedEvidence(specDirectory);

        // The specification's own input locks are verified, never rewritten: a drifted interop means the frozen
        // metadata describes another build, so the run has to fail instead of silently rebasing the evidence.
        var contracts = new List<AssemblyDefinition>();
        var interopFiles = new List<string>();
        var declaringFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root["assemblies"]!.AsArray())
        {
            string file = entry!["file"]!.GetValue<string>();
            string path = Path.Combine(bep, "interop", file);
            string hash = Hash(path);
            if (hash != entry["sha256"]!.GetValue<string>())
                throw new InvalidDataException($"Interop hash drift for {file}: {hash}.");
            var assembly = AssemblyDefinition.ReadAssembly(path);
            contracts.Add(assembly);
            interopFiles.Add(file);
            foreach (var type in assembly.MainModule.Types.SelectMany(ForgeIl.Walk))
                declaringFile.TryAdd(Open(type.FullName), file);
        }

        var methods = root["methods"]!.AsArray();
        var usage = ForgeIl.Read(Path.GetFullPath(args[1]), interopFiles.ToHashSet(StringComparer.Ordinal));

        // Reviewed rows keep their order, id, area, evidence level and data evidence; only the IL-derived use
        // lists are refreshed, and a row whose frozen metadata no longer matches the locked interop fails.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var owners = new Dictionary<(string Type, string Name), int>();
        var refreshed = new List<string>();
        foreach (var row in methods)
        {
            var frozen = row!.AsObject();
            string typeName = frozen["type"]!.GetValue<string>();
            string signature = frozen["signature"]!.GetValue<string>();
            if (!ids.Add(frozen["id"]!.GetValue<string>()) || !signatures.Add(signature))
                throw new InvalidDataException("Duplicate frozen id or signature: " + signature);
            var declaration = Declared(contracts, typeName, signature)
                ?? throw new InvalidDataException("Frozen entry no longer resolves in the locked interop: " + signature);
            foreach (string flag in new[] { "isStatic", "isVirtual", "isPublic" })
            {
                bool actual = flag switch
                {
                    "isStatic" => declaration.IsStatic,
                    "isVirtual" => declaration.IsVirtual,
                    _ => declaration.IsPublic,
                };
                if (frozen[flag]!.GetValue<bool>() != actual)
                    throw new InvalidDataException($"{flag} drift on {signature}: locked interop says {actual}.");
            }
            string methodName = Member(signature);
            var hooks = usage.Hooks.TryGetValue((typeName, methodName), out var hook)
                ? hook.Kinds : new SortedSet<string>(StringComparer.Ordinal);
            var callers = usage.Calls.TryGetValue(signature, out var call)
                ? call.Callers : new SortedSet<string>(StringComparer.Ordinal);
            if (!Strings(frozen["forgeHooks"]!).SequenceEqual(hooks) || !Strings(frozen["forgeCallers"]!).SequenceEqual(callers))
                refreshed.Add($"{frozen["id"]!.GetValue<string>()}: hooks [{string.Join(",", hooks)}], callers [{string.Join(",", callers)}]");
            frozen["forgeHooks"] = Array(hooks);
            frozen["forgeCallers"] = Array(callers);
            if (hooks.Count > 0) owners[(typeName, methodName)] = owners.GetValueOrDefault((typeName, methodName)) + 1;
        }

        // A hook the plugin adds needs exactly one frozen owner; a game API it starts calling needs one entry.
        var added = new List<(string Area, JsonObject Row)>();
        foreach (var (key, hook) in usage.Hooks)
        {
            if (owners.GetValueOrDefault(key) > 1)
                throw new InvalidDataException($"{key.Type}::{key.Name} is claimed by {owners[key]} frozen entries.");
            if (owners.ContainsKey(key)) continue;
            var row = NewRow(contracts, declaringFile, hook.Type, key.Name,
                Required(contracts, hook.Type, key.Name).FullName, hook.ScopeFile);
            row["forgeHooks"] = Array(hook.Kinds);
            Add(added, ids, signatures, evidence, row);
        }
        foreach (var (signature, call) in usage.Calls)
        {
            if (signatures.Contains(signature)) continue;
            var row = NewRow(contracts, declaringFile, call.DeclaringType, Member(signature), signature, call.ScopeFile);
            row["forgeCallers"] = Array(call.Callers);
            if (usage.Hooks.TryGetValue((call.DeclaringType, Member(signature)), out var hook)) row["forgeHooks"] = Array(hook.Kinds);
            Add(added, ids, signatures, evidence, row);
        }

        // New entries join the end of their area group, which is how the specification already orders the batch
        // that added them; an area the specification does not have yet is appended as its own block.
        var groups = methods.Select(m => m!["area"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToList();
        foreach (string area in added.Select(a => a.Area).Distinct(StringComparer.Ordinal))
            if (!groups.Contains(area)) groups.Add(area);
        var ordered = new List<JsonNode?>();
        foreach (string area in groups)
        {
            ordered.AddRange(methods.Where(m => m!["area"]!.GetValue<string>() == area));
            ordered.AddRange(added.Where(a => a.Area == area)
                .OrderBy(a => a.Row["id"]!.GetValue<string>(), StringComparer.Ordinal).Select(a => (JsonNode?)a.Row));
        }
        methods.Clear();
        foreach (var row in ordered) methods.Add(row);

        // The data evidence a cited member points at is locked by content hash, like the interop assemblies.
        var locked = root["dataEvidenceFiles"]!.AsArray();
        var relocked = new List<string>();
        foreach (string path in root["methods"]!.AsArray().SelectMany(m => m!["dataEvidence"]!.AsArray())
            .Select(d => d!["file"]!.GetValue<string>()).Distinct(StringComparer.Ordinal))
        {
            string full = Path.Combine(specDirectory, path);
            if (!File.Exists(full)) throw new InvalidDataException("Cited data evidence is missing: " + path);
            string hash = Hash(full);
            var entry = locked.FirstOrDefault(e => e!["path"]!.GetValue<string>() == path);
            if (entry == null) locked.Add(new JsonObject { ["path"] = path, ["sha256"] = hash });
            else if (entry["sha256"]!.GetValue<string>() != hash)
            {
                relocked.Add($"{path}: {entry["sha256"]!.GetValue<string>()} -> {hash}");
                entry["sha256"] = hash;
            }
        }

        string text = root.ToJsonString(Json).Replace("\r\n", "\n") + "\n";
        string temporary = output + ".generating";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, output, true);
        Console.WriteLine($"GENERATED {output}: methods={root["methods"]!.AsArray().Count} (+{added.Count}), refreshed={refreshed.Count}");
        foreach (var row in added) Console.WriteLine("  new " + row.Row["id"]!.GetValue<string>());
        foreach (var row in refreshed) Console.WriteLine("  refreshed " + row);
        foreach (var file in relocked) Console.WriteLine("  relocked " + file);
        return 0;
    }

    private static void Add(List<(string Area, JsonObject Row)> added, HashSet<string> ids, HashSet<string> signatures,
        IReadOnlyDictionary<(string Type, string Member), (string File, string Pointer)> evidence, JsonObject row)
    {
        string type = row["type"]!.GetValue<string>();
        string member = Member(row["signature"]!.GetValue<string>());
        var observed = Reviewed(type, member);
        string id = $"{observed.Area}.{type}.{member}.0";
        if (!ids.Add(id) || !signatures.Add(row["signature"]!.GetValue<string>()))
            throw new InvalidDataException("Generated entry duplicates a frozen id or signature: " + id);
        var source = evidence[(observed.Type, observed.Member)];
        row["id"] = id;
        row["area"] = observed.Area;
        row["dataEvidence"] = new JsonArray(new JsonObject
        { ["file"] = source.File, ["pointer"] = source.Pointer });
        added.Add((observed.Area, row));
    }

    private static Observed Reviewed(string type, string member)
        => ObservedMembers.SingleOrDefault(o => o.Type == type && o.Member == member)
            ?? throw new InvalidDataException($"No reviewed area/data evidence for {type}::{member}; add it to ObservedMembers.");

    private static JsonObject NewRow(IReadOnlyList<AssemblyDefinition> contracts, Dictionary<string, string> declaringFile,
        string typeName, string member, string signature, string scopeFile)
    {
        var declaration = Required(contracts, typeName, signature);
        if (declaration.Name != member)
            throw new InvalidDataException($"Interop declaration does not match {typeName}::{member}.");
        return new JsonObject
        {
            ["id"] = "", ["area"] = "", ["assembly"] = declaringFile.GetValueOrDefault(Open(typeName), scopeFile),
            ["type"] = typeName, ["signature"] = signature,
            ["isStatic"] = declaration.IsStatic, ["isVirtual"] = declaration.IsVirtual, ["isPublic"] = declaration.IsPublic,
            ["evidenceLevel"] = "metadata", ["nativeCallPhase"] = "unknown",
            ["forgeHooks"] = new JsonArray(), ["forgeCallers"] = new JsonArray(), ["dataEvidence"] = new JsonArray(),
        };
    }

    // A hook target is frozen by its declaration: the plugin only patches it, so the locked interop declaration
    // supplies the one signature the auditor resolves. A called member is frozen by its IL reference string.
    private static MethodDefinition Required(IReadOnlyList<AssemblyDefinition> contracts, string typeName, string signature)
        => Declared(contracts, typeName, signature)
            ?? throw new InvalidDataException($"No interop declaration for {typeName}::{signature}.");

    private static MethodDefinition? Declared(IReadOnlyList<AssemblyDefinition> contracts, string typeName, string signature)
    {
        var matches = contracts.SelectMany(a => a.MainModule.Types).SelectMany(ForgeIl.Walk)
            .Where(t => Open(t.FullName) == Open(typeName))
            .SelectMany(t => t.Methods)
            .Where(m => signature.Contains("::") ? Open(m.FullName) == Open(signature) : m.Name == signature)
            .ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"Ambiguous interop declaration for {typeName}::{signature}.");
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string Member(string signature) => Regex.Match(signature, @"::([^(]+)\(").Groups[1].Value;
    private static string[] Strings(JsonNode array) => array.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)v).ToArray());
    private static string Open(string fullName) => ForgeIl.Open(fullName);

    // The behaviour record names a type as the interop dump does ("EnemyDetection"); the plugin's IL reference and
    // the specification entry carry the namespace, so the lookup compares the type name alone.
    private static string Short(string typeName)
    {
        string open = ForgeIl.Open(typeName);
        int separator = open.LastIndexOf('.');
        return separator < 0 ? open : open[(separator + 1)..];
    }

    // Each observed member maps to the one behaviour record that describes it: a read field to its declaration
    // and a reviewed hook body to its RVA. The pointer names that record by index, so the auditor resolves it as
    // the source of this entry and not merely as the batch it came from.
    private static Dictionary<(string Type, string Member), (string File, string Pointer)> ReviewedEvidence(string specDirectory)
    {
        var pointers = new Dictionary<(string Type, string Member), (string File, string Pointer)>();
        foreach (var observed in ObservedMembers)
        {
            if (observed.Evidence != BehaviourEvidence)
            {
                pointers[(observed.Type, observed.Member)] = DamageWindowPointer(specDirectory, observed);
                continue;
            }
            string path = Path.Combine(specDirectory, BehaviourEvidence);
            if (!File.Exists(path))
                throw new InvalidDataException($"The reviewed behaviour record {BehaviourEvidence} is missing.");
            var evidence = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            int field = FieldIndex(evidence, observed);
            int hook = HookIndex(evidence, observed);
            if (field < 0 && hook < 0)
                throw new InvalidDataException($"{BehaviourEvidence} does not describe {observed.Type}::{observed.Member}.");
            if (field >= 0 && hook >= 0)
                throw new InvalidDataException($"{BehaviourEvidence} describes {observed.Type}::{observed.Member} twice.");
            pointers[(observed.Type, observed.Member)] = (BehaviourEvidence, field >= 0
                ? $"readFields[{field}].declaration" : $"hookMembers[{hook}].nativeRva");
        }
        return pointers;
    }

    // A called member's source is the damage window's own method row, which freezes the declaration, the RVA and
    // the call sites of that entry point. Exactly one row may describe it.
    private static (string File, string Pointer) DamageWindowPointer(string specDirectory, Observed observed)
    {
        string path = Path.Combine(specDirectory, DamageWindowEvidence);
        if (!File.Exists(path))
            throw new InvalidDataException($"The reviewed damage record {DamageWindowEvidence} is missing.");
        var rows = JsonNode.Parse(File.ReadAllText(path))!.AsObject()["damageMethods"]!.AsArray();
        int found = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!string.Equals((string?)rows[i]!["type"], observed.Type, StringComparison.Ordinal)
                || !string.Equals((string?)rows[i]!["name"], observed.Member, StringComparison.Ordinal)) continue;
            if (found >= 0)
                throw new InvalidDataException($"{DamageWindowEvidence} describes {observed.Type}::{observed.Member} more than once.");
            found = i;
        }
        if (found < 0)
            throw new InvalidDataException($"{DamageWindowEvidence} does not describe {observed.Type}::{observed.Member}.");
        return (DamageWindowEvidence, $"damageMethods[{found}].nativeRva");
    }

    // Both lookups require exactly one record: a member described twice has no single source to cite.
    private static int FieldIndex(JsonObject evidence, Observed observed)
        => Unique(evidence["readFields"]!.AsArray(), observed.Type,
            record => ((string?)record["declaration"] ?? "").EndsWith(" " + observed.Field, StringComparison.Ordinal));

    private static int HookIndex(JsonObject evidence, Observed observed)
        => Unique(evidence["hookMembers"]!.AsArray(), observed.Type,
            record => string.Equals((string?)record["name"], observed.Member, StringComparison.Ordinal));

    private static int Unique(JsonArray records, string type, Func<JsonNode, bool> matches)
    {
        int found = -1;
        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record == null) continue;
            if (Short((string?)record["type"] ?? "") != Short(type)) continue;
            if (!matches(record)) continue;
            if (found >= 0) throw new InvalidDataException($"{BehaviourEvidence} describes {type} more than once.");
            found = i;
        }
        return found;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
