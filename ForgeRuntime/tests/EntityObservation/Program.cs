using ForgeRuntime.Framework;

ContractTests.Run();
InstanceResolutionTests.Run();
if (args.Length == 2 && args[0] == "--wire") WireCases.Run(args[1]);
if (args.Contains("--probe-registration")) RegistrationProbe.Run();
Console.WriteLine($"Entity contracts: {Check.Passed} passed; {Check.Failed} failed. No native APIs exercised.");
return Check.Failed == 0 ? 0 : 1;

static class Check
{
    public static int Passed, Failed;
    public static void That(bool value, string name)
    {
        if (value) Passed++;
        else { Failed++; Console.WriteLine("FAIL: " + name); }
    }
    public static void Reject(Action action, string name, string? code = null)
    {
        try { action(); That(false, name + " accepted"); }
        catch (RuntimeContractException error) { That(code == null || error.Code == code, name + ": " + error.Code); }
        catch (ArgumentException) { That(code == null, name + ": argument error"); }
    }
}
