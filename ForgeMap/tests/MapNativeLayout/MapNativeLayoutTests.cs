using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ForgeMap.Tests.MapNativeLayout;

// Compiled-assembly metadata only: proves boundaries, plugin identity and hook shape, not native call timing.
// Inputs come from the environment (see ConfiguredInputs); the suite is excluded by the Native trait by default.
[Trait("Category", "Native")]
public sealed class MapNativeLayoutTests
{
    private static readonly string BepInEx = Environment.GetEnvironmentVariable("FORGE_MAP_BEPINEX")
        ?? Environment.GetEnvironmentVariable("GTFO_BEPINEX_PATH") ?? "";
    private static readonly string Artifacts = Environment.GetEnvironmentVariable("FORGE_ARTIFACTS") ?? "";
    private static readonly string Spec = Environment.GetEnvironmentVariable("FORGE_MAP_HOOK_SPEC")
        ?? Path.Combine("ForgeMap", "evidence", "map-hooks.json");

    private static string Artifact(string relative) => Path.Combine(Artifacts, relative);

    private static void RequireInputs()
    {
        Assert.False(string.IsNullOrWhiteSpace(BepInEx) || string.IsNullOrWhiteSpace(Artifacts),
            "Set FORGE_MAP_BEPINEX (or GTFO_BEPINEX_PATH) and FORGE_ARTIFACTS to an existing build output.");
    }

    /// <summary>Rewrites the member rows of the evidence file from the compiled plugin and the interop
    /// assemblies, when `FORGE_MAP_LAYOUT_REGEN` names the file to write. The audit's own reading of the module is
    /// what an evidence row has to agree with, so the rows are derived from that reading instead of typed beside
    /// it: a member the plugin starts or stops calling changes this file or the suite fails.</summary>
    [Fact]
    public void evidence_member_rows_are_regenerated_when_asked()
    {
        string target = Environment.GetEnvironmentVariable("FORGE_MAP_LAYOUT_REGEN") ?? "";
        if (string.IsNullOrWhiteSpace(target)) return;
        RequireInputs();
        var inputs = Read();
        EvidenceRegenerator.Run(inputs, Spec, target);
    }

    /// <summary>The audit's own reading of the module and the interop assemblies, without its assertions.</summary>
    private static EvidenceRegenerator.Inputs Read()
    {
        string specPath = Spec;
        using var native = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeMap.Native/release/ForgeMap.Native.dll"));
        using var modules = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "Modules-ASM.dll"));
        using var snet = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "SNet_ASM.dll"));
        using var data = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "GameData-ASM.dll"));
        // The fourth game assembly this module reads: the asset table the room resolver loads an authored room
        // reference through. A member of it carries a row like any other, so the assembly that declares it is
        // opened here and the row's own type resolves instead of reading as an unresolved spelling.
        using var shards = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "Shards-ASM.dll"));
        using var specDocument = JsonDocument.Parse(File.ReadAllText(specPath));
        return EvidenceRegenerator.Read(native, new[] { modules, snet, data, shards }, specDocument);
    }

    [Fact]
    public void map_native_layout_checks_all_pass()
    {
        RequireInputs();
        var checks = Audit();
        Dump(checks);
        string failed = string.Join(" | ", checks.Where(c => !c.Passed).Select(c => c.Id + ": " + c.Detail));
        Assert.True(checks.Any(c => c.Id == "map.game-independent"), "The audit did not run to completion: " + failed);
        Assert.True(checks.Count(c => !c.Passed) == 0, failed);
        Assert.True(checks.Count >= 30, "Expected the full layout set, observed " + checks.Count + " checks.");
    }

    private static List<CheckRow> Audit()
    {
        var checks = new List<CheckRow>();
        void Check(string id, bool passed, string detail = "") => checks.Add(new(id, passed, detail));
        IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
        using var sdk = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll"));
        using var host = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeRuntime/release/ForgeRuntime.dll"));
        using var map = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeMap/release/ForgeMap.dll"));
        using var native = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeMap.Native/release/ForgeMap.Native.dll"));
        using var modules = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "Modules-ASM.dll"));
        using var snet = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "SNet_ASM.dll"));
        using var data = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "GameData-ASM.dll"));
        // The asset table the room resolver loads an authored room reference through: the fourth game assembly
        // this module reads, and the one its readback row has to resolve against.
        using var shards = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "Shards-ASM.dll"));
        using var specDocument = JsonDocument.Parse(File.ReadAllText(Spec));
        var specHooks = specDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
        var specReadbacks = specDocument.RootElement.GetProperty("readbacks").EnumerateArray().ToArray();
        // The action layer's write entries are declared the same way the reads are: one row per game member, in
        // the same spec, each documented by its own member row in evidence/door-terminal-hooks.json.
        var specWrites = specDocument.RootElement.GetProperty("writes").EnumerateArray().ToArray();
        var nativeTypes = native.MainModule.Types.SelectMany(Walk).ToArray();
        var mapTypes = map.MainModule.Types.SelectMany(Walk).ToArray();
        var gameTypes = modules.MainModule.Types.Concat(snet.MainModule.Types).Concat(data.MainModule.Types)
            .Concat(shards.MainModule.Types).SelectMany(Walk).ToArray();
        bool GameAssembly(string name) => name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("BepInEx", StringComparison.Ordinal)
            || name.Contains("Harmony", StringComparison.Ordinal) || name.StartsWith("Il2Cpp", StringComparison.Ordinal) || name.EndsWith("-ASM", StringComparison.Ordinal) || name.EndsWith("_ASM", StringComparison.Ordinal);
        string[] Forge(AssemblyDefinition assembly) => assembly.MainModule.AssemblyReferences.Where(r => r.Name.StartsWith("Forge", StringComparison.Ordinal))
            .Select(r => r.FullName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var instructions = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
        var calls = instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        string Scope(MethodReference m) => m.DeclaringType.Scope is AssemblyNameReference scope ? scope.Name : "";
        string Member(MethodReference m) => (m.DeclaringType is GenericInstanceType g ? g.ElementType.FullName : m.DeclaringType.FullName) + "::" + m.Name;
        // The ForgeMap sources the plugin's own constants are copied from: the audit runs beside the evidence file
        // it checks, and the rows that carry these ids are declared in the package one directory above it. A spec
        // that does not sit in a package's evidence directory answers no source at all, which fails the identity
        // check by name instead of passing it silently.
        string ModuleSource()
        {
            var package = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(Spec)));
            return package == null || !Directory.Exists(package) ? ""
                : string.Concat(Directory.GetFiles(package, "*.cs").OrderBy(x => x, StringComparer.Ordinal).Select(File.ReadAllText));
        }

        Check("map.game-independent", !map.MainModule.AssemblyReferences.Any(r => GameAssembly(r.Name)));
        Check("map.only-sdk", Forge(map).SequenceEqual(new[] { sdk.Name.FullName }), string.Join(", ", Forge(map)));
        Check("native.forge-references", Forge(native).SequenceEqual(new[] { sdk.Name.FullName, host.Name.FullName, map.Name.FullName }.OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(", ", Forge(native)));
        Check("host.no-map-reference", !Forge(host).Any(n => n.StartsWith("ForgeMap", StringComparison.Ordinal)), string.Join(", ", Forge(host)));
        Check("native.no-embedded-kernel", !nativeTypes.Concat(mapTypes).Any(t => t.Name == "RuntimeKernel"));
        Check("native.no-update-loop", !nativeTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"));

        // One provider identity: the native plugin extends ForgeMap.ModuleDefinition instead of declaring a second
        // registry. The `forge.module.` ids the plugin carries are compile-time copies of ForgeMap's own constants —
        // a `const` is inlined into every assembly that uses it — so the rule is not "the plugin carries none",
        // which would only hold while no row of this provider is declared as a constant, but "every id it carries
        // is one ForgeMap's own source spells": an id invented on the native side is an id no ForgeMap line writes.
        var moduleIds = instructions.Select(i => i.Operand).OfType<string>()
            .SelectMany(text => Regex.Matches(text, @"forge\.module\.[A-Za-z0-9_.]+").Select(m => m.Value))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var moduleSource = ModuleSource();
        var inventedIds = moduleIds.Where(id => !moduleSource.Contains(id, StringComparison.Ordinal)).ToArray();
        Check("native.single-provider-source", calls.Any(m => Member(m) == "ForgeMap.ModuleDefinition::Create")
            && !instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference c && c.DeclaringType.FullName == "ForgeRuntime.Framework.RuntimeModule")
            && inventedIds.Length == 0,
            inventedIds.Length == 0 ? "" : "ids no ForgeMap source spells: " + string.Join(", ", inventedIds));
        // The player half's whole ownership surface on the one registration: the resolver, the instance lookup,
        // the observer and the candidate source that names the same kind.
        Check("native.player-resolver-instance-lookup-and-observer", calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeModule::set_EntityResolvers")
            && calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeModule::set_EntityInstanceResolvers")
            && calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeModule::set_EntityObservers")
            && calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeModule::set_EntityCandidates")
            && instructions.Any(i => i.Operand is string s && s == "gtfo.player"));
        // Only the spawn/despawn readback allocates lives; the SDK instance lookup reads the recorded table and nothing else.
        var lookup = nativeTypes.Where(t => t.Name == "PlayerIdentityModule").SelectMany(t => t.Methods).SingleOrDefault(m => m.Name == "ResolveInstance");
        var lookupCalls = lookup?.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray() ?? Array.Empty<MethodReference>();
        Check("native.instance-lookup-never-allocates", lookup != null && lookup.Parameters.Count == 1 && lookup.Parameters[0].ParameterType.FullName == "System.Object"
            && !lookup.Body.Instructions.Any(i => i.OpCode.Code is Code.Stfld or Code.Stsfld)
            && !lookupCalls.Any(m => m.Name is "Reconcile" or "Add" or "Remove" or "Clear" or "get_Lookup" or "get_PlayerAgentsInLevel"),
            string.Join(", ", lookupCalls.Select(Member).Distinct(StringComparer.Ordinal)));
        Check("native.no-gameplay-gate", !calls.Any(m => m.Name == "get_CanExecuteGameplay"));
        // SNet_Player.Lookup is a Steam64 account ID: compared and used as a private dictionary key, never formatted or boxed.
        Check("native.account-lookup-never-formatted", !calls.Any(m => m.DeclaringType.FullName == "System.UInt64" && m.Name == "ToString")
            && !instructions.Any(i => i.OpCode == OpCodes.Box && i.Operand is TypeReference t && t.FullName == "System.UInt64")
            && !calls.Any(m => m is GenericInstanceMethod g && g.GenericArguments.Any(a => a.FullName == "System.UInt64"))
            && !calls.Any(m => m.Parameters.Any(p => p.ParameterType.FullName == "System.UInt64") && m.DeclaringType.FullName is "System.String" or "System.Convert"));

        // Native state is read, and the only writes are the action layer's declared entries below: identity reads
        // only the player key and agent links, and no name or slot is ever read or written.
        var gameCalls = calls.Where(m => GameAssembly(Scope(m)) && !Scope(m).StartsWith("BepInEx", StringComparison.Ordinal)
                && !Scope(m).Contains("Harmony", StringComparison.Ordinal))
            .Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // Game members that are reads although their name does not start with `get_`: the cast and operator
        // spellings the identity code uses, `PlayerSlotIndex`, the one game accessor spelled without `get_`, the
        // rundown manager's own lookup of the active expedition, and Unity's hierarchy lookup the hit path climbs
        // a bullet's collider with.
        string[] readSpellings = { "TryCast", "op_Equality", "op_Inequality", "op_Implicit", "PlayerSlotIndex", "GetActiveExpeditionData", "GetComponentInParent" };
        // The engine's own interop members — the object base, the interop collections, Unity's transform and
        // Unity's text package — are not the game's types and carry no evidence row of their own; every other
        // game call must be declared. A member of one of these assemblies cannot carry a row even in principle:
        // the three game interop assemblies this audit opens are the only ones a row resolves against, so an
        // engine call left in the write set below is a write no row could ever satisfy.
        bool EngineCall(string member) => member.StartsWith("Il2CppInterop.", StringComparison.Ordinal)
            || member.StartsWith("Il2CppSystem.", StringComparison.Ordinal)
            || member.StartsWith("UnityEngine.", StringComparison.Ordinal)
            || member.StartsWith("TMPro.", StringComparison.Ordinal);
        var engineCalls = gameCalls.Where(EngineCall).ToArray();
        // Game members that return information without changing any: the names the `get_` spelling does not catch,
        // because the game spells them as a lookup, a question or a value construction. Each one is named here
        // rather than matched by a spelling rule, and each is declared as a read below: a pure query counted as a
        // write would make the observation half that calls it look like an action layer.
        string[] readMembers =
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
        // The write spelling: a non-getter game call that is not one of the named pure queries. The door and
        // terminal execution layer is a write layer by definition, so these calls are declared one by one below
        // and every one of them has to be declared: a non-getter call no row names is a write nobody reviewed.
        // The engine's own members are not part of that set — the read set below subtracts them for the same
        // reason — so the comparison is between the game calls and the rows, both sides engine-free.
        var writes = gameCalls.Where(c => !EngineCall(c)).Where(c => { var name = c[(c.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
            return !name.StartsWith("get_", StringComparison.Ordinal) && !readSpellings.Contains(name) && !readMembers.Contains(c); }).ToArray();
        // The observer's reads span the agent base type, the player types, the damage-base types the health reads
        // come from, and the network session types. The player half's own presentation and inventory reads are of
        // the same kind: the HUD manager, the local player's status layer and its two text figures, the marker a
        // ping placed on a player, the item a player wields and carries, the player's own interaction target, and
        // the item a life is holding. Every game call this module makes is one of them or of the set below.
        bool GameRead(string member) => member.StartsWith("Player.", StringComparison.Ordinal)
            || member.StartsWith("SNetwork.", StringComparison.Ordinal) || member.StartsWith("Agents.", StringComparison.Ordinal)
            || member.StartsWith("Dam_", StringComparison.Ordinal)
            || member.StartsWith("GuiManager::", StringComparison.Ordinal) || member.StartsWith("PlayerGuiLayer::", StringComparison.Ordinal)
            || member.StartsWith("PUI_", StringComparison.Ordinal) || member.StartsWith("PLOC_", StringComparison.Ordinal)
            || member.StartsWith("PlayerInventoryBase::", StringComparison.Ordinal) || member.StartsWith("ItemEquippable::", StringComparison.Ordinal)
            || member.StartsWith("Item::", StringComparison.Ordinal) || member.StartsWith("PlayerInteraction::", StringComparison.Ordinal)
            || member.StartsWith("Interact_Timed::", StringComparison.Ordinal) || member.StartsWith("NavMarker::", StringComparison.Ordinal)
            || member.StartsWith("PlaceNavMarkerOnGO::", StringComparison.Ordinal);
        // The map-object readers' own types: the level generation the zone table is built from, the level's
        // data block, the course node a terminal reaches its zone through, the localization value type the
        // door's own no-key prompt is built from, and the level identity a `level` mount is compared against —
        // the rundown block the process loaded and the active expedition's own tier, index and key.
        bool LevelRead(string member) => member is "Globals.Global::get_RundownIdToLoad" or "RundownManager::GetActiveExpeditionData"
            or "RundownManager::get_ActiveExpeditionUniqueKey" || member.StartsWith("pActiveExpedition::", StringComparison.Ordinal);
        // The level's own objects and machines beside that table: the asset a room reference loads, the alarm and
        // scan puzzles, the generators the generator rows read, the objective machine and its state block, the
        // environment state the environment rows read, the encounter director, the world event manager, the
        // elevator landing whose win condition the extraction row sets, and the zone index a lighting read takes.
        bool MapObjectRead(string member) => member.StartsWith("LevelGeneration.", StringComparison.Ordinal)
            || member.StartsWith("GameData.", StringComparison.Ordinal) || member.StartsWith("AIGraph.", StringComparison.Ordinal)
            || member.StartsWith("Localization.", StringComparison.Ordinal)
            || member.StartsWith("AssetShards.", StringComparison.Ordinal) || member.StartsWith("ChainedPuzzles.", StringComparison.Ordinal)
            || member.StartsWith("EnvironmentStateManager::", StringComparison.Ordinal) || member.StartsWith("WardenObjective", StringComparison.Ordinal)
            || member.StartsWith("IWardenObjective::", StringComparison.Ordinal)
            || member.StartsWith("pWardenObjectiveState::", StringComparison.Ordinal) || member.StartsWith("WO_", StringComparison.Ordinal)
            || member.StartsWith("LG_", StringComparison.Ordinal) || member.StartsWith("Mastermind::", StringComparison.Ordinal)
            || member.StartsWith("WorldEventManager::", StringComparison.Ordinal) || member.StartsWith("ElevatorShaftLanding::", StringComparison.Ordinal)
            || member.StartsWith("GlobalZoneIndex::", StringComparison.Ordinal)
            || LevelRead(member);
        // Which game member a readback or write row names is resolved against the interop assemblies by member
        // name along the declaring type's base chain: a member named on a derived type may be declared on its
        // base. Types are compared by name, never resolved, because the interop assemblies reference game
        // assemblies this audit does not open. The resolved member — not the spec's spelling — is what each set
        // is compared against. Walking the chain in order makes the first declaration found the most derived
        // one, which is the declaration a call site on the named type binds to.
        IEnumerable<TypeDefinition> Chain(TypeDefinition declared)
        {
            for (var current = declared; current != null;)
            {
                yield return current;
                var name = current.BaseType?.FullName;
                current = name == null ? null : gameTypes.FirstOrDefault(t => t.FullName == name);
            }
        }
        MethodDefinition? Resolve(JsonElement row)
        {
            var owner = gameTypes.FirstOrDefault(t => t.FullName == row.GetProperty("type").GetString());
            if (owner == null) return null;
            var name = SignatureName(row.GetProperty("signature").GetString()!);
            return Chain(owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == name);
        }
        // The declaration an actual read binds to, resolved from the member name the IL spells along its
        // declaring type's base chain the same way a spec row is: the call site names the type the reader called
        // through, the spec names the type it documented, and a member declared on a base type is one read either
        // way. A name this audit cannot resolve against the interop assemblies is compared as it was spelled.
        string Resolved(string member)
        {
            int split = member.LastIndexOf("::", StringComparison.Ordinal);
            if (split < 0) return member;
            var owner = gameTypes.FirstOrDefault(t => t.FullName == member[..split]);
            var declared = owner == null ? null
                : Chain(owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == member[(split + 2)..]);
            return declared == null ? member : Member(declared);
        }
        // The whole signature of an actual call, with the declaration it binds to resolved along the base chain
        // by member name and parameter types: a call spelled on a derived type still compares equal to the row
        // that documents the base declaration, and an overloaded name resolves to the one form the call used.
        string CallSignature(MethodReference call)
        {
            var ownerName = call.DeclaringType is GenericInstanceType g ? g.ElementType.FullName : call.DeclaringType.FullName;
            var owner = gameTypes.FirstOrDefault(t => t.FullName == ownerName);
            var declared = owner == null ? null : Chain(owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == call.Name
                && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(call.Parameters.Select(p => p.ParameterType.FullName)));
            return declared == null ? call.FullName : declared.FullName;
        }
        // The action layer's write entries, declared by the spec the same way the reads are and resolved the same
        // way: each row names the game member the compiled plugin must call, and each is documented by a member
        // row of evidence/door-terminal-hooks.json that carries the build's own RVA and the bytes at it. The set
        // is compared both ways — an undeclared non-getter call fails, and a declared entry the plugin stopped
        // calling fails — so it cannot rot into a loosened whitelist.
        var declaredWrites = specWrites.Select(r => (Row: r, Member: Resolve(r))).ToArray();
        string unresolvedWrites = string.Join(", ", declaredWrites.Where(x => x.Member == null)
            .Select(x => x.Row.GetProperty("id").GetString()));
        var declaredWriteMembers = declaredWrites.Where(x => x.Member != null).Select(x => Member(x.Member!))
            .Distinct(StringComparer.Ordinal).ToArray();
        var declaredWriteSignatures = declaredWrites.Select(x => x.Row.GetProperty("signature").GetString()!)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        bool DeclaredWrite(string member) => declaredWriteMembers.Contains(member, StringComparer.Ordinal)
            || declaredWriteMembers.Contains(Resolved(member), StringComparer.Ordinal);
        // The comparison runs on the whole signature and not on the member name: the terminal's AddLine is
        // overloaded, so a row that pinned the wrong form, or a call site that reached another one, is reported.
        var writeSignatures = calls.Where(m => DeclaredWrite(Member(m))).Select(CallSignature)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.declared-writes-exact", unresolvedWrites.Length == 0 && writeSignatures.SequenceEqual(declaredWriteSignatures),
            "declared=" + declaredWriteSignatures.Length + " actual=" + writeSignatures.Length
            + (unresolvedWrites.Length == 0 ? "" : " unresolved: " + unresolvedWrites)
            + " missing=" + string.Join(", ", declaredWriteSignatures.Except(writeSignatures))
            + " extra=" + string.Join(", ", writeSignatures.Except(declaredWriteSignatures)));
        var undeclaredWrites = writes.Where(c => !DeclaredWrite(c)).ToArray();
        Check("native.game-writes-are-declared", gameCalls.Length > 0 && undeclaredWrites.Length == 0,
            undeclaredWrites.Length == 0 ? string.Join(", ", writes) : string.Join(", ", undeclaredWrites));
        // The declared entries are the action layer's own: the observation and query halves of this module write
        // nothing, so a write called from anywhere else would be exactly the state change the read-only rule
        // forbade. The list is the action layer of both domains — the map-object commands and presentations, the
        // agent modifier, the level events, the HUD and marker rows, the player health, command, damage and
        // inventory actions, and the door, terminal, objective and alarm rows — together with the two compiler
        // generated closure classes the level-event and environment rows build their own display values in.
        var writeCallSites = nativeTypes.SelectMany(t => t.Methods.Select(m => (Type: t.Name, Method: m)))
            .Where(x => x.Method.HasBody)
            .SelectMany(x => x.Method.Body.Instructions.Select(i => (x.Type, Call: i.Operand)))
            .Where(x => x.Call is MethodReference r && DeclaredWrite(Member(r)))
            .Select(x => x.Type).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.writes-live-in-the-action-layer",
            writeCallSites.SequenceEqual(new[]
            {
                "<>c__DisplayClass0_0", "<>c__DisplayClass1_0", "AgentModifierAdapter", "AlarmWaveActions", "DoorActions",
                "DoorTerminalActions", "EnvironmentActions", "EnvironmentPresentation", "HudActions", "LevelEventActions",
                "NativeMarker", "ObjectiveActions", "PlayerActions", "PlayerCommandActions", "PlayerHealthReceiver",
                "TerminalActions", "TerminalObjectActions"
            }),
            string.Join(", ", writeCallSites));
        // A declared write is not a read: both the raw spelling and the declaration it binds to are subtracted
        // from the read sets below, so the reads a spec row declares stay exactly the reads the module makes.
        var gameReads = gameCalls.Where(c => !EngineCall(c) && !DeclaredWrite(c)).ToArray();
        // Every call this module makes to a game type is one of the two read sets below, and neither set may
        // grow without its evidence row: a member read but not declared, or declared but no longer read, fails.
        Check("native.reads-are-declared", gameReads.All(c => GameRead(c) || MapObjectRead(c)),
            string.Join(", ", gameReads.Where(c => !GameRead(c) && !MapObjectRead(c))));
        var identityReads = gameReads.Where(GameRead).ToArray();
        var declaredReads = specReadbacks.Select(r => (Row: r, Member: Resolve(r))).ToArray();
        // Only the game's own reads are declared in the spec, so a row whose member the interop assemblies do
        // not declare is either a stale row or a spelling the audit cannot resolve — both reported as unresolved.
        string unresolved = string.Join(", ", declaredReads.Where(x => x.Member == null)
            .Select(x => x.Row.GetProperty("id").GetString()));
        var expectedReads = declaredReads.Where(x => x.Member != null && GameRead(Member(x.Member!))).Select(x => Member(x.Member!))
            .Append("SNetwork.SNet::get_IsMaster").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.exact-player-reads", unresolved.Length == 0 && identityReads.SequenceEqual(expectedReads),
            unresolved.Length == 0 ? string.Join(", ", identityReads) : "unresolved: " + unresolved);
        // The map-object readers read the level's zone table and the objects in it, and the spec names every one
        // of those reads too: a coordinate or a member added without its evidence row fails here.
        var expectedMapReads = declaredReads.Where(x => x.Member != null && MapObjectRead(Member(x.Member!)))
            .Select(x => Member(x.Member!)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var mapReads = gameReads.Where(MapObjectRead).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // The comparison runs on resolved members, not on the names the IL or the spec spell: a member declared
        // on a base type is the same read whichever type the call site or the evidence row names it through.
        var resolvedMapReads = mapReads.Select(Resolved).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.exact-map-object-reads", resolvedMapReads.SequenceEqual(expectedMapReads),
            "rows=" + specReadbacks.Length + " resolved=" + declaredReads.Count(x => x.Member != null)
            + " actual=" + resolvedMapReads.Length + " expected=" + expectedMapReads.Length
            + " missing=" + string.Join(", ", resolvedMapReads.Except(expectedMapReads))
            + " extra=" + string.Join(", ", expectedMapReads.Except(resolvedMapReads)));
        Check("native.every-game-read-is-classified", gameReads.Length == identityReads.Length + mapReads.Length
            && engineCalls.Length > 0,
            "game reads=" + gameReads.Length + " player=" + identityReads.Length + " map-object=" + mapReads.Length
            + " engine=" + engineCalls.Length);
        foreach (var (row, member) in declaredReads)
        {
            string id = row.GetProperty("id").GetString()!;
            Check("readback." + id + ".game-member", member != null && member.IsStatic == row.GetProperty("isStatic").GetBoolean(),
                row.GetProperty("signature").GetString() + (member == null ? "" : " declared by " + member.DeclaringType.FullName));
        }
        foreach (var (row, _) in declaredWrites)
        {
            string id = row.GetProperty("id").GetString()!;
            string signature = row.GetProperty("signature").GetString()!;
            // A write row is pinned by its whole signature and not by its member name: the terminal's AddLine has
            // three forms and only one takes a line kind, so the row has to name the declaration it means, exactly
            // once, with the static flag the interop assembly carries.
            var owner = gameTypes.FirstOrDefault(t => t.FullName == row.GetProperty("type").GetString());
            var pinned = owner == null ? Array.Empty<MethodDefinition>()
                : Chain(owner).SelectMany(t => t.Methods).Where(m => m.FullName == signature).ToArray();
            Check("write." + id + ".game-member", pinned.Length == 1 && pinned[0].IsStatic == row.GetProperty("isStatic").GetBoolean(),
                string.Join(" | ", pinned.Select(m => m.FullName)));
        }
        var harmonyCalls = calls.Where(m => Scope(m).Contains("Harmony", StringComparison.Ordinal)).Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.harmony-install-and-unpatch-only", harmonyCalls.All(c => c is "HarmonyLib.Harmony::.ctor" or "HarmonyLib.Harmony::CreateClassProcessor"
            or "HarmonyLib.PatchClassProcessor::Patch" or "HarmonyLib.Harmony::UnpatchSelf"), string.Join(", ", harmonyCalls));

        bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
        var hooks = nativeTypes.Where(Patch).ToArray();
        Check("native.exact-hook-set", hooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(specHooks.Select(h => h.GetProperty("hook").GetString()!).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(", ", hooks.Select(t => t.Name)));
        // The parameters a postfix body loads, by the ldarg/ldarga that loads them. Whether a hook may read its
        // arguments is declared per hook, so an undeclared read fails that hook's own row instead of passing.
        IEnumerable<ParameterDefinition> parameterReads(MethodDefinition method)
        {
            // A parameter a lambda in the body captures is not loaded by the body itself: the compiler hoists it
            // into the body's own display class and passes that instance to the lambda, so the assembly spells the
            // read as a field of the display class. One alias per hoisted parameter is gathered here — where the
            // body's own instructions store the parameter into its display class — and every name the body or a
            // lambda on that class loads is then answered as the parameter it stands for.
            var alias = new Dictionary<string, string>(StringComparer.Ordinal);
            var display = method.DeclaringType.NestedTypes.Where(t => t.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal)).ToArray();
            var arguments = new List<ParameterDefinition>();
            foreach (var instruction in method.Body.Instructions)
            {
                var parameter = instruction.OpCode.Code switch
                {
                    Code.Ldarg_0 => method.Parameters.ElementAtOrDefault(0),
                    Code.Ldarg_1 => method.Parameters.ElementAtOrDefault(1),
                    Code.Ldarg_2 => method.Parameters.ElementAtOrDefault(2),
                    Code.Ldarg_3 => method.Parameters.ElementAtOrDefault(3),
                    Code.Ldarg or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S => instruction.Operand as ParameterDefinition,
                    _ => null
                };
                if (parameter != null)
                {
                    arguments.Add(parameter);
                    continue;
                }
                // The store into the display class is the parameter's other spelling: the argument loaded just
                // before it is the one being hoisted. Whether it was read is decided by the loads below, because
                // a field the body only ever writes is not a read.
                if (instruction.OpCode.Code == Code.Stfld && instruction.Operand is FieldReference field
                    && display.Any(t => t.FullName == field.DeclaringType.FullName) && arguments.Count != 0)
                    alias[field.Name] = arguments[^1].Name;
            }
            foreach (var parameter in arguments)
                if (!Injected(parameter, method)) yield return parameter;
            if (alias.Count == 0) yield break;
            foreach (var holder in new[] { method }.Concat(display.SelectMany(t => t.Methods)))
            {
                if (!holder.HasBody) continue;
                foreach (var instruction in holder.Body.Instructions)
                    if (instruction.OpCode.Code is Code.Ldfld or Code.Ldflda or Code.Ldsfld or Code.Ldsflda
                        && instruction.Operand is FieldReference field && alias.TryGetValue(field.Name, out var aliasName))
                    {
                        var captured = method.Parameters.Single(p => p.Name == aliasName);
                        if (!Injected(captured, method)) yield return captured;
                    }
            }
        }
        // Harmony's own injected parameters are not arguments of the patched method: `__instance` is the instance
        // the audit declares separately, `__result` is the return value a postfix may read, and `__state` is the
        // slot a prefix writes for its postfix. None of them is a value the native body was handed, so the read
        // set a row declares is the arguments alone.
        bool Injected(ParameterDefinition parameter, MethodDefinition method)
            => parameter.Name == "__result" || parameter.Name == "__state"
                || (parameter.Name == "__instance" && Patched(method) == parameter.ParameterType.FullName);
        // The type a patch class declares it patches, read off the class's own HarmonyPatch attribute. The
        // attribute's own arguments carry the type first and the member name second, so the first type reference
        // is the patched type.
        string? Patched(MethodDefinition method)
            => method.DeclaringType.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch")
                ?.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().FirstOrDefault()?.FullName;
        // Whether a patch body changes a parameter it declares, which is the write side of the same declaration:
        // a by-ref parameter the body stores into is one the patched method reads back changed, and a parameter
        // the body only loads is one it observed. The store is found by the same argument bound the reads use, so
        // a parameter the body never touches is in neither set.
        bool IsStored(MethodDefinition method, ParameterDefinition parameter)
        {
            foreach (var instruction in method.Body.Instructions)
            {
                bool bound = instruction.OpCode.Code switch
                {
                    Code.Ldarg_0 => method.Parameters.ElementAtOrDefault(0) == parameter,
                    Code.Ldarg_1 => method.Parameters.ElementAtOrDefault(1) == parameter,
                    Code.Ldarg_2 => method.Parameters.ElementAtOrDefault(2) == parameter,
                    Code.Ldarg_3 => method.Parameters.ElementAtOrDefault(3) == parameter,
                    Code.Ldarg or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S => instruction.Operand == parameter,
                    _ => false
                };
                if (bound && instruction.OpCode.Code is Code.Starg or Code.Starg_S) return true;
                if (bound && instruction.OpCode.Code is Code.Ldarga or Code.Ldarga_S)
                {
                    // The address of the argument is taken, so the store is an indirect one: `stind` on the next
                    // instruction writes through it. Any other use of the address is a read of a by-ref argument
                    // by the callee, not a change of it.
                    int at = method.Body.Instructions.IndexOf(instruction);
                    if (at + 1 < method.Body.Instructions.Count
                        && method.Body.Instructions[at + 1].OpCode.Code is Code.Stind_I or Code.Stind_I1 or Code.Stind_I2
                            or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_R4 or Code.Stind_R8 or Code.Stind_Ref or Code.Stobj)
                        return true;
                }
                // A by-ref parameter carries its address, so it is loaded with the plain `ldarg` forms and a store
                // into it is the element type's indirect store, with the value pushed in between (`visible = false`
                // is `ldarg.1`, `ldc.i4.0`, `stind.i1`). That is the same change of the argument the `ldarga`
                // spelling makes for a by-value one, and the `starg` above never appears for a by-ref parameter.
                if (bound && parameter.ParameterType.IsByReference)
                {
                    int at = method.Body.Instructions.IndexOf(instruction);
                    for (int next = at + 1; next < method.Body.Instructions.Count && next <= at + 3; next++)
                        if (method.Body.Instructions[next].OpCode.Code is Code.Stind_I or Code.Stind_I1 or Code.Stind_I2
                            or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_R4 or Code.Stind_R8 or Code.Stind_Ref or Code.Stobj)
                            return true;
                }
            }
            return false;
        }
        // The methods one patch's whole callback runs: the patch body itself, the helpers of its own class it
        // calls by name, and the lambdas the compiler moved into that class's own display classes. A guard the
        // patch reaches through one of those is the same guard the callback runs inside, so a row that declares
        // one is answered by this set rather than by the body alone.
        IEnumerable<MethodDefinition> Callback(MethodDefinition body)
        {
            var closures = body.DeclaringType.NestedTypes.Where(t => t.Name.StartsWith("<", StringComparison.Ordinal));
            var helpers = body.DeclaringType.Methods.Where(m => m.HasBody && m != body
                && body.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                    .Any(c => c.Name == m.Name && c.DeclaringType.FullName == body.DeclaringType.FullName));
            return new[] { body }.Concat(helpers).Concat(closures.SelectMany(t => t.Methods));
        }
        foreach (var spec in specHooks)
        {
            string name = spec.GetProperty("hook").GetString()!;
            var hook = hooks.SingleOrDefault(t => t.Name == name);
            if (hook == null) { Check("hook." + name + ".present", false); continue; }
            var patch = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
            var type = patch.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
            var method = patch.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
            Check("hook." + name + ".declared-target", type.FullName == spec.GetProperty("type").GetString() && method == spec.GetProperty("name").GetString(),
                type.FullName + "::" + method);
            // The target is pinned by its whole signature and not by its name: `PostEvent` is overloaded, so a
            // row that named the wrong form would otherwise pass on a member the plugin never patched.
            var targets = gameTypes.Where(t => t.FullName == type.FullName).SelectMany(t => t.Methods.Where(m => m.Name == method))
                .Where(m => m.FullName == spec.GetProperty("signature").GetString()).ToArray();
            Check("hook." + name + ".unique-game-method", targets.Length == 1
                && targets[0].IsVirtual == spec.GetProperty("isVirtual").GetBoolean() && targets[0].IsStatic == spec.GetProperty("isStatic").GetBoolean(),
                string.Join(" | ", gameTypes.Where(t => t.FullName == type.FullName).SelectMany(t => t.Methods.Where(m => m.Name == method)).Select(m => m.FullName)));
            var patches = hook.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Namespace == "HarmonyLib"
                && a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyTranspiler" or "HarmonyFinalizer")).ToArray();
            // The kind is declared by the row and not assumed by the audit: the module carries postfixes
            // wherever the native body may run first and prefixes where the body has to be pre-empted, and each
            // row says which one it is so a patch that silently changed kind fails here.
            string kind = spec.GetProperty("patch").GetString()!;
            // The class carries exactly one method of the declared kind: a prefix beside a postfix is Harmony's
            // own state capture rather than a second hook, and a class with two of the same kind would have one
            // body the audit never reads.
            var kindMethods = patches.Where(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "Harmony" + Kind(kind))).ToArray();
            var patchBody = kindMethods.Length == 1 && kindMethods[0].IsStatic ? kindMethods[0] : null;
            Check("hook." + name + ".single-" + kind, patchBody != null, string.Join(", ", patches.Select(p => p.Name)));
            // The patch shape is declared by the hook, not assumed by the audit (see patchVocabulary in the
            // spec): the player readbacks take the patched instance and never read it, the door and terminal state
            // readbacks take and read it, and the terminal command readback has no instance but reads the entry's
            // own arguments. A declared instance must be the patched type, and every declared argument must be
            // declared with the exact interop type; the read set is compared both ways.
            var shape = spec.GetProperty(kind);
            string? instance = shape.GetProperty("instanceParameter").GetString();
            var expectedParameters = (instance == null ? Array.Empty<(string, string)>() : new[] { (instance, type.FullName) })
                .Concat(shape.GetProperty("arguments").EnumerateArray().Select(a => (a.GetProperty("name").GetString()!, a.GetProperty("type").GetString()!))).ToArray();
            // The declared shape is the patched method's own parameters, which is what a row documents: the
            // instance when the row declares one, then the method's arguments. Harmony's own slots — the state a
            // prefix writes and the result a postfix may read — are the patch's parameters and not the method's,
            // so a row declares neither.
            var parameters = patchBody?.Parameters.Where(p => p.Name is not ("__state" or "__result")).Select(p => (p.Name, p.ParameterType.FullName)).ToArray() ?? Array.Empty<(string, string)>();
            Check("hook." + name + "." + kind + "-parameters", parameters.SequenceEqual(expectedParameters),
                string.Join(", ", parameters.Select(p => p.Item1 + " " + p.Item2)));
            string[] reads = shape.GetProperty("reads").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string[] loaded = patchBody == null ? Array.Empty<string>()
                : parameterReads(patchBody).Select(p => p.Name).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Check("hook." + name + ".parameter-reads", loaded.SequenceEqual(reads), string.Join(", ", loaded));
            // The parameters a prefix changes, by the by-ref parameters its body stores through. A postfix
            // changes nothing, so its write set is empty by definition and a row that declared one fails here.
            string[] shapeWrites = shape.GetProperty("writes").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string[] stored = patchBody == null ? Array.Empty<string>()
                : patchBody.Parameters.Where(p => IsStored(patchBody, p)).Select(p => p.Name)
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Check("hook." + name + ".parameter-writes", stored.SequenceEqual(shapeWrites), string.Join(", ", stored));
            var priority = patchBody?.CustomAttributes.SingleOrDefault(a => a.AttributeType.Name == "HarmonyPriority");
            // Every patch declares its own priority explicitly: the module's convention is that a patch which
            // reads what the body left behind runs last and a patch which has to pre-empt the body runs first,
            // and a hook that follows neither has to say so rather than inherit a default nobody chose.
            Check("hook." + name + ".priority", priority != null);

            // Whether a hook reaches a guard is declared by the row, because the module has two kinds: a readback
            // whose whole callback runs inside a guard method, and a patch-level table that is this package's own
            // state and answers nothing while its half is not attached. A row that declares a guard must call it
            // somewhere in the callback — the patch body, a helper of the patch class it calls, or a lambda the
            // compiler moved into that class's own display class — and a row that declares none must not name one.
            // The gate is either the session entry (`Plugin.Session`) or the half that owns its own lifecycle
            // (`DoorTerminalFacts.Current`), which the session creates and drops with itself.
            string session = spec.GetProperty("session").GetString()!;
            var patchCalls = (patchBody == null ? Array.Empty<MethodDefinition>() : Callback(patchBody))
                .Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
            bool reachesSession = patchCalls.Any(m => Member(m) is "ForgeMap.Native.Plugin::get_Session" or "ForgeMap.Native.DoorTerminalFacts::get_Current");
            string? declaredGuard = shape.GetProperty("guard").GetString();
            Check("hook." + name + ".guarded-by-session", session == "off"
                ? declaredGuard == null
                : reachesSession && patchCalls.Any(m => Member(m) == "ForgeMap.Native.MapPluginSession::" + declaredGuard
                    || Member(m) == "ForgeMap.Native.DoorTerminalFacts::" + declaredGuard),
                session + " " + declaredGuard);
        }

        var plugins = nativeTypes.Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin")).ToArray();
        Check("plugin.single-entry", plugins.Length == 1 && plugins[0].FullName == "ForgeMap.Native.Plugin"
            && plugins[0].BaseType?.FullName == "BepInEx.Unity.IL2CPP.BasePlugin", string.Join(", ", plugins.Select(p => p.FullName)));
        string[] Args(CustomAttribute a) => a.ConstructorArguments.Select(x => x.Value as string ?? "").ToArray();
        var hostPlugin = host.MainModule.Types.SelectMany(Walk).Single(t => t.FullName == "ForgeRuntime.Plugin")
            .CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin");
        if (plugins.Length == 1)
        {
            var plugin = plugins[0];
            var identity = Args(plugin.CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
            Check("plugin.identity", identity.SequenceEqual(new[] { "NAinfini.ForgeMap", "Infini Forge Map", ReleaseVersion("NAinfini-ForgeMap") })
                && map.Name.Version.ToString(3) == identity[2], string.Join(", ", identity) + "; ForgeMap " + map.Name.Version);
            var dependencies = plugin.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency").Select(Args).ToArray();
            // The dependency's version is a SemVer range, so the literal is the `>=` floor of the host's own version.
            Check("plugin.depends-on-host-only", dependencies.Length == 1
                && dependencies[0].SequenceEqual(new[] { Args(hostPlugin)[0], ">=" + Args(hostPlugin)[2] })
                && dependencies[0][0] == "NAinfini.ForgeRuntime", string.Join(" | ", dependencies.Select(d => string.Join(", ", d))));
            var load = plugin.Methods.Single(m => m.Name == "Load").Body.Instructions.ToList();
            int Index(string member) => load.FindIndex(i => i.Operand is MethodReference m && Member(m) == member);
            int off = Index("ForgeRuntime.Plugin::get_ConfiguredMode"), runtime = Index("ForgeRuntime.Plugin::get_Runtime"),
                harmony = load.FindIndex(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m && Member(m) == "HarmonyLib.Harmony::.ctor"),
                start = Index("ForgeMap.Native.MapPluginSession::Start");
            Check("plugin.off-gate-before-runtime-and-hooks", off >= 0 && off < runtime && runtime < harmony && harmony < start, $"{off} {runtime} {harmony} {start}");
            var unload = plugin.Methods.Single(m => m.Name == "Unload").Body.Instructions;
            Check("plugin.no-hot-unload", unload.Count == 2 && unload[0].OpCode == OpCodes.Ldc_I4_0 && unload[1].OpCode == OpCodes.Ret);
        }
        return checks;
    }

    /// <summary>Writes the whole audit to the file `FORGE_MAP_LAYOUT_DUMP` names, one check per line. The
    /// assertion message only carries the failures, and rebuilding the evidence file from them loses the detail
    /// of a check that passed with a list: this dump is the audit's own reading, in its own order.</summary>
    private static void Dump(List<CheckRow> checks)
    {
        string path = Environment.GetEnvironmentVariable("FORGE_MAP_LAYOUT_DUMP") ?? "";
        if (string.IsNullOrWhiteSpace(path)) return;
        File.WriteAllLines(path, checks.Select(c => (c.Passed ? "PASS " : "FAIL ") + c.Id + "\t" + c.Detail));
    }

    private sealed record CheckRow(string Id, bool Passed, string Detail);

    /// <summary>The Harmony attribute name a declared patch kind is spelled with: the rows carry the kind in
    /// lower case, the attributes capitalize it.</summary>
    private static string Kind(string kind) => char.ToUpperInvariant(kind[0]) + kind[1..];

    /// <summary>The member name a dump-style signature declares: everything after the last `::`, without the
    /// parameter list. The interop assembly's own member name is what the audit compares against.</summary>
    private static string SignatureName(string signature)
    {
        int start = signature.LastIndexOf("::", StringComparison.Ordinal) + 2;
        int end = signature.IndexOf('(', start);
        return end < 0 ? signature[start..] : signature[start..end];
    }

    /// <summary>The package version from the release identity, so the audit follows the release instead of pinning a copy.</summary>
    private static string ReleaseVersion(string packageName, [CallerFilePath] string source = "")
    {
        // The test host runs with the build output as its working directory, so the repository is found from this source file.
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
        {
            var releasePath = Path.Combine(directory.FullName, "Release", "release.json");
            if (!File.Exists(releasePath)) continue;
            using var release = JsonDocument.Parse(File.ReadAllText(releasePath));
            return release.RootElement.GetProperty("packages").EnumerateArray()
                .Single(p => p.GetProperty("packageName").GetString() == packageName).GetProperty("version").GetString()!;
        }
        throw new InvalidOperationException("Release/release.json was not found above " + source + "; the audit must run from a repository checkout.");
    }
}
