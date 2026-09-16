using System.Text.Json;
using ForgeDevelopment.Native;
using ForgeRuntime.Framework;

namespace ForgeDevelopment.Tests.DevModule;

/// <summary>
/// The focused suite for the authoring-only diagnostic provider. Every case runs the production handler through
/// the kernel's own `CommandContext` shape and asserts two things: the result row's own columns, and what the
/// session ledger recorded. Nothing here is a claim about the game: the rows read no game state, so the only
/// world under them is this project's fixture kernel.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.Error.WriteLine("FAIL: " + name);
    }

    private static int Main()
    {
        Registration();
        ShapeAlignment();
        SuccessPaths();
        Refusals();
        WorldLifetime();
        Console.WriteLine($"checks={_checks} failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Registration: the four implemented rows are declared and bound, the three the design leaves open
    /// are absent, and a caller that is not the authoring runtime gets the named refusal instead of four
    /// endpoints.</summary>
    private static void Registration()
    {
        using (var world = new ModuleWorld())
        {
            Check(world.Registered && world.RegistrationCode == "diagnostic-registered", "authoring registration succeeds");
            Check(DevelopmentModule.IsRegistered, "the module reports itself registered");
            Check(DevelopmentModule.SinkDrops == 0, "no record was dropped for want of a sink");
            var registry = JsonDocument.Parse(DevelopmentModule.Registry()).RootElement;
            var capabilities = registry.GetProperty("capabilities").EnumerateArray()
                .Select(c => c.GetProperty("id").GetString()!).ToArray();
            Check(capabilities.SequenceEqual(new[]
            {
                DevelopmentModule.TraceCapability, DevelopmentModule.AssertCapability,
                DevelopmentModule.InspectCapability, DevelopmentModule.MetricCapability
            }), "the four implemented rows are declared in order");
            foreach (var absent in new[]
            {
                DevelopmentModule.BreakpointCapability, DevelopmentModule.SpawnPreviewCapability,
                DevelopmentModule.CostEstimateCapability
            })
                Check(!capabilities.Contains(absent), "the unregistered row is absent: " + absent);
            var bindings = registry.GetProperty("bindings").EnumerateArray().ToArray();
            Check(bindings.Length == 4 && bindings.All(b => b.GetProperty("status").GetString() == "implemented"),
                "every declared row is bound as implemented");
            Check(bindings.All(b => b.GetProperty("providerId").GetString() == DevelopmentModule.ProviderId),
                "every binding is owned by this provider");
            var rows = registry.GetProperty("capabilities").EnumerateArray().ToArray();
            Check(rows.All(r => r.GetProperty("graph").GetProperty("execution").GetString() == "presentation"),
                "every row keeps the presentation tier");
            Check(rows.All(r => r.GetProperty("owner").GetString() == DevelopmentModule.ProviderId),
                "every capability row names its owner");
            var inspect = rows.Single(c => c.GetProperty("id").GetString() == DevelopmentModule.InspectCapability)
                .GetProperty("graph").GetProperty("recipients");
            Check(inspect.GetProperty("target").GetString() == "entity" && inspect.GetProperty("cardinality").GetString() == "many",
                "inspect addresses the entity collection the catalog declares");
            var session = rows.Single(c => c.GetProperty("id").GetString() == DevelopmentModule.MetricCapability)
                .GetProperty("graph").GetProperty("inputs").EnumerateArray()
                .Single(p => p.GetProperty("id").GetString() == "session");
            Check(session.GetProperty("type").GetString() == "handle"
                && session.GetProperty("handleKind").GetString() == "request"
                && session.GetProperty("lifetime").GetString() == "session",
                "the session handle is the request/session handle the kernel casts");
        }
        DevelopmentModule.Forget();

        using (var world = new ModuleWorld(enabled: false))
        {
            Check(!world.Registered && world.RegistrationCode == DiagnosticCodes.Disabled,
                "a non-authoring caller is refused by name");
            Check(!DevelopmentModule.IsRegistered, "a refused registration leaves no module behind");
            Check(!world.Registered && world.RegistrationCode == DiagnosticCodes.Disabled,
                "the refusal is the author gate and not a kernel rejection");
        }
        DevelopmentModule.Forget();

        // The registration the game runs is the same one the plugin makes; `enabled` is the author gate and its
        // default is the authoring case, so the plugin cannot forget to pass it.
        using var authoring = new ModuleWorld();
        Check(authoring.Registered, "the gate defaults to the authoring case");
    }

    /// <summary>Each handler's own declared port set has to be the capability's own: the kernel resolves the shape
    /// against the graph at registration, and this pins the two lists together so a renamed port fails here instead
    /// of at a plan load.</summary>
    private static void ShapeAlignment()
    {
        var registry = JsonDocument.Parse(DevelopmentModule.Registry()).RootElement;
        foreach (var (handler, capability, shape) in new[]
        {
            (DevelopmentModule.TraceHandlerName, DevelopmentModule.TraceCapability, DevelopmentModule.TraceShape),
            (DevelopmentModule.AssertHandlerName, DevelopmentModule.AssertCapability, DevelopmentModule.AssertShape),
            (DevelopmentModule.InspectHandlerName, DevelopmentModule.InspectCapability, DevelopmentModule.InspectShape),
            (DevelopmentModule.MetricHandlerName, DevelopmentModule.MetricCapability, DevelopmentModule.MetricShape)
        })
        {
            var graph = registry.GetProperty("capabilities").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == capability).GetProperty("graph");
            var declaredInputs = graph.GetProperty("inputs").EnumerateArray()
                .Where(p => p.GetProperty("type").GetString() != "execution")
                .Select(p => p.GetProperty("id").GetString()).ToArray();
            var declaredOutputs = graph.GetProperty("outputs").EnumerateArray()
                .Where(p => p.GetProperty("type").GetString() != "execution")
                .Select(p => p.GetProperty("id").GetString()).ToArray();
            var declaredParameters = graph.GetProperty("parameters").EnumerateArray()
                .Select(p => p.GetProperty("id").GetString()!).ToArray();
            Check(shape.InputPorts.SequenceEqual(declaredInputs), handler + " shape names the capability's inputs");
            Check(shape.OutputPorts.SequenceEqual(declaredOutputs), handler + " shape names the capability's outputs");
            Check(shape.ParameterIds.SequenceEqual(declaredParameters), handler + " shape names the capability's parameters");
        }

        using var world = new ModuleWorld();
        Check(world.Registered, "every shape resolves against its capability");
        Check(world.Kernel.ExportManifest().Contains(DevelopmentModule.TraceHandlerName, StringComparison.Ordinal),
            "the manifest carries the handler the module registered");
    }

    /// <summary>What a row answers when everything it asks for is there: the row's own columns, the record the
    /// ledger kept, and the non-committing result the `presentation` tier requires.</summary>
    private static void SuccessPaths()
    {
        using var world = new ModuleWorld();
        var actor = world.Actor("a1", lifeState: "alive", faction: "hostile", x: 1.5, y: 2, z: -3);
        var handlers = DevelopmentModule.HandlersForTest();

        var trace = handlers[DevelopmentModule.TraceHandlerName](TraceCommand(world, actor.Reference));
        Check(trace.Status == CommandStatuses.Rejected && trace.CommitState == CommitStates.None,
            "a presentation row reports no commit");
        Check(trace.Code == "traced" && ModuleWorld.Field(trace, "status") == CommandStatuses.Succeeded,
            "a taken trace reports the row's own success");
        Check(ModuleWorld.Field(trace, "committed") == CommitStates.None, "the row commits nothing");
        Check(ModuleWorld.Columns(trace).SequenceEqual(new[] { "target", "status", "committed", "code", "sampleRate" }),
            "the trace row carries the schema's own columns in order");
        Check(ModuleWorld.Field(trace, "sampleRate") == "1", "the row reports the rate it used");
        Check(world.Records.Count == 1 && world.Records[0].Category == "diagnostic_trace"
            && world.Records[0].Subject == "fixture.development.plan"
            && world.Records[0].Fields["nodeId"] == "fixture.development.node"
            && world.Records[0].Fields["eventId"] == "fixture.development.event"
            && world.Records[0].Fields["graphId"] == "fixture.development.resource"
            && world.Records[0].Fields["worldEpoch"] == "11",
            "the trace record carries the identities the row's requirements name");

        // A rate of two takes every second call and reports the first as skipped, never as recorded.
        var sampledOut = handlers[DevelopmentModule.TraceHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, sample_rate = 2 }));
        Check(sampledOut.Code == "traced" && world.Records.Count == 2, "the call the rate takes is recorded");
        var sampledIn = handlers[DevelopmentModule.TraceHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, sample_rate = 2 }));
        Check(sampledIn.Code == "sampled-out" && world.Records.Count == 2, "a call the rate skips records nothing");

        for (var index = 0; index < 3; index++)
        {
            var metric = handlers[DevelopmentModule.MetricHandlerName](ModuleWorld.Context(
                new { session = world.SessionHandle, value = 5.0, window = 100, sample_rate = 1 }));
            Check(metric.Code == "metric-recorded" && ModuleWorld.Field(metric, "committed") == CommitStates.None,
                "each metric sample is recorded without a commit");
            if (index != 2) continue;
            var fields = world.Records[^1].Fields;
            Check(fields["samples"] == "3" && fields["sum"] == "15" && fields["minimum"] == "5"
                && fields["maximum"] == "5" && fields["last"] == "5" && fields["window"] == "100",
                "the metric aggregate covers the samples the session took");
        }

        var held = handlers[DevelopmentModule.AssertHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, condition = true, message = "holds" },
            new { policy = "report", severity = "warning" }));
        Check(held.Code == "assert-held" && DevelopmentModule.Sessions!.FailedAssertions == 0,
            "a held invariant is recorded and counted as no failure");
        Check(world.Records[^1].Fields["severity"] == "warning" && world.Records[^1].Fields["outcome"] == "held",
            "the assert record carries its severity and outcome");
        var reported = handlers[DevelopmentModule.AssertHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, condition = false, message = "broke" },
            new { policy = "report", severity = "error" }));
        Check(reported.Code == "assert-reported" && DevelopmentModule.Sessions!.FailedAssertions == 1,
            "a failure under report is recorded and counted without stopping");
        var counted = handlers[DevelopmentModule.AssertHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, condition = false, message = "broke" },
            new { policy = "record", severity = "error" }));
        Check(counted.Code == "assert-failed" && ModuleWorld.Field(counted, "status") == CommandStatuses.Partial
            && DevelopmentModule.Sessions!.FailedAssertions == 2,
            "a failure under record reaches the row as a partial outcome");
        Check(world.Records[^1].Fields["message"] == "broke" && world.Records[^1].Fields["condition"] == "false",
            "the assert record carries the failing condition and its message");

        var inspect = handlers[DevelopmentModule.InspectHandlerName](ModuleWorld.Context(
            new { targets = new[] { ModuleWorld.Entity(actor.Reference) }, fields = "kind,lifeState,position", budget = 4 }));
        Check(inspect.Code == "inspected" && ModuleWorld.Field(inspect, "status") == CommandStatuses.Succeeded,
            "an inspect of a current entity succeeds");
        Check(ModuleWorld.Field(inspect, "budget") == "4" && ModuleWorld.Field(inspect, "targetCount") == "1",
            "the inspect row reports the budget and the target count");
        var snapshot = inspect.Outputs.GetProperty("snapshot").GetString() ?? "";
        Check(snapshot.Contains("ref=" + actor.Reference.Id, StringComparison.Ordinal)
            && snapshot.Contains("kind=" + ModuleWorld.ActorKind, StringComparison.Ordinal)
            && snapshot.Contains("lifeState=alive", StringComparison.Ordinal)
            && snapshot.Contains("position=1.5", StringComparison.Ordinal)
            && !snapshot.Contains("faction", StringComparison.Ordinal),
            "the snapshot is projected onto exactly the fields the row asked for");
        Check(inspect.Outputs.TryGetProperty("results", out _) && inspect.Outputs.TryGetProperty("snapshot", out _)
            && inspect.Outputs.EnumerateObject().Count() == 2,
            "inspect answers with its row and its snapshot and nothing else");
    }

    /// <summary>What the rows answer when the session, the arguments, the target set or the policy are not what
    /// they require.</summary>
    private static void Refusals()
    {
        using var world = new ModuleWorld();
        var actor = world.Actor("a1");
        var handlers = DevelopmentModule.HandlersForTest();

        var noSession = handlers[DevelopmentModule.TraceHandlerName](ModuleWorld.Context(new { sample_rate = 1 }));
        Check(noSession.Status == CommandStatuses.Rejected && noSession.CommitState == CommitStates.None
            && noSession.Code == DiagnosticCodes.SessionMissing
            && ModuleWorld.Field(noSession, "code") == DiagnosticCodes.SessionMissing,
            "trace without a session is refused by name");

        var staleSession = handlers[DevelopmentModule.TraceHandlerName](ModuleWorld.Context(new { session = 42 }));
        Check(staleSession.Code is "diagnostic-handle-foreign" or DiagnosticCodes.StaleSession,
            "a value that is not a handle is refused");

        var badRate = handlers[DevelopmentModule.TraceHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, sample_rate = 0 }));
        Check(badRate.Code == DiagnosticCodes.SampleRateInvalid, "a sample rate below one is refused");

        var badPolicy = handlers[DevelopmentModule.AssertHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, condition = false, message = "x" },
            new { policy = "halt", severity = "error" }));
        Check(badPolicy.Code == DiagnosticCodes.PolicyUnsupported,
            "a policy this build cannot keep is refused by name");

        var badSeverity = handlers[DevelopmentModule.AssertHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, condition = true, message = "x" },
            new { policy = "report", severity = "loud" }));
        Check(badSeverity.Code == "diagnostic-severity-unknown", "an unknown severity is refused");

        var badCondition = handlers[DevelopmentModule.AssertHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, message = "x" }, new { policy = "report", severity = "info" }));
        Check(badCondition.Code == "diagnostic-condition-invalid", "an absent condition is refused");

        var badWindow = handlers[DevelopmentModule.MetricHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, value = 1.0, window = 0, sample_rate = 1 }));
        Check(badWindow.Code == "diagnostic-argument-invalid", "a window below one tick is refused");

        var badValue = handlers[DevelopmentModule.MetricHandlerName](ModuleWorld.Context(
            new { session = world.SessionHandle, window = 1, sample_rate = 1 }));
        Check(badValue.Code == "diagnostic-argument-invalid", "an absent sample value is refused");

        var badBudget = handlers[DevelopmentModule.InspectHandlerName](ModuleWorld.Context(
            new { targets = new[] { ModuleWorld.Entity(actor.Reference) }, fields = "", budget = 0 }));
        Check(badBudget.Code == "diagnostic-budget-invalid", "a call budget of zero is refused");

        var overBudget = handlers[DevelopmentModule.InspectHandlerName](ModuleWorld.Context(
            new { targets = new[] { ModuleWorld.Entity(actor.Reference) }, fields = "", budget = 33 }));
        Check(overBudget.Code == "diagnostic-budget-invalid", "a call budget above the ceiling is refused");

        world.Actor("a2");
        var tooMany = handlers[DevelopmentModule.InspectHandlerName](ModuleWorld.Context(
            new { targets = new[] { ModuleWorld.Entity(actor.Reference), ModuleWorld.Entity(world.Actor("a2").Reference) },
                fields = "", budget = 1 }));
        Check(tooMany.Code == DiagnosticCodes.Budget && ModuleWorld.Field(tooMany, "status") == CommandStatuses.Partial,
            "more targets than the call budget allows is a counted partial, not a silent truncation");

        var unknownField = handlers[DevelopmentModule.InspectHandlerName](ModuleWorld.Context(
            new { targets = new[] { ModuleWorld.Entity(actor.Reference) }, fields = "kind,health", budget = 4 }));
        Check(unknownField.Code == DiagnosticCodes.FieldsUnknown, "a field the runtime cannot read is refused by name");

        var noTargets = handlers[DevelopmentModule.InspectHandlerName](ModuleWorld.Context(new { fields = "", budget = 4 }));
        Check(noTargets.Code == "diagnostic-targets-missing", "inspect without its target collection is refused");

        world.Drop("a1");
        var unavailable = handlers[DevelopmentModule.InspectHandlerName](ModuleWorld.Context(
            new { targets = new[] { ModuleWorld.Entity(actor.Reference) }, fields = "", budget = 4 }));
        Check(unavailable.Code == DiagnosticCodes.EntityUnavailable
            && ModuleWorld.Field(unavailable, "status") == CommandStatuses.Partial,
            "an entity the world no longer holds is refused, never answered with an empty snapshot");
    }

    /// <summary>The life of a session and of the records it holds: a world change drops both, and the handle a plan
    /// still carries is then refused instead of being read against the new world.</summary>
    private static void WorldLifetime()
    {
        using var world = new ModuleWorld();
        var handlers = DevelopmentModule.HandlersForTest();
        Check(DevelopmentModule.Sessions!.HasSession, "a session exists while the world does");
        Check(DevelopmentModule.Sessions!.TryBegin(1, out _, out var reopened) && reopened == "diagnostic-session-open",
            "a second begin is answered with the live session");

        var record = new DiagnosticRecord("diagnostic_trace", "node", "plan", new Dictionary<string, string>(), 0);
        DevelopmentModule.Sink(record);
        Check(world.Records.Count == 1 && world.Records[0].Category == "diagnostic_trace",
            "the sink received the record");

        var trace = handlers[DevelopmentModule.TraceHandlerName](TraceCommand(world, world.Actor("a1").Reference));
        Check(trace.Code == "traced", "the session records before the world ends");
        Check(DevelopmentModule.Sessions!.Records == 1 && DevelopmentModule.Sessions!.Dropped == 0,
            "the ledger counts what it really recorded");
        // The handle a plan is still holding when the world ends, kept as a value so the frame can be built after
        // the session itself is gone.
        var stale = world.SessionHandle;

        DevelopmentModule.Sessions.EndWorld();
        Check(!DevelopmentModule.Sessions.HasSession, "a world change drops the session");
        var afterWorld = handlers[DevelopmentModule.TraceHandlerName](ModuleWorld.Context(new { session = stale }));
        Check(afterWorld.Code is DiagnosticCodes.SessionMissing or DiagnosticCodes.StaleSession,
            "a session handle from an ended world is refused");
        Check(world.Records.Count == 2, "the refused row wrote no further record");
    }

    /// <summary>One trace frame over the fixture's session.</summary>
    private static CommandContext TraceCommand(ModuleWorld world, EntityReference target)
        => ModuleWorld.Context(new
        {
            session = world.SessionHandle,
            sample_rate = 1,
            graph = new { resourceKind = "behavior_graph", resourceId = "fixture.development.graph" }
        });
}
