using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// The trigger card's options, as the runtime enforces them (plan §3.4 统一选项). What is under test is the whole
/// path: an option block is read once when the plan loads and every refusal is a code, not a silently dropped
/// event — a threshold that is not reached yet, a cap that is spent, a clock that has not run out, a roll that
/// failed — while an entry point that declares nothing keeps behaving exactly as it did before the block existed.
///
/// The state these options carry belongs to the world, so the last thing under test is that a checkpoint taken
/// half way through a threshold puts the same counters back and the count finishes from there rather than starting
/// over.
/// </summary>
internal static class TriggerGateTests
{
    private const string Id = "example.alpha";

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void RejectCode(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }

        // Every option is an entry point's structural parameter: the block is read while the plan loads, so a
        // malformed one rejects the plan instead of failing at dispatch, and it is read exactly once — a plan that
        // declares it never re-reads it per event.
        foreach (var (gate, code, name) in new[]
        {
            ("[]", "gate-shape", "a non-object block"),
            ("{}", "gate-empty", "an empty block"),
            ("{\"accumulate\":0}", "gate-count", "a threshold below one"),
            ("{\"accumulate\":1.5}", "gate-count", "a fractional threshold"),
            ("{\"max_count\":-1}", "gate-count", "a negative cap"),
            ("{\"cooldown\":\"soon\"}", "gate-count", "a non-numeric cooldown"),
            ("{\"reset_delay\":-1}", "gate-reset-delay", "a negative reset delay"),
            ("{\"scope\":\"world\"}", "gate-scope", "an unknown scope"),
            ("{\"cooldown\":10}", "gate-scope", "a booked option with no scope"),
            ("{\"accumulate\":2}", "gate-scope", "a threshold with no scope"),
            ("{\"max_count\":2}", "gate-scope", "a cap with no scope"),
            ("{\"scope\":\"player\"}", "gate-scope", "a scope with nothing to book against it"),
            ("{\"scope\":\"instance\",\"cooldown\":10}", "gate-scope", "a per-instance gate on a level mount"),
            ("{\"probability\":0}", "gate-probability", "a probability of zero"),
            ("{\"probability\":1.5}", "gate-probability", "a probability above one"),
            ("{\"once\":\"yes\"}", "gate-once", "a non-boolean once"),
            ("{\"fires\":1}", "unknown-field", "an unknown option")
        })
        {
            var scenario = new Scenario();
            var module = scenario.Register(Id);
            RejectCode(() => scenario.Gated("gated", Id, gate), code, name);
            Check(module.IsRegistered, name + " leaves the module alone");
        }

        // No block at all is the shape every plan written before this batch has, and it must not be gated.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id);
            scenario.Plan("plain", Id);
            var fired = 0L;
            for (var i = 1; i <= 3; i++) fired += Beat(scenario, module, "plain-" + i).CommandsExecuted;
            Check(fired == 3, "an entry point with no block is never gated");
        }

        // A threshold counts the events the entry matched and fires on the one that reaches it, then counts again:
        // the window is the threshold's own, while the cap is a separate budget of firings. Once the budget is
        // spent the arrival is refused by the cap first, before anything could have been counted for it.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id);
            scenario.Gated("threshold", Id, "{\"accumulate\":2,\"max_count\":2,\"scope\":\"level\"}");
            var first = Beat(scenario, module, "t1");
            Check(first.CommandsExecuted == 0 && first.GateRefusals.Single().Code == "gate-accumulate"
                && first.Events.Single().Status == "rejected" && first.Events.Single().Code == "gate-refused",
                "the first arrival below the threshold is refused as gate-refused");
            Check(Beat(scenario, module, "t2").CommandsExecuted == 1, "the arrival that reaches the threshold fires");
            Check(scenario.Kernel.GateStateOf("threshold", "Entry") == (1, 0), "a fired threshold resets its counter");
            Check(Beat(scenario, module, "t3").CommandsExecuted == 0, "the window starts over after a fire");
            Check(Beat(scenario, module, "t4").CommandsExecuted == 1, "the second window reaches the threshold on its own");
            var spent = Beat(scenario, module, "t5");
            Check(spent.CommandsExecuted == 0 && spent.GateRefusals.Single().Code == "gate-max-count",
                "a spent budget refuses the next arrival before it is counted");
            Check(scenario.Kernel.GateStateOf("threshold", "Entry") == (2, 0), "a refused arrival leaves the counter alone");
        }

        // A threshold counts a number the matched event carries rather than the event itself, and a head start
        // belongs to the first window alone: a window cleared by the reset delay starts at zero instead of being
        // handed the offset again. An arrival whose amount cannot be read is refused by the amount check without
        // charging the window, so a later event that does carry one can still reach the threshold.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id, amountPort: true);
            scenario.Gated("head-start", Id, "{\"accumulate\":3,\"threshold_offset\":2,\"reset_delay\":5,\"accumulate_amount\":\"amount\",\"scope\":\"level\"}", amountPort: true);
            Check(Beat(scenario, module, "o1", port: "amount", value: 1.0).CommandsExecuted == 1,
                "the first arrival starts at the offset and reaches the threshold with the amount it carried");
            Check(scenario.Kernel.GateStateOf("head-start", "Entry") == (1, 0), "the head start is spent with the window");
            var missing = Beat(scenario, module, "o2", 9);
            Check(missing.CommandsExecuted == 0 && missing.GateRefusals.Single().Code == "gate-amount",
                "an event that does not carry the port is refused by the amount check");
            Check(scenario.Kernel.GateStateOf("head-start", "Entry") == (1, 0), "a refused amount leaves the cleared window alone");
            var quiet = Beat(scenario, module, "o3", port: "amount", value: 0.0);
            Check(quiet.GateRefusals.Single().Code == "gate-accumulate",
                "an amount of zero is a counted arrival that adds nothing");
            Check(scenario.Kernel.GateStateOf("head-start", "Entry") == (1, 0), "the window is still the one the reset cleared");
            Check(Beat(scenario, module, "o4", port: "amount", value: 1.0).CommandsExecuted == 0,
                "one more inside the delay is still below the threshold");
            Check(Beat(scenario, module, "o5", port: "amount", value: 2.0).CommandsExecuted == 1,
                "the arrival whose amount reaches the threshold fires");
            Check(scenario.Kernel.GateStateOf("head-start", "Entry") == (2, 0), "the next window starts at zero");
            Check(Beat(scenario, module, "o6", port: "amount", value: 3.0).CommandsExecuted == 1,
                "the offset is not handed out a second time");
        }


        // A gate is a host decision, and only a host advances with it: a client runs what the host decided instead
        // of charging a second counter of its own, and its own advance drops the event as `not-host`.
        {
            var host = new Scenario(); var module = host.Register(Id);
            host.Gated("split", Id, "{\"accumulate\":2,\"scope\":\"level\"}");
            Beat(host, module, "s1");
            Beat(host, module, "s2");
            Check(host.Kernel.GateStateOf("split", "Entry") == (1, 0) && host.Kernel.GateStateCounts() == (1, 1),
                "the host counts the two arrivals that reached its threshold");
            var replica = new Scenario(); var replicaModule = replica.Register(Id);
            replica.Gated("split", Id, "{\"accumulate\":2,\"scope\":\"level\"}");
            replicaModule.Publish(new RuntimeEvent("replica", Fixture.Trigger(Id), replica.World, 0, "shared-scope",
                RuntimeJson.From(new Dictionary<string, object> { ["target"] = new EntityReference(Id + ":1", replica.World, replica.Life) })));
            var replicaAdvance = replica.Kernel.Advance(0, false);
            Check(replicaAdvance.CommandsExecuted == 0 && replicaAdvance.GateRefusals.Count == 0
                && replicaAdvance.Events.Single().Status == "rejected" && replicaAdvance.Events.Single().Code == "not-host",
                "a replica asks no gate of its own and drops the event as not-host");
            Check(replica.Kernel.GateStateOf("split", "Entry") == null && replica.Kernel.GateStateCounts() == (0, 0),
                "the replica's advance charged no counter");
        }

        // The reset delay forgives a threshold that took too long: arrivals that are too far apart never add up.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id);
            scenario.Gated("decay", Id, "{\"accumulate\":2,\"reset_delay\":5,\"scope\":\"level\"}");
            Beat(scenario, module, "d1");
            var late = Beat(scenario, module, "d2", 9);
            Check(late.CommandsExecuted == 0 && late.GateRefusals.Single().Code == "gate-accumulate",
                "an arrival after the reset delay starts the count over");
            Check(scenario.Kernel.GateStateOf("decay", "Entry") == (0, 1), "the count after a reset is one");
            Check(Beat(scenario, module, "d3").CommandsExecuted == 1, "two arrivals inside the delay still reach the threshold");
        }

        // A cooldown that belongs to a player is charged per player: one player's activation does not spend
        // another's, and a refused activation spends nothing.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id, instigatorPort: true);
            scenario.Gated("per-player", Id, "{\"cooldown\":10,\"scope\":\"player\"}", instigatorPort: true);
            var one = Beat(scenario, module, "p1-a", player: 1);
            Check(one.CommandsExecuted == 1, "the first activation of a player fires");
            var window = Beat(scenario, module, "p1-b", player: 1);
            Check(window.CommandsExecuted == 0 && window.GateRefusals.Single().Code == "gate-cooldown",
                "a second activation inside the same player's cooldown is refused");
            Check(Beat(scenario, module, "p2-a", player: 2).CommandsExecuted == 1, "another player is not inside that cooldown");
            // The fixture's ticks are the kernel's own: a beat is dispatched at the tick it advances to, so the
            // clock is asked about the same numbers the plan's cards were. The clock was charged in tick 0, so a
            // cooldown of ten is over in tick 10: nine ticks later is still inside the window, the tenth tick is
            // free, and the activation that fired charged the clock again (ruling 2026-09-17: `tick - charged >=
            // cooldown` releases).
            Check(Beat(scenario, module, "p1-c", 6, 1).CommandsExecuted == 0, "the ninth tick after the charge is still inside the window");
            Check(Beat(scenario, module, "p1-d", 0, 1).CommandsExecuted == 1, "the tenth tick is free: tick - charged reaches the cooldown");
            Check(Beat(scenario, module, "p1-e", 0, 1).CommandsExecuted == 0, "the activation that fired charged the clock again");
        }

        // A cooldown that belongs to the world is one clock for the whole card, whoever caused the event.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id, instigatorPort: true);
            scenario.Gated("per-level", Id, "{\"cooldown\":10,\"scope\":\"level\"}", instigatorPort: true);
            Check(Beat(scenario, module, "l1", player: 1).CommandsExecuted == 1, "the first activation fires");
            var window = Beat(scenario, module, "l2", player: 2);
            Check(window.CommandsExecuted == 0 && window.GateRefusals.Single().Code == "gate-cooldown",
                "one world clock refuses a different player's activation");
            Check(Beat(scenario, module, "l3", 8, 2).CommandsExecuted == 1, "the world clock is free on the tenth tick after it was charged and frees every player at once");
        }

        // A per-player scope books every option, not the cooldown alone: one player's threshold is their own window,
        // so the same event that fires for one player leaves another still short of it.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id, instigatorPort: true);
            scenario.Gated("windows", Id, "{\"accumulate\":2,\"scope\":\"player\"}", instigatorPort: true);
            Check(Beat(scenario, module, "w1", player: 1).CommandsExecuted == 0, "the first player's first arrival is short of their own threshold");
            Check(Beat(scenario, module, "w2", player: 2).CommandsExecuted == 0, "another player's first arrival starts their own window");
            Check(Beat(scenario, module, "w3", player: 1).CommandsExecuted == 1, "the first player's second arrival reaches their own threshold");
            Check(scenario.Kernel.GateStateOf("windows", "Entry", scenario.Player(Id, 1).Id) == (1, 0),
                "the window that fired is the first player's");
            Check(scenario.Kernel.GateStateOf("windows", "Entry", scenario.Player(Id, 2).Id) == (0, 1),
                "the other player's window is untouched by it");
        }

        // A per-instance scope books one window per mounting entity: the same card counts each enemy's hits apart,
        // and an instance whose entity is gone is dropped at the question rather than counted as a stranger's.
        {
            var scenario = new Scenario(subjectMounts: true); var module = scenario.Register(Id);
            scenario.GatedOnSubject("each", Id, "{\"accumulate\":2,\"scope\":\"instance\"}");
            var enemy = Id + ":9";
            Check(Beat(scenario, module, "i1", target: enemy).CommandsExecuted == 0, "the instance's first arrival is short of its own threshold");
            Check(Beat(scenario, module, "i2", target: Id + ":8").CommandsExecuted == 0, "another instance has its own window");
            Check(Beat(scenario, module, "i3", target: enemy).CommandsExecuted == 1, "the instance's second arrival reaches its own threshold");
            Check(scenario.Kernel.GateStateOf("each", "Entry", enemy) == (1, 0), "the instance that fired is the enemy that was hit");
            Check(scenario.Kernel.GateStateOf("each", "Entry", Id + ":8") == (0, 1), "the other instance keeps its half-reached window");
            // The entity went away between two ticks, and the counter goes with it instead of being handed to the
            // next entity that happens to carry the same id.
            scenario.Life = 2;
            Check(Beat(scenario, module, "i4", target: enemy).CommandsExecuted == 0,
                "an instance that ended is not inside a window it charged before");
            Check(scenario.Kernel.GateStateOf("each", "Entry", enemy) == (0, 1), "the dead instance's window started over");
            // A checkpoint rebuilds the level's objects, so every per-instance set is dropped with them: the count
            // is not carried back onto an entity that no longer exists.
            var checkpoint = scenario.Kernel.CaptureCheckpoint();
            scenario.Kernel.RestoreCheckpoint(checkpoint);
            Check(scenario.Kernel.GateStateOf("each", "Entry", enemy) == (0, 0),
                "a restored checkpoint drops the per-instance windows the objects went with");
            Check(Beat(scenario, module, "i5", target: enemy).CommandsExecuted == 0, "the first arrival after the restore starts over");
        }

        // An activation that accepts several entities is not one instance, so a per-instance option has no subject
        // to book against and refuses as `gate-scope` rather than silently sharing one window between them.
        {
            var scenario = new Scenario(subjectMounts: true); var module = scenario.Register(Id, manyPort: true);
            scenario.GatedOnSubject("ambiguous", Id, "{\"cooldown\":10,\"scope\":\"instance\"}");
            module.Publish(new RuntimeEvent("s1", Fixture.Trigger(Id), scenario.World, 1, "shared-scope",
                RuntimeJson.From(new Dictionary<string, object>
                {
                    ["target"] = new EntityReference(Id + ":1", scenario.World, scenario.Life),
                    ["targets"] = new[]
                    {
                        new EntityReference(Id + ":2", scenario.World, scenario.Life),
                        new EntityReference(Id + ":3", scenario.World, scenario.Life)
                    }
                })));
            var tick = scenario.Kernel.Advance(1, true);
            Check(tick.CommandsExecuted == 0 && tick.GateRefusals.Single().Code == "gate-scope",
                "an instance scope with two instances in one activation is refused by the scope");
        }

        // A roll is deterministic per entry point and plan — no host time and no entity identity enters it — and a
        // refused roll is counted, so a retry is the next draw rather than the same one again.
        {
            for (var run = 0; run < 2; run++)
            {
                var scenario = new Scenario(); var module = scenario.Register(Id);
                scenario.Gated("roll", Id, "{\"probability\":0.5}");
                // The die is the world's own, so two worlds roll the same sequence when they are begun with the
                // same seed — which is exactly what a checkpoint carries from one to the next. Two worlds with two
                // seeds are two sequences, and that is GateOptionTests' own case.
                scenario.Kernel.BeginWorld(2, 20260917); scenario.World = 2;
                var fired = 0; var refusals = 0;
                for (var i = 1; i <= 32; i++)
                {
                    var tick = Beat(scenario, module, "r" + i);
                    fired += tick.CommandsExecuted;
                    refusals += tick.GateRefusals.Count;
                }
                Check(fired + refusals == 32, "every arrival is either fired or refused by its roll");
                Check(refusals > 0 && fired > 0, "a half chance over thirty-two arrivals does both");
                if (run == 0) FirstRun = (fired, refusals); else Check((fired, refusals) == FirstRun,
                    "the same seed rolls the same sequence in a second world");
            }
        }

        // `once` is a cap of one: the entry point fires at most once however many arrivals it matches. The latch is
        // world state, so a checkpoint taken after the fire carries it; a released plan is not a counter the world
        // kept, so unloading the file drops the latch with the rest of its rows.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id);
            scenario.Gated("single", Id, "{\"once\":true}");
            Check(Beat(scenario, module, "o1").CommandsExecuted == 1, "a once entry point fires the first time");
            Check(Beat(scenario, module, "o2").CommandsExecuted == 0, "a once entry point does not fire a second time");
            var checkpoint = scenario.Kernel.CaptureCheckpoint();
            scenario.Kernel.RestoreCheckpoint(checkpoint);
            Check(scenario.Kernel.GateStateOf("single", "Entry") == (1, 0), "the latch is readable as one fire");
            Check(Beat(scenario, module, "o3").CommandsExecuted == 0, "a restored latch is still spent");
            Check(scenario.Kernel.GateStateOf("single", "Absent") == null, "an entry point with no gate has no state");
            scenario.Kernel.UnloadPlan("single");
            Check(scenario.Kernel.GateStateOf("single", "Entry") == null, "releasing a plan drops its latch");
            scenario.Gated("single", Id, "{\"once\":true}");
            Check(Beat(scenario, module, "o4").CommandsExecuted == 1, "a row that was released with its plan starts over");
        }

        // The counters are world state, so a checkpoint taken between two arrivals of a threshold resumes there.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id);
            scenario.Gated("resume", Id, "{\"accumulate\":3,\"scope\":\"level\"}");
            Beat(scenario, module, "c1");
            var checkpoint = scenario.Kernel.CaptureCheckpoint();
            Check(scenario.Kernel.GateStateCounts() == (1, 1), "a checkpointed world reports its gate rows");
            Check(Beat(scenario, module, "c2").CommandsExecuted == 0, "the second arrival is still below the threshold");
            scenario.Kernel.RestoreCheckpoint(checkpoint);
            Check(scenario.Kernel.GateStateOf("resume", "Entry") == (0, 1), "restoring puts the half-reached count back");
            Beat(scenario, module, "c3");
            Check(Beat(scenario, module, "c4").CommandsExecuted == 1, "the threshold finishes from the restored count");
        }

        // A released plan's rows are dropped with it, and a row whose plan is gone is not restored from a
        // checkpoint: a counter is only ever charged to the entry point that owns it now.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id);
            scenario.Gated("gone", Id, "{\"accumulate\":3,\"scope\":\"level\"}");
            Beat(scenario, module, "g1");
            var checkpoint = scenario.Kernel.CaptureCheckpoint();
            scenario.Kernel.UnloadPlan("gone");
            Check(scenario.Kernel.GateStateCounts() == (0, 0), "releasing a plan releases its gate rows");
            scenario.Gated("gone", Id, "{\"accumulate\":3,\"scope\":\"level\"}");
            scenario.Kernel.RestoreCheckpoint(checkpoint);
            Check(scenario.Kernel.GateStateOf("gone", "Entry") == (0, 1), "a checkpoint restores a row for the plan that owns it");
        }

        // An event that every claim of refused is reported as a refusal rather than as a plan that was never
        // loaded, and the trace row names the option that did it.
        {
            var scenario = new Scenario(); var module = scenario.Register(Id);
            scenario.Gated("trace", Id, "{\"accumulate\":2,\"scope\":\"level\"}");
            var tick = Beat(scenario, module, "x1");
            Check(tick.Events.Single().Status == "rejected" && tick.Events.Single().Code == "gate-refused",
                "a gated refusal is distinguishable from an unloaded plan");
            Check(tick.GateRefusals.Single().NodeId == "Entry" && tick.GateRefusals.Single().PlanId == "trace",
                "the refusal names the entry point and the plan");
        }

        return checks;
    }

    /// <summary>The last run's fire and refusal counts, compared against a second world rolling the same plan.</summary>
    private static (int Fired, int Refusals) FirstRun;

    /// <summary>
    /// One arrival: an event published at the world's current tick and the advance that carries the world to the
    /// next one. Time is the gate's own unit — a threshold's reset delay and a cooldown are both measured in ticks —
    /// so the clock the events arrive at is what the test says it is, and <paramref name="idle"/> is how many quiet
    /// ticks pass before the arrival. The event is published a tick early only in the sense that a publisher stamps
    /// its own message; the advance is what moves the world, and every assertion reads the tick it returned.
    ///
    /// <paramref name="port"/> names one more output the event carries, which is how an amount threshold reads the
    /// number a card accumulates: the value is the port's own, so a test can hand the gate a string or a fraction.
    /// </summary>
    private static TickResult Beat(Scenario scenario, RuntimeModuleHandle module, string id, long idle = 0, int? player = null,
        string? port = null, object? value = null, string? target = null)
    {
        var tick = scenario.Kernel.CurrentTick + 1 + idle;
        var outputs = new Dictionary<string, object> { ["target"] = new EntityReference(target ?? Id + ":1", scenario.World, scenario.Life) };
        if (player != null) outputs["instigator"] = scenario.Player(Id, player.Value);
        if (port != null) outputs[port] = value!;
        module.Publish(new RuntimeEvent(id, Fixture.Trigger(Id), scenario.World, tick,
            player == null ? "shared-scope" : "player-scope:" + id, RuntimeJson.From(outputs)));
        return scenario.Kernel.Advance(tick, true);
    }
}
