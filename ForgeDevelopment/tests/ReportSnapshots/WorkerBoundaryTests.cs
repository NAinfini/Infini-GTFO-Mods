using System.Collections.Concurrent;
using System.Text.Json;
using ForgeRuntime;

internal static class WorkerBoundaryTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var caller = Environment.CurrentManagedThreadId;
        var errors = new ConcurrentQueue<(Exception Error, int Thread)>();
        var report = new DiagnosticsReport("oversized-receipt");
        var expedition = new string('\u5730', 4096);
        var declarations = Enumerable.Range(0, 1600).Select(i => new ProjectObjectDeclaration(
            expedition, "zone-" + i, new ProjectZoneLocator((uint)(1000 + i), 0, 0, 0))).ToArray();
        report.AttachObjectReferences(new ProjectObjectReferenceScan(declarations, 7, 0, ProjectSourceVerification.Matched));
        var target = Path.Combine(root, "async-over-budget.json");
        File.WriteAllText(target, "original bytes");
        var healthy = Path.Combine(root, "after-budget-failure.json");
        using (var writer = new AsyncReportWriter(error => errors.Enqueue((error, Environment.CurrentManagedThreadId))))
        {
            check(writer.Enqueue(report, target, "unavailable"), "capture does not serialize or trim on the producer thread");
            check(writer.Enqueue(new DiagnosticsReport("next-valid"), healthy, "observing"), "valid work accepted after oversized receipt");
        }
        check(errors.Count == 1 && errors.TryPeek(out var failure) && failure.Error is InvalidDataException,
            "oversized receipt reports its actual budget failure once");
        check(errors.All(e => e.Thread != caller), "serialization failure is delivered by the writer thread");
        check(File.ReadAllText(target) == "original bytes", "failed async budget check preserves previous output");
        using var json = JsonDocument.Parse(File.ReadAllBytes(healthy));
        check(json.RootElement.GetProperty("runId").GetString() == "next-valid", "writer continues with the next valid frozen request");
        check(!Directory.EnumerateFiles(root, ".*.tmp").Any(), "failed reduction leaves no temporary output");

        var callbacks = 0;
        var blocked = Path.Combine(root, "throwing-error-callback.json");
        Directory.CreateDirectory(blocked);
        var next = Path.Combine(root, "after-callback-failure.json");
        using (var writer = new AsyncReportWriter(_ =>
        {
            Interlocked.Increment(ref callbacks);
            throw new InvalidOperationException("Injected error reporter failure");
        }))
        {
            check(writer.Enqueue(new DiagnosticsReport("bad-target"), blocked, "failure"), "callback failure fixture queued");
            check(writer.Enqueue(new DiagnosticsReport("survives-callback"), next, "observing"), "work following callback failure queued");
        }
        check(callbacks == 1 && File.Exists(next), "throwing error callback does not kill the writer");
    }
}
