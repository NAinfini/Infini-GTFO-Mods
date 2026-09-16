using ForgeRuntime;
using ForgeRuntime.Framework;
using ForgeRuntime.GameBindings;

internal sealed class UnwritableDataException : Exception
{ public override System.Collections.IDictionary Data => throw new InvalidOperationException("Data unavailable"); }

internal static class Probe
{
    internal static readonly List<string> Calls = new();
    internal static readonly Dictionary<string, Exception> Faults = new();
    internal static readonly List<UnityEngine.Object> Components = new();
    internal static int Checks, Failures;
    internal static void Call(string stage)
    { Calls.Add(stage); if (Faults.TryGetValue(stage, out var error)) throw error; }
    internal static void That(bool value, string message)
    { if (!value) throw new Exception(message); Checks++; }
    internal static void Case(string name, Action test)
    {
        Calls.Clear(); Faults.Clear(); Components.Clear();
        GameRuntimeBridge.Kernel = null; GameRuntimeBridge.LogLevel = null; GameRuntimeBridge.Suspension = null;
        GameRuntimeBridge.Subscribed = false;
        NetworkBinding.Started = false;
        // Plugin.Load is single-attempt per process, so each scenario starts from a plugin that has never loaded.
        ResetPlugin();
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failures++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
        finally { GameRuntimeBridge.Kernel?.StopRuntime(); GameRuntimeBridge.Kernel = null; }
    }
    /// <summary>The bootstrap latch lives in production statics; the double's state alone cannot make Load runnable again.</summary>
    private static void ResetPlugin()
    {
        var type = typeof(Plugin);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic;
        type.GetField("_loadAttempted", flags)!.SetValue(null, false);
        type.GetField("_loadComplete", flags)!.SetValue(null, false);
    }
    internal static Plugin Plugin(RuntimeMode mode)
    {
        var p = new Plugin(); p.Config.Preset["Runtime.Mode"] = mode.ToString(); return p;
    }
    internal static Exception? LoadError(Plugin p)
    { try { p.Load(); return null; } catch (Exception error) { return error; } }
}
