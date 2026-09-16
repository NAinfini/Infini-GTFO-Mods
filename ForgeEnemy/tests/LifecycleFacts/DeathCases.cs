using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using SNetwork;
using static T;

internal static class DeathCases
{
    internal static void Run()
    {
        Case("death.fact-not-kill", () => {
            using var s = new Scene(); s.Die(); var tick = s.Tick(); var c = s.Records.Single();
            Check(tick.CommandsExecuted == 1 && c.Inputs.EnumerateObject().Count() == 1
                && c.GetEntityInput("target") == s.Ref && c.CauseId == null,
                "Identity or unknown causal actors were changed.");
            Check(!s.Enemy.Alive && s.Enemy.Damage.Sends == 0, "Observation mutated health.");
        });
        Case("death.duplicate-and-nested", () => {
            using var s = new Scene(); var first = s.Module.BeforeDeath(s.Enemy);
            Check(first != null && s.Module.BeforeDeath(s.Enemy) == null, "Nested death claimed twice.");
            s.Enemy.Alive = false; s.Module.AfterDeath(s.Enemy, first); s.Module.AfterDeath(s.Enemy, first);
            s.Die(); s.Tick(); s.Tick(1); Check(s.Records.Count == 1, "Duplicate death replayed.");
        });
        Case("death.already-dead-at-entry", () => {
            using var s = new Scene(); s.Enemy.Alive = false; s.Die(); s.Tick();
            Check(s.Records.Count == 1, "Native death flow cannot assume Alive was true at entry.");
        });
        Case("death.alive-readback-is-not-death", () => {
            using var s = new Scene(); var token = s.Module.BeforeDeath(s.Enemy);
            s.Module.AfterDeath(s.Enemy, token); s.Enemy.Alive = false;
            s.Module.AfterDeath(s.Enemy, token); s.Die(); s.Tick();
            Check(s.Records.Count == 0, "Unknown/failed death completion was replayed.");
        });
        Case("death.no-consumer-does-not-replay", () => {
            using var s = new Scene(load: false); s.Die(); s.Load("death_started"); s.Die(); s.Tick();
            Check(s.Records.Count == 0, "Subscription backfilled an already observed death.");
        });
        Case("death.no-postfix-does-not-replay", () => {
            using var s = new Scene(); s.Module.BeforeDeath(s.Enemy); s.Die(); s.Tick();
            Check(s.Records.Count == 0, "A missing native completion was retried.");
        });
        Case("death.no-startup-replay", () => {
            using var s = new Scene(start: false);
            Check(s.Module.BeforeDeath(s.Enemy) == null, "Captured pre-ready fact.");
            s.Kernel.StartRuntime(() => { }); s.Die(); s.Tick(); Check(s.Records.Count == 1, "Valid new callback suppressed.");
        });
        Case("death.foreign-owner-and-wrong-kind", () => {
            using var s = new Scene(); using var other = new Scene(); var token = s.Module.BeforeDeath(s.Enemy);
            other.Enemy.Alive = false; other.Module.AfterDeath(other.Enemy, token);
            s.Module.AfterLimbBreak(s.Limb, token); s.Enemy.Alive = false; s.Module.AfterDeath(s.Enemy, token);
            other.Tick(); s.Tick(); Check(other.Records.Count == 0 && s.Records.Count == 1, "Token ownership crossed.");
        });
        foreach (var change in new (string Id, Action<Scene> Change)[] {
            ("phase", s => s.Allowed = false), ("client", _ => SNet.IsMaster = false),
            ("despawn", s => s.Module.TrackDespawn(s.Enemy)), ("world", s => s.Kernel.BeginWorld(2)),
            ("pointer", s => s.Enemy.Pointer = new(999)), ("id", s => s.Enemy.GlobalID = 99) })
            Case("death.stale-" + change.Id, () => {
                using var s = new Scene(); var token = s.Module.BeforeDeath(s.Enemy); s.Enemy.Alive = false;
                change.Change(s); s.Module.AfterDeath(s.Enemy, token); s.Allowed = true; SNet.IsMaster = true;
                s.Module.AfterDeath(s.Enemy, token); s.Tick(); Check(s.Records.Count == 0, "Stale completion emitted.");
            });
        Case("death.pooled-new-life", () => {
            using var s = new Scene(); var token = s.Module.BeforeDeath(s.Enemy); s.Module.TrackDespawn(s.Enemy);
            var fresh = s.Module.TrackSpawn(s.Enemy); s.Enemy.Alive = false; s.Module.AfterDeath(s.Enemy, token);
            s.Die(); s.Tick(); Check(s.Records.Count == 1 && s.Records[0].GetEntityInput("target") == fresh
                && fresh.LifeEpoch != s.Ref.LifeEpoch, "Old token affected reused native pointer.");
        });
        Case("death.two-instances-same-tick", () => {
            using var s = new Scene(); var second = Scene.NewEnemy(8, 20); s.Module.TrackSpawn(second);
            s.Die(); s.Die(second); s.Tick(); Check(s.Records.Count == 2, "Distinct deaths merged by tick.");
        });
        Case("death.thread-boundary", () => {
            using var s = new Scene(); bool rejected = false;
            try { Task.Run(() => s.Module.BeforeDeath(s.Enemy)).GetAwaiter().GetResult(); }
            catch (RuntimeContractException e) { rejected = e.Code == "wrong-thread"; }
            Check(rejected, "Wrong thread entered native observation."); s.Die(); s.Tick();
            Check(s.Records.Count == 1, "Rejected thread mutated life latch.");
        });
    }
}
