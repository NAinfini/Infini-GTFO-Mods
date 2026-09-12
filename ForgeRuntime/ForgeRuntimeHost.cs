namespace Infini.ForgeRuntime;

public enum ForgeProviderKind
{
    Native,
    Adapter,
    Extension
}

public sealed record ForgeProviderInfo(string Id, ForgeProviderKind Kind);

/// <summary>
/// Shared entry point for Forge-native handlers, legacy adapters and reviewed extensions.
/// Providers may report facts and register behavior, but canonical scheduling/dedupe remains
/// inside <see cref="CanonicalTriggerRuntime"/>. Content packs do not need a provider unless
/// they introduce executable behavior beyond a supported base-framework profile.
/// </summary>
public sealed class ForgeRuntimeHost
{
    private readonly Dictionary<string, ForgeProviderInfo> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _extensionTriggers = new(StringComparer.Ordinal);

    public ForgeRuntimeHost(Func<bool> isHost)
    {
        Triggers = new CanonicalTriggerRuntime(isHost);
        Logic = new ForgeLogicRuntime(Triggers);
        RegisterProvider("forge.core", ForgeProviderKind.Native);
    }

    public CanonicalTriggerRuntime Triggers { get; }
    public ForgeLogicRuntime Logic { get; }

    public ForgeProviderInfo RegisterProvider(string providerId, ForgeProviderKind kind)
    {
        ValidateProviderId(providerId);
        if (kind == ForgeProviderKind.Native && !providerId.StartsWith("forge.", StringComparison.Ordinal))
            throw new ArgumentException("Native provider ids must use the forge namespace.", nameof(providerId));
        if (kind != ForgeProviderKind.Native && providerId.StartsWith("forge.", StringComparison.Ordinal))
            throw new ArgumentException("External providers cannot claim the forge namespace.", nameof(providerId));
        var value = new ForgeProviderInfo(providerId, kind);
        if (!_providers.TryAdd(providerId, value))
            throw new InvalidOperationException($"Provider already registered: {providerId}");
        return value;
    }

    public void RegisterExtensionTrigger(string providerId, string triggerId)
    {
        var provider = RequireProvider(providerId);
        if (provider.Kind != ForgeProviderKind.Extension)
            throw new InvalidOperationException("Only extension providers may create new trigger semantics.");
        ValidateCapabilityId(triggerId, "trigger");
        if (triggerId.StartsWith("forge.", StringComparison.Ordinal))
            throw new InvalidOperationException("Extensions cannot replace a canonical Forge trigger.");
        if (!triggerId.StartsWith(providerId + ".trigger.", StringComparison.Ordinal))
            throw new ArgumentException("Extension trigger must live under its provider trigger namespace.", nameof(triggerId));
        if (!_extensionTriggers.TryAdd(triggerId, providerId))
            throw new InvalidOperationException($"Trigger already registered: {triggerId}");
        Triggers.RegisterExtensionTrigger(triggerId, providerId);
    }

    public IDisposable ObserveTrigger(string providerId, string triggerId, Action<ForgeTriggerContext> callback)
    {
        RequireProvider(providerId);
        EnsureKnownTrigger(triggerId);
        return Triggers.Observe(triggerId, providerId, callback);
    }

    public ForgeDispatchResult ReportTrigger(
        string providerId,
        string triggerId,
        string occurrenceId,
        string scopeId,
        long sequence,
        IReadOnlyDictionary<string, string>? payload = null)
    {
        var provider = RequireProvider(providerId);
        EnsureKnownTrigger(triggerId);
        if (_extensionTriggers.TryGetValue(triggerId, out var owner) && owner != provider.Id)
            throw new InvalidOperationException($"Extension trigger '{triggerId}' is owned by '{owner}', not '{provider.Id}'.");
        ValidateOccurrenceId(occurrenceId);
        // Namespace source occurrences so two adapters observing the same raw numeric/event id
        // never suppress one another accidentally. Forge still owns the final dedupe table.
        var eventId = providerId + ":" + occurrenceId;
        return Triggers.Publish(new ForgeTriggerEvent(eventId, triggerId, scopeId, sequence, payload));
    }

    public void RegisterCondition(
        string providerId,
        string capabilityId,
        Func<ForgeRuleContext, IReadOnlyDictionary<string, string>, bool> handler)
    {
        RequireProvider(providerId);
        ValidateOwnedCapability(providerId, capabilityId, "condition");
        Logic.RegisterCondition(capabilityId, providerId, handler);
    }

    public void RegisterAction(
        string providerId,
        string capabilityId,
        Action<ForgeRuleContext, IReadOnlyDictionary<string, string>> handler)
    {
        RequireProvider(providerId);
        ValidateOwnedCapability(providerId, capabilityId, "action");
        Logic.RegisterAction(capabilityId, providerId, handler);
    }

    public ForgeProviderInfo GetProvider(string providerId) => RequireProvider(providerId);

    private ForgeProviderInfo RequireProvider(string providerId)
    {
        ValidateProviderId(providerId);
        if (!_providers.TryGetValue(providerId, out var value))
            throw new InvalidOperationException($"Unknown Forge provider: {providerId}");
        return value;
    }

    private void EnsureKnownTrigger(string triggerId)
    {
        ValidateCapabilityId(triggerId, "trigger");
        if (!triggerId.StartsWith("forge.trigger.", StringComparison.Ordinal) && !_extensionTriggers.ContainsKey(triggerId))
            throw new InvalidOperationException($"Unknown extension trigger: {triggerId}");
    }

    private ForgeProviderInfo ValidateOwnedCapability(string providerId, string capabilityId, string kind)
    {
        var provider = RequireProvider(providerId);
        ValidateCapabilityId(capabilityId, kind);
        if (capabilityId.StartsWith("forge.", StringComparison.Ordinal))
        {
            if (provider.Kind != ForgeProviderKind.Native)
                throw new InvalidOperationException($"External provider '{providerId}' cannot own canonical capability '{capabilityId}'.");
        }
        else if (!capabilityId.StartsWith(providerId + "." + kind + ".", StringComparison.Ordinal))
        {
            throw new ArgumentException($"External {kind} must live under its provider namespace.", nameof(capabilityId));
        }
        return provider;
    }

    private static void ValidateProviderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl) ||
            value.StartsWith('.') || value.EndsWith('.') || value.Contains("..", StringComparison.Ordinal) ||
            value.Any(ch => !(char.IsLower(ch) || char.IsDigit(ch) || ch is '.' or '_')))
            throw new ArgumentException("Invalid provider id.", nameof(value));
    }

    private static void ValidateCapabilityId(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl) || !value.Contains('.'))
            throw new ArgumentException($"Invalid {kind} capability id.", nameof(value));
    }

    private static void ValidateOccurrenceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 192 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid provider occurrence id.", nameof(value));
    }
}
