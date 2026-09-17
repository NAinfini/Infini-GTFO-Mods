using ForgeRuntime.Framework;

namespace ForgeRuntime.Tests.Perf;

/// <summary>
/// The five blocks R-142's performance principles ask for, measured against the kernel itself. Every block states
/// the ground it stands on (limits, plan shape, recipient count) because a number without its ground is not a
/// measurement: the kernel's per-tick budgets are part of what the numbers mean.
///
/// Run: `dotnet run -c Release --project ForgeRuntime/tests/Perf [block ...]`. No package is referenced and no
/// production file is touched by this project.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var blocks = args.Length == 0
            ? new[] { "no-subscriber", "gate", "burst", "timers", "query-memo", "presentation" }
            : args;
        Console.WriteLine("ForgeRuntime kernel micro-benchmarks. Timings are wall-clock on this machine; allocation is");
        Console.WriteLine("GC.GetAllocatedBytesForCurrentThread() over the measured block only. Neither is a GTFO frame rate.");
        Console.WriteLine();
        Warmup();
        foreach (var block in blocks)
        {
            Console.WriteLine("== " + block);
            switch (block)
            {
                case "no-subscriber": NoSubscriber(); break;
                case "gate": Gate(); break;
                case "burst": Burst(); break;
                case "timers": Timers(); break;
                case "query-memo": QueryMemo(); break;
                case "presentation": Presentation(); break;
                default: Harness.Failed("unknown block " + block); break;
            }
            Console.WriteLine();
        }
        Console.WriteLine("Receipts: " + Harness.All.Count);
        return Environment.ExitCode;
    }

    /// <summary>
    /// One throwaway pass over every path a block measures, run before any block. Every block measures with
    /// `warmups: 0` because its own work mutates the world it is measured in — a queue fills, a tick passes a pulse —
    /// so the JIT and the tiered compiler are warmed here instead. Without this a block's number depends on whether
    /// an earlier block happened to compile the same method: an empty `Advance` measured 0.30 us as the second block
    /// and 5.57 us as the first, which is a property of the run and not of the kernel.
    /// </summary>
    private static void Warmup()
    {
        using (var w = new PerfWorld.World(plan: OneActionPlan))
            for (var i = 0; i < 400; i++) { var tick = 1 + i / 8; w.Publish("warm-" + i, tick); w.Advance(tick); }
        using (var w = new PerfWorld.World(query: true, plan: x => QueryPlan(x, 4), candidates: 8))
            for (var i = 0; i < 40; i++) { w.Publish("warm-q-" + i, 1 + i); w.Advance(1 + i); }
        using (var w = new PerfWorld.World(query: true, plan: x => ChainPlan(x, 8), candidates: 8))
            for (var i = 0; i < 40; i++) { w.Publish("warm-c-" + i, 1 + i); w.Advance(1 + i); }
        using (var w = new PerfWorld.World(presentation: true, plan: x => PerfWorld.World.Entry(x.Kernel, PerfWorld.World.PresentStep(x.Kernel, "Present", null))))
            for (var i = 0; i < 40; i++) { w.Publish("warm-p-" + i, 1 + i); w.Advance(1 + i); }
        using (var w = new PerfWorld.World(plan: OneActionPlan))
        {
            for (var i = 0; i < 32; i++)
                w.Module.Schedule(new RuntimeEvent("warm-s-" + i, PerfWorld.TriggerBinding, 1, 1, "perf-scope",
                        RuntimeJson.From(new { target = PerfWorld.SubjectRef })),
                    new PulseSchedule(10_000, FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 10_000));
            for (var i = 0; i < 40; i++) w.Advance(2 + i);
        }
        using (var w = new PerfWorld.World())
        {
            var idle = new RuntimeEvent("warm-none", PerfWorld.TriggerBinding, 1, 1, "perf-scope",
                RuntimeJson.From(new { target = PerfWorld.SubjectRef }));
            for (var i = 0; i < 4_000; i++) w.Module.Publish(idle);
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
    }

    /// <summary>
    /// P2's ground. A provider publishing on a binding no plan mounted is exactly the state a build with no Forge
    /// plan puts every native hook in, so the per-event numbers here are the floor those hooks pay.
    ///
    /// Two numbers are reported, because they answer different questions: what the kernel's own path costs once the
    /// event value exists, and what producing that value costs. A hook with nothing to publish should build neither,
    /// but the kernel cannot make the caller's own `RuntimeEvent` and its payload free.
    /// </summary>
    private static void NoSubscriber()
    {
        const int events = 100_000;
        using var world = new PerfWorld.World();
        var accepted = 0;
        var code = "none";
        Harness.Note("ground: no plan loaded, so `subscriptions` is empty; the trigger binding of one provider is registered.");

        var ready = new RuntimeEvent("none-fixed", PerfWorld.TriggerBinding, 1, 1, "perf-scope",
            RuntimeJson.From(new { target = PerfWorld.SubjectRef }));
        Harness.Measure("publish a ready event with no subscriber", events,
            "The kernel's own path: module handle, binding owner, subscription miss, `no-consumer`.",
            () =>
            {
                for (var i = 0; i < events; i++)
                {
                    var result = world.Module.Publish(ready);
                    if (result.Status is "queued" or "duplicate") accepted++;
                    code = result.Code;
                }
            }, warmups: 0);
        Harness.Note($"status: {accepted} accepted, last code `{code}`, queued={world.Kernel.QueuedEvents}");

        var built = 0;
        Harness.Measure("build one event value (id + payload)", events,
            "What the caller pays before the kernel is reached: an event record and its payload document.",
            () =>
            {
                for (var i = 0; i < events; i++)
                {
                    var value = new RuntimeEvent("none-" + i, PerfWorld.TriggerBinding, 1, 1, "perf-scope",
                        RuntimeJson.From(new { target = PerfWorld.SubjectRef }));
                    if (value.EventId.Length > 0) built++;
                }
            }, warmups: 0);
        Harness.Note($"built {built} event values");
    }

    /// <summary>One trigger into one action: the smallest plan the kernel dispatches.</summary>
    private static PerfWorld.World.Trigger OneActionPlan(PerfWorld.World w)
        => PerfWorld.World.Entry(w.Kernel, PerfWorld.World.ActionStep(w.Kernel, "Action", null, null, null));

    /// <summary>
    /// P2's caller side. A publisher that owns a binding holds the kernel's own gate for it — one boolean the kernel
    /// refreshes when plans or modules change — so a hook on a binding no plan is mounted on reads one field and
    /// builds nothing: no event value, no payload document, no kernel path. That is what "a caller with no
    /// subscribers publishes in ≤ 0.1 us and allocates nothing" means, and it is the number a ForgeMap or ForgeWeapon
    /// publish point pays per native event in a build with no plan loaded.
    ///
    /// The gate is not the only thing such a publisher reads: the registration and startup checks it already made
    /// stay, in their own order, in front of the same early return. The block measures the whole caller path, and it
    /// is deliberately the closed case only — an open gate is the `no-subscriber` block's kernel path plus whatever
    /// the caller builds.
    /// </summary>
    private static void Gate()
    {
        const int events = 1_000_000;
        using var world = new PerfWorld.World();
        var gate = world.Module.SubscriptionGate(PerfWorld.TriggerBinding);
        Harness.Note("ground: no plan is loaded, so the trigger binding's gate is closed; the publisher's own");
        Harness.Note("        registration and startup checks run first, exactly as a native hook's publish point does.");
        var built = 0;
        Harness.Measure("caller publish with no subscriber, behind the binding's gate", events,
            "One registration check, one startup-state check and one gate read, then the caller returns.",
            () =>
            {
                for (var i = 0; i < events; i++)
                {
                    if (!world.Module.IsRegistered || world.Kernel.StartupState != RuntimeStartupState.Ready) continue;
                    if (!gate.HasSubscribers) continue;
                    built++;
                }
            }, warmups: 0);
        Harness.Note($"{built} events would have been built, {world.Kernel.QueuedEvents} queued");

        // The gate on its own: the marginal cost of the boolean, which is what a publisher that made its other
        // checks once per hook adds per native event.
        Harness.Measure("gate read alone", events, "One boolean field read; no registry, no kernel.",
            () => { for (var i = 0; i < events; i++) if (gate.HasSubscribers) built++; }, warmups: 0);
    }

    /// <summary>
    /// P5's ground: how many events one tick admits, and what the rest cost. The receiving plan is one host action of
    /// two steps, so the numbers are the dispatcher's and not a game's.
    ///
    /// The kernel's limits are ceilings, not preferences: every field a caller raises above <see cref="RuntimeLimits"/>'s
    /// own maximum is refused at construction, so there is no "unbounded burst" run to compare against. What is
    /// measured instead is the budget the runtime actually runs at, plus what an over-budget frame does with its
    /// remainder.
    /// </summary>
    private static void Burst()
    {
        var defaults = new RuntimeLimits();
        var perFrame = defaults.MaxEventsPerTick;
        const int frames = 500;
        Harness.Note($"ground: default limits (MaxEventsPerTick={perFrame}, MaxCommandsPerTick={defaults.MaxCommandsPerTick}, " +
                     $"MaxQueuedEvents={defaults.MaxQueuedEvents}). A limit above these is refused by the kernel's own constructor.");
        using (var world = new PerfWorld.World(plan: OneActionPlan))
        {
            var published = 0; TickResult? sample = null;
            Harness.Measure($"{frames} frames of a full {perFrame}-event budget", (long)frames * perFrame,
                "One advance per frame; every frame publishes exactly the tick's event budget and dispatches it.",
                () =>
                {
                    var tick = 0L;
                    for (var frame = 0; frame < frames; frame++)
                    {
                        tick++;
                        for (var i = 0; i < perFrame; i++) if (world.Publish("load-" + frame + "-" + i, tick).Status == "queued") published++;
                        var result = world.Advance(tick);
                        sample ??= result;
                    }
                }, warmups: 0);
            Harness.Note($"published {published} events over {frames} frames, queue={world.Kernel.QueuedEvents}, " +
                         $"handler calls={world.HandlerCalls}");
            Harness.Note("first frame: " + Harness.Describe(sample!));
        }

        // One over-budget frame: what the burst does with the part it cannot dispatch.
        using (var world = new PerfWorld.World(plan: OneActionPlan))
        {
            const int burst = 1000;
            var queued = 0; var refused = 0; var lastCode = "none";
            for (var i = 0; i < burst; i++)
            {
                var result = world.Publish("burst-" + i);
                if (result.Status == "queued") queued++; else { refused++; lastCode = result.Code; }
            }
            var first = world.Advance(1);
            Harness.Note($"publish {burst} in one frame: {queued} queued, {refused} refused (last `{lastCode}`); " +
                         $"frame 1 dispatched {first.EventsProcessed}, carries {first.DeferredEvents} over");
            var drained = first.EventsProcessed; var extraFrames = 0;
            while (world.Kernel.QueuedEvents > 0 && extraFrames < 20)
            {
                var tick = 2 + extraFrames;
                var next = world.Advance(tick);
                drained += next.EventsProcessed; extraFrames++;
                if (next.EventsProcessed == 0) break;
            }
            Harness.Note($"the backlog drained over {extraFrames + 1} frames: {drained} events, {world.Kernel.QueuedEvents} left queued");
        }
    }

    /// <summary>
    /// P3's ground. The schedules are left idle for the whole measured window, which is the steady state a build
    /// with timers actually sits in: what is measured is the per-frame cost of the timers existing, not of firing.
    /// The same loop runs with no schedules at all, so the difference is the schedule machinery.
    ///
    /// "500 concurrent timers" is not a state this kernel can reach: <see cref="RuntimeKernel.MaximumSchedules"/>
    /// refuses the 257th with `schedule-budget`, so the ceiling itself is the largest case measured.
    /// </summary>
    private static void Timers()
    {
        const int frames = 2000;
        Harness.Note("ground: IntervalTicks=10000, first pulse after the interval, 10000 pulses allowed, so no schedule");
        Harness.Note("        fires or ends inside the measured window and every frame sees the whole table live.");
        Harness.Note($"        The kernel's own ceiling is {RuntimeKernel.MaximumSchedules} schedules; a 500-timer request is");
        Harness.Note("        refused past that count, so 256 is the most a per-frame scan can ever cost.");
        using (var idle = new PerfWorld.World())
        {
            Harness.Measure("advance with no schedules", frames, "Empty queue, no plans, no timers.",
                () => { for (var i = 0; i < frames; i++) idle.Advance(2); }, warmups: 0);
        }
        foreach (var count in new[] { 50, RuntimeKernel.MaximumSchedules })
        {
            using var world = new PerfWorld.World(plan: OneActionPlan);
            var accepted = 0; var lastCode = "none";
            for (var i = 0; i < count; i++)
            {
                var scheduled = world.Module.Schedule(new RuntimeEvent("sched-" + i, PerfWorld.TriggerBinding, 1, 1, "perf-scope",
                        RuntimeJson.From(new { target = PerfWorld.SubjectRef })),
                    new PulseSchedule(10_000, FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 10_000));
                if (scheduled.Status == "scheduled") accepted++; else lastCode = scheduled.Code;
            }
            world.Advance(1);
            Harness.Measure($"advance with {count} live idle schedules", frames,
                "Each frame scans the schedule table and re-validates every job whose pulse is not due.",
                () => { for (var i = 0; i < frames; i++) world.Advance(2 + i); }, warmups: 0);
            Harness.Note($"{count} requested: {accepted} scheduled, {count - accepted} refused (last `{lastCode}`); " +
                         $"first pulse due tick 10001, queued={world.Kernel.QueuedEvents}");
        }
    }

    /// <summary>
    /// P4's ground: one activation that reads the same query result many times, against the same plan with two
    /// branches. The evaluator count is the direct evidence — one call per dispatch is the memo working, N calls are
    /// N world reads — and the receipts give the marginal cost of one more read.
    ///
    /// The two shapes are the two ways a single activation can hold several reads. `parallel_all` fans out inside one
    /// dispatch but caps `branch_count` at 32, so it cannot reach the 100 reads the brief names; a chain of action
    /// steps walked by one entry is the same single activation (<c>BeginActivation</c> runs once per work item, not
    /// once per step) and has no such ceiling beyond <c>MaxStepsPerEntrypoint</c>, so the 100-read case is a chain.
    /// The tick advances once per dispatch because the query budget is 64 entity queries per tick
    /// (<c>MaximumEntityQueriesPerTick</c>): a plan whose branches each read the world would spend one per branch.
    /// </summary>
    private static void QueryMemo()
    {
        const int perWorld = 2_000;
        Harness.Note("ground: every read is the same `query` step through `fromStepSlot` step 0 port 0. One evaluator");
        Harness.Note("        call per dispatch is the activation memo working; the candidate count is the query's own width.");
        var cases = new (int Reads, int Candidates, bool Chain)[]
        {
            // The empty-candidate case is the query path's floor: the step is still evaluated and the memo still
            // holds it, but the selector answers nobody, so the difference from `(2, 1, false)` is what the
            // candidate width costs. A number for the 32-candidate case alone says nothing about that split.
            (2, 0, false), (2, 1, false), (2, 32, false), (32, 32, false), (100, 32, true),
        };
        foreach (var (reads, candidates, chain) in cases)
        {
            using var world = new PerfWorld.World(query: true, plan: w => chain ? ChainPlan(w, reads) : QueryPlan(w, reads), candidates: candidates);
            world.Advance(1);
            var refused = 0; var executed = 0L; string? described = null;
            Harness.Measure($"dispatch, {(chain ? "a chain of " : "a fan-out of ")}{reads} reads in one activation", perWorld,
                "One event and one advance per dispatch; every branch is an action step whose handler runs once.",
                () =>
                {
                    for (var i = 0; i < perWorld; i++)
                    {
                        var tick = 2L + i;
                        if (world.Publish("q-" + i, tick).Status != "queued") refused++;
                        var result = world.Advance(tick);
                        executed += result.CommandsExecuted;
                        described ??= Harness.Describe(result);
                    }
                }, warmups: 0);
            Harness.Note($"{reads} reads, {candidates} candidates: evaluations/dispatch={(double)world.QueryEvaluations / perWorld:F4}, " +
                         $"handler calls={world.HandlerCalls}, commands executed={executed}, refused publishes={refused}");
            Harness.Note("        first tick: " + described);
        }
    }

    /// <summary>
    /// One entry whose `reads` action steps are walked in one activation, every one of them reading the single
    /// `query` step's output. The step order is the loader's canonical one: the read-only `AQuery` is step 0 (`A`
    /// sorts before `C`), and the actions follow in id order, each `next` naming a strictly later step.
    /// </summary>
    private static PerfWorld.World.Trigger ChainPlan(PerfWorld.World w, int reads)
    {
        var steps = new List<PerfWorld.StepDesc>
        {
            PerfWorld.World.QueryStep(w.Kernel, "AQuery"),   // the read every step makes
        };
        for (var i = 0; i < reads; i++)
            steps.Add(PerfWorld.World.ActionStep(w.Kernel, "Step" + i.ToString("D3"), 0, 0, i == reads - 1 ? null : 2 + i));
        // The entry starts at the first action: a `query` step is read on demand and is never walked.
        return PerfWorld.World.Entry(w.Kernel, 1, steps.ToArray());
    }

    /// <summary>
    /// One `parallel_all` whose `N` branches are all action steps reading the single `query` step's output. The
    /// step order is the loader's canonical one — Kahn's algorithm, ties broken by node id ordinal: the read-only
    /// query is step 0 (`AQuery`), the control is step 1 (`BParallel`, both start at indegree 0 and `A` sorts
    /// first), and the actions follow in id order. Every action reads the query through `fromStepSlot` step 0 port
    /// 0, which is what makes the read a candidate for the activation memo.
    ///
    /// `parallel_all` is what puts the reads in one activation: every branch is entered inside one dispatch, so the
    /// memo the first read fills is the memo the other N-1 reads hit. A `sequence` would not do it — each of its
    /// exits is its own activation, and an activation's memo starts empty by design.
    /// </summary>
    private static PerfWorld.World.Trigger QueryPlan(PerfWorld.World w, int reads)
    {
        var steps = new List<PerfWorld.StepDesc>
        {
            PerfWorld.World.QueryStep(w.Kernel, "AQuery"),   // the read every branch makes
        };
        // `branch_1`..`branch_N`, then the single `next` this plan leaves unwired: a successor must name a strictly
        // later action or control, and no step follows the control here.
        var successors = new int?[reads + 1];
        for (var i = 0; i < reads; i++) successors[i] = 2 + i;
        successors[reads] = null;
        steps.Add(PerfWorld.World.ParallelStep(w.Kernel, "BParallel", reads, successors));
        for (var i = 0; i < reads; i++)
            steps.Add(PerfWorld.World.ActionStep(w.Kernel, "Step" + i.ToString("D2"), 0, 0, null));
        return PerfWorld.World.Entry(w.Kernel, 1, steps.ToArray());
    }

    /// <summary>
    /// P6's ground: one presentation step addressed to eight player sessions, against one action step. What is
    /// measured is the host's half — the recipient resolution and the intent — because that is the half the kernel
    /// owns; the recipient's write is not in this process.
    /// </summary>
    private static void Presentation()
    {
        const int dispatches = 20_000;
        foreach (var presentation in new[] { false, true })
        {
            using var world = new PerfWorld.World(presentation: presentation, plan: w => PerfWorld.World.Entry(w.Kernel,
                presentation
                    ? PerfWorld.World.PresentStep(w.Kernel, "Present", null)
                    : PerfWorld.World.ActionStep(w.Kernel, "Action", null, null, null)));
            var intents = 0; var recipients = 0; var processed = 0; string? described = null;
            Harness.Measure(presentation ? "dispatch, one presentation step / 8 recipients" : "dispatch, one action step",
                dispatches, "One event per advance, and the tick advances with it: the per-tick event budget is the frame.",
                () =>
                {
                    for (var i = 0; i < dispatches; i++)
                    {
                        world.Publish("p-" + i, 1 + i);
                        var tick = world.Advance(1 + i);
                        processed += tick.EventsProcessed;
                        intents += tick.Presentations.Count;
                        if (tick.Presentations.Count > 0) recipients = tick.Presentations[0].Recipients.Count;
                        described ??= Harness.Describe(tick);
                    }
                }, warmups: 0);
            Harness.Note($"events processed={processed}, intents={intents}, recipients on the last intent={recipients}, " +
                         $"handler calls={world.HandlerCalls}");
            Harness.Note("first tick: " + described);
        }
    }
}
