using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Targeting;

/// <summary>
/// The session read a selector uses for a whole explicit candidate set: one budgeted call, the complete set or the
/// kernel's own refusal. `TrySnapshots` is the multi-reference form of <see cref="RuntimeQuerySession.TrySnapshot"/>,
/// so the cases below are about what a caller may conclude from its answer — a complete set is every requested
/// reference, a partial set is the framework's `entity-query-incomplete` and never a shorter answer, and a rejected
/// read keeps the kernel's own code. The caller contract is checked with the production row that consumes it:
/// <see cref="ObservedSpaceNodes.Observe"/> must refuse an incomplete set rather than let a selector rank the half
/// it happened to see.
/// </summary>
internal static class QuerySnapshotTests
{
    private const string Kind = "test.snapshot";

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void Reject(string code, Action action, string name)
        {
            checks++;
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception("FAIL: " + name + " [" + error.Code + " expected " + code + "]");
                return;
            }
            throw new Exception("FAIL: accepted " + name);
        }

        var a = At("a", 0); var b = At("b", 1); var c = At("c", 2);
        using (var world = new World(new[] { a, b, c }))
        {
            var session = world.Session();
            // One call answers the whole explicit set: every requested reference comes back with its snapshot, in
            // the kernel's own order, and `RequireComplete` is the framework's own gate over that answer.
            Check(session.TrySnapshots(new[] { a.Ref, b.Ref, c.Ref }, out var complete, out var code)
                && complete.IsComplete && complete.Code == "entities-observed" && complete.Status == "complete"
                && complete.Requested == 3 && complete.Distinct == 3 && complete.Items.Count == 3
                && complete.RequireComplete().Select(row => row.Ref).SequenceEqual(new[] { a.Ref, b.Ref, c.Ref })
                && code == complete.Code,
                "one call reads the complete explicit set and answers every reference ["
                + complete.Status + ":" + complete.Code + ":" + complete.Items.Count + "]");
            // Repeats are the raw input cost, not extra entities: the read deduplicates them exactly once.
            Check(session.TrySnapshots(new[] { a.Ref, a.Ref, b.Ref }, out var repeated, out _)
                && repeated.Requested == 3 && repeated.Distinct == 2 && repeated.Items.Count == 2 && repeated.IsComplete,
                "a repeated reference costs its slot and is observed once ["
                + repeated.Requested + ":" + repeated.Distinct + ":" + repeated.Items.Count + "]");
            // The whole set is one call and therefore one query, for any width the kernel accepts.
            Check(world.Observations == 5, "two calls observe the five requested references, never one read per call ["
                + world.Observations + "]");
        }
        // A selector's caller contract: an incomplete set is refused with the framework's own code, never answered
        // as a shorter selection.
        using (var world = new World(new[] { a, b, c }))
        {
            var session = world.Session();
            world.OnObserve = reference => reference == b.Ref ? null : world.Snapshot(reference);
            Check(session.TrySnapshots(new[] { a.Ref, b.Ref, c.Ref }, out var partial, out var partialCode)
                && !partial.IsComplete && partial.Status == "partial" && partial.Code == "entity-query-incomplete"
                && partialCode == "entity-query-incomplete"
                && partial.Items.Select(item => item.Snapshot != null).SequenceEqual(new[] { true, false, true })
                && partial.Items.Single(item => item.Snapshot == null).Code == "entity-observation-unavailable",
                "a set the kernel could not observe completely keeps the kernel's own code ["
                + partial.Status + ":" + partial.Code + "]");
            Reject("entity-query-incomplete", () => partial.RequireComplete(),
                "RequireComplete refuses the partial answer with the kernel's own code");
            Reject("entity-query-incomplete", () => ObservedSpaceNodes.Observe(session, new[] { a.Ref, b.Ref, c.Ref }),
                "ObservedSpaceNodes.Observe refuses a partial set instead of ranking the rest");
            world.OnObserve = null;
            Reject("entity-query-budget", () => ObservedSpaceNodes.Observe(session,
                    Enumerable.Repeat(a.Ref, RuntimeKernel.MaximumEntityReferencesPerQuery + 1).ToArray()),
                "one reference past the per-query limit is refused rather than split into batches");
            Check(session.TrySnapshots(Enumerable.Repeat(a.Ref, RuntimeKernel.MaximumEntityReferencesPerQuery).ToArray(),
                    out var wide, out _) && wide.IsComplete && wide.Requested == RuntimeKernel.MaximumEntityReferencesPerQuery
                && wide.Distinct == 1,
                "the per-query limit itself is a legal single call");
        }
        // The per-tick budget is the kernel's, not a caller's: a read that spends the query ceiling stops answering,
        // and batching wider calls cannot buy more references than the tick holds.
        using (var world = new World(new[] { a, b, c }))
        {
            var session = world.Session();
            var succeeded = 0; var refused = 0; string? refusal = null;
            for (var i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick + 1; i++)
            {
                if (session.TrySnapshots(new[] { a.Ref }, out _, out var refusalCode)) succeeded++;
                else { refused++; refusal = refusalCode; }
            }
            Check(succeeded == RuntimeKernel.MaximumEntityQueriesPerTick && refused == 1
                && refusal == "entity-query-tick-budget",
                $"one call is one query: {succeeded} calls answered, {refused} refused [" + refusal + "]");
        }
        using (var world = new World(new[] { a, b, c }))
        {
            var session = world.Session();
            var wide = Enumerable.Repeat(a.Ref, RuntimeKernel.MaximumEntityReferencesPerQuery).ToArray();
            var succeeded = 0; var refused = 0; string? refusal = null;
            for (var i = 0; i < RuntimeKernel.MaximumEntityReferencesPerTick / RuntimeKernel.MaximumEntityReferencesPerQuery + 1; i++)
            {
                if (session.TrySnapshots(wide, out _, out var refusalCode)) succeeded++;
                else { refused++; refusal = refusalCode; }
            }
            Check(succeeded == RuntimeKernel.MaximumEntityReferencesPerTick / RuntimeKernel.MaximumEntityReferencesPerQuery
                && refused == 1 && refusal == "entity-query-tick-budget",
                $"the tick's reference ceiling is not avoided by batching: {succeeded} wide calls, {refused} refused [" + refusal + "]");
        }
        // The readings a snapshot may carry and the parent edge the kernel derives from them: both travel through
        // the same one budgeted read, and a field the observer did not publish stays null rather than a zero.
        using (var world = new World(new[] { a, b, c }))
        {
            var session = world.Session();
            world.Readings = reference => reference == a.Ref
                ? new RuntimeEntitySnapshot(reference, "enemy", null, "alive", Array.Empty<string>(), Array.Empty<string>(),
                    new[] { 0d, 0d, 0d }, 40d, 100d, new[] { 0d, 0d, 1d }, "alerted", 3.5d, c.Ref)
                : world.Snapshot(reference);
            Check(session.TrySnapshots(new[] { a.Ref, b.Ref }, out var readings, out _) && readings.IsComplete
                && readings.Items[0].Snapshot!.Health == 40 && readings.Items[0].Snapshot!.HealthMaximum == 100
                && readings.Items[0].Snapshot!.AiState == "alerted" && readings.Items[0].Snapshot!.Speed == 3.5
                && readings.Items[0].Snapshot!.Parent == c.Ref
                && readings.Items[0].Snapshot!.Rotation!.SequenceEqual(new[] { 0d, 0d, 1d })
                && readings.Items[1].Snapshot!.Health == null && readings.Items[1].Snapshot!.Parent == null,
                "the optional readings travel through the session read and stay unknown where none was published");
            var children = world.Kernel.InspectChildren(c.Ref);
            Check(children.IsComplete && children.Items.Count == 1 && children.Items[0].Reference == a.Ref,
                "observing a child under a parent is what the kernel's reverse index answers with ["
                + children.Status + ":" + children.Code + ":" + children.Items.Count + "]");
            world.Readings = null;
        }
        // A rejected read keeps the kernel's own code and answers no result at all; a step that may not read the
        // world is refused by `query-authority` before the kernel is ever asked.
        using (var world = new World(new[] { a }))
        {
            var notReady = new RuntimeKernel(Fixture.Identity);
            Check(!notReady.InspectEntities(new[] { a.Ref }).IsComplete,
                "a runtime that was never started rejects its read");
            Check(!new RuntimeQuerySession(notReady, "test.snapshot.node", true)
                    .TrySnapshots(new[] { a.Ref }, out var rejected, out var rejectedCode)
                && rejected == null && rejectedCode == "runtime-not-ready",
                "a rejected read answers no result and keeps the kernel's code [" + rejectedCode + "]");
            var authority = world.Session(false);
            Check(!authority.TrySnapshots(new[] { a.Ref }, out var unauthorised, out var authorityCode)
                && unauthorised == null && authorityCode == RuntimeAbiCodes.QueryAuthority,
                "a step with no world authority is refused by name [" + authorityCode + "]");
            Reject(RuntimeAbiCodes.QueryAuthority, () => ObservedSpaceNodes.Observe(authority, new[] { a.Ref }),
                "ObservedSpaceNodes.Observe reports the session's own authority refusal");
        }
        // The caller's own bound is decided before the read: the public pre-check names the per-query limit even
        // when the read would have been refused by the kernel anyway.
        using (var world = new World(new[] { a }))
        {
            var before = world.Observations;
            Reject("entity-query-budget", () => ObservedSpaceNodes.Observe(world.Session(),
                    Enumerable.Repeat(a.Ref, RuntimeKernel.MaximumEntityReferencesPerQuery + 1).ToArray()),
                "ObservedSpaceNodes.Observe bounds the request before the read");
            Check(world.Observations == before, "an over-wide request observes nothing [" + (world.Observations - before) + "]");
        }
        return checks;
    }

    private static RuntimeEntitySnapshot At(string id, double x)
        => new(new EntityReference(Kind + ":" + id, 1, 1), "enemy", null, "alive",
            Array.Empty<string>(), new[] { "test.receiver" }, new[] { x, 0d, 0d });

    /// <summary>The fixture's world: one entity kind owned through the public registration path, so a session read is
    /// answered by a real resolver and observer. The observer is replaceable so one reference can be made
    /// unobservable without touching the resolvers that decide currency.</summary>
    private sealed class World : IDisposable
    {
        private readonly RuntimeModuleHandle handle;
        internal readonly RuntimeKernel Kernel = new(Fixture.Identity);
        internal int Observations { get; private set; }
        internal Func<EntityReference, RuntimeEntitySnapshot?>? OnObserve;
        /// <summary>Replaces what one reference observes, for a case about the readings a snapshot carries.</summary>
        internal Func<EntityReference, RuntimeEntitySnapshot?>? Readings;
        internal World(IEnumerable<RuntimeEntitySnapshot> entities)
        {
            var table = entities.ToDictionary(entity => entity.Ref, entity => entity);
            handle = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
            {
                providers = new[] { new { id = Kind, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
                capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
            }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
                new Dictionary<string, Func<EntityReference, bool>> { [Kind] = reference => table.ContainsKey(reference) })
            {
                EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { [Kind] = Observe }
            }, RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => Kernel.BeginWorld(1));
            Kernel.Advance(0, true);
        }
        private RuntimeEntitySnapshot? Observe(EntityReference reference)
        {
            Observations++;
            if (Readings != null) return Readings(reference);
            return OnObserve is null ? Snapshot(reference) : OnObserve(reference);
        }
        internal RuntimeEntitySnapshot? Snapshot(EntityReference reference)
            => reference.WorldEpoch == 1 && reference.LifeEpoch == 1 && Kernel.WorldEpoch == 1
                ? new RuntimeEntitySnapshot(reference, "enemy", null, "alive", Array.Empty<string>(),
                    new[] { "test.receiver" }, new[] { 0d, 0d, 0d })
                : null;
        internal RuntimeQuerySession Session(bool available = true) => new(Kernel, "test.snapshot.node", available);
        public void Dispose() { Kernel.StopRuntime(); handle.Dispose(); }
    }
}
