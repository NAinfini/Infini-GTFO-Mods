using Infini.ForgeRuntime;

var tests = new (string Name, Action Run)[]
{
    ("host dispatch invokes observers in registration order", HostDispatch),
    ("non-host cannot schedule canonical triggers", NonHostRejected),
    ("duplicate event id executes only once", DuplicateSuppressed),
    ("observer failure is isolated and traced", ObserverFailureIsolated),
    ("subscription removal affects future events only", SubscriptionRemoval),
    ("checkpoint restores state and dedupe history", CheckpointRestore),
    ("new expedition clears state and increments generation", ExpeditionReset),
    ("invalid canonical namespaces fail closed", InvalidNamespace),
    ("reentrant duplicate cannot execute twice", ReentrantDuplicate),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures.Add($"{test.Name}: {error}");
        Console.WriteLine($"FAIL {test.Name}: {error.Message}");
    }
}
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} passed");
if (failures.Count != 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    Environment.Exit(1);
}

static ForgeTriggerEvent E(string id, long sequence = 1) =>
    new(id, "forge.trigger.door.opened", "door:42", sequence,
        new Dictionary<string, string> { ["door"] = "42" });

static void Equal<T>(T expected, T actual, string? message = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception(message ?? $"expected {expected}, got {actual}");
}

static void True(bool value, string? message = null)
{
    if (!value) throw new Exception(message ?? "expected true");
}

static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T error) { return error; }
    throw new Exception($"expected {typeof(T).Name}");
}

static void HostDispatch()
{
    var host = true;
    var runtime = new CanonicalTriggerRuntime(() => host);
    var order = new List<string>();
    runtime.Observe("forge.trigger.door.opened", "adapter.cee", _ => order.Add("cee"));
    runtime.Observe("forge.trigger.door.opened", "adapter.cte", _ => order.Add("cte"));
    var result = runtime.Publish(E("event-1"));
    Equal(ForgeDispatchStatus.Accepted, result.Status);
    Equal(2, result.ObserversInvoked);
    Equal("cee,cte", string.Join(',', order));
    Equal(0, result.Failures.Count);
}

static void NonHostRejected()
{
    var host = false;
    var runtime = new CanonicalTriggerRuntime(() => host);
    var called = 0;
    runtime.Observe("forge.trigger.door.opened", "adapter.cee", _ => called++);
    var result = runtime.Publish(E("event-client"));
    Equal(ForgeDispatchStatus.RejectedNotHost, result.Status);
    Equal(0, called);
    True(runtime.GetTrace().Any(row => row.Kind == ForgeTraceKind.EventRejectedNotHost));
    Throws<InvalidOperationException>(() => runtime.CaptureCheckpoint());
}

static void DuplicateSuppressed()
{
    var runtime = new CanonicalTriggerRuntime(() => true);
    var called = 0;
    runtime.Observe("forge.trigger.door.opened", "adapter.eec", _ => called++);
    Equal(ForgeDispatchStatus.Accepted, runtime.Publish(E("same-event")).Status);
    Equal(ForgeDispatchStatus.Duplicate, runtime.Publish(E("same-event", 2)).Status);
    Equal(1, called);
}

static void ObserverFailureIsolated()
{
    var runtime = new CanonicalTriggerRuntime(() => true);
    var second = false;
    runtime.Observe("forge.trigger.door.opened", "adapter.bad", _ => throw new InvalidOperationException("boom"));
    runtime.Observe("forge.trigger.door.opened", "adapter.good", context => { second = true; context.SetState("door.42", "opened"); });
    var result = runtime.Publish(E("observer-failure"));
    Equal(2, result.ObserversInvoked);
    Equal(1, result.Failures.Count);
    True(second);
    True(runtime.GetTrace().Any(row => row.Kind == ForgeTraceKind.ObserverFailed && row.ProviderId == "adapter.bad"));
}

static void SubscriptionRemoval()
{
    var runtime = new CanonicalTriggerRuntime(() => true);
    var called = 0;
    using var subscription = runtime.Observe("forge.trigger.door.opened", "adapter.cee", _ => called++);
    runtime.Publish(E("sub-1"));
    subscription.Dispose();
    runtime.Publish(E("sub-2", 2));
    Equal(1, called);
}

static void CheckpointRestore()
{
    var runtime = new CanonicalTriggerRuntime(() => true);
    runtime.Observe("forge.trigger.door.opened", "forge.test", context => context.SetState("door", "open"));
    runtime.Publish(E("checkpoint-event"));
    var checkpoint = runtime.CaptureCheckpoint();
    runtime.ResetForExpedition();
    runtime.RestoreCheckpoint(checkpoint);
    Equal(ForgeDispatchStatus.Duplicate, runtime.Publish(E("checkpoint-event", 3)).Status);
    var observed = false;
    runtime.Observe("forge.trigger.door.opened", "forge.read", context =>
    {
        observed = context.TryGetState("door", out var value) && value == "open";
    });
    runtime.Publish(E("checkpoint-next", 4));
    True(observed);
}

static void ExpeditionReset()
{
    var runtime = new CanonicalTriggerRuntime(() => true);
    runtime.Observe("forge.trigger.door.opened", "forge.state", context => context.SetState("x", "1"));
    runtime.Publish(E("old"));
    var generation = runtime.Generation;
    runtime.ResetForExpedition();
    Equal(generation + 1, runtime.Generation);
    Equal(ForgeDispatchStatus.Accepted, runtime.Publish(E("old", 2)).Status);
}

static void InvalidNamespace()
{
    var runtime = new CanonicalTriggerRuntime(() => true);
    Throws<ArgumentException>(() => runtime.Observe("cee.trigger.door.opened", "adapter.cee", _ => { }));
    Throws<ArgumentException>(() => runtime.Publish(new ForgeTriggerEvent("id", "eec.enemy.death", "enemy:1", 1)));
}

static void ReentrantDuplicate()
{
    var runtime = new CanonicalTriggerRuntime(() => true);
    var count = 0;
    runtime.Observe("forge.trigger.door.opened", "adapter.reentrant", context =>
    {
        count++;
        var nested = runtime.Publish(context.Event);
        Equal(ForgeDispatchStatus.Duplicate, nested.Status);
    });
    runtime.Publish(E("reentrant"));
    Equal(1, count);
}
