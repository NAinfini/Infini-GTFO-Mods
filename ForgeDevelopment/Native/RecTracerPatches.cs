using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace ForgeDevelopment.Native;

/// <summary>
/// The one generic prefix/postfix pair every trace patch uses. Harmony binds them per target: <c>__instance</c>,
/// <c>__args</c>, <c>__originalMethod</c> and <c>__result</c> are resolved against the patched method, so a single
/// pair covers every signature the profiles match. Every decision — whether the call is over its rate, whether it is
/// a change, what is written — belongs to <see cref="RecPatch"/>; this file only carries the call into it.
/// </summary>
internal static class RecTracerPatches
{
    /// <summary>The method the tracer installs. It is resolved once here, so a HarmonyX that cannot bind the pair is
    /// a startup failure the tracer reports rather than a patch that silently does nothing.</summary>
    internal static MethodInfo PrefixMethod { get; } = typeof(RecTracerPatches).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)!;
    internal static MethodInfo PostfixMethod { get; } = typeof(RecTracerPatches).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>Per in-flight call state. The prefix and postfix always run on one thread around one body, and a
    /// nested patched call is a second frame on the same thread's own instance of this array.</summary>
    [ThreadStatic] private static Frame[]? _frames;
    [ThreadStatic] private static int _depth;

    private struct Frame
    {
        internal RecPatch Patch;
        internal object? Instance;
        internal object?[]? Arguments;
        internal bool Admitted;
        internal bool FirstCall;
        internal long Stamp;
    }

    private static void Prefix(object __instance, object?[] __args, MethodBase __originalMethod)
    {
        try
        {
            var frames = _frames ??= new Frame[32];
            if (_depth >= frames.Length) return;
            ref var frame = ref frames[_depth];
            frame.Patch = null!;
            frame.Instance = null;
            frame.Arguments = null;
            frame.Admitted = false;
            frame.FirstCall = false;
            var patch = RecTracerRuntime.Get(__originalMethod);
            if (patch == null) { _depth++; return; }
            frame.Patch = patch;
            frame.Instance = __instance;
            frame.Arguments = __args;
            frame.Stamp = Environment.TickCount64;
            if (!patch.Gate.Admit(frame.Stamp)) { _depth++; return; }
            frame.Admitted = true;
            frame.FirstCall = patch.Entry.FirstStack && patch.Calls == 0;
            _depth++;
        }
        catch (Exception error)
        {
            // The prefix runs inside the game's own call: it may record its own failure, and nothing else.
            _depth = Math.Max(0, _depth - 1);
            Safe(error);
        }
    }

    private static void Postfix(object __instance, object?[] __args, MethodBase __originalMethod, ref object? __result)
    {
        Frame frame = default;
        try
        {
            if (_depth <= 0) return;
            _depth--;
            frame = _frames![_depth];
            if (frame.Patch == null) return;
            var patch = frame.Patch;
            if (!frame.Admitted)
            {
                patch.ReportOverflow(false);
                return;
            }
            if (patch.Entry.RecordsChanges)
            {
                var key = patch.ChangeKey(__instance, __args);
                if (key == null || !patch.Changes.Changed(key)) { patch.ReportOverflow(false); return; }
            }
            // The experiments module waits on "this method was hit"; one branch on a volatile flag costs
            // nothing outside a run and keeps the observation inside the patch that already fired.
            if (ExperimentTrace.Capturing && __originalMethod is MethodInfo traced)
                ExperimentTrace.Observe(patch.Entry.Type, traced.Name);
            patch.Record(__instance, frame.Arguments, __result, patch.Entry.RecordsResult);
            if (frame.FirstCall) WriteStack(patch, __instance);
            patch.ReportOverflow(false);
        }
        catch (Exception error)
        {
            if (frame.Patch != null) frame.Patch.Fail(error);
            Safe(error);
        }
    }

    /// <summary>The managed stack of the first call of a method, which is what a profile asks for when it cannot tell
    /// from the arguments who called it.</summary>
    private static void WriteStack(RecPatch patch, object? instance)
    {
        var stack = new StackTrace(3, false).ToString();
        RecSession.Write("tracer", "first_stack", json =>
        {
            json.WriteString("profile", patch.Entry.Profile);
            json.WriteString("type", patch.Entry.Type);
            json.WriteString("method", patch.Method.Name);
            json.WriteNumber("patch", patch.Id);
            json.WriteString("stack", RecReflect.Truncate(stack, 8192));
        });
    }

    private static void Safe(Exception error)
    {
        try { RecSession.Write("tracer", "patch_error", json => json.WriteString("error", error.GetType().Name + ": " + RecReflect.Truncate(error.Message, 200))); }
        catch (Exception) { /* A recorder that cannot record its own failure must not replace the game's call. */ }
    }
}
