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
    /// <summary>The gate of one of this module's own bindings. Resolved once — at registration, where the binding is
    /// already a fact of this module — and then read as one boolean before an event value is built.</summary>
    public RuntimeSubscriptionGate SubscriptionGate(string bindingId) => kernel.SubscriptionGate(ProviderId, bindingId);
    /// <summary>Every gate of every binding this module declares, resolved once here — the moment its bindings are
    /// already facts of the registry — so a publisher that writes to several bindings of its own keeps one set
    /// instead of re-asking the kernel per publish.</summary>
    public IReadOnlyDictionary<string, RuntimeSubscriptionGate> SubscriptionGates() => kernel.SubscriptionGates(ProviderId);
    public void Dispose() => kernel.Unregister(this);
}

/// <summary>
/// Whether any loaded plan is mounted on one binding. The answer is precomputed: it changes only when plans or
/// modules change, and the kernel refreshes it in the same place it rebuilds the subscriptions. A publisher that
/// holds its own gate therefore answers "is anyone listening" with one field read, before it builds the event value
/// and before the kernel is reached at all.
/// </summary>
public sealed class RuntimeSubscriptionGate
{
    internal RuntimeSubscriptionGate(string bindingId) { BindingId = bindingId; }
    public string BindingId { get; }
    public bool HasSubscribers { get; internal set; }
}

/// <summary>Single-thread simulation dispatcher. Registration and plans are resolved once; no Unity/game types are owned here.</summary>
public sealed partial class RuntimeKernel
{
    /// <summary>1.0.0 is the release wire of the Forge Standard; it moves together with the website.</summary>
    public const string ApiVersion = "1.0.0";
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
    private readonly Dictionary<string, RuntimeSubscriptionGate> subscriptionGates = new(StringComparer.Ordinal);
    private long generation, sequence;
    /// <summary>Every registration or lifecycle change that can invalidate a decision already taken: a module
    /// registering or unregistering, a plan loading or unloading, a world beginning. Nothing derived from those
    /// tables — a schedule's captured validation, a queued dispatch's plan grouping — is recomputed while this
    /// number stands still, and everything derived from them that survives across frames records the generation it
    /// was derived at.</summary>
    private long lifecycleGeneration;
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
    /// <summary>One plan waiting on one event. <see cref="Subjects"/> is what the plan's own mount targets accepted
    /// for that event — the entities its steps read `self` from — and travels with the item so no step re-matches:
    /// the queue carries the decision the dispatch already made.</summary>
    private sealed record Work(LoadedPlan Plan, ResolvedEntry Entry, IReadOnlyList<EntityReference> Subjects);
    /// <summary>One queued dispatch: the event, the work it claimed, and the event rows it will carry. Row 0 of
    /// <see cref="Rows"/> is this event and every event its own handlers publish is appended while it is walked,
    /// so a row index means something for exactly as long as the dispatch that handed it out. <see cref="Sequence"/>
    /// is the publish order the event reached the queue in, which is the order its own envelope reports.</summary>
    private sealed record Pending(string Provider, long Generation, RuntimeEvent Event, IReadOnlyList<Work> Work,
        ScheduledJob? Schedule = null, long Sequence = 0, RuntimeEventRows? Rows = null,
        WaitJob? Wait = null, bool WaitTimeout = false)
    {
        /// <summary>This dispatch's work grouped by plan, and how many commands it can produce, computed the first
        /// time a budget question is asked and kept for the rest of the item's life. <see cref="PlanUsageGeneration"/>
        /// is the lifecycle generation it was computed at: a plan that unloaded since then makes the grouping stale,
        /// and it is recomputed rather than trusted.</summary>
        internal IReadOnlyList<PendingPlan>? PlanUsage;
        internal long PlanUsageGeneration = -1;
        /// <summary>The first half of every step's command identity: the publisher and the event, quoted by the
        /// JSON encoder, so a step appends its own precomputed half instead of serializing a four-element array.</summary>
        internal string? CommandPrefix;
    }
    /// <summary>One plan's share of a queued dispatch: the plan and the commands its claimed entries can produce.</summary>
    private sealed record PendingPlan(LoadedPlan Plan, int Steps);
    /// <summary>One entity reference and the resolver that answered for its kind, kept for as long as the value that
    /// carried it is still being read — one activation for a frame, the life of the job for a captured template.</summary>
    private readonly record struct EntityCheck(EntityReference Entity, Func<EntityReference, bool> Resolve);

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
    /// <summary>I-DIAG. The level is the registering package's own cfg value and is required: a provider has no level
    /// until its package hands one over, and the level table never invents a default for it. One package registering more
    /// than one provider passes the same level for each. The Runtime's own built-in providers do not come through here.</summary>
    public RuntimeModuleHandle RegisterModule(RuntimeModule module, RuntimeLogLevel level)
    {
        RuntimeJson.Require(level is RuntimeLogLevel.Off or RuntimeLogLevel.Error or RuntimeLogLevel.Info, "log-level",
            "Configured log levels are off, error or info; trace is only reachable through elevation.");
        return Register(module, level);
    }
    /// <summary>The Runtime's own built-in providers (CombatContracts, ControlContracts) ship no package cfg, so they
    /// take the Runtime provider's level. Host assembly only: a domain package must hand over its own.</summary>
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
        foreach (var id in ownedBindings) { registry.Bindings.Remove(id); registry.Handlers.Remove(id); registry.Evaluators.Remove(id); registry.Support.Remove(id); registry.Shapes.Remove(id); }
        foreach (var id in ownedCaps) { registry.Capabilities.Remove(id); registry.CapabilityRegistrants.Remove(id); }
        foreach (var key in registry.Resolvers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray()) registry.Resolvers.Remove(key);
        foreach (var key in registry.EntityObservers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.EntityObservers.Remove(key);
        foreach (var key in registry.EntityInstanceResolvers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.EntityInstanceResolvers.Remove(key);
        // A candidate source is claimed per kind and one kind has exactly one owner, so the kinds go with the
        // provider: a leftover would refuse the replacement provider's claim as a conflict and would keep handing
        // a query step entities the kernel no longer has any provider for.
        foreach (var key in registry.EntityCandidates.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.EntityCandidates.Remove(key);
        // A zone responder goes with the resolver it was registered beside: a leftover would keep answering where
        // a kind's entities stand for a package that no longer owns the kind.
        foreach (var key in registry.EntityZones.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.EntityZones.Remove(key);
        // A mount matcher is claimed per kind and one kind has exactly one owner, so a provider that unregisters
        // has to take its own kinds with it: a leftover would refuse the replacement provider's claim as a
        // conflict and would keep matching mounts for a module that is gone.
        foreach (var key in registry.AttachmentMatchers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.AttachmentMatchers.Remove(key);
        // Resource kinds go with their owner for the same reason: every read of a kind is answered by the package
        // that knows it, and a kind left behind would answer for a provider that is gone.
        foreach (var key in registry.ResourceProviders.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.ResourceProviders.Remove(key);
        // A native-object lookup is the package's own reading of its own native types, so it goes with it. The
        // handles that package cast are dropped here too: a handle whose owner is gone stands for nothing.
        registry.ObjectEntityResolvers.RemoveAll(entry => entry.Owner == provider);
        // A presentation session list is the provider's own answer about the running process, so it goes with the
        // provider: a leftover would keep presenting steps to sessions nothing owns any more.
        foreach (var key in registry.PresentationSessions.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.PresentationSessions.Remove(key);
        // The owner-session answers are the same kind of fact and go the same way: a leftover would keep routing
        // owner steps to a holder nothing owns any more.
        foreach (var key in registry.OwnerSessions.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.OwnerSessions.Remove(key);
        for (var slot = 0; slot < handleSlots.Count; slot++)
            if (handleSlots[slot] is { Active: true } entry && entry.ProviderId == provider) ReleaseHandle(slot);
        foreach (var id in plans.Where(x => x.Value.Modules.ContainsKey(provider)).Select(x => x.Key).ToArray()) plans.Remove(id);
        UnregisterLogGate(provider);
        lifecycleGeneration++;
        RebuildSubscriptions();
    }
    public string ExportManifest()
    {
        ReadThread(); return RuntimeJson.StableText(RuntimeJson.From(new {
            schemaVersion = 1, runtime = Identity, registry = registry.Snapshot(),
            bindingSupport = registry.Support.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray(), limits = Limits
        }));
    }
    /// <summary>I-PACK batch load. Every file gets its own outcome and its own plan.loaded/plan.rejected record;
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
            try { resolved = RuntimePlan.Parse(file.Json!, Identity, Limits, registry, this); DeclareVariables(resolved); }
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
    { Mutable(); var removed = plans.Remove(planId); if (removed) { ForgetVariables(planId); lifecycleGeneration++; RebuildSubscriptions(); } return removed; }
    private void RebuildSubscriptions()
    {
        subscriptions.Clear();
        foreach (var plan in plans.Values.OrderBy(p => p.Plan.Id, StringComparer.Ordinal))
            foreach (var entry in plan.Plan.Entries)
            {
                if (!subscriptions.TryGetValue(entry.BindingId, out var work)) subscriptions.Add(entry.BindingId, work = new());
                // The template item names no subject: which entities a dispatch is about is decided by the mount
                // comparison of that dispatch, and the claimed copy carries the answer.
                work.Add(new Work(plan, entry, Array.Empty<EntityReference>()));
            }
        // The gates are refreshed here and nowhere else, because this is the one place the subscription table is
        // rebuilt: a publisher's own boolean changes exactly when the set of plans mounted on its binding does.
        RefreshSubscriptionGates();
    }
    /// <summary>
    /// Recomputes every gate from the two things that consume a binding: a loaded plan mounted on it, and a
    /// suspended `g-wait` naming it. Both make a publish reach somebody, so the gate is open while either is there,
    /// and it is refreshed wherever one of the two tables changes.
    /// </summary>
    private void RefreshSubscriptionGates()
    {
        foreach (var gate in subscriptionGates.Values)
            gate.HasSubscribers = subscriptions.ContainsKey(gate.BindingId) || waits.ContainsKey(gate.BindingId);
    }
    public bool HasSubscribers(string bindingId) { ReadThread(); return subscriptions.ContainsKey(bindingId); }
    /// <summary>The gate of one binding, resolved by its own provider. The binding must be the caller's: a publisher
    /// asks about its own trigger, and a gate handed to someone else would answer a question that is not theirs.</summary>
    internal RuntimeSubscriptionGate SubscriptionGate(string provider, string bindingId)
    {
        Thread();
        RuntimeJson.Require(registry.Bindings.TryGetValue(bindingId, out var binding) && RuntimeJson.Text(binding, "providerId") == provider, "binding-owner", bindingId);
        if (!subscriptionGates.TryGetValue(bindingId, out var gate))
        {
            gate = new RuntimeSubscriptionGate(bindingId) { HasSubscribers = subscriptions.ContainsKey(bindingId) || waits.ContainsKey(bindingId) };
            subscriptionGates.Add(bindingId, gate);
        }
        return gate;
    }
    /// <summary>The gates of one provider's own bindings, as the module's registration takes them. A publisher
    /// resolves this once and then reads one boolean per publish, so the only thing left between a native callback
    /// and a skipped event is a dictionary lookup.</summary>
    internal IReadOnlyDictionary<string, RuntimeSubscriptionGate> SubscriptionGates(string provider)
    {
        Thread();
        var gates = new Dictionary<string, RuntimeSubscriptionGate>(StringComparer.Ordinal);
        foreach (var entry in registry.Bindings)
            if (RuntimeJson.Text(entry.Value, "providerId") == provider) gates[entry.Key] = SubscriptionGate(provider, entry.Key);
        return gates;
    }
    public void BeginWorld(long worldEpoch)
    {
        Mutable(); AcceptRuntimeWork(true); RuntimeJson.Integer(worldEpoch);
        RuntimeJson.Require(!worldStarted || worldEpoch > WorldEpoch, "world-epoch", "A new world must advance its epoch.");
        long? previousWorldEpoch = worldStarted ? WorldEpoch : null;
        StopScheduledSource(null, null, "world-ended"); StopStateSource(null, null, "world-ended");
        WorldEpoch = worldEpoch; worldStarted = true; CurrentTick = -1; worldHost = null; scheduledThisTick = 0; deferredLogged = false;
        queue.Clear(); history.Clear(); cancelled.Clear(); planTickUsage.Clear(); eventsThisTick = commandsThisTick = 0;
        // A new world invalidates everything derived from the old one: the queue is gone, no plan's capture survives
        // it, and every provider's own answers about entities belong to the epoch that just ended.
        lifecycleGeneration++;
        // Continuations, their schedules and every handle die with the world: a handle names a world epoch, so
        // nothing from the previous one may stay live, and the handle table starts over with it. The faction
        // relations belong to the world the same way, so a new world starts with none until its own providers
        // publish theirs. The parent index is observations of a world too, so it starts empty with them.
        handleSlots.Clear(); controlWalk.Clear(); BeginActivation(); children.Clear();
        // A variable from the previous world names entities that no longer exist, and a suspended `g-wait` waits on
        // an event of that world: values and latches go, the declarations the loaded plans own stay.
        BeginVariableWorld();
        relations = new RuntimeFactionRelations(Array.Empty<RuntimeFactionRelation>());
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
            if (!subscriptions.TryGetValue(value.BindingId, out var registeredWork))
            {
                // A message no plan hangs on still reaches the `g-wait` steps that named it: a waiting flow is a
                // consumer of the message even when no entry is mounted on the receive trigger. What the wake hands
                // over is validated exactly as a dispatch's payload is, so a malformed message wakes nothing.
                if (waits.ContainsKey(value.BindingId))
                {
                    var waited = value with { Outputs = RuntimeJson.Parse(value.Outputs.GetRawText()) };
                    ValidateEvent(waited, registry.Capabilities[RuntimeJson.Text(binding, "capabilityId")]);
                    WakeWaiters(waited);
                }
                return new DispatchResult("ignored", "no-consumer", value.EventId);
            }
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
            var fingerprint = LedgerFingerprint(RuntimeJson.StableText(RuntimeJson.From(snapshot)));
            if (history.TryGetValue(key, out var previous))
            {
                if (previous == fingerprint) return new DispatchResult("duplicate", "duplicate-event", value.EventId);
                LogEventRejected(value.BindingId, value.EventId, "event-id-conflict", handle.ProviderId);
                return new DispatchResult("rejected", "event-id-conflict", value.EventId);
            }
            // A plan only runs for events its mount targets claim: a subject-free kind is answered from the mount
            // target alone, every other kind asks its provider's matcher about the entities the event's own payload
            // carries. An event no plan claims is ignored here rather than queued, so a filtered-out trigger costs
            // the tick nothing.
            var work = ClaimedWork(registeredWork, snapshot);
            if (work.Length == 0) return new DispatchResult("ignored", "attachment-mismatch", value.EventId);
            // The claimed work is a decision of the enqueue, so the grouping and the command bound are computed here
            // once and travel with the item: the budget question the tick asks is answered from these numbers
            // instead of rebuilding the same groups.
            var planUsage = PlanUsageOf(work);
            var commandBound = 0;
            foreach (var plan in planUsage) commandBound += plan.Steps;
            foreach (var plan in planUsage)
            {
                var limit = plan.Plan.Plan.Limits;
                RuntimeJson.Require(snapshot.CausalDepth <= limit.MaxCausalDepth, "causal-depth", plan.Plan.Plan.Id);
                RuntimeJson.Require(plan.Steps <= limit.MaxCommandsPerTick, "event-command-budget", plan.Plan.Plan.Id);
                RuntimeJson.Require(QueuedFor(plan.Plan) < limit.MaxQueuedEvents, "plan-queue-budget", plan.Plan.Plan.Id);
            }
            RuntimeJson.Require(commandBound <= Limits.MaxCommandsPerTick, "event-command-budget", value.EventId);
            RuntimeJson.Require(history.Count < MaximumEventHistory, "event-history-budget", "This world exhausted its replay ledger; old IDs are never evicted and replayed.");
            RuntimeJson.Require(queue.Count < Limits.MaxQueuedEvents, "queue-budget", value.EventId);
            history.Add(key, fingerprint);
            queue.Enqueue(new Pending(handle.ProviderId, handle.Generation, snapshot, work, null, ++sequence, EventRowsOf(work, snapshot, sequence))
            { PlanUsage = planUsage, PlanUsageGeneration = lifecycleGeneration }, (snapshot.SimulationTick, sequence));
            // A `g-wait` that named this event resumes in the same advance the event was published in, so a message
            // and the flow that answers it are one tick's work rather than two.
            WakeWaiters(snapshot);
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
        foreach (var port in outputs) if (value.Outputs.TryGetProperty(RuntimeJson.Text(port, "id"), out var data)) { ResolveValue(data, port); ValidateEntities(data, port); }
    }
    /// <summary>One entity reference of a captured event, of a frame or of a publish payload, checked against the
    /// provider that answers for its kind. <paramref name="live"/> collects the reference together with the resolver
    /// that answered for it, so a later read of the same value asks that resolver again without parsing the frame a
    /// second time.</summary>
    private void CheckEntity(EntityReference entity, List<EntityCheck>? live = null)
    {
        RuntimeJson.Text(entity.Id); RuntimeJson.Integer(entity.WorldEpoch); RuntimeJson.Integer(entity.LifeEpoch);
        RuntimeJson.Require(entity.WorldEpoch == WorldEpoch, "stale-world", entity.Id);
        var kind = RuntimeJson.KindOf(entity.Id);
        RuntimeJson.Require(kind.Length > 0, "entity-resolver", entity.Id);
        RuntimeJson.Require(registry.Resolvers.TryGetValue(kind, out var registered), "entity-resolver", entity.Id);
        bool valid;
        try { valid = registered.Resolve(entity); }
        catch (Exception) { throw new RuntimeContractException("entity-resolver-failed", entity.Id); }
        RuntimeJson.Require(valid, "stale-entity", entity.Id);
        live?.Add(new EntityCheck(entity, registered.Resolve));
    }
    private void ValidateEntities(JsonElement value, JsonElement port, List<EntityCheck>? live = null)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (RuntimeJson.Text(port, "type") != "entity") return;
        if (!RuntimeGraphContracts.Many(port)) CheckEntity(RuntimeJson.EntityRow(value), live);
        else foreach (var item in value.EnumerateArray()) CheckEntity(RuntimeJson.EntityRow(item), live);
    }
    /// <summary>
    /// One value against the port that carries it, with every entity reference parsed exactly once. A `pure`/`query`
    /// frame is validated here, when it is produced, and never again while the activation lasts: the shape, the
    /// cardinality and the references are decisions of a frozen registry and an immutable frame. <paramref name="live"/>
    /// keeps the references and their resolvers, which is what a later read of the same frame re-asks.
    /// </summary>
    private void ValidateFrameValue(JsonElement value, JsonElement port, List<EntityCheck>? live)
    {
        if (RuntimeJson.Text(port, "type") != "entity") { ResolveValue(value, port); return; }
        if (value.ValueKind == JsonValueKind.Null)
        {
            RuntimeJson.Require(RuntimeJson.Flag(port, "nullable"), "null-input", RuntimeJson.Text(port, "id"));
            return;
        }
        if (!RuntimeGraphContracts.Many(port)) { CheckEntity(RuntimeJson.EntityRow(value), live); return; }
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 256, "invalid-array", "entity many");
        var references = new EntityReference[value.GetArrayLength()];
        var distinct = new HashSet<EntityReference>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var reference = RuntimeJson.EntityRow(item);
            RuntimeJson.Require(distinct.Add(reference), "duplicate-entity", "Repeated recipients require explicit semantics.");
            references[index++] = reference;
        }
        foreach (var reference in references) CheckEntity(reference, live);
    }
    /// <summary>Re-asks the resolvers one value's entities were validated with. This is the only part of a frame's
    /// validation that a handler of the same activation can still invalidate, so it is the only part a repeated read
    /// repeats.</summary>
    private void RecheckEntities(EntityCheck[] checks)
    {
        var code = EntityLivenessCode(checks, out var subject);
        if (code != null) throw new RuntimeContractException(code, subject);
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
                var dispatch = Publish(handle, new RuntimeEvent(factId, fact.BindingId, WorldEpoch, simulationTick, pending.Event.ScopeId, fact.Outputs));
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
        // The intents belong to this advance: a caller that never took them asked for nothing to be presented.
        presentationOutputs.Clear();
        // The same rule for the variable delta: the touch set is this advance's writes and clears, so a client is
        // handed exactly what changed under the snapshot it holds.
        variables.BeginTick();
        // The same rule for the owner tier's dispatches: they are this advance's decisions and nothing else's.
        ownerOutputs.Clear();
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
            CleanScheduledLifetimes(scheduleReports); CleanStateLifetimes(leaseReports); CleanHandleLifetimes();
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
                // A pending that carries a suspended `g-wait` resumes the walk only if it is the outcome that won:
                // the event that woke it, or a deadline no event beat. The loser is dropped without a receipt.
                if (pending.Wait != null && !AdmitWait(pending)) continue;
                // The dispatch's own event table: row 0 is this event and any event a handler of it publishes is
                // appended while it is walked, so an event value is only ever a row of the dispatch reading it.
                currentEventRows = pending.Rows;
                if (!IsRegistered(pending.Provider, pending.Generation))
                {
                    if (pending.Schedule != null) EndSchedule(pending.Schedule, "cancelled", "source-lifecycle");
                    LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "source-lifecycle", pending.Provider);
                    events.Add(new EventReceipt(pending.Event.EventId, "cancelled", "source-lifecycle")); continue;
                }
                var scopeCancelled = cancelled.Count > 0 && cancelled.Contains((pending.Provider, pending.Event.ScopeId));
                if (scopeCancelled)
                {
                    if (pending.Schedule != null) EndSchedule(pending.Schedule, "cancelled", "source-lifecycle");
                    LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "source-lifecycle", pending.Provider);
                    events.Add(new EventReceipt(pending.Event.EventId, "cancelled", "source-lifecycle")); continue;
                }
                var groups = PendingPlans(pending, out var active);
                var count = 0;
                foreach (var group in groups) count += group.Steps;
                if (active.Count == 0)
                {
                    if (pending.Schedule != null) EndSchedule(pending.Schedule, "cancelled", "plan-unloaded");
                    LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "plan-unloaded", pending.Provider);
                    events.Add(new EventReceipt(pending.Event.EventId, "cancelled", "plan-unloaded")); continue;
                }
                if (pending.Schedule != null && !AdmitPulse(pending, scheduleReports))
                {
                    // The refused pulse was already dequeued and its schedule ended; leaving it out of the receipts would
                    // hide a refused event from the tick result.
                    events.Add(new EventReceipt(pending.Event.EventId, "rejected", scheduleReports[^1].Code));
                    continue;
                }
                eventsThisTick++; commandsThisTick += count; processed++;
                foreach (var group in groups) { planTickUsage.TryGetValue(group.Plan.Plan.Id, out var used); planTickUsage[group.Plan.Plan.Id] = (used.Events + 1, used.Commands + group.Steps); }
                var failed = false;
                foreach (var item in active)
                {
                    var stepPlan = new RuntimeLogPlan { PlanId = item.Plan.Plan.Id, ResourceId = item.Plan.Plan.ResourceId, ResourceRevision = item.Plan.Plan.ResourceRevision };
                    var stepOrigin = new StepOrigin(pending.Event, in stepPlan, item.Entry.NodeId);
                    // An activation's memo is `pure`/`query` results; `query` and `pure` steps are never dispatched
                    // directly, they are read on demand by whichever step's input names them via `fromStepSlot`.
                    // The same table holds what a `control` step wrote when it was entered and what an `action`
                    // committed when it ran, so one table answers every `fromStepSlot` read in the activation.
                    // Every dispatch, loop round and schedule resumption is its own activation.
                    BeginActivation();
                    var continuation = pending.Schedule?.Resume is { } resume && ReferenceEquals(resume.Work, item) ? resume : null;
                    // A `g-wait` resumption is not a schedule: the walk re-enters the wait step itself, with the
                    // received event's own frame, which is what the steps after it read.
                    int? cursor = pending.Wait is { } waiting && ReferenceEquals(waiting.Item, item)
                        ? ResumeWait(item.Entry, waiting, pending)
                        : continuation == null ? item.Entry.Start : ResumeActivation(item.Entry, continuation);
                    var controlFailure = (string?)null;
                    while (true)
                    {
                        if (cursor is not int stepIndex)
                        {
                            // The walked path ended: the innermost control region advances, or the walk is over.
                            if (!ResumeControl(out var resumed)) break;
                            cursor = resumed;
                            continue;
                        }
                        var step = item.Entry.Steps[stepIndex];
                        // The identity is the four names it always was, composed from the two halves that are
                        // already quoted: this dispatch's publisher and event, and this step's own plan and node.
                        var commandId = string.Concat(CommandPrefixOf(pending), step.CommandSuffix);
                        CommandResult? result = null; var invoked = false; int? next = null; var stopped = false;
                        // `step.*` belongs to the provider of the binding that executes the step, which is not the entry's
                        // trigger binding: a trigger may route into another package's action.
                        var stepProvider = step.ProviderId;
                        // One activation may execute this many steps before the plan is refused by name. The control
                        // dispatch below is charged the same as an action: a control is a step too.
                        stepExecutions++;
                        if (StepBudgetSpent(item.Plan.Plan.Limits)) { controlFailure = RuntimeAbiCodes.DispatchStepBudget; break; }
                        try
                        {
                            RuntimeJson.Require(item.Plan.Modules.All(m => IsRegistered(m.Key, m.Value)), "binding-lifecycle", step.BindingId);
                            RuntimeJson.Require(pending.Schedule == null || pending.Schedule.Handle.Status == "active", "schedule-cancelled", pending.Event.EventId);
                            RuntimeJson.Require(!cancelled.Contains((pending.Provider, pending.Event.ScopeId)), "scope-cancelled", pending.Event.ScopeId);
                            var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                            var merged = step.Promoted.Count == 0 ? null : step.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                            foreach (var input in step.Inputs)
                            {
                                JsonElement value;
                                if (input.Literal is { } literal) value = literal;
                                else if (input.FromStep is { } from)
                                {
                                    var frame = EvaluateStep(item, from.Step, pending);
                                    var port = RuntimeJson.Text(input.Port, "id");
                                    value = StepSlotValue(frame, port, step.NodeId + "." + input.Name);
                                    // A `pure`/`query` frame was validated against its own contract when it was
                                    // produced and cannot change inside the activation, so a later read of it only
                                    // re-asks what a handler of this same activation can still invalidate: whether
                                    // the entities it carries are alive. Every other frame — and every read that
                                    // widens a single value into a collection — is validated in full.
                                    if (!input.Wrap && validatedSlots.Contains(from.Step))
                                    {
                                        if (memoEntities.TryGetValue((from.Step, port), out var entities)) RecheckEntities(entities);
                                    }
                                    else { ResolveValue(value, input.Port); ValidateEntities(value, input.Port); }
                                    // A non-nullable "one" output wired into a "many" input was validated above
                                    // against its own (origin) cardinality; wrap it into the one-element collection the
                                    // "many" input expects only now, after that validation has passed.
                                    if (input.Wrap) value = RuntimeJson.From(new[] { value });
                                }
                                else
                                {
                                    if (!pending.Event.Outputs.TryGetProperty(input.EventPort!, out value)) continue;
                                    // A handle is moved, not decoded: only the step that consumes it checks it, so
                                    // a value passing through several steps is validated exactly once.
                                    if (RuntimeJson.Text(input.Port, "type") == "handle") CheckHandle(value, input.Port, step.NodeId + "." + input.Name);
                                    else { ResolveValue(value, input.Port); ValidateEntities(value, input.Port); }
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
                            var presentationTier = IsPresentationStep(step);
                            // The owner tier is the presentation tier's sibling with one difference: what the
                            // recipient writes is world state, so the step's own receipt still records a dispatch
                            // rather than a write made here, but the holder's result is a real commit.
                            var ownerTier = !presentationTier && IsOwnerStep(step);
                            if (step.NodeKind == "control")
                            {
                                // Control routing is internal to the kernel: no handler lookup, no command receipt —
                                // only `action` steps ever produce one. Every control kind is dispatched in that one
                                // place, so `next`, `then`, `pulse` and `body` share one walker. A control that refuses
                                // — a stale handle, an exhausted budget — rejects the event; it never invents a command.
                                int? controlCursor = null;
                                try { controlFailure = EnterControl(item, stepIndex, step, pending, RuntimeJson.From(inputs), parameters, out controlCursor); }
                                catch (RuntimeContractException ex) { controlFailure = ex.Code; }
                                if (controlFailure != null) break;
                                // A control routes the walk itself: it writes no receipt and produces no command, so
                                // it leaves the step body exactly where the walk continues.
                                cursor = controlCursor;
                                goto continueWalk;
                            }
                            if (presentationTier)
                            {
                                // A `presentation` step is dispatched, not invoked here. What it writes is the game's
                                // own presentation, so the advance decides the timing and the recipient set and records
                                // the intent the network layer hands to each recipient; this is also the step's own
                                // receipt, and the code says so instead of claiming a command nobody ran on this
                                // machine. The recorded result carries no commit and no fact, so an entrypoint that
                                // continues past a presentation step never sees a committed write it did not make.
                                var presented = EnterPresentation(item.Plan, step, pending, commandId, RuntimeJson.From(inputs), out var presentationResult);
                                if (presented != null) { controlFailure = presented; break; }
                                result = presentationResult;
                            }
                            else if (ownerTier)
                            {
                                // An `owner` step is dispatched to the one machine that holds what it changes: the
                                // host resolved the inputs and decided the timing, and the holder runs the same
                                // handler through the owner entry point. The walk continues on a confirmed commit,
                                // so an owner step may sit in the middle of an entry.
                                var owned = EnterOwner(item.Plan, step, pending, commandId, RuntimeJson.From(inputs), out var ownerResult);
                                if (owned != null) { controlFailure = owned; break; }
                                result = ownerResult;
                            }
                            else
                            {
                                // The step's binding decides who runs what: a handler the provider registered runs
                                // here, and nothing about the walk is special to an action.
                                var handler = registry.Handlers[step.BindingId];
                                LogStepStarted(in stepOrigin, stepProvider, step.NodeId, step.BindingId, commandId);
                                currentCommand = new CommandContext(pending.Event, simulationTick, commandId, item.Plan.Plan.Id, item.Plan.Plan.ResourceId, item.Plan.Plan.ResourceRevision, step.NodeId, parameters, RuntimeJson.From(inputs), isHost);
                                executed++; invoked = true;
                                result = NormalizeInvokedResult(handler(currentCommand));
                                if (result.Facts.Count > 0) PublishConfirmedFacts(result, step.BindingId, pending, simulationTick, events);
                            }
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
                        // An action's own result row is a value a later step reads through `fromStepSlot`, so it is
                        // published into the same activation table a `pure`/`query`/`control` step's frame goes into:
                        // one slot table per activation, and one place a step's outputs are read from.
                        if (step.NodeKind == "action") stepFrames[stepIndex] = commandResult.Outputs;
                        commands.Add(new CommandReceipt(commandId, pending.Event.EventId, pending.Event.CauseId, pending.Event.RootEventId ?? pending.Event.EventId, item.Plan.Plan.Id, item.Plan.Plan.ResourceId, item.Plan.Plan.ResourceRevision, step.NodeId, step.BindingId, WorldEpoch, simulationTick, commandResult));
                        // An entrypoint stops on a rejected/failed/cancelled/expired command, or on a partial result
                        // with an unknown commit state; a confirmed-commit partial continues.
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
                    // A control that refused — a stale handle, an exhausted iteration or step budget — stops this
                    // entry's walk and is reported as the event's rejection instead of an invented command result.
                    if (controlFailure != null)
                    {
                        if (controlFailure.EndsWith("-budget", StringComparison.Ordinal))
                            LogBudgetExceeded(pending.Event.BindingId, pending.Event.EventId, controlFailure);
                        else LogEventRejected(pending.Event.BindingId, pending.Event.EventId, controlFailure, pending.Provider);
                        events.Add(new EventReceipt(pending.Event.EventId, "rejected", controlFailure));
                        failed = true;
                    }
                }
                if (pending.Schedule != null && pending.Schedule.Handle.Status == "active"
                    && pending.Schedule.TotalPulses is { } pulses && pending.Schedule.Index >= pulses)
                    EndSchedule(pending.Schedule, "completed", "all-pulses-dispatched");
                if (!failed) events.Add(new EventReceipt(pending.Event.EventId, "processed", "dispatched"));
            }
        }
        finally { currentCommand = null; currentEventRows = null; advancing = false; }
        return new TickResult(processed, executed, queue.Count, commands, events)
        {
            Schedules = scheduleReports.AsReadOnly(),
            StateLeases = leaseReports.AsReadOnly(),
            // The intents are handed out with the tick that decided them, which is what makes "the host decided
            // the timing" true: a caller that reads this result holds exactly the presentations of this advance.
            Presentations = presentationOutputs.Count == 0 ? Array.Empty<PresentationOutput>() : presentationOutputs.ToArray(),
            // The owner dispatches travel with the tick that decided them, for the same reason: the host decided
            // the timing, and a caller that reads this result holds exactly the commands of this advance.
            OwnerCommands = ownerOutputs.Count == 0 ? Array.Empty<OwnerOutput>() : ownerOutputs.ToArray()
        };
    }
    /// <summary>The budget reason that keeps the head event queued for the next tick, or null when it can dispatch now,
    /// in the order the dispatch loop has always evaluated them (global event, global command, then per plan). The first
    /// refused condition is the reason, so the code names what actually stopped it; the caller writes at most one
    /// `event.deferred` per tick.</summary>
    private string? BudgetRefusal(Pending pending)
    {
        var groups = PendingPlans(pending, out _);
        var count = 0;
        foreach (var group in groups) count += group.Steps;
        if (eventsThisTick >= Limits.MaxEventsPerTick) return "tick-event-budget";
        if (commandsThisTick + count > Limits.MaxCommandsPerTick) return "tick-command-budget";
        foreach (var group in groups)
        {
            planTickUsage.TryGetValue(group.Plan.Plan.Id, out var used);
            if (used.Events >= group.Plan.Plan.Limits.MaxEventsPerTick) return "plan-tick-event-budget";
            if (used.Commands + group.Steps > group.Plan.Plan.Limits.MaxCommandsPerTick) return "plan-tick-command-budget";
        }
        return null;
    }
    /// <summary>
    /// This dispatch's per-plan share and the work items still owned by a loaded plan, at the generation they were
    /// derived at. The claimed work is a decision of the enqueue and cannot change; the one thing that can retire it
    /// before it is walked is a plan unloading, which is exactly what the lifecycle generation reports. While the
    /// number stands still the enqueue's own grouping is the answer, and the same question asked twice — by the
    /// budget check and then by the walk — is answered once.
    /// </summary>
    private IReadOnlyList<PendingPlan> PendingPlans(Pending pending, out IReadOnlyList<Work> active)
    {
        if (pending.PlanUsage != null && pending.PlanUsageGeneration == lifecycleGeneration)
        { active = pending.Work; return pending.PlanUsage; }
        var live = new List<Work>();
        for (var i = 0; i < pending.Work.Count; i++)
            if (plans.TryGetValue(pending.Work[i].Plan.Plan.Id, out var loaded) && ReferenceEquals(loaded, pending.Work[i].Plan)) live.Add(pending.Work[i]);
        active = live;
        var usage = PlanUsageOf(live);
        pending.PlanUsage = usage; pending.PlanUsageGeneration = lifecycleGeneration;
        return usage;
    }
    /// <summary>The work of one dispatch grouped by plan, in the order the plans were first claimed, with each
    /// plan's command bound. One pass, no grouping object and no closure: the numbers are read as often as the
    /// dispatch is asked about its budget.</summary>
    private static IReadOnlyList<PendingPlan> PlanUsageOf(IReadOnlyList<Work> work)
    {
        var usage = new List<PendingPlan>();
        for (var i = 0; i < work.Count; i++)
        {
            var index = -1;
            for (var j = 0; j < usage.Count; j++) if (ReferenceEquals(usage[j].Plan, work[i].Plan)) { index = j; break; }
            if (index < 0) usage.Add(new PendingPlan(work[i].Plan, work[i].Entry.DispatchableStepCount));
            else usage[index] = usage[index] with { Steps = usage[index].Steps + work[i].Entry.DispatchableStepCount };
        }
        return usage;
    }
    /// <summary>How many queued dispatches still name this plan. The queue is the authority — an item carries the
    /// plans it claimed — so the answer is read from it rather than from a second counter that would have to be kept
    /// in step with every path that drops an item.</summary>
    private int QueuedFor(LoadedPlan plan)
    {
        var count = 0;
        foreach (var entry in queue.UnorderedItems) if (NamesPlan(entry.Element, plan)) count++;
        return count;
    }
    private static bool NamesPlan(Pending pending, LoadedPlan plan)
    {
        if (pending.PlanUsage is { } usage)
        {
            for (var i = 0; i < usage.Count; i++) if (ReferenceEquals(usage[i].Plan, plan)) return true;
            return false;
        }
        for (var i = 0; i < pending.Work.Count; i++) if (ReferenceEquals(pending.Work[i].Plan, plan)) return true;
        return false;
    }
    /// <summary>
    /// The first half of every step's command identity for one dispatch: the publisher and the event, each quoted
    /// by the JSON encoder. The identity a receipt carries is the same text it always was — the whole identity is
    /// `[publisher,event,plan,node]` — but only this half is per dispatch: the plan and the node half is decided
    /// when the plan loads, so composing an identity is two strings joined instead of a document serialized again.
    /// </summary>
    private static string CommandPrefixOf(Pending pending)
        => pending.CommandPrefix ??= "[" + RuntimeJson.Quote(pending.Provider) + "," + RuntimeJson.Quote(pending.Event.EventId);
    /// <summary>
    /// The replay ledger's value for one canonical text. The ledger only ever compares it with the entry the same
    /// identity already recorded — nothing carries it to the wire, a log record or a receipt — so a local 128-bit
    /// hash of the canonical text is enough and is computed without a digest. Identity that does travel keeps
    /// <see cref="Fingerprint"/>'s SHA-256: a schedule's own identity is part of the event identity its pulses and
    /// receipts report.
    /// </summary>
    private static string LedgerFingerprint(string canonical)
    {
        var low = 14695981039346656037UL; var high = 1099511628211UL;
        foreach (var character in canonical)
        { low = (low ^ character) * 1099511628211UL; high = (high ^ character) * 14695981039346656037UL; }
        return low.ToString("x16") + high.ToString("x16");
    }
    /// <summary>Evaluates a step's frame on demand through <see cref="EvaluateStep"/>. Kept as the named entry
    /// point the dispatch loop reads like any other step accessor.</summary>
    private JsonElement EvaluatePure(Work item, int stepIndex, Pending pending) => EvaluateStep(item, stepIndex, pending);
}
