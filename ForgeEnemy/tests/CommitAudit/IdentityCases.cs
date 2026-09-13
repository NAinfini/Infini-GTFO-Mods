using ForgeRuntime.Framework;

internal static class IdentityCases
{
    internal static void Run()
    {
        Audit.Case("identity.duplicate-spawn", () => { var s = new AuditScene();
            Audit.Require(s.Module.TrackSpawn(s.Actor) == s.Reference, "A duplicate spawn changed lifeEpoch."); });
        Audit.Case("identity.wrapper-not-new-life", () => { var s = new AuditScene(); var wrapper = AuditScene.CreateEnemy();
            Audit.Require(s.Module.TrackSpawn(wrapper) == s.Reference, "A different managed wrapper falsely proved a new native life."); });
        Audit.Case("identity.captured-old-token-after-pool-reuse", () =>
        {
            var s = new AuditScene(); var captured = s.Module.CaptureDespawn(s.Actor);
            s.Module.CompleteDespawn(captured); var newer = s.Module.TrackSpawn(s.Actor);
            s.Module.CompleteDespawn(captured); var result = s.Heal(newer);
            Audit.Require(newer.LifeEpoch != s.Reference.LifeEpoch && s.Heal(s.Reference).Status == "rejected"
                && result.Status == "succeeded", "A captured old-life token deleted the reused-pointer new life.");
        });
        Audit.Case("identity.pointer-mutated", () => { var s = new AuditScene(); s.Actor.Pointer = new IntPtr(20); var r = s.Heal();
            Audit.Require(r.Status == "rejected" && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("identity.foreign-despawn-token", () => { var first = new AuditScene(); var second = new AuditScene();
            second.Module.CompleteDespawn(first.Module.CaptureDespawn(first.Actor));
            Audit.Require(second.Heal().Status == "succeeded", "Foreign module teardown deleted an unrelated receiver."); });
        Audit.Case("identity.old-world", () => { var s = new AuditScene(); s.Kernel.BeginWorld(2); s.Module.ClearWorld(); var r = s.Heal();
            Audit.Require(r.Status == "rejected" && r.CommitState == CommitStates.None && s.Actor.Damage.Sends == 0, r.Code); });
    }
}
