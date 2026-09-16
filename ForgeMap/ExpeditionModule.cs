using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The Map provider's session half: the one trigger this provider publishes about the expedition itself
/// rather than about a map object. A map object is addressed and observed per instance; an expedition ends once
/// per world, and the fact carries nothing but which of the game's three ends it was.
///
/// Publication is deliberately narrow. Only the host publishes, only a world the kernel already started accepts
/// the event, and the event id is the trigger's own id plus the world epoch and the state, so a repeated native
/// report of one end is the kernel's own `duplicate` answer instead of a second dispatch. An end state this
/// provider has no outcome for is reported once and publishes nothing rather than being folded into a state it
/// is not.
///
/// This half publishes through the registration the Map provider already owns: one provider, one registration,
/// so the session half is composed on it instead of opening a second one.</summary>
public sealed class ExpeditionModule : IDisposable
{
    private readonly RuntimeModuleHandle _registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _authority;
    private readonly Action<string> _report;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private long _publishedFacts;
    private bool _disposed;

    public ExpeditionModule(RuntimeModuleHandle registration, RuntimeKernel kernel, Func<bool> authority, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Events this half actually handed to the kernel (status `queued`). A client, an unknown end state
    /// and a repeated report never contribute, which is what makes the count assertable.</summary>
    public long PublishedFacts => _publishedFacts;

    /// <summary>One end of the expedition the native side reported, as the game's own `ExpeditionEndState`
    /// value. The answer is the kernel's own: `queued` when a plan's mounts claimed the event, `duplicate` when
    /// this same end was already published in this world, `ignored` when no plan is mounted on this world, and
    /// null when this half did not publish at all (a client, an unregistered provider, or a state that names no
    /// outcome).</summary>
    public DispatchResult? Ended(int endState)
    {
        CheckThread();
        if (_disposed || !_registration.IsRegistered) return null;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return null;
        if (!_authority())
        {
            ReportOnce("client", "expedition end observed on a non-authoritative peer: the host publishes the "
                + "expedition's own facts, a client does not.");
            return null;
        }
        if (ExpeditionContract.Outcome(endState) is not { } outcome)
        {
            ReportOnce("end-state:" + endState.ToString(CultureInfo.InvariantCulture),
                "expedition end state " + endState.ToString(CultureInfo.InvariantCulture) + " names no execution "
                + "outcome this provider publishes; the event is not sent.");
            return null;
        }
        // Nobody subscribes to the expedition's own row: the kernel would answer `no-consumer` for the event this
        // call is about to build, so it is never built.
        if (_gates.TryGetValue(ExpeditionContract.EndedBinding, out var gate) && !gate.HasSubscribers) return null;
        var result = _registration.Publish(new RuntimeEvent(
            ExpeditionContract.EventId(_kernel.WorldEpoch, endState), ExpeditionContract.EndedBinding,
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), Scope(), Payload(outcome)));
        if (result.Status == "queued") _publishedFacts++;
        if (result.Status == "rejected")
            ReportOnce("publish:" + result.Code, "expedition end fact rejected: " + result.Code);
        return result;
    }

    /// <summary>One published payload, built port by port: the row declares one value output, and it carries the
    /// outcome's own member index — the wire form of an enum port, never its name.</summary>
    private static JsonElement Payload(int outcome)
        => RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["outcome"] = RuntimeJson.From(outcome)
        });

    private string Scope() => "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _report(message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "The expedition trigger requires the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        _disposed = true;
        _reported.Clear();
    }
}
