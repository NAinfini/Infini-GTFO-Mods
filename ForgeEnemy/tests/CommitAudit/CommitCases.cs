using ForgeRuntime.Framework;
using SNetwork;

internal static class CommitCases
{
    internal static void Run()
    {
        Audit.Case("heal.actual-five", () => { var s = new AuditScene(); var r = s.Heal();
            Audit.Require(s.Actor.Damage.Health == 55 && s.Actor.Damage.Sends == 1 && r.Facts.Count == 1
                && AuditScene.Row(r).GetProperty("actualAmount").GetDouble() == 5, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.full-no-packet", () => { var s = new AuditScene(); s.Actor.Damage.Health = 100; var r = s.Heal();
            Audit.Require(r.Status == "succeeded" && r.Facts.Count == 0 && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.clamp", () => { var s = new AuditScene(); s.Actor.Damage.Health = 99; var r = s.Heal();
            Audit.Require(s.Actor.Damage.Health == 100 && AuditScene.Row(r).GetProperty("actualAmount").GetDouble() == 1, r.Outputs.ToString()); });
        Audit.Case("heal.cap-limits-below-max", () => { var s = new AuditScene(); var r = s.Heal(cap: 52);
            Audit.Require(s.Actor.Damage.Health == 52 && AuditScene.Row(r).GetProperty("actualAmount").GetDouble() == 2
                && AuditScene.Row(r).GetProperty("overflowAmount").GetDouble() == 3, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.invalid-cap-rejected", () => { var s = new AuditScene(); var r = s.Heal(cap: 0);
            Audit.Require(r.Status == "rejected" && r.Code == "gtfo.enemy.invalid_cap" && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.discard-would-overheal", () => { var s = new AuditScene(); s.Actor.Damage.Health = 99;
            var r = s.Heal(policy: "discard");
            Audit.Require(r.Status == "rejected" && r.Code == "gtfo.enemy.would_overheal" && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.discard-fits-commits", () => { var s = new AuditScene(); s.Actor.Damage.Health = 90;
            var r = s.Heal(policy: "discard");
            Audit.Require(r.Status == "succeeded" && s.Actor.Damage.Health == 95, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.overheal-unsupported", () => { var s = new AuditScene(); var r = s.Heal(policy: "overheal");
            Audit.Require(r.Status == "rejected" && r.Code == "gtfo.enemy.overheal_unsupported" && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.dead-no-revive", () => { var s = new AuditScene(); s.Actor.Alive = false; s.Actor.Damage.Health = 0; var r = s.Heal();
            Audit.Require(r.Code == "gtfo.enemy.not_alive" && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.initial-client", () => { var s = new AuditScene(); SNet.IsMaster = false; var r = s.Heal();
            Audit.Require(r.CommitState == CommitStates.None && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.removed-after-submit", () => { var s = new AuditScene(); var d = s.Actor.Damage;
            d.Commit = v => { d.Health = v; s.Module.TrackDespawn(s.Actor); }; s.RequireUnknown(s.Heal(), d); });
        Audit.Case("heal.invalid-readback", () => { var s = new AuditScene(); var d = s.Actor.Damage;
            d.Commit = _ => d.Health = float.NaN; s.RequireUnknown(s.Heal(), d); });
        Audit.Case("heal.submit-throws", () => { var s = new AuditScene(); var d = s.Actor.Damage;
            d.Commit = _ => throw new IOException("synthetic submit exception"); s.RequireUnknown(s.Heal(), d); });
        Audit.Case("heal.throw-after-mutation", () => { var s = new AuditScene(); var d = s.Actor.Damage;
            d.Commit = v => { d.Health = v; throw new IOException("synthetic post-submit exception"); }; s.RequireUnknown(s.Heal(), d); });
        Preflight("health-fell", s => s.Actor.Damage.Health = 40);
        Preflight("health-rose", s => s.Actor.Damage.Health = 60);
        Preflight("maximum-changed", s => s.Actor.Damage.HealthMax = 80);
        Preflight("authority-lost", s => s.Allowed = false);
        Preflight("client-now", _ => SNet.IsMaster = false);
        Preflight("died", s => { s.Actor.Alive = false; s.Actor.Damage.Health = 0; });
        Preflight("world-changed", s => { s.Kernel.BeginWorld(2); s.Module.ClearWorld(); });
        Preflight("life-changed", s => { s.Module.TrackDespawn(s.Actor); s.Module.TrackSpawn(s.Actor); });
        Preflight("receiver-replaced", s => s.Actor.Damage = new Dam_EnemyDamageBase { Owner = s.Actor, Pointer = new IntPtr(900) });
        Audit.Case("preflight.negative-native-quantization", () => { var s = new AuditScene(); SFloat16.Preview = (_, _) => -1;
            var r = s.Heal(); Audit.Require(r.Status == "rejected" && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("preflight.noop-state-changed", () => { var s = new AuditScene(); s.Actor.Damage.Health = 100;
            SFloat16.Preview = (v, _) => { s.Actor.Damage.Health = 90; return v; }; var r = s.Heal();
            Audit.Require(r.CommitState == CommitStates.None && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });

        // A full-health target commits with actual==0 and produces no fact; combined with a rejected target and
        // no unknown outcome, no target's HP actually changed, so the handler must report Rejected/None with a
        // dedicated code instead of a factless Partial (which RuntimeKernel.NormalizeInvokedResult would otherwise
        // rewrite into a fabricated FailedUnknown("invalid-handler-result")).
        Audit.Case("heal.multi-full-and-rejected-is-no-state-change", () =>
        {
            var s = new AuditScene(); s.Actor.Damage.Health = 100;
            var (second, secondRef) = s.SpawnActor();
            second.Alive = false; second.Damage.Health = 0;
            var r = s.Heal(new[] { s.Reference, secondRef });
            Audit.Require(r.Status == "rejected" && r.CommitState == CommitStates.None
                && r.Code == "gtfo.enemy.heal_no_state_change" && r.Facts.Count == 0
                && AuditScene.Rows(r).Length == 2, AuditScene.Describe(r, s.Actor.Damage.Sends));
        });
        // A full-health target contributes no fact; combined with a target whose commit becomes unknown, the
        // handler must explicitly return Failed/Unknown rather than an invalid factless Partial/Unknown.
        Audit.Case("heal.multi-full-and-unknown-is-failed", () =>
        {
            var s = new AuditScene(); s.Actor.Damage.Health = 100;
            var (second, secondRef) = s.SpawnActor();
            second.Damage.Commit = _ => throw new IOException("synthetic commit exception");
            var r = s.Heal(new[] { s.Reference, secondRef });
            Audit.Require(r.Status == "failed" && r.CommitState == CommitStates.Unknown
                && r.Code == "gtfo.enemy.heal_all_unknown" && r.Facts.Count == 0
                && AuditScene.Rows(r).Length == 2, AuditScene.Describe(r, s.Actor.Damage.Sends));
        });
        // Exhaustive coverage: for every combination of two targets' outcomes (full health / damageable /
        // dead-rejected / commit-throws-unknown), the raw handler result must independently satisfy
        // CommandResultRules.TryValidate and must never carry the kernel's fallback code.
        Audit.Case("heal.multi-exhaustive-never-invalid-handler-result", () =>
        {
            string[] modes = { "full", "damage", "dead", "throws" };
            foreach (var m1 in modes)
                foreach (var m2 in modes)
                {
                    var s = new AuditScene(); Configure(s.Actor, m1);
                    var (second, secondRef) = s.SpawnActor(); Configure(second, m2);
                    var r = s.Heal(new[] { s.Reference, secondRef });
                    bool valid = AuditScene.IsValidHandlerResult(r, out var violation);
                    Audit.Require(valid && r.Code != "invalid-handler-result",
                        $"modes={m1}+{m2}; violation={violation}; {AuditScene.Describe(r, s.Actor.Damage.Sends)}");
                }
        });
    }
    private static void Configure(Enemies.EnemyAgent actor, string mode)
    {
        switch (mode)
        {
            case "full": actor.Damage.Health = actor.Damage.HealthMax; break;
            case "damage": actor.Damage.Health = actor.Damage.HealthMax / 2; break;
            case "dead": actor.Alive = false; actor.Damage.Health = 0; break;
            case "throws": actor.Damage.Health = actor.Damage.HealthMax / 2;
                actor.Damage.Commit = _ => throw new IOException("synthetic commit exception"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown scene mode.");
        }
    }
    private static void Preflight(string name, Action<AuditScene> change)
    {
        Audit.Case("preflight." + name, () =>
        {
            var scene = new AuditScene(); var original = scene.Actor.Damage;
            SFloat16.Preview = (value, _) => { change(scene); return value; };
            var result = scene.Heal();
            Audit.Require(original.Sends == 0 && result.CommitState == CommitStates.None,
                "Must reject before native submission when preflight state changes: " + AuditScene.Describe(result, original.Sends));
        });
    }
}
