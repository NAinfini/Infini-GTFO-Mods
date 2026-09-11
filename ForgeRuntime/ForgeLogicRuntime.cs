namespace Infini.ForgeRuntime;

public sealed record ForgeConditionStep(string CapabilityId, IReadOnlyDictionary<string, string>? Parameters = null);
public sealed record ForgeActionStep(string CapabilityId, IReadOnlyDictionary<string, string>? Parameters = null);
public sealed record ForgeRuleDefinition(
    string Id,
    string TriggerId,
    IReadOnlyList<ForgeConditionStep> Conditions,
    IReadOnlyList<ForgeActionStep> Actions);

public enum ForgeLogicTraceKind
{
    RuleStarted,
    ConditionPassed,
    ConditionStoppedRule,
    ActionCompleted,
    RuleCompleted,
    RuleFailed
}

public sealed record ForgeLogicTraceEntry(
    long Index,
    ForgeLogicTraceKind Kind,
    string RuleId,
    string EventId,
    string? CapabilityId = null,
    string? ProviderId = null,
    string? Message = null);

public sealed class ForgeLogicRuntime
{
    private sealed record ConditionRegistration(string ProviderId, Func<ForgeRuleContext, IReadOnlyDictionary<string, string>, bool> Handler);
    private sealed record ActionRegistration(string ProviderId, Action<ForgeRuleContext, IReadOnlyDictionary<string, string>> Handler);

    private readonly CanonicalTriggerRuntime _triggers;
    private readonly Dictionary<string, ConditionRegistration> _conditions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActionRegistration> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _rules = new(StringComparer.Ordinal);
    private readonly Queue<ForgeLogicTraceEntry> _trace = new();
    private readonly int _traceCapacity;
    private long _traceIndex;

    public ForgeLogicRuntime(CanonicalTriggerRuntime triggers, int traceCapacity = 4096)
    {
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        if (traceCapacity is < 32 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(traceCapacity));
        _traceCapacity = traceCapacity;
    }

    public void RegisterCondition(
        string capabilityId,
        string providerId,
        Func<ForgeRuleContext, IReadOnlyDictionary<string, string>, bool> handler)
    {
        ValidateCapability(capabilityId, "condition");
        ValidateToken(providerId, nameof(providerId));
        ArgumentNullException.ThrowIfNull(handler);
        if (!_conditions.TryAdd(capabilityId, new ConditionRegistration(providerId, handler)))
            throw new InvalidOperationException($"Condition capability already registered: {capabilityId}");
    }

    public void RegisterAction(
        string capabilityId,
        string providerId,
        Action<ForgeRuleContext, IReadOnlyDictionary<string, string>> handler)
    {
        ValidateCapability(capabilityId, "action");
        ValidateToken(providerId, nameof(providerId));
        ArgumentNullException.ThrowIfNull(handler);
        if (!_actions.TryAdd(capabilityId, new ActionRegistration(providerId, handler)))
            throw new InvalidOperationException($"Action capability already registered: {capabilityId}");
    }

    public IDisposable InstallRule(ForgeRuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateToken(definition.Id, "rule id");
        if (!definition.TriggerId.StartsWith("forge.trigger.", StringComparison.Ordinal))
            throw new ArgumentException("Rules must begin at a canonical Forge trigger.", nameof(definition));
        if (definition.Conditions is null || definition.Actions is null)
            throw new ArgumentException("Rule condition/action lists are required.", nameof(definition));
        if (definition.Conditions.Count > 256 || definition.Actions.Count > 1024)
            throw new ArgumentException("Rule exceeds condition/action safety limits.", nameof(definition));
        if (_rules.ContainsKey(definition.Id))
            throw new InvalidOperationException($"Rule already installed: {definition.Id}");

        var conditions = definition.Conditions.Select(step =>
        {
            if (!_conditions.TryGetValue(step.CapabilityId, out var registration))
                throw new InvalidOperationException($"Condition is not registered: {step.CapabilityId}");
            return (Step: new ForgeConditionStep(step.CapabilityId, CopyParameters(step.Parameters)), Registration: registration);
        }).ToArray();
        var actions = definition.Actions.Select(step =>
        {
            if (!_actions.TryGetValue(step.CapabilityId, out var registration))
                throw new InvalidOperationException($"Action is not registered: {step.CapabilityId}");
            return (Step: new ForgeActionStep(step.CapabilityId, CopyParameters(step.Parameters)), Registration: registration);
        }).ToArray();

        var subscription = _triggers.Observe(definition.TriggerId, $"forge.rule.{definition.Id}", trigger =>
        {
            var context = new ForgeRuleContext(trigger, definition.Id);
            Trace(ForgeLogicTraceKind.RuleStarted, definition.Id, trigger.Event.EventId);
            try
            {
                foreach (var item in conditions)
                {
                    var parameters = item.Step.Parameters ?? EmptyParameters.Instance;
                    var passed = item.Registration.Handler(context, parameters);
                    Trace(passed ? ForgeLogicTraceKind.ConditionPassed : ForgeLogicTraceKind.ConditionStoppedRule,
                        definition.Id, trigger.Event.EventId, item.Step.CapabilityId, item.Registration.ProviderId);
                    if (!passed)
                        return;
                }
                foreach (var item in actions)
                {
                    item.Registration.Handler(context, item.Step.Parameters ?? EmptyParameters.Instance);
                    Trace(ForgeLogicTraceKind.ActionCompleted, definition.Id, trigger.Event.EventId,
                        item.Step.CapabilityId, item.Registration.ProviderId);
                }
                Trace(ForgeLogicTraceKind.RuleCompleted, definition.Id, trigger.Event.EventId);
            }
            catch (Exception error)
            {
                var message = error.GetType().Name + ": " + error.Message;
                if (message.Length > 1000)
                    message = message[..1000];
                Trace(ForgeLogicTraceKind.RuleFailed, definition.Id, trigger.Event.EventId, message: message);
                throw;
            }
        });
        _rules.Add(definition.Id, subscription);
        return new InstalledRule(this, definition.Id, subscription);
    }

    public IReadOnlyList<ForgeLogicTraceEntry> GetTrace() => _trace.ToArray();

    private void RemoveRule(string id, IDisposable subscription)
    {
        if (_rules.TryGetValue(id, out var current) && ReferenceEquals(current, subscription))
            _rules.Remove(id);
        subscription.Dispose();
    }

    private void Trace(ForgeLogicTraceKind kind, string ruleId, string eventId,
        string? capabilityId = null, string? providerId = null, string? message = null)
    {
        _trace.Enqueue(new ForgeLogicTraceEntry(++_traceIndex, kind, ruleId, eventId, capabilityId, providerId, message));
        while (_trace.Count > _traceCapacity)
            _trace.Dequeue();
    }

    private static IReadOnlyDictionary<string, string> CopyParameters(IReadOnlyDictionary<string, string>? value)
    {
        if (value is null || value.Count == 0)
            return EmptyParameters.Instance;
        if (value.Count > 256)
            throw new ArgumentException("Step parameter count exceeds 256.");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in value)
        {
            ValidateToken(pair.Key, "parameter key");
            if (pair.Value is null || pair.Value.Length > 16_384 || pair.Value.Any(char.IsControl))
                throw new ArgumentException("Invalid parameter value.");
            if (!result.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Duplicate parameter key.");
        }
        return result;
    }

    private static void ValidateCapability(string value, string kind)
    {
        ValidateToken(value, $"{kind} capability id");
        var canonical = $"forge.{kind}.";
        if (!value.StartsWith(canonical, StringComparison.Ordinal) && !value.Contains('.', StringComparison.Ordinal))
            throw new ArgumentException($"Invalid {kind} capability id.", nameof(value));
    }

    private static void ValidateToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException($"Invalid {name}.", name);
    }

    private sealed class InstalledRule : IDisposable
    {
        private ForgeLogicRuntime? _owner;
        private readonly string _id;
        private readonly IDisposable _subscription;

        public InstalledRule(ForgeLogicRuntime owner, string id, IDisposable subscription)
        {
            _owner = owner;
            _id = id;
            _subscription = subscription;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.RemoveRule(_id, _subscription);
        }
    }

    private sealed class EmptyParameters : IReadOnlyDictionary<string, string>
    {
        public static readonly EmptyParameters Instance = new();
        public int Count => 0;
        public IEnumerable<string> Keys => Array.Empty<string>();
        public IEnumerable<string> Values => Array.Empty<string>();
        public string this[string key] => throw new KeyNotFoundException();
        public bool ContainsKey(string key) => false;
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        public bool TryGetValue(string key, out string value) { value = null!; return false; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public sealed class ForgeRuleContext
{
    private readonly ForgeTriggerContext _trigger;

    internal ForgeRuleContext(ForgeTriggerContext trigger, string ruleId)
    {
        _trigger = trigger;
        RuleId = ruleId;
    }

    public string RuleId { get; }
    public ForgeTriggerEvent Event => _trigger.Event;
    public bool TryGetState(string key, out string value) => _trigger.TryGetState(key, out value!);
    public void SetState(string key, string value) => _trigger.SetState(key, value);
}
