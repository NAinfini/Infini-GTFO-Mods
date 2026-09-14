using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Native;

/// <summary>Native equipment observation lifetime on the host's single Runtime, started by <see cref="Plugin"/>.
/// Owners are never created here: the adapter asks the SDK for the gtfo.player references ForgeMap records,
/// and the identity session re-checks them through the same SDK entry.</summary>
internal sealed class WeaponNativeSession : IDisposable
{
    internal static WeaponNativeSession? Current { get; private set; }
    internal static string? LastCleanupDiagnostic { get; private set; }
    private readonly RuntimeKernel _kernel;
    private readonly Action<string> _report;
    private readonly Action _removeHooks;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed, _faulted;
    internal EquipmentNativeAdapter Adapter { get; private set; } = null!;
    internal EquipmentIdentitySession Identity { get; private set; } = null!;
    internal bool Faulted => _faulted;
    internal string? LastFault { get; private set; }
    internal string? LastReporterFailure { get; private set; }

    private WeaponNativeSession(RuntimeKernel kernel, Action<string> report, Action removeHooks)
    { _kernel = kernel; _report = report; _removeHooks = removeHooks; }

    internal static WeaponNativeSession Start(RuntimeKernel kernel, RuntimeLogLevel logLevel, Func<bool> canExecute,
        Action<string> report, Action<string> info, Action installHooks, Action removeHooks)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(canExecute);
        ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(installHooks); ArgumentNullException.ThrowIfNull(removeHooks);
        if (Current != null)
            throw new InvalidOperationException("Weapon native observation is single-instance per process.");
        if (!kernel.IsRegistrationOpen)
            throw new InvalidOperationException("Weapon must register during dependent plugin Load, before Runtime startup.");
        var session = new WeaponNativeSession(kernel, report, removeHooks);
        bool hooksAttempted = false;
        try
        {
            session.Adapter = new EquipmentNativeAdapter(kernel, () => !session._faulted && canExecute(), report, info);
            // Duplicate provider registration throws here, before any detour is attempted.
            session.Identity = new EquipmentIdentitySession(kernel, logLevel, session.Adapter.IsNativeCurrent, kernel.IsEntityCurrent);
            session.Adapter.Attach(session.Identity);
            Current = session;
            hooksAttempted = true; installHooks();
            return session;
        }
        catch (Exception original)
        {
            session._faulted = true;
            var errors = new List<Exception>();
            if (hooksAttempted) Cleanup(removeHooks, errors);
            if (session.Identity != null) Cleanup(session.Identity.Dispose, errors);
            if (ReferenceEquals(Current, session)) Current = null;
            session._disposed = true;
            if (errors.Count != 0) PreserveCleanupFailure(original, "ForgeWeapon.CleanupFailures", new AggregateException(errors), report);
            throw;
        }
    }

    internal void Guard(Action<EquipmentNativeAdapter> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || _faulted || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { callback(Adapter); }
        catch (Exception error)
        {
            // Contract rejections are handled per observation inside the adapter; anything reaching here is unexpected,
            // including a missing gtfo.player instance lookup, which must stop observation rather than record ownerless items.
            _faulted = true;
            var errors = new List<Exception>();
            Cleanup(Adapter.Clear, errors);
            LastFault = Describe(error);
            try { _report("Weapon equipment observation disabled until restart: " + LastFault + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        // Unregister first; Runtime rejects disposal during dispatch and the session stays intact.
        Identity.Dispose();
        _faulted = true;
        Adapter.Clear();
        var errors = new List<Exception>();
        Cleanup(_removeHooks, errors);
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        if (errors.Count != 0) throw new AggregateException("Weapon shutdown cleanup failed.", errors);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Weapon session requires its owning simulation thread.");
    }

    private static string Describe(Exception error)
    {
        string message;
        try { message = error.Message ?? ""; }
        catch (Exception unavailable) { message = "<message unavailable: " + unavailable.GetType().Name + ">"; }
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
