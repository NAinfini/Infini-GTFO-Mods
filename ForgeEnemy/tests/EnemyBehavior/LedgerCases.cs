using System;
using System.Linq;
using System.Text.Json;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>The behaviour ledger's own cases: what one successful ability submission remembers, when the row is
/// dropped, and that a row from another world is never answered. The ledger is the provider-side table the
/// interruption path reads, so the cases cover the interface a consumer uses rather than the dictionary behind
/// it.</summary>
internal static class LedgerCases
{
    private const byte Melee = 1, Ranged = 2, Alarm = 3, Healing = 5, Detection = 7, DoorBreaker = 8;

    internal static void Run()
    {
        T.Case("ledger.running-ability-is-remembered", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Ranged);
            FakePorts.Register(enemy, Ranged, 2, component: 777);
            var ledger = new EnemyBehaviorLedger();
            var reference = Scene.Reference();
            Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("ranged")), ports, ledger);
            var entry = ledger.Find(reference.Id, Scene.World);
            T.Check(entry != null, "the running ability is remembered");
            T.Equal(new IntPtr(777), entry!.Component, "the row names the component that was submitted to");
            T.Equal(Ranged, entry.Ability, "the row names the requested ability");
            T.Equal(Scene.World, entry.WorldEpoch, "the row records the world it was written in");
        });

        T.Case("ledger.end-drops-the-row", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Detection);
            var ledger = new EnemyBehaviorLedger();
            var reference = Scene.Reference();
            Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("detection")), ports, ledger);
            T.Check(ledger.End(reference.Id, Scene.World), "ending a row that exists answers true");
            T.Check(ledger.Find(reference.Id, Scene.World) == null, "an ended row is gone");
        });

        T.Case("ledger.retired-life-answers-nothing", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", DoorBreaker);
            var ledger = new EnemyBehaviorLedger();
            var reference = Scene.Reference();
            Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("door_breaker")), ports, ledger);
            T.Check(ledger.Find(reference.Id, Scene.World + 1) == null, "a row from an earlier world is not answered");
        });

        T.Case("ledger.other-enemy-answers-nothing", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Healing);
            var ledger = new EnemyBehaviorLedger();
            Scene.DispatchAbility(Scene.AbilityContext(new[] { Scene.Reference() }, Scene.Ability("healing")), ports, ledger);
            T.Check(ledger.Find("gtfo.enemy:999", Scene.World) == null, "a life with no row answers nothing");
        });

        T.Case("ledger.clear-drops-every-row", () =>
        {
            var ports = new FakePorts();
            ports.Track("gtfo.enemy:7", Melee);
            ports.Track("gtfo.enemy:8", Alarm);
            var ledger = new EnemyBehaviorLedger();
            Scene.DispatchAbility(Scene.AbilityContext(new[] { Scene.Reference("gtfo.enemy:7") }, Scene.Ability("melee")), ports, ledger);
            Scene.DispatchAbility(Scene.AbilityContext(new[] { Scene.Reference("gtfo.enemy:8") }, Scene.Ability("alarm")), ports, ledger);
            T.Equal(2, ledger.Count, "two lives hold a row");
            ledger.Clear();
            T.Equal(0, ledger.Count, "a world change drops every row");
        });

        T.Case("ledger.a-second-submission-replaces-the-row", () =>
        {
            var ports = new FakePorts();
            var enemy = ports.Track("gtfo.enemy:7", Melee, Alarm);
            FakePorts.Register(enemy, Melee, 0, component: 100);
            FakePorts.Register(enemy, Alarm, 1, component: 200);
            var ledger = new EnemyBehaviorLedger();
            var reference = Scene.Reference();
            Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("melee")), ports, ledger);
            Scene.DispatchAbility(Scene.AbilityContext(new[] { reference }, Scene.Ability("alarm")), ports, ledger);
            var entry = ledger.Find(reference.Id, Scene.World);
            T.Check(entry != null, "the life still holds a row");
            T.Equal(Alarm, entry!.Ability, "the newest submission is the one running");
            T.Equal(new IntPtr(200), entry.Component, "the row names the newest component");
            T.Equal(1, ledger.Count, "one agent holds one row, as the native machine holds one active ability");
        });
    }
}
