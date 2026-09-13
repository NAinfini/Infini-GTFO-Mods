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
RuntimeKernel Kernel()
{
    var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"), new RuntimeLimits());
    kernel.BeginWorld(1); kernel.RegisterModule(CombatContracts.Module()); return kernel;
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
        RuntimeJson.From(new { amount }), RuntimeJson.From(new { target }));
    // This is the production module's registered handler, invoked directly for receiver boundary cases.
    return (CommandResult)typeof(EnemyModule).GetMethod("Heal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, new object[] { context })!;
}

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
Check(result.Outputs.GetProperty("actualAmount").GetDouble() == 5 && result.Facts.Count == 1, "healing reports committed delta");
Check(result.Facts[0].BindingId == EnemyModule.HealthChangedBinding && result.Facts[0].Outputs.GetProperty("delta").GetDouble() == 5, "typed health fact");
enemy.Damage.Health = 99;
result = Heal(module, reference);
Check(enemy.Damage.Health == 100 && result.Outputs.GetProperty("actualAmount").GetDouble() == 1, "heal clamps at native max");
int sends = enemy.Damage.Sends;
result = Heal(module, reference);
Check(result.Status == "succeeded" && result.Facts.Count == 0 && enemy.Damage.Sends == sends, "full health is zero effective, no packet or fact");
enemy.Alive = false; enemy.Damage.Health = 0;
Check(Heal(module, reference).Code == "gtfo.enemy.not_alive" && enemy.Damage.Health == 0, "heal cannot revive");
enemy.Alive = true; enemy.Damage.Health = 50; enemy.Damage.IsSetup = false;
Check(Heal(module, reference).Code == "gtfo.enemy.missing_health_receiver", "uninitialized receiver");
enemy.Damage.IsSetup = true;
Check(Heal(module, new EntityReference("gtfo.player:7", 1, reference.LifeEpoch)).Status == "rejected", "unsupported player is never retargeted");
Check(Heal(module, reference with { LifeEpoch = reference.LifeEpoch + 1 }).Status == "rejected", "stale life");
Check(Heal(module, reference with { WorldEpoch = 2 }).Status == "rejected", "stale world");
Check(Heal(module, reference, 0).Code == "gtfo.enemy.amount_out_of_range", "zero amount rejected");
Check(Heal(module, reference, -1).Code == "gtfo.enemy.amount_out_of_range", "negative damage is not healing");
Check(Heal(module, reference, EnemyModule.MaximumAmount + 1).Code == "gtfo.enemy.amount_out_of_range", "amount budget");
allowed = false; Check(Heal(module, reference).Code == "gtfo.enemy.authority_or_phase", "phase gated at commit"); allowed = true;
SNet.IsMaster = false; Check(Heal(module, reference).Code == "gtfo.enemy.authority_or_phase", "client cannot commit"); SNet.IsMaster = true;
enemy.Damage.Health = float.NaN;
Check(Heal(module, reference).Status == "rejected", "invalid health rejected"); enemy.Damage.Health = 50;
enemy.Damage.HealthMax = float.PositiveInfinity;
Check(Heal(module, reference).Code == "gtfo.enemy.invalid_health_state", "invalid maximum rejected"); enemy.Damage.HealthMax = 100;
enemy.Damage.Commit = _ => { };
result = Heal(module, reference, EnemyModule.MinimumAmount);
Check(result.Status == "succeeded" && result.Facts.Count == 0 && result.Outputs.GetProperty("actualAmount").GetDouble() == 0, "quantization never creates fake healing");
int beforeQuantizedSkip = enemy.Damage.Sends;
SFloat16.Preview = (_, _) => 49.999f;
result = Heal(module, reference, EnemyModule.MinimumAmount);
Check(result.Status == "succeeded" && enemy.Damage.Sends == beforeQuantizedSkip, "sub-quantum heal never lowers health");
SFloat16.Preview = (value, _) => value;
enemy.Damage.Commit = _ => enemy.Damage.Health = 49;
Check(Heal(module, reference).Code == "gtfo.enemy.unexpected_health_readback", "unexpected native result is failure");
enemy.Damage.Health = 50; enemy.Damage.Commit = _ => module.TrackDespawn(enemy);
Check(Heal(module, reference).Code == "gtfo.enemy.receiver_changed_during_commit", "removed receiver cannot report success");
enemy.Damage.Commit = null;
var replacement = Enemy(pointer: 20); var nextLife = module.TrackSpawn(replacement);
Check(nextLife.LifeEpoch != reference.LifeEpoch && Heal(module, reference).Status == "rejected", "reused global id invalidates old life");
module.TrackDespawn(enemy);
Check(Heal(module, nextLife).Status == "succeeded", "old pointer teardown must not remove replacement");
kernel.BeginWorld(2); module.ClearWorld();
Check(Heal(module, nextLife).Status == "rejected", "world reset invalidates recipients");

string testRoot = Path.Combine(Path.GetTempPath(), "forge-game-bindings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    string file = Path.Combine(testRoot, "plan.json"); File.WriteAllText(file, "{}");
    Check(FrameworkFiles.ReadPlan(testRoot, "plan.json") == "{}", "relative offline plan read");
    Reject(() => FrameworkFiles.ReadPlan(testRoot, "../escape.json"), "traversal allowed");
    Reject(() => FrameworkFiles.ReadPlan(testRoot, file), "absolute path allowed");
    File.WriteAllBytes(file, new byte[FrameworkFiles.MaximumPlanBytes + 1]);
    Reject(() => FrameworkFiles.ReadPlan(testRoot, "plan.json"), "oversized plan allowed");
    File.WriteAllText(file, "{}" + new string(' ', 2 * 1024 * 1024));
    Check(FrameworkFiles.ReadPlan(testRoot, "plan.json").Length > 1024 * 1024, "website-valid plan ceiling diverges");
}
finally { Directory.Delete(testRoot, true); }

if (args.Length >= 2 && args[0] == "--export-manifest")
{
    var exportKernel = Kernel(); _ = new EnemyModule(exportKernel, () => true, messages.Add);
    File.WriteAllText(args[1], exportKernel.ExportManifest());
    Console.WriteLine("ACTUAL MODULE MANIFEST " + Path.GetFullPath(args[1]));
}
if (args.Length >= 4 && args[0] == "--native") checks += NativeEvidence.Verify(args[1], args[2], args[3]);
if (args.Length >= 3 && args[0] == "--bridge")
{
    BepInEx.Paths.GameRootPath = Path.GetFullPath(args[1]);
    string plan = File.ReadAllText(Path.Combine(args[2], "native-heal.plan.json"));
    string isolated = Path.Combine(Path.GetTempPath(), "forge-bridge-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(isolated); BepInEx.Paths.BepInExRootPath = isolated;
    void State(eGameStateName state) { GameStateManager.CurrentStateName = state; GameRuntimeBridge.StateChanged(state); }
    void Frame() => GameRuntimeBridge.Guard(GameRuntimeBridge.FixedTick);
    EnemyModule? bridgeEnemies = null;
    void InitializeBridge(string planPath, string permissions)
    {
        bridgeEnemies?.Dispose();
        GameRuntimeBridge.Initialize(planPath, permissions);
        bridgeEnemies = new EnemyModule(GameRuntimeBridge.Kernel!, () => GameRuntimeBridge.CanExecute, message => Plugin.PluginLog.LogWarning(message));
    }
    void Damage(EnemyAgent actor)
    {
        var observed = bridgeEnemies!.BeforeDamage(actor.Damage);
        actor.Damage.Health -= 10;
        bridgeEnemies.AfterDamage(actor.Damage, observed);
    }
    RuntimeModule ProbeModule() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new {
        providers = new[] { new { id = "test.bridge.lifecycle", kind = "native", version = "1.0.0", dependencies = Array.Empty<object>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
    try
    {
        InitializeBridge("missing.json", "gtfo.enemy.health.read,gtfo.enemy.health.write");
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
        string manifestPath = Path.Combine(isolated, "ForgeRuntime", "capabilities.json");
        var writeTime = File.GetLastWriteTimeUtc(manifestPath);
        File.WriteAllText(Path.Combine(isolated, "missing.json"), plan);
        for (int i = 0; i < 100; i++) Frame();
        Check(GameRuntimeBridge.Kernel!.LoadedPlans == 0 && Plugin.PluginLog.Messages.Count == logs && File.GetLastWriteTimeUtc(manifestPath) == writeTime,
            "actual FixedTick retries failed plan or manifest IO");
        GameRuntimeBridge.Stop(); GameRuntimeBridge.Stop();
        Check(failedKernel.StartupState == RuntimeStartupState.Stopped && !failedSub.IsActive, "failed bridge stop retained observers");
        failedOwner.Dispose();
        InitializeBridge("missing.json", "gtfo.enemy.health.read,gtfo.enemy.health.write");
        var liveKernel = GameRuntimeBridge.Kernel!;
        var liveOwner = liveKernel.RegisterModule(ProbeModule());
        var liveStates = new List<RuntimeLifecycleEvent>();
        var liveSub = liveOwner.ObserveLifecycle(liveStates.Add);
        var badSub = liveOwner.ObserveLifecycle(e => { if (e.Kind == RuntimeLifecycleKind.StartupChanged) throw new InvalidOperationException("observer test failure"); }, false);
        State(eGameStateName.Generating); var actor = Enemy(); bridgeEnemies!.TrackSpawn(actor);
        State(eGameStateName.InLevel); Frame(); Damage(actor); Frame();
        Check(actor.Damage.Health == 45 && actor.Damage.Sends == 1, "real bridge loads configured plan and dispatches");
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
        State(eGameStateName.Generating); bridgeEnemies.TrackSpawn(actor); State(eGameStateName.InLevel); Frame(); Damage(actor); Frame();
        Check(actor.Damage.Sends == 1, "checkpoint generation cannot bypass restore suspension");
        State(eGameStateName.Lobby); State(eGameStateName.Generating); bridgeEnemies.TrackSpawn(actor);
        State(eGameStateName.InLevel); Frame(); Damage(actor); Frame();
        Check(actor.Damage.Sends == 2, "explicit fresh expedition can execute after restore rejection");
        SNet.MasterManagement.IsMigrating = true; Frame(); SNet.MasterManagement.IsMigrating = false;
        State(eGameStateName.Generating); bridgeEnemies.TrackSpawn(actor); State(eGameStateName.InLevel); Frame(); Damage(actor); Frame();
        Check(actor.Damage.Sends == 2, "migration cannot silently resume on generation state");
        State(eGameStateName.Lobby); State(eGameStateName.Generating); bridgeEnemies.TrackSpawn(actor);
        State(eGameStateName.InLevel); Frame();
        actor.Damage.Health = 80;
        actor.Damage.Commit = value => { actor.Damage.Health = value; State(eGameStateName.Lobby); };
        Damage(actor); long epoch = GameRuntimeBridge.Kernel!.WorldEpoch; Frame();
        Check(GameRuntimeBridge.Kernel.WorldEpoch > epoch && GameRuntimeBridge.Kernel.QueuedEvents == 0,
            "native teardown during dispatch must flush at return without reentrant core mutation");
        Check(!GameRuntimeBridge.CanExecute, "lobby must close action phase gate");
        State(eGameStateName.Generating); bridgeEnemies!.TrackSpawn(actor);
        State(eGameStateName.InLevel); Frame();
        bool gateClosedInsideCommit = false;
        actor.Damage.Commit = value => { actor.Damage.Health = value; GameRuntimeBridge.Stop(); gateClosedInsideCommit = !GameRuntimeBridge.CanExecute; };
        int beforeStop = actor.Damage.Sends; Damage(actor); Frame();
        Check(gateClosedInsideCommit && actor.Damage.Sends == beforeStop + 1, "teardown did not block further commits immediately");
        Check(GameRuntimeBridge.Kernel == null && liveKernel.StartupState == RuntimeStartupState.Stopped, "deferred bridge stop did not stop retained kernel");
        Check(liveKernel.LoadedPlans == 0 && liveKernel.QueuedEvents == 0 && !liveSub.IsActive, "deferred stop leaked tasks/plans/observers");
        Reject(() => liveKernel.Advance(999, true), "retained kernel executed after host stop");
        liveOwner.Dispose(); liveSub.Dispose();
        SNet.IsMaster = false;
        InitializeBridge("missing.json", "gtfo.enemy.health.read,gtfo.enemy.health.write");
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
    string plan = File.ReadAllText(Path.Combine(root, cases.GetProperty("validPlan").GetString()!));
    var live = Kernel(); var liveModule = new EnemyModule(live, () => true, messages.Add);
    var liveEnemy = Enemy(); liveModule.TrackSpawn(liveEnemy);
    string[] grants = { "gtfo.enemy.health.read", "gtfo.enemy.health.write" };
    live.LoadPlan(plan, grants);
    var before = liveModule.BeforeDamage(liveEnemy.Damage);
    liveEnemy.Damage.Health = 40;
    liveModule.AfterDamage(liveEnemy.Damage, before);
    var tick = live.Advance(1, true);
    Check(liveEnemy.Damage.Health == 45 && liveEnemy.Damage.Sends == 1 && tick.Commands.Count == 1, "real website plan must dispatch actual module handler once");
    Check(tick.Commands[0].Result.Outputs.GetProperty("actualAmount").GetDouble() == 5, "plan receipt actual delta");
    live.Advance(1, true);
    Check(liveEnemy.Damage.Sends == 1, "same tick reentry cannot duplicate health commit");
    foreach (var invalid in cases.GetProperty("invalidPlans").EnumerateArray())
    {
        var candidate = Kernel(); _ = new EnemyModule(candidate, () => true, messages.Add);
        string bad = File.ReadAllText(Path.Combine(root, invalid.GetProperty("file").GetString()!));
        string[] permissions = invalid.TryGetProperty("grantedPermissions", out var value) ? value.EnumerateArray().Select(x => x.GetString()!).ToArray() : grants;
        Reject(() => candidate.LoadPlan(bad, permissions), "accepted invalid fixture " + invalid.GetProperty("id"));
    }
    Check(messages.Count == 0, "unexpected module diagnostics: " + string.Join(";", messages));
}
Console.WriteLine($"PASS {checks} native-module boundary assertions. Native API execution and multiplayer are not exercised by these doubles.");
