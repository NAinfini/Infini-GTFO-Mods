using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// The action card's duration options, as the kernel enforces them (plan §3.4 动作卡). What is under test is a
/// lifecycle and not a single call: the kernel casts the handle, keeps the clock and the layer count, publishes the
/// handle where the card asked for a cancel handle, and calls the module's own restore callback exactly once when
/// the instance ends — by its duration, by an explicit cancellation, by a released plan, by a life that ended or by
/// the world itself.
///
/// The state belongs to the host: an effect is opened on the authoritative side, and a replica neither runs the
/// handler nor keeps a clock. A refused application leaves a running instance exactly as it was, because a refresh a
/// command did not earn is a duration nobody asked for.
/// </summary>
internal static class EffectLifecycleTests
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

        // The block is a structural parameter of the step: a malformed one rejects the plan while it loads, and the
        // refusal names the option that is wrong rather than the step that carries it.
        foreach (var (block, code, name) in new[]
        {
            ("[]", "effect-shape", "a non-object block"),
            ("{}", "effect-duration", "a block with no duration"),
            ("{\"duration\":0}", "effect-duration", "a duration below one"),
            ("{\"duration\":1.5}", "effect-duration", "a fractional duration"),
            ("{\"duration\":5,\"reapply\":\"again\"}", "effect-reapply", "an unknown reapply"),
            ("{\"duration\":5,\"max_stacks\":2}", "effect-stacks", "a cap without a stacking reapply"),
            ("{\"duration\":5,\"reapply\":\"stack\"}", "effect-stacks", "a stacking reapply with no cap"),
            ("{\"duration\":5,\"reapply\":\"stack\",\"max_stacks\":0}", "effect-stacks", "a cap below one"),
            ("{\"duration\":5,\"cancel\":\"yes\"}", "effect-cancel", "a non-boolean cancel"),
            ("{\"duration\":5,\"interval\":0}", "effect-interval", "a period below one"),
            ("{\"duration\":5,\"interval_immediate\":true}", "effect-interval", "an immediate first run with no period"),
            ("{\"duration\":5,\"interval\":2,\"end_on\":\"staggered\"}", "effect-end-on", "an end_on that is not an array"),
            ("{\"duration\":5,\"interval\":2,\"end_on\":[\"example.alpha\"]}", "effect-end-on", "an end_on that names an action"),
            ("{\"duration\":5,\"interval\":2,\"end_on\":[\"example.ghost\"]}", "effect-end-on", "an end_on that names nothing"),
            ("{\"duration\":5,\"refresh\":true}", "unknown-field", "an unknown option")
        })
        {
            var scenario = new Scenario();
            scenario.Register(Id, effectPort: true, restore: _ => { });
            RejectCode(() => scenario.Plan("bad", Id, effectPort: true, effect: block), code, name);
        }

        // An effect is only loadable where the module registered the callback that ends it, and a cancel handle is
        // only loadable where the row declares the port it is published into: a duration nothing can undo, and a
        // handle that goes nowhere, are both refused at load rather than discovered at runtime. The one action that
        // carries no callback is the one that only runs once per period: nothing it writes outlives it.
        {
            var noRestore = new Scenario();
            var module = noRestore.Register(Id, effectPort: true);
            RejectCode(() => noRestore.Plan("unsupported", Id, effectPort: true, effect: "{\"duration\":5}"),
                "effect-unsupported", "a row whose module registered no restore callback");
            RejectCode(() => noRestore.Plan("unsupported", Id, effectPort: true, effect: "{\"duration\":5,\"end_on\":[]}"),
                "effect-unsupported", "an end_on on a row whose module registered no restore callback");
            noRestore.Plan("periodic", Id, effectPort: true, effect: "{\"duration\":5,\"interval\":2}");
            Check(Beat(noRestore, module, "p0").CommandsExecuted == 1, "a periodic action loads without a restore callback");

            var noPort = new Scenario();
            noPort.Register(Id, restore: _ => { });
            RejectCode(() => noPort.Plan("handle", Id, effect: "{\"duration\":5,\"cancel\":true}"), "effect-handle",
                "a cancel handle on a row that declares no handle output");
        }

        // The published handle is the module's own cancellation path: the kernel ends the instance through the same
        // restore callback, with the cancellation named, and the handle is spent by it.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context));
            scenario.Plan("cancel", Id, effectPort: true, effect: "{\"duration\":50,\"cancel\":true}");
            var tick = Beat(scenario, module, "c1");
            var handle = TheHandle(tick);
            Check(tick.CommandsExecuted == 1 && handle != null, "the step published the cancel handle its card asked for");
            Check(scenario.Kernel.IsHandleLive(module, handle!.Value), "the published handle is a live effect handle");
            var cancelled = scenario.Kernel.CancelHandle(module, handle.Value, Fixture.EffectPort(), "cancel");
            Check(cancelled == 1 && ends.Count == 1 && ends[0].Reason == EffectEndReasons.Cancelled,
                "the module's own cancellation ends the instance through the restore callback");
            Check(!scenario.Kernel.IsHandleLive(module, handle.Value), "the cancelled handle is spent");
            scenario.Kernel.Advance(scenario.Kernel.CurrentTick + 100, true);
            Check(ends.Count == 1, "an instance that already ended is not ended a second time");
        }

        // A duration is measured from the tick the application was accepted in, and the ending is the same
        // comparison the trigger card's cooldown uses: an effect of five applied in tick T is over in tick T+5.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context));
            scenario.Plan("expiry", Id, effectPort: true, effect: "{\"duration\":5}");
            Check(Beat(scenario, module, "x1").CommandsExecuted == 1, "the effect is applied in tick zero");
            scenario.Kernel.Advance(4, true);
            Check(ends.Count == 0, "the effect is still running one tick short of its duration");
            scenario.Kernel.Advance(5, true);
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Expired && ends[0].Stacks == 1,
                "the effect ends as expired on the tick its duration runs out");
            Check(ends[0].NodeId == "Action0" && ends[0].CommandId.Length > 0 && ends[0].PlanId == "expiry",
                "the ending names the plan, the card and the command that applied it");
            Check(ends[0].Subject.Count == 1 && ends[0].Subject[0].Id == Id + ":1",
                "the ending carries the recipients the application was about");
        }

        // A second application of the same card about the same recipients refreshes the one instance: the duration
        // restarts and the module is called back once, at the end of the refreshed window.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context));
            scenario.Plan("refresh", Id, effectPort: true, effect: "{\"duration\":5,\"reapply\":\"refresh\"}");
            Beat(scenario, module, "r1");
            scenario.Kernel.Advance(3, true);
            Check(ends.Count == 0, "the first window is open in tick three");
            Check(Beat(scenario, module, "r2").CommandsExecuted == 1, "the refresh is dispatched inside the window");
            scenario.Kernel.Advance(8, true);
            Check(ends.Count == 0, "the refreshed window is still open where the first one would have ended");
            scenario.Kernel.Advance(9, true);
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Expired && ends[0].Stacks == 1,
                "one instance, one ending, one layer");
        }

        // Two applications of one card about different recipients are two instances with two clocks: a refresh is
        // about the same subject, never about the card.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context));
            scenario.Plan("subject", Id, effectPort: true, effect: "{\"duration\":5}");
            Beat(scenario, module, "t1", target: Id + ":1");
            Beat(scenario, module, "over", target: Id + ":1");
            Beat(scenario, module, "t2", target: Id + ":2");
            scenario.Kernel.Advance(5, true);
            Check(ends.Count == 0, "both instances are still running where the first application's own window would have ended");
            scenario.Kernel.Advance(6, true);
            Check(ends.Count == 1 && ends[0].Subject[0].Id == Id + ":1",
                "the instance the second application refreshed ends on its refreshed clock");
            scenario.Kernel.Advance(7, true);
            Check(ends.Count == 2 && ends[1].Reason == EffectEndReasons.Expired && ends[1].Subject[0].Id == Id + ":2",
                "each recipient's instance ends on its own clock");
        }

        // A stacking reapply adds a layer and restarts the duration; at the cap a further application only
        // refreshes, and the ending reports the layers the module has to undo.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var layers = new List<(int? Stacks, bool AddLayer)>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context),
                handler: context =>
                {
                    layers.Add((context.EffectStacks, context.EffectAddLayer));
                    return CommandResult.Succeeded(RuntimeJson.EmptyObject);
                });
            scenario.Plan("stack", Id, effectPort: true, effect: "{\"duration\":5,\"reapply\":\"stack\",\"max_stacks\":2}");
            Beat(scenario, module, "s1");
            Beat(scenario, module, "s2");
            Beat(scenario, module, "s3");
            Check(layers.Count == 3 && layers[0] == (1, true) && layers[1] == (2, true),
                "the first two applications add one layer each and are told the count they leave behind");
            Check(layers[2] == (2, false), "an application at the cap refreshes instead of adding a third layer");
            Check(scenario.Kernel.Advance(7, true).CommandsExecuted == 0, "the instance outlives its first window");
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Expired && ends[0].Stacks == 2,
                "the ending reports the two layers the module wrote");
        }

        // A refused application leaves the running instance alone: the clock is not restarted and the layer count
        // does not move, while a refused first application leaves no handle and nothing to undo.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var refuse = false;
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context),
                handler: _ => refuse ? CommandResult.Rejected("example-refused") : CommandResult.Succeeded(RuntimeJson.EmptyObject));
            scenario.Plan("refused", Id, effectPort: true, effect: "{\"duration\":5}");
            Beat(scenario, module, "f1");
            refuse = true;
            scenario.Kernel.Advance(3, true);
            Beat(scenario, module, "f2");
            scenario.Kernel.Advance(5, true);
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Expired,
                "a refused refresh did not restart the clock: the original duration still ends it");
        }
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context),
                handler: _ => CommandResult.Rejected("example-refused"));
            scenario.Plan("first-refused", Id, effectPort: true, effect: "{\"duration\":5,\"cancel\":true}");
            var refused = Beat(scenario, module, "g1");
            Check(refused.CommandsExecuted == 1 && TheHandle(refused) == null, "a refused first application publishes no handle");
            scenario.Kernel.Advance(50, true);
            Check(ends.Count == 0, "an application that never happened has nothing to undo");
        }

        // A life that ended, a released plan, a new world and an unregistering module all end the instance the same
        // way: the module is called back before the handles it names are gone.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context));
            scenario.Plan("life", Id, effectPort: true, effect: "{\"duration\":50}");
            Beat(scenario, module, "l1");
            scenario.Life = 2;
            scenario.Kernel.Advance(1, true);
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Reclaimed,
                "an effect whose recipients are gone is reclaimed");
        }
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context));
            scenario.Plan("unloaded", Id, effectPort: true, effect: "{\"duration\":50}");
            Beat(scenario, module, "u1");
            Check(scenario.Kernel.UnloadPlan("unloaded"), "the plan unloads");
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Reclaimed,
                "a released plan's effects are undone with it");
        }
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context));
            scenario.Plan("world", Id, effectPort: true, effect: "{\"duration\":50}");
            Beat(scenario, module, "w1");
            scenario.Kernel.BeginWorld(2);
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Reclaimed && ends[0].WorldEpoch == 1,
                "a new world undoes the effects of the old one before its handles go");
        }
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context));
            scenario.Plan("module", Id, effectPort: true, effect: "{\"duration\":50}");
            Beat(scenario, module, "m1");
            module.Dispose();
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Reclaimed,
                "an unregistering module is called back before its registration goes");
        }

        // A period re-runs the action inside the kernel's own advance: the frame the card was applied with is run
        // again every interval, the first run being the tick the card asked for, and the pulses stop with the
        // duration. A pulse is not an event: nothing is dispatched and no entry point is entered again.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var runs = new List<long>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context),
                handler: context => { runs.Add(context.SimulationTick); return CommandResult.Succeeded(RuntimeJson.EmptyObject); });
            scenario.Plan("period", Id, effectPort: true, effect: "{\"duration\":10,\"interval\":3,\"interval_immediate\":true}");
            Check(Beat(scenario, module, "q0").CommandsExecuted == 1, "the application is its own immediate first run");
            Check(runs.Count == 1 && runs[0] == 0, "the first run happens in the tick the card was applied in");
            Check(scenario.Kernel.Advance(2, true).Commands.Count == 0, "the period is quiet before its next tick");
            var first = scenario.Kernel.Advance(3, true);
            Check(first.Commands.Count(c => c.CommandId.Contains(",pulse:")) == 1 && runs[^1] == 3,
                "the next run is one interval after the first");
            var second = scenario.Kernel.Advance(6, true);
            Check(second.Commands.Count(c => c.CommandId.Contains(",pulse:")) == 1 && runs[^1] == 6, "and again one interval later");
            // A period that was slept through is skipped rather than replayed: one run, and the next one is one
            // interval after it instead of the ones that were missed.
            var skipped = scenario.Kernel.Advance(20, true);
            Check(skipped.Commands.Count(c => c.CommandId.Contains(",pulse:")) == 1,
                "a skipped period runs once, not once per missed tick");
            Check(skipped.CommandsExecuted == 0 && runs[^1] == 20,
                "the run the world slept through is a pulse of the tick that noticed it, not a dispatched event");
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Expired && ends[0].Stacks == 1,
                "the pulses stop with the duration and the instance ends once");
        }

        // A `stack_independent` reapply gives every layer a clock of its own: each layer ends by itself and is
        // restored by itself, where a shared clock ends the whole stack at once.
        {
            var scenario = new Scenario();
            var ended = new List<int>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ended.Add(context.Stacks));
            scenario.Plan("shared", Id, effectPort: true, effect: "{\"duration\":5,\"reapply\":\"stack\",\"max_stacks\":2}");
            Beat(scenario, module, "c1");
            scenario.Kernel.Advance(3, true);
            Beat(scenario, module, "c2");
            scenario.Kernel.Advance(8, true);
            Check(ended.Count == 0 && scenario.Kernel.LiveEffects == 1, "the shared clock keeps every layer alive");
            scenario.Kernel.Advance(9, true);
            Check(ended.SequenceEqual(new[] { 2 }), "the shared clock ends both layers in one callback");
        }
        {
            var scenario = new Scenario();
            var ended = new List<int>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ended.Add(context.Stacks));
            scenario.Plan("independent", Id, effectPort: true,
                effect: "{\"duration\":5,\"reapply\":\"stack_independent\",\"max_stacks\":2}");
            Beat(scenario, module, "d1");
            scenario.Kernel.Advance(3, true);
            Beat(scenario, module, "d2");
            scenario.Kernel.Advance(5, true);
            Check(ended.SequenceEqual(new[] { 1 }), "the second layer is one clock behind the first");
            Check(scenario.Kernel.LiveEffects == 1, "the instance is still running while a layer is left");
            scenario.Kernel.Advance(9, true);
            Check(ended.SequenceEqual(new[] { 1, 1 }), "the second layer is restored on its own clock too");
            Check(scenario.Kernel.LiveEffects == 0, "the instance is forgotten once its last layer is undone");
        }

        // An `end_on` ability ends the effect early: the receiver's own event ends it through the same restore
        // callback, and the ending is not a cancellation the author did not ask for.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context), endOn: true);
            scenario.EndOnPlan("ended", Id, "{\"duration\":50,\"end_on\":[\"" + Fixture.EndTriggerCapability(Id) + "\"]}");
            Check(Beat(scenario, module, "e1").CommandsExecuted == 1, "the effect is applied");
            Check(scenario.Kernel.LiveEffects == 1, "the effect is running before the receiver's own event");
            module.Publish(new RuntimeEvent("drop", Fixture.EndTrigger(Id), scenario.World, scenario.Kernel.CurrentTick + 1, "shared-scope",
                RuntimeJson.From(new Dictionary<string, object> { ["target"] = new EntityReference(Id + ":7", scenario.World, scenario.Life) })));
            var ended = scenario.Kernel.Advance(scenario.Kernel.CurrentTick + 1, true);
            Check(ends.Count == 1 && ends[0].Reason == EffectEndReasons.Expired, "the receiver's own event ends the effect as expired");
            Check(ends[0].Subject.Single().Id == Id + ":1",
                "the callback names the entity the effect was applied to, which is what the module has to restore");
            Check(ended.Commands.Count(c => c.BindingId == Id + ".binding.apply") == 0,
                "the ending itself dispatches no action of the card");
            Check(ended.Events.Any(e => e.EventId == "drop" && e.Status == "ignored" && e.Code == "no-consumer"),
                "the receiver's event is one no card consumed, and it still ended the effect");
        }

        // The same card on a receiver the ability cannot be about never ends: the event names another kind, so the
        // instance lives out its own duration.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context), endOn: true);
            scenario.EndOnPlan("kept", Id, "{\"duration\":50,\"end_on\":[\"" + Fixture.EndTriggerCapability(Id) + "\"]}");
            Beat(scenario, module, "k1");
            module.Publish(new RuntimeEvent("elsewhere", Fixture.EndTrigger(Id), scenario.World, scenario.Kernel.CurrentTick + 1, "shared-scope",
                RuntimeJson.From(new Dictionary<string, object> { ["target"] = new EntityReference("example.other:1", scenario.World, scenario.Life) })));
            scenario.Kernel.Advance(scenario.Kernel.CurrentTick + 1, true);
            Check(ends.Count == 0 && scenario.Kernel.LiveEffects == 1, "an event about another entity does not end the effect");
        }

        // An effect is a host decision like every other: a replica runs no handler and opens no effect, and a
        // module's restore callback is never reached by a tick the replica decided.
        {
            var scenario = new Scenario();
            var ends = new List<RuntimeEffectContext>();
            var module = scenario.Register(Id, effectPort: true, restore: context => ends.Add(context));
            scenario.Plan("replica", Id, effectPort: true, effect: "{\"duration\":5}");
            Publish(scenario, module, "replica", 0, Id + ":1");
            var advance = scenario.Kernel.Advance(0, false);
            Check(advance.CommandsExecuted == 0 && advance.Events.Single().Code == "not-host",
                "the replica refuses its own advance as not-host");
            // The world's authority is decided once per world, so the replica's own advance is the last one this
            // scenario may ask for: what it proves is that no handler ran and no clock was opened on this side.
            Check(ends.Count == 0, "the replica opened no effect of its own");
        }

        return checks;
    }

    /// <summary>One application of the fixture's action card, at the next tick after the idle gap the test asked
    /// for, about the recipient it named. The event carries the target the plan's own `target` input reads.</summary>
    private static TickResult Beat(Scenario scenario, RuntimeModuleHandle module, string id, long idle = 0, string? target = null)
    {
        var tick = scenario.Kernel.CurrentTick + 1 + idle;
        Publish(scenario, module, id, tick, target ?? Id + ":1");
        return scenario.Kernel.Advance(tick, true);
    }

    private static void Publish(Scenario scenario, RuntimeModuleHandle module, string id, long tick, string target)
        => module.Publish(new RuntimeEvent(id, Fixture.Trigger(Id), scenario.World, tick, "shared-scope",
            RuntimeJson.From(new Dictionary<string, object>
            {
                ["target"] = new EntityReference(target, scenario.World, scenario.Life)
            })));

    /// <summary>The effect handle one dispatch published, read back from the command receipt's own result: the
    /// handle port is the capability's, and the fixture's own is named `effect`.</summary>
    private static JsonElement? TheHandle(TickResult tick)
    {
        foreach (var receipt in tick.Commands)
        {
            var outputs = receipt.Result.Outputs;
            if (outputs.ValueKind == JsonValueKind.Object && outputs.TryGetProperty("effect", out var handle)) return handle;
        }
        return null;
    }
}
