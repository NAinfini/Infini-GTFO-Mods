using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

internal static class ExecutionResultTests
{
    private static int checks;

    private static void Check(bool condition, string name)
    {
        checks++;
        if (!condition) throw new Exception("FAIL execution-result: " + name);
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (RuntimeContractException) { checks++; return; }
        throw new Exception("FAIL execution-result: " + name + " did not reject");
    }

    private static RuntimeFact Fact(string bindingId, object payload)
        => new(bindingId, RuntimeJson.From(payload));

    private static void CheckInvalid(string status, string commitState, RuntimeFact[] facts, string name)
    {
        var result = CommandResult.Create(status, commitState, "test.invalid", "", RuntimeJson.EmptyObject, facts);
        Check(!CommandResultRules.TryValidate(result, out _), name);
    }

    internal static int Run()
    {
        var factBinding = Fixture.Trigger("example.alpha");
        var fact = new RuntimeFact(factBinding, RuntimeJson.EmptyObject);

        var succeeded = CommandResult.Succeeded(RuntimeJson.EmptyObject);
        Check(succeeded.Status == CommandStatuses.Succeeded && succeeded.CommitState == CommitStates.Confirmed && CommandResultRules.TryValidate(succeeded, out _),
            "succeeded is confirmed");

        var partialConfirmed = CommandResult.Partial(RuntimeJson.EmptyObject, CommitStates.Confirmed, fact);
        var partialUnknown = CommandResult.Partial(RuntimeJson.EmptyObject, CommitStates.Unknown, fact);
        Check(partialConfirmed.Status == CommandStatuses.Partial && partialConfirmed.CommitState == CommitStates.Confirmed && partialConfirmed.Facts.Count == 1
            && CommandResultRules.TryValidate(partialConfirmed, out _), "partial is valid with a known confirmed fact");
        Check(partialUnknown.Status == CommandStatuses.Partial && partialUnknown.CommitState == CommitStates.Unknown && partialUnknown.Facts.Count == 1
            && CommandResultRules.TryValidate(partialUnknown, out _), "partial is valid with unknown completion and a known fact");

        var rejected = CommandResult.Rejected("test.rejected");
        var failed = CommandResult.Failed("test.failed");
        var failedUnknown = CommandResult.FailedUnknown("test.failed_unknown");
        var cancelled = CommandResult.Cancelled("test.cancelled");
        var expired = CommandResult.Expired("test.expired");
        Check(rejected.Status == CommandStatuses.Rejected && rejected.CommitState == CommitStates.None && rejected.Facts.Count == 0 && CommandResultRules.TryValidate(rejected, out _),
            "rejected is none without facts");
        Check(failed.Status == CommandStatuses.Failed && failed.CommitState == CommitStates.None && failed.Facts.Count == 0 && CommandResultRules.TryValidate(failed, out _),
            "failed may be none without facts");
        Check(failedUnknown.Status == CommandStatuses.Failed && failedUnknown.CommitState == CommitStates.Unknown && failedUnknown.Facts.Count == 0 && CommandResultRules.TryValidate(failedUnknown, out _),
            "failed may be unknown without facts");
        Check(cancelled.Status == CommandStatuses.Cancelled && cancelled.CommitState == CommitStates.None && cancelled.Facts.Count == 0 && CommandResultRules.TryValidate(cancelled, out _),
            "cancelled is none without facts");
        Check(expired.Status == CommandStatuses.Expired && expired.CommitState == CommitStates.None && expired.Facts.Count == 0 && CommandResultRules.TryValidate(expired, out _),
            "expired is none without facts");

        CheckInvalid(CommandStatuses.Succeeded, CommitStates.None, Array.Empty<RuntimeFact>(), "succeeded+none is invalid");
        CheckInvalid(CommandStatuses.Succeeded, CommitStates.Unknown, Array.Empty<RuntimeFact>(), "succeeded+unknown is invalid");
        CheckInvalid(CommandStatuses.Partial, CommitStates.None, new[] { fact }, "partial+none is invalid");
        CheckInvalid(CommandStatuses.Partial, CommitStates.Confirmed, Array.Empty<RuntimeFact>(), "partial without a known commit is invalid");
        CheckInvalid(CommandStatuses.Partial, CommitStates.Unknown, Array.Empty<RuntimeFact>(), "partial+unknown without a known commit is invalid");
        CheckInvalid(CommandStatuses.Rejected, CommitStates.Confirmed, Array.Empty<RuntimeFact>(), "rejected+confirmed is invalid");
        CheckInvalid(CommandStatuses.Rejected, CommitStates.None, new[] { fact }, "rejected facts are invalid");
        CheckInvalid(CommandStatuses.Cancelled, CommitStates.Unknown, Array.Empty<RuntimeFact>(), "cancelled+unknown is invalid");
        CheckInvalid(CommandStatuses.Cancelled, CommitStates.None, new[] { fact }, "cancelled facts are invalid");
        CheckInvalid(CommandStatuses.Expired, CommitStates.Confirmed, Array.Empty<RuntimeFact>(), "expired+confirmed is invalid");
        CheckInvalid(CommandStatuses.Expired, CommitStates.None, new[] { fact }, "expired facts are invalid");
        CheckInvalid(CommandStatuses.Failed, CommitStates.Confirmed, Array.Empty<RuntimeFact>(), "failed+confirmed is invalid");
        CheckInvalid(CommandStatuses.Failed, CommitStates.None, new[] { fact }, "failed+none facts are invalid");
        CheckInvalid("unknown", CommitStates.None, Array.Empty<RuntimeFact>(), "unknown status is invalid");

        var failedUnknownFact = CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, "test.failed_unknown_fact", "", RuntimeJson.EmptyObject, fact);
        Check(failedUnknownFact.Status == CommandStatuses.Failed && failedUnknownFact.CommitState == CommitStates.Unknown && failedUnknownFact.Facts.Count == 1
            && CommandResultRules.TryValidate(failedUnknownFact, out _), "failed+unknown can carry a known fact");

        var outputDoc = JsonDocument.Parse("{\"actual\":7}");
        var factDoc = JsonDocument.Parse("{\"delta\":3}");
        var snapshot = CommandResult.Succeeded(outputDoc.RootElement, new RuntimeFact(factBinding, factDoc.RootElement));
        outputDoc.Dispose(); factDoc.Dispose();
        Check(snapshot.Outputs.GetProperty("actual").GetInt32() == 7, "outputs are an immutable snapshot");
        Check(snapshot.Facts.Count == 1 && snapshot.Facts[0].Outputs.GetProperty("delta").GetInt32() == 3, "facts are immutable snapshots");

        Reject(() => CommandResult.Succeeded(RuntimeJson.EmptyObject,
            Enumerable.Repeat(new RuntimeFact(factBinding, RuntimeJson.EmptyObject), CommandResult.MaximumFacts + 1).ToArray()),
            "fact count is bounded");
        Reject(() => CommandResult.Succeeded(RuntimeJson.From(new { oversized = new string('x', RuntimeKernel.MaximumEventPayloadBytes) })),
            "output payload is bounded");
        Reject(() => CommandResult.Rejected("test.rejected", new string('d', CommandResult.MaximumDetailBytes + 1)),
            "detail is bounded");

        {
            var s = new Scenario(new RuntimeLimits { MaxEventsPerTick = 1 });
            var calls = 0;
            var a = s.Register("example.alpha", ctx =>
            {
                calls++;
                return CommandResult.Partial(RuntimeJson.EmptyObject,
                    new RuntimeFact(Fixture.Trigger("example.alpha"), RuntimeJson.From(new { target = ctx.GetEntityInput("target") })));
            });
            s.Plan("partial-plan", "example.alpha", steps: 2);
            a.Publish(s.Event("partial-event", "example.alpha"));
            var tick = s.Kernel.Advance(1, true);
            Check(calls == 1 && tick.CommandsExecuted == 1 && tick.Commands.Count == 1, "partial stops the entrypoint after its invoked step");
            var partialResult = tick.Commands[0].Result;
            Check(partialResult.Status == CommandStatuses.Partial && partialResult.CommitState == CommitStates.Confirmed && partialResult.Facts.Count == 1,
                "partial carries its confirmed fact evidence");
            Check(tick.DeferredEvents == 1 && s.Kernel.QueuedEvents == 1, "partial publishes its confirmed facts before stopping");
        }

        {
            var s = new Scenario();
            var calls = 0;
            var a = s.Register("example.alpha", _ => { calls++; throw new InvalidOperationException("boom"); });
            s.Plan("exception-plan", "example.alpha", steps: 2);
            a.Publish(s.Event("exception-event", "example.alpha"));
            var tick = s.Kernel.Advance(1, true);
            Check(calls == 1 && tick.CommandsExecuted == 1 && tick.Commands.Count == 1, "handler exception stops later steps");
            var result = tick.Commands[0].Result;
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown && result.Code == "handler-exception",
                "handler exception is failed+unknown");
            Check(result.Facts.Count == 0, "handler exception reports no fabricated facts");
            Check(a.Publish(s.Event("exception-event", "example.alpha")).Status == "duplicate", "failed+unknown is not retried");
            Check(s.Kernel.Advance(2, true).CommandsExecuted == 0, "duplicate event does not re-run after failure");
        }

        {
            var s = new Scenario();
            var a = s.Register("example.alpha", _ => null!);
            s.Plan("null-plan", "example.alpha");
            a.Publish(s.Event("null-event", "example.alpha"));
            var tick = s.Kernel.Advance(1, true);
            var result = tick.Commands.Single().Result;
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown && result.Code == "null-result",
                "null handler result is failed+unknown");
        }

        {
            var s = new Scenario(new RuntimeLimits { MaxEventsPerTick = 1 });
            var a = s.Register("example.alpha", ctx => CommandResult.Create(
                CommandStatuses.Rejected, CommitStates.Confirmed, "invalid.combination", "",
                RuntimeJson.EmptyObject,
                new RuntimeFact(Fixture.Trigger("example.alpha"), RuntimeJson.From(new { target = ctx.GetEntityInput("target") }))));
            s.Plan("invalid-plan", "example.alpha", steps: 2);
            a.Publish(s.Event("invalid-event", "example.alpha"));
            var tick = s.Kernel.Advance(1, true);
            Check(tick.CommandsExecuted == 1 && tick.Commands.Count == 1, "invalid handler result stops later steps");
            var result = tick.Commands[0].Result;
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown && result.Code == "invalid-handler-result",
                "invalid handler result is failed+unknown");
            Check(result.Facts.Count == 1, "invalid handler result preserves already confirmed facts");
            Check(tick.DeferredEvents == 1, "confirmed facts from an invalid result are still published");
        }

        {
            var s = new Scenario(); RuntimeModuleHandle? a = null;
            a = s.Register("example.alpha", _ => { a!.CancelScope("shared-scope"); return CommandResult.Succeeded(RuntimeJson.EmptyObject); });
            s.Plan("cancel-plan", "example.alpha", steps: 2);
            a.Publish(s.Event("cancel-event", "example.alpha"));
            var tick = s.Kernel.Advance(1, true);
            Check(tick.CommandsExecuted == 1 && tick.Commands.Count == 2, "cancellation after the handler leaves the next step uninvoked");
            Check(tick.Commands[0].Result.Status == CommandStatuses.Succeeded && tick.Commands[0].Result.CommitState == CommitStates.Confirmed,
                "first step committed before cancellation");
            var result = tick.Commands[1].Result;
            Check(result.Status == CommandStatuses.Cancelled && result.CommitState == CommitStates.None && result.Code == "scope-cancelled" && result.Facts.Count == 0,
                "pre-invocation scope cancellation is cancelled+none");
        }

        {
            var s = new Scenario(); var a = s.Register("example.alpha"); s.Plan("stale-plan", "example.alpha");
            a.Publish(s.Event("stale-event", "example.alpha")); s.Life = 2;
            var tick = s.Kernel.Advance(1, true);
            Check(tick.CommandsExecuted == 0 && tick.Commands.Count == 1, "stale entity never invokes the handler");
            var result = tick.Commands[0].Result;
            Check(result.Status == CommandStatuses.Cancelled && result.CommitState == CommitStates.None && result.Code == "stale-entity" && result.Facts.Count == 0,
                "stale entity is cancelled+none");
        }

        {
            var s = new Scenario();
            var resolverCalls = 0;
            Func<EntityReference, bool> resolver = _ =>
            {
                resolverCalls++;
                if (resolverCalls == 1) return true;
                throw new InvalidOperationException("resolver failed");
            };
            var module = Fixture.Module("example.alpha") with { EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { ["example.alpha"] = resolver } };
            var a = s.Kernel.RegisterModule(module);
            s.Plan("resolver-plan", "example.alpha");
            a.Publish(s.Event("resolver-event", "example.alpha"));
            var tick = s.Kernel.Advance(1, true);
            Check(resolverCalls >= 2 && tick.CommandsExecuted == 0 && tick.Commands.Count == 1, "non-cancel pre-invocation failure never invokes the handler");
            var result = tick.Commands[0].Result;
            Check(result.Status == CommandStatuses.Rejected && result.CommitState == CommitStates.None && result.Code == "entity-resolver-failed" && result.Facts.Count == 0,
                "other pre-invocation contract failure is rejected+none");
        }

        {
            var publicMethods = typeof(RuntimeKernel).GetMethods().Select(m => m.Name).ToArray();
            Check(!publicMethods.Any(n => n.Contains("Task", StringComparison.Ordinal) || n.Contains("Timeout", StringComparison.Ordinal)
                || n.Contains("Child", StringComparison.Ordinal) || n.Contains("Deadline", StringComparison.Ordinal)
                || n.Contains("Recover", StringComparison.Ordinal) || n.Contains("ResultGraph", StringComparison.Ordinal)),
                "Task.Run, wall-clock timeout, parent-child task tree, deadline, recovery, and result graph remain unsupported in this slice");
        }

        Console.WriteLine($"Execution result checks: {checks} passed.");
        return checks;
    }
}
