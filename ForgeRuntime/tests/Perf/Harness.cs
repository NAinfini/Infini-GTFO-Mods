using System.Diagnostics;
using ForgeRuntime.Framework;

namespace ForgeRuntime.Tests.Perf;

/// <summary>
/// The measuring half: one receipt per measured block, and the printing the report quotes. Timing is a
/// <see cref="Stopwatch"/> and allocation is <see cref="GC.GetAllocatedBytesForCurrentThread"/>, both of which
/// need no package and no profiler. Every block reports the mean of its operations and the bytes one operation
/// allocated, because those are the two numbers a per-frame budget is written against.
/// </summary>
internal sealed record Receipt(string Work, long Operations, double Milliseconds, long Bytes, string Note)
{
    internal double MicrosecondsPerOperation => Milliseconds * 1000d / Math.Max(1, Operations);
    internal double BytesPerOperation => (double)Bytes / Math.Max(1, Operations);

    internal string Line()
        => $"{Work}: {Operations} ops, {Milliseconds:F2} ms total, {MicrosecondsPerOperation:F2} us/op, " +
           $"{Bytes} B total, {BytesPerOperation:F1} B/op. {Note}";
}

internal static class Harness
{
    internal static readonly RuntimeIdentity Identity = new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457");
    private static readonly List<Receipt> Receipts = new();

    /// <summary>
    /// Runs one measured block. The first call of a block warms the JIT and the tiered compiler, so the block is
    /// run once unmeasured and then measured; a block that allocates into a growing world (a queue) is expected to
    /// be written so the warm-up is the same work as the measured run.
    /// </summary>
    internal static Receipt Measure(string work, long operations, string note, Action body, int warmups = 1)
    {
        for (var i = 0; i < warmups; i++) body();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        body();
        clock.Stop();
        var receipt = new Receipt(work, operations, clock.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before, note);
        Receipts.Add(receipt);
        Console.WriteLine(receipt.Line());
        return receipt;
    }

    /// <summary>One non-measured line: counts and statuses a receipt cannot carry.</summary>
    internal static void Note(string text) => Console.WriteLine(text);

    /// <summary>What one tick actually did, so a receipt can be read next to the work behind it: the first few
    /// command results and any event refusal. A benchmark whose plan was refused would otherwise report a number
    /// that measures the refusal.</summary>
    internal static string Describe(TickResult tick)
    {
        var commands = string.Join(" | ", tick.Commands.Take(3).Select(c => c.NodeId + "=" + c.Result.Status + ":" + c.Result.Code));
        var events = string.Join(" | ", tick.Events.Take(3).Select(e => e.Status + ":" + e.Code));
        return $"commands={tick.Commands.Count} [{commands}] events={tick.Events.Count} [{events}]";
    }

    internal static void Failed(string text)
    {
        Console.WriteLine("FAIL " + text);
        Environment.ExitCode = 1;
    }

    /// <summary>Every receipt this process printed, so a caller can quote them without re-parsing stdout.</summary>
    internal static IReadOnlyList<Receipt> All => Receipts;
}
