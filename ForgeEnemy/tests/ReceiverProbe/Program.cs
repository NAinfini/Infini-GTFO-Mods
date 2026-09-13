using System.Reflection;
using System.Text.Json;
using Enemies;
using ForgeRuntime.Framework;
using ForgeEnemy.Native;
using SNetwork;

if (args.Length != 2) { Console.Error.WriteLine("Usage: ReceiverProbe <shared runtime fixtures> <report.json>"); return 2; }
var checks = new List<ProbeCheck>();
void Check(string id, bool passed, string expected, string observed) => checks.Add(new(id, passed, expected, observed));
EnemyAgent Enemy(long pointer = 10)
{
    var actor = new EnemyAgent { GlobalID = 7, Pointer = new IntPtr(pointer) };
    actor.Damage = new Dam_EnemyDamageBase { Owner = actor, Pointer = new IntPtr(pointer + 100) };
    return actor;
}
(RuntimeKernel Kernel, EnemyModule Module, EnemyAgent Enemy, EntityReference Ref) Scene()
{
    var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"), new RuntimeLimits());
    kernel.BeginWorld(1); kernel.RegisterModule(CombatContracts.Module());
    var module = new EnemyModule(kernel, () => true, _ => { });
    var actor = Enemy(); return (kernel, module, actor, module.TrackSpawn(actor));
}
CommandResult Heal(EnemyModule module, EntityReference target, double amount = 5)
{
    var origin = new RuntimeEvent("probe.damage", EnemyModule.DamageBinding, 1, 0, "probe.scope", RuntimeJson.EmptyObject);
    var context = (CommandContext)Activator.CreateInstance(typeof(CommandContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
        new object[] { origin, 0L, "probe.command", "probe.plan", "probe.resource", "1", "probe.node", RuntimeJson.From(new { amount }), RuntimeJson.From(new { target }) }, null)!;
    return (CommandResult)typeof(EnemyModule).GetMethod("Heal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, new object[] { context })!;
}
try
{
    var duplicate = Scene();
    var again = duplicate.Module.TrackSpawn(duplicate.Enemy);
    Check("E2-001.duplicate-spawn", again == duplicate.Ref, "Repeated observation of the same live spawn preserves its reference.", $"first={duplicate.Ref}; second={again}");
    var replacement = Scene(); var next = Enemy(20);
    var nextRef = replacement.Module.TrackSpawn(next);
    Check("identity.new-life", nextRef.LifeEpoch != replacement.Ref.LifeEpoch && Heal(replacement.Module, replacement.Ref).Status == "rejected",
        "A replacement gets a new life and rejects the old reference.", $"old={replacement.Ref}; new={nextRef}");
    replacement.Module.TrackDespawn(replacement.Enemy);
    Check("identity.old-pointer", Heal(replacement.Module, nextRef).Status == "succeeded", "Old distinct-pointer teardown cannot remove the replacement.", "Heal attempted on replacement.");
    // Pointer equality alone cannot establish a new life. Capture at the observed old-life boundary.
    var pooled = Scene(); var retired = pooled.Module.CaptureDespawn(pooled.Enemy);
    pooled.Module.CompleteDespawn(retired);
    var reusedPointer = Enemy(10); var pooledRef = pooled.Module.TrackSpawn(reusedPointer);
    pooled.Module.CompleteDespawn(retired);
    var pooledResult = Heal(pooled.Module, pooledRef);
    Check("E2-002.captured-life-late-despawn", retired != null && pooledRef.LifeEpoch != pooled.Ref.LifeEpoch
        && Heal(pooled.Module, pooled.Ref).Status == "rejected" && pooledResult.Status == "succeeded",
        "An old captured-life token cannot remove a respawn reusing ID and pointer.", pooledResult.Status + "/" + pooledResult.Code);
    var wrapper = Scene(); var sameNative = wrapper.Module.TrackSpawn(Enemy(10));
    Check("identity.wrapper-not-life", sameNative == wrapper.Ref,
        "A new managed wrapper without a native life boundary is not a respawn.", sameNative.ToString());
    var world = Scene(); world.Kernel.BeginWorld(2); world.Module.ClearWorld();
    Check("identity.world-reset", Heal(world.Module, world.Ref).Status == "rejected", "World reset rejects the old reference.", "Old-world Heal attempted.");
    var changed = Scene();
    changed.Enemy.Damage.Commit = value => { changed.Enemy.Damage.Health = value; changed.Module.TrackDespawn(changed.Enemy); };
    var changedResult = Heal(changed.Module, changed.Ref);
    Check("E3-001.receiver-changed-commit", changedResult.CommitState == CommitStates.Unknown,
        "An attempted native commit followed by receiver invalidation must not report commitState=none.", $"status={changedResult.Status}; commit={changedResult.CommitState}; sends={changed.Enemy.Damage.Sends}; health={changed.Enemy.Damage.Health}");
    var readback = Scene(); readback.Enemy.Damage.Commit = _ => readback.Enemy.Damage.Health = float.NaN;
    var readbackResult = Heal(readback.Module, readback.Ref);
    Check("E3-002.invalid-readback-commit", readbackResult.CommitState == CommitStates.Unknown,
        "An invalid readback after native submission leaves the committed amount unknown.", $"status={readbackResult.Status}; commit={readbackResult.CommitState}; sends={readback.Enemy.Damage.Sends}");
    var damage = Scene();
    damage.Kernel.LoadPlan(File.ReadAllText(Path.Combine(args[0], "native-heal.plan.json")), new[] { "gtfo.enemy.health.read", "gtfo.enemy.health.write" });
    var observation = damage.Module.BeforeDamage(damage.Enemy.Damage);
    if (observation == null) throw new InvalidOperationException("Fixture did not subscribe to damage observation.");
    damage.Enemy.Damage.Health = 40;
    damage.Module.AfterDamage(damage.Enemy.Damage, observation);
    damage.Module.AfterDamage(damage.Enemy.Damage, observation);
    var tick = damage.Kernel.Advance(1, true);
    Check("E3-003.duplicate-damage-observation", tick.Commands.Count == 1 && damage.Enemy.Damage.Sends == 1 && damage.Enemy.Damage.Health == 45,
        "Re-delivering one captured damage observation must not duplicate the +5 HP action.", $"commands={tick.Commands.Count}; sends={damage.Enemy.Damage.Sends}; health={damage.Enemy.Damage.Health}");
    var full = Scene(); full.Enemy.Damage.Health = 100; var fullResult = Heal(full.Module, full.Ref);
    Check("health.full", fullResult.Status == "succeeded" && fullResult.Facts.Count == 0 && full.Enemy.Damage.Sends == 0,
        "Full health generates no packet or health-change fact.", $"facts={fullResult.Facts.Count}; sends={full.Enemy.Damage.Sends}");
    var dead = Scene(); dead.Enemy.Alive = false; dead.Enemy.Damage.Health = 0;
    var deadResult = Heal(dead.Module, dead.Ref);
    Check("health.no-revive", deadResult.Code == "gtfo.enemy.not_alive" && dead.Enemy.Damage.Sends == 0,
        "Healing cannot implicitly revive.", deadResult.Code);
    var missing = Scene(); missing.Enemy.Damage.IsSetup = false;
    var missingResult = Heal(missing.Module, missing.Ref);
    Check("health.missing-receiver", missingResult.Code == "gtfo.enemy.missing_health_receiver" && missing.Enemy.Damage.Sends == 0,
        "Uninitialized receiver is rejected without a packet.", missingResult.Code);
    var quantum = Scene(); SFloat16.Preview = (_, _) => 49.999f;
    var quantumResult = Heal(quantum.Module, quantum.Ref, EnemyModule.MinimumAmount);
    Check("health.quantization", quantumResult.Status == "succeeded" && quantumResult.Facts.Count == 0 && quantum.Enemy.Damage.Sends == 0 && quantum.Enemy.Damage.Health == 50,
        "Sub-quantum positive healing cannot reduce health or publish a fact.", $"health={quantum.Enemy.Damage.Health}; sends={quantum.Enemy.Damage.Sends}");
    SFloat16.Preview = (value, _) => value;
    var client = Scene(); SNet.IsMaster = false;
    var clientResult = Heal(client.Module, client.Ref);
    Check("health.host-authority", clientResult.Code == "gtfo.enemy.authority_or_phase" && client.Enemy.Damage.Sends == 0,
        "A client cannot submit native healing.", clientResult.Code);
    SNet.IsMaster = true;
    var throwsBefore = Scene(); throwsBefore.Enemy.Damage.Commit = _ => throw new InvalidOperationException("send failed");
    var throwsBeforeResult = Heal(throwsBefore.Module, throwsBefore.Ref);
    Check("commit.throw-before-readback", throwsBeforeResult.CommitState == CommitStates.Unknown && throwsBefore.Enemy.Damage.Sends == 1
        && throwsBeforeResult.Facts.Count == 0 && !throwsBeforeResult.Outputs.TryGetProperty("actualAmount", out _),
        "Throwing native submissions are unknown, attempted once, with no fabricated actual amount.", throwsBeforeResult.Code);
    var throwsAfter = Scene(); throwsAfter.Enemy.Damage.Commit = value => { throwsAfter.Enemy.Damage.Health = value; throw new IOException("after commit"); };
    var throwsAfterResult = Heal(throwsAfter.Module, throwsAfter.Ref);
    Check("commit.throw-after-write", throwsAfterResult.CommitState == CommitStates.Unknown && throwsAfter.Enemy.Damage.Sends == 1
        && throwsAfter.Enemy.Damage.Health == 55 && throwsAfterResult.Facts.Count == 0,
        "A write followed by an exception must not retry or claim no commit.", throwsAfterResult.Code);
    var maxChanged = Scene(); maxChanged.Enemy.Damage.Commit = value => { maxChanged.Enemy.Damage.Health = value; maxChanged.Enemy.Damage.HealthMax = 200; };
    var maxResult = Heal(maxChanged.Module, maxChanged.Ref);
    Check("commit.maximum-changed", maxResult.CommitState == CommitStates.Unknown && maxResult.Facts.Count == 0,
        "A changed native quantization range invalidates the readback contract.", maxResult.Code);
    var swapped = Scene(); var oldDamage = swapped.Enemy.Damage;
    oldDamage.Commit = value => { oldDamage.Health = value; swapped.Enemy.Damage = new Dam_EnemyDamageBase { Owner = swapped.Enemy, Pointer = new IntPtr(777) }; };
    var swapResult = Heal(swapped.Module, swapped.Ref);
    Check("commit.component-replaced", swapResult.CommitState == CommitStates.Unknown && oldDamage.Sends == 1 && swapResult.Facts.Count == 0,
        "A replacement damage component cannot supply readback for the old commit.", swapResult.Code);
    var lostHost = Scene(); lostHost.Enemy.Damage.Commit = value => { lostHost.Enemy.Damage.Health = value; SNet.IsMaster = false; };
    var lostHostResult = Heal(lostHost.Module, lostHost.Ref); SNet.IsMaster = true;
    Check("commit.authority-lost", lostHostResult.CommitState == CommitStates.Unknown && lostHost.Enemy.Damage.Sends == 1,
        "Authority loss during a native callback cannot claim a verified commit.", lostHostResult.Code);
    var foreign = Scene(); var foreignToken = foreign.Module.CaptureDespawn(foreign.Enemy);
    var other = Scene(); other.Module.CompleteDespawn(foreignToken);
    Check("identity.foreign-despawn-token", Heal(other.Module, other.Ref).Status == "succeeded",
        "Tokens cannot cross module/kernel ownership even with matching numerical references.", "Other receiver remains current.");
    var sameWrapper = Scene(); var oldToken = sameWrapper.Module.CaptureDespawn(sameWrapper.Enemy);
    sameWrapper.Module.CompleteDespawn(oldToken); var freshLife = sameWrapper.Module.TrackSpawn(sameWrapper.Enemy);
    sameWrapper.Module.CompleteDespawn(oldToken); sameWrapper.Module.CompleteDespawn(oldToken);
    Check("identity.same-wrapper-respawn", freshLife.LifeEpoch != sameWrapper.Ref.LifeEpoch && Heal(sameWrapper.Module, freshLife).Status == "succeeded",
        "Observed despawn/respawn changes life even if both native and managed objects are reused.", freshLife.ToString());
    var epochToken = sameWrapper.Module.CaptureDespawn(sameWrapper.Enemy);
    sameWrapper.Kernel.BeginWorld(2); sameWrapper.Module.ClearWorld(); var worldLife = sameWrapper.Module.TrackSpawn(sameWrapper.Enemy);
    sameWrapper.Module.CompleteDespawn(epochToken);
    Check("identity.old-world-token", worldLife.WorldEpoch == 2 && Heal(sameWrapper.Module, worldLife).Status == "succeeded",
        "Old-world teardown cannot remove a current-world instance.", worldLife.ToString());
    var pointerChanged = Scene(); pointerChanged.Enemy.Pointer = new IntPtr(888);
    Check("identity.pointer-mutated", Heal(pointerChanged.Module, pointerChanged.Ref).Status == "rejected",
        "Stored native pointer changes invalidate old references.", "Old reference rejected.");
    var idChanged = Scene(); idChanged.Enemy.GlobalID = 8;
    Check("identity.id-mutated", Heal(idChanged.Module, idChanged.Ref).Status == "rejected",
        "A mutated GlobalID cannot be resolved through its former reference.", "Old ID rejected.");
    var unobserved = Scene();
    Check("damage.no-subscribers", unobserved.Module.BeforeDamage(unobserved.Enemy.Damage) == null,
        "No subscription means no damage observation allocation.", "No observation returned.");
    string safetyPlan = File.ReadAllText(Path.Combine(args[0], "native-heal.plan.json"));
    string[] safetyGrants = { "gtfo.enemy.health.read", "gtfo.enemy.health.write" };
    var independent = Scene(); independent.Kernel.LoadPlan(safetyPlan, safetyGrants);
    var firstHit = independent.Module.BeforeDamage(independent.Enemy.Damage);
    independent.Enemy.Damage.Health = 40; independent.Module.AfterDamage(independent.Enemy.Damage, firstHit);
    var secondHit = independent.Module.BeforeDamage(independent.Enemy.Damage);
    independent.Enemy.Damage.Health = 30; independent.Module.AfterDamage(independent.Enemy.Damage, secondHit);
    independent.Module.AfterDamage(independent.Enemy.Damage, firstHit);
    var independentTick = independent.Kernel.Advance(1, true);
    Check("damage.two-hits-same-tick", independentTick.Commands.Count == 2 && independent.Enemy.Damage.Sends == 2 && independent.Enemy.Damage.Health == 40,
        "Two distinct same-tick damage windows each execute once; a replay adds nothing.", $"commands={independentTick.Commands.Count}; health={independent.Enemy.Damage.Health}");
    var zeroHit = Scene(); zeroHit.Kernel.LoadPlan(safetyPlan, safetyGrants);
    var zeroToken = zeroHit.Module.BeforeDamage(zeroHit.Enemy.Damage);
    zeroHit.Module.AfterDamage(zeroHit.Enemy.Damage, zeroToken);
    zeroHit.Enemy.Damage.Health = 40; zeroHit.Module.AfterDamage(zeroHit.Enemy.Damage, zeroToken);
    Check("damage.zero-window-consumed", zeroHit.Kernel.Advance(1, true).Commands.Count == 0 && zeroHit.Enemy.Damage.Sends == 0,
        "A zero-loss window cannot later acquire unrelated damage by replay.", "No follow-up action.");
    var rejectedHit = Scene(); rejectedHit.Kernel.LoadPlan(safetyPlan, safetyGrants);
    var rejectedToken = rejectedHit.Module.BeforeDamage(rejectedHit.Enemy.Damage); rejectedHit.Enemy.Damage.Health = 40;
    SNet.IsMaster = false; rejectedHit.Module.AfterDamage(rejectedHit.Enemy.Damage, rejectedToken); SNet.IsMaster = true;
    rejectedHit.Module.AfterDamage(rejectedHit.Enemy.Damage, rejectedToken);
    Check("damage.rejected-window-consumed", rejectedHit.Kernel.Advance(1, true).Commands.Count == 0 && rejectedHit.Enemy.Damage.Sends == 0,
        "Rejected callbacks cannot be retried when authority returns.", "No replayed damage fact.");
    var pipeline = Scene(); pipeline.Kernel.LoadPlan(safetyPlan, safetyGrants);
    var pipelineDamage = pipeline.Module.BeforeDamage(pipeline.Enemy.Damage); pipeline.Enemy.Damage.Health = 40;
    pipeline.Module.AfterDamage(pipeline.Enemy.Damage, pipelineDamage);
    pipeline.Enemy.Damage.Commit = value => { pipeline.Enemy.Damage.Health = value; throw new InvalidOperationException("native callback"); };
    var pipelineTick = pipeline.Kernel.Advance(1, true); pipeline.Kernel.Advance(2, true);
    Check("commit.kernel-unknown-no-retry", pipelineTick.Commands.Count == 1 && pipelineTick.Commands[0].Result.CommitState == CommitStates.Unknown
        && pipelineTick.Commands[0].Result.Code == "gtfo.enemy.native_commit_exception" && pipeline.Enemy.Damage.Sends == 1
        && pipeline.Enemy.Damage.Health == 45 && pipeline.Kernel.QueuedEvents == 0,
        "The real kernel preserves unknown and never retries or publishes an unverified health fact.", $"commands={pipelineTick.Commands.Count}; sends={pipeline.Enemy.Damage.Sends}");
    var issuer = Scene(); issuer.Kernel.LoadPlan(safetyPlan, safetyGrants);
    var impostor = Scene(); impostor.Kernel.LoadPlan(safetyPlan, safetyGrants);
    var issuerToken = issuer.Module.BeforeDamage(issuer.Enemy.Damage); issuer.Enemy.Damage.Health = 40; impostor.Enemy.Damage.Health = 40;
    impostor.Module.AfterDamage(impostor.Enemy.Damage, issuerToken);
    issuer.Module.AfterDamage(issuer.Enemy.Damage, issuerToken);
    Check("damage.token-owner", impostor.Kernel.Advance(1, true).Commands.Count == 0 && issuer.Kernel.Advance(1, true).Commands.Count == 1,
        "A foreign module cannot publish or consume another receiver's observation.", "Only issuer dispatched.");
    var componentHit = Scene(); componentHit.Kernel.LoadPlan(safetyPlan, safetyGrants);
    var formerComponent = componentHit.Enemy.Damage; var componentToken = componentHit.Module.BeforeDamage(formerComponent);
    formerComponent.Health = 40; componentHit.Enemy.Damage = new Dam_EnemyDamageBase { Owner = componentHit.Enemy, Pointer = new IntPtr(999) };
    componentHit.Module.AfterDamage(formerComponent, componentToken);
    Check("damage.replaced-component", componentHit.Kernel.Advance(1, true).Commands.Count == 0,
        "A retired damage component cannot publish facts for its replacement.", "No stale-component fact.");
    var prepare = Scene(); SFloat16.Preview = (_, _) => throw new InvalidOperationException("quantizer");
    var prepareResult = Heal(prepare.Module, prepare.Ref); SFloat16.Preview = (value, _) => value;
    Check("commit.preparation-failure", prepareResult.CommitState == CommitStates.None && prepareResult.Code == "gtfo.enemy.quantization_failed"
        && prepare.Enemy.Damage.Sends == 0, "Failures before submission are known uncommitted.", prepareResult.Code);
    var wrongOwner = Scene(); wrongOwner.Enemy.Damage.Owner = Enemy(20);
    var wrongOwnerResult = Heal(wrongOwner.Module, wrongOwner.Ref);
    Check("health.owner-mismatch", wrongOwnerResult.Code == "gtfo.enemy.health_receiver_owner_mismatch" && wrongOwner.Enemy.Damage.Sends == 0,
        "A mismatched receiver owner is rejected before native submission.", wrongOwnerResult.Code);
    var changedOwner = Scene(); changedOwner.Enemy.Damage.Commit = value => { changedOwner.Enemy.Damage.Health = value; changedOwner.Enemy.Damage.Owner = Enemy(20); };
    var ownerResult = Heal(changedOwner.Module, changedOwner.Ref);
    Check("commit.owner-changed", ownerResult.CommitState == CommitStates.Unknown && ownerResult.Facts.Count == 0,
        "A receiver owner changed during native submission cannot verify the old target.", ownerResult.Code);
    var autoWorld = Scene(); autoWorld.Kernel.BeginWorld(2);
    var autoRef = autoWorld.Module.TrackSpawn(autoWorld.Enemy);
    Check("lifecycle.world-observer", autoRef.WorldEpoch == 2 && autoRef.LifeEpoch != autoWorld.Ref.LifeEpoch
        && Heal(autoWorld.Module, autoWorld.Ref).Status == "rejected",
        "World changes invalidate domain identities without the host calling ClearWorld.", autoRef.ToString());
    var disposed = Scene(); disposed.Module.Dispose(); disposed.Module.Dispose();
    Check("lifecycle.dispose", !disposed.Module.IsRegistered && Heal(disposed.Module, disposed.Ref).Status == "rejected",
        "Disposal removes the provider and is idempotent.", "Disposed twice, no handler execution.");
    var stopped = Scene(); stopped.Kernel.StartRuntime(() => { }); stopped.Kernel.StopRuntime();
    var stoppedResult = Heal(stopped.Module, stopped.Ref); stopped.Module.Dispose();
    Check("lifecycle.stop", stoppedResult.Status == "rejected" && stopped.Enemy.Damage.Sends == 0 && !stopped.Module.IsRegistered,
        "Stopped kernels reject gameplay even when a test host delegate returns true; cleanup still works.", stoppedResult.Code);
    var failedStartup = Scene();
    try { failedStartup.Kernel.StartRuntime(() => throw new IOException("fixture startup")); } catch (IOException) { }
    Check("lifecycle.failed-startup", Heal(failedStartup.Module, failedStartup.Ref).Status == "rejected"
        && failedStartup.Enemy.Damage.Sends == 0, "A failed startup cannot retain an executable enemy receiver.", "No native submission.");
    var thread = Scene();
    var threadCode = System.Threading.Tasks.Task.Run(() =>
    {
        try { thread.Module.TrackSpawn(thread.Enemy); return "accepted"; }
        catch (RuntimeContractException error) { return error.Code; }
    }).GetAwaiter().GetResult();
    Check("lifecycle.thread", threadCode == "wrong-thread" && thread.Module.TrackSpawn(thread.Enemy) == thread.Ref,
        "Foreign threads cannot mutate identity or consume native work.", threadCode);
    var registry = Scene(); var manifest = registry.Kernel.ExportManifest(); bool conflict = false;
    try { _ = new EnemyModule(registry.Kernel, () => true, _ => { }); }
    catch (RuntimeContractException error) { conflict = error.Code == "provider-conflict"; }
    Check("lifecycle.single-provider", conflict && registry.Kernel.ExportManifest() == manifest && Heal(registry.Module, registry.Ref).Status == "succeeded",
        "A second Enemy provider is rejected without damaging the registered provider.", "Registry unchanged.");
}
catch (Exception error) { Check("probe.error", false, "Every probe executes without harness errors.", error.ToString()); }
finally { SNet.IsMaster = true; SFloat16.Preview = (value, _) => value; }
int failed = checks.Count(c => !c.Passed);
using var sdkStream = File.OpenRead(typeof(RuntimeKernel).Assembly.Location);
using var sdkHash = System.Security.Cryptography.SHA256.Create();
var report = new { receiverSourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "EnemyModule.source.cs")))),
    scenarioRevision = "captured-life-despawn-v2", schemaVersion = 2, verification = "production-source-and-explicit-compiled-sdk-with-test-doubles", gameExecuted = false,
    frameworkAssemblySha256 = Convert.ToHexString(sdkHash.ComputeHash(sdkStream)),
    utc = DateTimeOffset.UtcNow, passed = checks.Count - failed, failed, checks };
string reportPath = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Observed);
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {checks.Count - failed}/{checks.Count} receiver probes; native game execution and multiplayer are NOT exercised.");
return failed == 0 ? 0 : 1;
internal sealed record ProbeCheck(string Id, bool Passed, string Expected, string Observed);
