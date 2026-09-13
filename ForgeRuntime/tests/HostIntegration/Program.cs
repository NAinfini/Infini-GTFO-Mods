using ForgeRuntime.Framework;

Verify.Run("startup and registration", LifecycleProbe.Startup);
Verify.Run("failed startup is terminal", LifecycleProbe.FailedStartup);
Verify.Run("observer ownership", LifecycleProbe.Ownership);
Verify.Run("observer isolation", LifecycleProbe.Isolation);
Verify.Run("observer capacity", LifecycleProbe.Capacity);
Verify.Run("thread boundary", LifecycleProbe.Threading);
if (args.Length == 2 && args[0] == "--host")
    Verify.Run("production assembly boundary", () => HostAssemblyProbe.Run(args[1]));
else if (args.Length != 0)
    throw new ArgumentException("Usage: HostIntegration [--host <ForgeRuntime.dll>]");
Console.WriteLine($"Host integration: {Verify.Checks} assertions passed; {Verify.Failures} groups failed.");
Console.WriteLine("Uses the compiled public SDK and metadata, not native GTFO execution or multiplayer.");
Environment.ExitCode = Verify.Failures == 0 ? 0 : 1;

internal static class Verify
{
    internal static int Checks, Failures;
    internal static void That(bool condition, string message)
    { if (!condition) throw new Exception(message); Checks++; }
    internal static void Reject(Action action, string message)
    { try { action(); } catch (RuntimeContractException) { Checks++; return; } throw new Exception(message); }
    internal static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }
}
