using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>Why one kernel-managed effect ended. The reason travels with the restore callback because the module's
/// own answer can differ: an expired effect and a cancelled one are the same revert, while a world that ended is
/// often a revert nothing has to do — the native state went with the world.</summary>
public static class EffectEndReasons
{
    /// <summary>The duration the card asked for ran out.</summary>
    public const string Expired = "expired";
    /// <summary>A `cancel` step, or the module's own holder, named the effect's handle.</summary>
    public const string Cancelled = "cancelled";
    /// <summary>The world ended, the plan was released, the module unregistered, or the life the effect belonged
    /// to is no longer current.</summary>
    public const string Reclaimed = "reclaimed";
}

/// <summary>
/// What one ending hands back to the module that applied the effect. The handle names the effect instance the
/// kernel cast — the same value the step published when the card asked for a cancel handle; the subject is the
/// recipient set the application was about; the stacks are how many layers the kernel counted, so a module that
/// wrote one native layer per layer knows how many it has to undo.
///
/// The callback runs inside the kernel's own turn: it may not dispatch, register or unregister, and it must not
/// assume the world is still there — `reclaimed` is written for a world that already ended. It also has to be
/// idempotent, because a module may have undone the effect itself through its own removal action while the kernel
/// still holds the instance until its duration runs out.
/// </summary>
public sealed class RuntimeEffectContext
{
    internal RuntimeEffectContext(JsonElement handle, string reason, int stacks, long tick, long worldEpoch,
        string planId, string nodeId, string bindingId, string capabilityId, string commandId,
        IReadOnlyList<EntityReference> subject)
    {
        Handle = handle; Reason = reason; Stacks = stacks; Tick = tick; WorldEpoch = worldEpoch;
        PlanId = planId; NodeId = nodeId; BindingId = bindingId; CapabilityId = capabilityId;
        CommandId = commandId; Subject = subject;
    }

    /// <summary>The effect handle the kernel cast for this instance, as the step published it.</summary>
    public JsonElement Handle { get; }
    /// <summary>One of <see cref="EffectEndReasons"/>.</summary>
    public string Reason { get; }
    /// <summary>How many layers the kernel counted for this instance, at least one.</summary>
    public int Stacks { get; }
    /// <summary>The tick the effect ended in.</summary>
    public long Tick { get; }
    /// <summary>The world the effect was applied in.</summary>
    public long WorldEpoch { get; }
    public string PlanId { get; }
    public string NodeId { get; }
    public string BindingId { get; }
    public string CapabilityId { get; }
    /// <summary>The command that last applied the effect, for diagnostics.</summary>
    public string CommandId { get; }
    /// <summary>The recipients the application was about, in the order the step's own recipients port carried
    /// them. Empty when that port carries no entity — a modifier named by a handle, for example.</summary>
    public IReadOnlyList<EntityReference> Subject { get; }
}

/// <summary>What a module does when the kernel ends an effect: undo what the application wrote. Keyed by the
/// handler name of the binding that applied it, exactly as the handler table is.</summary>
public delegate void EffectRestoreHandler(RuntimeEffectContext context);

/// <summary>
/// One action step's duration options, read once when the plan loads (plan §3.4 动作卡). These are structural: the
/// kernel owns the handle, the clock, the period, the refresh, the layer count and the early ending, and calls the
/// module's own restore callback when an instance ends, so a card never writes a duration into a port a handler
/// would have to keep a clock for.
///
/// <see cref="Reapply"/> is what a second application of the same effect instance does: `refresh` restarts the
/// duration over the one layer the instance has, `stack` adds a layer and restarts the duration of them all, up to
/// <see cref="MaxStacks"/> — at the cap a further application only refreshes, so the layer count never exceeds what
/// the module was told to write — and `stack_independent` adds a layer with a clock of its own, so each layer ends
/// by itself and is restored by itself.
///
/// <see cref="Interval"/> is the period: every layer runs the action again every that many ticks while it lasts,
/// the first run being immediate where the card asked for it. A sustained one-shot action (damage, healing) needs
/// no restore callback, which is why an action without one is allowed exactly when it carries a period.
///
/// <see cref="EndOn"/> names the trigger rows whose events end the effect early — the receiver's own abilities,
/// resolved to their bindings at load. <see cref="Cancel"/> is whether the step publishes the handle a later
/// `cancel` step holds.
/// </summary>
internal sealed record EffectOptions(long Duration, string Reapply, int MaxStacks, bool Cancel,
    long? Interval = null, bool IntervalImmediate = false, IReadOnlyList<string>? EndOn = null)
{
    internal const string Refresh = "refresh";
    internal const string Stack = "stack";
    internal const string StackIndependent = "stack_independent";
    private static readonly string[] Fields =
        { "duration", "reapply", "max_stacks", "cancel", "interval", "interval_immediate", "end_on" };

    /// <summary>Whether a second application adds a layer rather than refreshing the one there is.</summary>
    internal bool StacksOnReapply => Reapply is Stack or StackIndependent;

    /// <summary>Whether each layer keeps a clock of its own rather than sharing the instance's.</summary>
    internal bool IndependentLayers => Reapply == StackIndependent;

    /// <summary>The trigger bindings that end this effect early, empty where the card named none.</summary>
    internal IReadOnlyList<string> EndsOn => EndOn ?? Array.Empty<string>();

    /// <summary>The step's own `effect` block, or null where the card asked for none. Every refusal names the
    /// option that is wrong: a block that cannot be carried out is a plan that does not load, not an action that
    /// silently lasts forever. `unknown-field` stays the shared code for a field nothing reads.</summary>
    internal static EffectOptions? Parse(JsonElement step)
    {
        if (!step.TryGetProperty("effect", out var block) || block.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(block.ValueKind == JsonValueKind.Object, "effect-shape", "effect");
        RuntimeJson.Shape(block, "", string.Join(' ', Fields));
        RuntimeJson.Require(block.TryGetProperty("duration", out var durationValue) && Whole(durationValue, 1),
            "effect-duration", "effect.duration must be a whole number of ticks of at least one.");
        var reapply = Refresh;
        if (block.TryGetProperty("reapply", out var reapplyValue) && reapplyValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(reapplyValue.ValueKind == JsonValueKind.String, "effect-reapply", "effect.reapply");
            reapply = reapplyValue.GetString()!;
            RuntimeJson.Require(reapply is Refresh or Stack or StackIndependent, "effect-reapply",
                "effect.reapply must be `refresh`, `stack` or `stack_independent`.");
        }
        var maxStacks = 1;
        if (block.TryGetProperty("max_stacks", out var stacksValue) && stacksValue.ValueKind != JsonValueKind.Null)
        {
            // A cap is the one thing a stacking reapply cannot work without: without it the count has no ceiling
            // and the module is asked for layers nobody bounded.
            RuntimeJson.Require(reapply is Stack or StackIndependent, "effect-stacks",
                "effect.max_stacks belongs to a stacking reapply.");
            RuntimeJson.Require(Whole(stacksValue, 1), "effect-stacks", "effect.max_stacks must be at least one.");
            maxStacks = (int)stacksValue.GetDouble();
        }
        RuntimeJson.Require(reapply == Refresh || block.TryGetProperty("max_stacks", out _), "effect-stacks",
            "a stacking effect.reapply needs effect.max_stacks.");
        // The period: a sustained action re-runs itself every `interval` ticks while it lasts, with an optional
        // first run in the tick it was applied in.
        long? interval = null;
        if (block.TryGetProperty("interval", out var intervalValue) && intervalValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(Whole(intervalValue, 1), "effect-interval", "effect.interval must be at least one tick.");
            interval = (long)intervalValue.GetDouble();
        }
        var immediate = false;
        if (block.TryGetProperty("interval_immediate", out var immediateValue) && immediateValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(immediateValue.ValueKind is JsonValueKind.True or JsonValueKind.False, "effect-interval",
                "effect.interval_immediate");
            immediate = immediateValue.ValueKind == JsonValueKind.True;
            RuntimeJson.Require(interval != null, "effect-interval", "effect.interval_immediate needs effect.interval.");
        }
        // The events that end the effect early, as trigger binding ids of this plan: the receiver's own abilities,
        // which is why the row they name is looked up at load.
        var endOn = Array.Empty<string>();
        if (block.TryGetProperty("end_on", out var endValue) && endValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(endValue.ValueKind == JsonValueKind.Array, "effect-end-on", "effect.end_on must be an array.");
            var rows = endValue.EnumerateArray().ToArray();
            RuntimeJson.Require(rows.Length <= 8, "effect-end-on", "effect.end_on carries at most eight abilities.");
            var ids = new string[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                RuntimeJson.Require(rows[i].ValueKind == JsonValueKind.String, "effect-end-on", "effect.end_on");
                ids[i] = rows[i].GetString()!;
            }
            RuntimeJson.Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "effect-end-on",
                "effect.end_on repeats an ability.");
            endOn = ids;
        }
        var cancel = false;
        if (block.TryGetProperty("cancel", out var cancelValue) && cancelValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(cancelValue.ValueKind is JsonValueKind.True or JsonValueKind.False, "effect-cancel",
                "effect.cancel");
            cancel = cancelValue.ValueKind == JsonValueKind.True;
        }
        return new EffectOptions((long)durationValue.GetDouble(), reapply, maxStacks, cancel, interval, immediate, endOn);
    }

    private static bool Whole(JsonElement value, long minimum)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
           && double.IsFinite(number) && number == Math.Truncate(number)
           && number >= minimum && number <= RuntimeJson.MaxSafeInteger;
}

public sealed partial class RuntimeKernel
{
    /// <summary>
    /// Whether a trigger named by `end_on` can end this action, judged from the two rows' declared entity kinds: a
    /// trigger whose subject kinds do not cover every kind the action receives can never fire about a receiver, so
    /// the card is refused at load rather than silently keeping an effect nothing can end. A port that declares no
    /// kinds contributes none, and a receiver port that declares none accepts every kind — which is what the graph
    /// wiring already means by the same declaration.
    /// </summary>
    internal static bool EndOnCovers(JsonElement trigger, JsonElement action)
    {
        var from = EntityKinds(trigger);
        if (from.Count == 0) return false;
        var to = EntityKinds(action);
        if (to.Count == 0) return true;
        return to.All(from.Contains);
    }

    /// <summary>The entity kinds one trigger or action row declares across its entity ports, or none where it
    /// declares no kinds at all.</summary>
    private static HashSet<string> EntityKinds(JsonElement graph)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in new[] { RuntimeJson.Rows(graph, "outputs"), RuntimeJson.Rows(graph, "inputs") })
            foreach (var row in port)
            {
                if (RuntimeJson.Text(row, "type") != "entity") continue;
                if (!row.TryGetProperty("entityKinds", out var declared) || declared.ValueKind != JsonValueKind.Array) continue;
                foreach (var kind in declared.EnumerateArray()) kinds.Add(kind.GetString() ?? "");
            }
        return kinds;
    }

    /// <summary>One layer of a live effect: the clock the kernel owns, and the period it re-runs the action on.
    /// A `refresh` instance has exactly one layer and a `stack` instance shares one clock across its layers, so a
    /// layer per application is what makes `stack_independent` the same code with one clock each.</summary>
    private sealed class EffectLayer
    {
        internal long ExpiresAtTick;
        /// <summary>The next tick this layer's action runs again, or -1 for a layer that carries no period. Each
        /// layer keeps its own clock so a long gap in the advance skips missed pulses — the next one is always one
        /// interval after the pulse that ran — instead of replaying ticks nobody simulated.</summary>
        internal long NextPulseTick = -1;
    }

    /// <summary>One live effect instance: the card that applied it, the recipients it is about, its layers, and the
    /// handle that names it. The instance is the kernel's, not the module's — the module holds native state and
    /// answers the restore callback, and neither side keeps a clock for the other.</summary>
    private sealed class EffectSlot
    {
        internal int Id;
        internal string ProviderId = "";
        internal string BindingId = "";
        internal string CapabilityId = "";
        internal string PlanId = "";
        internal string ResourceId = "";
        internal string ResourceRevision = "";
        internal string NodeId = "";
        internal string Identity = "";
        internal IReadOnlyList<EntityReference> Subject = Array.Empty<EntityReference>();
        internal EffectOptions Options = null!;
        internal readonly List<EffectLayer> Layers = new();
        /// <summary>The most layers this instance ever carried, which is what a whole-instance ending reports: the
        /// module wrote one native layer per application and has to undo exactly that many.</summary>
        internal int Peak = 1;
        internal readonly HashSet<string> EndsOn = new(StringComparer.Ordinal);
        internal RuntimeEvent Origin = null!;
        internal JsonElement Inputs;
        internal string CommandPrefix = "";
        internal long WorldEpoch;
        internal string CommandId = "";
        internal JsonElement Handle;
        internal int HandleSlot = -1;
        /// <summary>How many times this instance's action has been re-run by its period, so a pulse's command
        /// identity is its own and a replay ledger can never mistake two pulses for one work item.</summary>
        internal long Pulses;
    }

    /// <summary>One application in flight: what the handler is told, and what is rolled back when it refuses. The
    /// instance's own clock and layer count are only written once the command says it committed, so a refused
    /// application neither refreshes a running effect nor spends a stack.</summary>
    private sealed class PendingEffect
    {
        internal EffectSlot Slot = null!;
        internal EffectOptions Options = null!;
        internal bool Created;
        internal bool AddLayer;
        internal string Port = "";
    }

    private readonly Dictionary<string, int> effectIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<int, EffectSlot> effects = new();
    private int effectSequence;
    /// <summary>Where an effect pulse's own command receipts go while a host advance runs, and the event rows a
    /// pulse's confirmed facts are published into: both are the running advance's, so they are set at its start and
    /// cleared with it. Null outside an advance, which is where a pulse cannot happen at all.</summary>
    private List<CommandReceipt>? pulseReceipts;
    private List<EventReceipt>? pulseEvents;
    /// <summary>The dispatch one effect's pulse writes its facts under, kept per binding: a fact is a fact of the
    /// event that caused the effect, and the pulse has no event of its own.</summary>
    private readonly Dictionary<string, Pending> pulseFacts = new(StringComparer.Ordinal);

    /// <summary>The effect instance one application belongs to: the card and the recipients it is about. Two
    /// dispatches of the same node about the same recipients are the same instance — that is what makes a refresh
    /// a refresh — and a different recipient is its own instance with its own clock. A card whose recipients port
    /// carries no entity keys on the node alone.</summary>
    private static string EffectIdentity(string planId, string nodeId, IReadOnlyList<EntityReference> subject)
    {
        if (subject.Count == 0) return planId + "\n" + nodeId;
        var builder = new StringBuilder(planId).Append('\n').Append(nodeId);
        foreach (var entity in subject)
            builder.Append('\n').Append(entity.Id).Append('@').Append(entity.WorldEpoch).Append(':').Append(entity.LifeEpoch);
        return builder.ToString();
    }

    /// <summary>The recipients one application is about, read from the step's own `recipients.input` port. A port
    /// the plan left unwired, or one that carries a handle rather than an entity, answers with none: the effect
    /// still has its own instance, keyed by the node.</summary>
    private static IReadOnlyList<EntityReference> EffectSubject(ResolvedStep step, JsonElement inputs)
    {
        if (!step.Contract.TryGetProperty("recipients", out var recipients)) return Array.Empty<EntityReference>();
        if (!recipients.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.String)
            return Array.Empty<EntityReference>();
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(input.GetString()!, out var value)
            || value.ValueKind == JsonValueKind.Null) return Array.Empty<EntityReference>();
        if (value.ValueKind == JsonValueKind.Array)
        {
            var entities = new List<EntityReference>(value.GetArrayLength());
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) return Array.Empty<EntityReference>();
                entities.Add(RuntimeJson.Entity(element));
            }
            return entities;
        }
        return value.ValueKind == JsonValueKind.Object
            ? new[] { RuntimeJson.Entity(value) }
            : Array.Empty<EntityReference>();
    }

    /// <summary>
    /// Opens, refreshes or stacks the effect one action step is about and hands the handler what it needs. The
    /// handle is cast here — before the handler runs, so an application that cannot name its own effect writes
    /// nothing — and the layer count is decided here but committed in <see cref="CommitEffect"/>.
    ///
    /// The instance also captures what a later period needs to run the same action again: the origin event, the
    /// resolved inputs and parameters, and this dispatch's own command prefix. A pulse is not a new event, so this
    /// is the only place the frame it re-runs can come from (plan §3.2: 不新派发事件).
    /// </summary>
    private PendingEffect BeginEffect(ResolvedStep step, EffectOptions options, ResolvedPlan plan, JsonElement inputs,
        RuntimeEvent origin, string commandPrefix)
    {
        var planId = plan.Id;
        var subject = EffectSubject(step, inputs);
        var identity = EffectIdentity(planId, step.NodeId, subject);
        EffectSlot? slot = null;
        var created = !effectIdentity.TryGetValue(identity, out var id) || !effects.TryGetValue(id, out slot);
        if (created)
        {
            var fresh = new EffectSlot
            {
                Id = ++effectSequence,
                ProviderId = step.ProviderId,
                BindingId = step.BindingId,
                CapabilityId = step.CapabilityId,
                PlanId = planId,
                ResourceId = plan.ResourceId,
                ResourceRevision = plan.ResourceRevision,
                NodeId = step.NodeId,
                Identity = identity,
                Subject = subject,
                Options = options,
                Origin = origin,
                Inputs = inputs,
                CommandPrefix = commandPrefix
            };
            foreach (var ability in options.EndsOn) fresh.EndsOn.Add(ability);
            fresh.Handle = CreateHandle(step.ProviderId, "effect", EffectLifetime(step.Contract), out var handleSlot, effectId: fresh.Id);
            fresh.HandleSlot = handleSlot;
            slot = fresh;
        }
        return new PendingEffect
        {
            Slot = slot!,
            Options = options,
            Created = created,
            // A fresh instance is its first layer; a running one adds a layer only where the card stacks and the
            // cap has not been reached, so an application at the cap is a pure refresh.
            AddLayer = created || options.StacksOnReapply && slot!.Layers.Count < options.MaxStacks,
            Port = options.Cancel ? EffectHandlePort(step.Contract) ?? "" : ""
        };
    }

    /// <summary>Whether the command proved that nothing was written. A refused or failed command with no commit
    /// leaves no effect; anything that may have written keeps its instance, because a module that did write has to
    /// be called back for it.</summary>
    private static bool EffectKept(CommandResult result)
        => result.Status == CommandStatuses.Succeeded || result.CommitState != CommitStates.None;

    /// <summary>
    /// Makes one application real, or rolls it back. A kept application takes the layer the kernel decided, restarts
    /// the duration from this tick — of the layer it refreshed, or of every layer a `stack` share one clock with —
    /// and — where the card asked for a cancel handle — has the handle published into the step's own handle port, so
    /// a later `cancel` step or removal action holds exactly what this step wrote. A refused application leaves a
    /// running instance exactly as it was: no refreshed clock, no spent stack, and a brand-new instance that never
    /// existed gives its handle back to the pool.
    /// </summary>
    private void CommitEffect(PendingEffect effect, CommandResult result, long tick, string commandId,
        ref CommandResult commandResult)
    {
        var slot = effect.Slot;
        if (!EffectKept(result))
        {
            if (effect.Created)
            {
                ReleaseHandle(slot.HandleSlot);
                slot.HandleSlot = -1;
            }
            return;
        }
        if (effect.Created)
        {
            effects.Add(slot.Id, slot);
            effectIdentity[slot.Identity] = slot.Id;
        }
        var options = effect.Options;
        var layer = new EffectLayer { ExpiresAtTick = tick + options.Duration };
        // An immediate first run is the application itself — the command already in flight is the first period run —
        // so the next one is one interval after this tick either way, and a pulse never fires twice in one tick.
        if (options.Interval is { } period) layer.NextPulseTick = tick + period;
        if (effect.Created) slot.Layers.Add(layer);
        else if (effect.AddLayer)
        {
            slot.Layers.Add(layer);
            slot.Peak = Math.Max(slot.Peak, slot.Layers.Count);
            // A stacking reapply shares one duration and one period across its layers: the layer just added does
            // not start a clock of its own, the whole stack ends — and pulses — on the refreshed one.
            // `stack_independent` is the reapply that keeps a clock per layer and is left alone.
            if (!options.IndependentLayers)
                foreach (var running in slot.Layers)
                { running.ExpiresAtTick = layer.ExpiresAtTick; running.NextPulseTick = layer.NextPulseTick; }
        }
        else foreach (var running in slot.Layers) running.ExpiresAtTick = tick + options.Duration;
        slot.WorldEpoch = WorldEpoch;
        slot.CommandId = commandId;
        if (effect.Port.Length == 0) return;
        commandResult = CommandResult.Create(commandResult.Status, commandResult.CommitState, commandResult.Code,
            commandResult.Detail, WithPort(commandResult.Outputs, effect.Port, slot.Handle), commandResult.Facts.ToArray());
    }

    /// <summary>The port a step's effect handle is published into: the graph's declared `recipients.handle`. Null
    /// where the row declares none, which is what makes `effect.cancel` a refusal at load rather than a handle that
    /// goes nowhere. The argument is a resolved graph — what a step carries — not a capability row's whole text.</summary>
    internal static string? EffectHandlePort(JsonElement graph)
    {
        if (!graph.TryGetProperty("recipients", out var recipients)) return null;
        if (!recipients.TryGetProperty("handle", out var handle) || handle.ValueKind != JsonValueKind.String) return null;
        var id = handle.GetString()!;
        foreach (var port in RuntimeJson.Rows(graph, "outputs"))
            if (RuntimeJson.Text(port, "id") == id && RuntimeJson.Text(port, "type") == "handle") return id;
        return null;
    }

    /// <summary>The lifetime that port declares; a row that declares an effect handle carries one, and the tag is
    /// what a consuming port's own `handle-lifetime` check reads.</summary>
    internal static string EffectLifetime(JsonElement graph)
    {
        if (EffectHandlePort(graph) is not { } id) return "entity_life";
        foreach (var port in RuntimeJson.Rows(graph, "outputs"))
            if (RuntimeJson.Text(port, "id") == id && port.TryGetProperty("lifetime", out var lifetime)
                && lifetime.ValueKind == JsonValueKind.String) return lifetime.GetString()!;
        return "entity_life";
    }

    /// <summary>A handler's own outputs with one more port added. The rest of the frame is the handler's answer
    /// untouched, so a later step reads one output object exactly as it reads any other.</summary>
    private static JsonElement WithPort(JsonElement outputs, string port, JsonElement value)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (outputs.ValueKind == JsonValueKind.Object)
            foreach (var field in outputs.EnumerateObject()) fields[field.Name] = field.Value;
        fields[port] = value;
        return RuntimeJson.From(fields);
    }

    /// <summary>
    /// Ends one effect instance and calls the module back once for the layers it carries. The callback runs before
    /// the handle is released, so a module that reads the handle it was handed still finds it live; a callback that
    /// throws is reported against its provider and never breaks the tick, because a module's own bug is not the
    /// world's.
    /// </summary>
    private void EndEffect(EffectSlot slot, string reason, long tick)
    {
        if (!effects.Remove(slot.Id)) return;
        effectIdentity.Remove(slot.Identity);
        RestoreEffect(slot, reason, slot.Layers.Count == 0 ? slot.Peak : Math.Max(slot.Peak, slot.Layers.Count));
        if (slot.HandleSlot >= 0) ReleaseHandle(slot.HandleSlot);
        slot.HandleSlot = -1;
    }

    /// <summary>One callback into the module that applied an effect, for the layers that just ended. A module that
    /// registered no restore callback is one whose effect was allowed without one — a periodic one-shot — and there
    /// is nothing to undo.</summary>
    private void RestoreEffect(EffectSlot slot, string reason, int layers)
    {
        if (!registry.EffectRestores.TryGetValue(slot.BindingId, out var restore) || slot.Handle.ValueKind == JsonValueKind.Undefined) return;
        var context = new RuntimeEffectContext(slot.Handle, reason, layers, CurrentTick, slot.WorldEpoch,
            slot.PlanId, slot.NodeId, slot.BindingId, slot.CapabilityId, slot.CommandId, slot.Subject);
        try { restore.Restore(context); }
        catch (Exception error)
        {
            // The record point is the shared one for a module callback the kernel invoked: the detail names
            // what failed, since an effect restore has no record code of its own in the contract.
            LogObserverFailed(slot.ProviderId,
                "effect-restore-failed " + reason + " " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>
    /// Ends one effect early because its own receiver did something the card named in `end_on`. Nothing is
    /// dispatched for the event that ends it: the trigger the card named is read for its subject and its binding
    /// alone, and an effect ends as its own kind of ending rather than as a cancellation the author did not ask
    /// for. Called once per dispatch, before the walk, so a dispatch that both ends an effect and fires the very
    /// entry point that applied it sees an effect that has already ended.
    /// </summary>
    private void EndEffectOn(RuntimeEvent value)
    {
        if (effects.Count == 0) return;
        foreach (var slot in effects.Values.ToArray())
        {
            if (!slot.EndsOn.Contains(value.BindingId)) continue;
            if (!EndsOnSubject(slot, value.Outputs)) continue;
            EndEffect(slot, EffectEndReasons.Expired, CurrentTick);
        }
    }

    /// <summary>Whether the event that just arrived is about one of an effect's own receivers: the same entity, or
    /// one of the same kind, which is what the load-time coverage check left to be decided here.</summary>
    private static bool EndsOnSubject(EffectSlot slot, JsonElement outputs)
    {
        if (slot.Subject.Count == 0 || outputs.ValueKind != JsonValueKind.Object) return false;
        foreach (var field in outputs.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.Object && field.Value.TryGetProperty("id", out var single))
            {
                if (single.ValueKind == JsonValueKind.String && Names(slot, single.GetString()!)) return true;
                continue;
            }
            if (field.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var element in field.Value.EnumerateArray())
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var many)
                    && many.ValueKind == JsonValueKind.String && Names(slot, many.GetString()!)) return true;
        }
        return false;
    }

    private static bool Names(EffectSlot slot, string id)
    {
        var kind = RuntimeJson.KindOf(id);
        foreach (var entity in slot.Subject)
            if (entity.Id == id || RuntimeJson.KindOf(entity.Id) == kind) return true;
        return false;
    }

    /// <summary>
    /// Runs one effect layer's action again, inside the kernel's own advance and without dispatching an event: the
    /// frame the card was applied with is re-run through the same handler, with a command identity of its own so a
    /// pulse is a work item the replay ledger can tell apart from the application that opened it. A handler that
    /// refuses leaves the layer to end on its duration alone, which is what a period that nobody has to hold is.
    /// </summary>
    private void PulseEffect(EffectSlot slot)
    {
        if (!registry.Bindings.TryGetValue(slot.BindingId, out _)) return;
        if (!modules.TryGetValue(slot.ProviderId, out var generation) || !IsRegistered(slot.ProviderId, generation)) return;
        if (!registry.Handlers.TryGetValue(slot.BindingId, out var handler)) return;
        var step = FindEffectStep(slot);
        if (step == null) return;
        var commandId = slot.CommandPrefix + "," + RuntimeJson.Quote(slot.PlanId) + "," + RuntimeJson.Quote(slot.NodeId)
            + ",pulse:" + (++slot.Pulses);
        var plan = new RuntimeLogPlan { PlanId = slot.PlanId, ResourceId = slot.ResourceId, ResourceRevision = slot.ResourceRevision };
        var context = new CommandContext(slot.Origin, CurrentTick, commandId, slot.PlanId, slot.ResourceId,
            slot.ResourceRevision, slot.NodeId, step.Parameters, slot.Inputs, isHost: true, EntityInstance)
        {
            EffectHandle = slot.Handle,
            EffectStacks = slot.Layers.Count,
            EffectAddLayer = false
        };
        var origin = new StepOrigin(slot.Origin, in plan, slot.NodeId);
        CommandResult result;
        LogStepStarted(in origin, slot.ProviderId, slot.NodeId, slot.BindingId, commandId, "action");
        try { result = NormalizeInvokedResult(handler(context)); }
        catch (RuntimeContractException error) { result = CommandResult.Rejected(error.Code, CommandResult.TruncateDetail(error.Message)); }
        catch (Exception error) { result = CommandResult.FailedUnknown("handler-exception", CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message)); }
        LogStepFinished(in origin, slot.ProviderId, slot.NodeId, slot.BindingId, commandId, "action", in result);
        pulseReceipts?.Add(new CommandReceipt(commandId, slot.Origin.EventId, slot.Origin.CauseId,
            slot.Origin.RootEventId ?? slot.Origin.EventId, slot.PlanId, slot.ResourceId, slot.ResourceRevision,
            slot.NodeId, slot.BindingId, WorldEpoch, CurrentTick, result));
        if (result.Facts.Count > 0 && pulseFacts.TryGetValue(slot.BindingId, out var caused))
            PublishConfirmedFacts(result, slot.BindingId, caused, CurrentTick, pulseEvents!);
    }

    /// <summary>The step a live effect re-runs: its own plan and node, read from the plan the instance belongs to.
    /// The instance names the step that opened it, not the entry point that started the walk, so the search is over
    /// every entry's steps. A plan that unloaded between two ticks ends its effects first, so this answers for every
    /// live instance.</summary>
    private ResolvedStep? FindEffectStep(EffectSlot slot)
    {
        if (!plans.TryGetValue(slot.PlanId, out var loaded)) return null;
        foreach (var entry in loaded.Plan.Entries)
            foreach (var step in entry.Steps)
                if (step.NodeId == slot.NodeId && step.BindingId == slot.BindingId) return step;
        return null;
    }

    /// <summary>Ends every effect one plan owns: a released plan's effects are undone and their handles spent, so
    /// nothing keeps running for a card that is no longer loaded.</summary>
    private void ReclaimEffectPlan(string planId, long tick)
    {
        foreach (var slot in effects.Values.Where(x => x.PlanId == planId).ToArray())
            EndEffect(slot, EffectEndReasons.Reclaimed, tick);
    }

    /// <summary>Ends every effect one provider owns: the module is going away, so its native state is restored
    /// before its registration is.</summary>
    private void ReclaimEffectProvider(string provider, long tick)
    {
        foreach (var slot in effects.Values.Where(x => x.ProviderId == provider).ToArray())
            EndEffect(slot, EffectEndReasons.Reclaimed, tick);
    }

    /// <summary>Ends every effect, at a world boundary or a stop: the handles die with the world either way, and
    /// the modules are told before the table they were cast from is cleared.</summary>
    private void ReclaimEffects(long tick)
    {
        foreach (var slot in effects.Values.ToArray()) EndEffect(slot, EffectEndReasons.Reclaimed, tick);
        effects.Clear(); effectIdentity.Clear();
    }

    /// <summary>
    /// The per-tick effect pass, run where the other lifetime passes run. Three things happen in one pass, in this
    /// order: an instance whose handle or recipients are gone ends as `reclaimed`, because a native write onto a
    /// life that ended has nothing left to undo; a layer whose period has come runs the action again; and a layer
    /// whose duration has run out ends — on its own for an instance whose layers each keep a clock, and with the
    /// whole instance for the one clock the other reapplies share. The comparison is the same rule the trigger
    /// card's cooldown uses — the charge is a tick and the window is exactly its length — so an effect of N ticks
    /// applied in tick T is over in tick T+N.
    /// </summary>
    private void CleanEffectLifetimes()
    {
        if (effects.Count == 0) return;
        foreach (var slot in effects.Values.ToArray())
        {
            if (!effects.ContainsKey(slot.Id)) continue;
            if (slot.HandleSlot < 0 || handleSlots.Count <= slot.HandleSlot
                || handleSlots[slot.HandleSlot] is not { Active: true } handle || handle.EffectId != slot.Id)
            {
                // The handle went away under the effect — a provider released it, or the pool dropped it with the
                // life its native object belonged to. The instance ends with it rather than staying as a record of
                // something nothing can cancel.
                EndEffect(slot, EffectEndReasons.Reclaimed, CurrentTick);
                continue;
            }
            if (slot.Subject.Count != 0 && !SubjectCurrent(slot.Subject)) { EndEffect(slot, EffectEndReasons.Reclaimed, CurrentTick); continue; }
            for (var i = slot.Layers.Count - 1; i >= 0; i--)
            {
                var layer = slot.Layers[i];
                if (layer.NextPulseTick >= 0 && CurrentTick >= layer.NextPulseTick)
                {
                    // A period that was slept through is skipped rather than replayed: the next pulse is one
                    // interval after the one that ran, so a long gap costs one re-run and not a burst.
                    layer.NextPulseTick = CurrentTick + slot.Options.Interval!.Value;
                    PulseEffect(slot);
                }
                if (CurrentTick < layer.ExpiresAtTick) continue;
                if (slot.Options.IndependentLayers)
                {
                    // Each layer of an independent stack is undone by itself, so the module hears one restore per
                    // layer rather than one for the set. The instance is its layers: once the last one is undone
                    // there is no clock left to keep, so the handle goes back to the pool and the record is
                    // forgotten — without a second restore, because every layer already had its own.
                    slot.Layers.RemoveAt(i);
                    RestoreEffect(slot, EffectEndReasons.Expired, 1);
                    if (slot.Layers.Count == 0)
                    {
                        effects.Remove(slot.Id); effectIdentity.Remove(slot.Identity);
                        if (slot.HandleSlot >= 0) ReleaseHandle(slot.HandleSlot);
                        slot.HandleSlot = -1;
                    }
                    continue;
                }
                EndEffect(slot, EffectEndReasons.Expired, CurrentTick);
                break;
            }
        }
    }

    /// <summary>Whether every recipient of one effect is still current. A reference whose own resolver answers no
    /// is a life that ended; one whose kind has no resolver at all is not this kernel's to judge, so it counts as
    /// current and the instance lives out its own duration.</summary>
    private bool SubjectCurrent(IReadOnlyList<EntityReference> subject)
    {
        foreach (var entity in subject)
        {
            var kind = RuntimeJson.KindOf(entity.Id);
            if (!registry.Resolvers.TryGetValue(kind, out var resolver)) continue;
            bool current;
            try { current = resolver.Resolve(entity); }
            catch (Exception) { current = false; }
            if (!current) return false;
        }
        return true;
    }
}
