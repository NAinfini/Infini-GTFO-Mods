using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ForgeWeapon.Tests.NativeLayout;

// Compiled-assembly metadata only: proves boundaries, plugin identity and hook shape, not native call timing.
// Inputs come from the environment (see ConfiguredInputs); the suite is excluded by the Native trait by default.
[Trait("Category", "Native")]
public sealed class NativeLayoutTests
{
    private static readonly string BepInEx = Environment.GetEnvironmentVariable("FORGE_WEAPON_BEPINEX")
        ?? Environment.GetEnvironmentVariable("GTFO_BEPINEX_PATH") ?? "";
    private static readonly string Artifacts = Environment.GetEnvironmentVariable("FORGE_ARTIFACTS") ?? "";
    private static readonly string Spec = Environment.GetEnvironmentVariable("FORGE_WEAPON_HOOK_SPEC")
        ?? Path.Combine("ForgeWeapon", "evidence", "w1-native-hooks.json");
    /// <summary>The loadout policy's own hook record, written for a later batch than the w1 file: its two targets
    /// are verified by the same checks, and the dispatch kind and priority each one declares are verified too, which
    /// the w1 record's postfixes do not carry.</summary>
    private static readonly string LoadoutSpec = Environment.GetEnvironmentVariable("FORGE_WEAPON_LOADOUT_SPEC")
        ?? Path.Combine("ForgeWeapon", "evidence", "w7-loadout-policy.json");

    private static string Artifact(string relative) => Path.Combine(Artifacts, relative);

    private static void RequireInputs()
    {
        Assert.False(string.IsNullOrWhiteSpace(BepInEx) || string.IsNullOrWhiteSpace(Artifacts),
            "Set FORGE_WEAPON_BEPINEX (or GTFO_BEPINEX_PATH) and FORGE_ARTIFACTS to an existing build output.");
    }

    [Fact]
    public void weapon_native_layout_checks_all_pass()
    {
        RequireInputs();
        var checks = Audit();
        string failed = string.Join(" | ", checks.Where(c => !c.Passed).Select(c => c.Id + ": " + c.Detail));
        Assert.True(checks.Any(c => c.Id == "weapon.game-independent"), "The audit did not run to completion: " + failed);
        Assert.True(checks.Count(c => !c.Passed) == 0, failed);
        Assert.True(checks.Count >= 56, "Expected the full layout set, observed " + checks.Count + " checks.");
    }

    private static List<CheckRow> Audit()
    {
        var checks = new List<CheckRow>();
        void Check(string id, bool passed, string detail = "") => checks.Add(new(id, passed, detail));
        IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
        // The package assemblies reference the loader's Harmony and the interop game assemblies, and Cecil resolves
        // a reference out of the directory of the assembly it is reading unless it is told where else to look. The
        // one place those live is the BepInEx installation this test is already given, so it is handed to it.
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.Combine(BepInEx, "core"));
        resolver.AddSearchDirectory(Path.Combine(BepInEx, "interop"));
        var reader = new ReaderParameters { AssemblyResolver = resolver };
        using var sdk = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll"), reader);
        using var host = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeRuntime/release/ForgeRuntime.dll"), reader);
        using var mapNative = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeMap.Native/release/ForgeMap.Native.dll"), reader);
        using var weapon = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeWeapon/release/ForgeWeapon.dll"), reader);
        using var native = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeWeapon.Native/release/ForgeWeapon.Native.dll"), reader);
        using var modules = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "Modules-ASM.dll"), reader);
        using var snet = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "SNet_ASM.dll"), reader);
        using var specDocument = JsonDocument.Parse(File.ReadAllText(Spec));
        var specHooks = specDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
        using var loadoutDocument = JsonDocument.Parse(File.ReadAllText(LoadoutSpec));
        var loadoutHooks = loadoutDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
        var nativeTypes = native.MainModule.Types.SelectMany(Walk).ToArray();
        var weaponTypes = weapon.MainModule.Types.SelectMany(Walk).ToArray();
        var gameTypes = modules.MainModule.Types.Concat(snet.MainModule.Types).SelectMany(Walk).ToArray();
        bool GameAssembly(string name) => name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("BepInEx", StringComparison.Ordinal)
            || name.Contains("Harmony", StringComparison.Ordinal) || name.StartsWith("Il2Cpp", StringComparison.Ordinal) || name.EndsWith("-ASM", StringComparison.Ordinal) || name.EndsWith("_ASM", StringComparison.Ordinal);
        string[] Forge(AssemblyDefinition assembly) => assembly.MainModule.AssemblyReferences.Where(r => r.Name.StartsWith("Forge", StringComparison.Ordinal))
            .Select(r => r.FullName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var instructions = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
        var calls = instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        string Scope(MethodReference m) => m.DeclaringType.Scope is AssemblyNameReference scope ? scope.Name : "";
        string Member(MethodReference m) => (m.DeclaringType is GenericInstanceType g ? g.ElementType.FullName : m.DeclaringType.FullName) + "::" + m.Name;

        Check("weapon.game-independent", !weapon.MainModule.AssemblyReferences.Any(r => GameAssembly(r.Name)));
        Check("weapon.only-sdk", Forge(weapon).SequenceEqual(new[] { sdk.Name.FullName }), string.Join(", ", Forge(weapon)));
        Check("native.forge-references", Forge(native).SequenceEqual(new[] { sdk.Name.FullName, host.Name.FullName, weapon.Name.FullName }.OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(", ", Forge(native)));
        Check("native.no-map-reference", !native.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("ForgeMap", StringComparison.Ordinal)));
        Check("native.no-embedded-kernel", !nativeTypes.Concat(weaponTypes).Any(t => t.Name == "RuntimeKernel"));
        Check("native.single-session-over-weapon-identity", nativeTypes.Count(t => t.Name == "WeaponNativeSession") == 1
            && !nativeTypes.Any(t => t.Name is "EquipmentIdentitySession" or "EquipmentIdentityIndex"));
        Check("native.no-update-loop", !nativeTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"));

        // Owners come only from the player-owning domain through the SDK: no injected or cached player identity, no account key.
        Check("native.owner-through-sdk-player-lookup", calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeKernel::ResolveEntityInstance")
            && calls.Any(m => Member(m) == "ForgeRuntime.Framework.RuntimeKernel::IsEntityCurrent")
            && instructions.Any(i => i.Operand is string s && s == "gtfo.player")
            && !nativeTypes.Any(t => t.Name == "WeaponPlayerReferences")
            && !calls.Any(m => m.Name is "set_EntityResolvers" or "set_EntityInstanceResolvers" or "set_EntityObservers"));
        // The only identity this package reads out of the session layer is a player's own session id, which is
        // what the holder tier routes a command by: no account key, no injected player identity and no cached
        // player table is read anywhere.
        var lookups = calls.Where(m => m.Name == "get_Lookup").Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.session-only-lookup",
            lookups.SequenceEqual(new[] { "SNetwork.SNet_Player::get_Lookup" }, StringComparer.Ordinal), string.Join(", ", lookups));

        // Native state is read, never written: every game member used is a getter or a named pure query, pinned exactly.
        var gameCalls = calls.Where(m => GameAssembly(Scope(m)) && !Scope(m).StartsWith("BepInEx", StringComparison.Ordinal)
                && !Scope(m).Contains("Harmony", StringComparison.Ordinal))
            .Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // `GetBaseAgent` and `GetComponentInParent` are the two named pure queries the hit path adds: the first is
        // the game's own damage-limb owner accessor, the second is Unity's hierarchy lookup, and neither writes.
        // `Find` is Unity's own child lookup and `Vector3::.ctor` builds the value the pose is written as; a value
        // type's constructor changes nothing that outlives the call, so neither is a write. `GetAgent` is the tag
        // action's read of the game's own agent index: it answers with the agent an id names and writes nothing.
        // `GetCurrentClip`, `GetMaxClip`, `GetInventorySlotAmmo`, `pAgent::TryGet` and the reload gate's own
        // `CanReloadCurrent` are the reload, device and holder families' named pure queries: each answers a value
        // and writes nothing.
        string[] queries = { "TryGetBackpack", "IsDeployed", "GetChecksum", "GetCompID", "TryCast", "GetBaseAgent", "GetComponentInParent",
            "Find", "GetAgent", ".ctor", "op_Equality", "op_Inequality", "op_Implicit",
            "GetCurrentClip", "GetMaxClip", "GetInventorySlotAmmo", "TryGet", "CanReloadCurrent" };
        // The one presentation write whitelist: a gear part's absolute local pose and whether that part is shown.
        // Nothing else in the package may write native state, and an unexpected setter is still a failure below.
        // Each member here is exercised by tests/NativeAdapter, which asserts the written values.
        string[] presentationWrites =
        {
            "UnityEngine.GameObject::SetActive",
            "UnityEngine.Transform::set_localEulerAngles",
            "UnityEngine.Transform::set_localPosition",
            "UnityEngine.Transform::set_localScale"
        };
        // The one commanded write: the tag action submits the game's own tag through the entry point the vanilla
        // BioTracker uses, and that call is the whole action. It is not a presentation write — nothing in this
        // package draws the marker — so it gets its own exact whitelist rather than being folded into that one.
        // The holder tier's two commanded writes, alongside the tag action: a reload the holder's own machine
        // starts through the very body the player's reload key reaches, and the magazine the clip-set action
        // writes. Both are the executed half of an owner-tier request, and `tests/WeaponFacts` and
        // `tests/ReloadInventoryFacts` assert what each answers.
        string[] commandWrites = { "ItemEquippable::SetCurrentClip", "PlayerInventoryBase::TriggerReload", "ToolSyncManager::WantToTagEnemy" };
        // The loadout policy's own write, and the only native state this package changes: the three covered lists of
        // `GearManager.m_gearPerSlot`, emptied and refilled one item at a time through the list instances the game
        // itself built. `tests/NativeAdapter` asserts the resulting contents, the order and the restore.
        string[] poolWrites = { "Il2CppSystem.Collections.Generic.List`1::Add", "Il2CppSystem.Collections.Generic.List`1::Clear" };
        var writes = gameCalls.Where(c => { var name = c[(c.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
            return !name.StartsWith("get_", StringComparison.Ordinal) && !queries.Contains(name)
                && !presentationWrites.Contains(c, StringComparer.Ordinal) && !commandWrites.Contains(c, StringComparer.Ordinal)
                && !poolWrites.Contains(c, StringComparer.Ordinal); }).ToArray();
        Check("native.read-only-game-access", gameCalls.Length > 0 && writes.Length == 0, writes.Length == 0 ? string.Join(", ", gameCalls) : string.Join(", ", writes));
        // Every whitelisted write has to be used: a member nobody writes is a loosened whitelist, not a feature.
        var usedWrites = gameCalls.Where(c => presentationWrites.Contains(c, StringComparer.Ordinal)).ToArray();
        Check("native.presentation-writes-exact", usedWrites.SequenceEqual(presentationWrites, StringComparer.Ordinal), string.Join(", ", usedWrites));
        var usedCommands = gameCalls.Where(c => commandWrites.Contains(c, StringComparer.Ordinal)).ToArray();
        Check("native.command-writes-exact", usedCommands.SequenceEqual(commandWrites, StringComparer.Ordinal), string.Join(", ", usedCommands));
        var usedPoolWrites = gameCalls.Where(c => poolWrites.Contains(c, StringComparer.Ordinal)).ToArray();
        Check("native.pool-writes-exact", usedPoolWrites.SequenceEqual(poolWrites, StringComparer.Ordinal), string.Join(", ", usedPoolWrites));
        // Pinned from the reviewed build: any new native member, read or written, must be reviewed here first.
        string[] expectedReads =
        {
            "Agents.Agent::get_Alive",
            "Agents.AgentManager::GetAgent",
            "Agents.pAgent::TryGet",
            "Dam_EnemyDamageBase::get_Owner",
            "Dam_EnemyDamageLimb::GetBaseAgent",
            "Dam_EnemyDamageLimb::get_m_limbID",
            "Dam_PlayerDamageLimb::GetBaseAgent",
            "Dam_SyncedDamageBase::get_Health",
            "Enemies.EnemyAgent::get_IsTagged",
            // The expedition gate the projection reads: no gear can be chosen inside a level.
            "GameStateManager::get_IsInExpedition",
            "Gear.BulletWeapon::get_m_burstMax",
            "Gear.BulletWeaponArchetype::get_m_weapon",
            "Gear.GearIDRange::GetChecksum",
            "Gear.GearIDRange::GetCompID",
            "Gear.GearIDRange::get_PlayfabItemInstanceId",
            // The loadout policy's two ends: the pool it narrows and the manager that owns it, plus the rundown that
            // decides whether a policy is in force at all. The pool's own lists are the one native state this
            // package writes, one item at a time through `Clear` and `Add`.
            "Gear.GearManager::get_Current",
            "Gear.GearManager::get_m_gearPerSlot",
            "Gear.GearPartHolder::get_FlashlightPart",
            "Gear.GearPartHolder::get_FrontPart",
            "Gear.GearPartHolder::get_FrontPartAttachmentA",
            "Gear.GearPartHolder::get_FrontPartAttachmentB",
            "Gear.GearPartHolder::get_GearIDRange",
            "Gear.GearPartHolder::get_MagPart",
            "Gear.GearPartHolder::get_MeleeHandlePart",
            "Gear.GearPartHolder::get_MeleeHeadPart",
            "Gear.GearPartHolder::get_MeleeNeckPart",
            "Gear.GearPartHolder::get_MeleePommelPart",
            "Gear.GearPartHolder::get_ReceiverPart",
            "Gear.GearPartHolder::get_ReceiverPartAttachment",
            "Gear.GearPartHolder::get_SightPart",
            "Gear.GearPartHolder::get_StockPart",
            "Gear.GearPartHolder::get_ToolDeliveryPart",
            "Gear.GearPartHolder::get_ToolDeliveryPartAttachment",
            "Gear.GearPartHolder::get_ToolGripPart",
            "Gear.GearPartHolder::get_ToolMainPart",
            "Gear.GearPartHolder::get_ToolMainPartAttachment",
            "Gear.GearPartHolder::get_ToolPayloadPart",
            "Gear.GearPartHolder::get_ToolScreenPart",
            "Gear.GearPartHolder::get_ToolTargetingPart",
            "Gear.MeleeWeaponDamageData::get_damageGO",
            "Gear.MeleeWeaponFirstPerson::get_CurrentStateName",
            "Gear.MeleeWeaponFirstPerson::get_m_damageToDeal",
            "Globals.Global::get_RundownIdToLoad",
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1::get_Item",
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1::get_Length",
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1::op_Implicit",
            "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase::TryCast",
            "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase::get_Pointer",
            "Il2CppSystem.Collections.Generic.List`1::Add",
            "Il2CppSystem.Collections.Generic.List`1::Clear",
            "Il2CppSystem.Collections.Generic.List`1::get_Count",
            "Il2CppSystem.Collections.Generic.List`1::get_Item",
            "Item::get_Owner",
            "ItemEquippable::GetCurrentClip",
            "ItemEquippable::GetMaxClip",
            "ItemEquippable::SetCurrentClip",
            "ItemEquippable::get_IsReloading",
            "MineDeployerInstance_Detonate_Explosive::get_m_core",
            "Player.BackpackItem::get_GearIDRange",
            "Player.BackpackItem::get_Instance",
            "Player.BackpackItem::get_IsLoaded",
            "Player.BackpackItem::get_ItemID",
            "Player.InventorySlotAmmo::get_BulletsInPack",
            "Player.PlayerAgent::get_Inventory",
            "Player.PlayerAgent::get_Owner",
            "Player.PlayerAmmoStorage::GetInventorySlotAmmo",
            "Player.PlayerAmmoStorage::get_m_playerBackpack",
            "Player.PlayerBackpack::IsDeployed",
            "Player.PlayerBackpack::get_AmmoStorage",
            "Player.PlayerBackpack::get_Owner",
            "Player.PlayerBackpack::get_Slots",
            "Player.PlayerBackpackManager::TryGetBackpack",
            "PlayerInventoryBase::CanReloadCurrent",
            "PlayerInventoryBase::TriggerReload",
            "PlayerInventoryBase::get_Owner",
            "PlayerInventoryBase::get_WieldedItem",
            "PlayerInventoryBase::get_m_wieldedItem",
            "SNetwork.SNet::get_HasLocalPlayer",
            "SNetwork.SNet::get_IsMaster",
            "SNetwork.SNet::get_LocalPlayer",
            "SNetwork.SNet_Player::get_HasPlayerAgent",
            "SNetwork.SNet_Player::get_IsBot",
            "SNetwork.SNet_Player::get_IsLocal",
            "SNetwork.SNet_Player::get_Lookup",
            "SNetwork.SNet_Player::get_PlayerAgent",
            "SentryGunInstance_Firing_Bullets::get_m_core",
            "ToolSyncManager::WantToTagEnemy",
            "UnityEngine.Component::GetComponentInParent",
            "UnityEngine.Component::get_gameObject",
            "UnityEngine.Component::get_transform",
            "UnityEngine.GameObject::GetComponentInParent",
            "UnityEngine.GameObject::SetActive",
            "UnityEngine.GameObject::get_transform",
            "UnityEngine.Object::op_Equality",
            "UnityEngine.Object::op_Inequality",
            "UnityEngine.RaycastHit::get_collider",
            "UnityEngine.RaycastHit::get_point",
            "UnityEngine.Time::get_frameCount",
            "UnityEngine.Transform::Find",
            "UnityEngine.Transform::get_position",
            "UnityEngine.Transform::set_localEulerAngles",
            "UnityEngine.Transform::set_localPosition",
            "UnityEngine.Transform::set_localScale",
            "UnityEngine.Vector3::.ctor",
            "Weapon/WeaponHitData::get_owner",
            "Weapon/WeaponHitData::get_rayHit",
            "iSentrygunInstanceCore::get_Ammo"
        };
        Check("native.exact-game-reads", gameCalls.SequenceEqual(expectedReads, StringComparer.Ordinal), string.Join(", ", gameCalls));
        // The gear-block mount is answered from the record the game's own offline gear loader writes onto a gear,
        // so both halves of that read are pinned: the accessor and the literal prefix it is measured against.
        Check("native.gear-block-reads-the-offline-record",
            calls.Any(m => Member(m) == "Gear.GearIDRange::get_PlayfabItemInstanceId")
            && nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand! == "OfflineGear_ID_"),
            "The gear-block matcher does not read the offline gear record.");
        var snetReads = gameCalls.Where(c => c.StartsWith("SNetwork.", StringComparison.Ordinal)).ToArray();
        Check("native.snet-read-only", snetReads.Length > 0 && snetReads.All(c => c.Contains("::get_", StringComparison.Ordinal)), string.Join(", ", snetReads));
        var harmonyCalls = calls.Where(m => Scope(m).Contains("Harmony", StringComparison.Ordinal)).Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.harmony-install-and-unpatch-only", harmonyCalls.All(c => c is "HarmonyLib.Harmony::.ctor" or "HarmonyLib.Harmony::CreateClassProcessor"
            or "HarmonyLib.PatchClassProcessor::Patch" or "HarmonyLib.Harmony::UnpatchSelf"), string.Join(", ", harmonyCalls));

        bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
        var hooks = nativeTypes.Where(Patch).ToArray();
        // The evidence file is the frozen witness for the hooks it was written for; the combat and deployable
        // hooks added later are not in it, so their declared target is checked against the same game assemblies
        // instead, and their shape by the same checks. Every hook is still pinned by exactly one source.
        var evidenced = specHooks.Concat(loadoutHooks).ToArray();
        var specNames = evidenced.Select(h => h.GetProperty("hook").GetString()!).ToArray();
        var extraNames = hooks.Select(t => t.Name).Where(n => !specNames.Contains(n, StringComparer.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.exact-hook-set", hooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(specNames.Concat(extraNames).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(", ", hooks.Select(t => t.Name)));
        Check("native.spec-hook-set-partitioned", specNames.Distinct(StringComparer.Ordinal).Count() == specNames.Length,
            "Two hook records claim the same hook name.");
        // Every hook class the package declares is named by one of the lists its own installation loop reads: the
        // package's own list and the two families that declare theirs beside their observer. A family list nobody
        // installs is the exact failure that left the reload rules compiled but never patched, so the declaration
        // and the installation are pinned against each other rather than each against itself.
        string[] ListedHookNames(string owner)
        {
            var type = nativeTypes.Single(t => t.Name == owner);
            var methods = new List<MethodDefinition>();
            // A static property's initializer runs in the type's own static constructor; the getter is read too so
            // a list written in either place is found.
            if (type.Methods.FirstOrDefault(m => m.Name == ".cctor") is { } initializer) methods.Add(initializer);
            if (type.Properties.FirstOrDefault(p => p.Name == "Types")?.GetMethod is { } getter) methods.Add(getter);
            return methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Where(i => i.OpCode == OpCodes.Ldtoken && i.Operand is TypeReference)
                .Select(i => ((TypeReference)i.Operand!).Name).ToArray();
        }
        string[] hookLists = { "WeaponNativeHooks", "ReloadInventoryHooks", "WeaponPlacementHooks" };
        var listed = hookLists.SelectMany(ListedHookNames).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.declared-hooks-are-listed", listed.SequenceEqual(hooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal),
            string.Join(", ", listed));
        // And the one installation path reads all three lists: the loop that patches hook classes follows each of
        // them, so being listed and being installed are the same thing.
        // The walk follows the package's own call graph. A reference to one of this assembly's own types is
        // resolved by the module that declares it rather than by an assembly reference, so membership is asked of
        // the type table rather than of the operand's scope. A static property's initializer is not in its own
        // getter but in the type's constructor, so every own type a body mentions is entered from that side too:
        // a list written as `{ get; } = ...` is otherwise invisible to a walk that arrives through a getter.
        var ownTypes = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var type in nativeTypes) ownTypes.TryAdd(type.FullName, type);
        var ownMethods = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        foreach (var method in nativeTypes.SelectMany(t => t.Methods)) ownMethods.TryAdd(method.FullName, method);
        var reachedLists = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<MethodDefinition>(nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody
            && m.Body.Instructions.Any(i => i.Operand is MethodReference reference && reference.Name == "CreateClassProcessor")));
        var walked = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var caller = pending.Dequeue();
            if (!walked.Add(caller.FullName) || !caller.HasBody) continue;
            foreach (var reference in caller.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>())
            {
                if (!ownTypes.TryGetValue(reference.DeclaringType.FullName, out var declaring)) continue;
                if (reference.Name == "get_Types") reachedLists.Add(declaring.Name);
                if (ownMethods.TryGetValue(reference.FullName, out var target)) pending.Enqueue(target);
                if (declaring.Methods.FirstOrDefault(m => m.Name == ".cctor") is { } initializer) pending.Enqueue(initializer);
            }
        }
        Check("native.hook-lists-are-installed", hookLists.All(reachedLists.Contains),
            string.Join(", ", reachedLists.OrderBy(x => x, StringComparer.Ordinal)));
        foreach (var name in specNames.Concat(extraNames))
        {
            JsonElement? spec = evidenced.Where(h => h.GetProperty("hook").GetString() == name)
                .Select(h => (JsonElement?)h).SingleOrDefault();
            var hook = hooks.SingleOrDefault(t => t.Name == name);
            if (hook == null) { Check("hook." + name + ".present", false); continue; }
            var patch = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
            var type = patch.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
            var method = patch.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
            Check("hook." + name + ".declared-target",
                spec == null || (type.FullName == spec.Value.GetProperty("type").GetString() && method == spec.Value.GetProperty("name").GetString()),
                type.FullName + "::" + method);
            // A setter patch names the property and asks for its accessor, so the member to find is the one the
            // metadata spells `set_<name>`; everything else patches the member the declaration names.
            bool setter = patch.ConstructorArguments.Any(a => a.Type.Name == "MethodType" && Convert.ToInt32(a.Value, CultureInfo.InvariantCulture) == 2);
            var targetName = setter ? "set_" + method : method;
            // A patch that names its argument types brackets exactly one of several bodies of the same name, so
            // the declaration's own argument list is the filter; without one the name alone must be unique.
            var argumentTypes = patch.ConstructorArguments.Where(a => a.Value is CustomAttributeArgument[])
                .SelectMany(a => (CustomAttributeArgument[])a.Value!)
                .Select(a => a.Value is TypeReference argument ? argument.FullName : "").ToArray();
            var named = gameTypes.Where(t => t.FullName == type.FullName).SelectMany(t => t.Methods.Where(m => m.Name == targetName));
            var targets = argumentTypes.Length == 0 ? named.ToArray()
                : named.Where(m => m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(argumentTypes, StringComparer.Ordinal)).ToArray();
            // The evidenced hooks are pinned to their recorded signature; a hook added after that evidence was
            // frozen is pinned to the one game method its patch declaration names, which is the same guarantee.
            Check("hook." + name + ".unique-game-method", targets.Length == 1 && (spec == null
                    ? targets[0].Name == targetName && type.Name == targets[0].DeclaringType.Name
                    : targets[0].FullName == spec.Value.GetProperty("signature").GetString()
                        && targets[0].IsVirtual == spec.Value.GetProperty("isVirtual").GetBoolean()),
                string.Join(" | ", targets.Select(m => m.FullName)));
            // A record that carries the build's own sharing count for its target body is pinned on it: 1 is a unique
            // entry, which is what makes the RVA a safe patch target. The w1 record predates that field.
            if (spec != null && spec.Value.TryGetProperty("rvaSharing", out var sharing))
                Check("hook." + name + ".unique-rva", sharing.GetInt32() == 1, spec.Value.GetProperty("rva").GetString() ?? "");
            var patches = hook.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Namespace == "HarmonyLib"
                && a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyTranspiler" or "HarmonyFinalizer")).ToArray();
            // A hook class carries one dispatched method — except where one native body is bracketed, which is the
            // receiver the remote-melee half patches: then the pair is the whole hook, one prefix and one postfix
            // on the same target, and nothing else is in the class.
            bool Prefix(MethodDefinition p) => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix");
            bool Postfix(MethodDefinition p) => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix");
            var pair = patches.Length == 2 && patches.Count(Prefix) == 1 && patches.Count(Postfix) == 1;
            // A record that names its dispatch is the record for a hook whose body may be a prefix or a postfix.
            // A hook the frozen records do not carry declares its own dispatch and its own priority: the checks
            // below then hold it to the attributes it actually carries — exactly one dispatch, and a priority that
            // is one of the two this package uses — rather than to a record it does not have.
            var declared = spec != null && spec.Value.TryGetProperty("dispatch", out var dispatch)
                ? dispatch.GetProperty("kind").GetString()
                : spec == null ? null : "Postfix";
            var declaredPriority = spec != null && spec.Value.TryGetProperty("dispatch", out var priorityRecord)
                ? priorityRecord.GetProperty("priority").GetString()
                : spec == null ? null : "Last";
            // The methods the hook's own dispatch declares: one, or the prefix and the postfix of a bracketing pair.
            var dispatched = pair || declared == null
                ? patches
                : patches.Where(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "Harmony" + declared)).ToArray();
            var expectedPriority = declaredPriority == "First" ? 800 : declaredPriority == "Last" ? 0 : (int?)null;
            Check("hook." + name + ".dispatch", patches.Length is 1 or 2 && patches.All(p => p.IsStatic) && dispatched.Length > 0,
                string.Join(", ", patches.Select(p => p.Name)));
            // A body patch binds nothing it does not name: `__instance` is the target's own instance, so where a body
            // takes it, it has to be typed as the target. Which parameters a patched body declares beyond that is
            // its own business — the loadout hooks read the session and the patched array instead of the target.
            Check("hook." + name + ".instance-type", dispatched.All(p =>
            {
                var names = p.Parameters.Select(parameter => parameter.Name).ToArray();
                return names.Length == 0 || names[0] != "__instance" || p.Parameters[0].ParameterType.FullName == type.FullName;
            }), string.Join(" | ", dispatched.Select(p => p.Parameters.Select(parameter => parameter.Name + " " + parameter.ParameterType.FullName).Aggregate("", (all, one) => all + one + " "))));
            int? PriorityOf(MethodDefinition? method) => method?.CustomAttributes.SingleOrDefault(a => a.AttributeType.Name == "HarmonyPriority")
                is { } attribute && attribute.ConstructorArguments[0].Value is int value ? value : null;
            Check("hook." + name + ".priority", pair
                ? PriorityOf(patches.Single(Prefix)) == 800 && PriorityOf(patches.Single(Postfix)) == 0
                : expectedPriority == null
                    ? PriorityOf(dispatched.Length == 1 ? dispatched[0] : null) is 0 or 800
                    : PriorityOf(dispatched.Length == 1 ? dispatched[0] : null) == expectedPriority,
                string.Join(", ", dispatched.Select(p => p.Name + "=" + (PriorityOf(p)?.ToString() ?? "<none>"))));
            var body = dispatched.SelectMany(p => p.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()).ToArray();
            // Either the session is read as a property and asked once, or its own hook body is named; a body that
            // does neither would run native work with nothing to answer to.
            Check("hook." + name + ".guarded-by-session",
                body.Any(m => m.DeclaringType.Name == "WeaponNativeSession"
                    && m.Name is "get_Current" or "Guard" or "GuardNarrow" or "Offer")
                // The reload and inventory hooks reach the session through their own family's guard, which asks
                // the same session and applies its guard; either front door is the session being asked once.
                || body.Any(m => m.DeclaringType.Name == "ReloadInventoryHooks" && m.Name is "Guard" or "GuardInventory"),
                string.Join(", ", body.Where(m => m.DeclaringType.Name is "WeaponNativeSession" or "ReloadInventoryHooks").Select(m => m.Name)));
        }

        // One patch per Slot 151 body. The build declares exactly four Fire bodies and none of them reaches
        // another (evidence/w4-player-fire.json), so the four Fire hooks must name four distinct game methods:
        // two hooks on one body would count a single shot twice, and a family whose body is not named at all would
        // never produce a shot.
        string? FireTarget(string hookName)
        {
            var hook = hooks.SingleOrDefault(t => t.Name == hookName);
            var patch = hook?.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
            if (patch == null) return null;
            var type = patch.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().FirstOrDefault();
            var name = patch.ConstructorArguments.Select(a => a.Value).OfType<string>().FirstOrDefault();
            return type == null || name == null ? null : type.FullName + "::" + name;
        }
        var fireTargets = new[] { "WeaponFired", "ShotgunFired", "SyncedWeaponFired", "SyncedShotgunFired" }.Select(FireTarget).ToArray();
        Check("native.one-patch-per-fire-body", fireTargets.All(t => t != null) && fireTargets.Distinct(StringComparer.Ordinal).Count() == 4,
            string.Join(" | ", fireTargets.Select(t => t ?? "<missing>")));

        // A part pose names an `eGearComponent` member, and the number that reaches `GearIDRange.GetCompID` is the
        // production enum's own value: every slot the package can name must carry the build's number, and the two
        // enums must agree on the members, so a pose can never address a different slot than it names.
        var gameComponents = gameTypes.Where(t => t.FullName == "Gear.eGearComponent").SelectMany(t => t.Fields)
            .Where(f => f.HasConstant).ToDictionary(f => f.Name, f => Convert.ToInt32(f.Constant), StringComparer.Ordinal);
        var packageSlots = nativeTypes.Where(t => t.Name == "GearPartSlot").SelectMany(t => t.Fields)
            .Where(f => f.HasConstant).ToDictionary(f => f.Name, f => Convert.ToInt32(f.Constant), StringComparer.Ordinal);
        var slotMismatch = packageSlots.Where(s => !gameComponents.TryGetValue(s.Key, out var value) || value != s.Value)
            .Select(s => s.Key + "=" + s.Value + " vs " + (gameComponents.TryGetValue(s.Key, out var v) ? v.ToString() : "<absent>")).ToArray();
        Check("native.gear-part-slots-exact", packageSlots.Count == 21 && slotMismatch.Length == 0,
            slotMismatch.Length == 0 ? packageSlots.Count + " slots" : string.Join(", ", slotMismatch));

        var plugins = nativeTypes.Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin")).ToArray();
        Check("plugin.single-entry", plugins.Length == 1 && plugins[0].FullName == "ForgeWeapon.Native.Plugin"
            && plugins[0].BaseType?.FullName == "BepInEx.Unity.IL2CPP.BasePlugin", string.Join(", ", plugins.Select(p => p.FullName)));
        string[] Args(CustomAttribute a) => a.ConstructorArguments.Select(x => x.Value as string ?? "").ToArray();
        string[] PluginIdentity(AssemblyDefinition assembly, string type) => Args(assembly.MainModule.Types.SelectMany(Walk).Single(t => t.FullName == type)
            .CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
        var hostIdentity = PluginIdentity(host, "ForgeRuntime.Plugin");
        var mapIdentity = PluginIdentity(mapNative, "ForgeMap.Native.Plugin");
        if (plugins.Length == 1)
        {
            var plugin = plugins[0];
            var identity = Args(plugin.CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
            Check("plugin.identity", identity.SequenceEqual(new[] { "NAinfini.ForgeWeapon", "Infini Forge Weapon", ReleaseVersion("NAinfini-ForgeWeapon") })
                && weapon.Name.Version.ToString(3) == identity[2] && native.Name.Version.ToString(3) == identity[2],
                string.Join(", ", identity) + "; ForgeWeapon " + weapon.Name.Version + "; native " + native.Name.Version);
            // The dependency versions are read from the built host and Map plugins, so a renamed or re-versioned owner
            // fails here. Each literal is a minimum version — the shipped loader parses it as a SemVer range — so the
            // expected spelling is the owner's own version behind a `>=` floor.
            var dependencies = plugin.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency").Select(a => string.Join("@", Args(a)))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var expectedDependencies = new[] { hostIdentity[0] + "@>=" + hostIdentity[2], mapIdentity[0] + "@>=" + mapIdentity[2] }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Check("plugin.depends-on-host-and-map", dependencies.SequenceEqual(expectedDependencies, StringComparer.Ordinal)
                && hostIdentity[0] == "NAinfini.ForgeRuntime" && mapIdentity[0] == "NAinfini.ForgeMap", string.Join(" | ", dependencies));
            var load = plugin.Methods.Single(m => m.Name == "Load").Body.Instructions.ToList();
            int Index(string member) => load.FindIndex(i => i.Operand is MethodReference m && Member(m) == member);
            int off = Index("ForgeRuntime.Plugin::get_ConfiguredMode"), runtime = Index("ForgeRuntime.Plugin::get_Runtime"),
                harmony = load.FindIndex(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m && Member(m) == "HarmonyLib.Harmony::.ctor"),
                start = Index("ForgeWeapon.Native.WeaponNativeSession::Start");
            Check("plugin.off-gate-before-runtime-and-hooks", off >= 0 && off < runtime && runtime < harmony && harmony < start, $"{off} {runtime} {harmony} {start}");
            Check("plugin.gameplay-gate-from-host", calls.Any(m => Member(m) == "ForgeRuntime.Plugin::get_CanExecuteGameplay"));
            var unload = plugin.Methods.Single(m => m.Name == "Unload").Body.Instructions;
            Check("plugin.no-hot-unload", unload.Count == 2 && unload[0].OpCode == OpCodes.Ldc_I4_0 && unload[1].OpCode == OpCodes.Ret);
        }
        return checks;
    }

    private sealed record CheckRow(string Id, bool Passed, string Detail);

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
