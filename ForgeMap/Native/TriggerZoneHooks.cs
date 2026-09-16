using System;
using System.Collections.Generic;
using ForgeMap;
using ForgeRuntime.Framework;
using HarmonyLib;
using Player;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the trigger zones: the package document read once at start, the zone table the map-object
/// half addresses, the blocking bodies, and the one tick that drives the judging.
///
/// Nothing here registers an entity namespace or a resource kind of its own. A zone is one category of the map
/// object's own namespace, so this half hands its table to the map-object module — which is what makes a plan hang
/// on a zone through the existing `map-object` mount — and the facts themselves are published by that module.
///
/// A document's pose is room-local, so the table handed over is the placed one: the level this world is running
/// places the zones it owns through the room resolver the host composed, once per level, when the level is known —
/// which is after the level's own generation, because that is when a room's world pose exists at all. A zone whose
/// room the resolver cannot name is refused by name and left out of the table, so no plan is dispatched for a
/// volume that would have been judged in coordinates nobody converted. The authored table itself is never
/// mutated: placement is a fold, and the next level places its own zones from the same document.
///
/// The tick is driven by the local player's own fixed update rather than by a component of this package's own: the
/// runtime advances its kernel from the same Unity step, and a player's fixed update is the one call that exists on
/// every machine exactly once for the machine's own player. The judgment itself is the host's — the tick is guarded
/// before any world read — while the blocking bodies are every machine's own local physics.
/// </summary>
internal sealed class TriggerZoneSession : IDisposable
{
    /// <summary>The one live session, reached by the tick patch. A package registers once and a hook is installed
    /// once, so there is exactly one.</summary>
    internal static TriggerZoneSession? Current { get; private set; }

    private readonly TriggerZoneSource _source;
    private readonly TriggerZoneModule _module;
    private readonly TriggerZoneColliders _colliders;
    private readonly TriggerZoneRoomLookup? _rooms;
    private readonly Func<MapLevelReference?> _level;
    private readonly Func<long> _tick;
    private readonly Action<string> _report;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private IReadOnlyList<TriggerZone> _authored = Array.Empty<TriggerZone>();
    private MapLevelReference? _placedLevel;
    private long _lastTick = long.MinValue;
    private bool _disposed;

    private TriggerZoneSession(TriggerZoneSource source, TriggerZoneModule module, TriggerZoneColliders colliders,
        TriggerZoneRoomLookup? rooms, Func<MapLevelReference?> level, Func<long> tick, Action<string> report)
    {
        _source = source;
        _module = module;
        _colliders = colliders;
        _rooms = rooms;
        _level = level;
        _tick = tick;
        _report = report;
    }

    /// <summary>Reads this install's zone documents, hands the table to the map-object module this session's
    /// provider is composed with — the module must receive it before it registers, because every category is
    /// declared on the one definition at registration time — and builds the bodies of this world's blocking zones.
    /// A document that does not read is reported and left out; it never stops the package from loading.</summary>
    /// <param name="rooms">The one resolver that can name an authored room in the level the game generated, or
    /// null when this build composed none. Every zone of the running level is then refused by name rather than
    /// judged at a pose nobody converted.</param>
    internal static TriggerZoneSession Start(RuntimeKernel kernel, MapObjectModule mapObjects, Func<bool> authority,
        Func<MapLevelReference?> level, TriggerZoneRoomLookup? rooms, Action<string> report, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(mapObjects);
        ArgumentNullException.ThrowIfNull(authority); ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(log);
        var source = mapObjects.Zones;
        var module = new TriggerZoneModule(kernel, mapObjects.Registration, mapObjects, authority, level, report);
        var session = new TriggerZoneSession(source, module, new TriggerZoneColliders(report), rooms, level,
            () => kernel.CurrentTick, report);
        try
        {
            session._authored = TriggerZoneData.Load(report);
            source.Load(session._authored);
            Current = session;
            log("trigger zones: " + source.Zones.Count + " zone(s) loaded from this install's package documents.");
            session.Place();
            session.SyncBodies();
            return session;
        }
        catch
        {
            Current = null;
            session.Dispose();
            throw;
        }
    }

    /// <summary>One tick of this machine. The patch that drives it runs once per local player per fixed update, so
    /// the kernel's own tick number is the throttle: one judgment per tick, whoever called it first.</summary>
    internal void Tick()
    {
        if (_disposed) return;
        var tick = _tick();
        if (_lastTick == tick) return;
        _lastTick = tick;
        Place();
        _module.Tick();
        SyncBodies();
    }

    /// <summary>Places the zones of the level this world is running, once per level: the room resolver answers each
    /// authored room's world pose, and a zone it cannot name is refused by name and left out of the judging table.
    /// A world with no level yet places nothing, because no room of it has a pose to resolve.</summary>
    private void Place()
    {
        if (_disposed) return;
        if (_level() is not { } level || _placedLevel == level) return;
        _placedLevel = level;
        _source.Load(TriggerZonePlacement.Of(_authored, level, _rooms, ReportOnce));
    }

    /// <summary>The blocking bodies of the level this world is running. Every machine keeps them in step with the
    /// level it is really in, so a body of a level that ended is never left standing in the next one.</summary>
    private void SyncBodies() => _colliders.Sync(_source.Zones, _level());

    /// <summary>One refusal, once. Placement runs again for every level, so a document with a room no level can
    /// name would otherwise repeat the same line on every tick.</summary>
    private void ReportOnce(string message)
    {
        if (_reported.Add(message)) _report(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        _colliders.Dispose();
        _module.Dispose();
    }
}

/// <summary>
/// The one Harmony patch of this family: after the local player's own fixed update, one zone tick. Every other
/// player's copy of this call belongs to another body on this machine, so it returns before touching the module —
/// which is also what keeps a client's fixed update from spending the query budget the host's plans need.
/// </summary>
[HarmonyPatch(typeof(PlayerInteraction), nameof(PlayerInteraction.FixedUpdate))]
internal static class TriggerZoneTick
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInteraction __instance)
    {
        var owner = __instance.m_owner;
        if (owner == null || !owner.IsLocallyOwned) return;
        TriggerZoneSession.Current?.Tick();
    }
}
