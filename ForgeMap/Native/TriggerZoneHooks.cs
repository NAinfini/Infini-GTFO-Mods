using System;
using System.Collections.Generic;
using ForgeMap;
using ForgeRuntime.Framework;

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
/// The tick is driven by the kernel's own clock, once per advance, rather than by the local player's fixed update:
/// the runtime advances the kernel from the same Unity step, and a player's fixed update exists once per player on
/// the machine. The judgment itself is the host's — <see cref="TriggerZoneModule.Tick"/> is guarded before any
/// world read — while the blocking bodies are every machine's own local physics.
/// </summary>
internal sealed class TriggerZoneSession : IDisposable
{
    private readonly TriggerZoneSource _source;
    private readonly TriggerZoneModule _module;
    private readonly TriggerZoneColliders _colliders;
    private readonly TriggerZoneRoomLookup? _rooms;
    private readonly Func<MapLevelReference?> _level;
    private readonly Func<bool> _subscribed;
    private readonly Action<string> _report;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private IReadOnlyList<TriggerZone> _authored = Array.Empty<TriggerZone>();
    private MapLevelReference? _placedLevel;
    private bool _disposed;

    private TriggerZoneSession(TriggerZoneSource source, TriggerZoneModule module, TriggerZoneColliders colliders,
        TriggerZoneRoomLookup? rooms, Func<MapLevelReference?> level, Func<bool> subscribed, Action<string> report)
    {
        _source = source;
        _module = module;
        _colliders = colliders;
        _rooms = rooms;
        _level = level;
        _subscribed = subscribed;
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
        // A zone nobody listens to is not work: the two facts this half publishes are the only reason to judge a
        // volume, so the clock is taken only while one of their bindings has a subscriber.
        var subscribed = () => mapObjects.HasSubscribers(TriggerZoneContract.BindingOf(TriggerZoneContract.EnteredFact))
            || mapObjects.HasSubscribers(TriggerZoneContract.BindingOf(TriggerZoneContract.ExitedFact));
        var session = new TriggerZoneSession(source, module, new TriggerZoneColliders(report), rooms, level, subscribed, report);
        try
        {
            session._authored = TriggerZoneData.Load(report);
            source.Load(session._authored);
            log("trigger zones: " + source.Zones.Count + " zone(s) loaded from this install's package documents.");
            session.Place();
            session.SyncBodies();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Whether this half has work a clock tick would spend: a zone the running level placed and a
    /// subscriber on one of the two bindings it publishes. A level that placed no zone, and a zone nobody
    /// subscribed to, both answer false — the first because there is nothing to judge, the second because the
    /// judgment would reach nobody.</summary>
    internal bool Pending => !_disposed && _source.Zones.Count > 0 && _subscribed();

    /// <summary>One tick of this machine: the level's zones are placed, the host judges them, and every machine's
    /// blocking bodies follow. The kernel's clock calls it at the cadence of the game's own collision trigger
    /// (`LG_CollisionWorldEventTrigger.COLLISION_CHECK_INTERVAL`, see <see cref="MapClock"/>), so a tick that
    /// arrives less than a cadence after the last one does not exist at all.</summary>
    internal void Tick()
    {
        if (_disposed) return;
        Place();
        _module.Tick();
        SyncBodies();
    }

    /// <summary>The level-facing half of the work, run when a level or its zones may have changed rather than on a
    /// clock tick: this is where a new level's room-local poses become world poses, and it is a native side effect
    /// that belongs to a native stage.</summary>
    internal void Refresh()
    {
        if (_disposed) return;
        Place();
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
        _colliders.Dispose();
        _module.Dispose();
    }
}
