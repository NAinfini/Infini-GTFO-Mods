using ForgeRuntime.Framework;
using SNetwork;
using static T;

internal static class LimbCases
{
    internal static void Run()
    {
        Case("limb.transition-once", () => {
            using var s = new Scene(); var token = s.Module.BeforeLimbBreak(s.Limb);
            Check(token != null && s.Module.BeforeLimbBreak(s.Limb) == null, "Nested window duplicated.");
            s.Limb.IsDestroyed = true; s.Module.AfterLimbBreak(s.Limb, token); s.Module.AfterLimbBreak(s.Limb, token);
            s.Break(); s.Tick(); var c = s.Records.Single();
            Check(c.GetEntityInput("target") == s.Ref && c.Inputs.GetProperty("limb_id").GetInt32() == 0
                && c.Source == null && s.Enemy.Damage.Sends == 0, "Wrong limb identity or inferred cause.");
        });
        Case("limb.two-parts-same-tick", () => {
            using var s = new Scene(); s.Break(); s.Break(s.Enemy.Damage.DamageLimbs[1]); s.Tick();
            Check(s.Records.Count == 2 && s.Records.Select(c => c.Inputs.GetProperty("limb_id").GetInt32())
                .SequenceEqual(new[] { 0, 1 }), "Distinct limb events merged or reordered.");
        });
        Case("limb.noop-then-genuine-transition", () => {
            using var s = new Scene(); var old = s.Module.BeforeLimbBreak(s.Limb); s.Module.AfterLimbBreak(s.Limb, old);
            var fresh = s.Module.BeforeLimbBreak(s.Limb); Check(fresh != null, "Proven no-op blocked future destruction.");
            s.Module.AfterLimbBreak(s.Limb, old); s.Limb.IsDestroyed = true;
            s.Module.AfterLimbBreak(s.Limb, fresh); s.Tick(); Check(s.Records.Count == 1, "Old token stole new window.");
        });
        Case("limb.prebroken-and-restored-not-new-life", () => {
            using var s = new Scene(); s.Limb.IsDestroyed = true;
            Check(s.Module.BeforeLimbBreak(s.Limb) == null, "Already broken part became a new transition.");
            s.Limb.IsDestroyed = false; s.Break(); s.Limb.IsDestroyed = false; s.Break(); s.Tick();
            Check(s.Records.Count == 1, "Unsupported same-life limb restoration created a second event.");
        });
        Case("limb.no-consumer-and-missing-postfix", () => {
            using var s = new Scene(load: false); s.Break(); s.Load("limb_broken"); s.Break();
            s.Module.BeforeLimbBreak(s.Enemy.Damage.DamageLimbs[1]);
            s.Break(s.Enemy.Damage.DamageLimbs[1]); s.Tick();
            Check(s.Records.Count == 0, "Unknown or unconsumed history was replayed.");
        });
        foreach (var change in new (string Id, Action<Scene, Dam_EnemyDamageLimb> Change)[] {
            ("missing-base", (_, l) => l.m_base = null!), ("negative-index", (_, l) => l.m_limbID = -1),
            ("index-budget", (_, l) => l.m_limbID = 256), ("zero-pointer", (_, l) => l.Pointer = IntPtr.Zero),
            ("not-indexed", (s, _) => s.Enemy.Damage.DamageLimbs = Array.Empty<Dam_EnemyDamageLimb>()),
            ("oversized-array", (s, _) => s.Enemy.Damage.DamageLimbs = new Dam_EnemyDamageLimb[257]),
            ("receiver-not-ready", (s, _) => s.Enemy.Damage.IsSetup = false),
            ("wrong-owner", (s, _) => s.Enemy.Damage.Owner = Scene.NewEnemy(8, 20)) })
            Case("limb.reject-" + change.Id, () => {
                using var s = new Scene(); var limb = s.Limb; change.Change(s, limb);
                Check(s.Module.BeforeLimbBreak(limb) == null, "Invalid limb accepted."); s.Tick();
                Check(s.Records.Count == 0, "Invalid limb produced an event.");
            });
        foreach (var change in new (string Id, Action<Scene, Dam_EnemyDamageLimb> Change)[] {
            ("phase", (s, _) => s.Allowed = false), ("client", (_, _) => SNet.IsMaster = false),
            ("despawn", (s, _) => s.Module.TrackDespawn(s.Enemy)),
            ("world", (s, _) => s.Kernel.BeginWorld(2)),
            ("new-receiver", (s, _) => s.Enemy.Damage = new() { Owner = s.Enemy, Pointer = new(999) }),
            ("new-indexed-part", (s, _) => s.Enemy.Damage.DamageLimbs[0] = new() { Pointer = new(999) }),
            ("new-base", (_, l) => l.m_base = new() { Pointer = new(999) }),
            ("renumbered-part", (_, l) => l.m_limbID = 1),
            ("owner-id-mismatch", (s, _) => s.Enemy.Damage.Owner = Scene.NewEnemy(8, 10)) })
            Case("limb.changed-after-capture-" + change.Id, () => {
                using var s = new Scene(); var limb = s.Limb; var token = s.Module.BeforeLimbBreak(limb);
                limb.IsDestroyed = true; change.Change(s, limb); s.Module.AfterLimbBreak(limb, token);
                SNet.IsMaster = true; s.Allowed = true; s.Module.AfterLimbBreak(limb, token); s.Tick();
                Check(s.Records.Count == 0, "Changed native readback was published/replayed.");
            });
        Case("limb.foreign-token", () => {
            using var s = new Scene(); using var other = new Scene(); var token = s.Module.BeforeLimbBreak(s.Limb);
            other.Limb.IsDestroyed = true; other.Module.AfterLimbBreak(other.Limb, token);
            s.Module.AfterDeath(s.Enemy, token); s.Limb.IsDestroyed = true; s.Module.AfterLimbBreak(s.Limb, token);
            other.Tick(); s.Tick(); Check(other.Records.Count == 0 && s.Records.Count == 1, "Foreign/kind token consumed.");
        });
        Case("limb.new-life-reuses-pointer", () => {
            using var s = new Scene(); var token = s.Module.BeforeLimbBreak(s.Limb);
            s.Module.TrackDespawn(s.Enemy); var fresh = s.Module.TrackSpawn(s.Enemy);
            s.Module.AfterLimbBreak(s.Limb, token); s.Break(); s.Tick();
            Check(s.Records.Count == 1 && s.Records[0].GetEntityInput("target") == fresh, "Reused limb inherited old life.");
        });
        Case("limb.unstable-readback", () => {
            using var s = new Scene(); var token = s.Module.BeforeLimbBreak(s.Limb);
            s.Limb.OnDestroyedRead = () => s.Limb.Destroyed = !s.Limb.Destroyed;
            s.Module.AfterLimbBreak(s.Limb, token); s.Limb.OnDestroyedRead = null;
            s.Limb.IsDestroyed = true; s.Module.AfterLimbBreak(s.Limb, token); s.Tick();
            Check(s.Records.Count == 0, "Unstable native readback was treated as confirmed.");
        });
        Case("limb.readback-exception-consumes-window", () => {
            using var s = new Scene(); var token = s.Module.BeforeLimbBreak(s.Limb);
            s.Limb.OnDestroyedRead = () => throw new IOException("native getter"); bool caught = false;
            try { s.Module.AfterLimbBreak(s.Limb, token); } catch (IOException) { caught = true; }
            s.Limb.OnDestroyedRead = null; s.Limb.IsDestroyed = true;
            s.Module.AfterLimbBreak(s.Limb, token); s.Tick(); Check(caught && s.Records.Count == 0, "Failed readback retried.");
        });
    }
}
