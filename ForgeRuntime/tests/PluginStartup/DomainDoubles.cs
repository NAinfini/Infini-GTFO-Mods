using ForgeRuntime.Framework;
using BepInEx.Configuration;

namespace ForgeRuntime
{
    internal static partial class Settings
    {
        internal static ConfigEntry<bool> PerformanceLogging = null!;
        internal static void Bind(ConfigFile config)
        {
            Probe.Call("diagnostics:bind");
            PerformanceLogging = config.Bind("Performance Diagnostics", "EnablePerformanceLogging", true, "");
        }
    }
    internal static class RuntimeDiagnostics
    {
        internal static void Initialize() => Probe.Call("diagnostics:init");
        internal static void Stop() => Probe.Call("diagnostics:stop");
    }
    public sealed class AuthoringMonitor : UnityEngine.Object { }
    public sealed class PerformanceMonitor : UnityEngine.Object { }
    [HarmonyLib.HarmonyPatch] internal sealed class DiagnosticHook { }
}
namespace ForgeRuntime.GameBindings
{
    internal static class GameRuntimeBridge
    {
        internal static RuntimeKernel? Kernel { get; set; }
        internal static bool CanExecute => Kernel != null;
        internal static string? Plan, Grants;
        internal static void Initialize(string plan, string grants)
        {
            Plan = plan; Grants = grants;
            Kernel = new(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "bootstrap-double"));
            Kernel.BeginWorld(1); Probe.Call("host:init");
        }
        internal static void Stop()
        { Probe.Call("host:stop"); Kernel?.StopRuntime(); Kernel = null; }
    }
    public sealed class FrameworkMonitor : UnityEngine.Object { }
    [HarmonyLib.HarmonyPatch] internal sealed class FrameworkHook { }
}
