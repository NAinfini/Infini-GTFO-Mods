using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;
using BepInExLogger = BepInEx.Logging.Logger;

namespace ForgeDevelopment.Native;

/// <summary>
/// Captures what the game, Unity and BepInEx say into the `log` channel. Three sources are joined here because they
/// see different things: Unity's threaded callback is the only one that sees engine errors and exceptions, BepInEx's
/// listener sees every plugin's line at its own level, and the process handler sees a fault that reaches the top.
/// A record written by the recorder's own listener is skipped, so a session cannot log itself into a loop.
/// </summary>
internal static class RecLog
{
    /// <summary>Unity's own level, kept as text because Unity's enum is not stable across versions.</summary>
    private static Application.LogCallback? _unityCallback;
    private static uint _unityCallbackRoot;
    private static Listener? _listener;
    private static bool _listening;

    [ThreadStatic] private static bool _inside;

    internal static void Start()
    {
        if (_listening) return;
        _listening = true;
        _unityCallback = (Application.LogCallback)(Action<string, string, LogType>)UnityLog;
        _unityCallbackRoot = IL2CPP.il2cpp_gchandle_new(_unityCallback.Pointer, false);
        Application.add_logMessageReceivedThreaded(_unityCallback);
        _listener = new Listener();
        BepInExLogger.Listeners.Add(_listener);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
    }

    internal static void Stop()
    {
        if (!_listening) return;
        _listening = false;
        try { if (_unityCallback != null) Application.remove_logMessageReceivedThreaded(_unityCallback); }
        catch (Exception error) { Plugin.PluginLog.LogWarning("Forge recorder could not detach the Unity log callback: " + error.Message); }
        if (_unityCallback != null && _unityCallbackRoot != 0)
        {
            try { IL2CPP.il2cpp_gchandle_free(_unityCallbackRoot); }
            catch (Exception) { /* A handle already released by shutdown must not fail the cleanup. */ }
            _unityCallbackRoot = 0;
        }
        if (_listener != null) BepInExLogger.Listeners.Remove(_listener);
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
    }

    /// <summary>Unity reports engine errors and exceptions on whatever thread produced them, which is why the
    /// threaded callback is used and why nothing here touches a Unity object.</summary>
    private static void UnityLog(string message, string stack, LogType level)
    {
        if (!_listening || _inside) return;
        Write("unity", level.ToString(), message, stack, null);
    }

    private static void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
    {
        if (!_listening || _inside) return;
        var error = args.ExceptionObject as Exception;
        Write("process", args.IsTerminating ? "terminating" : "unhandled", error?.Message ?? args.ExceptionObject?.ToString() ?? "unknown",
            error?.ToString() ?? "", error?.GetType().FullName);
    }

    private static void Write(string source, string level, string message, string stack, string? type)
    {
        _inside = true;
        try
        {
            RecSession.Write("log", "line", json =>
            {
                json.WriteString("source", source);
                json.WriteString("level", level);
                if (type != null) json.WriteString("exception", type);
                json.WriteString("message", RecReflect.Truncate(message ?? "", 4096));
                if (!string.IsNullOrEmpty(stack)) json.WriteString("stack", RecReflect.Truncate(stack!, 16384));
                json.WriteNumber("thread", Environment.CurrentManagedThreadId);
            });
        }
        finally { _inside = false; }
    }

    /// <summary>The BepInEx listener. It records everything at Info and above: the user asked for every point that
    /// could ever matter, and a session with a smaller log is a session that cannot answer a later question.</summary>
    private sealed class Listener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.Info | LogLevel.Warning | LogLevel.Error | LogLevel.Fatal | LogLevel.Message;

        public void LogEvent(object sender, LogEventArgs args)
        {
            if (!_listening || _inside) return;
            if ((args.Level & LogLevelFilter) == 0) return;
            var text = args.Data?.ToString() ?? "";
            if (text.Length == 0) return;
            var source = args.Source?.SourceName ?? "unknown";
            Write("bepinex", args.Level.ToString(), source + ": " + text, "", null);
        }

        public void Dispose() { }
    }
}
