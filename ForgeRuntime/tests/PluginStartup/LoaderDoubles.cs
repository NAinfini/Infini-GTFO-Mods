namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class BepInPlugin : Attribute
    { public BepInPlugin(string guid, string name, string version) { } }
    [AttributeUsage(AttributeTargets.Class)] public sealed class BepInDependency : Attribute
    {
        public enum DependencyFlags { SoftDependency }
        public BepInDependency(string guid, DependencyFlags flags) { }
    }
}
namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public void LogInfo(object message) => Probe.Call(message.ToString()!.Contains(" loaded in ") ? "log:loaded" : "log:info");
        public void LogWarning(object message) => Probe.Call("log:warning");
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
    public enum KeyCode { F10 }
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
        public Processor CreateClassProcessor(Type type) => new(type);
    }
    public sealed class Processor
    {
        private readonly Type type;
        public Processor(Type type) { this.type = type; }
        public void Patch() { Probe.Call("hook:type:" + type.Name); Probe.Call("hook:patch"); }
    }
}
