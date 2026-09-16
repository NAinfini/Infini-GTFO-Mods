using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeRuntime;
using ForgeRuntime.Framework;
using ForgeRuntime.GameBindings;
using ForgeEnemy;
using ForgeEnemy.Native;
using Plugin = ForgeRuntime.Plugin;
using SNetwork;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action, string message)
{
    try { action(); } catch (Exception error) when (error is RuntimeContractException or InvalidDataException or ArgumentException) { checks++; return; }
    throw new Exception(message);
}
// Assertions that cannot run against the current contracts. They are listed, never counted in `checks`.
var blocked = new List<(string Id, string Reason)>();
// Every plan here mounts the whole level, and no mount kind belongs to the kernel any more: a provider has to own
// it. This harness has no domain package, so the double states the rule ForgeMap's level matcher implements — the
// reference is `<rundown block id>:<tier A-E>:<tier index>`, decimal without leading zeros — and answers the one
// level it stands in for. A reference outside that grammar is refused rather than matched loosely, which is what
// the website's runtime fixtures hit until they write the same spelling.
const uint FixtureLevelRundown = 31; const char FixtureLevelTier = 'A'; const int FixtureLevelIndex = 0;
const string FixtureLevelReference = "31:A:0";
RuntimeKernel Kernel(string worldLevel = FixtureLevelReference)
{
    var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"), new RuntimeLimits());
    kernel.BeginWorld(1); kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off); kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
    kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
    kernel.RegisterModule(ForgeTrigger.ModuleDefinition.Create(), RuntimeLogLevel.Off);
    kernel.RegisterModule(LevelMountOwner(worldLevel), RuntimeLogLevel.Off);
    return kernel;
}
bool Decimal(string text, out long value)
{
    value = 0;
    if (text.Length == 0 || (text.Length > 1 && text[0] == '0')) return false;
    foreach (var digit in text) if (digit is < '0' or > '9') return false;
    return long.TryParse(text, out value);
}
bool IsFixtureLevel(string reference)
{
    var parts = reference.Split(':');
    return parts.Length == 3 && Decimal(parts[0], out var block) && Decimal(parts[2], out var index)
        && parts[1].Length == 1 && parts[1][0] == FixtureLevelTier
        && block == FixtureLevelRundown && index == FixtureLevelIndex;
}
RuntimeModule LevelMountOwner(string worldLevel = FixtureLevelReference) => new(RuntimeKernel.ApiVersion,
    RuntimeJson.From(new
    {
        providers = new[] { new { id = "test.level", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
{
    // The world stands at one level: the double answers a plan whose mount is written in the fixture's own reference
    // grammar and names that level, so a case can stand the world up elsewhere to make the mount comparison fail.
    AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
    {
        ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
            category == null && IsFixtureLevel(reference) && reference == worldLevel)
    }
};
// The enemy provider is registered from its own declaration, never from the game-bound module: this harness compiles
// no game-bound source, so what it registers is the package's declared surface and every body is a stand-in. The
// handle is what publishes a fact under the provider that owns its binding — the kernel refuses a fact published by
// any other provider — which is how a native observation is stood in for here.
RuntimeModuleHandle EnemyHandle(RuntimeKernel kernel, Action<CommandContext>? onCommit = null)
    => kernel.RegisterModule(EnemyDeclaration.Module(kernel, _ => context =>
    {
        onCommit?.Invoke(context);
        return CommandResult.Succeeded(RuntimeJson.EmptyObject);
    }), RuntimeLogLevel.Off);
// A parameterless QA action that hands each dispatched command to the test.
const string RecordBinding = "test.bridge.binding.record";
RuntimeModule Recorder(Action<CommandContext> record) => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
{
    providers = new[] { new { id = "test.bridge", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
    capabilities = new[] { new { id = "test.bridge.action.record", owner = "test.bridge", kind = "action", label = "QA record", version = "1.0.0", parameters = new { },
        graph = new { domains = new[] { "enemy" }, execution = "host",
            inputs = new[] { new { id = "in", type = "execution" }, new { id = "target", type = "entity" } },
            // A result port carries its row shape: the four shared columns in their fixed order first, then the
            // values the schema adds. The recorder commits nothing, so it declares the shared columns only.
            outputs = new object[] { new { id = "next", type = "execution" }, new { id = "result", type = "result", schema = "test.bridge.result.record",
                fields = new object[] { new { id = "target", type = "entity" }, new { id = "status", type = "enum", schema = "execution_outcome" },
                    new { id = "committed", type = "enum", schema = "commit_state" }, new { id = "code", type = "string" } } } },
            parameters = Array.Empty<object>(), recipients = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" } } } },
    bindings = new[] { new { id = RecordBinding, capabilityId = "test.bridge.action.record", providerId = "test.bridge", handler = "test.bridge.record",
        role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() } }
}).GetRawText(), new Dictionary<string, CommandHandler> { ["test.bridge.record"] = context => { record(context); return CommandResult.Succeeded(RuntimeJson.EmptyObject); } },
    new[] { new BindingSupport(RecordBinding, "implementation-only", new[] { "test.record" }) })
{
    // The recorder reads no port: it hands the whole dispatch context to the test.
    Shapes = new Dictionary<string, HandlerShape> { ["test.bridge.record"] = new HandlerShape() }
};
// trigger -> one action step, with pins, permissions, slot frames and positional constants taken from the kernel's own
// registry and graph contracts. Inputs are event-slot wires and {slot, value} literals; enum constants are member indexes.
string LocalPlan(RuntimeKernel k, string planId, string trigger, string action, (string EventOutput, string ActionInput)[] wires,
    (string ActionInput, object Value)[] literals, JsonElement parameters)
{
    var manifest = RuntimeJson.Parse(k.ExportManifest()); var registry = manifest.GetProperty("registry");
    JsonElement Row(string list, string id) => registry.GetProperty(list).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
    var ids = new[] { trigger, action }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    var pins = ids.Select(id =>
    {
        var binding = Row("bindings", id);
        string capabilityId = binding.GetProperty("capabilityId").GetString()!, providerId = binding.GetProperty("providerId").GetString()!;
        return new { bindingId = id, capabilityId, capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
            providerId, providerVersion = Row("providers", providerId).GetProperty("version").GetString()!, handler = binding.GetProperty("handler").GetString()! };
    }).ToArray();
    var permissions = manifest.GetProperty("bindingSupport").EnumerateArray().Where(s => ids.Contains(s.GetProperty("bindingId").GetString()!))
        .SelectMany(s => s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
        .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    JsonElement Parameters(string id) => id == action ? parameters : RuntimeJson.EmptyObject;
    var contracts = pins.ToDictionary(p => p.bindingId, p => k.ResolveGraphContract(p.capabilityId, p.capabilityVersion, Parameters(p.bindingId)));
    object?[] Constants(string id) => Row("capabilities", pins.Single(p => p.bindingId == id).capabilityId).GetProperty("graph").GetProperty("parameters").EnumerateArray()
        .Select(definition => Parameters(id).TryGetProperty(definition.GetProperty("id").GetString()!, out var value) ? (object?)value : null).ToArray();
    object Layout(string id) => new { inputs = RuntimeGraphContracts.Layout(contracts[id], "inputs"), outputs = RuntimeGraphContracts.Layout(contracts[id], "outputs"),
        constants = Constants(id), promoted = Array.Empty<int>() };
    int Slot(string id, string side, string port) => contracts[id].GetProperty(side).EnumerateArray()
        .Select((p, index) => (p, index)).Single(x => x.p.GetProperty("id").GetString() == port).index;
    var inputs = wires.Select(w => (Slot: Slot(action, "inputs", w.ActionInput), Row: (object)new { slot = Slot(action, "inputs", w.ActionInput), fromEventSlot = Slot(trigger, "outputs", w.EventOutput) }))
        .Concat(literals.Select(l => (Slot: Slot(action, "inputs", l.ActionInput), Row: (object)new { slot = Slot(action, "inputs", l.ActionInput), value = l.Value })))
        .OrderBy(x => x.Slot).Select(x => x.Row).ToArray();
    return RuntimeJson.From(new
    {
        schemaVersion = 4, kind = "forge-runtime-plan", planId, resource = new { id = planId, revision = "1" }, runtime = k.Identity,
        domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions, dependencies = Array.Empty<string>(),
        limits = new { k.Limits.MaxEventsPerTick, k.Limits.MaxCommandsPerTick, k.Limits.MaxQueuedEvents, k.Limits.MaxCausalDepth }, bindings = pins,
        // These plans mount the one level the mount double above stands in for, in the reference spelling the
        // level matcher parses; a plan mounted on nothing could never be dispatched.
        attachments = new[] { new { kind = "level", reference = FixtureLevelReference } },
        // One action step: the graph is a single node, so it is trivially its own canonical order and the entrypoint
        // ends at its single (unwired) execution output.
        entrypoints = new[] { new { nodeId = "Fact", binding = Array.IndexOf(ids, trigger), layout = Layout(trigger), start = 0,
            steps = new[] { new { nodeId = "Step", nodeKind = "action", binding = Array.IndexOf(ids, action), layout = Layout(action), inputs, successors = new int?[] { null } } } } }
    }).GetRawText();
}
string DeathRecordPlan(RuntimeKernel k) => LocalPlan(k, "test.bridge.death", EnemyRegistration.DeathStartedBinding, RecordBinding,
    new[] { ("enemy", "target") }, Array.Empty<(string, object)>(), RuntimeJson.EmptyObject);
// damage_applied -> record: the fact the native damage window publishes, wired into a recorder step, so the bridge
// dispatches a configured plan on the same observation the shipped heal plan is triggered by.
string DamageRecordPlan(RuntimeKernel k) => LocalPlan(k, "test.bridge.damage", EnemyRegistration.DamageBinding, RecordBinding,
    new[] { ("target", "target") }, Array.Empty<(string, object)>(), RuntimeJson.EmptyObject);

// The failure latch is tested across 100 further ticks, not merely a mode boolean.
var startup = Kernel(); int attempts = 0;
try { startup.StartRuntime(() => { attempts++; throw new IOException("bad configured plan"); }); } catch (IOException) { }
for (int i = 0; i < 100; i++) startup.StartRuntime(() => attempts++);
Check(attempts == 1 && startup.StartupState == RuntimeStartupState.Failed, "failed startup repeats file IO");
var successfulStartup = Kernel();
successfulStartup.StartRuntime(() => attempts++); successfulStartup.StartRuntime(() => attempts++);
Check(attempts == 2 && successfulStartup.StartupState == RuntimeStartupState.Ready, "successful startup must execute once");

var messages = new List<string>();
// The enemy declaration is a registration the kernel accepts: every declared row resolves a registered capability,
// every execute row carries a handler and a shape, every on-demand row an evaluator, and every binding a support row.
// The rows are the package's declared surface, so the harness asserts the surface the website compiles against the
// same way the release export does — and that no body of it runs here.
var kernel = Kernel();
var declaration = EnemyDeclaration.Module(kernel);
var declarationOwner = kernel.RegisterModule(declaration, RuntimeLogLevel.Off);
var declaredManifest = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry");
Check(declaredManifest.GetProperty("providers").EnumerateArray()
        .Any(row => row.GetProperty("id").GetString() == EnemyRegistration.ProviderId),
    "the enemy declaration registered no provider");
var enemyBindings = declaredManifest.GetProperty("bindings").EnumerateArray()
    .Where(row => row.GetProperty("providerId").GetString() == EnemyRegistration.ProviderId).ToArray();
Check(enemyBindings.Length > 0
        && enemyBindings.All(row => row.GetProperty("status").GetString() == "implemented"),
    "the enemy declaration carries no binding, or one that is not implemented: " + enemyBindings.Length);
var supported = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("bindingSupport").EnumerateArray()
    .Select(row => row.GetProperty("bindingId").GetString()!).ToHashSet(StringComparer.Ordinal);
Check(enemyBindings.All(row => supported.Contains(row.GetProperty("id").GetString()!)),
    "an enemy binding has no support row");
Check(enemyBindings.Any(row => row.GetProperty("id").GetString() == EnemyRegistration.DamageBinding)
        && enemyBindings.Any(row => row.GetProperty("id").GetString() == EnemyRegistration.HealBinding)
        && enemyBindings.Any(row => row.GetProperty("id").GetString() == EnemyRegistration.DeathStartedBinding),
    "the declaration lost a row the website fixtures pin");
bool refused = false;
try { declaration.Handlers.Values.First()(null!); } catch (InvalidOperationException) { refused = true; }
Check(refused && declaration.Handlers.Count > 0, "a declared body ran in a harness that has no game");
declarationOwner.Dispose();

string linkRoot = Path.Combine(Path.GetTempPath(), "forge-game-bindings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(linkRoot);
try
{
    string plansDir = Path.Combine(linkRoot, "plugins", "Team-Pack", "forge", "plans");
    Directory.CreateDirectory(plansDir);
    string file = Path.Combine(plansDir, "a.plan.json"); File.WriteAllText(file, "{}");
    Check(FrameworkFiles.Resolve(linkRoot, "plugins/Team-Pack/forge/plans/a.plan.json") == Path.GetFullPath(file), "relative discovery path resolves");
    Reject(() => FrameworkFiles.Resolve(linkRoot, "../escape.json"), "traversal allowed");
    Reject(() => FrameworkFiles.Resolve(linkRoot, file), "absolute path allowed");
}
finally { Directory.Delete(linkRoot, true); }

string discoveryRoot = Path.Combine(Path.GetTempPath(), "forge-discovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(discoveryRoot);
try
{
    Check(PlanDiscovery.Scan(discoveryRoot).Count == 0, "missing plugins/ is never enumerated");
    string teamPlans = Path.Combine(discoveryRoot, "plugins", "Team-Pack", "forge", "plans");
    Directory.CreateDirectory(teamPlans);
    File.WriteAllText(Path.Combine(teamPlans, "a.plan.json"), "{}");
    File.WriteAllText(Path.Combine(teamPlans, "b.plan.json"), "{\"id\":2}");
    File.WriteAllText(Path.Combine(teamPlans, "ignored.txt"), "not a plan");
    Directory.CreateDirectory(Path.Combine(teamPlans, "nested"));
    File.WriteAllText(Path.Combine(teamPlans, "nested", "c.plan.json"), "{}");
    Directory.CreateDirectory(Path.Combine(discoveryRoot, "plugins", "OtherMod")); // no forge/plans: silently skipped
    File.WriteAllText(Path.Combine(discoveryRoot, "plugins", "loose.plan.json"), "{}"); // directly under plugins/: not a package directory
    var hits = PlanDiscovery.Scan(discoveryRoot);
    Check(hits.Count == 2 && hits.All(h => !h.IsHostRejected), "non-.plan.json files, nested files and packages without forge/plans have no effect: " + hits.Count);
    Check(hits[0].Path == "plugins/Team-Pack/forge/plans/a.plan.json" && hits[1].Path == "plugins/Team-Pack/forge/plans/b.plan.json", "ordinal path sort");

    File.WriteAllBytes(Path.Combine(teamPlans, "big.plan.json"), new byte[FrameworkFiles.MaximumPlanBytes + 1]);
    var big = PlanDiscovery.Scan(discoveryRoot).Single(h => h.Path.EndsWith("big.plan.json", StringComparison.Ordinal));
    Check(big.IsHostRejected && big.RejectedCode == "json-size", "oversized plan file rejected as json-size");
    File.Delete(Path.Combine(teamPlans, "big.plan.json"));
}
finally { Directory.Delete(discoveryRoot, true); }

string budgetRoot = Path.Combine(Path.GetTempPath(), "forge-discovery-budget-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(budgetRoot);
try
{
    var plansDir = Path.Combine(budgetRoot, "plugins", "Pack", "forge", "plans");
    Directory.CreateDirectory(plansDir);
    for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(plansDir, $"p{i}.plan.json"), "{}"); // 2 bytes each
    var byCount = PlanDiscovery.Scan(budgetRoot, 3, long.MaxValue);
    Check(byCount.Count(h => !h.IsHostRejected) == 3 && byCount.Count(h => h.IsHostRejected && h.RejectedCode == "plan-budget") == 2
        && byCount.Where(h => h.IsHostRejected).All(h => h.Path.EndsWith("p3.plan.json", StringComparison.Ordinal) || h.Path.EndsWith("p4.plan.json", StringComparison.Ordinal)),
        "combined file-count budget rejects the tail in sort order");
    var byBytes = PlanDiscovery.Scan(budgetRoot, 256, 3);
    Check(byBytes.Count(h => !h.IsHostRejected) == 1 && byBytes.Count(h => h.IsHostRejected && h.RejectedCode == "plan-budget") == 4,
        "combined byte budget rejects the tail in sort order");
}
finally { Directory.Delete(budgetRoot, true); }

string middleOverflowRoot = Path.Combine(Path.GetTempPath(), "forge-discovery-middle-overflow-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(middleOverflowRoot);
try
{
    // p2 sits in the ordinal middle and is the file that pushes the combined total over budget; a forward-greedy scan
    // would reject only p2 (it doesn't fit under the running total) and then let the smaller p3/p4 back in afterwards.
    // Tail-first eviction must instead keep evicting from the end - p4, then p3, then p2 itself - until the survivors
    // fit, so p2 and everything ordinally after it are rejected, not just p2 alone.
    var plansDir = Path.Combine(middleOverflowRoot, "plugins", "Pack", "forge", "plans");
    Directory.CreateDirectory(plansDir);
    File.WriteAllText(Path.Combine(plansDir, "p0.plan.json"), "{}");           // 2 bytes
    File.WriteAllText(Path.Combine(plansDir, "p1.plan.json"), "{}");           // 2 bytes
    File.WriteAllText(Path.Combine(plansDir, "p2.plan.json"), new string('a', 100)); // 100 bytes: the overflow cause
    File.WriteAllText(Path.Combine(plansDir, "p3.plan.json"), "{}");           // 2 bytes
    File.WriteAllText(Path.Combine(plansDir, "p4.plan.json"), "{}");           // 2 bytes
    var byName = PlanDiscovery.Scan(middleOverflowRoot, 256, 50).ToDictionary(h => Path.GetFileName(h.Path));
    Check(!byName["p0.plan.json"].IsHostRejected && !byName["p1.plan.json"].IsHostRejected,
        "files before the overflowing middle file survive tail-first eviction");
    Check(byName["p2.plan.json"].IsHostRejected && byName["p2.plan.json"].RejectedCode == "plan-budget"
        && byName["p3.plan.json"].IsHostRejected && byName["p3.plan.json"].RejectedCode == "plan-budget"
        && byName["p4.plan.json"].IsHostRejected && byName["p4.plan.json"].RejectedCode == "plan-budget",
        "the overflowing middle file and every file ordinally after it are rejected as plan-budget, not just the middle file");
}
finally { Directory.Delete(middleOverflowRoot, true); }

string junctionRoot = Path.Combine(Path.GetTempPath(), "forge-discovery-junction-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(junctionRoot);
try
{
    var realPlans = Path.Combine(junctionRoot, "real-plans"); Directory.CreateDirectory(realPlans);
    File.WriteAllText(Path.Combine(realPlans, "linked.plan.json"), "{}");
    var pluginDir = Path.Combine(junctionRoot, "plugins", "Pack"); Directory.CreateDirectory(Path.Combine(pluginDir, "forge"));
    var plansLink = Path.Combine(pluginDir, "forge", "plans");
    if (TryJunction(plansLink, realPlans))
    {
        var linked = PlanDiscovery.Scan(junctionRoot);
        Check(linked.Count == 1 && linked[0].IsHostRejected && linked[0].RejectedCode == "plan-path", "plans/ being a junction is rejected, not followed");

        var siblingTarget = Path.Combine(junctionRoot, "real-sibling"); Directory.CreateDirectory(siblingTarget);
        TryJunction(Path.Combine(junctionRoot, "plugins", "Sibling"), siblingTarget);
        Check(PlanDiscovery.Scan(junctionRoot).Count == 1, "a sibling package directory being a junction has no effect");
    }
    else
    {
        // This host denies junction creation both from a child cmd.exe/mklink and from a child powershell.exe/New-Item
        // process. A skip must not look green: both assertions are reported as blocked (INCOMPLETE), never silently
        // passed or hidden on stderr; re-run on a host that can create junctions before trusting this file's count.
        const string reason = "host denies NTFS junction creation from a child process (mklink /J and New-Item -ItemType Junction both refused)";
        blocked.Add(("discovery.junction-plans-rejected-not-followed", reason));
        blocked.Add(("discovery.junction-sibling-package-no-effect", reason));
    }
}
finally { UnlinkReparsePoints(junctionRoot); Directory.Delete(junctionRoot, true); }

// Directory.Delete(path, recursive: true) throws Access-Denied on this host once the tree contains a junction -
// a non-recursive delete on the junction's own path removes just the reparse point, leaving its target untouched,
// so every junction created for this test must be unlinked before the recursive delete of junctionRoot runs.
void UnlinkReparsePoints(string root)
{
    if (!Directory.Exists(root)) return;
    foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(dir, false);
}

bool TryJunction(string link, string target)
{
    var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!;
    mklink.WaitForExit();
    if (mklink.ExitCode == 0 && Directory.Exists(link)) return true;
    var newItem = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
        $"-NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '{link}' -Target '{target}' | Out-Null\"")
    { UseShellExecute = false, CreateNoWindow = true })!;
    newItem.WaitForExit();
    return newItem.ExitCode == 0 && Directory.Exists(link);
}

if (args.Length >= 2 && args[0] == "--export-manifest")
{
    var exportKernel = Kernel(); EnemyHandle(exportKernel);
    File.WriteAllText(args[1], exportKernel.ExportManifest());
    Console.WriteLine("ACTUAL MODULE MANIFEST " + Path.GetFullPath(args[1]));
}
if (args.Length >= 4 && args[0] == "--native") checks += NativeEvidence.Verify(args[1], args[2], args[3]);
if (args.Length >= 2 && args[0] == "--bridge")
{
    BepInEx.Paths.GameRootPath = Path.GetFullPath(args[1]);
    string isolated = Path.Combine(Path.GetTempPath(), "forge-bridge-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(isolated); BepInEx.Paths.BepInExRootPath = isolated;
    // A file at this path blocks FrameworkFiles.WriteManifest's own directory, forcing a startup failure unrelated
    // to any plan content (plan discovery is designed to never fail startup on its own).
    string manifestCollision = Path.Combine(isolated, "ForgeRuntime");
    string bridgePlanDir = Path.Combine(isolated, "plugins", "NativeHeal", "forge", "plans");
    // Raised the way GTFO-API raises them: OnBuildStart at the start of level generation, OnEnterLevel once the local
    // player can move, OnLevelCleanup when the level is gone.
    void State(eGameStateName state)
    {
        GameStateManager.CurrentStateName = state;
        if (state == eGameStateName.Generating) GTFO.API.LevelAPI.RaiseBuildStart();
        else if (state == eGameStateName.InLevel) GTFO.API.LevelAPI.RaiseEnterLevel();
        else if (state is eGameStateName.Lobby or eGameStateName.NoLobby or eGameStateName.Offline) GTFO.API.LevelAPI.RaiseLevelCleanup();
    }
    void Frame() => GameRuntimeBridge.Guard(GameRuntimeBridge.FixedTick);
    RuntimeModuleHandle? bridgeEnemies = null;
    var records = new List<CommandContext>(); Action? onRecord = null;
    void InitializeBridge()
    {
        bridgeEnemies?.Dispose();
        GameRuntimeBridge.Initialize(RuntimeLogLevel.Error);
        // The provider's own declaration, with every body stood in for: the bridge is what this mode exercises, and
        // the observation the native hooks publish is modelled by publishing the same facts under the same bindings.
        bridgeEnemies = EnemyHandle(GameRuntimeBridge.Kernel!);
        GameRuntimeBridge.Kernel!.RegisterModule(Recorder(context => { records.Add(context); onRecord?.Invoke(); }), RuntimeLogLevel.Off);
    }
    // death_started is claimed once per life: every dispatch attempt spawns a fresh life, so a closed gate is what stops it.
    EntityReference? life = null; int lifeEpoch = 0;
    EntityReference Spawn(ushort id = 7)
    {
        var next = new EntityReference("gtfo.enemy:" + id, GameRuntimeBridge.Kernel!.WorldEpoch, ++lifeEpoch);
        Check(next != life, "bridge death attempt reused a life"); life = next; return next;
    }
    void Die(EntityReference target) => Publish("death", EnemyRegistration.DeathStartedBinding, target,
        new { enemy = target, source = (EntityReference?)null });
    void Damage(EntityReference target, double amount) => Publish("damage", EnemyRegistration.DamageBinding, target,
        new { source = (EntityReference?)null, target, amount, damage_kind = (int?)null, limb = (int?)null });
    void Publish(string name, string binding, EntityReference target, object outputs)
    {
        var queued = bridgeEnemies!.Publish(new RuntimeEvent("test.bridge." + name + ":" + target.LifeEpoch, binding,
            GameRuntimeBridge.Kernel!.WorldEpoch, GameRuntimeBridge.Kernel!.CurrentTick, "test.bridge.scope", RuntimeJson.From(outputs)));
        Check(queued.Status == "queued" && queued.Code == "accepted", "bridge fact " + name + " refused: " + queued.Status + "/" + queued.Code);
    }
    RuntimeModule ProbeModule() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new {
        providers = new[] { new { id = "test.bridge.lifecycle", kind = "native", version = "1.0.0", dependencies = Array.Empty<object>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
    // The plugin subscribes these in Plugin.Load; this harness drives the level lifecycle through the same GTFO-API
    // events (`State` below), so it must subscribe exactly as the plugin does before the first one is raised.
    LevelLifecycle.Subscribe();
    try
    {
        File.WriteAllText(manifestCollision, "blocks the manifest directory");
        InitializeBridge();
        var failedKernel = GameRuntimeBridge.Kernel!;
        // The Runtime's own built-in providers ship no package cfg, so they carry the Runtime's cfg level; the domain
        // and probe modules this harness registers hand over their own levels. The rule is therefore asserted over the
        // providers the host registers itself - the two modules GameRuntimeBridge hands to RegisterBuiltinModule, whose
        // ids are read from those modules rather than spelled a second time here - and not over every provider the
        // manifest happens to list.
        var builtins = new[] { CombatContracts.Module(), ControlContracts.Module() }
            .SelectMany(module => RuntimeJson.Rows(RuntimeJson.Parse(module.RegistryJson), "providers").Select(row => RuntimeJson.Text(row, "id"))).ToArray();
        Check(builtins.Length > 0 && builtins.All(id => failedKernel.LogGate(id).Level == RuntimeLogLevel.Error)
            && failedKernel.LogGate(EnemyRegistration.ProviderId).Level == RuntimeLogLevel.Off,
            "a Runtime built-in provider did not take the Runtime's own log level, or a package provider lost its own");
        var failedOwner = failedKernel.RegisterModule(ProbeModule(), RuntimeLogLevel.Off);
        var failedStates = new List<RuntimeLifecycleEvent>();
        var failedSub = failedOwner.ObserveLifecycle(failedStates.Add);
        Check(failedKernel.IsRegistrationOpen && !GameRuntimeBridge.CanExecute, "initial registration window and phase gate");
        Frame(); int logs = Plugin.PluginLog.Messages.Count;
        Check(failedKernel.StartupState == RuntimeStartupState.Failed && !failedKernel.IsRegistrationOpen, "bridge startup failure must latch in the public kernel");
        Check(failedStates.Last().Current.StartupState == RuntimeStartupState.Failed, "startup failure not observed");
        State(eGameStateName.Generating); State(eGameStateName.InLevel);
        Check(!GameRuntimeBridge.CanExecute, "state transition bypassed failed startup");
        var writeTime = File.GetLastWriteTimeUtc(manifestCollision);
        Directory.CreateDirectory(bridgePlanDir);
        File.WriteAllText(Path.Combine(bridgePlanDir, "death-record.plan.json"), DeathRecordPlan(failedKernel));
        File.WriteAllText(Path.Combine(bridgePlanDir, "damage-record.plan.json"), DamageRecordPlan(failedKernel));
        for (int i = 0; i < 100; i++) Frame();
        Check(GameRuntimeBridge.Kernel!.LoadedPlans == 0 && Plugin.PluginLog.Messages.Count == logs && File.GetLastWriteTimeUtc(manifestCollision) == writeTime,
            "actual FixedTick retries failed plan or manifest IO");
        GameRuntimeBridge.Stop(); GameRuntimeBridge.Stop();
        Check(failedKernel.StartupState == RuntimeStartupState.Stopped && !failedSub.IsActive, "failed bridge stop retained observers");
        failedOwner.Dispose();
        File.Delete(manifestCollision);
        InitializeBridge();
        var liveKernel = GameRuntimeBridge.Kernel!;
        var liveOwner = liveKernel.RegisterModule(ProbeModule(), RuntimeLogLevel.Off);
        var liveStates = new List<RuntimeLifecycleEvent>();
        var liveSub = liveOwner.ObserveLifecycle(liveStates.Add);
        var badSub = liveOwner.ObserveLifecycle(e => { if (e.Kind == RuntimeLifecycleKind.StartupChanged) throw new InvalidOperationException("observer test failure"); }, false);
        State(eGameStateName.Generating); var actor = Spawn();
        State(eGameStateName.InLevel); Frame(); Die(actor); Frame();
        Check(liveKernel.LoadedPlans == 2 && records.Count == 1, "real bridge loads both configured plans and dispatches");
        // The configured damage plan is wired from the fact the native damage window publishes, so the observation
        // dispatches it once and a later frame never retries it.
        var hit = Spawn(8); Damage(hit, 60); Frame();
        Check(records.Count == 2, "configured damage plan must dispatch exactly once on the observed fact; records=" + records.Count);
        Frame();
        Check(records.Count == 2, "the dispatched fact must not retry on a later frame");
        Check(liveKernel.StartupState == RuntimeStartupState.Ready && GameRuntimeBridge.CanExecute, "bridge readiness/phase gate not connected");
        Check(!badSub.IsActive && liveSub.IsActive && liveKernel.LifecycleFaultCount == 1, "bad observer stopped healthy gameplay");
        Check(liveStates.Count(e => e.Kind == RuntimeLifecycleKind.StartupChanged && e.Current.StartupState == RuntimeStartupState.Ready) == 1, "ready notification repeated");
        Reject(() => liveKernel.RegisterModule(ProbeModule(), RuntimeLogLevel.Off), "late module registration allowed by bridge");
        // The kernel records step/event outcomes itself, so the bridge adds no summary line of its own and an
        // observer fault cannot surface twice. Faults are counted by the kernel, not mirrored into the BepInEx log.
        Check(!Plugin.PluginLog.Messages.Any(m => m.Contains("Forge lifecycle observer removed", StringComparison.Ordinal)
            || m.Contains("Forge command ", StringComparison.Ordinal) || m.Contains("Forge event ", StringComparison.Ordinal)),
            "bridge still mirrors kernel records into the BepInEx log");
        Frame(); Frame();
        Check(liveKernel.LifecycleFaultCount == 1, "observer error repeats every frame");
        Exception? threadError = null;
        var thread = new System.Threading.Thread(() => { try { _ = GameRuntimeBridge.CanExecute; } catch (Exception e) { threadError = e; } });
        thread.Start(); thread.Join();
        Check(threadError is RuntimeContractException contract && contract.Code == "wrong-thread", "phase gate accessed native state from wrong thread");
        // A suspension that waits for a fresh expedition. A checkpoint reload is no longer one of them: it restores
        // the saved variables and re-arms the plans instead, which `tests/Variables` covers.
        GameRuntimeBridge.Suspend("bridge-exception", "test restore", true);
        State(eGameStateName.Generating); actor = Spawn(); State(eGameStateName.InLevel); Frame(); Die(actor); Frame();
        Check(records.Count == 2, "generation cannot bypass a suspension that waits for a fresh expedition");
        State(eGameStateName.Lobby); State(eGameStateName.Generating); actor = Spawn();
        State(eGameStateName.InLevel); Frame(); Die(actor); Frame();
        Check(records.Count == 3, "explicit fresh expedition can execute after restore rejection");
        SNet.MasterManagement.IsMigrating = true; Frame(); SNet.MasterManagement.IsMigrating = false;
        State(eGameStateName.Generating); actor = Spawn(); State(eGameStateName.InLevel); Frame(); Die(actor); Frame();
        Check(records.Count == 3, "migration cannot silently resume on generation state");
        State(eGameStateName.Lobby); State(eGameStateName.Generating); actor = Spawn();
        State(eGameStateName.InLevel); Frame();
        onRecord = () => State(eGameStateName.Lobby);
        int beforeTeardown = records.Count; Die(actor); long epoch = GameRuntimeBridge.Kernel!.WorldEpoch; Frame(); onRecord = null;
        Check(records.Count == beforeTeardown + 1 && GameRuntimeBridge.Kernel.WorldEpoch > epoch && GameRuntimeBridge.Kernel.QueuedEvents == 0,
            "native teardown during dispatch must flush at return without reentrant core mutation");
        Check(!GameRuntimeBridge.CanExecute, "lobby must close action phase gate");
        State(eGameStateName.Generating); actor = Spawn();
        State(eGameStateName.InLevel); Frame();
        bool gateClosedInsideCommit = false;
        onRecord = () => { GameRuntimeBridge.Stop(); gateClosedInsideCommit = !GameRuntimeBridge.CanExecute; };
        int beforeStop = records.Count; Die(actor); Frame(); onRecord = null;
        Check(gateClosedInsideCommit && records.Count == beforeStop + 1, "teardown did not block further commits immediately");
        Check(GameRuntimeBridge.Kernel == null && liveKernel.StartupState == RuntimeStartupState.Stopped, "deferred bridge stop did not stop retained kernel");
        Check(liveKernel.LoadedPlans == 0 && liveKernel.QueuedEvents == 0 && !liveSub.IsActive, "deferred stop leaked tasks/plans/observers");
        Reject(() => liveKernel.Advance(999, true), "retained kernel executed after host stop");
        liveOwner.Dispose(); liveSub.Dispose();
        SNet.IsMaster = false;
        InitializeBridge();
        State(eGameStateName.Generating); State(eGameStateName.InLevel); Frame();
        var clientKernel = GameRuntimeBridge.Kernel!;
        Check(clientKernel.Lifecycle.IsHost == false && !GameRuntimeBridge.CanExecute, "client entered gameplay commit phase");
        SNet.IsMaster = true;
        Check(!GameRuntimeBridge.CanExecute, "promotion before next simulation tick bypassed authority gate");
        // GTFO-API reports the level only once per build, so a repeated notification is modelled directly on the bridge.
        GameRuntimeBridge.EnterLevel();
        Check(!GameRuntimeBridge.CanExecute, "duplicate EnterLevel notification reset authority baseline");
        Frame();
        Check(!GameRuntimeBridge.CanExecute, "unsupported authority transition resumed gameplay");
    }
    finally { LevelLifecycle.Unsubscribe(); GameRuntimeBridge.Stop(); bridgeEnemies?.Dispose(); SNet.IsMaster = true; SNet.MasterManagement.IsMigrating = false; Directory.Delete(isolated, true); }
}
if (args.Length >= 2 && args[0] == "--bridge")
{
    // A hash mismatch suspends the host instead of throwing: the host latches as suspended, depends on nothing the
    // unsupported binary could provide, and writes exactly one runtime.suspended record with the expected and actual hash.
    string suspendedRoot = Path.Combine(Path.GetTempPath(), "forge-suspend-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(suspendedRoot);
    // The mismatch is driven through the real comparison: the game root handed to the bridge holds a decoy
    // GameAssembly.dll. The supported hash is a static readonly field, so a harness cannot replace it once the type is
    // initialized - and the main bridge scenario above has already initialized it, which makes the write throw.
    string decoyRoot = Path.Combine(Path.GetTempPath(), "forge-binary-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(decoyRoot);
    var decoy = Encoding.UTF8.GetBytes("decoy GameAssembly, not the supported binary");
    File.WriteAllBytes(Path.Combine(decoyRoot, "GameAssembly.dll"), decoy);
    string decoyHash = Convert.ToHexString(SHA256.HashData(decoy));
    try
    {
        BepInEx.Paths.GameRootPath = decoyRoot;
        BepInEx.Paths.BepInExRootPath = suspendedRoot;
        GameRuntimeBridge.Initialize(RuntimeLogLevel.Error);
        var suspendedKernel = GameRuntimeBridge.Kernel!;
        Check(GameRuntimeBridge.Suspension == "startup-failed" && suspendedKernel.StartupState == RuntimeStartupState.Registering
            && suspendedKernel.WorldEpoch == 0 && suspendedKernel.LoadedPlans == 0,
            "hash mismatch did not latch a suspended host before any world");
        // Suspension is the host's latch, not a kernel state - the Framework cannot read the game binary - so the
        // kernel stays in Registering and it is the host's own tick that must never start it.
        GameRuntimeBridge.Guard(GameRuntimeBridge.FixedTick); GameRuntimeBridge.Guard(GameRuntimeBridge.FixedTick);
        Check(!GameRuntimeBridge.CanExecute && suspendedKernel.StartupState == RuntimeStartupState.Registering,
            "a suspended host started its kernel or exposed gameplay");
        // A valid plan the host must never read: the suspended kernel carries only the Runtime's built-ins, so the text
        // comes from a harness kernel that has the domain and recorder modules registered.
        var planSource = Kernel(); EnemyHandle(planSource);
        planSource.RegisterModule(Recorder(_ => { }), RuntimeLogLevel.Off);
        Directory.CreateDirectory(Path.Combine(suspendedRoot, "plugins", "NativeHeal", "forge", "plans"));
        File.WriteAllText(Path.Combine(suspendedRoot, "plugins", "NativeHeal", "forge", "plans", "death-record.plan.json"), DeathRecordPlan(planSource));
        // Lifecycle events still arrive on a suspended host; none of them may throw or start anything.
        GameRuntimeBridge.BeginGeneration(); GameRuntimeBridge.EnterLevel();
        GameRuntimeBridge.Guard(GameRuntimeBridge.FixedTick); GameRuntimeBridge.LeaveLevel();
        Check(!GameRuntimeBridge.CanExecute && suspendedKernel.StartupState == RuntimeStartupState.Registering
            && suspendedKernel.WorldEpoch == 0 && suspendedKernel.LoadedPlans == 0,
            "lifecycle events started or loaded a suspended host");
        Check(!Directory.Exists(Path.Combine(suspendedRoot, "ForgeRuntime")), "a suspended host exported its manifest");
        GameRuntimeBridge.Stop();
        var files = Directory.GetFiles(Path.Combine(suspendedRoot, "forge-logs"), "*.jsonl");
        Check(files.Length == 1, "a suspended host wrote " + files.Length + " session files");
        var lines = File.ReadAllLines(files[0]).Where(line => line.Length > 0).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var suspension = lines.Where(line => line.GetProperty("code").GetString() == "runtime.suspended").ToArray();
        Check(suspension.Length == 1, "a suspended host wrote " + suspension.Length + " runtime.suspended records");
        Check(suspension[0].GetProperty("level").GetString() == "error" && suspension[0].GetProperty("provider").GetString() == "forge.runtime"
            && suspension[0].GetProperty("result").GetProperty("status").GetString() == "failed"
            && suspension[0].GetProperty("result").GetProperty("reason").GetString() == "startup-failed",
            "runtime.suspended did not carry startup-failed");
        var message = suspension[0].GetProperty("message").GetString()!;
        Check(message.Contains(GameRuntimeBridge.GameAssemblySha256, StringComparison.Ordinal) && message.Contains(decoyHash, StringComparison.Ordinal),
            "runtime.suspended message does not name the expected and actual hash");
    }
    finally
    {
        GameRuntimeBridge.Stop(); SNet.IsMaster = true; SNet.MasterManagement.IsMigrating = false;
        Directory.Delete(suspendedRoot, true); Directory.Delete(decoyRoot, true);
    }
}
if (args.Length >= 2 && args[0] == "--fixtures")
{
    string root = Path.GetFullPath(args[1]);
    var cases = RuntimeJson.Parse(File.ReadAllText(Path.Combine(root, "cases.json")));
    var live = Kernel(); EnemyHandle(live);
    // The website plans are compiled against the runtime's own manifest, and every invalid plan is a one-field edit of
    // a valid one. Pins older than this registry would make each invalid plan "fail" on binding-lock instead of its own
    // defect, and a plan recorded for another wire version would make every case "fail" on plan-version, so a stale
    // fixture set is blocked as a whole rather than run.
    const int planSchemaVersion = 4; // The one version RuntimePlan.Parse reads (`plan-version`).
    var registry = RuntimeJson.Parse(live.ExportManifest()).GetProperty("registry");
    string Registered(string list, string id) => registry.GetProperty(list).EnumerateArray()
        .Where(r => r.GetProperty("id").GetString() == id).Select(r => r.GetProperty("version").GetString()!).DefaultIfEmpty("unregistered").First();
    var planRows = cases.GetProperty("plans").EnumerateArray()
        .Select(row => (File: row.GetProperty("plan").GetString()!, Plan: RuntimeJson.Parse(File.ReadAllText(Path.Combine(root, row.GetProperty("plan").GetString()!))))).ToArray();
    var stalePlans = planRows.Where(row => row.Plan.GetProperty("schemaVersion").GetInt32() != planSchemaVersion)
        .Select(row => $"{row.File} schemaVersion {row.Plan.GetProperty("schemaVersion").GetInt32()} vs {planSchemaVersion}").ToArray();
    var stale = stalePlans.Concat(planRows
        .SelectMany(row => row.Plan.GetProperty("bindings").EnumerateArray())
        .SelectMany(pin => new[] {
            (Id: pin.GetProperty("capabilityId").GetString()!, Pinned: pin.GetProperty("capabilityVersion").GetString()!),
            (Id: pin.GetProperty("providerId").GetString()!, Pinned: pin.GetProperty("providerVersion").GetString()!) }
            .Select((x, index) => (x.Id, x.Pinned, Current: Registered(index == 0 ? "capabilities" : "providers", x.Id))))
        .Where(x => x.Pinned != x.Current).Distinct().Select(x => $"{x.Id} {x.Pinned} vs registry {x.Current}")).ToArray();
    if (stale.Length > 0)
    {
        string reason = "website fixture pins predate the registry (" + string.Join("; ", stale) + ")";
        blocked.Add(("fixtures.valid-plan-dispatch", reason));
        blocked.Add(("fixtures.invalid-plan-rejections", reason));
        blocked.Add(("fixtures.level-dispatch", reason));
    }
    else
    {
        // Every valid plan the website ships must load against the registry this harness declares. The plans are
        // load-validated by the Framework suite and dispatched by `tests/LifecycleWork --fixtures`, which stands a
        // double in for each row the manifest declares; what this mode keeps is the fixture set tied to this harness's
        // own registry, so a plan whose pins no longer resolve is a stale fixture set rather than a passing run.
        foreach (var row in planRows) live.LoadPlan(row.Plan.GetRawText());
        Check(live.LoadedPlans == planRows.Length,
            $"the fixture's {planRows.Length} plans must load against the declared registry; loaded {live.LoadedPlans}");
        foreach (var invalid in cases.GetProperty("invalidPlans").EnumerateArray())
        {
            string id = invalid.GetProperty("id").GetString()!;
            var candidate = Kernel(); EnemyHandle(candidate);
            string bad = File.ReadAllText(Path.Combine(root, invalid.GetProperty("file").GetString()!));
            // Every case whose refusal code belongs to the two-repository ABI records that code in cases.json; the
            // plan must be refused with that same code, not merely refused. A case without one is refused by a rule
            // only the website states, so rejection alone is what can be asserted for it here.
            string? expected = invalid.TryGetProperty("code", out var recorded) ? recorded.GetString() : null;
            string? actual = null;
            try { candidate.LoadPlan(bad); }
            catch (RuntimeContractException error) { actual = error.Code; }
            catch (Exception error) when (error is InvalidDataException or ArgumentException) { actual = error.GetType().Name; }
            Check(actual != null, "accepted invalid fixture " + id);
            if (actual != null && expected != null)
                Check(actual == expected, $"invalid fixture {id} was refused with {actual}; cases.json records {expected}");
        }
        // The shared level-mount vector: the shipped plan is claimed by a world standing on the level it mounts, and
        // by no other. The refusal is the mount comparison's own `attachment-mismatch`, read off the publish that
        // carries it, and a refused world runs none of the plan's commands.
        var dispatch = cases.GetProperty("levelDispatch");
        var mounted = File.ReadAllText(Path.Combine(root, dispatch.GetProperty("plan").GetString()!));
        var worlds = new[] { (Level: dispatch.GetProperty("match").GetString()!, Dispatched: true) }
            .Concat(dispatch.GetProperty("mismatches").EnumerateArray().Select(m => (Level: m.GetString()!, Dispatched: false)));
        foreach (var (level, dispatched) in worlds)
        {
            var scripted = Kernel(level);
            var owner = EnemyHandle(scripted);
            var target = new EntityReference("gtfo.enemy:7", scripted.WorldEpoch, 1);
            scripted.LoadPlan(mounted);
            scripted.StartRuntime(() => { });
            var queued = owner.Publish(new RuntimeEvent("fixture.level:" + level, EnemyRegistration.DamageBinding,
                scripted.WorldEpoch, 0, "fixture.level", RuntimeJson.From(new { source = (EntityReference?)null, target, amount = 10d,
                    damage_kind = (int?)null, limb = (int?)null })));
            Check(dispatched ? queued.Status == "queued" && queued.Code == "accepted"
                    : queued.Status == "ignored" && queued.Code == "attachment-mismatch",
                $"levelDispatch {level}: {queued.Status}/{queued.Code}");
            var world = scripted.Advance(0, true);
            Check(dispatched ? world.CommandsExecuted == 1 : world.CommandsExecuted == 0,
                $"levelDispatch {level}: commands={world.CommandsExecuted}");
        }
    }
    Check(messages.Count == 0, "unexpected module diagnostics: " + string.Join(";", messages));
}
checks += ForgeRuntime.GameBindings.Tests.PresentationBridgeTests.Run();
foreach (var group in blocked.GroupBy(b => b.Reason, StringComparer.Ordinal))
{
    Console.WriteLine($"BLOCKED {group.Count()} ({group.Key})");
    foreach (var row in group) Console.WriteLine("  " + row.Id);
}
Console.WriteLine($"{(blocked.Count > 0 ? "INCOMPLETE" : "PASS")} {checks} declared-boundary assertions; BLOCKED {blocked.Count}. The game-bound module is not compiled here: the enemy provider's rows are registered from their declaration and every body is a stand-in. Native API execution and multiplayer are not exercised by these doubles.");

