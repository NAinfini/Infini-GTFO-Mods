using System.Collections.Concurrent;
using System.Text.Json;
using ForgeRuntime;

internal static class QueueBoundaryTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var errors = new ConcurrentQueue<Exception>();
        var blocked = Path.Combine(root, "capacity-blocker.json");
        Directory.CreateDirectory(blocked);
        var report = new DiagnosticsReport("capacity");
        report.SetMetadata("phase", "first");
        var writer = new AsyncReportWriter(error =>
        {
            errors.Enqueue(error); entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) errors.Enqueue(new TimeoutException());
        });
        try
        {
            check(writer.Enqueue(report, blocked, "blocker"), "capacity blocker accepted");
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Capacity blocker did not start.");
            for (var i = 0; i < 8; i++)
                check(writer.Enqueue(report, Path.Combine(root, $"capacity-{i}.json"), "first"), "pending slot " + i + " accepted");
            check(!writer.Enqueue(report, Path.Combine(root, "overflow.json"), "overflow"), "ninth pending path is rejected");
            report.SetMetadata("phase", "coalesced");
            check(writer.Enqueue(report, Path.Combine(root, "capacity-0.json"), "coalesced"), "existing path may coalesce at capacity");
            report.SetMetadata("phase", "too-late");
        }
        finally { release.Set(); writer.Dispose(); }
        for (var i = 0; i < 8; i++)
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, $"capacity-{i}.json")));
            var expected = i == 0 ? "coalesced" : "first";
            check(json.RootElement.GetProperty("metadata").GetProperty("phase").GetString() == expected,
                "queued/coalesced snapshot owns captured content " + i);
            check(json.RootElement.GetProperty("outcome").GetString() == expected, "outcome matches captured content " + i);
        }
        check(!File.Exists(Path.Combine(root, "overflow.json")), "rejected request creates no file");
        check(errors.Count == 1, "writer recovered from the blocker failure");
        check(!writer.Enqueue(report, Path.Combine(root, "after-stop.json"), "rejected"), "disposed writer rejects snapshot work");
        writer.Dispose();

        var overflowReport = new DiagnosticsReport("overflow-capture");
        for (var i = 0; i < 4096; i++) overflowReport.Event("test", "event", "event-" + i);
        var frozen = overflowReport.Freeze("captured");
        overflowReport.Event("test", "event", "late-overflow");
        using var saved = JsonDocument.Parse(File.ReadAllBytes(frozen.Export(Path.Combine(root, "frozen-overflow.json"))));
        check(saved.RootElement.GetProperty("overflow").GetProperty("droppedEvents").GetInt64() == 0, "overflow counters freeze with evidence");
        check(saved.RootElement.GetProperty("events").GetArrayLength() == 4096, "capturing retains the whole permitted event set");
    }
}
