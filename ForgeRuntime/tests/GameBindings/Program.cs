using System.Reflection;
using System.Text.Json;
using Enemies;
using ForgeRuntime;
using ForgeRuntime.Framework;
using ForgeRuntime.GameBindings;
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
RuntimeKernel Kernel()
{
    var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"), new RuntimeLimits());
    kernel.BeginWorld(1); kernel.RegisterModule(CombatContracts.Module()); kernel.RegisterModule(ControlContracts.Module());
    kernel.RegisterModule(ForgeTrigger.ModuleDefinition.Create()); return kernel;
}
EnemyAgent Enemy(ushort id = 7, long pointer = 10)
{
    var enemy = new EnemyAgent { GlobalID = id, Pointer = new IntPtr(pointer) };
    enemy.Damage = new Dam_EnemyDamageBase { Owner = enemy, Pointer = new IntPtr(pointer + 100) };
    return enemy;
}
CommandResult Heal(EnemyModule module, EntityReference target, double amount = 5)
{
    var origin = new RuntimeEvent("test.damage:1", EnemyModule.DamageBinding, 1, 0, "test.scope", RuntimeJson.EmptyObject);
    var context = new CommandContext(origin, 0, "test.command", "test.plan", "test.resource", "1", "test.node",
        RuntimeJson.From(new { overheal_policy = "clamp" }), RuntimeJson.From(new { targets = new[] { target }, source = target, amount }));
    // This is the production module's registered handler, invoked directly for receiver boundary cases.
    return (CommandResult)typeof(EnemyModule).GetMethod("Heal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, new object[] { context })!;
}
JsonElement HealRow(CommandResult result) => result.Outputs.GetProperty("results").EnumerateArray().First();
// A parameterless QA action that hands each dispatched command to the test.
const string RecordBinding = "test.bridge.binding.record";
RuntimeModule Recorder(Action<CommandContext> record) => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
{
    providers = new[] { new { id = "test.bridge", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
    capabilities = new[] { new { id = "test.bridge.action.record", owner = "test.bridge", kind = "action", label = "QA record", version = "1.0.0", parameters = new { },
        graph = new { domains = new[] { "enemy" }, execution = "host",
            inputs = new[] { new { id = "in", type = "execution" }, new { id = "target", type = "entity" } },
            outputs = new object[] { new { id = "next", type = "execution" }, new { id = "result", type = "result", schema = "test.bridge.result.record" } },
            parameters = Array.Empty<object>(), recipients = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" } } } },
    bindings = new[] { new { id = RecordBinding, capabilityId = "test.bridge.action.record", providerId = "test.bridge", handler = "test.bridge.record",
        role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() } }
}).GetRawText(), new Dictionary<string, CommandHandler> { ["test.bridge.record"] = context => { record(context); return CommandResult.Succeeded(RuntimeJson.EmptyObject); } },
    new[] { new BindingSupport(RecordBinding, "implementation-only", new[] { "test.record" }) });
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
        schemaVersion = 3, kind = "forge-runtime-plan", planId, resource = new { id = planId, revision = "1" }, runtime = k.Identity,
        domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions, dependencies = Array.Empty<string>(),
        limits = new { k.Limits.MaxEventsPerTick, k.Limits.MaxCommandsPerTick, k.Limits.MaxQueuedEvents, k.Limits.MaxCausalDepth }, bindings = pins,
        // One action step: the graph is a single node, so it is trivially its own canonical order and the entrypoint
        // ends at its single (unwired) execution output.
        entrypoints = new[] { new { nodeId = "Fact", binding = Array.IndexOf(ids, trigger), layout = Layout(trigger), start = 0,
            steps = new[] { new { nodeId = "Step", nodeKind = "action", binding = Array.IndexOf(ids, action), layout = Layout(action), inputs, successors = new int?[] { null } } } } }
    }).GetRawText();
}
string DeathRecordPlan(RuntimeKernel k) => LocalPlan(k, "test.bridge.death", EnemyModule.DeathStartedBinding, RecordBinding,
    new[] { ("enemy", "target") }, Array.Empty<(string, object)>(), RuntimeJson.EmptyObject);
// damage_applied -> heal(targets <- target wrapped, source <- target, amount = 5, overheal_policy = clamp index 0).
string DamageHealPlan(RuntimeKernel k) => LocalPlan(k, "test.bridge.damage-heal", EnemyModule.DamageBinding, EnemyModule.HealBinding,
    new[] { ("target", "targets"), ("target", "source") }, new[] { ("amount", (object)5.0) }, RuntimeJson.From(new { overheal_policy = 0 }));

// The failure latch is tested across 100 further ticks, not merely a mode boolean.
var startup = Kernel(); int attempts = 0;
try { startup.StartRuntime(() => { attempts++; throw new IOException("bad configured plan"); }); } catch (IOException) { }
for (int i = 0; i < 100; i++) startup.StartRuntime(() => attempts++);
Check(attempts == 1 && startup.StartupState == RuntimeStartupState.Failed, "failed startup repeats file IO");
var successfulStartup = Kernel();
successfulStartup.StartRuntime(() => attempts++); successfulStartup.StartRuntime(() => attempts++);
Check(attempts == 2 && successfulStartup.StartupState == RuntimeStartupState.Ready, "successful startup must execute once");

var kernel = Kernel(); bool allowed = true;
var messages = new List<string>();
var module = new EnemyModule(kernel, () => allowed, messages.Add);
var enemy = Enemy(); var reference = module.TrackSpawn(enemy);
var result = Heal(module, reference);
Check(result.Status == "succeeded" && enemy.Damage.Health == 55 && enemy.Damage.Sends == 1, "explicit target +5 HP");
Check(HealRow(result).GetProperty("actualAmount").GetDouble() == 5 && result.Facts.Count == 1, "healing reports committed delta");
Check(result.Facts[0].BindingId == EnemyModule.HealthChangedBinding && result.Facts[0].Outputs.GetProperty("delta").GetDouble() == 5, "typed health fact");
enemy.Damage.Health = 99;
result = Heal(module, reference);
Check(enemy.Damage.Health == 100 && HealRow(result).GetProperty("actualAmount").GetDouble() == 1, "heal clamps at native max");
int sends = enemy.Damage.Sends;
result = Heal(module, reference);
Check(result.Status == "succeeded" && result.Facts.Count == 0 && enemy.Damage.Sends == sends, "full health is zero effective, no packet or fact");
enemy.Alive = false; enemy.Damage.Health = 0;
Check(Heal(module, reference).Code == "not-alive" && enemy.Damage.Health == 0, "heal cannot revive");
enemy.Alive = true; enemy.Damage.Health = 50; enemy.Damage.IsSetup = false;
Check(Heal(module, reference).Code == "missing-health-receiver", "uninitialized receiver");
enemy.Damage.IsSetup = true;
Check(Heal(module, new EntityReference("gtfo.player:7", 1, reference.LifeEpoch)).Status == "rejected", "unsupported player is never retargeted");
Check(Heal(module, reference with { LifeEpoch = reference.LifeEpoch + 1 }).Status == "rejected", "stale life");
Check(Heal(module, reference with { WorldEpoch = 2 }).Status == "rejected", "stale world");
Check(Heal(module, reference, 0).Code == "amount-out-of-range", "zero amount rejected");
Check(Heal(module, reference, -1).Code == "amount-out-of-range", "negative damage is not healing");
Check(Heal(module, reference, EnemyModule.MaximumAmount + 1).Code == "amount-out-of-range", "amount budget");
allowed = false; Check(Heal(module, reference).Code == "authority-or-phase", "phase gated at commit"); allowed = true;
SNet.IsMaster = false; Check(Heal(module, reference).Code == "authority-or-phase", "client cannot commit"); SNet.IsMaster = true;
enemy.Damage.Health = float.NaN;
Check(Heal(module, reference).Status == "rejected", "invalid health rejected"); enemy.Damage.Health = 50;
enemy.Damage.HealthMax = float.PositiveInfinity;
Check(Heal(module, reference).Code == "invalid-health-state", "invalid maximum rejected"); enemy.Damage.HealthMax = 100;
enemy.Damage.Commit = _ => { };
result = Heal(module, reference, EnemyModule.MinimumAmount);
Check(result.Status == "succeeded" && result.Facts.Count == 0 && HealRow(result).GetProperty("actualAmount").GetDouble() == 0, "quantization never creates fake healing");
int beforeQuantizedSkip = enemy.Damage.Sends;
SFloat16.Preview = (_, _) => 49.999f;
result = Heal(module, reference, EnemyModule.MinimumAmount);
Check(result.Status == "succeeded" && enemy.Damage.Sends == beforeQuantizedSkip, "sub-quantum heal never lowers health");
SFloat16.Preview = (value, _) => value;
enemy.Damage.Commit = _ => enemy.Damage.Health = 49;
Check(Heal(module, reference).Code == "unexpected-health-readback", "unexpected native result is failure");
enemy.Damage.Health = 50; enemy.Damage.Commit = _ => module.TrackDespawn(enemy);
Check(Heal(module, reference).Code == "receiver-changed-during-commit", "removed receiver cannot report success");
enemy.Damage.Commit = null;
var replacement = Enemy(pointer: 20); var nextLife = module.TrackSpawn(replacement);
Check(nextLife.LifeEpoch != reference.LifeEpoch && Heal(module, reference).Status == "rejected", "reused global id invalidates old life");
module.TrackDespawn(enemy);
Check(Heal(module, nextLife).Status == "succeeded", "old pointer teardown must not remove replacement");
kernel.BeginWorld(2); module.ClearWorld();
Check(Heal(module, nextLife).Status == "rejected", "world reset invalidates recipients");

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
    var exportKernel = Kernel(); _ = new EnemyModule(exportKernel, () => true, messages.Add);
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
    // to any plan content (D-009 plan discovery is designed to never fail startup on its own).
    string manifestCollision = Path.Combine(isolated, "ForgeRuntime");
    string bridgePlanDir = Path.Combine(isolated, "plugins", "NativeHeal", "forge", "plans");
    void State(eGameStateName state) { GameStateManager.CurrentStateName = state; GameRuntimeBridge.StateChanged(state); }
    void Frame() => GameRuntimeBridge.Guard(GameRuntimeBridge.FixedTick);
    EnemyModule? bridgeEnemies = null;
    var records = new List<CommandContext>(); Action? onRecord = null;
    void InitializeBridge()
    {
        bridgeEnemies?.Dispose();
        GameRuntimeBridge.Initialize(RuntimeLogLevel.Error);
        bridgeEnemies = new EnemyModule(GameRuntimeBridge.Kernel!, () => GameRuntimeBridge.CanExecute, message => Plugin.PluginLog.LogWarning(message));
        GameRuntimeBridge.Kernel!.RegisterModule(Recorder(context => { records.Add(context); onRecord?.Invoke(); }));
    }
    // death_started is claimed once per life: every dispatch attempt spawns a fresh life, so a closed gate is what stops it.
    EntityReference? life = null;
    void Spawn(EnemyAgent actor)
    {
        actor.Alive = true; var next = bridgeEnemies!.TrackSpawn(actor);
        Check(next != life, "bridge death attempt reused a life"); life = next;
    }
    void Die(EnemyAgent actor)
    {
        var observed = bridgeEnemies!.BeforeDeath(actor);
        actor.Alive = false;
        bridgeEnemies.AfterDeath(actor, observed);
    }
    RuntimeModule ProbeModule() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new {
        providers = new[] { new { id = "test.bridge.lifecycle", kind = "native", version = "1.0.0", dependencies = Array.Empty<object>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
    try
    {
        File.WriteAllText(manifestCollision, "blocks the manifest directory");
        InitializeBridge();
        var failedKernel = GameRuntimeBridge.Kernel!;
        var failedOwner = failedKernel.RegisterModule(ProbeModule());
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
        File.WriteAllText(Path.Combine(bridgePlanDir, "damage-heal.plan.json"), DamageHealPlan(failedKernel));
        for (int i = 0; i < 100; i++) Frame();
        Check(GameRuntimeBridge.Kernel!.LoadedPlans == 0 && Plugin.PluginLog.Messages.Count == logs && File.GetLastWriteTimeUtc(manifestCollision) == writeTime,
            "actual FixedTick retries failed plan or manifest IO");
        GameRuntimeBridge.Stop(); GameRuntimeBridge.Stop();
        Check(failedKernel.StartupState == RuntimeStartupState.Stopped && !failedSub.IsActive, "failed bridge stop retained observers");
        failedOwner.Dispose();
        File.Delete(manifestCollision);
        InitializeBridge();
        var liveKernel = GameRuntimeBridge.Kernel!;
        var liveOwner = liveKernel.RegisterModule(ProbeModule());
        var liveStates = new List<RuntimeLifecycleEvent>();
        var liveSub = liveOwner.ObserveLifecycle(liveStates.Add);
        var badSub = liveOwner.ObserveLifecycle(e => { if (e.Kind == RuntimeLifecycleKind.StartupChanged) throw new InvalidOperationException("observer test failure"); }, false);
        State(eGameStateName.Generating); var actor = Enemy(); Spawn(actor);
        State(eGameStateName.InLevel); Frame(); Die(actor); Frame();
        Check(liveKernel.LoadedPlans == 2 && records.Count == 1, "real bridge loads both configured plans and dispatches");
        // bridge.configured-heal-plan-commits-5hp: the discovered damage -> heal plan commits +5 HP once through the real handler.
        var healed = Enemy(8, 30); bridgeEnemies!.TrackSpawn(healed);
        var hit = bridgeEnemies.BeforeDamage(healed.Damage);
        Check(hit != null, "configured damage -> heal plan did not open the damage window");
        healed.Damage.Health = 40; bridgeEnemies.AfterDamage(healed.Damage, hit); Frame();
        Check(healed.Damage.Health == 45 && healed.Damage.Sends == 1 && records.Count == 1, "configured bridge heal plan must commit exactly +5 HP once");
        Frame();
        Check(healed.Damage.Sends == 1 && healed.Damage.Health == 45, "bridge heal must not retry on a later frame");
        Check(liveKernel.StartupState == RuntimeStartupState.Ready && GameRuntimeBridge.CanExecute, "bridge readiness/phase gate not connected");
        Check(!badSub.IsActive && liveSub.IsActive && liveKernel.LifecycleFaultCount == 1, "bad observer stopped healthy gameplay");
        Check(liveStates.Count(e => e.Kind == RuntimeLifecycleKind.StartupChanged && e.Current.StartupState == RuntimeStartupState.Ready) == 1, "ready notification repeated");
        Reject(() => liveKernel.RegisterModule(ProbeModule()), "late module registration allowed by bridge");
        int observerLogs = Plugin.PluginLog.Messages.Count(m => m.StartsWith("warning:Forge lifecycle observer removed:", StringComparison.Ordinal));
        Frame(); Frame();
        Check(Plugin.PluginLog.Messages.Count(m => m.StartsWith("warning:Forge lifecycle observer removed:", StringComparison.Ordinal)) == observerLogs && observerLogs == 1, "observer error repeats every frame");
        Exception? threadError = null;
        var thread = new System.Threading.Thread(() => { try { _ = GameRuntimeBridge.CanExecute; } catch (Exception e) { threadError = e; } });
        thread.Start(); thread.Join();
        Check(threadError is RuntimeContractException contract && contract.Code == "wrong-thread", "phase gate accessed native state from wrong thread");
        GameRuntimeBridge.Suspend("test restore", true);
        State(eGameStateName.Generating); Spawn(actor); State(eGameStateName.InLevel); Frame(); Die(actor); Frame();
        Check(records.Count == 1, "checkpoint generation cannot bypass restore suspension");
        State(eGameStateName.Lobby); State(eGameStateName.Generating); Spawn(actor);
        State(eGameStateName.InLevel); Frame(); Die(actor); Frame();
        Check(records.Count == 2, "explicit fresh expedition can execute after restore rejection");
        SNet.MasterManagement.IsMigrating = true; Frame(); SNet.MasterManagement.IsMigrating = false;
        State(eGameStateName.Generating); Spawn(actor); State(eGameStateName.InLevel); Frame(); Die(actor); Frame();
        Check(records.Count == 2, "migration cannot silently resume on generation state");
        State(eGameStateName.Lobby); State(eGameStateName.Generating); Spawn(actor);
        State(eGameStateName.InLevel); Frame();
        onRecord = () => State(eGameStateName.Lobby);
        int beforeTeardown = records.Count; Die(actor); long epoch = GameRuntimeBridge.Kernel!.WorldEpoch; Frame(); onRecord = null;
        Check(records.Count == beforeTeardown + 1 && GameRuntimeBridge.Kernel.WorldEpoch > epoch && GameRuntimeBridge.Kernel.QueuedEvents == 0,
            "native teardown during dispatch must flush at return without reentrant core mutation");
        Check(!GameRuntimeBridge.CanExecute, "lobby must close action phase gate");
        State(eGameStateName.Generating); Spawn(actor);
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
        State(eGameStateName.InLevel);
        Check(!GameRuntimeBridge.CanExecute, "duplicate InLevel notification reset authority baseline");
        Frame();
        Check(!GameRuntimeBridge.CanExecute, "unsupported authority transition resumed gameplay");
    }
    finally { GameRuntimeBridge.Stop(); bridgeEnemies?.Dispose(); SNet.IsMaster = true; SNet.MasterManagement.IsMigrating = false; Directory.Delete(isolated, true); }
}
if (args.Length >= 2 && args[0] == "--fixtures")
{
    string root = Path.GetFullPath(args[1]);
    var cases = RuntimeJson.Parse(File.ReadAllText(Path.Combine(root, "cases.json")));
    var live = Kernel(); var liveModule = new EnemyModule(live, () => true, messages.Add);
    // The website plans are compiled against the runtime's own manifest, and every invalid plan is a one-field edit of
    // a valid one. Pins older than this registry would make each invalid plan "fail" on binding-lock instead of its own
    // defect, so a stale fixture set is blocked as a whole rather than run.
    var registry = RuntimeJson.Parse(live.ExportManifest()).GetProperty("registry");
    string Registered(string list, string id) => registry.GetProperty(list).EnumerateArray()
        .Where(r => r.GetProperty("id").GetString() == id).Select(r => r.GetProperty("version").GetString()!).DefaultIfEmpty("unregistered").First();
    var stale = cases.GetProperty("plans").EnumerateArray()
        .SelectMany(row => RuntimeJson.Parse(File.ReadAllText(Path.Combine(root, row.GetProperty("plan").GetString()!))).GetProperty("bindings").EnumerateArray())
        .SelectMany(pin => new[] {
            (Id: pin.GetProperty("capabilityId").GetString()!, Pinned: pin.GetProperty("capabilityVersion").GetString()!),
            (Id: pin.GetProperty("providerId").GetString()!, Pinned: pin.GetProperty("providerVersion").GetString()!) }
            .Select((x, index) => (x.Id, x.Pinned, Current: Registered(index == 0 ? "capabilities" : "providers", x.Id))))
        .Where(x => x.Pinned != x.Current).Distinct().Select(x => $"{x.Id} {x.Pinned} vs registry {x.Current}").ToArray();
    if (stale.Length > 0)
    {
        string reason = "J-002/J-003: website fixture pins predate the registry (" + string.Join("; ", stale) + ")";
        blocked.Add(("fixtures.valid-plan-dispatch", reason));
        blocked.Add(("fixtures.invalid-plan-rejections", reason));
    }
    else
    {
        // One kernel and one module for both plans: the module's own damage observation is what publishes the event
        // the heal plan consumes, so the pair has to share a registry to prove the end-to-end contract.
        var liveEnemy = Enemy();
        liveModule.TrackSpawn(liveEnemy);
        // The website plans themselves are load-validated by the Framework suite; this mode needs one dispatching
        // graph, so it wires the damage trigger into the heal action from the same registry the fixture pins name.
        live.LoadPlan(DamageHealPlan(live));
        Check(live.LoadedPlans == 1, "the local damage->heal plan loads");
        // The host is only ready to execute once the registration phase closes, exactly as the plugin's first tick
        // does it; without this the observation path stays inert and no fact is ever published.
        live.StartRuntime(() => { });
        // A real loss inside the native damage window: 100 -> 40, so the observed fact is a 60 damage event and the
        // plan's literal amount caps the heal at +5.
        liveEnemy.Damage.Health = 100;
        var before = liveModule.BeforeDamage(liveEnemy.Damage);
        liveEnemy.Damage.Health = 40;
        liveModule.AfterDamage(liveEnemy.Damage, before);
        // One observed damage event must commit exactly one +5 HP heal through the actual native module handler,
        // and the next frame must not retry it.
        var tick = live.Advance(1, true);
        Check(liveEnemy.Damage.Health == 45 && liveEnemy.Damage.Sends == 1 && tick.Commands.Count == 1,
            $"real website plan must dispatch actual module handler once; health={liveEnemy.Damage.Health} sends={liveEnemy.Damage.Sends} commands={tick.Commands.Count} executed={tick.CommandsExecuted} msgs={string.Join("|", messages)}");
        Check(HealRow(tick.Commands[0].Result).GetProperty("actualAmount").GetDouble() == 5, "plan receipt actual delta");
        live.Advance(1, true);
        Check(liveEnemy.Damage.Sends == 1, "same tick reentry cannot duplicate health commit");
        foreach (var invalid in cases.GetProperty("invalidPlans").EnumerateArray())
        {
            string id = invalid.GetProperty("id").GetString()!;
            var candidate = Kernel(); _ = new EnemyModule(candidate, () => true, messages.Add);
            string bad = File.ReadAllText(Path.Combine(root, invalid.GetProperty("file").GetString()!));
            if (invalid.TryGetProperty("grantedPermissions", out _))
                blocked.Add(("fixtures.invalid-plan-" + id, "I-PACK D-009 dropped LoadPlan's permissions override that this case exercises; " +
                    "the website is removing the denied-permission negative and grantedPermissions from its generator in its next batch (after aeeb62f0)"));
            else
                Reject(() => candidate.LoadPlan(bad), "accepted invalid fixture " + id);
        }
    }
    Check(messages.Count == 0, "unexpected module diagnostics: " + string.Join(";", messages));
}
foreach (var group in blocked.GroupBy(b => b.Reason, StringComparer.Ordinal))
{
    Console.WriteLine($"BLOCKED {group.Count()} ({group.Key})");
    foreach (var row in group) Console.WriteLine("  " + row.Id);
}
Console.WriteLine($"{(blocked.Count > 0 ? "INCOMPLETE" : "PASS")} {checks} native-module boundary assertions; BLOCKED {blocked.Count}. Native API execution and multiplayer are not exercised by these doubles.");
