using System.Collections.ObjectModel;

namespace Infini.ForgeRuntime;

public enum ForgeDispatchStatus
{
    Accepted,
    Duplicate,
    RejectedNotHost
}

public enum ForgeTraceKind
{
    EventAccepted,
    EventDuplicate,
    EventRejectedNotHost,
    ObserverFailed,
    StateChanged,
    CheckpointCaptured,
    CheckpointRestored,
    ExpeditionReset
}

public sealed record ForgeTriggerEvent(
    string EventId,
    string TriggerId,
    string ScopeId,
    long Sequence,
    IReadOnlyDictionary<string, string>? Payload = null);

public sealed record ForgeObserverFailure(string ProviderId, string Message);

public sealed record ForgeDispatchResult(
    ForgeDispatchStatus Status,
    int ObserversInvoked,
    IReadOnlyList<ForgeObserverFailure> Failures);

public sealed record ForgeTraceEntry(
    long Index,
    ForgeTraceKind Kind,
    string Message,
    string? EventId = null,
    string? TriggerId = null,
    string? ScopeId = null,
    string? ProviderId = null);

public sealed record ForgeCheckpoint(
    long Generation,
    long TraceIndex,
    IReadOnlyList<string> ProcessedEventIds,
    IReadOnlyDictionary<string, string> State);

public sealed class CanonicalTriggerRuntime
{
    private sealed record Observer(long Order, string ProviderId, Action<ForgeTriggerContext> Callback);

    private readonly Func<bool> _isHost;
    private readonly int _traceCapacity;
    private readonly Dictionary<string, List<Observer>> _observers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _extensionTriggers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _processed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _state = new(StringComparer.Ordinal);
    private readonly Queue<ForgeTraceEntry> _trace = new();
    private long _registrationOrder;
    private long _traceIndex;
    private long _generation = 1;

    public CanonicalTriggerRuntime(Func<bool> isHost, int traceCapacity = 4096)
    {
        _isHost = isHost ?? throw new ArgumentNullException(nameof(isHost));
        if (traceCapacity is < 32 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(traceCapacity));
        _traceCapacity = traceCapacity;
    }

    public long Generation => _generation;

    /// <summary>
    /// Called only by ForgeRuntimeHost after provider ownership review. Generic trigger
    /// scheduling remains centralized here even when the trigger semantic is community-defined.
    /// </summary>
    internal void RegisterExtensionTrigger(string triggerId, string ownerProviderId)
    {
        ValidateToken(triggerId, "trigger id", 256);
        ValidateToken(ownerProviderId, "trigger owner", 160);
        if (triggerId.StartsWith("forge.trigger.", StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical Forge triggers are reserved.");
        if (!_extensionTriggers.TryAdd(triggerId, ownerProviderId))
            throw new InvalidOperationException($"Extension trigger already registered: {triggerId}");
    }

    public IDisposable Observe(string triggerId, string providerId, Action<ForgeTriggerContext> callback)
    {
        ValidateTriggerId(triggerId);
        ValidateToken(providerId, nameof(providerId), 160);
        ArgumentNullException.ThrowIfNull(callback);

        var observer = new Observer(++_registrationOrder, providerId, callback);
        if (!_observers.TryGetValue(triggerId, out var rows))
        {
            rows = new List<Observer>();
            _observers.Add(triggerId, rows);
        }
        rows.Add(observer);
        return new Subscription(this, triggerId, observer.Order);
    }

    public ForgeDispatchResult Publish(ForgeTriggerEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateToken(value.EventId, nameof(value.EventId), 256);
        ValidateTriggerId(value.TriggerId);
        ValidateToken(value.ScopeId, nameof(value.ScopeId), 256);
        if (value.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(value.Sequence));
        ValidatePayload(value.Payload);

        if (!_isHost())
        {
            Trace(ForgeTraceKind.EventRejectedNotHost, "Trigger rejected on a non-host peer.", value);
            return new ForgeDispatchResult(ForgeDispatchStatus.RejectedNotHost, 0, Array.Empty<ForgeObserverFailure>());
        }

        if (!_processed.Add(value.EventId))
        {
            Trace(ForgeTraceKind.EventDuplicate, "Duplicate trigger ignored.", value);
            return new ForgeDispatchResult(ForgeDispatchStatus.Duplicate, 0, Array.Empty<ForgeObserverFailure>());
        }

        Trace(ForgeTraceKind.EventAccepted, "Trigger accepted by the Forge host scheduler.", value);
        if (!_observers.TryGetValue(value.TriggerId, out var observers) || observers.Count == 0)
            return new ForgeDispatchResult(ForgeDispatchStatus.Accepted, 0, Array.Empty<ForgeObserverFailure>());

        // Snapshot registration order. An observer may register/unregister during dispatch
        // without changing which callbacks receive this already accepted event.
        var snapshot = observers.OrderBy(row => row.Order).ToArray();
        var failures = new List<ForgeObserverFailure>();
        var context = new ForgeTriggerContext(this, value);
        foreach (var observer in snapshot)
        {
            try
            {
                observer.Callback(context);
            }
            catch (Exception error)
            {
                var message = error.GetType().Name + ": " + error.Message;
                if (message.Length > 1000)
                    message = message[..1000];
                failures.Add(new ForgeObserverFailure(observer.ProviderId, message));
                Trace(ForgeTraceKind.ObserverFailed, message, value, observer.ProviderId);
            }
        }
        return new ForgeDispatchResult(ForgeDispatchStatus.Accepted, snapshot.Length, failures.AsReadOnly());
    }

    public ForgeCheckpoint CaptureCheckpoint()
    {
        RequireHost();
        var result = new ForgeCheckpoint(
            _generation,
            _traceIndex,
            _processed.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            new ReadOnlyDictionary<string, string>(new SortedDictionary<string, string>(_state, StringComparer.Ordinal)));
        Trace(ForgeTraceKind.CheckpointCaptured, "Forge checkpoint captured.");
        return result;
    }

    public void RestoreCheckpoint(ForgeCheckpoint checkpoint)
    {
        RequireHost();
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        if (checkpoint.ProcessedEventIds.Count > 1_000_000 || checkpoint.State.Count > 100_000)
            throw new InvalidOperationException("Checkpoint exceeds runtime safety limits.");

        var processed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in checkpoint.ProcessedEventIds)
        {
            ValidateToken(id, "checkpoint event id", 256);
            if (!processed.Add(id))
                throw new InvalidOperationException("Checkpoint contains duplicate event ids.");
        }
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in checkpoint.State)
        {
            ValidateToken(pair.Key, "checkpoint state key", 256);
            ValidateStateValue(pair.Value);
            if (!state.TryAdd(pair.Key, pair.Value))
                throw new InvalidOperationException("Checkpoint contains duplicate state keys.");
        }

        _processed.Clear();
        foreach (var id in processed)
            _processed.Add(id);
        _state.Clear();
        foreach (var pair in state)
            _state.Add(pair.Key, pair.Value);
        _generation = checkpoint.Generation;
        Trace(ForgeTraceKind.CheckpointRestored, "Forge checkpoint restored.");
    }

    public void ResetForExpedition()
    {
        RequireHost();
        _processed.Clear();
        _state.Clear();
        checked { _generation++; }
        Trace(ForgeTraceKind.ExpeditionReset, "Forge runtime reset for a new expedition.");
    }

    public IReadOnlyList<ForgeTraceEntry> GetTrace() => _trace.ToArray();

    internal bool TryGetState(string key, out string value)
    {
        ValidateToken(key, nameof(key), 256);
        return _state.TryGetValue(key, out value!);
    }

    internal void SetState(string key, string value, ForgeTriggerEvent source)
    {
        RequireHost();
        ValidateToken(key, nameof(key), 256);
        ValidateStateValue(value);
        _state[key] = value;
        Trace(ForgeTraceKind.StateChanged, $"State '{key}' changed.", source);
    }

    private void Remove(string triggerId, long order)
    {
        if (!_observers.TryGetValue(triggerId, out var rows))
            return;
        rows.RemoveAll(row => row.Order == order);
        if (rows.Count == 0)
            _observers.Remove(triggerId);
    }

    private void RequireHost()
    {
        if (!_isHost())
            throw new InvalidOperationException("Forge canonical state may only be mutated by the host authority.");
    }

    private void Trace(ForgeTraceKind kind, string message, ForgeTriggerEvent? source = null, string? providerId = null)
    {
        var entry = new ForgeTraceEntry(++_traceIndex, kind, message, source?.EventId, source?.TriggerId, source?.ScopeId, providerId);
        _trace.Enqueue(entry);
        while (_trace.Count > _traceCapacity)
            _trace.Dequeue();
    }

    private void ValidateTriggerId(string value)
    {
        ValidateToken(value, "trigger id", 256);
        if (!value.StartsWith("forge.trigger.", StringComparison.Ordinal) && !_extensionTriggers.ContainsKey(value))
            throw new ArgumentException("Trigger is neither canonical Forge nor a registered extension trigger.", nameof(value));
    }

    private static void ValidatePayload(IReadOnlyDictionary<string, string>? payload)
    {
        if (payload is null)
            return;
        if (payload.Count > 256)
            throw new ArgumentException("Trigger payload exceeds 256 fields.", nameof(payload));
        foreach (var pair in payload)
        {
            ValidateToken(pair.Key, "payload key", 128);
            ValidateStateValue(pair.Value);
        }
    }

    private static void ValidateStateValue(string value)
    {
        if (value is null || value.Length > 16_384 || value.Any(char.IsControl))
            throw new ArgumentException("State value is invalid or exceeds 16 KiB.", nameof(value));
    }

    private static void ValidateToken(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new ArgumentException($"Invalid {name}.", name);
    }

    private sealed class Subscription : IDisposable
    {
        private CanonicalTriggerRuntime? _runtime;
        private readonly string _triggerId;
        private readonly long _order;

        public Subscription(CanonicalTriggerRuntime runtime, string triggerId, long order)
        {
            _runtime = runtime;
            _triggerId = triggerId;
            _order = order;
        }

        public void Dispose()
        {
            var runtime = Interlocked.Exchange(ref _runtime, null);
            runtime?.Remove(_triggerId, _order);
        }
    }
}

public sealed class ForgeTriggerContext
{
    private readonly CanonicalTriggerRuntime _runtime;

    internal ForgeTriggerContext(CanonicalTriggerRuntime runtime, ForgeTriggerEvent source)
    {
        _runtime = runtime;
        Event = source;
    }

    public ForgeTriggerEvent Event { get; }

    public bool TryGetState(string key, out string value) => _runtime.TryGetState(key, out value!);

    public void SetState(string key, string value) => _runtime.SetState(key, value, Event);
}
