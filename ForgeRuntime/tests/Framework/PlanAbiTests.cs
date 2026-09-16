using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

/// <summary>
/// Plan-ABI checks that need a registry of their own: the `query` tier, the seven first-step controls and their
/// continuations, handles, mount targets and the result-row declaration. Every case builds its plan the way the
/// compiler does — bindings pinned by index, dense port slots, successors in execution-output order — and asserts
/// both the positive path (what ran, in what order) and the refusal code when the shape is wrong.
/// </summary>
internal static class PlanAbiTests
{
    private const string Id = "example.abi";
    private static readonly string[] NoPermissions = Array.Empty<string>();

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

        // query: a world read feeds an action through the query step's own frame.
        {
            var h = new Harness();
            h.Load(h.Plan("query-chain", new[]
            {
                h.Step("S0_observe", "query", h.ObserveBinding, h.ObserveContract, Array.Empty<object>(),
                    new object[] { FromEvent(h.Port(h.ObserveContract, "inputs", "targets"), h.TriggerPort("outputs", "targets")) }, Array.Empty<int?>()),
                h.Step("S1_apply", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    new object[] { FromEvent(h.Port(h.ActionContract, "inputs", "target"), h.TriggerPort("outputs", "target")),
                                   FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.ObserveContract, "outputs", "hits")) }, new int?[] { null })
            }, start: 1));
            h.Publish("q1");
            var tick = h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_apply:" + Entity(1) + ":1" }), "a query step's observation reaches the action that reads it [" + string.Join("|", h.Applied) + "]");
            Check(h.Queries == 1 && tick.Commands.All(c => c.Result.Status == "succeeded"), "the query step is evaluated once per activation");
        }
        // query: an exhausted read budget rejects the step instead of answering with a short set.
        {
            var h = new Harness();
            h.Load(h.Plan("query-budget", new[]
            {
                h.Step("S0_observe", "query", h.ObserveBinding, h.ObserveContract, Array.Empty<object>(),
                    new object[] { FromEvent(h.Port(h.ObserveContract, "inputs", "targets"), h.TriggerPort("outputs", "targets")) }, Array.Empty<int?>()),
                h.Step("S1_apply", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    new object[] { FromEvent(h.Port(h.ActionContract, "inputs", "target"), h.TriggerPort("outputs", "target")),
                                   FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.ObserveContract, "outputs", "hits")) }, new int?[] { null })
            }, start: 1));
            h.Publish("query-budget", targets: Enumerable.Range(1, RuntimeKernel.MaximumEntityQueriesPerTick + 1).Select(Entity).ToArray());
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "rejected"
                && tick.Commands[0].Result.Code == RuntimeAbiCodes.QueryBudget,
                "a query past the tick's read budget is refused as query-budget ["
                + string.Join("|", tick.Commands.Select(c => c.Result.Status + ":" + c.Result.Code)) + "]");
            Check(h.Applied.Count == 0, "the consumer of a refused query never sees a shorter candidate set");
        }
        // query: the tier itself is a registration fact, not a plan one.
        {
            RejectCode(() => Probe("pure", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"entity\"}"),
                RuntimeAbiCodes.PureWorldPort, "a pure observation cannot declare an entity port");
            RejectCode(() => Probe("query", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"boolean\"}"),
                RuntimeAbiCodes.QueryAuthority, "a value-only observation cannot claim the query tier");
            RejectCode(() => Probe("pure", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"execution\"}"),
                "pure-execution", "a pure observation carries no execution port");
            Probe("pure", "{\"id\":\"left\",\"type\":\"number\"}", "{\"id\":\"value\",\"type\":\"boolean\"}");
            Check(true, "a value-only observation registers as pure");
            Probe("query", "{\"id\":\"target\",\"type\":\"entity\"}", "{\"id\":\"value\",\"type\":\"entity\"}");
            Check(true, "an observation with a world port registers as query");
        }
        // query: a plan cannot run an observation outside its tier.
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.Plan("wrong-tier", new[]
            {
                h.Step("S0_observe", "pure", h.ObserveBinding, h.ObserveContract, Array.Empty<object>(), Array.Empty<object>(), Array.Empty<int?>()),
                h.Step("S1_apply", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }, start: 1)), "node-domain-authority", "a plan step's nodeKind must be the capability's own tier");
        }
        // action: a declared output is a value a later step reads, the same way a control's is. The action's own
        // result row stays a row: the port that carries it is refused by the rule every movable port is judged by.
        {
            var h = new Harness();
            h.Load(h.Plan("action-output", new[]
            {
                h.Step("S0_publish", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { 1 }),
                h.Step("S1_read", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.ActionContract, "outputs", "count"))), new int?[] { null })
            }));
            h.Publish("action-output");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S0_publish:" + Entity(1) + ":-", "S1_read:" + Entity(1) + ":7" }),
                "an action's declared output reaches the step that reads it [" + string.Join("|", h.Applied) + "]");
        }
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.Plan("reads-action-row", new[]
            {
                h.Step("S0_apply", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    new object[] { FromEvent(h.Port(h.ActionContract, "inputs", "target"), h.TriggerPort("outputs", "target")) }, new int?[] { 1 }),
                h.Step("S1_observe", "query", h.ObserveBinding, h.ObserveContract, Array.Empty<object>(),
                    new object[] { FromStep(h.Port(h.ObserveContract, "inputs", "targets"), 0, h.Port(h.ActionContract, "outputs", "result")) }, Array.Empty<int?>())
            })), "unsupported-input-port", "an action's result row is not a value a later step reads");
        }
        // variable: a declared read answers with the declaration's own initial value and answers non-null, so a
        // plan may hand it straight to a required input. `false` is the authored initial, so a read that answered
        // anything else — or nothing — could not take the branch it takes here.
        {
            var h = new Harness();
            h.Load(h.Plan("read-required", new[]
            {
                h.Step("S0_read", "control", h.ReadBinding, h.ReadFlagContract, new object[] { Harness.ReadVariable, 1 }, Array.Empty<object>(), new int?[] { 1 }),
                h.Step("S1_branch", "control", h.BranchBinding, h.BranchContract, Array.Empty<object>(),
                    new object[] { FromStep(h.Port(h.BranchContract, "inputs", "condition"), 0, h.Port(h.ReadFlagContract, "outputs", "value")) }, new int?[] { 2, 3 }),
                h.Step("S2_then", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S3_else", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }, variables: new object[] { new { id = Harness.ReadVariable, scope = "level", type = "flag", initial = false } }));
            h.Publish("read-required");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S3_else:" + Entity(1) + ":-" }),
                "a declared read is a non-null value the plan hands to a required input [" + string.Join("|", h.Applied) + "]");
        }
        // present: one value, two ways out, and the value itself is a value only where the step proved it is there.
        {
            var h = new Harness();
            h.Load(h.Plan("present-branch", new[]
            {
                h.Step("S0_present", "control", h.PresentBinding, h.PresentContract, new object[] { 1 },
                    new object[] { FromEvent(h.Port(h.PresentContract, "inputs", "value"), h.TriggerPort("outputs", "maybe")) }, new int?[] { 1, 2 }),
                h.Step("S1_guarded", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.PresentContract, "outputs", "value"))), new int?[] { null }),
                h.Step("S2_missing", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }));
            h.Publish("present-missing");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S2_missing:" + Entity(1) + ":-" }),
                "a value that is not there leaves through missing [" + string.Join("|", h.Applied) + "]");
            h.Applied.Clear();
            h.Publish("present-value", 2, null, maybe: 4);
            h.Kernel.Advance(2, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_guarded:" + Entity(1) + ":4" }),
                "the present branch reads the value the step tested [" + string.Join("|", h.Applied) + "]");
        }
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.Plan("guarded-outside", new[]
            {
                h.Step("S0_present", "control", h.PresentBinding, h.PresentContract, new object[] { 1 },
                    new object[] { FromEvent(h.Port(h.PresentContract, "inputs", "value"), h.TriggerPort("outputs", "maybe")) }, new int?[] { 1, 2 }),
                h.Step("S1_guarded", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.PresentContract, "outputs", "value"))), new int?[] { null }),
                h.Step("S2_outside", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.PresentContract, "outputs", "value"))), new int?[] { null })
            })), "present-branch", "a guarded value is refused outside the branch that proved it is there");
        }
        // Ruling 158.2: the region is dominance, not forward reachability. Two paths that meet again after the
        // guard leave a way in that never tested the value, so the step they meet at may not read it.
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.Plan("guarded-after-merge", new[]
            {
                h.Step("S0_present", "control", h.PresentBinding, h.PresentContract, new object[] { 1 },
                    new object[] { FromEvent(h.Port(h.PresentContract, "inputs", "value"), h.TriggerPort("outputs", "maybe")) }, new int?[] { 1, 2 }),
                h.Step("S1_guarded", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.PresentContract, "outputs", "value"))), new int?[] { 3 }),
                h.Step("S2_missing", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { 3 }),
                h.Step("S3_merged", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.PresentContract, "outputs", "value"))), new int?[] { null })
            })), "present-branch", "a guarded value is refused after the present and missing paths meet again");
        }
        // Ruling 158.4: a handle member spells both halves of a handle's contract, so a guarded wave handle
        // resolves to the same `effect`/`encounter` pair the action that stops the wave declares — and the pair is
        // the kind's own, never one the member leaves open (rule 142.1).
        {
            var h = new Harness();
            var guardedHandle = h.Contract("forge.control.flow.present", RuntimeJson.From(new { value_type = "handle:effect" }));
            var tested = RuntimeJson.Rows(guardedHandle, "inputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            var forwarded = RuntimeJson.Rows(guardedHandle, "outputs").Single(p => RuntimeJson.Text(p, "id") == "value");
            Check(RuntimeJson.Text(tested, "type") == "handle" && RuntimeJson.Text(tested, "handleKind") == "effect"
                && RuntimeJson.Text(tested, "lifetime") == "encounter" && RuntimeJson.Flag(tested, "nullable"),
                "a guarded handle member carries its kind, its lifetime and the fact it may be absent");
            Check(RuntimeGraphContracts.ValueTypeMatches(tested, forwarded) && RuntimeJson.Flag(forwarded, "nullable"),
                "the forwarded handle is the value that was tested");
            Check(RuntimeGraphContracts.IsValueTypeMember("handle:effect") && !RuntimeGraphContracts.IsValueTypeMember("handle:audio")
                && !RuntimeGraphContracts.IsValueTypeMember("audio"),
                "only the handle kinds a value may be missing in are guard members");
        }
        // branch: one execution output per outcome, in declaration order.
        foreach (var condition in new[] { true, false })
        {
            var h = new Harness();
            h.Load(h.Plan("branch-" + condition, new[]
            {
                h.Step("S0_branch", "control", h.BranchBinding, h.BranchContract, Array.Empty<object>(),
                    new object[] { new { slot = h.Port(h.BranchContract, "inputs", "condition"), value = condition } }, new int?[] { 1, 2 }),
                h.Step("S1_then", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S2_else", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }));
            h.Publish("branch");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { condition ? "S1_then:" + Entity(1) + ":-" : "S2_else:" + Entity(1) + ":-" }), "branch follows the condition's own output");
        }
        // sequence: depth first, one exit after the other, in output order.
        {
            var h = new Harness();
            h.Load(h.Plan("sequence", new[]
            {
                h.Step("S0_sequence", "control", h.SequenceBinding, h.SequenceContract, new object[] { 2 }, Array.Empty<object>(), new int?[] { 1, 2 }),
                h.Step("S1_first", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S2_second", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }));
            h.Publish("sequence");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_first:" + Entity(1) + ":-", "S2_second:" + Entity(1) + ":-" }), "sequence walks every exit in order");
        }
        // delay: the timer is written on entry and `next` only runs once it fires.
        {
            var h = new Harness();
            h.Load(h.Plan("delay", new[]
            {
                h.Step("S0_delay", "control", h.DelayBinding, h.DelayContract, Array.Empty<object>(),
                    new object[] { new { slot = h.Port(h.DelayContract, "inputs", "duration"), value = 2 } }, new int?[] { 1 }),
                h.Step("S1_after", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }));
            h.Publish("delay");
            h.Kernel.Advance(1, true);
            Check(h.Applied.Count == 0, "delay writes its timer and enters nothing yet");
            h.Kernel.Advance(2, true);
            Check(h.Applied.Count == 0, "delay waits for its own interval");
            h.Kernel.Advance(3, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_after:" + Entity(1) + ":-" }), "delay enters next when the timer fires");
            Check(h.Kernel.Advance(9, true).CommandsExecuted == 0, "a one-pulse continuation never fires twice");
        }
        // delay: a new world drops every continuation it held.
        {
            var h = new Harness();
            h.Load(h.Plan("delay-world", new[]
            {
                h.Step("S0_delay", "control", h.DelayBinding, h.DelayContract, Array.Empty<object>(),
                    new object[] { new { slot = h.Port(h.DelayContract, "inputs", "duration"), value = 2 } }, new int?[] { 1 }),
                h.Step("S1_after", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }));
            h.Publish("delay-world");
            h.Kernel.Advance(1, true);
            h.Kernel.BeginWorld(2); h.World = 2;
            h.Kernel.Advance(3, true);
            Check(h.Applied.Count == 0, "BeginWorld clears every continuation");
        }
        // interval: next runs now, every pulse re-enters the pulse region, and the count ends the schedule.
        {
            var h = new Harness();
            h.Load(h.Plan("interval", new[]
            {
                h.Step("S0_interval", "control", h.IntervalBinding, h.IntervalContract, new object[] { 0 },
                    new object[] { new { slot = h.Port(h.IntervalContract, "inputs", "interval"), value = 1 },
                                   new { slot = h.Port(h.IntervalContract, "inputs", "count"), value = 2 } }, new int?[] { 1, 3 }),
                h.Step("S1_next", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S2_observe", "query", h.ObserveBinding, h.ObserveContract, Array.Empty<object>(),
                    new object[] { FromEvent(h.Port(h.ObserveContract, "inputs", "targets"), h.TriggerPort("outputs", "targets")) }, Array.Empty<int?>()),
                h.Step("S3_pulse", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    new object[] { FromEvent(h.Port(h.ActionContract, "inputs", "target"), h.TriggerPort("outputs", "target")),
                                   FromStep(h.Port(h.ActionContract, "inputs", "steps"), 2, h.Port(h.ObserveContract, "outputs", "hits")) }, new int?[] { null })
            }));
            h.Publish("interval");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S1_next:" + Entity(1) + ":-", "S3_pulse:" + Entity(1) + ":1" }) && h.Queries == 1,
                "interval enters next immediately and runs the first pulse in the same tick");
            h.Kernel.Advance(2, true);
            Check(h.Queries == 2 && h.Applied.Count == 3, "every pulse re-evaluates the query step");
            h.Kernel.Advance(3, true);
            Check(h.Applied.Count == 3 && h.Kernel.Advance(9, true).CommandsExecuted == 0, "interval stops after its declared pulse count");
        }
        // interval: an omitted `count` is an unbounded series — only cancellation, the scope or the world ends it
        // (ruling 160.2). Its plan input row is absent, which is what the optional port admits.
        {
            var h = new Harness();
            h.Load(h.Plan("interval-unbounded", new[]
            {
                h.Step("S0_interval", "control", h.IntervalBinding, h.IntervalContract, new object[] { 0 },
                    new object[] { new { slot = h.Port(h.IntervalContract, "inputs", "interval"), value = 1 } }, new int?[] { 1, 3 }),
                h.Step("S1_next", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S2_observe", "query", h.ObserveBinding, h.ObserveContract, Array.Empty<object>(),
                    new object[] { FromEvent(h.Port(h.ObserveContract, "inputs", "targets"), h.TriggerPort("outputs", "targets")) }, Array.Empty<int?>()),
                h.Step("S3_pulse", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    new object[] { FromEvent(h.Port(h.ActionContract, "inputs", "target"), h.TriggerPort("outputs", "target")),
                                   FromStep(h.Port(h.ActionContract, "inputs", "steps"), 2, h.Port(h.ObserveContract, "outputs", "hits")) }, new int?[] { null })
            }));
            h.Publish("interval-unbounded");
            h.Kernel.Advance(1, true);
            h.Kernel.Advance(2, true); h.Kernel.Advance(3, true); h.Kernel.Advance(4, true);
            Check(h.Applied.Count == 5 && h.Queries == 4,
                "an interval without a count pulses every interval instead of stopping at a count it never declared");
            var before = h.Applied.Count;
            h.Kernel.BeginWorld(2); h.World = 2;
            Check(h.Kernel.Advance(5, true).CommandsExecuted == 0 && h.Applied.Count == before,
                "the end of the world ends an interval that declared no count");
        }
        // repeat: the body runs once per round with its own `index`, then `next` follows.
        {
            var h = new Harness();
            h.Load(h.Plan("repeat", new[]
            {
                h.Step("S0_repeat", "control", h.RepeatBinding, h.RepeatContract, Array.Empty<object>(),
                    new object[] { new { slot = h.Port(h.RepeatContract, "inputs", "count"), value = 3 } }, new int?[] { 1, 2 }),
                h.Step("S1_next", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S2_body", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.RepeatContract, "outputs", "index"))), new int?[] { null })
            }));
            h.Publish("repeat");
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S2_body:" + Entity(1) + ":0", "S2_body:" + Entity(1) + ":1", "S2_body:" + Entity(1) + ":2", "S1_next:" + Entity(1) + ":-" }),
                "repeat runs one body per round and then enters next");
        }
        // repeat: the round ceiling is a budget, not a truncation.
        {
            var h = new Harness(new RuntimeLimits { MaxControlIterations = 2 });
            h.Load(h.Plan("repeat-budget", new[]
            {
                h.Step("S0_repeat", "control", h.RepeatBinding, h.RepeatContract, Array.Empty<object>(),
                    new object[] { new { slot = h.Port(h.RepeatContract, "inputs", "count"), value = 3 } }, new int?[] { 1, 2 }),
                h.Step("S1_next", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S2_body", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(FromStep(h.Port(h.ActionContract, "inputs", "steps"), 0, h.Port(h.RepeatContract, "outputs", "index"))), new int?[] { null })
            }));
            h.Publish("repeat-budget");
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.IterationBudget),
                "a loop past its round ceiling is refused by name");
            Check(h.Applied.Count == 0, "a loop past its round ceiling is refused whole, not truncated to the ceiling");
        }
        // for_each: one round per candidate, with `item` and `index` written for the body.
        {
            var h = new Harness();
            h.Load(h.Plan("for-each", new[]
            {
                h.Step("S0_observe", "query", h.ObserveBinding, h.ObserveContract, Array.Empty<object>(),
                    new object[] { FromEvent(h.Port(h.ObserveContract, "inputs", "targets"), h.TriggerPort("outputs", "targets")) }, Array.Empty<int?>()),
                h.Step("S1_for_each", "control", h.ForEachBinding, h.ForEachContract, Array.Empty<object>(),
                    new object[] { FromStep(h.Port(h.ForEachContract, "inputs", "candidates"), 0, h.Port(h.ObserveContract, "outputs", "seen_many")),
                                   new { slot = h.Port(h.ForEachContract, "inputs", "budget"), value = 4 } }, new int?[] { 2, 3 }),
                h.Step("S2_next", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S3_body", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    new object[] { FromStep(h.Port(h.ActionContract, "inputs", "target"), 1, h.Port(h.ForEachContract, "outputs", "item")),
                                   FromStep(h.Port(h.ActionContract, "inputs", "steps"), 1, h.Port(h.ForEachContract, "outputs", "index")) }, new int?[] { null })
            }, start: 1));
            h.Publish("for-each", targets: new[] { Entity(1), Entity(2) });
            h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S3_body:" + Entity(1) + ":0", "S3_body:" + Entity(2) + ":1", "S2_next:" + Entity(1) + ":-" }),
                "for_each walks the candidate set in order and then enters next");
            Check(h.Queries == 2, "the candidate set is one query, not one per round");
        }
        // for_each: a candidate set larger than the declared budget is refused, never truncated.
        {
            var h = new Harness();
            h.Load(h.Plan("for-each-budget", new[]
            {
                h.Step("S0_observe", "query", h.ObserveBinding, h.ObserveContract, Array.Empty<object>(),
                    new object[] { FromEvent(h.Port(h.ObserveContract, "inputs", "targets"), h.TriggerPort("outputs", "targets")) }, Array.Empty<int?>()),
                h.Step("S1_for_each", "control", h.ForEachBinding, h.ForEachContract, Array.Empty<object>(),
                    new object[] { FromStep(h.Port(h.ForEachContract, "inputs", "candidates"), 0, h.Port(h.ObserveContract, "outputs", "seen_many")),
                                   new { slot = h.Port(h.ForEachContract, "inputs", "budget"), value = 1 } }, new int?[] { 2, 3 }),
                h.Step("S2_next", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S3_body", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }, start: 1));
            h.Publish("for-each-budget", targets: new[] { Entity(1), Entity(2) });
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.IterationBudget),
                "a candidate set above its budget is refused");
            Check(h.Applied.Count == 0, "no round runs off a partially read candidate set");
        }
        // cancel: the count is what actually happened, and the cancelled pulse never fires.
        {
            var h = new Harness();
            h.Load(h.Plan("cancel", new[]
            {
                h.Step("S0_interval", "control", h.IntervalBinding, h.IntervalContract, new object[] { 0 },
                    new object[] { new { slot = h.Port(h.IntervalContract, "inputs", "interval"), value = 1 },
                                   new { slot = h.Port(h.IntervalContract, "inputs", "count"), value = 3 } }, new int?[] { 1, 3 }),
                h.Step("S1_cancel", "control", h.CancelBinding, h.CancelContract, Array.Empty<object>(),
                    new object[] { FromStep(h.Port(h.CancelContract, "inputs", "task"), 0, h.Port(h.IntervalContract, "outputs", "timer")) }, new int?[] { 2 }),
                h.Step("S2_report", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(FromStep(h.Port(h.ActionContract, "inputs", "steps"), 1, h.Port(h.CancelContract, "outputs", "cancelled"))), new int?[] { null }),
                h.Step("S3_pulse", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }));
            h.Publish("cancel");
            var tick = h.Kernel.Advance(1, true);
            Check(h.Applied.SequenceEqual(new[] { "S2_report:" + Entity(1) + ":1" }), "cancel outputs the count it actually cancelled [" + string.Join("|", h.Applied) + "]" + string.Join("|", tick.Events.Select(e => e.Status + ":" + e.Code)));
            Check(tick.Events.All(e => e.Status != "rejected") && h.Kernel.Advance(9, true).CommandsExecuted == 0
                && h.Applied.Count == 1, "the cancelled continuation never fires");
        }
        // cancel: a handle that already died is refused by name, with a zero count.
        {
            var h = new Harness();
            h.Load(h.Plan("cancel-stale", new[]
            {
                h.Step("S0_interval", "control", h.IntervalBinding, h.IntervalContract, new object[] { 0 },
                    new object[] { new { slot = h.Port(h.IntervalContract, "inputs", "interval"), value = 1 },
                                   new { slot = h.Port(h.IntervalContract, "inputs", "count"), value = 3 } }, new int?[] { 1, 3 }),
                h.Step("S1_cancel", "control", h.CancelBinding, h.CancelContract, Array.Empty<object>(),
                    new object[] { FromStep(h.Port(h.CancelContract, "inputs", "task"), 0, h.Port(h.IntervalContract, "outputs", "timer")) }, new int?[] { 2 }),
                h.Step("S2_cancel", "control", h.CancelBinding, h.CancelContract, Array.Empty<object>(),
                    new object[] { FromStep(h.Port(h.CancelContract, "inputs", "task"), 0, h.Port(h.IntervalContract, "outputs", "timer")) }, new int?[] { 4 }),
                h.Step("S3_pulse", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S4_report", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }));
            h.Publish("cancel-stale");
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.StaleHandle), "a dead handle is refused as stale");
            Check(h.Applied.Count == 0, "the step after the refused cancel never runs");
        }
        // restart: the rolling window. A live timer is re-armed from now and the handle it published keeps
        // working, so the same plan re-runs its window without holding the handle anywhere. The interval's first
        // pulse is `after_interval` (constant 1), so the window the restart moves is still ahead of both calls.
        {
            var h = new Harness();
            h.Load(h.Plan("restart", new[]
            {
                h.Step("S0_interval", "control", h.IntervalBinding, h.IntervalContract, new object[] { 1 },
                    new object[] { new { slot = h.Port(h.IntervalContract, "inputs", "interval"), value = 5 },
                                   new { slot = h.Port(h.IntervalContract, "inputs", "count"), value = 3 } }, new int?[] { 1, 3 }),
                h.Step("S1_restart", "control", h.RestartBinding, h.RestartContract, Array.Empty<object>(),
                    new object[] { FromStep(h.Port(h.RestartContract, "inputs", "task"), 0, h.Port(h.IntervalContract, "outputs", "timer")) }, new int?[] { 2 }),
                h.Step("S2_report", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 },
                    h.ActionInput(), new int?[] { null }),
                h.Step("S3_pulse", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }));
            h.Publish("restart");
            // The interval's own first pulse is at 1 + 5 = 6; the restart at tick 1 puts it at 1 + 5 = 6 as well,
            // so the case reads the re-armed window off the two advances: nothing runs between them, the step after
            // the restart runs now, and exactly one pulse lands on the new tick.
            var activation = h.Kernel.Advance(1, true);
            Check(activation.CommandsExecuted == 1 && h.Applied.SequenceEqual(new[] { "S2_report:" + Entity(1) + ":-" }),
                "a live handle restarts and the flow after it runs [" + string.Join("|", h.Applied) + "]"
                + string.Join("|", activation.Events.Select(e => e.Status + ":" + e.Code))
                + " commands=" + string.Join("|", activation.Commands.Select(c => c.NodeId + ":" + c.Result.Status + ":" + c.Result.Code)));
            Check(activation.Events.All(e => e.Status != "rejected"), "restarting a live timer is not a refusal");
            Check(h.Kernel.Advance(5, true).CommandsExecuted == 0, "the restarted window does not fire before its new interval elapses");
            Check(h.Kernel.Advance(6, true).CommandsExecuted == 1 && h.Applied.Count == 2,
                "the re-armed timer fires one interval after the restart, on the same handle");
            Check(h.Kernel.Advance(11, true).CommandsExecuted == 1 && h.Applied.Count == 3,
                "the re-armed schedule keeps its own cadence for the pulses it has left");
        }
        // restart: a handle no live schedule answers for cannot be reached from a plan. The only handle a later
        // activation can read is the one its resumed control step republishes, and that one is live by
        // construction — so the refusal the kernel raises for a dead handle is proved where it is reachable, by
        // the two cancels of the case above, and this case proves the positive half only.
        // cancel: a handle has no literal spelling, in any plan.
        {
            var h = new Harness();
            RejectCode(() => h.Load(h.Plan("handle-literal", new[]
            {
                h.Step("S0_cancel", "control", h.CancelBinding, h.CancelContract, Array.Empty<object>(),
                    new object[] { new { slot = h.Port(h.CancelContract, "inputs", "task"), value = new { worldEpoch = 1, lifeEpoch = 1, local = 0, provider = 0 } } },
                    new int?[] { 1 }),
                h.Step("S1_report", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            })), RuntimeAbiCodes.HandleLiteral, "a handle cannot be written as a literal");
        }
        // one activation may execute only so many steps; a loop round or a resumption is its own activation.
        {
            var h = new Harness(new RuntimeLimits { MaxStepExecutionsPerDispatch = 3 });
            h.Load(h.Plan("step-budget", new[]
            {
                h.Step("S0_sequence", "control", h.SequenceBinding, h.SequenceContract3, new object[] { 3 }, Array.Empty<object>(), new int?[] { 1, 2, 3 }),
                h.Step("S1_first", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S2_second", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null }),
                h.Step("S3_third", "action", h.ApplyBinding, h.ActionContract, new object[] { 5 }, h.ActionInput(), new int?[] { null })
            }));
            h.Publish("step-budget");
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Events.Any(e => e.Status == "rejected" && e.Code == RuntimeAbiCodes.DispatchStepBudget),
                "an activation past its step ceiling is refused by name [" + string.Join("|", tick.Events.Select(e => e.Status + ":" + e.Code)) + "] applied=" + string.Join("|", h.Applied) + " executed=" + tick.CommandsExecuted);
            Check(h.Applied.SequenceEqual(new[] { "S1_first:" + Entity(1) + ":-", "S2_second:" + Entity(1) + ":-" }),
                "the refused step never runs [" + string.Join("|", h.Applied) + "]");
        }
        // attachments: the list is required, sorted, duplicate-free, of known kinds, and within budget.
        {
            var h = new Harness(mounts: true);
            object Map(string reference) => new { kind = "map-object", category = "zone", reference };
            RejectCode(() => h.Load(h.Plan("mount-empty", h.SingleAction(), Array.Empty<object>())),
                RuntimeAbiCodes.AttachmentEmpty, "a plan without a mount target is refused");
            RejectCode(() => h.Load(h.Plan("mount-order", h.SingleAction(), new object[] { Map("z1"), new { kind = "level", reference = "l" } })),
                RuntimeAbiCodes.AttachmentOrder, "mount targets are ordinal sorted");
            RejectCode(() => h.Load(h.Plan("mount-duplicate", h.SingleAction(), new object[] { Map("z1"), Map("z1") })),
                RuntimeAbiCodes.AttachmentDuplicate, "a repeated mount target is refused");
            RejectCode(() => h.Load(h.Plan("mount-kind", h.SingleAction(), new object[] { new { kind = "enemy-type", reference = "e1" } })),
                RuntimeAbiCodes.AttachmentKind, "a kind no provider registered a matcher for is refused at load");
            RejectCode(() => h.Load(h.Plan("mount-category", h.SingleAction(), new object[] { new { kind = "level", category = "zone", reference = "l" } })),
                RuntimeAbiCodes.AttachmentKind, "only a map object carries a category");
            RejectCode(() => h.Load(h.Plan("mount-kindname", h.SingleAction(), new object[] { new { kind = "sector", reference = "s1" } })),
                RuntimeAbiCodes.AttachmentKind, "a kind outside the fixed vocabulary is refused");
            var budget = new Harness(new RuntimeLimits { MaxAttachmentsPerPlan = 2 }, mounts: true);
            RejectCode(() => budget.Load(budget.Plan("mount-budget", budget.SingleAction(), new object[] { Map("z1"), Map("z2"), Map("z3") })),
                RuntimeAbiCodes.AttachmentBudget, "a plan above the mount budget is refused");
            budget.Load(budget.Plan("mount-ok", budget.SingleAction(), new object[] { Map("z1"), Map("z2") }));
            Check(true, "a sorted, duplicate-free mount list within budget loads");
        }
        // attachments: dispatch follows the plan's own mount targets.
        {
            var h = new Harness(mounts: true);
            h.Load(h.Plan("mount-filter", h.SingleAction(), new object[] { new { kind = "map-object", category = "zone", reference = "z1" } }));
            h.Kernel.Advance(1, true);
            Check(h.Applied.Count == 0, "an idle world dispatches nothing");
            var inside = h.Publish("mount-inside");
            h.Kernel.Advance(2, true);
            Check(inside.Status == "queued" && h.Applied.SequenceEqual(new[] { "Action0:" + Entity(1) + ":-" }),
                "an event whose subject is the mounted zone starts the plan");
            var outside = h.Publish("mount-outside", targets: new[] { Entity(2) });
            var tick = h.Kernel.Advance(3, true);
            Check(outside.Status == "ignored" && outside.Code == "attachment-mismatch" && tick.CommandsExecuted == 0,
                "an event about another object never reaches the plan");
        }
        // attachments: a kind whose target names no event subject is judged from the target alone. The world
        // trigger carries no entity port at all, so nothing but the scope matcher can claim its events — and it
        // does, while the same plan under a target the matcher does not answer is never dispatched.
        {
            var world = new Harness();
            world.Load(world.Plan("mount-level", new[]
            {
                world.Step("S0_branch", "control", world.BranchBinding, world.BranchContract, Array.Empty<object>(),
                    new object[] { new { slot = world.Port(world.BranchContract, "inputs", "condition"), value = true } }, new int?[] { null, null })
            }, new object[] { new { kind = "level", reference = Fixture.MountReference } }, trigger: world.WorldTriggerBinding));
            var claimed = world.PublishWorld("mount-level-world");
            Check(claimed.Status == "queued", "a subject-free mount claims an event that carries no subject ["
                + claimed.Status + ":" + claimed.Code + "]");
            var elsewhere = new Harness();
            elsewhere.Load(elsewhere.Plan("mount-level-elsewhere", new[]
            {
                elsewhere.Step("S0_branch", "control", elsewhere.BranchBinding, elsewhere.BranchContract, Array.Empty<object>(),
                    new object[] { new { slot = elsewhere.Port(elsewhere.BranchContract, "inputs", "condition"), value = true } }, new int?[] { null, null })
            }, new object[] { new { kind = "level", reference = "some-other-level" } }, trigger: elsewhere.WorldTriggerBinding));
            var miss = elsewhere.PublishWorld("mount-level-miss");
            Check(miss.Status == "ignored" && miss.Code == "attachment-mismatch",
                "a subject-free mount that does not answer the target never reaches the plan [" + miss.Status + ":" + miss.Code + "]");
        }
        // attachments: the two kinds of matcher coexist on one plan, and the subject path keeps its own rule.
        {
            var h = new Harness(mounts: true);
            h.Load(h.Plan("mount-mixed", h.SingleAction(), new object[]
            {
                new { kind = "level", reference = Fixture.MountReference },
                new { kind = "map-object", category = "zone", reference = "z1" }
            }));
            var inside = h.Publish("mount-mixed-subject");
            h.Kernel.Advance(1, true);
            Check(inside.Status == "queued" && h.Applied.SequenceEqual(new[] { "Action0:" + Entity(1) + ":-" }),
                "a plan with a subject-free mount also dispatches for a subject its other mount claims");
            // A second world-trigger entry on the same plan: the subject-free mount claims it, and nothing about
            // the subject-matched target is consulted for an event that carries no subject.
            h.Load(h.Plan("mount-mixed-world", new[]
            {
                h.Step("S0_branch", "control", h.BranchBinding, h.BranchContract, Array.Empty<object>(),
                    new object[] { new { slot = h.Port(h.BranchContract, "inputs", "condition"), value = true } }, new int?[] { null, null })
            }, new object[] { new { kind = "level", reference = Fixture.MountReference } }, trigger: h.WorldTriggerBinding));
            var subjectless = h.PublishWorld("mount-mixed-world");
            Check(subjectless.Status == "queued",
                "an event with no subject is judged by the subject-free mount alone [" + subjectless.Status + ":" + subjectless.Code + "]");
        }
        // result rows: the shared columns come first, in order, and every later column is a declared value.
        {
            foreach (var (fields, name) in new (string, string)[]
            {
                ("null", "a result schema without a declared row shape"),
                ("[{\"id\":\"status\",\"type\":\"enum\",\"schema\":\"execution_outcome\"},{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"committed\",\"type\":\"enum\",\"schema\":\"commit_state\"},{\"id\":\"code\",\"type\":\"string\"}]", "row columns out of order"),
                ("[{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"status\",\"type\":\"enum\",\"schema\":\"execution_outcome\"},{\"id\":\"committed\",\"type\":\"enum\",\"schema\":\"commit_state\"}]", "a row shorter than the four shared columns"),
                ("[{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"status\",\"type\":\"enum\",\"schema\":\"execution_outcome\"},{\"id\":\"committed\",\"type\":\"enum\",\"schema\":\"commit_state\"},{\"id\":\"code\",\"type\":\"string\"},{\"id\":\"code\",\"type\":\"string\"}]", "a repeated row column"),
                ("[{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"status\",\"type\":\"enum\",\"schema\":\"execution_outcome\"},{\"id\":\"committed\",\"type\":\"enum\",\"schema\":\"commit_state\"},{\"id\":\"code\",\"type\":\"string\"},{\"id\":\"blob\",\"type\":\"handle\"}]", "a row column type outside the row vocabulary"),
                ("[{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"status\",\"type\":\"enum\",\"schema\":\"execution_outcome\"},{\"id\":\"committed\",\"type\":\"enum\",\"schema\":\"commit_state\"},{\"id\":\"code\",\"type\":\"string\"},{\"id\":\"amount\",\"type\":\"string\",\"unit\":\"hp\"}]", "a unit on a column that carries none"),
                ("[{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"status\",\"type\":\"enum\",\"schema\":\"execution_outcome\"},{\"id\":\"committed\",\"type\":\"enum\",\"schema\":\"commit_state\"},{\"id\":\"code\",\"type\":\"string\"},{\"id\":\"outcome\",\"type\":\"enum\",\"schema\":\"no_such_set\"}]", "an enum column naming no declared set"),
                ("[{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"status\",\"type\":\"enum\",\"schema\":\"execution_outcome\",\"nullable\":true},{\"id\":\"committed\",\"type\":\"enum\",\"schema\":\"commit_state\"},{\"id\":\"code\",\"type\":\"string\"}]", "a shared column declared nullable")
            })
                RejectCode(() => ResultProbe(fields), RuntimeAbiCodes.ResultSchema, "result schema: " + name);
            ResultProbe(null);
            Check(true, "the four shared columns plus real values register");
        }
        // result rows: the kernel's own combat result is a declared row, and it is reachable from the manifest.
        {
            var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
            kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
            var capabilities = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry").GetProperty("capabilities");
            var result = capabilities.EnumerateArray().Single(c => c.GetProperty("id").GetString() == "forge.action.combat.heal")
                .GetProperty("graph").GetProperty("outputs").EnumerateArray().Single(p => p.GetProperty("type").GetString() == "result");
            var ids = result.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("id").GetString()!).ToArray();
            Check(ids.SequenceEqual(new[] { "target", "status", "committed", "code", "amount", "target_count" }),
                "the heal result declares the shared columns and its own values, in order");
        }
        return checks;
    }

    /// <summary>One observation capability on its own kernel: enough to prove which tier a port shape forces,
    /// and nothing else, so a rejection can only come from the tier rules.</summary>
    private static void Probe(string execution, string input, string output)
    {
        var kernel = new RuntimeKernel(Fixture.Identity);
        kernel.BeginWorld(1);
        var seed = RuntimeJson.From(new
        {
            providers = new[] { new { id = Id, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new object[] { new {
                id = Id + ".condition.probe", owner = Id, kind = "condition", label = "Probe", version = "1.0.0", parameters = new { },
                graph = new { domains = new[] { "enemy" }, execution, inputs = new[] { RuntimeJson.Parse(input) }, outputs = new[] { RuntimeJson.Parse(output) }, parameters = Array.Empty<object>() } } },
            bindings = new object[] { new {
                id = Id + ".binding.probe", capabilityId = Id + ".condition.probe", providerId = Id, handler = Id + ".handler.probe",
                role = "evaluate", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() } }
        });
        var module = new RuntimeModule(RuntimeKernel.ApiVersion, seed.GetRawText(), new Dictionary<string, CommandHandler>(),
            new[] { new BindingSupport(Id + ".binding.probe", "implementation-only", NoPermissions) })
        {
            Evaluators = new Dictionary<string, EvaluatorHandler> { [Id + ".handler.probe"] = _ => RuntimeJson.From(new { value = true }) },
            Shapes = new Dictionary<string, HandlerShape> { [Id + ".handler.probe"] = new HandlerShape().Outputs("value") }
        };
        kernel.RegisterModule(module, RuntimeLogLevel.Off);
    }

    /// <summary>The same probe for a result schema: the action's own row shape is the only variable. A null
    /// argument leaves the fixture's declared shape alone — that is the case that must register.</summary>
    private static void ResultProbe(string? fields)
    {
        var kernel = new RuntimeKernel(Fixture.Identity);
        kernel.BeginWorld(1);
        var seed = JsonNode.Parse(Fixture.Module(Id).RegistryJson)!;
        var result = seed["capabilities"]!.AsArray()[1]!["graph"]!["outputs"]!.AsArray()
            .Single(p => p!["type"]!.GetValue<string>() == "result")!.AsObject();
        if (fields != null) result["fields"] = JsonNode.Parse(fields);
        kernel.RegisterModule(Fixture.Module(Id) with { RegistryJson = seed.ToJsonString() }, RuntimeLogLevel.Off);
    }

    private static string Entity(int index) => Id + ":" + index;
    private static object FromStep(int slot, int step, int port) => new { slot, fromStepSlot = new { step, port } };
    private static object FromEvent(int slot, int port) => new { slot, fromEventSlot = port };

    /// <summary>One provider with the four capabilities these cases need: a recorded event, a recorded action that
    /// also reports the value a control step published, a budgeted observation that echoes what it read, and mount
    /// matching for `map-object`. Plan-side helpers speak in port ids; the harness resolves them to wire slots.</summary>
    private sealed class Harness
    {
        internal readonly RuntimeKernel Kernel;
        internal readonly RuntimeModuleHandle Module;
        internal readonly List<string> Applied = new();
        internal readonly string ApplyBinding = Id + ".binding.apply";
        internal readonly string TriggerBinding = Id + ".binding.trigger";
        /// <summary>The trigger whose events carry no entity at all: a world, timer or pulse event.</summary>
        internal readonly string WorldTriggerBinding = Id + ".binding.world";
        internal readonly string ObserveBinding = Id + ".binding.observe";
        internal readonly string BranchBinding = "forge.contract.control.binding.branch";
        internal readonly string SequenceBinding = "forge.contract.control.binding.sequence";
        internal readonly string DelayBinding = "forge.contract.control.binding.delay";
        internal readonly string IntervalBinding = "forge.contract.control.binding.interval";
        internal readonly string RepeatBinding = "forge.contract.control.binding.repeat";
        internal readonly string ForEachBinding = "forge.contract.control.binding.for_each";
        internal readonly string CancelBinding = "forge.contract.control.binding.cancel";
        internal readonly string RestartBinding = "forge.contract.control.binding.restart";
        internal readonly string ReadBinding = VariableContracts.ReadBinding;
        internal readonly string PresentBinding = "forge.contract.control.binding.present";
        internal readonly JsonElement TriggerContract, WorldTriggerContract, ActionContract, ObserveContract;
        internal readonly JsonElement BranchContract, SequenceContract, DelayContract, IntervalContract, RepeatContract, ForEachContract, CancelContract, RestartContract;
        /// <summary>The kernel's own variable read, resolved for one declared boolean variable — the one read whose
        /// value port lets a case prove the row answers non-null.</summary>
        internal readonly JsonElement ReadFlagContract;
        /// <summary>The when-present guard, resolved for a possibly-absent integer.</summary>
        internal readonly JsonElement PresentContract;
        /// <summary>The sequence resolved with three exits: its variadic port count is a structural parameter, so
        /// a case that walks more steps than the activation ceiling asks for the wider contract explicitly.</summary>
        internal readonly JsonElement SequenceContract3;
        internal long World = 1;
        internal int Queries;
        /// <summary>The one variable the variable cases declare in the plan header. A level-scope flag with an
        /// authored initial value, so a read of it answers without any step having written it.</summary>
        internal const string ReadVariable = "level.flag";

        internal Harness(RuntimeLimits? ceiling = null, bool mounts = false)
        {
            Kernel = new RuntimeKernel(Fixture.Identity, ceiling);
            Kernel.BeginWorld(1);
            Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
            Kernel.RegisterModule(VariableContracts.Module(), RuntimeLogLevel.Off);
            var module = Fixture.Module(Id, context =>
            {
                // The fixture action records which step ran, on what, and the value a preceding control step
                // published into `steps` — that is how a case asserts the frame a loop or a cancel wrote.
                var target = context.Inputs.TryGetProperty("target", out var entity) ? RuntimeJson.Entity(entity).Id : "-";
                var steps = context.Inputs.TryGetProperty("steps", out var value) && value.ValueKind == JsonValueKind.Number ? ((long)value.GetDouble()).ToString() : "-";
                Applied.Add($"{context.NodeId}:{target}:{steps}");
                // `count` is the movable output the action-publishes-a-value cases read: a declared column of this
                // capability's own result row, read back by a later step through `fromStepSlot`.
                return CommandResult.Succeeded(RuntimeJson.From(new { actual = 1, count = 7 }));
            });
            var seed = JsonNode.Parse(module.RegistryJson)!;
            seed["capabilities"]!.AsArray()[0]!["graph"]!["outputs"] = JsonNode.Parse(
                "[{\"id\":\"next\",\"type\":\"execution\"},{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"targets\",\"type\":\"entity\",\"cardinality\":\"many\"},{\"id\":\"amount\",\"type\":\"number\"},{\"id\":\"maybe\",\"type\":\"integer\",\"nullable\":true}]");
            // The action's declared outputs: the result row it publishes (a row is not a value a step hands on),
            // plus one movable integer a later step reads through `fromStepSlot`.
            seed["capabilities"]!.AsArray()[1]!["graph"]!["outputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"count\",\"type\":\"integer\"}"));
            // A second trigger with no entity port at all: a world, timer or pulse event carries no subject, and a
            // case that publishes one proves a subject-free mount is judged for exactly those events.
            seed["capabilities"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Id + ".trigger.world", owner = Id, kind = "trigger", label = "World event", version = "1.0.0",
                parameters = new { },
                graph = new { domains = new[] { "enemy" }, execution = "host",
                    inputs = Array.Empty<object>(), outputs = new object[] { new { id = "next", type = "execution" } },
                    parameters = Array.Empty<object>() }
            }).GetRawText())!);
            seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = WorldTriggerBinding, capabilityId = Id + ".trigger.world", providerId = Id, handler = Id + ".handler.world",
                role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
            }).GetRawText())!);
            // `steps` is the fixture's one movable value input and stays an integer: a control step publishes the
            // count a case asserts (`cancel`'s cancelled, a loop's index), and the action records it as the number
            // its own log line carries.
            seed["capabilities"]!.AsArray()[1]!["graph"]!["inputs"] = JsonNode.Parse(
                "[{\"id\":\"in\",\"type\":\"execution\"},{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"steps\",\"type\":\"integer\",\"optional\":true}]");
            seed["capabilities"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Id + ".observe", owner = Id, kind = "selector", label = "Observed entities", version = "1.0.0", parameters = new { },
                graph = new { domains = new[] { "enemy" }, execution = "query",
                    inputs = new object[] { new { id = "targets", type = "entity", cardinality = "many" } },
                    outputs = new object[] { new { id = "seen", type = "entity" }, new { id = "seen_many", type = "entity", cardinality = "many" }, new { id = "hits", type = "integer" } },
                    parameters = Array.Empty<object>() }
            }).GetRawText())!);
            seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = ObserveBinding, capabilityId = Id + ".observe", providerId = Id, handler = Id + ".handler.observe",
                role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
            }).GetRawText())!);
            Module = Kernel.RegisterModule(module with
            {
                RegistryJson = seed.ToJsonString(),
                Evaluators = new Dictionary<string, EvaluatorHandler> { [Id + ".handler.observe"] = Observe },
                Shapes = new Dictionary<string, HandlerShape> { [Fixture.Handler(Id)] = Fixture.Shape(Id, new[] { "target", "steps" }),
                    [Id + ".handler.observe"] = new HandlerShape().Inputs("targets").Outputs("seen", "seen_many", "hits") },
                BindingSupport = new[] { Fixture.Support(Id)[0], Fixture.Support(Id)[1],
                    new BindingSupport(WorldTriggerBinding, "implementation-only", NoPermissions),
                    new BindingSupport(ObserveBinding, "implementation-only", NoPermissions) },
                EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [Id] = reference => reference.Id.StartsWith(Id + ":", StringComparison.Ordinal) && reference.WorldEpoch == World && reference.LifeEpoch == 1 },
                EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { [Id] = reference => new RuntimeEntitySnapshot(reference, "enemy", null, "alive", new[] { "tag" }, Array.Empty<string>(), new[] { 0d, 0d, 0d }) },
                AttachmentMatchers = Mounts(mounts)
            }, RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { });
            TriggerContract = Contract(Fixture.TriggerCapability(Id), RuntimeJson.EmptyObject);
            WorldTriggerContract = Contract(Id + ".trigger.world", RuntimeJson.EmptyObject);
            ActionContract = Contract(Fixture.ActionCapability(Id), RuntimeJson.From(new { amount = 5 }));
            ObserveContract = Contract(Id + ".observe", RuntimeJson.EmptyObject);
            BranchContract = Contract("forge.control.flow.branch", RuntimeJson.EmptyObject);
            SequenceContract = Contract("forge.control.flow.sequence", RuntimeJson.EmptyObject);
            SequenceContract3 = Contract("forge.control.flow.sequence", RuntimeJson.From(new { step_count = 3 }));
            DelayContract = Contract("forge.control.flow.delay", RuntimeJson.EmptyObject);
            IntervalContract = Contract("forge.control.flow.interval", RuntimeJson.From(new { first_pulse = 0 }));
            RepeatContract = Contract("forge.control.flow.repeat", RuntimeJson.EmptyObject);
            ForEachContract = Contract("forge.control.flow.for_each", RuntimeJson.EmptyObject);
            CancelContract = Contract("forge.control.flow.cancel", RuntimeJson.EmptyObject);
            RestartContract = Contract("forge.control.flow.restart", RuntimeJson.EmptyObject);
            // The kernel's variable module: `value_type` is a compiled member index, so the read is resolved the way
            // the website compiles it — `1` is `boolean` in the row's own member list.
            ReadFlagContract = Contract(VariableContracts.ReadCapability, RuntimeJson.From(new { name = ReadVariable, value_type = 1 }));
            // The when-present guard, typed by the same compiled member: `1` is `integer` in its own member list.
            PresentContract = Contract("forge.control.flow.present", RuntimeJson.From(new { value_type = 1 }));
        }

        internal JsonElement Contract(string capabilityId, JsonElement parameters)
            => Kernel.ResolveGraphContract(capabilityId, "1.0.0", parameters);

        /// <summary>The fixture provider's mount kinds. Every plan here mounts the whole level, and no kind belongs
        /// to the kernel any more, so the scope-matched kind is always claimed — without it no fixture plan would
        /// load. The subject-matched kind is claimed only where a case is about the subject path.</summary>
        private static IReadOnlyDictionary<string, AttachmentMatcherRegistration> Mounts(bool subjectMounts)
        {
            var matchers = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
            {
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == Fixture.MountReference)
            };
            if (subjectMounts)
                matchers["map-object"] = AttachmentMatcherRegistration.BySubject(
                    (category, reference, subject) => category == "zone" && reference == "z1" && subject.Id == Id + ":1");
            return matchers;
        }

        private JsonElement Observe(EvaluationContext context)
        {
            var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
            var seen = new List<EntityReference>();
            foreach (var target in targets)
            {
                Queries++;
                if (!context.Query.TrySnapshot(target, out var snapshot, out var code)) throw new RuntimeContractException(code, code);
                seen.Add(snapshot!.Ref);
            }
            return RuntimeJson.From(new { seen = seen[0], seen_many = seen.ToArray(), hits = seen.Count });
        }

        internal int Port(JsonElement contract, string side, string port)
            => RuntimeJson.Rows(contract, side).Select((p, index) => (p, index)).Single(x => RuntimeJson.Text(x.p, "id") == port).index;

        internal int TriggerPort(string side, string port) => Port(TriggerContract, side, port);

        /// <summary>The fixture action's inputs: `target` always comes from the event, and a case that checks a
        /// control step's published value adds the `steps` row it wants.</summary>
        internal object[] ActionInput(object? steps = null) => steps == null
            ? new object[] { FromEvent(Port(ActionContract, "inputs", "target"), TriggerPort("outputs", "target")) }
            : new object[] { FromEvent(Port(ActionContract, "inputs", "target"), TriggerPort("outputs", "target")), steps };

        /// <summary>One step as the plan builder sees it: the binding by name, the rest already wire-shaped.</summary>
        internal sealed record StepRow(string NodeId, string NodeKind, string BindingId, object Layout, object[] Inputs, int?[] Successors);

        internal StepRow Step(string nodeId, string nodeKind, string bindingId, JsonElement contract, object[] constants, object[] inputs, int?[] successors)
            => new(nodeId, nodeKind, bindingId, Layout(contract, constants), inputs, successors);

        internal StepRow[] SingleAction() => new[]
        {
            Step("Action0", "action", ApplyBinding, ActionContract, new object[] { 5 }, ActionInput(), new int?[] { null })
        };

        internal string Plan(string planId, StepRow[] steps, object[]? attachments = null, int start = 0, string? trigger = null, object[]? variables = null)
        {
            // The entry's trigger is the one whose events this plan is written for; a world-trigger plan carries no
            // entity at all, which is the case a subject-free mount has to answer.
            var triggerBinding = trigger ?? TriggerBinding;
            var triggerContract = triggerBinding == WorldTriggerBinding ? WorldTriggerContract : TriggerContract;
            // The pin table is exactly the closure this plan uses — its entry's trigger plus its steps — ordinal
            // sorted, and every reference to it is an index into that table.
            var used = steps.Select(s => s.BindingId).Append(triggerBinding).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var pins = PinTable().Where(p => used.Contains(p.BindingId)).Select(p => (object)new
            {
                bindingId = p.BindingId, capabilityId = p.CapabilityId, capabilityVersion = p.CapabilityVersion,
                providerId = p.ProviderId, providerVersion = p.ProviderVersion, handler = p.Handler
            }).ToArray();
            int Pin(string bindingId) => Array.IndexOf(used, bindingId);
            // The plan's permission declaration is exactly the union its pinned bindings require; the manifest's own
            // support rows are the source, so a case cannot pass by restating them. A plan whose closure is one
            // control step and a subject-free trigger therefore declares none.
            var permissions = RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("bindingSupport").EnumerateArray()
                .Where(s => used.Contains(s.GetProperty("bindingId").GetString()!))
                .SelectMany(s => s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var rows = steps.Select(s => (object)new { nodeId = s.NodeId, nodeKind = s.NodeKind, binding = Pin(s.BindingId), layout = s.Layout, inputs = s.Inputs, successors = s.Successors }).ToArray();
            return RuntimeJson.From(new
            {
                schemaVersion = 4, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" },
                runtime = Kernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
                permissions, dependencies = Array.Empty<string>(),
                limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
                bindings = pins, attachments = attachments ?? Fixture.Attachments,
                variables = variables ?? Array.Empty<object>(),
                entrypoints = new[] { new { nodeId = "Entry", binding = Pin(triggerBinding),
                    layout = Layout(triggerContract, Array.Empty<object>()), start, steps = rows } }
            }).GetRawText();
        }

        /// <summary>Every registered binding, ordinal sorted. The loader locks a plan's pin table against the
        /// closure it actually uses, so a table built from the whole registry would be refused.</summary>
        private (string BindingId, string CapabilityId, string CapabilityVersion, string ProviderId, string ProviderVersion, string Handler)[] PinTable()
        {
            var manifest = RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry");
            var capabilities = manifest.GetProperty("capabilities").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("version").GetString()!, StringComparer.Ordinal);
            return manifest.GetProperty("bindings").EnumerateArray()
                .OrderBy(b => b.GetProperty("id").GetString(), StringComparer.Ordinal).Select(b => (
                    b.GetProperty("id").GetString()!, b.GetProperty("capabilityId").GetString()!, capabilities[b.GetProperty("capabilityId").GetString()!],
                    b.GetProperty("providerId").GetString()!, "1.0.0", b.GetProperty("handler").GetString()!)).ToArray();
        }

        internal static object Layout(JsonElement contract, object[] constants)
            => new { inputs = RuntimeGraphContracts.Layout(contract, "inputs"), outputs = RuntimeGraphContracts.Layout(contract, "outputs"), constants, promoted = Array.Empty<int>() };

        /// <summary>Loads one plan, re-throwing with the code in the message: a failing case must name the rule it
        /// broke, not just the node it broke on.</summary>
        internal void Load(string json)
        {
            try { Kernel.LoadPlan(json); }
            catch (RuntimeContractException error) { throw new RuntimeContractException(error.Code, error.Code + ": " + error.Message); }
        }

        internal DispatchResult Publish(string eventId, long tick = 1, string[]? targets = null, long? maybe = null)
        {
            // The event's subject is the first of its targets: an event about another object must carry that
            // object in `target` too, or a mount matcher could never tell the two apart. `maybe` is the one
            // possibly-absent payload value the when-present cases test, so it is always present in the payload and
            // null when the case means "no value".
            var all = targets ?? new[] { Id + ":1" };
            return Kernel.Publish(Module, new RuntimeEvent(eventId, TriggerBinding, World, tick, "shared-scope", RuntimeJson.From(new
            {
                target = new EntityReference(all[0], World, 1),
                targets = all.Select(id => new EntityReference(id, World, 1)).ToArray(),
                amount = 5d,
                maybe
            })));
        }

        /// <summary>One event of the world trigger, which carries no entity port at all: a world, timer or pulse
        /// event has no subject, so no mount matcher that reads subjects has anything to read.</summary>
        internal DispatchResult PublishWorld(string eventId, long tick = 1)
            => Kernel.Publish(Module, new RuntimeEvent(eventId, WorldTriggerBinding, World, tick, "shared-scope",
                RuntimeJson.From(new { })));
    }
}
