using System.Reflection;
using System.Text.Json;
using Enemies;
using ForgeRuntime.Framework;
using ForgeEnemy.Native;
using SNetwork;

if (args.Length != 2) { Console.Error.WriteLine("Usage: ReceiverProbe <report.json> <website directory>"); return 2; }
// The authoring catalog lives in the website repository, so that directory is a required argument: a run that
// cannot read it would skip exactly the contract comparison this suite exists for.
string websiteRoot = Path.GetFullPath(args[1]);
var checks = new List<ProbeCheck>();
// Each scenario owns its kernel and doubles. A throwing scenario settles only its own ids; later scenarios still run.
void Scenario(string[] ids, Func<(bool Passed, string Expected, string Observed)[]> body)
{
    try
    {
        var results = body();
        if (results.Length != ids.Length) throw new InvalidOperationException($"Scenario returned {results.Length} results for {ids.Length} ids.");
        for (int i = 0; i < ids.Length; i++) checks.Add(new(ids[i], results[i].Passed, results[i].Expected, results[i].Observed));
    }
    catch (Exception error) { checks.AddRange(ids.Select(id => new ProbeCheck(id, false, "Scenario executes without harness errors.", error.ToString()))); }
    finally { SNet.IsMaster = true; SFloat16.Preview = (value, _) => value; }
}
void Case(string id, Func<(bool Passed, string Expected, string Observed)> body) => Scenario(new[] { id }, () => new[] { body() });
EnemyAgent Enemy(long pointer = 10, ushort id = 7)
{
    var actor = new EnemyAgent { GlobalID = id, Pointer = new IntPtr(pointer) };
    actor.Damage = new Dam_EnemyDamageBase { Owner = actor, Pointer = new IntPtr(pointer + 100) };
    return actor;
}
(RuntimeKernel Kernel, EnemyModule Module, EnemyAgent Enemy, EntityReference Ref) Scene(Func<EnemyAgent, uint?>? enemyType = null)
{
    var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"), new RuntimeLimits());
    kernel.BeginWorld(1); kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
    kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
    LocalPlan.OwnMounts(kernel);
    // The probe reads no native enemy data: the `enemy-type` matcher is registered only when a case declares the
    // type its instances answer with, exactly as the session hands over the module's own read.
    var module = new EnemyModule(kernel, RuntimeLogLevel.Off, () => true, _ => { }, null, enemyType);
    var actor = Enemy(); return (kernel, module, actor, module.TrackSpawn(actor));
}
// One mounted plan over the damage fact: the enemy's type is what the case's own reader answers for that agent.
(RuntimeKernel Kernel, EnemyModule Module, EnemyAgent Enemy, EntityReference Ref, List<CommandContext> Records) MountScene(
    string planId, object[] attachments, Func<EnemyAgent, uint?> enemyType)
{
    var scene = Scene(enemyType); var records = new List<CommandContext>();
    scene.Kernel.RegisterModule(LocalPlan.Recorder(records.Add), RuntimeLogLevel.Off);
    LocalPlan.Load(scene.Kernel, LocalPlan.Build(scene.Kernel, planId, EnemyModule.DamageBinding, LocalPlan.RecordBinding,
        new[] { ("target", "target") }, Array.Empty<(string, object)>(), RuntimeJson.EmptyObject, attachments));
    return (scene.Kernel, scene.Module, scene.Enemy, scene.Ref, records);
}
void Damage(EnemyModule module, EnemyAgent enemy)
{
    var observation = module.BeforeDamage(enemy.Damage)
        ?? throw new InvalidOperationException("Mounted plan did not subscribe damage observation.");
    enemy.Damage.Health = 40; module.AfterDamage(enemy.Damage, observation);
}
// A second provider for the mount's subject question: its own trigger carries one entity that is not an enemy.
const string OtherTriggerBinding = "test.other.binding.ping";
// The authoring catalog row the canonical combat contract is compared against, in this suite and in the website.
const string DamageCapabilityId = "forge.action.combat.damage";
const string OtherDomainRegistry = """
{"providers":[{"id":"test.other","kind":"extension","version":"1.0.0","dependencies":[]}],
"capabilities":[{"id":"test.other.trigger.ping","owner":"test.other","kind":"trigger","label":"QA ping","version":"1.0.0",
"parameters":{},"graph":{"domains":["enemy"],"execution":"host","inputs":[],
"outputs":[{"id":"next","type":"execution"},{"id":"subject","type":"entity"}],"parameters":[]}}],
"bindings":[{"id":"test.other.binding.ping","capabilityId":"test.other.trigger.ping","providerId":"test.other",
"handler":"test.ping","role":"observe","status":"implemented","dependencies":[],"requires":[]}]}
""";
// damage_applied -> record(target): observes exactly what the receiver publishes, with no heal in the loop.
(RuntimeKernel Kernel, EnemyModule Module, EnemyAgent Enemy, EntityReference Ref, List<CommandContext> Records) HitScene()
{
    var scene = Scene(); var records = new List<CommandContext>();
    scene.Kernel.RegisterModule(LocalPlan.Recorder(records.Add), RuntimeLogLevel.Off);
    LocalPlan.Load(scene.Kernel, LocalPlan.Build(scene.Kernel, "test.receiver.damage", EnemyModule.DamageBinding, LocalPlan.RecordBinding, ("target", "target")));
    return (scene.Kernel, scene.Module, scene.Enemy, scene.Ref, records);
}
// health_changed -> record(target, value, delta): the only subscription, so the damage window is opened for health changes alone.
(RuntimeKernel Kernel, EnemyModule Module, EnemyAgent Enemy, EntityReference Ref, List<CommandContext> Records) HealthScene()
{
    var scene = Scene(); var records = new List<CommandContext>();
    scene.Kernel.RegisterModule(LocalPlan.Recorder(records.Add), RuntimeLogLevel.Off);
    LocalPlan.Load(scene.Kernel, LocalPlan.Build(scene.Kernel, "test.receiver.health", EnemyModule.HealthChangedBinding, LocalPlan.RecordBinding,
        ("target", "target"), ("value", "value"), ("delta", "delta")));
    return (scene.Kernel, scene.Module, scene.Enemy, scene.Ref, records);
}
CommandResult Heal(EnemyModule module, EntityReference target, double amount = 5)
{
    var origin = new RuntimeEvent("probe.damage", EnemyModule.DamageBinding, 1, 0, "probe.scope", RuntimeJson.EmptyObject);
    // Source carries no targeting restriction; the probe reuses the target itself (self-source is valid).
    var context = (CommandContext)Activator.CreateInstance(typeof(CommandContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
        new object[] { origin, 0L, "probe.command", "probe.plan", "probe.resource", "1", "probe.node",
            RuntimeJson.From(new { overheal_policy = "clamp" }), RuntimeJson.From(new { targets = new[] { target }, source = target, amount }), true, (Func<EntityReference, object?>)(reference => throw new InvalidOperationException("Direct receiver probes do not model cross-provider native instance lookups: " + reference.Id)) }, null)!;
    return (CommandResult)typeof(EnemyModule).GetMethod("Heal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, new object[] { context })!;
}
JsonElement HealRow(CommandResult result) => result.Outputs.GetProperty("results").EnumerateArray().First();

Case("E2-001.duplicate-spawn", () =>
{
    var duplicate = Scene(); var again = duplicate.Module.TrackSpawn(duplicate.Enemy);
    return (again == duplicate.Ref, "Repeated observation of the same live spawn preserves its reference.", $"first={duplicate.Ref}; second={again}");
});
Scenario(new[] { "identity.new-life", "identity.old-pointer" }, () =>
{
    var replacement = Scene(); var next = Enemy(20);
    var nextRef = replacement.Module.TrackSpawn(next);
    var newLife = (nextRef.LifeEpoch != replacement.Ref.LifeEpoch && Heal(replacement.Module, replacement.Ref).Status == "rejected",
        "A replacement gets a new life and rejects the old reference.", $"old={replacement.Ref}; new={nextRef}");
    replacement.Module.TrackDespawn(replacement.Enemy);
    return new[] { newLife, (Heal(replacement.Module, nextRef).Status == "succeeded", "Old distinct-pointer teardown cannot remove the replacement.", "Heal attempted on replacement.") };
});
Case("E2-002.captured-life-late-despawn", () =>
{
    // Pointer equality alone cannot establish a new life. Capture at the observed old-life boundary.
    var pooled = Scene(); var retired = pooled.Module.CaptureDespawn(pooled.Enemy);
    pooled.Module.CompleteDespawn(retired);
    var reusedPointer = Enemy(10); var pooledRef = pooled.Module.TrackSpawn(reusedPointer);
    pooled.Module.CompleteDespawn(retired);
    var pooledResult = Heal(pooled.Module, pooledRef);
    return (retired != null && pooledRef.LifeEpoch != pooled.Ref.LifeEpoch
        && Heal(pooled.Module, pooled.Ref).Status == "rejected" && pooledResult.Status == "succeeded",
        "An old captured-life token cannot remove a respawn reusing ID and pointer.", pooledResult.Status + "/" + pooledResult.Code);
});
Case("identity.wrapper-not-life", () =>
{
    var wrapper = Scene(); var sameNative = wrapper.Module.TrackSpawn(Enemy(10));
    return (sameNative == wrapper.Ref, "A new managed wrapper without a native life boundary is not a respawn.", sameNative.ToString());
});
Case("identity.world-reset", () =>
{
    var world = Scene(); world.Kernel.BeginWorld(2); world.Module.ClearWorld();
    return (Heal(world.Module, world.Ref).Status == "rejected", "World reset rejects the old reference.", "Old-world Heal attempted.");
});
Case("E3-001.receiver-changed-commit", () =>
{
    var changed = Scene();
    changed.Enemy.Damage.Commit = value => { changed.Enemy.Damage.Health = value; changed.Module.TrackDespawn(changed.Enemy); };
    var changedResult = Heal(changed.Module, changed.Ref);
    return (changedResult.CommitState == CommitStates.Unknown,
        "An attempted native commit followed by receiver invalidation must not report commitState=none.", $"status={changedResult.Status}; commit={changedResult.CommitState}; sends={changed.Enemy.Damage.Sends}; health={changed.Enemy.Damage.Health}");
});
Case("E3-002.invalid-readback-commit", () =>
{
    var readback = Scene(); readback.Enemy.Damage.Commit = _ => readback.Enemy.Damage.Health = float.NaN;
    var readbackResult = Heal(readback.Module, readback.Ref);
    return (readbackResult.CommitState == CommitStates.Unknown,
        "An invalid readback after native submission leaves the committed amount unknown.", $"status={readbackResult.Status}; commit={readbackResult.CommitState}; sends={readback.Enemy.Damage.Sends}");
});
Case("E3-003.duplicate-damage-observation", () =>
{
    var damage = HitScene();
    var observation = damage.Module.BeforeDamage(damage.Enemy.Damage);
    if (observation == null) throw new InvalidOperationException("Local plan did not subscribe to damage observation.");
    damage.Enemy.Damage.Health = 40;
    damage.Module.AfterDamage(damage.Enemy.Damage, observation);
    damage.Module.AfterDamage(damage.Enemy.Damage, observation);
    var tick = damage.Kernel.Advance(1, true);
    return (tick.Commands.Count == 1 && damage.Records.Count == 1,
        "Re-delivering one captured damage observation must not publish a second damage fact.", $"commands={tick.Commands.Count}; records={damage.Records.Count}");
});
Case("health.full", () =>
{
    var full = Scene(); full.Enemy.Damage.Health = 100; var fullResult = Heal(full.Module, full.Ref);
    return (fullResult.Status == "succeeded" && fullResult.Facts.Count == 0 && full.Enemy.Damage.Sends == 0,
        "Full health generates no packet or health-change fact.", $"facts={fullResult.Facts.Count}; sends={full.Enemy.Damage.Sends}");
});
Case("health.no-revive", () =>
{
    var dead = Scene(); dead.Enemy.Alive = false; dead.Enemy.Damage.Health = 0;
    var deadResult = Heal(dead.Module, dead.Ref);
    return (deadResult.Code == "not-alive" && dead.Enemy.Damage.Sends == 0, "Healing cannot implicitly revive.", deadResult.Code);
});
Case("health.missing-receiver", () =>
{
    var missing = Scene(); missing.Enemy.Damage.IsSetup = false;
    var missingResult = Heal(missing.Module, missing.Ref);
    return (missingResult.Code == "missing-health-receiver" && missing.Enemy.Damage.Sends == 0,
        "Uninitialized receiver is rejected without a packet.", missingResult.Code);
});
Case("health.quantization", () =>
{
    var quantum = Scene(); SFloat16.Preview = (_, _) => 49.999f;
    var quantumResult = Heal(quantum.Module, quantum.Ref, EnemyModule.MinimumAmount);
    return (quantumResult.Status == "succeeded" && quantumResult.Facts.Count == 0 && quantum.Enemy.Damage.Sends == 0 && quantum.Enemy.Damage.Health == 50,
        "Sub-quantum positive healing cannot reduce health or publish a fact.", $"health={quantum.Enemy.Damage.Health}; sends={quantum.Enemy.Damage.Sends}");
});
Case("health.host-authority", () =>
{
    var client = Scene(); SNet.IsMaster = false;
    var clientResult = Heal(client.Module, client.Ref);
    return (clientResult.Code == "authority-or-phase" && client.Enemy.Damage.Sends == 0, "A client cannot submit native healing.", clientResult.Code);
});
Case("commit.throw-before-readback", () =>
{
    var throwsBefore = Scene(); throwsBefore.Enemy.Damage.Commit = _ => throw new InvalidOperationException("send failed");
    var throwsBeforeResult = Heal(throwsBefore.Module, throwsBefore.Ref);
    // Every heal row carries actualAmount; an unknown commit must leave it null rather than invent a delta.
    var throwsBeforeRow = HealRow(throwsBeforeResult);
    return (throwsBeforeResult.CommitState == CommitStates.Unknown && throwsBefore.Enemy.Damage.Sends == 1 && throwsBeforeResult.Facts.Count == 0
        && throwsBeforeRow.GetProperty("actualAmount").ValueKind == JsonValueKind.Null,
        "Throwing native submissions are unknown, attempted once, with no fabricated actual amount.",
        $"commit={throwsBeforeResult.CommitState}; code={throwsBeforeResult.Code}; sends={throwsBefore.Enemy.Damage.Sends}; row={throwsBeforeRow}");
});
Case("commit.throw-after-write", () =>
{
    var throwsAfter = Scene(); throwsAfter.Enemy.Damage.Commit = value => { throwsAfter.Enemy.Damage.Health = value; throw new IOException("after commit"); };
    var throwsAfterResult = Heal(throwsAfter.Module, throwsAfter.Ref);
    return (throwsAfterResult.CommitState == CommitStates.Unknown && throwsAfter.Enemy.Damage.Sends == 1
        && throwsAfter.Enemy.Damage.Health == 55 && throwsAfterResult.Facts.Count == 0,
        "A write followed by an exception must not retry or claim no commit.", throwsAfterResult.Code);
});
Case("commit.maximum-changed", () =>
{
    var maxChanged = Scene(); maxChanged.Enemy.Damage.Commit = value => { maxChanged.Enemy.Damage.Health = value; maxChanged.Enemy.Damage.HealthMax = 200; };
    var maxResult = Heal(maxChanged.Module, maxChanged.Ref);
    return (maxResult.CommitState == CommitStates.Unknown && maxResult.Facts.Count == 0,
        "A changed native quantization range invalidates the readback contract.", maxResult.Code);
});
Case("commit.component-replaced", () =>
{
    var swapped = Scene(); var oldDamage = swapped.Enemy.Damage;
    oldDamage.Commit = value => { oldDamage.Health = value; swapped.Enemy.Damage = new Dam_EnemyDamageBase { Owner = swapped.Enemy, Pointer = new IntPtr(777) }; };
    var swapResult = Heal(swapped.Module, swapped.Ref);
    return (swapResult.CommitState == CommitStates.Unknown && oldDamage.Sends == 1 && swapResult.Facts.Count == 0,
        "A replacement damage component cannot supply readback for the old commit.", swapResult.Code);
});
Case("commit.authority-lost", () =>
{
    var lostHost = Scene(); lostHost.Enemy.Damage.Commit = value => { lostHost.Enemy.Damage.Health = value; SNet.IsMaster = false; };
    var lostHostResult = Heal(lostHost.Module, lostHost.Ref); SNet.IsMaster = true;
    return (lostHostResult.CommitState == CommitStates.Unknown && lostHost.Enemy.Damage.Sends == 1,
        "Authority loss during a native callback cannot claim a verified commit.", lostHostResult.Code);
});
Case("identity.foreign-despawn-token", () =>
{
    var foreign = Scene(); var foreignToken = foreign.Module.CaptureDespawn(foreign.Enemy);
    var other = Scene(); other.Module.CompleteDespawn(foreignToken);
    return (Heal(other.Module, other.Ref).Status == "succeeded",
        "Tokens cannot cross module/kernel ownership even with matching numerical references.", "Other receiver remains current.");
});
Scenario(new[] { "identity.same-wrapper-respawn", "identity.old-world-token" }, () =>
{
    var sameWrapper = Scene(); var oldToken = sameWrapper.Module.CaptureDespawn(sameWrapper.Enemy);
    sameWrapper.Module.CompleteDespawn(oldToken); var freshLife = sameWrapper.Module.TrackSpawn(sameWrapper.Enemy);
    sameWrapper.Module.CompleteDespawn(oldToken); sameWrapper.Module.CompleteDespawn(oldToken);
    var respawn = (freshLife.LifeEpoch != sameWrapper.Ref.LifeEpoch && Heal(sameWrapper.Module, freshLife).Status == "succeeded",
        "Observed despawn/respawn changes life even if both native and managed objects are reused.", freshLife.ToString());
    var epochToken = sameWrapper.Module.CaptureDespawn(sameWrapper.Enemy);
    sameWrapper.Kernel.BeginWorld(2); sameWrapper.Module.ClearWorld(); var worldLife = sameWrapper.Module.TrackSpawn(sameWrapper.Enemy);
    sameWrapper.Module.CompleteDespawn(epochToken);
    return new[] { respawn, (worldLife.WorldEpoch == 2 && Heal(sameWrapper.Module, worldLife).Status == "succeeded",
        "Old-world teardown cannot remove a current-world instance.", worldLife.ToString()) };
});
Case("identity.pointer-mutated", () =>
{
    var pointerChanged = Scene(); pointerChanged.Enemy.Pointer = new IntPtr(888);
    return (Heal(pointerChanged.Module, pointerChanged.Ref).Status == "rejected", "Stored native pointer changes invalidate old references.", "Old reference rejected.");
});
Case("identity.id-mutated", () =>
{
    var idChanged = Scene(); idChanged.Enemy.GlobalID = 8;
    return (Heal(idChanged.Module, idChanged.Ref).Status == "rejected", "A mutated GlobalID cannot be resolved through its former reference.", "Old ID rejected.");
});
Case("identity.same-resource-instances", () =>
{
    // Several live instances of one enemy resource share data, never identity.
    var herd = Scene(); var sibling = new EnemyAgent { GlobalID = 8, Pointer = new IntPtr(30) };
    sibling.Damage = new Dam_EnemyDamageBase { Owner = sibling, Pointer = new IntPtr(130) };
    var siblingRef = herd.Module.TrackSpawn(sibling); herd.Module.TrackDespawn(herd.Enemy);
    var herdOld = Heal(herd.Module, herd.Ref); var herdSibling = Heal(herd.Module, siblingRef);
    return (siblingRef.Id != herd.Ref.Id && siblingRef.LifeEpoch != herd.Ref.LifeEpoch
        && herdOld.Status == "rejected" && herdSibling.Status == "succeeded" && sibling.Damage.Sends == 1 && herd.Enemy.Damage.Sends == 0,
        "Despawning one instance of a shared resource leaves every other instance's life intact.", $"old={herdOld.Status}; sibling={herdSibling.Status}");
});
Case("identity.pooled-pointer-new-id", () =>
{
    // A pooled native object reassigned to another GlobalID without an observed despawn, then the old ID reused elsewhere.
    var renamed = Scene(); renamed.Enemy.GlobalID = 9; var movedRef = renamed.Module.TrackSpawn(renamed.Enemy);
    var reclaimer = new EnemyAgent { GlobalID = 7, Pointer = new IntPtr(40) };
    reclaimer.Damage = new Dam_EnemyDamageBase { Owner = reclaimer, Pointer = new IntPtr(140) };
    var reclaimedRef = renamed.Module.TrackSpawn(reclaimer);
    var renamedOld = Heal(renamed.Module, renamed.Ref); var renamedMoved = Heal(renamed.Module, movedRef); var renamedReclaimed = Heal(renamed.Module, reclaimedRef);
    return (movedRef.Id == "gtfo.enemy:9" && reclaimedRef.Id == renamed.Ref.Id
        && new[] { renamed.Ref.LifeEpoch, movedRef.LifeEpoch, reclaimedRef.LifeEpoch }.Distinct().Count() == 3
        && renamedOld.Status == "rejected" && renamedMoved.Status == "succeeded" && renamedReclaimed.Status == "succeeded",
        "A reused pointer under a new ID and a later reuse of the old ID each get their own life; the retired reference stays rejected.",
        $"old={renamed.Ref}:{renamedOld.Status}; moved={movedRef}:{renamedMoved.Status}; reclaimed={reclaimedRef}:{renamedReclaimed.Status}");
});
Case("identity.world-change-single-life", () =>
{
    // World change followed by a replayed spawn callback for the same native object.
    var rebirth = Scene(); rebirth.Kernel.BeginWorld(2);
    var bornInWorld = rebirth.Module.TrackSpawn(rebirth.Enemy); var replayedInWorld = rebirth.Module.TrackSpawn(rebirth.Enemy);
    var rebirthOld = Heal(rebirth.Module, rebirth.Ref); var rebirthNew = Heal(rebirth.Module, bornInWorld);
    return (bornInWorld == replayedInWorld && bornInWorld.WorldEpoch == 2 && bornInWorld.LifeEpoch != rebirth.Ref.LifeEpoch
        && rebirthOld.Status == "rejected" && rebirthNew.Status == "succeeded" && rebirth.Enemy.Damage.Sends == 1,
        "After a world change a replayed spawn keeps one new life; the previous world's reference never resolves.", $"first={bornInWorld}; replay={replayedInWorld}");
});
Case("damage.no-subscribers", () =>
{
    var unobserved = Scene();
    return (unobserved.Module.BeforeDamage(unobserved.Enemy.Damage) == null, "No subscription means no damage observation allocation.", "No observation returned.");
});
Case("damage.two-hits-same-tick", () =>
{
    var independent = HitScene();
    var firstHit = independent.Module.BeforeDamage(independent.Enemy.Damage);
    independent.Enemy.Damage.Health = 40; independent.Module.AfterDamage(independent.Enemy.Damage, firstHit);
    var secondHit = independent.Module.BeforeDamage(independent.Enemy.Damage);
    independent.Enemy.Damage.Health = 30; independent.Module.AfterDamage(independent.Enemy.Damage, secondHit);
    independent.Module.AfterDamage(independent.Enemy.Damage, firstHit);
    var independentTick = independent.Kernel.Advance(1, true);
    return (independentTick.Commands.Count == 2 && independent.Records.Count == 2,
        "Two distinct same-tick damage windows each publish once; a replay adds nothing.", $"commands={independentTick.Commands.Count}; records={independent.Records.Count}");
});
Case("damage.zero-window-consumed", () =>
{
    var zeroHit = HitScene();
    var zeroToken = zeroHit.Module.BeforeDamage(zeroHit.Enemy.Damage);
    zeroHit.Module.AfterDamage(zeroHit.Enemy.Damage, zeroToken);
    zeroHit.Enemy.Damage.Health = 40; zeroHit.Module.AfterDamage(zeroHit.Enemy.Damage, zeroToken);
    return (zeroHit.Kernel.Advance(1, true).Commands.Count == 0 && zeroHit.Records.Count == 0,
        "A zero-loss window cannot later acquire unrelated damage by replay.", $"records={zeroHit.Records.Count}");
});
Case("damage.rejected-window-consumed", () =>
{
    var rejectedHit = HitScene();
    var rejectedToken = rejectedHit.Module.BeforeDamage(rejectedHit.Enemy.Damage); rejectedHit.Enemy.Damage.Health = 40;
    SNet.IsMaster = false; rejectedHit.Module.AfterDamage(rejectedHit.Enemy.Damage, rejectedToken); SNet.IsMaster = true;
    rejectedHit.Module.AfterDamage(rejectedHit.Enemy.Damage, rejectedToken);
    return (rejectedHit.Kernel.Advance(1, true).Commands.Count == 0 && rejectedHit.Records.Count == 0,
        "Rejected callbacks cannot be retried when authority returns.", $"records={rejectedHit.Records.Count}");
});
Case("identity.late-damage-after-respawn", () =>
{
    // A damage callback that finishes after its enemy was destroyed and the same GlobalID respawned.
    var lateHit = HitScene();
    var lateToken = lateHit.Module.BeforeDamage(lateHit.Enemy.Damage);
    lateHit.Module.TrackDespawn(lateHit.Enemy); var lateRespawn = lateHit.Module.TrackSpawn(lateHit.Enemy);
    lateHit.Enemy.Damage.Health = 40; lateHit.Module.AfterDamage(lateHit.Enemy.Damage, lateToken);
    var lateQueued = lateHit.Kernel.QueuedEvents; var lateTick = lateHit.Kernel.Advance(1, true);
    return (lateRespawn.LifeEpoch != lateHit.Ref.LifeEpoch && lateQueued == 0 && lateTick.Commands.Count == 0 && lateHit.Records.Count == 0,
        "A late damage callback from a destroyed life cannot publish a fact for the respawned enemy with the same GlobalID.",
        $"queued={lateQueued}; commands={lateTick.Commands.Count}; records={lateHit.Records.Count}");
});
Case("commit.kernel-unknown-no-retry", () =>
{
    // damage_applied -> heal(targets <- target, source <- target, amount 5, clamp) dispatched by the real kernel into this receiver.
    var unknown = Scene(); var damage = unknown.Enemy.Damage;
    LocalPlan.Load(unknown.Kernel, LocalPlan.Heal(unknown.Kernel, "test.receiver.damage-heal", EnemyModule.DamageBinding, "target"));
    var observation = unknown.Module.BeforeDamage(damage);
    if (observation == null) throw new InvalidOperationException("Local heal plan did not subscribe to damage observation.");
    damage.Health = 40; unknown.Module.AfterDamage(damage, observation);
    damage.Commit = value => { damage.Health = value; throw new InvalidOperationException("after kernel commit"); };
    var tick = unknown.Kernel.Advance(1, true); var retry = unknown.Kernel.Advance(2, true);
    var result = tick.Commands.Count == 1 ? tick.Commands[0].Result : null;
    return (result != null && result.Status == "failed" && result.CommitState == CommitStates.Unknown && result.Code == "native-commit-exception"
        && damage.Sends == 1 && damage.Health == 45 && retry.Commands.Count == 0 && unknown.Kernel.QueuedEvents == 0,
        "A kernel-dispatched heal whose native commit throws after writing is unknown, written once (+5 HP) and never retried.",
        $"commands={tick.Commands.Count}; retry={retry.Commands.Count}; status={result?.Status}; commit={result?.CommitState}; code={result?.Code}; sends={damage.Sends}; health={damage.Health}; queued={unknown.Kernel.QueuedEvents}");
});
Case("health.damage-window-change", () =>
{
    var loss = HealthScene(); var damage = loss.Enemy.Damage;
    var observation = loss.Module.BeforeDamage(damage);
    if (observation == null) throw new InvalidOperationException("A health_changed subscription alone did not open the damage window.");
    damage.Health = 40; loss.Module.AfterDamage(damage, observation); loss.Module.AfterDamage(damage, observation);
    var tick = loss.Kernel.Advance(1, true);
    var inputs = loss.Records.Count == 1 ? loss.Records[0].Inputs : default;
    return (tick.Commands.Count == 1 && loss.Records.Count == 1 && RuntimeJson.Entity(inputs.GetProperty("target")) == loss.Ref
        && inputs.GetProperty("value").GetDouble() == 40 && inputs.GetProperty("delta").GetDouble() == -10,
        "One native damage window losing 10 HP publishes one health change {target, value=40, delta=-10}; a replay adds nothing.",
        $"commands={tick.Commands.Count}; records={loss.Records.Count}; inputs={(loss.Records.Count == 1 ? inputs.ToString() : "-")}");
});
Case("health.damage-window-rise-not-inferred", () =>
{
    var rise = HealthScene(); var damage = rise.Enemy.Damage;
    var observation = rise.Module.BeforeDamage(damage);
    if (observation == null) throw new InvalidOperationException("A health_changed subscription alone did not open the damage window.");
    damage.Health = 60; rise.Module.AfterDamage(damage, observation);
    var unchanged = rise.Module.BeforeDamage(damage); rise.Module.AfterDamage(damage, unchanged);
    return (rise.Kernel.Advance(1, true).Commands.Count == 0 && rise.Records.Count == 0 && rise.Kernel.QueuedEvents == 0,
        "A rise or no change inside the native damage window is not published as a health change.", $"records={rise.Records.Count}");
});
Case("health.damage-and-change-both-once", () =>
{
    var both = HealthScene(); var damage = both.Enemy.Damage;
    LocalPlan.Load(both.Kernel, LocalPlan.Build(both.Kernel, "test.receiver.damage", EnemyModule.DamageBinding, LocalPlan.RecordBinding, ("target", "target")));
    var observation = both.Module.BeforeDamage(damage); damage.Health = 45; both.Module.AfterDamage(damage, observation);
    var tick = both.Kernel.Advance(1, true);
    var plans = both.Records.Select(r => r.PlanId).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    return (tick.Commands.Count == 2 && plans.SequenceEqual(new[] { "test.receiver.damage", "test.receiver.health" }),
        "One observed loss publishes exactly one damage_applied and one health_changed fact.", string.Join(",", plans));
});
Case("health.damage-window-old-life", () =>
{
    var late = HealthScene(); var damage = late.Enemy.Damage;
    var observation = late.Module.BeforeDamage(damage);
    late.Module.TrackDespawn(late.Enemy); late.Module.TrackSpawn(late.Enemy);
    damage.Health = 40; late.Module.AfterDamage(damage, observation);
    return (late.Kernel.QueuedEvents == 0 && late.Kernel.Advance(1, true).Commands.Count == 0 && late.Records.Count == 0,
        "A health loss observed for a destroyed life is never published for the respawned enemy.", $"records={late.Records.Count}");
});
Case("damage.token-owner", () =>
{
    var issuer = HitScene(); var impostor = HitScene();
    var issuerToken = issuer.Module.BeforeDamage(issuer.Enemy.Damage); issuer.Enemy.Damage.Health = 40; impostor.Enemy.Damage.Health = 40;
    impostor.Module.AfterDamage(impostor.Enemy.Damage, issuerToken);
    issuer.Module.AfterDamage(issuer.Enemy.Damage, issuerToken);
    int impostorCommands = impostor.Kernel.Advance(1, true).Commands.Count, issuerCommands = issuer.Kernel.Advance(1, true).Commands.Count;
    return (impostorCommands == 0 && issuerCommands == 1,
        "A foreign module cannot publish or consume another receiver's observation.", $"impostor={impostorCommands}; issuer={issuerCommands}");
});
Case("damage.replaced-component", () =>
{
    var componentHit = HitScene();
    var formerComponent = componentHit.Enemy.Damage; var componentToken = componentHit.Module.BeforeDamage(formerComponent);
    formerComponent.Health = 40; componentHit.Enemy.Damage = new Dam_EnemyDamageBase { Owner = componentHit.Enemy, Pointer = new IntPtr(999) };
    componentHit.Module.AfterDamage(formerComponent, componentToken);
    return (componentHit.Kernel.Advance(1, true).Commands.Count == 0, "A retired damage component cannot publish facts for its replacement.", "No stale-component fact.");
});
Case("commit.preparation-failure", () =>
{
    var prepare = Scene(); SFloat16.Preview = (_, _) => throw new InvalidOperationException("quantizer");
    var prepareResult = Heal(prepare.Module, prepare.Ref); SFloat16.Preview = (value, _) => value;
    return (prepareResult.CommitState == CommitStates.None && prepareResult.Code == "quantization-failed"
        && prepare.Enemy.Damage.Sends == 0, "Failures before submission are known uncommitted.", prepareResult.Code);
});
Case("health.owner-mismatch", () =>
{
    var wrongOwner = Scene(); wrongOwner.Enemy.Damage.Owner = Enemy(20);
    var wrongOwnerResult = Heal(wrongOwner.Module, wrongOwner.Ref);
    return (wrongOwnerResult.Code == "health-receiver-owner-mismatch" && wrongOwner.Enemy.Damage.Sends == 0,
        "A mismatched receiver owner is rejected before native submission.", wrongOwnerResult.Code);
});
Case("commit.owner-changed", () =>
{
    var changedOwner = Scene(); changedOwner.Enemy.Damage.Commit = value => { changedOwner.Enemy.Damage.Health = value; changedOwner.Enemy.Damage.Owner = Enemy(20); };
    var ownerResult = Heal(changedOwner.Module, changedOwner.Ref);
    return (ownerResult.CommitState == CommitStates.Unknown && ownerResult.Facts.Count == 0,
        "A receiver owner changed during native submission cannot verify the old target.", ownerResult.Code);
});
Case("lifecycle.world-observer", () =>
{
    var autoWorld = Scene(); autoWorld.Kernel.BeginWorld(2);
    var autoRef = autoWorld.Module.TrackSpawn(autoWorld.Enemy);
    return (autoRef.WorldEpoch == 2 && autoRef.LifeEpoch != autoWorld.Ref.LifeEpoch && Heal(autoWorld.Module, autoWorld.Ref).Status == "rejected",
        "World changes invalidate domain identities without the host calling ClearWorld.", autoRef.ToString());
});
Case("lifecycle.dispose", () =>
{
    var disposed = Scene(); disposed.Module.Dispose(); disposed.Module.Dispose();
    return (!disposed.Module.IsRegistered && Heal(disposed.Module, disposed.Ref).Status == "rejected",
        "Disposal removes the provider and is idempotent.", "Disposed twice, no handler execution.");
});
Case("lifecycle.stop", () =>
{
    var stopped = Scene(); stopped.Kernel.StartRuntime(() => { }); stopped.Kernel.StopRuntime();
    var stoppedResult = Heal(stopped.Module, stopped.Ref); stopped.Module.Dispose();
    return (stoppedResult.Status == "rejected" && stopped.Enemy.Damage.Sends == 0 && !stopped.Module.IsRegistered,
        "Stopped kernels reject gameplay even when a test host delegate returns true; cleanup still works.", stoppedResult.Code);
});
Case("lifecycle.failed-startup", () =>
{
    var failedStartup = Scene();
    try { failedStartup.Kernel.StartRuntime(() => throw new IOException("fixture startup")); } catch (IOException) { }
    return (Heal(failedStartup.Module, failedStartup.Ref).Status == "rejected" && failedStartup.Enemy.Damage.Sends == 0,
        "A failed startup cannot retain an executable enemy receiver.", "No native submission.");
});
Case("lifecycle.thread", () =>
{
    var thread = Scene();
    var threadCode = System.Threading.Tasks.Task.Run(() =>
    {
        try { thread.Module.TrackSpawn(thread.Enemy); return "accepted"; }
        catch (RuntimeContractException error) { return error.Code; }
    }).GetAwaiter().GetResult();
    return (threadCode == "wrong-thread" && thread.Module.TrackSpawn(thread.Enemy) == thread.Ref,
        "Foreign threads cannot mutate identity or consume native work.", threadCode);
});
Case("lifecycle.single-provider", () =>
{
    var registry = Scene(); var manifest = registry.Kernel.ExportManifest(); bool conflict = false;
    try { _ = new EnemyModule(registry.Kernel, RuntimeLogLevel.Off, () => true, _ => { }); }
    catch (RuntimeContractException error) { conflict = error.Code == "provider-conflict"; }
    return (conflict && registry.Kernel.ExportManifest() == manifest && Heal(registry.Module, registry.Ref).Status == "succeeded",
        "A second Enemy provider is rejected without damaging the registered provider.", "Registry unchanged.");
});
// The `enemy-type` mount: one official enemy block id, matched against this provider's own instances.
Case("mount.enemy-type-loads-and-dispatches", () =>
{
    var mounted = MountScene("test.receiver.mount.match", LocalPlan.EnemyTypeMount(7), _ => 7u);
    Damage(mounted.Module, mounted.Enemy);
    var tick = mounted.Kernel.Advance(1, true);
    return (tick.Commands.Count == 1 && mounted.Records.Count == 1
        && RuntimeJson.Entity(mounted.Records[0].Inputs.GetProperty("target")) == mounted.Ref,
        "A plan mounted on an enemy type loads and dispatches for that type's own instance.",
        $"commands={tick.Commands.Count}; records={mounted.Records.Count}");
});
Case("mount.enemy-type-other-type-ignored", () =>
{
    var unmounted = MountScene("test.receiver.mount.other", LocalPlan.EnemyTypeMount(8), _ => 7u);
    Damage(unmounted.Module, unmounted.Enemy);
    var tick = unmounted.Kernel.Advance(1, true);
    return (tick.Commands.Count == 0 && unmounted.Records.Count == 0,
        "A mount on another enemy type never dispatches for this instance.",
        $"commands={tick.Commands.Count}; records={unmounted.Records.Count}");
});
Case("mount.enemy-type-only-that-type", () =>
{
    var herd = MountScene("test.receiver.mount.herd", LocalPlan.EnemyTypeMount(7), enemy => enemy.GlobalID == 7 ? 7u : 8u);
    var sibling = Enemy(20, 8); var siblingRef = herd.Module.TrackSpawn(sibling);
    Damage(herd.Module, herd.Enemy); Damage(herd.Module, sibling);
    var tick = herd.Kernel.Advance(1, true);
    return (tick.Commands.Count == 1 && herd.Records.Count == 1
        && siblingRef != herd.Ref && RuntimeJson.Entity(herd.Records[0].Inputs.GetProperty("target")) == herd.Ref,
        "One mounted type dispatches for its own instance and not for a sibling of another type.",
        $"commands={tick.Commands.Count}; records={herd.Records.Count}");
});
Scenario(new[] { "mount.enemy-type-leading-zero", "mount.enemy-type-signed", "mount.enemy-type-negative",
    "mount.enemy-type-above-uint", "mount.enemy-type-fractional" }, () =>
{
    // Every reference below is loadable plan text, so a mount that fired would prove the matcher reinterpreted it.
    var spellings = new[] { "007", "+7", "-7", "4294967296", "7.0" };
    return spellings.Select((reference, index) =>
    {
        var scene = MountScene("test.receiver.mount.spelling." + index, LocalPlan.Mount("enemy-type", reference), _ => 7u);
        Damage(scene.Module, scene.Enemy);
        var tick = scene.Kernel.Advance(1, true);
        return (tick.Commands.Count == 0 && scene.Records.Count == 0,
            "A reference that is not the canonical decimal text of one id never matches.", $"{reference}: commands={tick.Commands.Count}");
    }).ToArray();
});
Scenario(new[] { "mount.enemy-type-empty-reference", "mount.enemy-type-untrimmed-reference" }, () =>
{
    // A reference the plan text cannot carry is refused before any matcher sees it: the mount list and the plan
    // reader share one spelling rule, and an empty or padded reference is not that rule's text.
    var spellings = new[] { "", " 7" };
    return spellings.Select((reference, index) =>
    {
        var scene = Scene(_ => 7u);
        scene.Kernel.RegisterModule(LocalPlan.Recorder(_ => { }), RuntimeLogLevel.Off);
        var code = "loaded";
        try
        {
            LocalPlan.Load(scene.Kernel, LocalPlan.Build(scene.Kernel, "test.receiver.mount.empty." + index, EnemyModule.DamageBinding,
                LocalPlan.RecordBinding, new[] { ("target", "target") }, Array.Empty<(string, object)>(), RuntimeJson.EmptyObject,
                LocalPlan.Mount("enemy-type", reference)));
        }
        catch (RuntimeContractException error) { code = error.Code; }
        return (code == "invalid-string", "An unspellable enemy-type reference is refused when the plan loads.", $"{reference.Length} chars: {code}");
    }).ToArray();
});
Case("mount.enemy-type-foreign-subject-ignored", () =>
{
    // The event belongs to another domain's provider and carries only its own entity: the enemy provider is
    // asked whether it claims that subject, and it owns no such instance.
    var scene = Scene(_ => 7u); var other = new EntityReference("test.other:1", 1, 1);
    var records = new List<CommandContext>();
    var domain = scene.Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, OtherDomainRegistry,
        new Dictionary<string, CommandHandler>(), new[] { new BindingSupport(OtherTriggerBinding, "implementation-only", Array.Empty<string>()) },
        new Dictionary<string, Func<EntityReference, bool>> { ["test.other"] = reference => reference == other }), RuntimeLogLevel.Off);
    scene.Kernel.RegisterModule(LocalPlan.Recorder(records.Add), RuntimeLogLevel.Off);
    LocalPlan.Load(scene.Kernel, LocalPlan.Build(scene.Kernel, "test.receiver.mount.foreign", OtherTriggerBinding,
        LocalPlan.RecordBinding, new[] { ("subject", "target") }, Array.Empty<(string, object)>(), RuntimeJson.EmptyObject,
        LocalPlan.EnemyTypeMount(7)));
    var dispatch = domain.Publish(new RuntimeEvent("probe.ping", OtherTriggerBinding, 1, 0, "probe.scope", RuntimeJson.From(new { subject = other })));
    var tick = scene.Kernel.Advance(1, true);
    return (dispatch.Status == "ignored" && dispatch.Code == "attachment-mismatch" && tick.Commands.Count == 0 && records.Count == 0,
        "Another domain's subject never matches an enemy type.",
        $"dispatch={dispatch.Status}/{dispatch.Code}; commands={tick.Commands.Count}; records={records.Count}");
});
Case("mount.enemy-type-life-retired-while-reading", () =>
{
    // The type read is native, so the life it answered for can be retired before the answer is used. The module's
    // own resolve decides, not the fact that a type was once readable.
    EnemyModule? reading = null;
    var retired = MountScene("test.receiver.mount.retired", LocalPlan.EnemyTypeMount(7),
        enemy => { reading!.TrackDespawn(enemy); return 7u; });
    reading = retired.Module;
    Damage(retired.Module, retired.Enemy);
    var tick = retired.Kernel.Advance(1, true);
    return (tick.Commands.Count == 0 && retired.Records.Count == 0,
        "A life retired while its type was read is not a match.",
        $"commands={tick.Commands.Count}; records={retired.Records.Count}");
});
Case("mount.enemy-type-unregistered-kind", () =>
{
    // A session with no enemy-type read registers no such matcher, so the same plan is refused at load instead
    // of being accepted and never dispatched.
    var scene = Scene(); scene.Kernel.RegisterModule(LocalPlan.Recorder(_ => { }), RuntimeLogLevel.Off);
    var code = "loaded";
    try
    {
        LocalPlan.Load(scene.Kernel, LocalPlan.Build(scene.Kernel, "test.receiver.mount.unregistered", EnemyModule.DamageBinding,
            LocalPlan.RecordBinding, new[] { ("target", "target") }, Array.Empty<(string, object)>(), RuntimeJson.EmptyObject,
            LocalPlan.EnemyTypeMount(7)));
    }
    catch (RuntimeContractException error) { code = error.Code; }
    return (code == "attachment-kind", "A mount kind no provider registered is refused at load.", code);
});

// --- forge.action.combat.damage: the action itself, dispatched straight into the receiver ---------------------
// The probe compiles the production source against the stand-in receiver, so a submitted hit is counted by the
// same `Attacks` field that counts a heal's `Sends`: no injection seam and no second write path exists.
(RuntimeKernel Kernel, EnemyModule Module, EnemyAgent Enemy, EntityReference Ref) ActionScene(Action<EnemyAgent>? onAttack)
{
    var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"), new RuntimeLimits());
    kernel.BeginWorld(1); kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
    kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
    var module = new EnemyModule(kernel, RuntimeLogLevel.Off, () => true, _ => { });
    var actor = Enemy();
    // The stand-in receiver answers the one native entry call: a landed hit, a hit whose rules nullify it, or a
    // throwing call, exactly as the frozen evidence describes the game's own receiver.
    if (onAttack != null) actor.Damage.OnBulletDamage = _ => onAttack(actor);
    return (kernel, module, actor, module.TrackSpawn(actor));
}
CommandResult Strike(EnemyModule module, EntityReference[] targets, double amount, string policy = "receiver_rules",
    int kind = 0, int? limb = null)
{
    var origin = new RuntimeEvent("probe.damageaction", EnemyModule.DamageActionBinding, 1, 0, "probe.scope", RuntimeJson.EmptyObject);
    // Source and instigator carry no targeting restriction; the probe reuses the first recipient for both. An
    // omitted optional port is left out of the inputs instead of being written as null.
    var inputs = new Dictionary<string, object?>
    {
        ["targets"] = targets, ["source"] = targets[0], ["instigator"] = targets[0], ["amount"] = amount, ["damage_kind"] = kind
    };
    if (limb.HasValue) inputs["limb"] = limb.Value;
    var context = (CommandContext)Activator.CreateInstance(typeof(CommandContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
        new object[] { origin, 0L, "probe.command", "probe.plan", "probe.resource", "1", "probe.node",
            RuntimeJson.From(new { mitigation_policy = policy }), RuntimeJson.From(inputs), true, (Func<EntityReference, object?>)(reference => throw new InvalidOperationException("Direct receiver probes do not model cross-provider native instance lookups: " + reference.Id)) }, null)!;
    return (CommandResult)typeof(EnemyModule).GetMethod("Damage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, new object[] { context })!;
}
JsonElement[] HitRows(CommandResult result) => result.Outputs.GetProperty("results").EnumerateArray().ToArray();
// A row's own field, or a marker that no assertion can accidentally accept when it is absent.
string Field(JsonElement row, string name) => row.TryGetProperty(name, out var value) ? value.ToString() : "<missing:" + name + ">";
Case("action.ports-match-contract", () =>
{
    var scene = ActionScene(null);
    var contract = scene.Kernel.ResolveGraphContract("forge.action.combat.damage", "1.0.0", RuntimeJson.EmptyObject);
    // The execution ports are the kernel's own frames, not handler arguments.
    var declared = contract.GetProperty("inputs").EnumerateArray()
        .Where(p => p.GetProperty("type").GetString() != "execution").Select(p => p.GetProperty("id").GetString()!)
        .Concat(contract.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("id").GetString()!)).ToArray();
    var shape = (HandlerShape)typeof(EnemyModule).GetField("DamagePorts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    var names = shape.InputPorts.Concat(shape.ParameterIds).ToArray();
    return (declared.Length > 0 && names.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(declared.OrderBy(n => n, StringComparer.Ordinal)),
        "The handler declares exactly the ports the registered contract declares.",
        $"contract=[{string.Join(",", declared)}]; shape=[{string.Join(",", names)}]");
});
Case("action.host-commits", () =>
{
    var scene = ActionScene(enemy => enemy.Damage.Health = 40);
    var result = Strike(scene.Module, new[] { scene.Ref }, 10);
    var row = HitRows(result)[0];
    return (result.Status == "succeeded" && result.CommitState == CommitStates.Confirmed && scene.Enemy.Damage.Attacks == 1
        && Field(row, "status") == "committed" && Field(row, "committed") == "confirmed"
        && Field(row, "amount") == "10" && Field(row, "target_count") == "1",
        "A landed hit commits, is submitted once, and answers one row.",
        $"status={result.Status}; commit={result.CommitState}; attacks={scene.Enemy.Damage.Attacks}; row={row}");
});
Case("action.not-host-authority", () =>
{
    // The native entry point is not host-gated for this receiver type (its local-application gate reads a field
    // Setup arms only for player bots), so the provider's own authority check is the one that refuses a client.
    var scene = ActionScene(enemy => enemy.Damage.Health = 40); SNet.IsMaster = false;
    var result = Strike(scene.Module, new[] { scene.Ref }, 10);
    return (result.Status == "rejected" && result.Code == "authority-or-phase" && scene.Enemy.Damage.Attacks == 0,
        "A client cannot submit a damage action.", $"{result.Status}/{result.Code}; attacks={scene.Enemy.Damage.Attacks}");
});
Case("action.stale-entity", () =>
{
    var scene = ActionScene(null); scene.Module.TrackDespawn(scene.Enemy);
    var result = Strike(scene.Module, new[] { scene.Ref }, 10);
    return (result.Status == "rejected" && result.Code == "stale-or-unsupported-recipient" && scene.Enemy.Damage.Attacks == 0,
        "A retired life is refused before any native call.", $"{result.Status}/{result.Code}");
});
Case("action.missing-receiver", () =>
{
    var scene = ActionScene(null); scene.Enemy.Damage.IsSetup = false;
    var result = Strike(scene.Module, new[] { scene.Ref }, 10);
    return (result.Code == "missing-health-receiver" && scene.Enemy.Damage.Attacks == 0,
        "A receiver that is not set up is refused.", result.Code);
});
Case("action.dead-target", () =>
{
    var scene = ActionScene(null); scene.Enemy.Alive = false;
    var result = Strike(scene.Module, new[] { scene.Ref }, 10);
    return (result.Code == "not-alive" && scene.Enemy.Damage.Attacks == 0, "Damage never revives or strikes a dead recipient.", result.Code);
});
Case("action.unseen-commit-is-unknown", () =>
{
    // The frozen native evidence: a hit the receiver's rules reduce to nothing and a rejected hit are
    // indistinguishable from this side of the call, so neither may be reported as a commit.
    var scene = ActionScene(null);
    var result = Strike(scene.Module, new[] { scene.Ref }, 10);
    var row = HitRows(result)[0];
    return (result.Status == "failed" && result.CommitState == CommitStates.Unknown && result.Code == "damage-unseen"
        && scene.Enemy.Damage.Attacks == 1 && Field(row, "committed") == "unknown"
        && Field(row, "status") == "unknown" && Field(row, "amount") == "10",
        "A hit that moved no health is an unknown commit, never a success.",
        $"status={result.Status}; commit={result.CommitState}; code={result.Code}; row={row}");
});
Case("action.commit-throws-unknown", () =>
{
    var scene = ActionScene(enemy => throw new InvalidOperationException("not in the query region"));
    var result = Strike(scene.Module, new[] { scene.Ref }, 10);
    return (result.Status == "failed" && result.CommitState == CommitStates.Unknown && result.Code == "native-commit-exception",
        "A throwing entry point is an unknown commit that is never retried.", $"{result.Status}/{result.Code}");
});
Case("action.multiple-recipients-ordered", () =>
{
    var scene = ActionScene(null);
    var second = Enemy(20, 8); second.Damage.OnBulletDamage = damage => damage.Health = 45;
    scene.Enemy.Damage.OnBulletDamage = damage => damage.Health = 40;
    var secondRef = scene.Module.TrackSpawn(second);
    var result = Strike(scene.Module, new[] { scene.Ref, secondRef }, 10);
    var rows = HitRows(result);
    return (result.Status == "succeeded" && rows.Length == 2
        && RuntimeJson.Entity(rows[0].GetProperty("target")) == scene.Ref && RuntimeJson.Entity(rows[1].GetProperty("target")) == secondRef
        && rows.All(r => r.GetProperty("target_count").GetInt32() == 2),
        "One row per recipient, in the plan's own order.",
        $"status={result.Status}; code={result.Code}; rows={rows.Length}; first={(rows.Length > 0 ? RuntimeJson.Entity(rows[0].GetProperty("target")) == scene.Ref : false)};"
        + $" second={(rows.Length > 1 ? RuntimeJson.Entity(rows[1].GetProperty("target")) == secondRef : false)};"
        + $" counts=[{string.Join(",", rows.Select(r => Field(r, "target_count")))}];"
        + $" codes=[{string.Join(",", rows.Select(r => Field(r, "code")))}];"
        + $" health=[{string.Join(",", rows.Select(r => Field(r, "healthBefore") + "->" + Field(r, "healthAfter")))}];"
        + $" attacks=[{scene.Enemy.Damage.Attacks},{second.Damage.Attacks}];"
        + $" targets=[{string.Join(",", rows.Select(r => r.GetProperty("target").ToString()))}]");
});
Case("action.limb-id-resolves-to-index", () =>
{
    var scene = ActionScene(enemy => enemy.Damage.Health = 40);
    var first = new Dam_EnemyDamageLimb { m_limbID = 3, m_base = scene.Enemy.Damage, Pointer = new IntPtr(300) };
    var second = new Dam_EnemyDamageLimb { m_limbID = 7, m_base = scene.Enemy.Damage, Pointer = new IntPtr(301) };
    scene.Enemy.Damage.DamageLimbs = new[] { first, second };
    var named = Strike(scene.Module, new[] { scene.Ref }, 10, limb: 7);
    var absent = Strike(scene.Module, new[] { scene.Ref }, 10, limb: 5);
    return (named.Status == "succeeded" && absent.Status == "rejected" && absent.Code == "invalid-limb",
        "A limb id no limb declares is refused; a declared one is submitted.",
        $"named={named.Status}; absent={absent.Status}/{absent.Code}");
});
Case("action.kind-index-outside-set", () =>
{
    var scene = ActionScene(null);
    var outside = Strike(scene.Module, new[] { scene.Ref }, 10, kind: 9);
    var inside = Strike(scene.Module, new[] { scene.Ref }, 10, kind: 8);
    return (outside.Status == "rejected" && outside.Code == "damage-kind-unsupported" && inside.Code != "damage-kind-unsupported",
        "An index outside the declared damage_kind set is refused.", $"{outside.Code}; {inside.Code}");
});
Case("action.unsupported-mitigation", () =>
{
    var scene = ActionScene(null);
    var ignored = Strike(scene.Module, new[] { scene.Ref }, 10, policy: "ignore_armor");
    return (ignored.Status == "rejected" && ignored.Code == "mitigation-policy-unsupported" && scene.Enemy.Damage.Attacks == 0,
        "A mitigation policy the native entry point cannot express is refused.", ignored.Code);
});
Case("action.amount-bounds", () =>
{
    var scene = ActionScene(null);
    var zero = Strike(scene.Module, new[] { scene.Ref }, 0);
    var huge = Strike(scene.Module, new[] { scene.Ref }, 1000001);
    return (zero.Code == "amount-out-of-range" && huge.Code == "amount-out-of-range" && scene.Enemy.Damage.Attacks == 0,
        "A non-positive or unbounded amount is refused before submission.", $"{zero.Code}/{huge.Code}");
});

// --- the canonical damage contract against the authoring catalog ----------------------------------------------
// The catalog row is the shared contract's source of truth, so the declared capability has to be that row port
// for port and column for column, and the provider's manifest has to advertise the binding that serves it.
Case("contract.damage-row-verbatim", () =>
{
    string catalogPath = Path.Combine(websiteRoot, "catalog", "capability-catalog.json");
    if (!File.Exists(catalogPath))
        throw new FileNotFoundException("The authoring catalog this comparison needs is required: " + catalogPath);
    var catalog = JsonDocument.Parse(File.ReadAllBytes(catalogPath)).RootElement.GetProperty("canonicalVocabulary")
        .EnumerateArray().SingleOrDefault(row => row.GetProperty("id").GetString() == DamageCapabilityId);
    var declared = JsonDocument.Parse(CombatContracts.Module().RegistryJson).RootElement.GetProperty("capabilities")
        .EnumerateArray().SingleOrDefault(row => row.GetProperty("id").GetString() == DamageCapabilityId);
    bool row = catalog.ValueKind == JsonValueKind.Object && declared.ValueKind == JsonValueKind.Object;
    var columns = row ? declared.GetProperty("graph").GetProperty("outputs").EnumerateArray()
        .Single(port => port.GetProperty("id").GetString() == "result").GetProperty("fields").EnumerateArray()
        .Select(field => field.GetProperty("id").GetString()!).ToArray() : Array.Empty<string>();
    var scene = ActionScene(null);
    var binding = JsonDocument.Parse(scene.Kernel.ExportManifest()).RootElement.GetProperty("registry")
        .GetProperty("bindings").EnumerateArray()
        .SingleOrDefault(item => item.GetProperty("capabilityId").GetString() == DamageCapabilityId);
    bool served = binding.ValueKind == JsonValueKind.Object
        && binding.GetProperty("providerId").GetString() == EnemyModule.ProviderId
        && binding.GetProperty("handler").GetString() == "gtfo.enemy.damage"
        && binding.GetProperty("role").GetString() == "execute"
        && binding.GetProperty("status").GetString() == "implemented";
    return (row
        && catalog.GetProperty("category").GetString() == declared.GetProperty("kind").GetString()
        && catalog.GetProperty("labelZh").GetString() == declared.GetProperty("label").GetString()
        && catalog.GetProperty("descriptionZh").GetString() == declared.GetProperty("parameters").GetProperty("description").GetString()
        && SameGraph(catalog.GetProperty("graph"), declared.GetProperty("graph"))
        && columns.Take(4).SequenceEqual(new[] { "target", "status", "committed", "code" })
        && served,
        "The canonical damage row equals the catalog row and the manifest advertises its binding.",
        $"row={row}; columns=[{string.Join(",", columns)}]; binding={binding.ValueKind}; served={served}");
});
// Domains compare as a set, the way the two repositories compare every shared graph; every other field,
// including the result columns, is compared field for field.
bool SameGraph(JsonElement expected, JsonElement actual)
{
    var expectedDomains = expected.GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()!).ToArray();
    var actualDomains = actual.GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()!).ToArray();
    if (expectedDomains.Length != actualDomains.Length || !expectedDomains.All(actualDomains.Contains)) return false;
    var left = expected.EnumerateObject().Where(field => field.Name != "domains").ToDictionary(field => field.Name, field => field.Value);
    var right = actual.EnumerateObject().Where(field => field.Name != "domains").ToDictionary(field => field.Name, field => field.Value);
    return left.Count == right.Count
        && left.All(field => right.TryGetValue(field.Key, out var value) && Same(field.Value, value));
}
bool Same(JsonElement left, JsonElement right)
{
    if (left.ValueKind != right.ValueKind) return false;
    if (left.ValueKind == JsonValueKind.Object)
    {
        var leftFields = left.EnumerateObject().ToDictionary(field => field.Name, field => field.Value);
        var rightFields = right.EnumerateObject().ToDictionary(field => field.Name, field => field.Value);
        return leftFields.Count == rightFields.Count
            && leftFields.All(field => rightFields.TryGetValue(field.Key, out var value) && Same(field.Value, value));
    }
    if (left.ValueKind == JsonValueKind.Array)
    {
        var leftItems = left.EnumerateArray().ToArray();
        var rightItems = right.EnumerateArray().ToArray();
        return leftItems.Length == rightItems.Length
            && leftItems.Zip(rightItems).All(pair => Same(pair.First, pair.Second));
    }
    return left.GetRawText() == right.GetRawText();
}

int failed = checks.Count(c => !c.Passed), passed = checks.Count - failed;using var sdkStream = File.OpenRead(typeof(RuntimeKernel).Assembly.Location);
using var sdkHash = System.Security.Cryptography.SHA256.Create();
var report = new { receiverSourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "EnemyModule.source.cs")))),
    scenarioRevision = "independent-scenarios-local-plans-v4", schemaVersion = 4, verification = "production-source-and-explicit-compiled-sdk-with-test-doubles", gameExecuted = false,
    frameworkAssemblySha256 = Convert.ToHexString(sdkHash.ComputeHash(sdkStream)),
    utc = DateTimeOffset.UtcNow, passed, failed, checks };
string reportPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Observed);
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {passed}/{checks.Count} receiver probes; failed {failed}; native game execution and multiplayer are NOT exercised.");
return failed == 0 ? 0 : 1;
internal sealed record ProbeCheck(string Id, bool Passed, string Expected, string Observed);
