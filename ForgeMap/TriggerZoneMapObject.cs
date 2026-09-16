using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>
/// The trigger-zone half of the map-object provider. A trigger zone is one more category of `gtfo.map_object`: it
/// has an address, an identity in the one namespace, an observation and a position, and the two facts it publishes
/// are map-object facts — the event's subject is the zone, so a plan is hung on the zone itself and the existing
/// `map-object` attachment matcher decides which zone a behaviour belongs to. No second mount kind, no second
/// identity namespace, and no resource kind for a volume nobody resolves through a resource port.
///
/// This half is a partial declaration rather than a second module because there is exactly one map-object provider
/// and one publication path for it. The declared half composes the definition; this half adds the zone category to
/// it, owns the zone table's reads, and publishes the two edges the judging tick produces through the same
/// registration and under the same guards the door and terminal facts are published under.
/// </summary>
public sealed partial class MapObjectModule
{
    /// <summary>The zones this module addresses. A module that was composed without zones answers for none of
    /// them rather than refusing to exist: the zone category is optional to this provider exactly as the packages
    /// that declare zones are optional to the install.</summary>
    private TriggerZoneSource _zoneSource = new();

    /// <summary>The same registration, composed with the zones' own reader and category. The zone source is handed
    /// over before <see cref="Register"/> because every category is declared on the one definition at registration
    /// time; a module that is never given one registers the empty table and publishes no zone fact.</summary>
    public MapObjectModule(RuntimeKernel kernel, RuntimeLogLevel logLevel, IMapObjectSource doors,
        IMapObjectSource terminals, Func<string, string, object?> resolve, Func<object, EntityReference?>? players,
        Func<bool> authority, Action<string> report, Action<string> log, Func<MapLevelReference?>? currentLevel = null,
        Func<object, object?>? hit = null, TriggerZoneSource? zones = null)
        : this(kernel, logLevel, doors, terminals, resolve, players, authority, report, log, currentLevel, hit)
        => _zoneSource = zones ?? new TriggerZoneSource();

    /// <summary>The zone table this module addresses.</summary>
    public TriggerZoneSource Zones
    {
        get
        {
            CheckThread();
            return _zoneSource;
        }
    }

    /// <summary>
    /// One membership edge of one zone: the target walked into the volume or out of it. The zone is the fact's
    /// subject, re-read from the zone table exactly as a door's status is re-read from its instance, and the
    /// payload carries the zone entity and the target entity. The answer is the event this half published, or
    /// null when the edge reached the kernel as no fact at all: queued and ignored are both an edge the kernel
    /// accepted — the second because no plan hangs on the binding right now — while a zone this module does not
    /// hold, a client's own tick and a refused publication publish nothing, which is the same answer a door that
    /// no longer reads its address gets.
    /// </summary>
    public RuntimeEvent? ZoneMembership(string fact, TriggerZone zone, EntityReference target, long transition)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(target);
        if (TriggerZoneAddress.Of(zone.Id) is not { } address || !ReferenceEquals(_zoneSource.ById(zone.Id), zone))
        {
            ReportOnce("not-held:" + zone.Id, "map-object trigger-zone fact refused: zone `" + zone.Id + "` is not this provider's own record.");
            return null;
        }
        if (_disposed || _registration is not { IsRegistered: true } registration)
        {
            ReportOnce("unregistered", "map-object trigger-zone fact refused: the provider is not registered.");
            return null;
        }
        if (_kernel.StartupState != RuntimeStartupState.Ready)
        {
            ReportOnce("not-ready", "map-object trigger-zone fact refused: the runtime is not ready.");
            return null;
        }
        // The same guard the other map-object facts carry: the host publishes map-object state and a client does
        // not, so a client's own tick could never reach here with a fact to send.
        if (!_authority())
        {
            ReportOnce("client:" + address,
                "map-object fact observed on a non-authoritative peer: the host publishes map-object state, a client does not.");
            return null;
        }
        var subject = new MapObjectSubject(EntityId(address), address, _zoneSource, zone);
        var outputs = Payload(
            ("target", RuntimeJson.From(target)),
            ("zone", RuntimeJson.From(subject.Reference)));
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this
        // call is about to build, so the event value is never built.
        if (Unsubscribed(TriggerZoneContract.BindingOf(fact))) return null;
        var value = new RuntimeEvent(
            EntityKind + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
                + ":" + address + ":" + target.Id + ":" + transition.ToString(CultureInfo.InvariantCulture),
            TriggerZoneContract.BindingOf(fact), _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick),
            Scope(), outputs);
        DispatchResult result;
        try { result = registration.Publish(value); }
        catch (Exception error)
        {
            ReportOnce("publish:" + fact + ":" + address, "map-object trigger-zone fact threw: " + error.GetType().Name);
            return null;
        }
        if (result.Status is "queued" or "ignored")
        {
            if (result.Status == "queued") _publishedFacts++;
            return value;
        }
        if (result.Status == "rejected")
            ReportOnce("publish:" + fact + ":" + result.Code, "map-object trigger-zone fact rejected: " + result.Code);
        return null;
    }
}
