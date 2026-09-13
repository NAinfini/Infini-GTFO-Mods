internal static class DamageCases
{
    internal static void Run()
    {
        Audit.Case("damage.single-consumption", () => { var s = new AuditScene(true); var d = s.Actor.Damage;
            var observation = s.Module.BeforeDamage(d); Audit.Require(observation != null, "No fixture subscription.");
            d.Health = 40; s.Module.AfterDamage(d, observation); s.Module.AfterDamage(d, observation);
            var tick = s.Kernel.Advance(1, true); Audit.Require(tick.Commands.Count == 1 && d.Sends == 1 && d.Health == 45,
                $"Expected one +5 HP commit, commands={tick.Commands.Count}; sends={d.Sends}; HP={d.Health}"); });
        Audit.Case("damage.zero-window-not-replayed", () => { var s = new AuditScene(true); var d = s.Actor.Damage;
            var observation = s.Module.BeforeDamage(d); s.Module.AfterDamage(d, observation);
            d.Health = 40; s.Module.AfterDamage(d, observation); Audit.Require(s.Kernel.QueuedEvents == 0, "Zero window was reused later."); });
        Audit.Case("damage.two-real-hits-same-tick", () => { var s = new AuditScene(true); var d = s.Actor.Damage;
            var one = s.Module.BeforeDamage(d); d.Health = 45; s.Module.AfterDamage(d, one);
            var two = s.Module.BeforeDamage(d); d.Health = 40; s.Module.AfterDamage(d, two);
            var tick = s.Kernel.Advance(1, true); Audit.Require(tick.Commands.Count == 2 && d.Sends == 2 && d.Health == 50,
                "Different real damage windows were incorrectly merged."); });
        Audit.Case("damage.rejected-window-not-replayed", () => { var s = new AuditScene(true); var d = s.Actor.Damage;
            var observation = s.Module.BeforeDamage(d); d.Health = 40; s.Allowed = false; s.Module.AfterDamage(d, observation);
            s.Allowed = true; s.Module.AfterDamage(d, observation); Audit.Require(s.Kernel.QueuedEvents == 0, "Rejected observation became executable later."); });
        Audit.Case("damage.old-world", () => { var s = new AuditScene(true); var d = s.Actor.Damage;
            var observation = s.Module.BeforeDamage(d); d.Health = 40; s.Kernel.BeginWorld(2); s.Module.ClearWorld();
            s.Module.AfterDamage(d, observation); Audit.Require(s.Kernel.QueuedEvents == 0, "Old-world damage was published."); });
        Audit.Case("damage.foreign-owner", () => { var a = new AuditScene(true); var b = new AuditScene(true); var d = a.Actor.Damage;
            var observation = a.Module.BeforeDamage(d); d.Health = 40; b.Module.AfterDamage(d, observation); a.Module.AfterDamage(d, observation);
            Audit.Require(b.Kernel.QueuedEvents == 0 && a.Kernel.QueuedEvents == 1, "Wrong owner stole or consumed an observation."); });
    }
}
