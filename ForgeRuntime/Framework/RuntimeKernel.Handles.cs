using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>A module can cast the handles its own package owns, publish its own bindings and cancel its own
/// scopes. This is ownership isolation, not an untrusted-code sandbox.</summary>
public sealed partial class RuntimeModuleHandle
{
    /// <summary>
    /// Casts one handle of a kind this provider owns. The kind's own name in the shared handle-kind table and the
    /// lifetime it lives for are the two facts a consuming port is checked against, so both come from the shared
    /// vocabulary rather than from a free-form string. The handle goes through the kernel's one allocation path,
    /// which means the same pool, the same `handle-budget` ceiling and the same `stale-handle` rule every other
    /// handle obeys; nothing here is a second handle mechanism.
    /// </summary>
    public JsonElement CreateEffectHandle(string lifetime) => kernel.CastHandle(this, "effect", lifetime);
    public JsonElement CreateChargeHandle(string lifetime) => kernel.CastHandle(this, "charge", lifetime);
    public JsonElement CreatePoolHandle(string lifetime) => kernel.CastHandle(this, "pool", lifetime);
    public JsonElement CreateRequestHandle(string lifetime) => kernel.CastHandle(this, "request", lifetime);

    /// <summary>Attaches the native object a provider's own handle stands for, so the package that owns both can
    /// read its handle back as the native thing it tracks. One handle holds one object, and the table is dropped
    /// with the handle, with the world and with this module's registration: a handle that outlived its object
    /// would be a name for nothing.</summary>
    public void RegisterNative(JsonElement handle, object native)
        => kernel.RegisterNativeHandle(this, handle, native);

    /// <summary>The native object a live handle stands for, or false when the handle holds none, is not this
    /// module's, or is no longer live.</summary>
    public bool TryNative(JsonElement handle, out object? native)
        => kernel.TryNativeHandle(this, handle, out native);

    /// <summary>
    /// Registers what cancelling this handle has to do beyond releasing it. A timer handle's cancellation is the
    /// kernel's own — it ends the schedule the handle names — so this hook is for the kinds a provider owns: the
    /// package that started an effect or cast a charge is the only one that can stop it.
    /// The callback runs after the handle is spent and never replaces the handle check: a handle that is not this
    /// module's, or not live, is refused before the callback is reached.
    /// </summary>
    public void RegisterCancel(JsonElement handle, Action cancel)
        => kernel.RegisterHandleCancel(this, handle, cancel);
}

/// <summary>
/// The kernel's handle pool and the budgeted query session a `query` step reads the world through. Both exist so
/// that a plan can only ever touch what an earlier step gave it: a handle is a pool address plus a generation, so
/// a recycled slot can never be mistaken for the handle that used to live there, and a world read is a metered
/// call that reports its refusal instead of answering with an empty result.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>One live handle. Its kind and lifetime are what a consuming port must agree with; the schedule
    /// identity is what `cancel` resolves, so a handle never carries a schedule string of its own; the effect
    /// identity is what a kernel-managed effect resolves, so cancelling the handle ends the effect instead of
    /// releasing a slot the effect still names. The native object and the cancel callback belong to the provider
    /// that cast the handle, and neither outlives the slot.</summary>
    private sealed class HandleSlot
    {
        internal string Kind = "";
        internal string Lifetime = "";
        internal string ProviderId = "";
        internal int Generation;
        internal bool Active;
        internal string? ScheduleId;
        internal int EffectId = -1;
        internal object? Native;
        internal Action? Cancel;
    }
    private readonly List<HandleSlot?> handleSlots = new();
    private readonly Dictionary<string, int> providerIndex = new(StringComparer.Ordinal);
    private int handleGeneration;
    /// <summary>The first refusal of the query step currently being evaluated, or null. Cleared before every
    /// evaluation; read once after it, which is how a partial read becomes an explicit `query-budget` rejection
    /// rather than a frame that silently holds fewer candidates than the world has.</summary>
    private string? queryRefusal;

    /// <summary>The registration-order index a handle carries instead of its provider's id: stable for the life
    /// of the kernel, so a provider unregistering never renumbers another provider's handles.</summary>
    private int ProviderIndex(string provider)
    {
        if (providerIndex.TryGetValue(provider, out var index)) return index;
        index = providerIndex.Count;
        providerIndex.Add(provider, index);
        return index;
    }

    /// <summary>Allocates one handle and returns its wire value: the identity triple in a <see cref="FrameHandle"/>
    /// plus the creating provider's index. A slot is reused only when free, and every allocation takes a new
    /// generation, so a value read after its slot was recycled fails `stale-handle` instead of naming a stranger.</summary>
    private JsonElement CreateHandle(string provider, string kind, string lifetime, out int slot, string? scheduleId = null,
        int effectId = -1)
    {
        var free = handleSlots.FindIndex(entry => entry is not { Active: true });
        if (free < 0)
        {
            RuntimeJson.Require(handleSlots.Count < Limits.MaxLiveHandles, RuntimeAbiCodes.HandleBudget, "Live handle capacity reached.");
            handleSlots.Add(null); free = handleSlots.Count - 1;
        }
        var generation = ++handleGeneration;
        RuntimeJson.Require(generation > 0 && generation < int.MaxValue, RuntimeAbiCodes.HandleBudget, "Handle generation exhausted.");
        var entrySlot = new HandleSlot { Kind = kind, Lifetime = lifetime, ProviderId = provider, Generation = generation, Active = true, ScheduleId = scheduleId, EffectId = effectId };
        handleSlots[free] = entrySlot;
        slot = free;
        return HandleValue(WorldEpoch, generation, free, ProviderIndex(provider));
    }

    private static JsonElement HandleValue(long worldEpoch, int generation, int local, int provider)
        => RuntimeJson.From(new { worldEpoch, lifeEpoch = generation, local, provider });

    /// <summary>Releases one handle slot. The generation is left as it was: the next allocation overwrites both,
    /// and a value that still names the old generation must fail rather than read the new occupant. Everything the
    /// slot carried goes with it, so a released handle names no native object and no cancel callback even while the
    /// slot is free.</summary>
    private void ReleaseHandle(int slot)
    {
        if (slot >= 0 && slot < handleSlots.Count) handleSlots[slot] = null;
    }

    /// <summary>
    /// One provider's own handle, cast through the kernel's single allocation path. The lifetime is checked against
    /// the shared lifetime table here rather than at every consumer, because a lifetime no port could declare would
    /// make the handle unusable the moment it was cast.
    /// </summary>
    internal JsonElement CastHandle(RuntimeModuleHandle owner, string kind, string lifetime)
    {
        Thread(); ArgumentNullException.ThrowIfNull(owner);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var kindName = kind ?? "";
        var lifetimeName = lifetime ?? "";
        RuntimeJson.Require(RuntimeGraphContracts.HandleKinds.Contains(kindName), RuntimeAbiCodes.HandleKind, kindName);
        RuntimeJson.Require(RuntimeGraphContracts.HandleLifetimes.Contains(lifetimeName), RuntimeAbiCodes.HandleLifetime, lifetimeName);
        return CreateHandle(owner.ProviderId, kindName, lifetimeName, out _);
    }

    /// <summary>The slot one live handle value names, as its own provider wrote it. Everything a provider's own
    /// handle API accepts goes through this: the value must be a well-formed handle of this world, name a live
    /// slot, and be the slot this module's provider created. The port-level `handle-kind`/`handle-lifetime` check
    /// stays where it always was — at the step that consumes the handle through a declared port, which is what
    /// `CheckHandle` is.</summary>
    private HandleSlot HandleOf(RuntimeModuleHandle owner, JsonElement value)
    {
        RuntimeJson.Handle(value);
        var world = (int)RuntimeJson.Integer(value.GetProperty("worldEpoch"), 0, int.MaxValue);
        var generation = (int)RuntimeJson.Integer(value.GetProperty("lifeEpoch"), 0, int.MaxValue);
        var local = (int)RuntimeJson.Integer(value.GetProperty("local"), 0, int.MaxValue);
        RuntimeJson.Integer(value.GetProperty("provider"), 0, int.MaxValue);
        RuntimeJson.Require(world == WorldEpoch, "stale-world", owner.ProviderId);
        if (local >= handleSlots.Count || handleSlots[local] is not { Active: true } slot)
            throw new RuntimeContractException(RuntimeAbiCodes.StaleHandle, owner.ProviderId);
        RuntimeJson.Require(slot.Generation == generation, RuntimeAbiCodes.StaleHandle, owner.ProviderId);
        RuntimeJson.Require(slot.ProviderId == owner.ProviderId, "handle-owner", owner.ProviderId);
        return slot;
    }

    internal void RegisterNativeHandle(RuntimeModuleHandle owner, JsonElement value, object native)
    {
        Thread(); ArgumentNullException.ThrowIfNull(native);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var slot = HandleOf(owner, value);
        RuntimeJson.Require(slot.Native == null, "handle-native-conflict", owner.ProviderId);
        slot.Native = native;
    }

    internal bool TryNativeHandle(RuntimeModuleHandle owner, JsonElement value, out object? native)
    {
        Thread(); native = null;
        if (!owner.IsRegistered) return false;
        HandleSlot slot;
        try { slot = HandleOf(owner, value); }
        catch (RuntimeContractException) { return false; }
        native = slot.Native;
        return native != null;
    }

    internal void RegisterHandleCancel(RuntimeModuleHandle owner, JsonElement value, Action cancel)
    {
        Thread(); ArgumentNullException.ThrowIfNull(cancel);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var slot = HandleOf(owner, value);
        RuntimeJson.Require(slot.Cancel == null, "handle-cancel-conflict", owner.ProviderId);
        slot.Cancel = cancel;
    }

    /// <summary>
    /// Whether one module's own handle value still names a live slot of this world. This is the same allocation,
    /// generation and ownership rule <see cref="HandleOf"/> applies, asked as a question instead of as a refusal,
    /// so a provider that keeps a handle across frames can ask before it reads the native object behind it — and
    /// can tell a handle it never cast from one whose life has ended, because both are simply not live for it.
    /// </summary>
    public bool IsHandleLive(RuntimeModuleHandle owner, JsonElement value)
    {
        Thread(); ArgumentNullException.ThrowIfNull(owner);
        try { HandleOf(owner, value); return true; }
        catch (RuntimeContractException) { return false; }
    }

    /// <summary>
    /// One provider handle's own cancellation, as the `cancel` control performs it: the handle is checked against
    /// the kind and lifetime its port declares, the hook the provider registered runs once, and the handle is
    /// spent. This is the entry point a package that stops its own effect outside a plan ends through, so a
    /// provider never grows a second, weaker cancellation path.
    /// </summary>
    public int CancelHandle(RuntimeModuleHandle owner, JsonElement value, JsonElement port, string detail)
    {
        Thread(); ArgumentNullException.ThrowIfNull(owner);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var slot = CheckHandle(value, port, detail);
        RuntimeJson.Require(slot.ProviderId == owner.ProviderId, "handle-owner", owner.ProviderId);
        return CancelHandle(value, port, detail);
    }

    /// <summary>
    /// The handles whose life has ended. A handle the kernel cast for its own schedule dies with the world and is
    /// ended by its schedule's own cancellation; a handle a provider cast dies when the life its native object
    /// belonged to does, which is the same question the owning resolver already answers for every other reference.
    /// Both are the one lifetime rule the handle pool has, so nothing here invents a second expiry: a handle that no
    /// longer answers for its object is released, and every later read of its value is `stale-handle`.
    /// </summary>
    private void CleanHandleLifetimes()
    {
        for (var slot = 0; slot < handleSlots.Count; slot++)
        {
            if (handleSlots[slot] is not { Active: true, Native: { } native } handle) continue;
            string kind;
            try { kind = ResolveEntityObject(native); }
            catch (RuntimeContractException) { kind = ""; }
            if (kind != "" && IsEntityCurrentKind(kind, native)) continue;
            // An effect handle is not released on its own: the module that applied the effect is called back first,
            // which is the same ending its own tick pass would write.
            if (handle.EffectId >= 0 && effects.TryGetValue(handle.EffectId, out var effect)) { EndEffect(effect, EffectEndReasons.Reclaimed, CurrentTick); continue; }
            ReleaseHandle(slot);
        }
    }

    /// <summary>Whether the life the native object of one handle belongs to is still current: the question is put
    /// to the resolver that owns the object's kind and to nobody else, exactly as an instance lookup is.</summary>
    private bool IsEntityCurrentKind(string kind, object instance)
    {
        if (!registry.Resolvers.TryGetValue(kind, out var resolver)) return false;
        EntityReference? reference;
        try { reference = EntityInstanceOf(kind, instance); }
        catch (RuntimeContractException) { return false; }
        if (reference == null) return false;
        try { return resolver.Resolve(reference); }
        catch (Exception) { return false; }
    }

    /// <summary>The one place a handle is checked: at the step that consumes it, never while it is being moved.
    /// The identity must name a live slot of this world, and the port's declared kind and lifetime must be the
    /// ones the handle was created with.</summary>
    private HandleSlot CheckHandle(JsonElement value, JsonElement port, string detail)
        => CheckHandle(value, port, detail, out _);

    /// <summary>The same check, handing back the slot's own address so a caller that has to release what it just
    /// checked never reads the value a second time.</summary>
    private HandleSlot CheckHandle(JsonElement value, JsonElement port, string detail, out int address)
    {
        RuntimeJson.Handle(value);
        var world = (int)RuntimeJson.Integer(value.GetProperty("worldEpoch"), 0, int.MaxValue);
        var generation = (int)RuntimeJson.Integer(value.GetProperty("lifeEpoch"), 0, int.MaxValue);
        address = (int)RuntimeJson.Integer(value.GetProperty("local"), 0, int.MaxValue);
        RuntimeJson.Integer(value.GetProperty("provider"), 0, int.MaxValue);
        RuntimeJson.Require(world == WorldEpoch, "stale-world", detail);
        if (address >= handleSlots.Count || handleSlots[address] is not { Active: true } slot)
            throw new RuntimeContractException(RuntimeAbiCodes.StaleHandle, detail);
        RuntimeJson.Require(slot.Generation == generation, RuntimeAbiCodes.StaleHandle, detail);
        RuntimeJson.Require(slot.Kind == Optional(port, "handleKind"), RuntimeAbiCodes.HandleKind, detail);
        RuntimeJson.Require(slot.Lifetime == Optional(port, "lifetime"), RuntimeAbiCodes.HandleLifetime, detail);
        return slot;
    }

    private static string? Optional(JsonElement value, string key)
        => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    /// <summary>Cancels the schedule one timer handle holds, ends the effect one effect handle names, or runs the
    /// cancel hook the provider registered for a handle of its own. The count is the contract's own answer — 0 when
    /// the handle names a schedule that already ended, or a provider handle that had nothing left to stop — and a
    /// handle that is no longer live is refused by name. Either way the handle is spent here: a value that names a
    /// cancelled handle is `stale-handle` from then on, so nothing can keep using what its owner has already
    /// stopped.</summary>
    private int CancelHandle(JsonElement value, JsonElement port, string detail)
    {
        var slot = CheckHandle(value, port, detail, out var address);
        var cancelled = 0;
        if (slot.ScheduleId != null && schedules.TryGetValue((slot.ProviderId, slot.ScheduleId), out var job))
        {
            EndSchedule(job, "cancelled", "explicit-cancel");
            PruneSchedules();
            cancelled = 1;
        }
        // An effect handle is the kernel's own to end: the module that applied the effect is called back with the
        // cancellation, so the module never registers a second cancel hook for the same state.
        if (slot.EffectId >= 0 && effects.TryGetValue(slot.EffectId, out var effect))
        {
            EndEffect(effect, EffectEndReasons.Cancelled, CurrentTick);
            return 1;
        }
        if (slot.Cancel is { } hook)
        {
            slot.Cancel = null;
            try { hook(); }
            catch (Exception) { throw new RuntimeContractException("handle-cancel-failed", detail); }
            cancelled = 1;
        }
        ReleaseHandle(address);
        return cancelled;
    }

    /// <summary>Re-arms the schedule one timer handle holds from now — the rolling window a `restart` asks for.
    /// Unlike cancel the handle is not spent: it names the same schedule for its whole life. A handle with no
    /// schedule behind it is refused by name, and a handle whose schedule already ended fails the slot check as
    /// `stale-handle` before this is reached: a restart never quietly creates a second timer.</summary>
    private bool RestartHandle(JsonElement value, JsonElement port, string detail)
    {
        var slot = CheckHandle(value, port, detail, out _);
        if (slot.ScheduleId == null || !schedules.TryGetValue((slot.ProviderId, slot.ScheduleId), out var job))
            throw new RuntimeContractException("timer-without-schedule", detail);
        return RestartSchedule(job);
    }

    /// <summary>Records the first refusal of the query step being evaluated. The session reports it once, and the
    /// step turns it into a rejection: an exhausted budget is never answered with a partial frame.</summary>
    internal void QueryRefused(string code)
    {
        queryRefusal ??= code switch
        {
            "entity-query-budget" or "entity-query-tick-budget" => RuntimeAbiCodes.QueryBudget,
            "entity-observer-unavailable" => RuntimeAbiCodes.QueryObserverUnavailable,
            _ => code
        };
    }

    /// <summary>Starts one query step's evaluation: the session it hands out is live only for that call, and the
    /// refusal it may record belongs to this step alone.</summary>
    private RuntimeQuerySession BeginQuery(string nodeId)
    {
        queryRefusal = null;
        return new RuntimeQuerySession(this, nodeId, true);
    }

    /// <summary>The world read a `pure` step is never given; every attempt is refused rather than answered.</summary>
    private RuntimeQuerySession NoQuery(string nodeId) => new(this, nodeId, false);
}

