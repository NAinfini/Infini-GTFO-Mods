// Host, loader and Harmony doubles for the production Weapon plugin. Patch counts are synthetic; nothing is detoured.
namespace ForgeRuntime
{
    public enum RuntimeMode { Off, Play, Authoring }
    public static class Plugin
    {
        public static RuntimeMode ConfiguredMode = RuntimeMode.Play;
        public static ForgeRuntime.Framework.RuntimeKernel? Runtime;
        public static bool CanExecuteGameplay = true;
        // A suspended host still publishes its kernel; the reason code is what a package reports.
        public static string? Suspension;
        public static bool IsSuspended => Suspension != null;
        public static string? SuspensionCode => Suspension;
    }
}
namespace BepInEx
{
    /// <summary>The loader's own root path, which the plugin hands to the session as where package data lives.
    /// The fixture points it at a directory the test owns. `GameRootPath` is where the loadout policy's
    /// `gameAssemblySha256` pin reads the game assembly from.</summary>
    public static class Paths
    {
        public static string BepInExRootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-weapon-parts-noplugins");
        public static string GameRootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-weapon-game-noplugins");
    }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
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
        public readonly List<string> Infos = new(), Warnings = new(), Errors = new();
        public void LogInfo(object message) { if (ThrowInfo) throw new IOException("fixture logger failure"); Infos.Add((string)message); }
        public void LogWarning(object message) => Warnings.Add((string)message);
        public void LogError(object message) => Errors.Add((string)message);
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string name) { }
        // The reload flag's hook patches a property's setter, which this build's Harmony spells with the third
        // argument; the kind is not read by the double, only accepted.
        public HarmonyPatch(Type type, string name, MethodType methodType) { }
        // The overload a patch uses to name one body out of several of the same name: the game declares two
        // `SetClipAmmoInSlot` bodies, and a patch declared by name alone would not say which one it brackets.
        public HarmonyPatch(Type type, string name, Type[] argumentTypes) { }
    }
    public enum MethodType { Normal, Getter, Setter, Constructor, StaticConstructor }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPostfix : Attribute { }
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
