using ForgeDevelopment.Native;

internal static class ReceiptTests
{
    internal static void Run(Action<bool, string> check)
    {
        var nativeError = new IOException("native cleanup failed");
        var writerError = new IOException("writer cleanup failed");
        var reportError = new InvalidOperationException("log sink failed");
        var completed = 0;
        var receipt = ShutdownSequence.Run(new (string, Action)[]
        {
            ("native", () => throw nativeError),
            ("component", () => completed++),
            ("writer", () => throw writerError)
        }, (_, _) => throw reportError);
        check(completed == 1, "non-failing cleanup runs despite surrounding failures");
        check(receipt.Count == 4, "receipt retains both cleanup and reporting failures");
        check(receipt.Select(item => item.Stage).SequenceEqual(new[]
            { "native", "native/error_report", "writer", "writer/error_report" }), "receipt preserves failure order and source stage");
        // Indexed receipt assertions must survive a sequence that stops early: a short receipt is a
        // reported failure, not an escaping IndexOutOfRangeException that hides the remaining checks.
        check(ReferenceEquals(receipt.ElementAtOrDefault(0).Error, nativeError) && ReferenceEquals(receipt.ElementAtOrDefault(2).Error, writerError), "original cleanup exceptions are retained without replacement");
        check(ReferenceEquals(receipt.ElementAtOrDefault(1).Error, reportError) && ReferenceEquals(receipt.ElementAtOrDefault(3).Error, reportError), "reporter exception evidence is retained without recursive reporting");
        var immutable = false;
        try { ((ICollection<(string Stage, Exception Error)>)receipt).Add(("injected", reportError)); }
        catch (NotSupportedException) { immutable = true; }
        check(immutable && receipt.Count == 4, "the returned failure receipt cannot be mutated");
        var clean = ShutdownSequence.Run(new (string, Action)[]
            { ("clean", () => completed++) }, (_, _) => throw reportError);
        check(clean.Count == 0 && receipt.Count == 4, "separate cleanup runs do not share failure state");
        var empty = ShutdownSequence.Run(Array.Empty<(string, Action)>(), (_, _) => throw reportError);
        check(empty.Count == 0, "an empty cleanup has an empty receipt");
        var invalidSteps = false;
        try { ShutdownSequence.Run(null!, (_, _) => { }); }
        catch (ArgumentNullException) { invalidSteps = true; }
        check(invalidSteps, "null cleanup steps are rejected explicitly");
        var invalidReporter = false;
        try { ShutdownSequence.Run(new (string, Action)[] { ("never", () => completed++) }, null!); }
        catch (ArgumentNullException) { invalidReporter = true; }
        check(invalidReporter && completed == 2, "invalid reporter is rejected before any partial cleanup");
    }
}
