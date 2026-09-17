using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;
using GameData;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the three world-event rows: the one native entry the condition action reaches, and the
/// two trigger components a level's world event objects carry.
///
/// <b>The action.</b> `eWardenObjectiveEventType.SetWorldEventCondition` (19) through
/// `WorldEventManager.ExecuteEvent(WardenObjectiveEventData)` is the same entry every vanilla mount point uses: the
/// struct's `Condition` member is a `WorldEventConditionPair` — `ConditionIndex` plus `IsTrue` — and the executor
/// carries it to every client through the manager's own state replicator. The condition slots are the machine's own
/// (`pWorldEventManagerState.WorldEventConditions`, `WORLD_EVENT_CONDITION_COUNT` = 16), so an index outside that
/// range is refused before the call rather than truncated into a slot the author did not name. Nothing here writes
/// the condition byte array: `WorldEventManager.SetCondition` exists, but it writes one machine's copy outside the
/// replicator, which is exactly the write this layer must not make.
///
/// <b>The triggers.</b> The two components are `LG_InteractWorldEventTrigger` (an `Interact_Timed`) and
/// `LG_LookatWorldEventTrigger` (an `ICameraRayInteractor`). Both carry a `WorldEventTrigger_Sync`, and both
/// expose the game's own activation entries: `Trigger(SNet_Player, Item)` for the press and `ResetTrigger` for the
/// reusable reset. The patches below read those two entries and their results — a `Trigger` that returned false
/// did not activate anything and publishes nothing — so the fact is the activation the game performed. Both
/// patches are postfixes on non-virtual bodies `SNet_Replication`'s master side calls, which is why the host guard
/// inside is the whole authority rule: a client applies replicated state and must not publish an activation it did
/// not decide.
///
/// A trigger's own identity is its `LG_WorldEventObject.WorldEventObjectKey`, which is the same text a
/// `WardenObjectiveEventData` names its target with (`WorldEventObjectFilter`) — so a plan wires the key the
/// action's own authored filter spells, and the zone and position travel beside it for the filters a key cannot
/// answer.
///
/// The patches are declared here rather than added to `MapNativeHooks.cs` because that file, `MapPluginSession.cs`
/// and `MapObjectHooks.cs` are shared registration points; `MapNativeHooks.Types` carries the three types below,
/// which is the one line this family needs from the integration batch.</summary>
internal sealed class WorldEventFacts
{
    /// <summary>The one installed half. A Harmony callback is static, so the session hands its kernel and
    /// registration to the half through this slot before the first plan runs; a callback that fires before the
    /// attach publishes nothing rather than throwing through a patch.</summary>
    internal static WorldEventFacts? Current { get; private set; }

    private readonly RuntimeKernel _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;
    private readonly Func<bool> _authority;
    private readonly Action<string> _report;
    private readonly HashSet<string> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Harmony _harmony;
    private bool _faulted;
    private bool _disposed;

    private WorldEventFacts(RuntimeKernel kernel, RuntimeModuleHandle registration, Func<bool> authority,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _harmony = new Harmony("NAinfini.ForgeMap.world-event");
    }

    /// <summary>Installs the one half the static callbacks answer through and patches the two trigger components,
    /// answering the half so the session that owns it can detach exactly what it installed. The patches go through
    /// this half's own Harmony instance rather than the package's because the package's type list belongs to
    /// `MapNativeHooks`, which is a shared registration point; the instance is named, so `Dispose` below releases
    /// exactly these four patches.</summary>
    internal static WorldEventFacts Attach(RuntimeKernel kernel, RuntimeModuleHandle registration,
        Func<bool> authority, Action<string> report)
    {
        var half = new WorldEventFacts(kernel, registration, authority, report);
        half.Patch(typeof(InteractTriggeredReadback), typeof(InteractResetReadback),
            typeof(LookatTriggeredReadback), typeof(LookatResetReadback));
        Current = half;
        return half;
    }

    /// <summary>Releases one installation: the static slot first, so a callback already inside the patches finds
    /// nothing, then the four patches themselves.</summary>
    internal static void Detach(WorldEventFacts half)
    {
        ArgumentNullException.ThrowIfNull(half);
        if (ReferenceEquals(Current, half)) Current = null;
        half.Dispose();
    }

    internal bool Faulted => _faulted;

    /// <summary>How many distinct trigger states this half has published, for a case to assert that a repeated
    /// activation is not news.</summary>
    internal int Published { get; private set; }

    // ---- the action ---------------------------------------------------------------------------------

    /// <summary>The condition action: it validates nothing of its own beyond the host guard, which the session's
    /// dispatch already answered — the two decisions the row makes live in `WorldEventContract.TryCondition`, in
    /// the game-independent assembly, so a case can drive every refusal without a game. What is left here is the
    /// one native call, the struct it takes, and the outcome a caller reads.</summary>
    internal static CommandResult Condition(CommandContext context)
    {
        if (!WorldEventContract.TryCondition(context, out var request, out string? code))
            return WorldEventContract.Refused(code!);
        try
        {
            if (WorldEventManager.Current == null)
                return WorldEventContract.Refused(WorldEventContract.AuthorityCode);
            var data = new WardenObjectiveEventData { Type = eWardenObjectiveEventType.SetWorldEventCondition };
            data.Condition = new WorldEventConditionPair { ConditionIndex = request.Index, IsTrue = request.Solved };
            WorldEventManager.ExecuteEvent(data, 0f);
        }
        catch (Exception error)
        {
            // The executor may already have written the replicated condition before it threw, so the outcome is
            // reported as unknown: claiming nothing happened is a claim this layer cannot make.
            Current?.ReportOnce("commit:" + request.Index.ToString(CultureInfo.InvariantCulture),
                "world event condition action threw: " + error.GetType().Name);
            return WorldEventContract.Failed("native-commit-exception");
        }
        return CommandResult.Succeeded(WorldEventContract.Outputs(request));
    }

    // ---- the two trigger rows -----------------------------------------------------------------------

    /// <summary>One activation of the interact trigger. It publishes the object's own key, the trigger state
    /// `true`, the player whose interaction did it, the zone the object stands in and its world position.</summary>
    internal void InteractTriggered(LG_InteractWorldEventTrigger trigger, SNet_Player? source)
        => Trigger((trigger == null ? null : trigger.gameObject), WorldEventContract.WorldEventInteractFact,
            WorldEventContract.TriggeredMoment, source);

    /// <summary>The interact trigger's reusable reset, which is the same row's other moment: the trigger state
    /// goes back to `false`.</summary>
    internal void InteractReset(LG_InteractWorldEventTrigger trigger, SNet_Player? source)
        => Trigger((trigger == null ? null : trigger.gameObject), WorldEventContract.WorldEventInteractFact,
            WorldEventContract.ResetMoment, source);

    /// <summary>One activation of the look-at trigger, carrying one port more than the interact row: the trigger's
    /// own maximum look-at distance, which is the field this component kind exists for.</summary>
    internal void LookatTriggered(LG_LookatWorldEventTrigger trigger, SNet_Player? source)
        => Trigger((trigger == null ? null : trigger.gameObject), WorldEventContract.WorldEventLookatFact,
            WorldEventContract.TriggeredMoment, source);

    /// <summary>The look-at trigger's own reset.</summary>
    internal void LookatReset(LG_LookatWorldEventTrigger trigger, SNet_Player? source)
        => Trigger((trigger == null ? null : trigger.gameObject), WorldEventContract.WorldEventLookatFact,
            WorldEventContract.ResetMoment, source);

    /// <summary>One moment of one trigger object. The object's own key is the identity, so the two trigger
    /// components of one object publish under one subject and an author filters on the text a
    /// `WardenObjectiveEventData` names its target with. An object this provider cannot read a key for publishes
    /// nothing: a fact with an invented identity is one no plan could name. A repeated moment is not news, so the
    /// state key is the moment itself: a second activation of an already-triggered object publishes nothing, and
    /// the reset that follows it is a new transition.</summary>
    private void Trigger(GameObject? owner, string fact, string moment, SNet_Player? source)
    {
        if (_disposed || _faulted || !_authority()) return;
        if (owner == null) return;
        var world = owner.GetComponent<LG_WorldEventObject>();
        if (world == null || world.WasCollected) return;
        string? key = world.WorldEventObjectKey;
        if (string.IsNullOrEmpty(key)) return;
        bool triggered = WorldEventContract.IsTriggered(moment);
        double? distance = fact == WorldEventContract.WorldEventLookatFact ? LookatDistance(owner) : null;
        if (!_states.Add(key + "|" + fact + "|" + moment)) return;
        string binding = WorldEventContract.Binding(WorldEventContract.TriggerCapability(fact));
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this call
        // is about to build, so the event value is never built. The state above is still remembered, because a
        // trigger seen while nobody listened is one this half has already reported.
        if (_gates.TryGetValue(binding, out var gate) && !gate.HasSubscribers) return;
        var entity = source == null ? null : PlayerReference(source);
        double[]? position = Position(owner);
        var outputs = Payload(
            ("key", RuntimeJson.From(key)),
            ("triggered", RuntimeJson.From(triggered)),
            ("source", entity is { } player ? RuntimeJson.From(player) : (JsonElement?)null),
            ("zone", ZoneReference(owner) is { } zone ? RuntimeJson.From(zone) : (JsonElement?)null),
            ("position", position is { } point ? RuntimeJson.From(point) : (JsonElement?)null),
            ("lookat_distance", distance is { } meters ? RuntimeJson.From(meters) : (JsonElement?)null));
        var published = new RuntimeEvent(
            "gtfo.world_event:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture) + ":" + key + ":"
                + (triggered ? "1" : "0"),
            binding, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick),
            "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture), outputs);
        var result = _registration.Publish(published);
        if (result.Status == "queued") { Published++; return; }
        if (result.Status == "rejected")
            ReportOnce("publish:" + fact + ":" + moment, "world event trigger fact rejected: " + result.Code);
    }

    /// <summary>The look-at trigger's own serialized distance, read through the component rather than restated,
    /// so a plan filters on the value the level authored.</summary>
    private static double? LookatDistance(GameObject owner)
    {
        var lookat = owner.GetComponent<LG_LookatWorldEventTrigger>();
        return lookat == null || lookat.WasCollected ? null : lookat.GetCamHoverMaxDistance;
    }

    /// <summary>The zone a world event object stands in, in the `gtfo.zone` grammar every other zone reader of
    /// this package answers with. An object outside a zone's own area is published without the port rather than
    /// with an invented zone.</summary>
    private static ResourceRef? ZoneReference(GameObject owner)
    {
        var area = owner.GetComponentInParent<LG_Area>();
        var zone = area == null || area.WasCollected ? null : area.m_zone;
        if (zone == null || zone.WasCollected) return null;
        if (ZoneIndex.Coordinates(zone) is not { } coordinates) return null;
        return new ResourceRef(RuntimeZones.ResourceKind,
            RuntimeZones.Id(coordinates.Dimension, coordinates.Layer, coordinates.Zone));
    }

    /// <summary>One world position, as the three components the `vector3` port carries. The value is read from the
    /// object's own transform and is never a guess about where the trigger was authored.</summary>
    private static double[]? Position(GameObject owner)
    {
        var transform = owner.transform;
        if (transform == null) return null;
        Vector3 point = transform.position;
        return new[] { (double)point.x, (double)point.y, (double)point.z };
    }

    /// <summary>The player entity one activation names, through the player half's own identity: a callback the
    /// game raises carries the `SNet_Player`, and `PlayerIdentityModule` is the half that already resolves that
    /// player to the reference its player rows publish under. A player the identity half cannot resolve yields no
    /// port rather than a partial reference.</summary>
    internal static EntityReference? PlayerReference(SNet_Player? source)
        => source == null || source.WasCollected ? null : PlayerIdentityModule.Current?.ReferenceOf(source);

    /// <summary>One published payload, port by port. A port whose value the native side could not read is left
    /// out instead of being published as a JSON null: an absent port is how this framework says "not
    /// observable", where a null would claim the port was written.</summary>
    private static JsonElement Payload(params (string Id, JsonElement? Value)[] ports)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in ports) if (value is { } present) payload[id] = present;
        return RuntimeJson.From(payload);
    }

    /// <summary>Runs one trigger callback under this half's own guard: a callback that fails disables the producer
    /// once instead of leaving a broken one running for the rest of the session, which is the rule every native
    /// callback of this package follows.</summary>
    internal void Guard(Action<WorldEventFacts> callback)
    {
        if (_disposed || _faulted) return;
        try { callback(this); }
        catch (Exception error)
        {
            _faulted = true;
            ReportOnce("guard", "World event trigger observation disabled until restart after native callback "
                + "failure: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void Patch(params Type[] types)
    {
        foreach (var type in types) _harmony.CreateClassProcessor(type).Patch();
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _report(message);
    }

    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _states.Clear();
        _harmony.UnpatchSelf();
    }
}

// The four patches below are postfixes on the game's own activation entries, one pair per component kind. Both
// entry results are read: a `Trigger` that returned false activated nothing, and publishing an activation the game
// refused would be a fact no level produced.

[HarmonyPatch(typeof(LG_InteractWorldEventTrigger),
    nameof(LG_InteractWorldEventTrigger.Trigger))]
internal static class InteractTriggeredReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_InteractWorldEventTrigger __instance, SNet_Player source, bool __result)
    {
        if (!__result) return;
        WorldEventFacts.Current?.Guard(facts => facts.InteractTriggered(__instance, source));
    }
}

[HarmonyPatch(typeof(LG_InteractWorldEventTrigger),
    nameof(LG_InteractWorldEventTrigger.ResetTrigger))]
internal static class InteractResetReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_InteractWorldEventTrigger __instance, SNet_Player source, bool __result)
    {
        if (!__result) return;
        WorldEventFacts.Current?.Guard(facts => facts.InteractReset(__instance, source));
    }
}

[HarmonyPatch(typeof(LG_LookatWorldEventTrigger),
    nameof(LG_LookatWorldEventTrigger.Trigger))]
internal static class LookatTriggeredReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_LookatWorldEventTrigger __instance, SNet_Player source, bool __result)
    {
        if (!__result) return;
        WorldEventFacts.Current?.Guard(facts => facts.LookatTriggered(__instance, source));
    }
}

[HarmonyPatch(typeof(LG_LookatWorldEventTrigger),
    nameof(LG_LookatWorldEventTrigger.ResetTrigger))]
internal static class LookatResetReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_LookatWorldEventTrigger __instance, SNet_Player source, bool __result)
    {
        if (!__result) return;
        WorldEventFacts.Current?.Guard(facts => facts.LookatReset(__instance, source));
    }
}
