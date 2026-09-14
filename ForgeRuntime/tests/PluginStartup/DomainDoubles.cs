using ForgeRuntime.Framework;

namespace ForgeRuntime.GameBindings
{
    internal static class GameRuntimeBridge
    {
        internal static RuntimeKernel? Kernel { get; set; }
        internal static bool CanExecute => Kernel != null;
        internal static RuntimeLogLevel? LogLevel;
        internal static void Initialize(RuntimeLogLevel logLevel)
        {
            LogLevel = logLevel;
            Kernel = new(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "bootstrap-double"));
            Kernel.BeginWorld(1); Probe.Call("host:init");
        }
        internal static void Stop()
        { Probe.Call("host:stop"); Kernel?.StopRuntime(); Kernel = null; }
    }
    public sealed class FrameworkMonitor : UnityEngine.Object { }
    [HarmonyLib.HarmonyPatch] internal sealed class FrameworkHook { }
}
