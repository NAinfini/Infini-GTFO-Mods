using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>What one tick did. A refused tick judged nothing and published nothing; `Zones` and `Targets` are what
/// it read, not what the world holds.</summary>
public readonly record struct TriggerZoneTickResult(string Status, string Code, int Zones, int Targets, int Entered, int Exited);

/// <summary>
/// The host half of the trigger zones: every tick it reads the players and the enemies this install can name, tests
/// each of this level's zones against their positions, and publishes an entry or an exit edge for the targets whose
/// membership changed. Nothing is polled on a client: the guard is the first thing a tick checks, so a machine that
/// is not the host makes no world read and spends no query budget a plan may need.
///
/// A tick judges where a target is now and the line it travelled since the last tick, because the two are a cadence
/// apart: a body that crossed a thin volume between them is one entry and one exit, in that order, rather than
/// nothing at all. A line is only a path while the game carried the body along it: a target further from its last
/// judged place than the game can carry one in a beat was put where it is — a warp, this package's own teleport, a
/// fall the level undid — and only where it stands now is judged, so a volume it was placed beyond never fires.
///
/// Membership is the module's own table, one set per zone. An entity that stops being a candidate of its kind — a
/// dead enemy, a player who left — is dropped from the set silently and publishes no exit, because a disappearance
/// is not the same fact as walking out. An entity whose position could not be read keeps the membership it had: a
/// tick that could not observe the world in full publishes nothing rather than a false edge.
///
/// The zones are the ones the map-object half addresses: this module judges the table that half publishes and
/// nothing else, so a volume no plan can mount is a volume this module does not judge either.
/// </summary>
public sealed class TriggerZoneModule : IDisposable
{
    /// <summary>Zones one tick judges. A level with more zones than this is judged up to the cap and reported,
    /// never partially: the zones a tick could not reach keep the membership they had.</summary>
    public const int MaximumZonesPerTick = 64;
    /// <summary>Targets one tick reads. The kernel is the one that enforces it — a candidate set past its own
    /// per-query ceiling is refused by name, and this module keeps the membership it had — so the value is
    /// declared here as the number a reader of this module's reports can compare against.</summary>
    public const int MaximumTargetsPerTick = 256;
    /// <summary>The quickest the game carries a body this module judges, in metres per second: the widest move that
    /// is still a move rather than a placement. It is the one number of this module no dump can hand over — the game
    /// keeps its speeds as data rather than as code. `PlayerLocomotion` carries the speed it is moving at
    /// (`m_lastMoveSpeed` at 0x1B8, dump line 656256), `EnemyLocomotion` the ceiling of its own archetype
    /// (`m_maxMovementSpeed` at 0x1E0, line 663099) and the player's melee lunge the two speeds its attack data
    /// names (`MWS_AttackLight.m_wantedNormalSpeed`/`m_wantedChargeSpeed`, lines 615376-615377), and every one of
    /// them is serialized from a data block this repository cannot read offline; the build states no speed constant
    /// (a pass over every `const float` in the dump finds scroll, explode, blend and line speeds and no movement).
    /// The bound is therefore inferred, with the movement of the game as the ground: a sprint raised by a booster
    /// (`AgentModifier.MovementSpeed`, 250, scales the block's own value), a melee lunge and an enemy charge are
    /// single-digit to low-double-digit metres per second, so 30 m/s leaves the fastest of them at least twice its
    /// own speed as headroom, and a body quicker than that was not carried along the line it appears to have taken.
    /// It judges the same thin crossing one beat at 2 m does — a sprint carrying a body across a wall must stay
    /// visible — which is why the bound sits above that and not at it. This is the value to re-derive in the game
    /// (see `evidence/trigger-zone-natives.json`, "placement-bound").</summary>
    public const double MaximumJudgedSpeed = 30;
    /// <summary>The greatest distance one beat of the game's own movement covers: <see cref="MaximumJudgedSpeed"/>
    /// over the 0.1 second a zone is judged at — `MapClock.TriggerZoneSeconds`, the game's own
    /// `LG_CollisionWorldEventTrigger.COLLISION_CHECK_INTERVAL`, which carries that citation. The distance is
    /// written out rather than multiplied because the two halves cannot share the expression: the clock lives in the
    /// native half of the package and its test project compiles it in as a source without this module beside it.
    /// They are one fact and move together. A target further than this from where the last judgment left it was
    /// placed rather than carried, so the straight line between the two places is not a path it walked and nothing
    /// on it was crossed.</summary>
    public const double MaximumJudgedTravel = 3;
    /// <summary>Zones this module holds for one install.</summary>
    public const int MaximumZones = TriggerZoneManifest.MaximumZones;
    /// <summary>The entity kinds a zone reads, spelled exactly as the providers that own them register them.</summary>
    public const string PlayerKind = "gtfo.player";
    public const string EnemyKind = "gtfo.enemy";

    private readonly RuntimeKernel _kernel;
    private readonly MapObjectModule _mapObjects;
    private readonly Func<bool> _authority;
    private readonly Func<MapLevelReference?> _level;
    private readonly Action<string> _report;
    private readonly RuntimeLifecycleSubscription? _lifecycle;
    /// <summary>One membership set per zone id: the entities this module judged inside it at the last tick it
    /// judged the zone. The reference is kept with the id so an exit names the entity the entry named.</summary>
    private readonly Dictionary<string, Dictionary<string, EntityReference>> _inside = new(StringComparer.Ordinal);
    /// <summary>The place each target was judged at in the last complete tick, one entry per entity that tick read.
    /// The tick after it judges the line from this place to the place the target is at now, which is what makes a
    /// crossing between two judgments observable. A target the last tick did not read — a body that just appeared
    /// in the world, or one whose position was unreadable — has no entry and is judged by where it is alone.</summary>
    private Dictionary<string, double[]> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _transitions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    /// <summary>The level the current membership was judged in. The membership and the level it was read in are
    /// one fact, so a level this tick does not recognize drops the table instead of comparing against it.</summary>
    private MapLevelReference? _judgedLevel;
    private long _publishedFacts;
    private bool _disposed;

    public TriggerZoneModule(RuntimeKernel kernel, RuntimeModuleHandle registration, MapObjectModule mapObjects,
        Func<bool> authority, Func<MapLevelReference?> level, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _mapObjects = mapObjects ?? throw new ArgumentNullException(nameof(mapObjects));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _level = level ?? throw new ArgumentNullException(nameof(level));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        ArgumentNullException.ThrowIfNull(registration);
        // A subscription that cannot be taken leaves no module behind: a member table that outlived its world would
        // report entries of a level that is gone.
        _lifecycle = registration.ObserveLifecycle(OnLifecycle);
    }

    /// <summary>Every edge this module produced, in publication order. The kernel is the real consumer; this is
    /// what a test reads instead of a second publication path.</summary>
    public Action<RuntimeEvent>? EventObserver { get; set; }

    /// <summary>Every fact this module handed to the map-object half and that the kernel queued. An edge nobody
    /// subscribes to is still an edge this module published, so this counts the publication and not the dispatch.</summary>
    public long PublishedFacts => _publishedFacts;

    /// <summary>Whether this module has been disposed. A disposed module judges nothing and reports `idle`.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>The entities this module currently holds inside one zone, in the order they entered. Answers empty
    /// for a zone it does not have.</summary>
    public IReadOnlyList<EntityReference> Inside(string zoneId)
        => _inside.TryGetValue(zoneId, out var members)
            ? Array.AsReadOnly(members.Values.ToArray())
            : Array.Empty<EntityReference>();

    /// <summary>One tick of the host's own judgment. It reads the world at most once per entity kind and once for
    /// the positions of the candidate set, and publishes one fact per target whose membership changed — or whose
    /// travel between this judgment and the last crossed a volume that neither end of it stood in.</summary>
    public TriggerZoneTickResult Tick()
    {
        if (_disposed || !_mapObjects.IsRegistered) return Idle("module-disposed");
        if (_kernel.StartupState != RuntimeStartupState.Ready) return Idle("runtime-not-ready");
        // The client guard is the first world-facing check: a machine that is not the host makes no read at all,
        // which is what keeps a zone tick from spending the query budget the host's own plans need.
        if (!_authority()) return Idle("not-authoritative");
        if (_level() is not { } current) { ClearMembership(); _judgedLevel = null; return Idle("level-unknown"); }
        if (_judgedLevel != current) { ClearMembership(); _judgedLevel = current; }
        var zones = _mapObjects.Zones.Zones;
        // The candidate read is shared by every zone of the tick: one enumeration per kind, one position read for
        // the whole set, so a level with many zones costs the same as one zone.
        var wanted = new List<TriggerZone>(Math.Min(zones.Count, MaximumZonesPerTick));
        var overflow = false;
        foreach (var zone in TriggerZoneContract.ForLevel(zones, current))
        {
            if (wanted.Count == MaximumZonesPerTick) { overflow = true; break; }
            wanted.Add(zone);
        }
        if (overflow)
            ReportOnce("zones:" + MaximumZonesPerTick, "trigger-zone-tick-budget: more than " + MaximumZonesPerTick
                + " zones in this level; the zones past the cap were not judged this tick.");
        if (wanted.Count == 0) return new TriggerZoneTickResult("complete", "no-zones", 0, 0, 0, 0);
        var needPlayers = wanted.Any(zone => zone.Who != TriggerZoneWho.Enemy);
        var needEnemies = wanted.Any(zone => zone.Who != TriggerZoneWho.Player);
        var players = needPlayers ? Read(PlayerKind) : null;
        var enemies = needEnemies ? Read(EnemyKind) : null;
        // A kind this tick could not enumerate at all keeps the members it already had: they were not observed to
        // have left, so they neither exit nor enter again while their kind is unreadable.
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        if (needPlayers && players == null) unknown.Add(PlayerKind);
        if (needEnemies && enemies == null) unknown.Add(EnemyKind);
        var candidates = new List<EntityReference>();
        if (players is { } playerSet) candidates.AddRange(playerSet);
        if (enemies is { } enemySet) candidates.AddRange(enemySet);
        var positions = new Dictionary<string, double[]>(StringComparer.Ordinal);
        // The ids the tick could observe at all: a target that is not here any more left the world, which is not
        // the same fact as walking out of a zone.
        var observable = new HashSet<string>(StringComparer.Ordinal);
        if (players is { } known) foreach (var reference in known) observable.Add(reference.Id);
        if (enemies is { } other) foreach (var reference in other) observable.Add(reference.Id);
        if (candidates.Count != 0)
        {
            var readings = _kernel.InspectEntities(candidates);
            if (!readings.IsComplete)
            {
                ReportOnce("readings:" + readings.Code, "trigger-zone-observation: " + readings.Code
                    + "; no zone membership changed this tick.");
                return new TriggerZoneTickResult("refused", readings.Code, wanted.Count, candidates.Count, 0, 0);
            }
            foreach (var item in readings.Items)
                if (item.Snapshot is { } snapshot && snapshot.Position.Count == 3)
                    positions[item.Reference.Id] = new[] { snapshot.Position[0], snapshot.Position[1], snapshot.Position[2] };
        }
        var entered = 0; var exited = 0;
        foreach (var zone in wanted)
        {
            var members = _inside.TryGetValue(zone.Id, out var held) ? held : new Dictionary<string, EntityReference>(StringComparer.Ordinal);
            var current_ = new Dictionary<string, EntityReference>(StringComparer.Ordinal);
            foreach (var reference in candidates)
            {
                if (!Reacts(zone, reference) || !positions.TryGetValue(reference.Id, out var position)) continue;
                var wasInside = members.ContainsKey(reference.Id);
                var isInside = zone.Contains(position);
                if (isInside)
                {
                    current_[reference.Id] = reference;
                    if (!wasInside) entered += Publish(TriggerZoneContract.EnteredFact, zone, reference) ? 1 : 0;
                    continue;
                }
                if (wasInside)
                {
                    exited += Publish(TriggerZoneContract.ExitedFact, zone, reference) ? 1 : 0;
                    continue;
                }
                // Neither end is inside, but the body may have crossed the whole volume between the two judgments:
                // that is one entry and one exit, in the order the body made them. A body with no place from the
                // last tick is new here and is judged by where it is, which is what the two branches above just did.
                if (!_previous.TryGetValue(reference.Id, out var before)) continue;
                // A body this far from where the last judgment left it was put there rather than carried there, so
                // the line between the two places is not a path it walked and no volume on that line was crossed.
                // The two branches above already published what is true either way: a member observed outside
                // really did leave, and a body that landed inside really did arrive.
                if (Travel(before, position) > MaximumJudgedTravel) continue;
                if (!zone.IntersectsSegment(before, position)) continue;
                entered += Publish(TriggerZoneContract.EnteredFact, zone, reference) ? 1 : 0;
                exited += Publish(TriggerZoneContract.ExitedFact, zone, reference) ? 1 : 0;
            }
            if (unknown.Count != 0)
                foreach (var pair in members)
                    if (unknown.Contains(Kind(pair.Value))) current_[pair.Key] = pair.Value;
            foreach (var pair in members)
            {
                // The edges themselves were published as each candidate was judged. What is left is the member this
                // tick could not judge at all: still a target, but with no position to leave a volume from.
                if (current_.ContainsKey(pair.Key) || !observable.Contains(pair.Key)) continue;
                if (positions.ContainsKey(pair.Key)) continue;
                if (players != null && enemies != null)
                    ReportOnce("unread:" + pair.Key, "trigger-zone-observation: " + pair.Key
                        + " is a current target whose position could not be read; no exit was published for it.");
            }
            if (current_.Count == 0) _inside.Remove(zone.Id); else _inside[zone.Id] = current_;
        }
        // The places just judged are where the next tick's segments start. A target this tick did not read has no
        // place here, so the next tick treats it as a body that just appeared and judges it by where it is then.
        _previous = positions;
        return new TriggerZoneTickResult("complete", "zones-judged", wanted.Count, candidates.Count, entered, exited);
    }

    private TriggerZoneTickResult Idle(string code) => new("idle", code, 0, 0, 0, 0);

    /// <summary>Whether a zone reacts to one entity, by the kind the reference's own id names. The kind is read
    /// from the reference and never from a position: a zone that reacts to players must not fire for an enemy that
    /// happens to stand in it.</summary>
    private static bool Reacts(TriggerZone zone, EntityReference reference)
        => zone.Who switch
        {
            TriggerZoneWho.Player => Kind(reference) == PlayerKind,
            TriggerZoneWho.Enemy => Kind(reference) == EnemyKind,
            _ => Kind(reference) is PlayerKind or EnemyKind
        };

    /// <summary>The distance between the two places one body was judged at, in metres. Both positions carry three
    /// coordinates: a snapshot of any other count is never read into one.</summary>
    private static double Travel(IReadOnlyList<double> from, IReadOnlyList<double> to)
    {
        var x = to[0] - from[0];
        var y = to[1] - from[1];
        var z = to[2] - from[2];
        return Math.Sqrt((x * x) + (y * y) + (z * z));
    }

    private static string Kind(EntityReference reference)
    {
        var split = reference.Id.IndexOf(':');
        return split > 0 ? reference.Id[..split] : "";
    }

    /// <summary>One entity kind's candidates, or null when the kernel refused: a kind nobody owns is not an empty
    /// world, and its entities keep the membership they had rather than being published as gone.</summary>
    private IReadOnlyList<EntityReference>? Read(string kind)
    {
        RuntimeEntityQueryResult answer;
        try { answer = _kernel.EnumerateEntityCandidates(kind); }
        catch (Exception error)
        {
            ReportOnce("reader:" + kind, "trigger-zone-reader-failed: " + kind + " (" + error.GetType().Name + ")");
            return null;
        }
        if (answer.IsComplete) return Array.AsReadOnly(answer.Items.Select(item => item.Reference).ToArray());
        ReportOnce("reader:" + kind + ":" + answer.Code, "trigger-zone-targets: " + kind + " answered " + answer.Code
            + "; its targets keep their membership this tick.");
        return null;
    }

    /// <summary>Publishes one edge of one target in one zone through the map-object half, which owns the event's
    /// identity, its subject and its authority guard. The transition number is the target's own edge count inside
    /// that zone, so a repeated tick produces neither a new transition nor a new event id.</summary>
    private bool Publish(string fact, TriggerZone zone, EntityReference reference)
    {
        var key = fact + ":" + zone.Id + ":" + reference.Id;
        var transition = _transitions.TryGetValue(key, out var last) ? last + 1 : 1;
        _transitions[key] = transition;
        // The map-object half owns the binding, so the gate is checked there: this module only counts what that
        // half really published.
        RuntimeEvent? published;
        try { published = _mapObjects.ZoneMembership(fact, zone, reference, transition); }
        catch (Exception error)
        {
            ReportOnce("publish:" + fact + ":" + key, "trigger-zone fact threw: " + error.GetType().Name + ": " + error.Message);
            return false;
        }
        if (published == null) return false;
        _publishedFacts++;
        EventObserver?.Invoke(published);
        return true;
    }

    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        if (value.Kind != RuntimeLifecycleKind.WorldChanged) return;
        // A new world holds no membership of the old one: the zones of the level that ended are not the zones of
        // the level that started, and an exit across the boundary would name a body that no longer exists.
        ClearMembership();
        _judgedLevel = null;
    }

    private void ClearMembership()
    {
        _inside.Clear();
        _transitions.Clear();
        _previous = new Dictionary<string, double[]>(StringComparer.Ordinal);
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _report(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        ClearMembership();
    }
}
