if (args.Length != 2 || args[0] != "--fixtures")
    throw new ArgumentException("Usage: LifecycleWork --fixtures <website runtime fixture directory>");
WorkFixture.FixtureRoot = Path.GetFullPath(args[1]);
Probe.Run("stop clears loaded work", CleanupTests.Stop);
Probe.Run("failed startup clears loaded work", CleanupTests.Failure);
Probe.Run("world change does not revive old handles", CleanupTests.World);
Probe.Run("cleanup retains thread ownership", CleanupTests.Threading);
Probe.Run("observers cannot change queued work", ObserverTests.ReadOnly);
Probe.Run("reentrant stop preserves unknown commit", ObserverTests.ReentrantStop);
Console.WriteLine($"Lifecycle work: {Probe.Checks} assertions passed; {Probe.Failures} groups failed.");
Console.WriteLine("Compiled production SDK + website fixtures; managed action doubles, no native game execution.");
Environment.ExitCode = Probe.Failures == 0 ? 0 : 1;

internal static class Probe
{
    internal static int Checks, Failures;
    internal static void That(bool condition, string message)
    { if (!condition) throw new Exception(message); Checks++; }
    internal static void Denied(Func<string> call, string expected)
    {
        string actual;
        try { actual = call(); } catch (ForgeRuntime.Framework.RuntimeContractException ex) { actual = ex.Code; }
        That(actual == expected, $"Expected {expected}, received {actual}");
    }
    internal static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }
}
