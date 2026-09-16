namespace BepInEx
{
    /// <summary>The one install-root path the trigger-zone loader names when its caller supplies none.</summary>
    public static class Paths
    {
        public static string BepInExRootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-map-noplugins");
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInDependency : Attribute { public BepInDependency(string id, string version) { } }
}
namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T>
    {
        public T Value { get; set; }
        internal ConfigEntry(T value) { Value = value; }
    }
    // Only the members the production plugin reads: one raw-text entry, with Preset driving a malformed value.
    public sealed class ConfigFile
    {
        public readonly Dictionary<string, string> Preset = new();
        private readonly Dictionary<string, object> entries = new();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, string description)
        {
            string id = section + "." + key;
            if (entries.TryGetValue(id, out var prior)) return (ConfigEntry<T>)prior;
            var entry = new ConfigEntry<T>(Preset.TryGetValue(id, out var selected) ? (T)(object)selected : value);
            entries.Add(id, entry); return entry;
        }
    }
}
namespace BepInEx.Unity.IL2CPP
{
    public abstract class BasePlugin
    {
        public BepInEx.Configuration.ConfigFile Config { get; } = new();
        public TestLog Log { get; } = new();
        public abstract void Load();
        public virtual bool Unload() => false;
    }
    public sealed class TestLog
    {
        public bool ThrowInfo;
        /// <summary>Throws on the one info line that carries this text, so a case can fail the load at a chosen
        /// point of it instead of at whichever line happens to log first. Null means no line is singled out.</summary>
        public string? ThrowOn;
        public readonly List<string> Infos = new(), Warnings = new(), Errors = new();
        public void LogInfo(object message)
        {
            if (ThrowInfo || (ThrowOn is { } text && ((string)message).Contains(text, StringComparison.Ordinal)))
                throw new IOException("fixture logger failure");
            Infos.Add((string)message);
        }
        public void LogWarning(object message) => Warnings.Add((string)message);
        public void LogError(object message) => Errors.Add((string)message);
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string name) { }
        /// <summary>The three-argument form several hooks use: the patched member plus the parameter types the
        /// overload is bound by.</summary>
        public HarmonyPatch(Type type, string name, Type[] argumentTypes) { }
    }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPostfix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int First = 800, Last = 0; }
    public sealed class Harmony
    {
        public static int Patches, Unpatches;
        public static int FailPatchAt;
        public static Exception? UnpatchFailure;
        public Harmony(string id) { }
        public Processor CreateClassProcessor(Type type) => new();
        public void UnpatchSelf() { Unpatches++; if (UnpatchFailure != null) throw UnpatchFailure; }
        public sealed class Processor
        {
            public void Patch()
            {
                Patches++;
                if (FailPatchAt == Patches) throw new IOException("fixture patch failure");
            }
        }
        public static void Reset() { Patches = Unpatches = FailPatchAt = 0; UnpatchFailure = null; }
    }
}
