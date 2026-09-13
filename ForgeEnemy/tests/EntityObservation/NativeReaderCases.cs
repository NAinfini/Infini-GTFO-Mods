using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;
using static Checks;

internal static class NativeReaderCases
{
    internal static void Run()
    {
        var cases = new (string Name, Action<Scene> Change, Func<RuntimeEntitySnapshot?, bool> Accept)[]
        {
            ("healthy", _ => { }, x => x?.Kind == "enemy" && x.Faction == null && x.Tags.Count == 0
                && x.Position.SequenceEqual(new double[] { 1, 2, 3 }) && x.Receives.SequenceEqual(new[] { "health.heal" })),
            ("full", s => s.Enemy.Damage.Health = 100, x => x?.Receives.Contains("health.heal") == true),
            ("dead", s => s.Enemy.Alive = false, x => x?.LifeState == "dead" && x.Receives.Count == 0),
            ("missing-health", s => s.Enemy.Damage = null!, x => x?.Receives.Count == 0),
            ("bad-owner", s => s.Enemy.Damage.Owner = Scene.NewEnemy(8, 20), x => x?.Receives.Count == 0),
            ("unsetup-health", s => s.Enemy.Damage.IsSetup = false, x => x?.Receives.Count == 0),
            ("nan-health", s => s.Enemy.Damage.Health = float.NaN, x => x?.Receives.Count == 0),
            ("bad-max", s => s.Enemy.Damage.HealthMax = float.PositiveInfinity, x => x?.Receives.Count == 0),
            ("above-max", s => s.Enemy.Damage.Health = 101, x => x?.Receives.Count == 0),
            ("zero-native", s => s.Enemy.Pointer = IntPtr.Zero, x => x == null),
            ("unsetup-enemy", s => s.Enemy.IsSetup = false, x => x == null),
            ("nan-position", s => s.Enemy.Point.x = float.NaN, x => x == null),
            ("unstable-position", s => s.Enemy.OnPosition = () => s.Enemy.Point.x++, x => x == null)
        };
        foreach (var item in cases) Case("native-reader." + item.Name, () =>
        {
            using var s = new Scene(); var original = s.Enemy.Damage; item.Change(s);
            Require(item.Accept(EnemyEntityObserver.Read(s.Enemy, s.Ref)), "Native reader did not preserve evidence boundaries.");
            Require(original.Sends == 0, "Read-only inspection submitted native health.");
        });
        Case("native-reader.wrong-id", () =>
        {
            using var s = new Scene();
            Require(EnemyEntityObserver.Read(s.Enemy, s.Ref with { Id = "gtfo.enemy:8" }) == null, "Wrong native association.");
        });
        Case("native-reader.frozen", () =>
        {
            using var s = new Scene(); var snapshot = EnemyEntityObserver.Read(s.Enemy, s.Ref)!;
            s.Enemy.Point.x = 100;
            Require(snapshot.Position[0] == 1, "Snapshot remained attached to mutable native data.");
        });
    }
}
