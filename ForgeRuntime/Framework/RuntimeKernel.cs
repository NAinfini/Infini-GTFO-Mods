using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ForgeRuntime.Framework;

/// <summary>A module can publish its own bindings and cancel its own scopes. This is ownership isolation, not an untrusted-code sandbox.</summary>
public sealed partial class RuntimeModuleHandle : IDisposable
{
    private readonly RuntimeKernel kernel;
    internal RuntimeModuleHandle(RuntimeKernel kernel, string providerId, long generation)
    { this.kernel = kernel; ProviderId = providerId; Generation = generation; }
    public string ProviderId { get; }
    internal long Generation { get; }
    public bool IsRegistered => kernel.IsRegistered(ProviderId, Generation);
    public DispatchResult Publish(RuntimeEvent value) => kernel.Publish(this, value);
    public int CancelScope(string scopeId) => kernel.CancelScope(this, scopeId);
    public void Dispose() => kernel.Unregister(this);
}

/// <summary>Single-thread simulation dispatcher. Registration and plans are resolved once; no Unity/game types are owned here.</summary>
public sealed partial class RuntimeKernel
{
    /// <summary>2.0.0 is the Forge Standard v0.2 wire (schemaVersion 2 plans); it moves together with the website.</summary>
    public const string ApiVersion = "2.0.0";
    public const int MaximumEventHistory = 65536;
    public const int MaximumEventPayloadBytes = 65536;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private RuntimeRegistry registry = new();
    private readonly Dictionary<string, long> modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedPlan> plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Work>> subscriptions = new(StringComparer.Ordinal);
    private readonly PriorityQueue<Pending, (long Tick, long Sequence)> queue = new();
    private readonly Dictionary<string, string> history = new(StringComparer.Ordinal);
    private readonly HashSet<(string Provider, string Scope)> cancelled = new();
    private readonly Dictionary<string, (int Events, int Commands)> planTickUsage = new(StringComparer.Ordinal);
    private long generation, sequence;
    private bool advancing, worldStarted;
    private bool? worldHost;
    private int eventsThisTick, commandsThisTick;
    /// <summary>`event.deferred` is one record per tick even when several events wait on it.</summary>
    private bool deferredLogged;
    private CommandContext? currentCommand;
    public RuntimeIdentity Identity { get; }
    public RuntimeLimits Limits { get; }
    public long WorldEpoch { get; private set; }
    public long CurrentTick { get; private set; } = -1;
    public int QueuedEvents => queue.Count;
    public int LoadedPlans => plans.Count;
    private sealed record LoadedPlan(ResolvedPlan Plan, IReadOnlyDictionary<string, long> Modules);
    private sealed record Work(LoadedPlan Plan, ResolvedEntry Entry);
    private sealed record Pending(string Provider, long Generation, RuntimeEvent Event, IReadOnlyList<Work> Work, ScheduledJob? Schedule = null);

    public RuntimeKernel(RuntimeIdentity identity, RuntimeLimits? limits = null)
    {
        RuntimeJson.Require(identity.ApiVersion == ApiVersion, "api-version", "Unsupported runtime API.");
        var json = RuntimeJson.From(identity);
        RuntimeJson.Id(json, "id"); RuntimeJson.Version(json, "version"); RuntimeJson.Text(json, "gameBuild");
        Identity = identity; Limits = limits ?? new RuntimeLimits();
        var actual = RuntimeJson.From(Limits); var ceiling = RuntimeJson.From(new RuntimeLimits());
        foreach (var property in actual.EnumerateObject()) RuntimeJson.Integer(property.Value, 1, ceiling.GetProperty(property.Name).GetInt32());
    }
    private void Thread() { ReadThread(); AcceptRuntimeWork(); }
    private void Mutable()
    { ReadThread(); NoLifecycleMutation(); RuntimeJson.Require(!advancing, "reentrant-mutation", "Cannot change registration, plans or world during dispatch."); }
    internal bool IsRegistered(string provider, long token) => modules.TryGetValue(provider, out var current) && current == token;
    /// <summary>I-DIAG D-007. The level is the registering package's own cfg value and is required: a provider has no level
    /// until its package hands one over, and the level table never invents a default for it. One package registering more
    /// than one provider passes the same level for each. The Runtime's own built-in providers do not come through here.</summary>
    public RuntimeModuleHandle RegisterModule(RuntimeModule module, RuntimeLogLevel level)
    {
        RuntimeJson.Require(level is RuntimeLogLevel.Off or RuntimeLogLevel.Error or RuntimeLogLevel.Info, "log-level",
            "Configured log levels are off, error or info; trace is only reachable through elevation.");
        return Register(module, level);
    }
    /// <summary>The Runtime's own built-in providers (CombatContracts, ControlContracts, the trigger frame module) ship no
    /// package cfg, so they take the Runtime provider's level. Host assembly only: a domain package must hand over its own.</summary>
    internal RuntimeModuleHandle RegisterBuiltinModule(RuntimeModule module) => Register(module, runtimeLogLevel);
    private RuntimeModuleHandle Register(RuntimeModule module, RuntimeLogLevel level)
    {
        Mutable(); RuntimeRegistry next; string provider;
        try
        {
            RuntimeJson.Require(IsRegistrationOpen, "registration-closed", "Module registration is frozen before host startup.");
            RuntimeJson.Require(modules.Count < 128, "module-budget", "Module capacity reached.");
            next = registry.WithModule(module, ApiVersion, out var declared);
            provider = declared;
            RuntimeJson.Require(next.Providers.Count <= 2048 && next.Capabilities.Count <= 2048 && next.Bindings.Count <= 2048, "registry-budget", "Registration capacity reached.");
        }
        catch (RuntimeContractException ex)
        {
            // The seed's own provider id is the only thing known before the seed validates; anything else is Runtime's own record without a subject.
            LogRegistrationRejected(DeclaredProvider(module), ex.Code, ex.Message);
            throw;
        }
        registry = next; var token = ++generation; modules.Add(provider, token);
        RegisterLogGate(provider, level);
        if (logSink != null) PublishLogLevels(logLevels!.Tier);
        LogRegisteredBindings(provider);
        return new RuntimeModuleHandle(this, provider, token);
    }
    /// <summary>Reads the provider id straight out of the seed text: the exception path cannot use the parsed registry, and an
    /// unreadable seed simply has no subject provider.</summary>
    private static string DeclaredProvider(RuntimeModule module)
    {
        try
        {
            var providers = RuntimeJson.Rows(RuntimeJson.Parse(module.RegistryJson), "providers");
            return providers.Length == 1 ? RuntimeJson.Text(providers[0], "id") : "";
        }
        catch (Exception) { return ""; }
    }
    private void LogRegisteredBindings(string provider)
    {
        if (logSink == null || !LogGate(provider).IsEnabled(RuntimeLogLevel.Info)) return;
        foreach (var binding in registry.Bindings.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray())
            if (RuntimeJson.Text(binding.Value, "providerId") == provider) LogBindingRegistered(provider, binding.Key);
    }
    internal void Unregister(RuntimeModuleHandle handle)
    {
        Mutable(); if (!handle.IsRegistered) return;
        var provider = handle.ProviderId;
        var ownedCaps = registry.CapabilityRegistrants.Where(x => x.Value == provider).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var ownedBindings = registry.Bindings.Where(x => RuntimeJson.Text(x.Value, "providerId") == provider).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var binding in registry.Bindings.Values.Where(b => RuntimeJson.Text(b, "providerId") != provider))
            RuntimeJson.Require(!ownedCaps.Contains(RuntimeJson.Text(binding, "capabilityId")) && !RuntimeJson.Strings(binding.GetProperty("requires")).Any(ownedBindings.Contains), "module-in-use", "Another module still requires this module's contracts/bindings.");
        RuntimeJson.Require(!stateLeases.Values.Any(l => l.Handle.ProviderId != provider && ownedCaps.Contains(l.Key.Definition)), "module-in-use", "Another module still holds a lease of this definition.");
        // A queued event whose publisher unregisters is dropped at its next dispatch, where the removal is recorded with the
        // reason the pending snapshot carries (source-lifecycle / plan-unloaded); cancel and unload count before removal.
        StopScheduledSource(provider, null, "module-unregistered"); StopStateSource(provider, null, "module-unregistered");
        RemoveLifecycleObservers(provider, handle.Generation);
        modules.Remove(provider); registry.Providers.Remove(provider);
        foreach (var id in ownedBindings) { registry.Bindings.Remove(id); registry.Handlers.Remove(id); registry.Evaluators.Remove(id); registry.Support.Remove(id); }
        foreach (var id in ownedCaps) { registry.Capabilities.Remove(id); registry.CapabilityRegistrants.Remove(id); }
        foreach (var key in registry.Resolvers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray()) registry.Resolvers.Remove(key);
        foreach (var key in registry.EntityObservers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.EntityObservers.Remove(key);
        foreach (var key in registry.EntityInstanceResolvers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.EntityInstanceResolvers.Remove(key);
        foreach (var id in plans.Where(x => x.Value.Modules.ContainsKey(provider)).Select(x => x.Key).ToArray()) plans.Remove(id);
        UnregisterLogGate(provider);
        RebuildSubscriptions();
    }
    public string ExportManifest()
    {
        ReadThread(); return RuntimeJson.StableText(RuntimeJson.From(new {
            schemaVersion = 1, runtime = Identity, registry = registry.Snapshot(),
            bindingSupport = registry.Support.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray(), limits = Limits
        }));
    }
    /// <summary>I-PACK D-009 batch load. Every file gets its own outcome and its own plan.loaded/plan.rejected record;
    /// one file's problem never affects another's, and the whole call never throws for a domain-level rejection.
    /// Files are expected pre-sorted by the host (ordinal on the BepInEx-relative path); that order drives conflict-group
    /// order, load order and which files the 128-plan cap truncates.</summary>
    public IReadOnlyList<PlanLoadOutcome> LoadPlans(IReadOnlyList<PlanCandidate> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Mutable();
        AcceptRuntimeWork(true);
        var outcomes = new List<PlanLoadOutcome>(files.Count);
        var identified = new List<(PlanCandidate File, PlanIdentity Identity)>();
        foreach (var file in files)
        {
            if (file.IsHostRejected)
            {
                LogPlanRejected(file.Path, null, file.RejectedCode!);
                outcomes.Add(new PlanLoadOutcome(file.Path, false, null, file.RejectedCode, file.RejectedDetail));
                continue;
            }
            try { identified.Add((file, RuntimePlan.PeekIdentity(file.Json!, Identity))); }
            catch (RuntimeContractException ex)
            {
                // A file whose planId cannot even be parsed is rejected on its own error and never joins conflict grouping.
                LogPlanRejected(file.Path, null, ex.Code);
                outcomes.Add(new PlanLoadOutcome(file.Path, false, null, ex.Code, ex.Message));
            }
        }
        var loadedAny = false;
        foreach (var group in identified.GroupBy(x => x.Identity.Id, StringComparer.Ordinal))
        {
            var members = group.ToArray();
            if (members.Length > 1)
            {
                // Each conflicting file gets its own path-carrying record; collectively the group's records cover every path.
                var paths = string.Join(", ", members.Select(m => m.File.Path).OrderBy(p => p, StringComparer.Ordinal));
                foreach (var member in members)
                {
                    var plan = new RuntimeLogPlan { PlanId = member.Identity.Id, ResourceId = member.Identity.ResourceId, ResourceRevision = member.Identity.ResourceRevision };
                    LogPlanRejected(member.File.Path, plan, "plan-conflict", paths);
                    outcomes.Add(new PlanLoadOutcome(member.File.Path, false, member.Identity.Id, "plan-conflict", paths));
                }
                continue;
            }
            var (file, identity) = members[0];
            var identityPlan = new RuntimeLogPlan { PlanId = identity.Id, ResourceId = identity.ResourceId, ResourceRevision = identity.ResourceRevision };
            if (plans.ContainsKey(identity.Id))
            {
                LogPlanRejected(file.Path, identityPlan, "plan-conflict", file.Path);
                outcomes.Add(new PlanLoadOutcome(file.Path, false, identity.Id, "plan-conflict", file.Path));
                continue;
            }
            if (plans.Count >= 128)
            {
                LogPlanRejected(file.Path, identityPlan, "plan-budget");
                outcomes.Add(new PlanLoadOutcome(file.Path, false, identity.Id, "plan-budget", "Plan capacity reached."));
                continue;
            }
            ResolvedPlan resolved;
            try { resolved = RuntimePlan.Parse(file.Json!, Identity, Limits, registry); }
            catch (RuntimeContractException ex)
            {
                LogPlanRejected(file.Path, identityPlan, ex.Code);
                outcomes.Add(new PlanLoadOutcome(file.Path, false, identity.Id, ex.Code, ex.Message));
                continue;
            }
            var providers = resolved.Bindings.Select(b => RuntimeJson.Text(registry.Bindings[b], "providerId"))
                .Concat(resolved.Bindings.Select(b => RuntimeJson.Text(registry.Capabilities[RuntimeJson.Text(registry.Bindings[b], "capabilityId")], "owner"))).Distinct(StringComparer.Ordinal);
            plans.Add(resolved.Id, new LoadedPlan(resolved, providers.ToDictionary(p => p, p => modules[p], StringComparer.Ordinal)));
            loadedAny = true;
            LogPlanLoaded(file.Path, identityPlan, resolved.Permissions);
            outcomes.Add(new PlanLoadOutcome(file.Path, true, identity.Id, null, null));
        }
        if (loadedAny) RebuildSubscriptions();
        return outcomes;
    }
    private void LogPlanRejected(string path, RuntimeLogPlan? plan, string code, string? detail = null)
    {
        if (logSink == null || !LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Error)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.PlanRejected, Provider = Identity.Id,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, Path = path, Plan = plan, Detail = detail,
            Result = new RuntimeLogResult { Status = "rejected", Commit = null, Reason = code } });
    }
    private void LogPlanLoaded(string path, RuntimeLogPlan plan, IReadOnlyList<string> permissions)
    {
        if (logSink == null || !LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Info, Code = RuntimeLogCodes.PlanLoaded, Provider = Identity.Id,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, Path = path, Plan = plan, Permissions = permissions });
    }
    public bool UnloadPlan(string planId)
    { Mutable(); var removed = plans.Remove(planId); if (removed) RebuildSubscriptions(); return removed; }
    private void RebuildSubscriptions()
    {
        subscriptions.Clear();
        foreach (var plan in plans.Values.OrderBy(p => p.Plan.Id, StringComparer.Ordinal))
            foreach (var entry in plan.Plan.Entries)
            {
                if (!subscriptions.TryGetValue(entry.BindingId, out var work)) subscriptions.Add(entry.BindingId, work = new());
                work.Add(new Work(plan, entry));
            }
    }
    public bool HasSubscribers(string bindingId) { ReadThread(); return subscriptions.ContainsKey(bindingId); }
    public void BeginWorld(long worldEpoch)
    {
        Mutable(); AcceptRuntimeWork(true); RuntimeJson.Integer(worldEpoch);
        RuntimeJson.Require(!worldStarted || worldEpoch > WorldEpoch, "world-epoch", "A new world must advance its epoch.");
        long? previousWorldEpoch = worldStarted ? WorldEpoch : null;
        StopScheduledSource(null, null, "world-ended"); StopStateSource(null, null, "world-ended");
        WorldEpoch = worldEpoch; worldStarted = true; CurrentTick = -1; worldHost = null; scheduledThisTick = 0; deferredLogged = false;
        queue.Clear(); history.Clear(); cancelled.Clear(); planTickUsage.Clear(); eventsThisTick = commandsThisTick = 0;
        LogWorldBegan();
        NotifyLifecycle(RuntimeLifecycleKind.WorldChanged, previousWorldEpoch);
    }
    internal int CancelScope(RuntimeModuleHandle handle, string scopeId)
    {
        Thread(); RuntimeJson.Require(handle.IsRegistered, "module-unregistered", handle.ProviderId);
        RuntimeJson.Text(scopeId);
        RuntimeJson.Require(cancelled.Count < MaximumEventHistory || cancelled.Contains((handle.ProviderId, scopeId)), "scope-budget", scopeId);
        cancelled.Add((handle.ProviderId, scopeId));
        var affected = queue.UnorderedItems.Count(x => x.Element.Provider == handle.ProviderId && x.Element.Event.ScopeId == scopeId);
        StopScheduledSource(handle.ProviderId, scopeId, "scope-cancelled"); StopStateSource(handle.ProviderId, scopeId, "scope-cancelled");
        return affected;
    }
    internal DispatchResult Publish(RuntimeModuleHandle handle, RuntimeEvent value)
    {
        ReadThread();
        try
        {
            AcceptRuntimeWork();
            RuntimeJson.Require(handle.IsRegistered, "module-unregistered", handle.ProviderId);
            RuntimeJson.Require(registry.Bindings.TryGetValue(value.BindingId, out var binding) && RuntimeJson.Text(binding, "providerId") == handle.ProviderId, "binding-owner", value.BindingId);
            if (!subscriptions.TryGetValue(value.BindingId, out var registeredWork)) return new DispatchResult("ignored", "no-consumer", value.EventId);
            RuntimeJson.Require(worldStarted && value.WorldEpoch == WorldEpoch, "stale-world", value.EventId);
            RuntimeJson.Text(value.EventId); RuntimeJson.Text(value.ScopeId);
            RuntimeJson.Integer(value.WorldEpoch); RuntimeJson.Integer(value.SimulationTick);
            RuntimeJson.Require(!cancelled.Contains((handle.ProviderId, value.ScopeId)), "scope-cancelled", value.ScopeId);
            RuntimeJson.Require(worldHost != false, "not-host", "This world is not authoritative.");
            var payloadText = value.Outputs.GetRawText();
            RuntimeJson.Require(Encoding.UTF8.GetByteCount(payloadText) <= MaximumEventPayloadBytes, "event-payload-budget", value.EventId);
            var snapshot = value with { Outputs = RuntimeJson.Parse(payloadText) };
            if (currentCommand != null) snapshot = snapshot with {
                CauseId = currentCommand.CommandId, RootEventId = currentCommand.RootEventId, CausalDepth = currentCommand.CausalDepth + 1
            };
            else RuntimeJson.Require(snapshot.CausalDepth == 0 && snapshot.CauseId == null && (snapshot.RootEventId == null || snapshot.RootEventId == snapshot.EventId), "external-causality", "Root hooks cannot invent an internal cause.");
            RuntimeJson.Require(snapshot.CausalDepth >= 0 && snapshot.CausalDepth <= Limits.MaxCausalDepth, "causal-depth", value.EventId);
            var capability = registry.Capabilities[RuntimeJson.Text(binding, "capabilityId")];
            ValidateEvent(snapshot, capability);
            var key = handle.ProviderId + "\0" + value.EventId;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RuntimeJson.StableText(RuntimeJson.From(snapshot)))));
            if (history.TryGetValue(key, out var previous))
            {
                if (previous == fingerprint) return new DispatchResult("duplicate", "duplicate-event", value.EventId);
                LogEventRejected(value.BindingId, value.EventId, "event-id-conflict", handle.ProviderId);
                return new DispatchResult("rejected", "event-id-conflict", value.EventId);
            }
            var work = registeredWork.ToArray();
            foreach (var group in work.GroupBy(w => w.Plan))
            {
                var limit = group.Key.Plan.Limits;
                RuntimeJson.Require(snapshot.CausalDepth <= limit.MaxCausalDepth, "causal-depth", group.Key.Plan.Id);
                RuntimeJson.Require(group.Sum(w => w.Entry.DispatchableStepCount) <= limit.MaxCommandsPerTick, "event-command-budget", group.Key.Plan.Id);
                RuntimeJson.Require(queue.UnorderedItems.Count(x => x.Element.Work.Any(w => ReferenceEquals(w.Plan, group.Key))) < limit.MaxQueuedEvents, "plan-queue-budget", group.Key.Plan.Id);
            }
            RuntimeJson.Require(work.Sum(w => w.Entry.DispatchableStepCount) <= Limits.MaxCommandsPerTick, "event-command-budget", value.EventId);
            RuntimeJson.Require(history.Count < MaximumEventHistory, "event-history-budget", "This world exhausted its replay ledger; old IDs are never evicted and replayed.");
            RuntimeJson.Require(queue.Count < Limits.MaxQueuedEvents, "queue-budget", value.EventId);
            history.Add(key, fingerprint);
            queue.Enqueue(new Pending(handle.ProviderId, handle.Generation, snapshot, work), (snapshot.SimulationTick, ++sequence));
            LogTriggerFired(RuntimeJson.Text(binding, "providerId"), snapshot.BindingId, snapshot.EventId,
                snapshot.CauseId, snapshot.RootEventId ?? snapshot.EventId);
            return new DispatchResult("queued", "accepted", value.EventId);
        }
        // A refusal is recorded once: a budget reason is what the code table calls budget.exceeded, everything else is
        // event.rejected. The event's binding may already be unregistered, so the raw id is reported as it arrived.
        catch (RuntimeContractException ex)
        {
            if (ex.Code.EndsWith("-budget", StringComparison.Ordinal)) LogBudgetExceeded(value.BindingId, value.EventId, ex.Code);
            else LogEventRejected(value.BindingId, value.EventId, ex.Code, handle.ProviderId);
            return new DispatchResult("rejected", ex.Code, value.EventId);
        }
        catch (ObjectDisposedException)
        {
            LogEventRejected(value.BindingId, value.EventId, "disposed-payload", handle.ProviderId);
            return new DispatchResult("rejected", "disposed-payload", value.EventId);
        }
    }
    private void ValidateEvent(RuntimeEvent value, JsonElement capability)
    {
        RuntimeJson.Require(RuntimeJson.Text(capability, "kind") == "trigger", "not-trigger", value.BindingId);
        var outputs = RuntimeJson.Rows(capability.GetProperty("graph"), "outputs").Where(p => RuntimeJson.Text(p, "type") != "execution").ToArray();
        RuntimeJson.Shape(value.Outputs, string.Join(" ", outputs.Where(p => !RuntimeJson.Flag(p, "optional")).Select(p => RuntimeJson.Text(p, "id"))), string.Join(" ", outputs.Where(p => RuntimeJson.Flag(p, "optional")).Select(p => RuntimeJson.Text(p, "id"))));
        foreach (var port in outputs) if (value.Outputs.TryGetProperty(RuntimeJson.Text(port, "id"), out var data)) { RuntimeJson.ValidateValue(data, port); ValidateEntities(data, port); }
        if (value.Source != null) CheckEntity(value.Source);
    }
    private void CheckEntity(EntityReference entity)
    {
        RuntimeJson.Text(entity.Id); RuntimeJson.Integer(entity.WorldEpoch); RuntimeJson.Integer(entity.LifeEpoch);
        RuntimeJson.Require(entity.WorldEpoch == WorldEpoch, "stale-world", entity.Id);
        var split = entity.Id.IndexOf(':');
        RuntimeJson.Require(split > 0 && registry.Resolvers.TryGetValue(entity.Id[..split], out _), "entity-resolver", entity.Id);
        bool valid;
        try { valid = registry.Resolvers[entity.Id[..split]].Resolve(entity); }
        catch (Exception) { throw new RuntimeContractException("entity-resolver-failed", entity.Id); }
        RuntimeJson.Require(valid, "stale-entity", entity.Id);
    }
    private void ValidateEntities(JsonElement value, JsonElement port)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (RuntimeJson.Text(port, "type") != "entity") return;
        if (!RuntimeGraphContracts.Many(port)) CheckEntity(RuntimeJson.Entity(value));
        else foreach (var item in value.EnumerateArray()) CheckEntity(RuntimeJson.Entity(item));
    }
    private static CommandResult PreInvocationFailure(RuntimeContractException error)
    {
        var code = CommandResult.TruncateCode(error.Code);
        var detail = CommandResult.TruncateDetail(error.Message);
        return code is "scope-cancelled" or "schedule-cancelled" or "binding-lifecycle" or "stale-entity" or "stale-world"
            ? CommandResult.Cancelled(code, detail)
            : CommandResult.Rejected(code, detail);
    }
    private static CommandResult NormalizeInvokedResult(CommandResult? result)
    {
        if (result == null) return CommandResult.FailedUnknown("null-result", "Handler returned null after invocation.");
        if (CommandResultRules.TryValidate(result, out var violation)) return result;
        var detail = CommandResult.TruncateDetail($"Invalid handler result ({violation}): status={result.Status}, commitState={result.CommitState}.");
        return CommandResult.FailedUnknown(result.Outputs, "invalid-handler-result", detail, result.Facts.ToArray());
    }
    private void PublishConfirmedFacts(CommandResult result, string stepBindingId, Pending pending, long simulationTick, List<EventReceipt> events)
    {
        var owner = RuntimeJson.Text(registry.Bindings[stepBindingId], "providerId");
        if (!modules.TryGetValue(owner, out var moduleGeneration))
        {
            for (var i = 0; i < result.Facts.Count; i++)
            {
                var refused = "fact:" + WorldEpoch + ":" + (++sequence);
                LogEventRejected(result.Facts[i].BindingId, refused, "module-unregistered", owner);
                events.Add(new EventReceipt(refused, "rejected", "module-unregistered"));
            }
            return;
        }
        var handle = new RuntimeModuleHandle(this, owner, moduleGeneration);
        for (var i = 0; i < result.Facts.Count; i++)
        {
            var fact = result.Facts[i];
            var factId = "fact:" + WorldEpoch + ":" + (++sequence);
            try
            {
                var dispatch = Publish(handle, new RuntimeEvent(factId, fact.BindingId, WorldEpoch, simulationTick, pending.Event.ScopeId, fact.Outputs, pending.Event.Source));
                if (dispatch.Status == "rejected") events.Add(new EventReceipt(factId, dispatch.Status, dispatch.Code));
            }
            catch (Exception error)
            {
                var code = error is RuntimeContractException contract ? CommandResult.TruncateCode(contract.Code) : "fact-publish-failed";
                LogEventRejected(fact.BindingId, factId, code);
                events.Add(new EventReceipt(factId, "rejected", code));
            }
        }
    }
    private TickResult AdvanceCore(long simulationTick, bool isHost)
    {
        Thread(); RuntimeJson.Require(!advancing && worldStarted, "dispatch-lifecycle", "World must be started and dispatch cannot be recursive.");
        RuntimeJson.Integer(simulationTick); RuntimeJson.Require(simulationTick >= CurrentTick, "time-reversal", "Simulation time cannot go backwards.");
        RuntimeJson.Require(worldHost != false || !isHost, "host-migration-unsupported", "Authority cannot migrate inside the current world.");
        if (simulationTick > CurrentTick) { CurrentTick = simulationTick; eventsThisTick = commandsThisTick = scheduledThisTick = 0; deferredLogged = false; planTickUsage.Clear(); }
        worldHost = isHost;
        var commands = new List<CommandReceipt>(); var events = new List<EventReceipt>(); var processed = 0; var executed = 0;
        var scheduleReports = new List<ScheduleReceipt>(); var leaseReports = new List<StateLeaseReceipt>();
        advancing = true;
        try
        {
            if (!isHost)
            {
                foreach (var job in schedules.Values) scheduleReports.Add(ScheduleReport(job, "cancelled", "not-host"));
                foreach (var lease in stateLeases.Values) leaseReports.Add(new StateLeaseReceipt(lease.Handle.ProviderId, lease.Handle.LeaseId, lease.Key.Definition, lease.Key.Target, "cancelled", "not-host"));
                StopScheduledSource(null, null, "not-host"); StopStateSource(null, null, "not-host");
                // A non-host advance clears the queue, and every dropped event is recorded once.
                while (queue.TryDequeue(out var stale, out _))
                {
                    LogEventRejected(stale.Event.BindingId, stale.Event.EventId, "not-host", stale.Provider);
                    events.Add(new EventReceipt(stale.Event.EventId, "rejected", "not-host"));
                }
                return new TickResult(0, 0, 0, commands, events) { Schedules = scheduleReports.AsReadOnly(), StateLeases = leaseReports.AsReadOnly() };
            }
            CleanScheduledLifetimes(scheduleReports); CleanStateLifetimes(leaseReports);
            while (queue.TryPeek(out var pending, out var priority) && priority.Tick <= simulationTick)
            {
                if (pending.Schedule != null && !PreparePulse(pending, scheduleReports)) continue;
                if (pending.Schedule != null && scheduledThisTick >= MaximumScheduledPulsesPerTick)
                {
                    scheduleReports.Add(ScheduleReport(pending.Schedule, "deferred", "scheduled-tick-budget"));
                    if (!deferredLogged)
                    {
                        deferredLogged = true;
                        LogEventDeferred(pending.Event.BindingId, pending.Event.EventId, "scheduled-tick-budget");
                    }
                    break;
                }
                var budget = BudgetRefusal(pending);
                if (budget != null)
                {
                    if (pending.Schedule != null) scheduleReports.Add(ScheduleReport(pending.Schedule, "deferred", budget));
                    if (!deferredLogged)
                    {
                        deferredLogged = true;
                        LogEventDeferred(pending.Event.BindingId, pending.Event.EventId, budget);
                    }
                    break;
                }
                queue.Dequeue();
                if (!IsRegistered(pending.Provider, pending.Generation))
                {
                    if (pending.Schedule != null) EndSchedule(pending.Schedule, "cancelled", "source-lifecycle");
                    LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "source-lifecycle", pending.Provider);
                    events.Add(new EventReceipt(pending.Event.EventId, "cancelled", "source-lifecycle")); continue;
                }
                var scopeCancelled = cancelled.Contains((pending.Provider, pending.Event.ScopeId));
                if (scopeCancelled)
                {
                    if (pending.Schedule != null) EndSchedule(pending.Schedule, "cancelled", "source-lifecycle");
                    LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "source-lifecycle", pending.Provider);
                    events.Add(new EventReceipt(pending.Event.EventId, "cancelled", "source-lifecycle")); continue;
                }
                var active = pending.Work.Where(w => plans.TryGetValue(w.Plan.Plan.Id, out var live) && ReferenceEquals(live, w.Plan)).ToArray();
                var count = active.Sum(w => w.Entry.DispatchableStepCount);
                if (active.Length == 0)
                {
                    if (pending.Schedule != null) EndSchedule(pending.Schedule, "cancelled", "plan-unloaded");
                    LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "plan-unloaded", pending.Provider);
                    events.Add(new EventReceipt(pending.Event.EventId, "cancelled", "plan-unloaded")); continue;
                }
                var groups = active.GroupBy(w => w.Plan).ToArray();
                if (pending.Schedule != null && !AdmitPulse(pending, scheduleReports))
                {
                    // The refused pulse was already dequeued and its schedule ended; leaving it out of the receipts would
                    // hide a refused event from the tick result.
                    events.Add(new EventReceipt(pending.Event.EventId, "rejected", scheduleReports[^1].Code));
                    continue;
                }
                eventsThisTick++; commandsThisTick += count; processed++;
                foreach (var group in groups) { planTickUsage.TryGetValue(group.Key.Plan.Id, out var used); planTickUsage[group.Key.Plan.Id] = (used.Events + 1, used.Commands + group.Sum(w => w.Entry.DispatchableStepCount)); }
                foreach (var item in active)
                {
                    var stepPlan = new RuntimeLogPlan { PlanId = item.Plan.Plan.Id, ResourceId = item.Plan.Plan.ResourceId, ResourceRevision = item.Plan.Plan.ResourceRevision };
                    var stepOrigin = new StepOrigin(pending.Event, in stepPlan, item.Entry.NodeId);
                    // D-017 R4-a: `pure` steps are never dispatched directly; they are read on demand by whichever
                    // action/control step's input names them via `fromStepSlot`, and memoized at most once per
                    // dispatch of this entry (cleared with `pureCache` on every new event this entry consumes).
                    var pureCache = new Dictionary<int, JsonElement>();
                    int? cursor = item.Entry.Start;
                    while (cursor is int stepIndex)
                    {
                        var step = item.Entry.Steps[stepIndex];
                        var commandId = RuntimeJson.StableText(RuntimeJson.From(new[] { pending.Provider, pending.Event.EventId, item.Plan.Plan.Id, step.NodeId }));
                        CommandResult? result = null; var invoked = false; int? next = null; var stopped = false;
                        // `step.*` belongs to the provider of the binding that executes the step, which is not the entry's
                        // trigger binding: a trigger may route into another package's action.
                        var stepProvider = BindingProvider(step.BindingId);
                        try
                        {
                            RuntimeJson.Require(item.Plan.Modules.All(m => IsRegistered(m.Key, m.Value)), "binding-lifecycle", step.BindingId);
                            RuntimeJson.Require(pending.Schedule == null || pending.Schedule.Handle.Status == "active", "schedule-cancelled", pending.Event.EventId);
                            if (pending.Event.Source != null) CheckEntity(pending.Event.Source);
                            RuntimeJson.Require(!cancelled.Contains((pending.Provider, pending.Event.ScopeId)), "scope-cancelled", pending.Event.ScopeId);
                            var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                            var merged = step.Promoted.Count == 0 ? null : step.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                            foreach (var input in step.Inputs)
                            {
                                JsonElement value;
                                if (input.Literal is { } literal) value = literal;
                                else if (input.FromStep is { } from)
                                {
                                    var frame = EvaluatePure(item.Entry, from.Step, pureCache, pending);
                                    value = frame.GetProperty(RuntimeJson.Text(input.Port, "id"));
                                    // D-006②: a non-nullable "one" output wired into a "many" input was validated above
                                    // against its own (origin) cardinality; wrap it into the one-element collection the
                                    // "many" input expects only now, after that validation has passed.
                                    if (input.Wrap) value = RuntimeJson.From(new[] { value });
                                }
                                else
                                {
                                    if (!pending.Event.Outputs.TryGetProperty(input.EventPort!, out value)) continue;
                                    RuntimeJson.ValidateValue(value, input.Port); ValidateEntities(value, input.Port);
                                    if (input.Wrap) value = RuntimeJson.From(new[] { value });
                                }
                                // A promoted value stays a compiled index until the re-validation below runs on indices
                                // throughout; a genuine action input has no further index-based check, so it resolves now.
                                if (merged != null && step.Promoted.Contains(input.Name)) merged.Add(input.Name, value);
                                else inputs.Add(input.Name, RuntimeJson.EnumPortToHandlerValue(value, input.Port));
                            }
                            var capability = registry.Capabilities[RuntimeJson.Text(registry.Bindings[step.BindingId], "capabilityId")];
                            var parameters = step.Parameters;
                            if (merged != null)
                            {
                                // A computed value meets the bounds and members of the literal it replaced: rejected, never clamped.
                                parameters = RuntimeJson.From(merged);
                                RuntimeJson.Parameters(parameters, capability);
                            }
                            parameters = RuntimeJson.ResolveEnumParameters(parameters, capability);
                            if (step.NodeKind == "control")
                            {
                                // Branch routing is internal to the kernel (D-017 R4-a): no handler lookup, no
                                // command receipt — only `action` steps ever produce one.
                                var condition = inputs.TryGetValue("condition", out var conditionValue) && conditionValue.ValueKind == JsonValueKind.True;
                                cursor = condition ? step.Successors[0] : step.Successors[1];
                                goto continueWalk;
                            }
                            var handler = registry.Handlers[step.BindingId];
                            LogStepStarted(in stepOrigin, stepProvider, step.NodeId, step.BindingId, commandId);
                            currentCommand = new CommandContext(pending.Event, simulationTick, commandId, item.Plan.Plan.Id, item.Plan.Plan.ResourceId, item.Plan.Plan.ResourceRevision, step.NodeId, parameters, RuntimeJson.From(inputs));
                            executed++; invoked = true;
                            result = NormalizeInvokedResult(handler(currentCommand));
                            if (result.Facts.Count > 0) PublishConfirmedFacts(result, step.BindingId, pending, simulationTick, events);
                        }
                        catch (RuntimeContractException ex) when (!invoked)
                        { result = PreInvocationFailure(ex); }
                        catch (Exception ex) when (!invoked)
                        { result = CommandResult.Rejected("precondition-failed", CommandResult.TruncateDetail(ex.GetType().Name + ": " + ex.Message)); }
                        catch (RuntimeContractException ex)
                        { result = CommandResult.FailedUnknown("handler-exception", CommandResult.TruncateDetail(ex.GetType().Name + ": " + ex.Message)); }
                        catch (Exception ex)
                        { result = CommandResult.FailedUnknown("handler-exception", CommandResult.TruncateDetail(ex.GetType().Name + ": " + ex.Message)); }
                        finally { currentCommand = null; }
                        var commandResult = result ?? (invoked
                            ? CommandResult.FailedUnknown("null-result", "Handler returned null after invocation.")
                            : CommandResult.Rejected("precondition-failed", "Precondition failed before handler invocation."));
                        commands.Add(new CommandReceipt(commandId, pending.Event.EventId, pending.Event.CauseId, pending.Event.RootEventId ?? pending.Event.EventId, item.Plan.Plan.Id, item.Plan.Plan.ResourceId, item.Plan.Plan.ResourceRevision, step.NodeId, step.BindingId, WorldEpoch, simulationTick, commandResult));
                        // r11: entrypoint execution only stops on rejected/failed/cancelled/expired, or on a
                        // partial result with an unknown commit state; a confirmed-commit partial continues.
                        if (commandResult.Status != CommandStatuses.Succeeded
                            && !(commandResult.Status == CommandStatuses.Partial && commandResult.CommitState == CommitStates.Confirmed))
                        { cursor = null; stopped = true; }
                        else next = step.Successors.Count > 0 ? step.Successors[0] : null;
                        LogStepFinished(in stepOrigin, stepProvider, step.NodeId, step.BindingId, commandId, in commandResult);
                        // The stop shares the code table's level line with step.finished, and is written only when the entry
                        // really had a step left: the last step of an entry stops nothing.
                        if (stopped && step.Successors.Count > 0) LogEntryStopped(in stepOrigin, step.NodeId, commandId);
                        cursor = next;
                        continueWalk: ;
                    }
                }
                if (pending.Schedule != null && pending.Schedule.Handle.Status == "active" && pending.Schedule.Index >= pending.Schedule.TotalPulses)
                    EndSchedule(pending.Schedule, "completed", "all-pulses-dispatched");
                events.Add(new EventReceipt(pending.Event.EventId, "processed", "dispatched"));
            }
        }
        finally { currentCommand = null; advancing = false; }
        return new TickResult(processed, executed, queue.Count, commands, events) { Schedules = scheduleReports.AsReadOnly(), StateLeases = leaseReports.AsReadOnly() };
    }
    /// <summary>The budget reason that keeps the head event queued for the next tick, or null when it can dispatch now,
    /// in the order the dispatch loop has always evaluated them (global event, global command, then per plan). The first
    /// refused condition is the reason, so the code names what actually stopped it; the caller writes at most one
    /// `event.deferred` per tick.</summary>
    private string? BudgetRefusal(Pending pending)
    {
        var groups = pending.Work.Where(w => plans.TryGetValue(w.Plan.Plan.Id, out var live) && ReferenceEquals(live, w.Plan))
            .GroupBy(w => w.Plan).ToArray();
        var count = groups.Sum(g => g.Sum(w => w.Entry.DispatchableStepCount));
        if (eventsThisTick >= Limits.MaxEventsPerTick) return "tick-event-budget";
        if (commandsThisTick + count > Limits.MaxCommandsPerTick) return "tick-command-budget";
        foreach (var group in groups)
        {
            planTickUsage.TryGetValue(group.Key.Plan.Id, out var used);
            if (used.Events >= group.Key.Plan.Limits.MaxEventsPerTick) return "plan-tick-event-budget";
            if (used.Commands + group.Sum(w => w.Entry.DispatchableStepCount) > group.Key.Plan.Limits.MaxCommandsPerTick) return "plan-tick-command-budget";
        }
        return null;
    }
    /// <summary>D-017 R4-a: evaluates a `pure` step's frame (an object keyed by its own output port ids) on demand,
    /// memoized in <paramref name="cache"/> at most once per dispatch of <paramref name="entry"/>. Any failure —
    /// while resolving this step's own inputs, or thrown by the evaluator itself — is wrapped as the single outer
    /// code `pure-evaluation-failed` carrying the original code/message, except a nested `fromStepSlot` read of an
    /// already-failing upstream pure step, which propagates as-is rather than being wrapped a second time.</summary>
    private JsonElement EvaluatePure(ResolvedEntry entry, int stepIndex, Dictionary<int, JsonElement> cache, Pending pending)
    {
        if (cache.TryGetValue(stepIndex, out var cached)) return cached;
        var step = entry.Steps[stepIndex];
        var upstream = new Dictionary<int, JsonElement>();
        foreach (var input in step.Inputs)
            if (input.FromStep is { } from && !upstream.ContainsKey(from.Step))
                upstream[from.Step] = EvaluatePure(entry, from.Step, cache, pending);
        JsonElement frame;
        try
        {
            var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var merged = step.Promoted.Count == 0 ? null : step.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            foreach (var input in step.Inputs)
            {
                JsonElement value;
                if (input.Literal is { } literal) value = literal;
                else if (input.FromStep is { } from)
                {
                    value = upstream[from.Step].GetProperty(RuntimeJson.Text(input.Port, "id"));
                    if (input.Wrap) value = RuntimeJson.From(new[] { value });
                }
                else
                {
                    if (!pending.Event.Outputs.TryGetProperty(input.EventPort!, out value)) continue;
                    RuntimeJson.ValidateValue(value, input.Port); ValidateEntities(value, input.Port);
                    if (input.Wrap) value = RuntimeJson.From(new[] { value });
                }
                if (merged != null && step.Promoted.Contains(input.Name)) merged.Add(input.Name, value);
                else inputs.Add(input.Name, RuntimeJson.EnumPortToHandlerValue(value, input.Port));
            }
            var capability = registry.Capabilities[RuntimeJson.Text(registry.Bindings[step.BindingId], "capabilityId")];
            var parameters = step.Parameters;
            if (merged != null) { parameters = RuntimeJson.From(merged); RuntimeJson.Parameters(parameters, capability); }
            parameters = RuntimeJson.ResolveEnumParameters(parameters, capability);
            var evaluator = registry.Evaluators[step.BindingId];
            var result = evaluator(new EvaluationContext(step.NodeId, parameters, RuntimeJson.From(inputs)));
            var outputs = RuntimeJson.Rows(step.Contract, "outputs");
            RuntimeJson.Shape(result, string.Join(" ", outputs.Select(p => RuntimeJson.Text(p, "id"))));
            foreach (var port in outputs)
            {
                var value = result.GetProperty(RuntimeJson.Text(port, "id"));
                RuntimeJson.ValidateValue(value, port); ValidateEntities(value, port);
            }
            frame = result;
        }
        catch (RuntimeContractException ex) { throw new RuntimeContractException("pure-evaluation-failed", ex.Code + ": " + ex.Message); }
        catch (Exception ex) { throw new RuntimeContractException("pure-evaluation-failed", ex.GetType().Name + ": " + ex.Message); }
        cache[stepIndex] = frame;
        return frame;
    }
}
