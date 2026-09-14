namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInDependency : Attribute { public BepInDependency(string id, string version) { } }
    // Only the members the production plugin reads are doubled; a missing directory is "no package".
    public static class Paths { public static string PluginPath = ""; }
}
namespace BepInEx.Unity.IL2CPP
{
    public abstract class BasePlugin
    {
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
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string name) { } }
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
