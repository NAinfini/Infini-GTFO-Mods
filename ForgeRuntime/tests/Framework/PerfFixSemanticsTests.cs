using ForgeRuntime.Framework;

/// <summary>
/// The semantics the kernel's hot-path work must not move: the command identity a receipt carries, and the
/// subscription gate a publisher checks before it builds an event value. Both are the R-156.2 items whose only
/// observable surface is what a caller and a receipt see, so both are locked here rather than inferred from a
/// benchmark number.
/// </summary>
static class PerfFixSemanticsTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string name) { checks++; if (!condition) throw new Exception("FAIL perf-semantics: " + name); }

        // The command identity is composed from a per-dispatch half and a per-step half instead of being serialized
        // as a four-element array per step. The text is the same text, escaping included: a publisher, an event, a
        // plan and a node, in that order, spelled by the JSON encoder.
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); s.Plan("alpha", a.ProviderId);
            var ids = new[] { "plain-event", "quote\"inside", "back\\slash", "html<&>plus+tag", "unicode-Ω", "tab\\u0009" };
            foreach (var id in ids)
            {
                Check(a.Publish(s.Event(a.ProviderId, id, tick: 0)).Status == "queued", "the event " + id + " is accepted");
                var receipt = s.Kernel.Advance(0, true).Commands[0];
                Check(receipt.EventId == id, "the receipt names the event it was dispatched for");
                var canonical = RuntimeJson.StableText(RuntimeJson.From(new[] { a.ProviderId, id, receipt.PlanId, receipt.NodeId }));
                Check(receipt.CommandId == canonical, "command identity of " + id + " is the canonical four-name text");
            }
        }

        // A gate is the kernel's own precomputed answer to "is any loaded plan mounted on this binding", kept by the
        // publisher so the answer costs one field read. What it must equal is the kernel's own verdict: while it is
        // closed the publish is `no-consumer`, and while it is open the publish is accepted.
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha");
            var trigger = Fixture.Trigger(a.ProviderId);
            var gate = a.SubscriptionGate(trigger);
            Check(!gate.HasSubscribers, "a binding no plan mounts has a closed gate");
            Check(ReferenceEquals(a.SubscriptionGate(trigger), gate), "one binding keeps one gate for the module that owns it");
            var ignored = a.Publish(s.Event(a.ProviderId, "unmounted"));
            Check(gate.HasSubscribers == false && ignored.Status == "ignored" && ignored.Code == "no-consumer",
                "a closed gate is exactly the state the kernel answers no-consumer in");
            s.Plan("alpha", a.ProviderId);
            Check(gate.HasSubscribers, "loading a plan mounted on the binding opens its gate");
            Check(a.Publish(s.Event(a.ProviderId, "mounted")).Status == "queued",
                "an open gate is the state the kernel accepts a publish in");
            s.Kernel.UnloadPlan("alpha");
            Check(!gate.HasSubscribers, "unloading the plan closes the gate again");
            s.Kernel.Advance(0, true);
            Check(a.Publish(s.Event(a.ProviderId, "after-unload")).Code == "no-consumer",
                "and the kernel refuses the binding again");
        }

        // A gate belongs to its own provider: another module cannot ask about a binding it does not own.
        {
            var s = new TimingScenario(); var a = s.Register("example.alpha"); var b = s.Register("example.beta");
            var refused = false;
            try { b.SubscriptionGate(Fixture.Trigger(a.ProviderId)); }
            catch (RuntimeContractException ex) when (ex.Code == "binding-owner") { refused = true; }
            Check(refused, "a module can only resolve gates for its own bindings");
        }

        Console.WriteLine($"Perf-semantics checks: {checks} passed.");
        return checks;
    }
}
