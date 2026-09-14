using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>Domain-owned lifetime; the host remains the only runtime, clock and authority gate.</summary>
internal sealed class MapPluginSession : IDisposable
{
    private readonly RuntimeKernel _kernel;
    private readonly Action<string> _report;
    private readonly Action _removeHooks;
    private bool _disposed, _faulted;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    internal static string? LastCleanupDiagnostic { get; private set; }
    internal PlayerIdentityModule Module { get; private set; } = null!;
    internal bool Faulted => _faulted;
    internal string? LastFault { get; private set; }
    internal string? LastReporterFailure { get; private set; }
    private MapPluginSession(RuntimeKernel kernel, Action<string> report, Action removeHooks)
    { _kernel = kernel; _report = report; _removeHooks = removeHooks; }

    internal static MapPluginSession Start(RuntimeKernel kernel, RuntimeLogLevel logLevel, Action<string> report, Action<string> log,
        Action installHooks, Action removeHooks)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(log); ArgumentNullException.ThrowIfNull(installHooks);
        ArgumentNullException.ThrowIfNull(removeHooks);
        if (!kernel.IsRegistrationOpen)
            throw new InvalidOperationException("Map must register during dependent plugin Load, before Runtime startup.");
        var session = new MapPluginSession(kernel, report, removeHooks);
        bool hooksAttempted = false;
        try
        {
            // Duplicate provider or entity namespace throws atomically before any Harmony patch is attempted.
            session.Module = new PlayerIdentityModule(kernel, logLevel, () => !session._faulted, log, report);
            hooksAttempted = true; installHooks();
            return session;
        }
        catch (Exception original)
        {
            // Any residual registration is inert even if rollback itself fails.
            session._faulted = true;
            var errors = new List<Exception>();
            if (hooksAttempted) Cleanup(removeHooks, errors);
            if (session.Module != null) Cleanup(session.Module.Dispose, errors);
            session._disposed = true;
            if (errors.Count != 0) PreserveCleanupFailure(original, "ForgeMap.CleanupFailures", new AggregateException(errors), report);
            throw;
        }
    }

    internal void Guard(Action<PlayerIdentityModule> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || _faulted || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { callback(Module); }
        catch (Exception error)
        {
            _faulted = true;
            var errors = new List<Exception>(); Cleanup(Module.ClearWorld, errors);
            string detail = Describe(error);
            LastFault = detail;
            try { _report("Map player identity disabled until restart after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        // Unregister first: Runtime rejects dispatch/lifecycle re-entry or in-use providers
        // before native detours or session flags can be changed.
        Module.Dispose();
        _faulted = true;
        var errors = new List<Exception>();
        Cleanup(_removeHooks, errors);
        _disposed = true;
        if (errors.Count != 0) throw new AggregateException("Map shutdown cleanup failed.", errors);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Map session requires its owning simulation thread.");
    }

    private static string Describe(Exception error)
    {
        string message;
        try { message = error.Message ?? ""; }
        catch (Exception unavailable) { message = "<message unavailable: " + unavailable.GetType().Name + ">"; }
        if (message.Length > 1900) message = message[..1900];
        var description = error.GetType().Name + ": " + message;
        return description.Length <= 2048 ? description : description[..2048];
    }

    internal static void PreserveCleanupFailure(Exception original, string key, Exception cleanup, Action<string> report)
    {
        // One bounded diagnostic. Hostile exception accessors or a broken logger must not replace the primary exception.
        string diagnostic = key + ": " + Describe(cleanup);
        try { original.Data[key] = cleanup; }
        catch (Exception attachment) { diagnostic += "; attachment=" + attachment.GetType().Name; }
        try { report(diagnostic); }
        catch (Exception reporter) { diagnostic += "; reporter=" + reporter.GetType().Name; }
        LastCleanupDiagnostic = diagnostic.Length <= 4096 ? diagnostic : diagnostic[..4096];
    }

    private static void Cleanup(Action action, List<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }
}
