namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInDependency : Attribute { public BepInDependency(string id, string version) { } }
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
        public Exception? InfoFailure;
        public void LogInfo(object message) { if (InfoFailure != null) throw InfoFailure; if (ThrowInfo) throw new IOException("fixture logger failure"); }
        public void LogWarning(object message) { }
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string name) { } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPrefix : Attribute { }
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

internal sealed class ReadOnlyDataFailure : IOException
{
    private readonly System.Collections.IDictionary _data =
        new System.Collections.ObjectModel.ReadOnlyDictionary<object, object>(new Dictionary<object, object>());
    public override System.Collections.IDictionary Data => _data;
}
internal sealed class MessageGetterFailure : Exception
{
    public override string Message => throw new InvalidOperationException("fixture message getter");
}

internal sealed class DataGetterFailure : Exception
{
    public override System.Collections.IDictionary Data => throw new InvalidOperationException("fixture Data getter");
}
internal sealed class NullMessageFailure : Exception
{
    public override string Message => null!;
}
