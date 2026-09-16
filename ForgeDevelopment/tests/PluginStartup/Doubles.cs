using System.Reflection;
using System.Reflection.Emit;
using ForgeRuntime;

internal sealed class UnwritableDataException : Exception
{ public override System.Collections.IDictionary Data => throw new InvalidOperationException("Data unavailable"); }

internal static class Probe
{
    internal static readonly List<string> Calls = new();
    internal static readonly Dictionary<string, Exception> Faults = new();
    internal static readonly List<UnityEngine.Object> Components = new();
    internal static int Checks, Failures;
    private static bool _legacyTweaksInstalled;

    // The plugin's only soft dependency is detected by assembly name and by the absence of
    // InfiniTweaks.Telemetry. This defines an assembly named InfiniTweaks without that type, i.e. the
    // pre-2.5.0 package the plugin must refuse. Assembly identity is process-wide, so it is installed
    // once, after the cases that must run without any InfiniTweaks assembly.
    internal static void InstallLegacyTweaks()
    {
        if (_legacyTweaksInstalled) return;
        _legacyTweaksInstalled = true;
        var name = new AssemblyName("InfiniTweaks") { Version = new Version(2, 0, 0) };
        AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run).DefineDynamicModule("InfiniTweaks").DefineType("InfiniTweaks.LegacyCollector").CreateType();
    }
    internal static void Call(string stage)
    { Calls.Add(stage); if (Faults.TryGetValue(stage, out var error)) throw error; }
    internal static void That(bool value, string message)
    { if (!value) throw new Exception(message); Checks++; }
    internal static void Case(string name, Action test)
    {
        Calls.Clear(); Faults.Clear(); Components.Clear();
        ForgeDevelopment.Native.Settings.PerformanceLogging.Value = true;
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failures++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    internal static ForgeDevelopment.Native.Plugin Plugin(RuntimeMode mode, bool runtimeAvailable = true)
    {
        ForgeRuntime.Plugin.ConfiguredMode = mode;
        ForgeRuntime.Plugin.Runtime = runtimeAvailable ? new object() : null;
        return new ForgeDevelopment.Native.Plugin();
    }
    internal static Exception? LoadError(ForgeDevelopment.Native.Plugin p)
    { try { p.Load(); return null; } catch (Exception error) { return error; } }
}

namespace ForgeRuntime
{
    public enum RuntimeMode { Off = 0, Authoring = 1, Play = 2 }
    // Only the public host surface Development reads: frozen mode, published kernel and version.
    public static class Plugin
    {
        public const string PluginVersion = "1.2.0";
        public static RuntimeMode ConfiguredMode { get; set; }
        public static object? Runtime { get; set; }
    }
}
namespace ForgeDevelopment.Native
{
    internal sealed class TestSetting<T>
    {
        internal TestSetting(T value) => Value = value;
        internal T Value { get; set; }
    }
    internal static class Settings
    {
        internal static readonly TestSetting<bool> PerformanceLogging = new(true);
        internal static readonly TestSetting<bool> RecorderEnabled = new(true);
        internal static void Bind(BepInEx.Configuration.ConfigFile config) => Probe.Call("settings:bind");
        internal static void BindAuthoring(BepInEx.Configuration.ConfigFile config) => Probe.Call("settings:authoring");
        internal static void BindRecorder(BepInEx.Configuration.ConfigFile config) => Probe.Call("settings:recorder");
    }
    internal static class RuntimeDiagnostics
    {
        internal static void Initialize() => Probe.Call("diagnostics:init");
        internal static void Stop() => Probe.Call("diagnostics:stop");
    }
    // The recorder's own stages are covered by tests/Recorder; here it is one startup stage whose calls the
    // plugin's order and rollback matrix can observe.
    internal static class RecRuntime
    {
        internal static void Start(string root) => Probe.Call("recorder:start");
        internal static void Stop() => Probe.Call("recorder:stop");
    }
    public sealed class AuthoringMonitor : UnityEngine.MonoBehaviour { }
    public sealed class PerformanceMonitor : UnityEngine.Object { }
    // The capture registry and the experiment components are the pieces the plugin's authoring branch attaches. Each is
    // one call here, so the startup order and the rollback matrix see them exactly as the game build would.
    internal static class CaptureRegistry
    {
        internal const int DefaultPeriodicSeconds = 30;
        internal static void EnsureStarted(UnityEngine.MonoBehaviour host, int periodicSeconds) => Probe.Call("capture:start");
        internal static void Stop() => Probe.Call("capture:stop");
    }
    public sealed class ExperimentRunner : UnityEngine.Object { }
    public sealed class ExperimentPanel : UnityEngine.Object
    {
        internal static void Load() => Probe.Call("experiment:load");
    }
    [HarmonyLib.HarmonyPatch] internal sealed class GenerationHook { }
}
namespace BepInEx
{
    // The recorder writes under the BepInEx root; the double answers a temporary path so the loader-path cases can
    // run the same startup sequence the game does.
    public static class Paths
    {
        public static string BepInExRootPath => System.IO.Path.GetTempPath();
    }

    [AttributeUsage(AttributeTargets.Class)] public sealed class BepInPlugin : Attribute
    {
        public string GUID { get; }
        public string Name { get; }
        public string Version { get; }
        public BepInPlugin(string guid, string name, string version) { GUID = guid; Name = name; Version = version; }
    }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] public sealed class BepInDependency : Attribute
    {
        public enum DependencyFlags { HardDependency = 1, SoftDependency = 2 }
        public string GUID { get; }
        public string? Version { get; }
        public DependencyFlags Flags { get; }
        public BepInDependency(string guid, string version) { GUID = guid; Version = version; }
        public BepInDependency(string guid, DependencyFlags flags) { GUID = guid; Flags = flags; }
    }
}
namespace BepInEx.Configuration { public sealed class ConfigFile { } }
namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public void LogInfo(object message)
        {
            var text = message.ToString()!;
            Probe.Call(text.Contains(" loaded for ") ? "log:loaded" : text.Contains(" inactive") ? "log:inactive" : "log:info");
        }
        public void LogError(object message) => Probe.Call("log:error");
    }
}
namespace BepInEx.Unity.IL2CPP
{
    public abstract class BasePlugin
    {
        public BepInEx.Configuration.ConfigFile Config { get; } = new();
        public BepInEx.Logging.ManualLogSource Log { get; } = new();
        public abstract void Load();
        public virtual bool Unload() => true;
        protected T AddComponent<T>() where T : UnityEngine.Object, new()
        { Probe.Call("component:add:" + typeof(T).Name); var value = new T(); Probe.Components.Add(value); return value; }
    }
}
namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed { get; private set; }
        public static void Destroy(Object value)
        { Probe.Call("component:destroy:" + value.GetType().Name); value.Destroyed = true; }
    }
    public class Component : Object { }
    public class Behaviour : Component { }
    // The capture registry is handed a MonoBehaviour, which is what the authoring monitor is in the game build.
    public class MonoBehaviour : Behaviour { }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute { }
    public sealed class Harmony
    {
        public Harmony(string guid) { Probe.Call("harmony:new"); }
        public void UnpatchSelf() => Probe.Call("hook:unpatch");
        public void PatchAll(System.Reflection.Assembly assembly)
        {
            foreach (var type in assembly.GetTypes().Where(t => t.IsDefined(typeof(HarmonyPatch), false)))
            { Probe.Call("hook:type:" + type.Name); Probe.Call("hook:patch"); }
        }
    }
}
