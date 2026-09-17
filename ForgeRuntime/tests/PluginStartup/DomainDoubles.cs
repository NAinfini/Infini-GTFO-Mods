using ForgeRuntime.Framework;

namespace ForgeRuntime.GameBindings
{
    internal static class GameRuntimeBridge
    {
        internal const string GameBuild = "bootstrap-double";
        internal static RuntimeKernel? Kernel { get; set; }
        /// <summary>A suspended host still publishes its kernel but never opens the gameplay gate, which is what makes
        /// "suspended" distinguishable from "the host never loaded" for a dependent package.</summary>
        internal static bool CanExecute => Kernel != null && Suspension == null;
        internal static string? Suspension { get; set; }
        internal static RuntimeLogLevel? LogLevel;
        internal static bool Subscribed;
        internal static void Initialize(RuntimeLogLevel logLevel)
        {
            LogLevel = logLevel;
            Kernel = new(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "bootstrap-double"));
            Probe.Call("host:init");
            // A suspended host starts neither the world nor the network binding: no game event may be claimed for a
            // binary this host does not support.
            if (Suspension == null) { Kernel.BeginWorld(1); NetworkBinding.Start(); }
        }
        internal static void Stop()
        { Probe.Call("host:stop"); Kernel?.StopRuntime(); Kernel = null; Suspension = null; }
        internal static void Disconnect()
        {
            if (!Subscribed) return;
            Subscribed = false; Probe.Call("level:unsubscribe");
        }
    }
    /// <summary>Double for the GTFO-API level lifecycle; the real one subscribes to LevelAPI in the shipped plugin.</summary>
    internal static class LevelLifecycle
    {
        internal static void Subscribe()
        { GameRuntimeBridge.Subscribed = true; Probe.Call("level:subscribe"); }
        internal static void Unsubscribe() => GameRuntimeBridge.Disconnect();
    }
    public sealed class FrameworkMonitor : UnityEngine.Object { }
    [HarmonyLib.HarmonyPatch] internal sealed class FrameworkHook { }
    /// <summary>Double for the network binding; the real one claims the GTFO-API event names once per process and
    /// owns one session host, so releasing it twice is a no-op and releasing what never started is too.</summary>
    internal static class NetworkBinding
    {
        internal static bool Started;
        internal static void Start() { Started = true; Probe.Call("network:start"); }
        internal static void Stop()
        {
            if (!Started) return;
            Started = false;
            Probe.Call("network:stop");
        }
    }
}
