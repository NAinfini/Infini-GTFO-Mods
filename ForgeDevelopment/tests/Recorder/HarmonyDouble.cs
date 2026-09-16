using System.Reflection;

// The HarmonyX surface the recorder core names. The test process has no HarmonyX and no game method to patch, so
// this stands in for it: RecTracerRuntime reaches its own decisions first and only then asks this to install them,
// which is exactly the seam the tracer's tests exercise. Nothing here pretends a patch ran.
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class HarmonyPatch : Attribute { }

    public sealed class Priority
    {
        public const int First = 100;
        public const int Last = 900;
    }

    public enum HarmonyPatchType { All, Prefix, Postfix, Transpiler, Finalizer, ReversePatch }

    public sealed class HarmonyMethod
    {
        public HarmonyMethod(MethodInfo method) => method_ = method;
        public HarmonyMethod(MethodInfo method, int priority)
        {
            method_ = method;
            this.priority = priority;
        }
        public MethodInfo method_ { get; }
        public int priority { get; set; }
    }

    public sealed class Patches
    {
        internal Patches(params MethodInfo[] methods) => Methods = methods;
        internal MethodInfo[] Methods { get; }
    }

    public sealed class Harmony
    {
        private readonly List<MethodBase> _patched = new();

        public Harmony(string id) => Id = id;
        public string Id { get; }
        internal IReadOnlyList<MethodBase> PatchedByThis => _patched;
        internal Action<MethodBase>? BeforePatch { get; set; }

        public HarmonyProcessor CreateProcessor(MethodBase original) => new(this, original);

        public void Unpatch(MethodBase original, HarmonyPatchType type, string harmonyId)
        {
            if (harmonyId != Id) throw new InvalidOperationException("Unpatch by another owner.");
            PatchedMethods.Remove(original);
            _patched.Remove(original);
        }

        internal static readonly List<MethodBase> PatchedMethods = new();

        internal void Patched(MethodBase method)
        {
            BeforePatch?.Invoke(method);
            if (PatchedMethods.Contains(method)) throw new InvalidOperationException("Already patched: " + method);
            PatchedMethods.Add(method);
            _patched.Add(method);
        }
    }

    public sealed class HarmonyProcessor
    {
        private readonly Harmony _harmony;
        private readonly MethodBase _original;
        private HarmonyMethod? _prefix, _postfix;

        internal HarmonyProcessor(Harmony harmony, MethodBase original)
        {
            _harmony = harmony;
            _original = original;
        }

        public HarmonyProcessor AddPrefix(HarmonyMethod method) { _prefix = method; return this; }
        public HarmonyProcessor AddPostfix(HarmonyMethod method) { _postfix = method; return this; }

        public MethodInfo Patch()
        {
            if (_prefix == null && _postfix == null) throw new InvalidOperationException("A patch needs a prefix or a postfix.");
            _harmony.Patched(_original);
            return (MethodInfo)_original;
        }
    }
}
