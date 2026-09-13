using ForgeDevelopment.Native;

var checks = 0;
var failures = 0;
void Check(bool condition, string name)
{
    checks++;
    if (!condition) { failures++; Console.Error.WriteLine("FAIL: " + name); }
}
var completed = new List<string>();
var reported = new List<string>();
ShutdownSequence.Run(new (string, Action)[]
{
    ("unsubscribe", () => completed.Add("unsubscribe")),
    ("writer", () => completed.Add("writer"))
}, (stage, _) => reported.Add(stage));
Check(completed.SequenceEqual(new[] { "unsubscribe", "writer" }), "successful cleanup preserves order");
Check(reported.Count == 0, "successful cleanup does not call the error reporter");

completed.Clear();
ShutdownSequence.Run(new (string, Action)[]
{
    ("unsubscribe", () => throw new InvalidOperationException("native unsubscribe failed")),
    ("writer", () => completed.Add("writer"))
}, (stage, _) => reported.Add(stage));
Check(completed.SequenceEqual(new[] { "writer" }), "cleanup continues after an action failure");
Check(reported.SequenceEqual(new[] { "unsubscribe" }), "action failure is reported once at its stage");

// Each position may fail, including the first and last cleanup stages.
for (var failedIndex = 0; failedIndex < 5; failedIndex++)
{
    var attempted = new List<int>();
    var errors = 0;
    var escaped = false;
    var steps = Enumerable.Range(0, 5).Select(index => ("stage-" + index, (Action)(() =>
    {
        attempted.Add(index);
        if (index == failedIndex) throw new IOException("injected cleanup failure");
    }))).ToArray();
    try
    {
        ShutdownSequence.Run(steps, (_, _) =>
        {
            errors++;
            throw new InvalidOperationException("injected report failure");
        });
    }
    catch { escaped = true; }
    Check(!escaped, "reporter failure stays isolated at cleanup stage " + failedIndex);
    Check(attempted.SequenceEqual(Enumerable.Range(0, 5)), "every cleanup stage runs once after reporter failure at " + failedIndex);
    Check(errors == 1, "failed error reporting is not recursively retried at " + failedIndex);
}

var allAttempts = 0;
var reportAttempts = 0;
var allEscaped = false;
try
{
    ShutdownSequence.Run(Enumerable.Range(0, 4).Select(index => ("failure-" + index, (Action)(() =>
    {
        allAttempts++;
        throw new IOException("action " + index);
    }))).ToArray(), (_, _) =>
    {
        reportAttempts++;
        throw new InvalidOperationException("reporter unavailable");
    });
}
catch { allEscaped = true; }
Check(!allEscaped, "all failing actions/reporters still finish the sequence");
Check(allAttempts == 4 && reportAttempts == 4, "each failed action gets one bounded reporting attempt");
ReceiptTests.Run(Check);
Console.WriteLine($"Forge Development shutdown: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
