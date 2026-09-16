using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ForgeMap.Tests.MapNativeLayout;

/// <summary>
/// Rewrites the member rows of `evidence/map-hooks.json` from the compiled plugin and the interop assemblies:
/// one readback row per game member the plugin reads, one write row per non-getter member it calls, and one hook
/// row per Harmony patch class with the target the interop assembly really declares.
///
/// The audit in this project is what decides whether those rows are right, and it compares the whole set both
/// ways, so this is not a second opinion: it is the same reading of the same assemblies, written down. A row that
/// already exists keeps its prose and its `rva`; a member the plugin stopped calling loses its row.
/// </summary>
internal static class EvidenceRegenerator
{
    /// <summary>One game member the plugin calls, with the declaration the call binds to along the type's base
    /// chain: the spellings an evidence row pins are these, not the call site's own.</summary>
    internal sealed record Call(string Member, string Signature, string Type, bool IsStatic);

    /// <summary>Everything the rows are written from: the interop types, and every game member the plugin calls
    /// together with the plugin's own patch classes.</summary>
    internal sealed record Inputs(IReadOnlyList<TypeDefinition> GameTypes, IReadOnlyList<TypeDefinition> NativeTypes,
        IReadOnlyList<Call> Calls, IReadOnlyList<JsonObject> Hooks);

    /// <summary>Reads the plugin and the interop assemblies the way the audit does and answers the sets the rows
    /// are written from. The assemblies are the caller's: this only reads them.</summary>
    internal static Inputs Read(AssemblyDefinition native, IReadOnlyList<AssemblyDefinition> gameAssemblies, JsonDocument spec)
    {
        var gameTypes = gameAssemblies.SelectMany(a => a.MainModule.Types.SelectMany(Walk)).ToArray();
        var nativeTypes = native.MainModule.Types.SelectMany(Walk).ToArray();
        var gameTypeNames = new HashSet<string>(gameAssemblies.Select(a => a.Name.Name), StringComparer.Ordinal);
        var calls = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>()
            .Where(m => gameTypeNames.Contains(Scope(m)))
            .Select(m => Resolve(gameTypes, m))
            .Where(x => x != null)
            .Select(x => new Call(Member(x!), x!.FullName, x.DeclaringType.FullName, x.IsStatic))
            // The engine's own interop members — the object base, the interop collections and Unity's transform —
            // are not the game's types and carry no evidence row of their own, which is the boundary the audit
            // draws before it sorts anything; keeping them here would write rows for members it never compares.
            .Where(x => !Engine(x.Member))
            // One entry per declaration and not per name: a name the game overloads carries one row per form, so
            // collapsing the calls here would drop every form but the first from the tables below.
            .DistinctBy(x => x.Signature, StringComparer.Ordinal)
            .OrderBy(x => x.Member, StringComparer.Ordinal)
            .ToArray();
        var hooks = spec.RootElement.GetProperty("hooks").EnumerateArray().Select(x => JsonNode.Parse(x.GetRawText())!.AsObject()).ToArray();
        return new Inputs(gameTypes, nativeTypes, calls, hooks);
    }

    internal static void Run(Inputs inputs, string specPath, string outputPath)
    {
        var spec = JsonNode.Parse(File.ReadAllText(specPath))!.AsObject();
        var previous = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in spec["readbacks"]!.AsArray().OfType<JsonObject>())
            if (ResolveRow(inputs, row) is { } member) previous[member] = row;
        var previousWrites = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in spec["writes"]!.AsArray().OfType<JsonObject>())
            if (ResolveWrite(inputs, row) is { } signature) previousWrites[signature] = row;
        var previousHooks = inputs.Hooks.Where(h => h["hook"] != null).ToDictionary(h => (string)h["hook"]!, StringComparer.Ordinal);
        var unresolved = new List<string>();
        // The dump is optional: the hook rows' RVAs are read out of it when the build it was made from is the
        // one the spec froze, and the rows keep the RVAs they already carry when it is not.
        var dump = Dump.Open(Environment.GetEnvironmentVariable("FORGE_GTFO_DUMP") ?? "",
            (string?)spec["dumpSha256"] ?? "", message => Console.WriteLine(message));

        // The engine's own interop members carry no evidence row: the audit compares its declared sets against
        // the game's own members only, so writing a row for `Il2CppSystem` or `UnityEngine` would be a row the
        // audit reads as an extra it never asked for.
        var declared = inputs.Calls.Where(c => !Engine(c.Member)).ToArray();
        var writes = new JsonArray();
        var writeMembers = new HashSet<string>(StringComparer.Ordinal);
        // One row per declaration and not per name: a name the game overloads has one row per form the plugin
        // calls, because the audit compares the whole signature set and a dropped form reads as a call nobody
        // reviewed. The row id only carries the parameters when a name has more than one form, so the ids of the
        // single-form rows stay the ones the evidence already spelled.
        var writeCalls = declared.Where(IsWrite).ToArray();
        var forms = writeCalls.GroupBy(c => c.Member, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var call in writeCalls)
        {
            writeMembers.Add(call.Member);
            writes.Add(previousWrites.TryGetValue(call.Signature, out var row) ? row.DeepClone() : new JsonObject
            {
                ["id"] = forms[call.Member] == 1 ? Id(call.Member) : Id(call),
                ["assembly"] = Assembly(inputs, call.Type),
                ["type"] = call.Type,
                ["isStatic"] = call.IsStatic,
                ["signature"] = call.Signature,
                ["role"] = "a game entry this provider calls: a non-getter member whose effect is the action it names, reached through the handler of the row that declares it."
            });
        }

        // Only the reads the audit sorts into its two declared sets carry a row. The master read is resolved
        // from the compiled module like the rest and the audit appends it to the player set itself, so its row
        // is an extra there; a game read outside both sets is one the audit refuses instead of one that silently
        // disappears, so it is reported rather than dropped. A read is one row per member, because the audit's
        // read sets are compared by member and a second row for an overload would read as a read it never saw.
        var readbacks = new JsonArray();
        var unclassified = new List<string>();
        foreach (var call in declared.Where(c => !writeMembers.Contains(c.Member))
            .DistinctBy(c => c.Member, StringComparer.Ordinal))
        {
            // The audit appends this one to the player set itself, from its own reading of the compiled module,
            // so a row for it would be read as a second, extra read of the same member.
            if (call.Member == "SNetwork.SNet::get_IsMaster") continue;
            if (!GameRead(call.Member) && !MapObjectRead(call.Member)) { unclassified.Add(call.Member); continue; }
            readbacks.Add(previous.TryGetValue(call.Member, out var row) ? row.DeepClone() : new JsonObject
            {
                ["id"] = Id(call.Member),
                ["assembly"] = Assembly(inputs, call.Type),
                ["type"] = call.Type,
                ["isStatic"] = call.IsStatic,
                ["signature"] = call.Signature
            });
        }

        spec["hooks"] = Hooks(inputs, previousHooks, dump, unresolved);
        spec["readbacks"] = readbacks;
        spec["writes"] = writes;
        // The rows are written from parsed values, so the tables this file carries beside them are re-emitted
        // the same way: a section left as the raw element the parser produced would be serialized as a verbatim
        // copy of the old text, which is how a table this run did not rewrite would drift out of the document.
        foreach (string table in new[] { "callEdges", "arrays", "enums", "shapes" })
            if (spec[table] is JsonValue verbatim) spec[table] = JsonNode.Parse(verbatim.ToJsonString());
        File.WriteAllText(outputPath, spec.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        if (unresolved.Count != 0)
            Console.WriteLine("unresolved hooks (no interop method): " + string.Join(" | ", unresolved));
        if (unclassified.Count != 0)
            Console.WriteLine("game reads the audit does not classify: " + string.Join(" | ", unclassified));
    }

    /// <summary>The interop's own object base, collections and Unity members, which are not the game's types.</summary>
    private static bool Engine(string member) => member.StartsWith("Il2CppInterop.", StringComparison.Ordinal)
        || member.StartsWith("Il2CppSystem.", StringComparison.Ordinal)
        || member.StartsWith("UnityEngine.", StringComparison.Ordinal);

    /// <summary>One row per patch class the plugin carries, with the target, the static flag, the patch kind and
    /// that kind's parameter shape read off the compiled plugin and the interop assembly. A row that already
    /// existed keeps its readback prose, which is the reading of the binary this run does not repeat, and its
    /// `rva` when the dump it was read from is the one this run can open.</summary>
    private static JsonArray Hooks(Inputs inputs, IReadOnlyDictionary<string, JsonObject> previous, Dump? dump, List<string> unresolved)
    {
        var rows = new JsonArray();
        foreach (var hook in inputs.NativeTypes.Where(Patch).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var patch = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
            var type = patch.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
            var name = patch.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
            // The patch kind is what the plugin declares rather than what the schema prefers. A class that
            // carries a postfix is a postfix hook: the postfix is the body that reads the state the write left
            // behind, and a prefix beside it is Harmony's own `__state` capture rather than a second kind. Only a
            // class with no postfix at all is a prefix hook, which is where the patched body has to be
            // pre-empted.
            var prefix = hook.Methods.FirstOrDefault(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix"));
            var body = hook.Methods.FirstOrDefault(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix"))
                ?? prefix ?? throw new InvalidOperationException("No Harmony patch method on " + hook.Name);
            string kind = body.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix") ? "postfix" : "prefix";
            var owner = inputs.GameTypes.FirstOrDefault(t => t.FullName == type.FullName);
            var target = owner == null ? null : Target(inputs.GameTypes, owner, patch, name);
            // A patch whose target the interop assembly does not declare as a method cannot be installed by
            // Harmony at all: a name that is a field or a property's own backing member is not a target. A name
            // the interop assembly declares more than once is resolved to the form the patch itself names, and
            // one that stays ambiguous is reported the same way: a row for one form would claim a hook the other
            // form may be the target of.
            if (target == null)
            {
                unresolved.Add(hook.Name + " -> " + type.FullName + "::" + name);
                continue;
            }
            // The guard is the session entry the whole patch reaches the half through. A hook that guards inside
            // a lambda the compiler moved into the patch class's own display class still names the same entry, so
            // the search covers the body and every method under the class.
            // A hook that reaches the session names the guard its whole callback runs inside. A hook that only
            // maintains patch-level state — the damage-kind token a receive entry sets for the accept path to
            // read, and the native extra-information row a line of this model owns — has no guard: what it
            // touches is this package's own table, and it answers nothing while the half it belongs to is not
            // attached. `session: off` is how such a row says so instead of naming a guard it does not call.
            var guard = Guard(body);
            string? session = guard == null ? null : "on";
            if (guard == null && Facts(body) is { } factsGuard) { guard = factsGuard; session = "on"; }
            var arguments = new JsonArray();
            var reads = new JsonArray();
            var writes = new JsonArray();
            foreach (var parameter in body.Parameters)
            {
                if (parameter.Name == "__instance" && parameter.ParameterType.FullName == type.FullName) continue;
                // `__state` and `__result` are Harmony's own slots rather than arguments of the patched method,
                // so they are not part of the parameter list a row declares.
                if (parameter.Name is "__state" or "__result") continue;
                arguments.Add(new JsonObject { ["name"] = parameter.Name, ["type"] = parameter.ParameterType.FullName });
            }
            foreach (string read in Loaded(body, type.FullName))
                reads.Add(read);
            // A prefix may change an argument the patched body is about to read, and the only way it can is a
            // by-ref parameter; every other parameter is as read-only for a prefix as it is for a postfix.
            // Harmony's own `__state` is not an argument of the patched method at all — a prefix writes it for
            // the postfix to read — so it is a parameter of the patch and never a write of the patch's.
            foreach (var parameter in body.Parameters)
                if (parameter.ParameterType.IsByReference && parameter.Name != "__state") writes.Add(parameter.Name);
            previous.TryGetValue(hook.Name, out var known);
            var row = new JsonObject
            {
                ["hook"] = hook.Name,
                ["assembly"] = target.Module.Assembly.Name.Name + ".dll",
                ["type"] = target.DeclaringType.FullName,
                ["name"] = target.Name,
                ["signature"] = target.FullName,
                ["isVirtual"] = target.IsVirtual,
                ["isStatic"] = target.IsStatic,
                ["rva"] = dump?.Rva(target) ?? known?["rva"]?.DeepClone(),
                ["patch"] = kind,
                ["session"] = session ?? "off",
                [kind] = new JsonObject
                {
                    ["instanceParameter"] = body.Parameters.Count > 0 && body.Parameters[0].Name == "__instance"
                        && body.Parameters[0].ParameterType.FullName == type.FullName ? "__instance" : null,
                    ["arguments"] = arguments,
                    ["reads"] = reads,
                    ["writes"] = writes,
                    ["guard"] = guard
                }
            };
            // The `readback` prose names the native member the callback really reads. Deriving that from the
            // binary is what this run does not do — the reading runs through the half's own observers — so a row
            // that already carries a reading keeps it and a hook nobody has read yet carries no field at all. A
            // sentence invented here would be prose the evidence does not support, which is worse than silence.
            if (known?["readback"]?.DeepClone() is { } reading) row["readback"] = reading;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The one method a patch class targets, resolved the way Harmony resolves it: the name is looked up
    /// on the type the `[HarmonyPatch]` attribute declares, and only a name that type does not carry at all is
    /// looked up along its base chain, where the first — most derived — declaration wins, which is how an
    /// override and the base it replaces stay one target.
    ///
    /// The form of an overloaded name is pinned by the parameter type list the attribute declares and by nothing
    /// else. The patch body's own arguments are how Harmony binds a body to a target it has already chosen, so
    /// reading a form out of them would answer one the attribute never named; a name the declared type carries
    /// more than once with no parameter list to choose between the forms is the ambiguity Harmony itself refuses,
    /// and it is reported instead of guessed.</summary>
    private static MethodDefinition? Target(IReadOnlyList<TypeDefinition> gameTypes, TypeDefinition owner,
        CustomAttribute patch, string name)
    {
        // The attribute's parameter list, which is how a class with an overloaded target names the form it
        // means. Cecil carries `new[] { typeof(A), typeof(B) }` as an array of its own argument values.
        var declared = patch.ConstructorArguments.Count > 2
            ? (patch.ConstructorArguments[2].Value as CustomAttributeArgument[])?.Select(a => a.Value as TypeReference).ToArray()
            : null;
        var wanted = declared is { Length: > 0 } && declared.All(t => t != null)
            ? declared.Select(t => t!.FullName).ToArray() : null;
        foreach (var type in Chain(gameTypes, owner))
        {
            var candidates = type.Methods.Where(m => m.Name == name).ToArray();
            if (wanted != null)
            {
                var form = candidates.FirstOrDefault(m => m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(wanted));
                if (form != null) return form;
                continue;
            }
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length > 1) return null;
        }
        return null;
    }

    /// <summary>The `dump.cs` this run reads RVAs from, or null when it was not handed one. The hook rows keep
    /// their `rva` across a regeneration only while the dump the spec was read from is the one that can be
    /// opened here, because an RVA means nothing without the binary it was read out of.</summary>
    internal sealed record Dump(ulong Sha256, string[] Lines)
    {
        private static readonly Regex RvaLine = new(@"^\s*// RVA: 0x([0-9A-F]+) ", RegexOptions.CultureInvariant);
        private static readonly Regex NamespaceLine = new("^// Namespace: ", RegexOptions.CultureInvariant);

        /// <summary>Opens the dump and answers null when its bytes are not the ones the spec froze, so a row
        /// whose RVA cannot be re-read keeps the RVA it already carries instead of a value from another build.</summary>
        internal static Dump? Open(string path, string expectedSha256, Action<string> report)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                report("dump " + path + " is not the build this spec froze (" + actual + ")");
                return null;
            }
            return new Dump(0, File.ReadAllLines(path));
        }

        /// <summary>The one RVA the dump declares for the method a row names, or null when the dump names none
        /// or the name stays overloaded and the dump cannot say which declaration the row meant. The declaration
        /// is found the way <c>MapNativeEvidence</c> finds it: the type's own class line, the `// Namespace:`
        /// line above it, and the `// RVA:` line above the method. The dump spells a signature in the
        /// decompiler's own C# types, which are not the interop assembly's, so an overloaded name is told apart
        /// by how many parameters the declaration takes.
        /// </summary>
        internal string? Rva(MethodDefinition target)
        {
            string fullName = target.DeclaringType.FullName;
            int dot = fullName.LastIndexOf('.');
            string ns = dot < 0 ? "" : fullName[..dot], shortName = dot < 0 ? fullName : fullName[(dot + 1)..];
            var declaration = new Regex(@"^(public|internal|private|protected)[\w ]* class " + Regex.Escape(shortName) + @"( |$)", RegexOptions.CultureInvariant);
            var method = new Regex(@"\s" + Regex.Escape(target.Name) + @"\(", RegexOptions.CultureInvariant);
            var found = new List<(ulong Rva, int Parameters)>();
            for (int i = 1; i < Lines.Length; i++)
            {
                if (!declaration.IsMatch(Lines[i]) || Lines[i - 1] != "// Namespace: " + ns) continue;
                for (int j = i + 1; j < Lines.Length && !NamespaceLine.IsMatch(Lines[j]); j++)
                {
                    if (!method.IsMatch(Lines[j])) continue;
                    var match = RvaLine.Match(Lines[j - 1]);
                    if (!match.Success) continue;
                    ulong rva = Convert.ToUInt64(match.Groups[1].Value, 16);
                    var entry = (rva, Parameters(Lines[j]));
                    if (!found.Contains(entry)) found.Add(entry);
                }
            }
            var counted = found.Where(x => x.Parameters == target.Parameters.Count).Select(x => x.Rva).Distinct().ToArray();
            if (counted.Length == 1) return "0x" + counted[0].ToString("X");
            var all = found.Select(x => x.Rva).Distinct().ToArray();
            return all.Length == 1 ? "0x" + all[0].ToString("X") : null;
        }

        /// <summary>How many parameters the dump's own spelling of a method declaration takes, by counting the
        /// top-level commas of its parameter list. Generic arguments and array ranks carry commas of their own,
        /// so the count tracks the nesting.</summary>
        private static int Parameters(string line)
        {
            int open = line.IndexOf('(');
            if (open < 0) return -1;
            int depth = 0, commas = 0;
            bool written = false;
            for (int i = open; i < line.Length; i++)
            {
                char c = line[i];
                if (c is '(' or '<' or '[') depth++;
                else if (c is ')' or '>' or ']')
                {
                    depth--;
                    if (depth == 0) return written ? commas + 1 : 0;
                }
                else if (depth == 1 && c == ',') commas++;
                else if (depth == 1 && !char.IsWhiteSpace(c)) written = true;
            }
            return -1;
        }
    }

    /// <summary>The parameters a patch body reads, by its own `ldarg`/`ldarga` and by the display-class fields
    /// the compiler hoists a captured parameter into: a parameter a lambda captures is not loaded by the body
    /// itself, so the assembly spells the read as a field of the body's own display class. The audit reads both
    /// spellings the same way, and the instance parameter it declares separately is not one of the reads.</summary>
    private static IEnumerable<string> Loaded(MethodDefinition body, string patchedType)
    {
        bool Instance(ParameterDefinition parameter)
            => parameter.Name == "__instance" && parameter.ParameterType.FullName == patchedType;
        // Harmony's own injected parameters are not arguments of the patched method: `__result` is the return
        // value a postfix may read and `__state` is the slot a prefix writes for its postfix. Neither is a value
        // the native body was handed, so the read set a row declares is the arguments alone.
        bool Injected(ParameterDefinition parameter)
            => Instance(parameter) || parameter.Name is "__result" or "__state";
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        string Path(string name) => name.Replace('+', '/');
        var display = body.DeclaringType.NestedTypes
            .Where(t => t.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal)).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ParameterDefinition? last = null;
        foreach (var instruction in body.Body.Instructions)
        {
            var parameter = instruction.OpCode.Code switch
            {
                Code.Ldarg_0 => body.Parameters.ElementAtOrDefault(0),
                Code.Ldarg_1 => body.Parameters.ElementAtOrDefault(1),
                Code.Ldarg_2 => body.Parameters.ElementAtOrDefault(2),
                Code.Ldarg_3 => body.Parameters.ElementAtOrDefault(3),
                Code.Ldarg or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S => instruction.Operand as ParameterDefinition,
                _ => null
            };
            if (parameter != null)
            {
                last = parameter;
                if (!Injected(parameter) && seen.Add(parameter.Name)) yield return parameter.Name;
                continue;
            }
            // The store into the display class is the parameter's other spelling: the argument loaded just before
            // it is the one being hoisted. Whether it was read is decided by the loads below, because a field the
            // body only ever writes is not a read.
            if (instruction.OpCode.Code == Code.Stfld && instruction.Operand is FieldReference field && last != null && !Injected(last)
                && display.Any(t => Path(t.FullName) == Path(field.DeclaringType.FullName)))
                alias[field.Name] = last.Name;
        }
        if (alias.Count == 0) yield break;
        foreach (var holder in new[] { body }.Concat(display.SelectMany(t => t.Methods)))
        {
            if (!holder.HasBody) continue;
            foreach (var instruction in holder.Body.Instructions)
                if (instruction.OpCode.Code is Code.Ldfld or Code.Ldflda or Code.Ldsfld or Code.Ldsflda
                    && instruction.Operand is FieldReference field && alias.TryGetValue(field.Name, out var name)
                    && seen.Add(name))
                    yield return name;
        }
    }

    private static bool Patch(TypeDefinition type)
        => type.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");

    /// <summary>The members this module writes: a non-getter game call that is not one of the pure query
    /// spellings the audit names and not one of the query members it names one by one. Every one of them is a
    /// write an evidence row has to declare, and a query left in this set would be a read the audit refuses as an
    /// undeclared write.</summary>
    private static bool IsWrite(Call call)
    {
        string name = Name(call.Member);
        return !name.StartsWith("get_", StringComparison.Ordinal) && Array.IndexOf(ReadSpellings, name) < 0
            && Array.IndexOf(QueryMembers, call.Member) < 0;
    }

    /// <summary>Whether the audit classifies a member as one of the player/identity reads. The spellings are
    /// the audit's own, including the two members it counts as reads although their names do not say so: the
    /// active expedition the rundown manager looks up, and Unity's hierarchy lookup the hit path climbs. The
    /// player half's own presentation and inventory reads are of the same kind: the HUD manager, the local
    /// player's status layer and its text figures, the marker a ping placed on a player, the item a player wields
    /// and carries, the player's own interaction target and the item a life is holding.</summary>
    private static bool GameRead(string member) => member.StartsWith("Player.", StringComparison.Ordinal)
        || member.StartsWith("SNetwork.", StringComparison.Ordinal) || member.StartsWith("Agents.", StringComparison.Ordinal)
        || member.StartsWith("Dam_", StringComparison.Ordinal)
        || member.StartsWith("GuiManager::", StringComparison.Ordinal) || member.StartsWith("PlayerGuiLayer::", StringComparison.Ordinal)
        || member.StartsWith("PUI_", StringComparison.Ordinal) || member.StartsWith("PLOC_", StringComparison.Ordinal)
        || member.StartsWith("PlayerInventoryBase::", StringComparison.Ordinal) || member.StartsWith("ItemEquippable::", StringComparison.Ordinal)
        || member.StartsWith("Item::", StringComparison.Ordinal) || member.StartsWith("PlayerInteraction::", StringComparison.Ordinal)
        || member.StartsWith("Interact_Timed::", StringComparison.Ordinal) || member.StartsWith("NavMarker::", StringComparison.Ordinal)
        || member.StartsWith("PlaceNavMarkerOnGO::", StringComparison.Ordinal);

    /// <summary>Whether the audit classifies a member as one of the map-object reads: the level generation the
    /// zone table is built from, the level's data block, the course node a terminal reaches its zone through,
    /// the localization value type the door's own no-key prompt is built from, and the level identity a `level`
    /// mount is compared against — plus the level's own objects and machines beside that table: the asset a room
    /// reference loads, the alarm and scan puzzles, the generators and HSUs, the objective machine and its state
    /// block, the environment state, the encounter director, the world event manager, the elevator landing and
    /// the zone index a lighting read takes.</summary>
    private static bool MapObjectRead(string member) => member.StartsWith("LevelGeneration.", StringComparison.Ordinal)
        || member.StartsWith("GameData.", StringComparison.Ordinal) || member.StartsWith("AIGraph.", StringComparison.Ordinal)
        || member.StartsWith("Localization.", StringComparison.Ordinal)
        || member.StartsWith("AssetShards.", StringComparison.Ordinal) || member.StartsWith("ChainedPuzzles.", StringComparison.Ordinal)
        || member.StartsWith("EnvironmentStateManager::", StringComparison.Ordinal) || member.StartsWith("WardenObjective", StringComparison.Ordinal)
        || member.StartsWith("IWardenObjective::", StringComparison.Ordinal)
        || member.StartsWith("pWardenObjectiveState::", StringComparison.Ordinal) || member.StartsWith("WO_", StringComparison.Ordinal)
        || member.StartsWith("LG_", StringComparison.Ordinal) || member.StartsWith("Mastermind::", StringComparison.Ordinal)
        || member.StartsWith("WorldEventManager::", StringComparison.Ordinal) || member.StartsWith("ElevatorShaftLanding::", StringComparison.Ordinal)
        || member.StartsWith("GlobalZoneIndex::", StringComparison.Ordinal)
        || member is "Globals.Global::get_RundownIdToLoad" or "RundownManager::GetActiveExpeditionData"
            or "RundownManager::get_ActiveExpeditionUniqueKey" || member.StartsWith("pActiveExpedition::", StringComparison.Ordinal);

    /// <summary>The pure query spellings: game members whose name does not start with `get_` and which are reads
    /// all the same, exactly as the audit lists them.</summary>
    private static readonly string[] ReadSpellings =
        { "TryCast", "op_Equality", "op_Inequality", "op_Implicit", "PlayerSlotIndex", "GetActiveExpeditionData", "GetComponentInParent" };

    /// <summary>The query members whose names the spellings above do not catch: the audit's own one-by-one list,
    /// kept here in the same spelling and the same order, so this generator writes a readback row for each one
    /// instead of a write row. A member added to one list and not the other is a row the audit fails by name.</summary>
    private static readonly string[] QueryMembers =
    {
        "AssetShards.AssetShardManager::GetLoadedAsset",
        "ChainedPuzzles.ChainedPuzzleInstance::NRofPuzzles",
        "EnvironmentStateManager::GetCurrentFogID",
        "EnvironmentStateManager::GetLightMode",
        "GameData.GameDataBlockBase`1::GetAllBlocks",
        "GameData.GameDataBlockBase`1::GetBlock",
        "GlobalZoneIndex::.ctor",
        "ItemEquippable::GetClassAmmoInPackAbs",
        "ItemEquippable::GetClassAmmoMaxCap",
        "ItemEquippable::GetCurrentClip",
        "ItemEquippable::GetMaxClip",
        "LevelGeneration.LG_ComputerTerminal::CommandIsHidden",
        "LevelGeneration.LG_ComputerTerminalCommandInterpreter::TryGetCommand",
        "Mastermind::TryGetEvent",
        "Player.PlayerAgent::SampleWarpPosition",
        "Player.PlayerBackpackManager::TryGetBackpack",
        "SNetwork.SNetStructs/pPlayer::TryGetPlayer",
        "WardenObjectiveManager::HasWardenObjectiveDataForLayer",
        "WardenObjectiveManager::TryGetWardenObjective",
        "pWardenObjectiveState::GetChainIndexForLayer",
        "pWardenObjectiveState::GetLayerStatus",
        "pWardenObjectiveState::GetLayerSubStatus",
        "pWardenObjectiveState::GetStartTimeFromLayer"
    };

    private static IEnumerable<TypeDefinition> Walk(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Walk));

    private static string Scope(MethodReference method)
        => method.DeclaringType.Scope is AssemblyNameReference scope ? scope.Name : "";

    private static string Member(MethodReference method)
        => (method.DeclaringType is GenericInstanceType generic ? generic.ElementType.FullName : method.DeclaringType.FullName)
            + "::" + method.Name;

    private static IEnumerable<TypeDefinition> Chain(IReadOnlyList<TypeDefinition> gameTypes, TypeDefinition declared)
    {
        for (var current = declared; current != null;)
        {
            yield return current;
            var name = current.BaseType?.FullName;
            current = name == null ? null : gameTypes.FirstOrDefault(t => t.FullName == name);
        }
    }

    /// <summary>The declaration a call binds to, resolved along the declaring type's base chain by name and
    /// parameter types, exactly as the audit resolves it. A member the interop assemblies do not declare answers
    /// nothing and carries no row.</summary>
    private static MethodDefinition? Resolve(IReadOnlyList<TypeDefinition> gameTypes, MethodReference call)
    {
        var ownerName = call.DeclaringType is GenericInstanceType generic ? generic.ElementType.FullName : call.DeclaringType.FullName;
        var owner = gameTypes.FirstOrDefault(t => t.FullName == ownerName);
        if (owner == null) return null;
        return Chain(gameTypes, owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == call.Name
            && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(call.Parameters.Select(p => p.ParameterType.FullName)));
    }

    /// <summary>The member an existing readback row documents, or null when the row names a member the interop
    /// assemblies no longer declare: such a row is stale and is dropped rather than kept.</summary>
    private static string? ResolveRow(Inputs inputs, JsonObject row)
    {
        var owner = inputs.GameTypes.FirstOrDefault(t => t.FullName == (string?)row["type"]);
        if (owner == null) return null;
        var name = Name((string)row["signature"]!);
        var declared = Chain(inputs.GameTypes, owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == name);
        return declared == null ? null : Member(declared);
    }

    /// <summary>The declaration an existing write row documents, or null when the row names a form the interop
    /// assemblies no longer declare: such a row is stale and is dropped rather than kept. The whole signature is
    /// matched, so the forms of one overloaded name keep their own rows.</summary>
    private static string? ResolveWrite(Inputs inputs, JsonObject row)
    {
        var owner = inputs.GameTypes.FirstOrDefault(t => t.FullName == (string?)row["type"]);
        var signature = (string?)row["signature"];
        var declared = owner == null || signature == null
            ? null : Chain(inputs.GameTypes, owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.FullName == signature);
        return declared?.FullName;
    }

    /// <summary>The door/terminal fact half's own guard, which is the session gate a door or terminal readback
    /// runs inside when its body does not call a session guard directly. `Guard` is the same session entry the
    /// `GuardMapObjects` spelling is: it runs the callback on the owning thread and drops it while the half is
    /// not attached.</summary>
    private static string? Facts(MethodDefinition body)
    {
        foreach (var holder in new[] { body }.Concat(body.DeclaringType.NestedTypes.SelectMany(t => t.Methods)))
        {
            if (!holder.HasBody) continue;
            if (holder.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Any(m => m.DeclaringType.FullName == "ForgeMap.Native.DoorTerminalFacts" && m.Name.StartsWith("Guard", StringComparison.Ordinal)))
                return "Guard";
        }
        return null;
    }
    /// <summary>The session guard a patch reaches the half through, by the body's own calls and by the calls of
    /// the display class the compiler moved a lambda into. Nothing is loaded until the session exists, so the
    /// guard is what a patch's whole callback runs inside.</summary>
    private static string? Guard(MethodDefinition body)
    {
        var closures = body.DeclaringType.NestedTypes
            .Where(t => t.Name.StartsWith("<>", StringComparison.Ordinal) || t.Name.StartsWith("<", StringComparison.Ordinal));
        var helpers = body.DeclaringType.Methods.Where(m => m.HasBody && m != body
            && body.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Any(c => c.Name == m.Name && c.DeclaringType.FullName == body.DeclaringType.FullName));
        foreach (var holder in new[] { body }.Concat(helpers).Concat(closures.SelectMany(t => t.Methods)))
        {
            if (!holder.HasBody) continue;
            var calls = holder.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Where(m => m.DeclaringType.FullName == "ForgeMap.Native.MapPluginSession").ToArray();
            if (calls.FirstOrDefault(m => m.Name.StartsWith("Guard", StringComparison.Ordinal)) is { } guard)
                return guard.Name;
        }
        return null;
    }
    private static string Name(string signature)
    {
        int start = signature.LastIndexOf("::", StringComparison.Ordinal) + 2;
        int end = signature.IndexOf('(', start);
        return end < 0 ? signature[start..] : signature[start..end];
    }

    private static string Assembly(Inputs inputs, string typeName)
    {
        var owner = inputs.GameTypes.FirstOrDefault(t => t.FullName == typeName);
        return owner == null ? "Modules-ASM.dll" : owner.Module.Assembly.Name.Name + ".dll";
    }

    private static string Id(string member)
    {
        string owner = member[..member.LastIndexOf("::", StringComparison.Ordinal)];
        string name = member[(member.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
        return (owner[(owner.LastIndexOf('.') + 1)..] + "-" + name).Replace('_', '-').ToLowerInvariant();
    }

    /// <summary>The id of a form of an overloaded name: the member's own id with the parameter list the form
    /// takes, so two rows of one name are two ids in the audit's messages instead of one.</summary>
    private static string Id(Call call)
    {
        int open = call.Signature.IndexOf('(');
        string parameters = open < 0 ? "" : call.Signature[(open + 1)..].TrimEnd(')');
        return parameters.Length == 0 ? Id(call.Member) : Id(call.Member) + "-" + parameters.Replace('_', '-').ToLowerInvariant();
    }
}
