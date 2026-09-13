using ForgeRuntime;

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
        internal static void Bind(BepInEx.Configuration.ConfigFile config) => Probe.Call("settings:bind");
        internal static void BindAuthoring(BepInEx.Configuration.ConfigFile config) => Probe.Call("settings:authoring");
    }
    internal static class RuntimeDiagnostics
    {
        internal static void Initialize() => Probe.Call("diagnostics:init");
        internal static void Stop() => Probe.Call("diagnostics:stop");
    }
    public sealed class AuthoringMonitor : UnityEngine.Object { }
    public sealed class PerformanceMonitor : UnityEngine.Object { }
    [HarmonyLib.HarmonyPatch] internal sealed class GenerationHook { }
}
namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class BepInPlugin : Attribute
    { public BepInPlugin(string guid, string name, string version) { } }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] public sealed class BepInDependency : Attribute
    {
        public enum DependencyFlags { HardDependency = 1, SoftDependency = 2 }
        public BepInDependency(string guid, string version) { }
        public BepInDependency(string guid, DependencyFlags flags) { }
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
