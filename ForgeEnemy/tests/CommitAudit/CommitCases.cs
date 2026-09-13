using ForgeRuntime.Framework;
using SNetwork;

internal static class CommitCases
{
    internal static void Run()
    {
        Audit.Case("heal.actual-five", () => { var s = new AuditScene(); var r = s.Heal();
            Audit.Require(s.Actor.Damage.Health == 55 && s.Actor.Damage.Sends == 1 && r.Facts.Count == 1
                && r.Outputs.GetProperty("actualAmount").GetDouble() == 5, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.full-no-packet", () => { var s = new AuditScene(); s.Actor.Damage.Health = 100; var r = s.Heal();
            Audit.Require(r.Status == "succeeded" && r.Facts.Count == 0 && s.Actor.Damage.Sends == 0, AuditScene.Describe(r, s.Actor.Damage.Sends)); });
        Audit.Case("heal.clamp", () => { var s = new AuditScene(); s.Actor.Damage.Health = 99; var r = s.Heal();
            Audit.Require(s.Actor.Damage.Health == 100 && r.Outputs.GetProperty("actualAmount").GetDouble() == 1, r.Outputs.ToString()); });
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
