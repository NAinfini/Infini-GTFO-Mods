using System.Reflection;
using System.Text.Json;
using Enemies;
using ForgeRuntime.Framework;
using ForgeEnemy.Native;
using SNetwork;

if (args.Length != 1) { Console.Error.WriteLine("Usage: ReceiverProbe <report.json>"); return 2; }
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
EnemyAgent Enemy(long pointer = 10)
{
    var actor = new EnemyAgent { GlobalID = 7, Pointer = new IntPtr(pointer) };
    actor.Damage = new Dam_EnemyDamageBase { Owner = actor, Pointer = new IntPtr(pointer + 100) };
    return actor;
}
(RuntimeKernel Kernel, EnemyModule Module, EnemyAgent Enemy, EntityReference Ref) Scene()
{
    var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"), new RuntimeLimits());
    kernel.BeginWorld(1); kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
    var module = new EnemyModule(kernel, RuntimeLogLevel.Off, () => true, _ => { });
    var actor = Enemy(); return (kernel, module, actor, module.TrackSpawn(actor));
}
// damage_applied -> record(target): observes exactly what the receiver publishes, with no heal in the loop.
(RuntimeKernel Kernel, EnemyModule Module, EnemyAgent Enemy, EntityReference Ref, List<CommandContext> Records) DamageScene()
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
            RuntimeJson.From(new { overheal_policy = "clamp" }), RuntimeJson.From(new { targets = new[] { target }, source = target, amount }) }, null)!;
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
    var damage = DamageScene();
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
    var independent = DamageScene();
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
    var zeroHit = DamageScene();
    var zeroToken = zeroHit.Module.BeforeDamage(zeroHit.Enemy.Damage);
    zeroHit.Module.AfterDamage(zeroHit.Enemy.Damage, zeroToken);
    zeroHit.Enemy.Damage.Health = 40; zeroHit.Module.AfterDamage(zeroHit.Enemy.Damage, zeroToken);
    return (zeroHit.Kernel.Advance(1, true).Commands.Count == 0 && zeroHit.Records.Count == 0,
        "A zero-loss window cannot later acquire unrelated damage by replay.", $"records={zeroHit.Records.Count}");
});
Case("damage.rejected-window-consumed", () =>
{
    var rejectedHit = DamageScene();
    var rejectedToken = rejectedHit.Module.BeforeDamage(rejectedHit.Enemy.Damage); rejectedHit.Enemy.Damage.Health = 40;
    SNet.IsMaster = false; rejectedHit.Module.AfterDamage(rejectedHit.Enemy.Damage, rejectedToken); SNet.IsMaster = true;
    rejectedHit.Module.AfterDamage(rejectedHit.Enemy.Damage, rejectedToken);
    return (rejectedHit.Kernel.Advance(1, true).Commands.Count == 0 && rejectedHit.Records.Count == 0,
        "Rejected callbacks cannot be retried when authority returns.", $"records={rejectedHit.Records.Count}");
});
Case("identity.late-damage-after-respawn", () =>
{
    // A damage callback that finishes after its enemy was destroyed and the same GlobalID respawned.
    var lateHit = DamageScene();
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
    var issuer = DamageScene(); var impostor = DamageScene();
    var issuerToken = issuer.Module.BeforeDamage(issuer.Enemy.Damage); issuer.Enemy.Damage.Health = 40; impostor.Enemy.Damage.Health = 40;
    impostor.Module.AfterDamage(impostor.Enemy.Damage, issuerToken);
    issuer.Module.AfterDamage(issuer.Enemy.Damage, issuerToken);
    int impostorCommands = impostor.Kernel.Advance(1, true).Commands.Count, issuerCommands = issuer.Kernel.Advance(1, true).Commands.Count;
    return (impostorCommands == 0 && issuerCommands == 1,
        "A foreign module cannot publish or consume another receiver's observation.", $"impostor={impostorCommands}; issuer={issuerCommands}");
});
Case("damage.replaced-component", () =>
{
    var componentHit = DamageScene();
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

int failed = checks.Count(c => !c.Passed), passed = checks.Count - failed;
using var sdkStream = File.OpenRead(typeof(RuntimeKernel).Assembly.Location);
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
