using System;
using System.Collections.Generic;
using System.Linq;
using ForgeMap;
using ForgeMap.Tests.Support;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.TriggerZoneFacts;

/// <summary>
/// One live world for the cases below: the real map-object module with the trigger zones as one of its categories,
/// the kernel's own entity tables answering for the players and the enemies, and the judging module publishing
/// through the map-object half exactly as the game-bound session wires it.
///
/// The fixture models the kernel's own answers, including the ones that are refusals: a kind whose provider cannot
/// enumerate and a reference whose snapshot cannot be read are answered as such, so a case can prove that the
/// module publishes no edge it did not observe. A zone fact is a map-object fact here, so the cases read it the way
/// a plan does.
/// </summary>
internal sealed class TriggerZoneWorld : IDisposable
{
    /// <summary>One fixture entity: the reference the kernel validates, the position its observer answers with,
    /// and the snapshot a refused read is modelled by.</summary>
    private sealed class Reading
    {
        internal Reading(EntityReference reference, double[] position)
        {
            Reference = reference;
            Position = position;
            Refresh();
        }

        internal EntityReference Reference { get; }
        internal double[] Position { get; set; }
        internal RuntimeEntitySnapshot? Snapshot { get; private set; }

        internal void Refresh() => Snapshot = new RuntimeEntitySnapshot(Reference, Kind(Reference), null, "alive",
            Array.Empty<string>(), Array.Empty<string>(), new[] { Position[0], Position[1], Position[2] });
    }

    private readonly Dictionary<string, Reading> _readings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityReference> _candidates = new(StringComparer.Ordinal);
    private readonly Func<bool> _authority;

    /// <summary>Builds one world around the map-object module that was composed for it. The registration follows
    /// through <see cref="Register"/>, because every category and every entity surface is declared at that one
    /// call and this world's own tables are what those surfaces answer from.</summary>
    private TriggerZoneWorld(RuntimeKernel kernel, MapObjectModule mapObjects, TriggerZoneSource zones, bool authority)
    {
        Kernel = kernel;
        MapObjects = mapObjects;
        Zones = zones;
        Authority = authority;
        _authority = () => Authority;
    }

    /// <summary>Registers the map-object provider with this world's own entity surface composed onto the zone
    /// rows, and builds the judging module on the registration that call produced.</summary>
    private void Register()
    {
        MapObjects.Register(ZoneDefinition.Create() with
        {
            EntityResolvers = Resolvers(Kernel, Zones),
            EntityObservers = Observers(),
            EntityCandidates = Candidates()
        });
        ZoneModule = new TriggerZoneModule(Kernel, MapObjects.Registration, MapObjects, _authority,
            () => Level, Reports.Add) { EventObserver = Published.Add };
    }

    internal RuntimeKernel Kernel { get; }
    internal MapObjectModule MapObjects { get; }
    internal TriggerZoneSource Zones { get; }
    internal TriggerZoneModule ZoneModule { get; private set; } = null!;
    /// <summary>The zone table this world judges: the document's zones placed in the level it is running. A zone
    /// whose room the resolver cannot name is not in it, exactly as the game-bound session leaves it out.</summary>
    internal IReadOnlyList<TriggerZone> Table => Zones.Zones;
    /// <summary>The room every document below writes, in the package's own locator spelling: a room of one known
    /// native zone the resolver answers for unless a case says otherwise.</summary>
    internal const string RoomRevision = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    internal static string RoomJson(string zoneAuthorId = "zone-1", string prefab = "Assets/Complex/Mining/Room.prefab",
        int dimension = 0, int layer = 0, int localIndex = 1)
        => "{\"kind\":\"unique-geomorph-in-zone\",\"zoneAuthorId\":\"" + zoneAuthorId
            + "\",\"dimension\":" + dimension + ",\"layer\":" + layer + ",\"localIndex\":" + localIndex
            + ",\"room\":{\"id\":\"room-definition\",\"revision\":\"" + RoomRevision + "\"},\"sourcePrefab\":\"" + prefab + "\"}";
    /// <summary>The room resolver the game-bound half composes. The default answers the world origin with no
    /// rotation, so a case about a shape or a membership reads back the numbers it wrote; a case about the room
    /// itself replaces it with the pose it is about, or with one that names no room at all.</summary>
    internal TriggerZoneRoomLookup? Rooms { get; set; } =
        _ => TriggerZoneRoomPose.Of(new double[] { 0, 0, 0 }, new double[] { 0, 0, 0, 1 });

    private MapLevelReference? _level = MapLevelReference.TryParse("31:A:0");
    /// <summary>The level this world is running. Setting it is the level change the session sees: the new level's
    /// zones are placed, and the zones of a level this world is not running are carried through as authored.</summary>
    internal MapLevelReference? Level
    {
        get => _level;
        set { _level = value; Place(); }
    }
    private IReadOnlyList<TriggerZone> _authored = Array.Empty<TriggerZone>();
    internal bool Authority { get; set; }
    /// <summary>Whether the enemy kind's own provider can enumerate right now. A package that is not installed and
    /// a table that cannot be read are the same answer to the module: a refusal by name, never an empty world.</summary>
    internal bool EnemiesEnumerate { get; set; } = true;
    internal readonly List<string> Reports = new();
    internal readonly List<RuntimeEvent> Published = new();

    /// <summary>Starts one world with the map-object provider registered on one zone table.</summary>
    internal static TriggerZoneWorld Start(TriggerZoneSource? zones = null, bool authority = true)
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
        // The control vocabulary is the kernel's own module, exactly as the host registers it: a mounted plan in
        // these cases needs one step that executes. The trigger rows are the production contract's own and this
        // provider declares them here, which is what a provider does for the two rows it implements.
        kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        kernel.BeginWorld(1);
        var source = zones ?? new TriggerZoneSource();
        var mapObjects = new MapObjectModule(kernel, RuntimeLogLevel.Off, new NoDoorSource(), new NoTerminalSource(),
            (_, _) => null, null, () => true, _ => { }, _ => { }, () => null, null, source);
        var world = new TriggerZoneWorld(kernel, mapObjects, source, authority);
        world.Register();
        // The observation point these cases assert on sits in the judging module, before the kernel is reached, so
        // the module needs a subscriber for every row it publishes under or it builds no edge at all.
        SubscriptionGateFixture.Open(kernel, mapObjects.Registration);
        if (!kernel.StartRuntime(static () => { })) throw new InvalidOperationException("Test startup failed.");
        kernel.Advance(1, true);
        return world;
    }

    /// <summary>The entity surface the kernel is given: the map-object kind answers for the zones this provider
    /// holds, and the two kinds a zone reads answer from this fixture's own tables. The kernel routes every read
    /// through these, so a case cannot read a world the module itself would not have read.</summary>
    private Dictionary<string, Func<EntityReference, bool>> Resolvers(RuntimeKernel kernel, TriggerZoneSource source)
        => new(StringComparer.Ordinal)
        {
            [MapObjectModule.EntityKind] = reference => reference.WorldEpoch == kernel.WorldEpoch
                && source.ById(TriggerZoneAddress.TryParse(AfterKind(reference))?.Key) != null,
            [TriggerZoneModule.PlayerKind] = reference => reference.WorldEpoch == kernel.WorldEpoch,
            [TriggerZoneModule.EnemyKind] = reference => reference.WorldEpoch == kernel.WorldEpoch
        };

    private Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> Observers()
        => new(StringComparer.Ordinal)
        {
            [TriggerZoneModule.PlayerKind] = Observe,
            [TriggerZoneModule.EnemyKind] = Observe
        };

    private Dictionary<string, Func<IReadOnlyList<EntityReference>>> Candidates()
        => new(StringComparer.Ordinal)
        {
            [TriggerZoneModule.PlayerKind] = () => Candidates(TriggerZoneModule.PlayerKind),
            [TriggerZoneModule.EnemyKind] = EnemyCandidates
        };

    /// <summary>Adds one target at a position: the candidate set and the position table are the two reads the
    /// module makes of it.</summary>
    internal EntityReference Player(string id, double x, double y, double z)
        => Add(TriggerZoneModule.PlayerKind, id, x, y, z);

    internal EntityReference Enemy(string id, double x, double y, double z)
        => Add(TriggerZoneModule.EnemyKind, id, x, y, z);

    /// <summary>Moves one target. A snapshot carries its own copy of the position, so the observation is re-made
    /// for the place the body is now: a fixture that moved the array alone would answer with the old place. A
    /// reading that was dropped is made again, because a body that is a candidate and reports a position is
    /// exactly a reading.</summary>
    internal void Move(EntityReference reference, double x, double y, double z)
    {
        if (!_readings.TryGetValue(reference.Id, out var reading))
        {
            reading = new Reading(reference, new[] { x, y, z });
            _readings[reference.Id] = reading;
            return;
        }
        reading.Position = new[] { x, y, z };
        reading.Refresh();
    }

    /// <summary>A target that is not a candidate any more: it left the world, which is not the same fact as
    /// walking out of a zone.</summary>
    internal void Vanish(EntityReference reference)
    {
        _candidates.Remove(reference.Id);
        _readings.Remove(reference.Id);
    }

    /// <summary>A target that is still a candidate but whose snapshot cannot be read any more: the whole
    /// inspection is partial, which is the refusal the module must not turn into an exit.</summary>
    internal void StopReading(EntityReference reference) => _readings.Remove(reference.Id);

    /// <summary>The next world: a new epoch with no body of the old one left in it. The module's own subscription
    /// is what clears its membership, and the fixture only stops answering for lives that cannot exist in the new
    /// epoch.</summary>
    internal void NewWorld(long epoch)
    {
        Kernel.BeginWorld(epoch);
        _readings.Clear();
        _candidates.Clear();
    }

    /// <summary>One tick of the judging module, with the kernel advanced first so the facts this tick's judgment
    /// produces are dispatched in the same call, exactly as the game's own step does.</summary>
    internal TriggerZoneTickResult Tick() => TickOutcome().Judgment;

    /// <summary>The same tick with the kernel's own answer: a case that has to tell a plan that was never handed an
    /// event from one whose edge was nobody's reads the receipts and the processed count here.</summary>
    internal (TriggerZoneTickResult Judgment, TickResult Advance) TickOutcome()
    {
        var judgment = ZoneModule.Tick();
        return (judgment, Kernel.Advance(Kernel.CurrentTick + 1, true));
    }

    /// <summary>The zones of one install, parsed by the module's own grammar, so a case cannot hand the module a
    /// zone the game could never have produced. Loading replaces the table before the first tick, which is the
    /// only moment a package's zones exist, and places the running level's zones through this fixture's resolver
    /// exactly as the game-bound session places them.</summary>
    internal void Load(string json)
    {
        if (!TriggerZoneManifest.TryParse(json, out var zones, out var code, out var reason))
            throw new InvalidOperationException(code + ": " + reason);
        _authored = zones!;
        Place();
    }

    /// <summary>The authored table placed in the level this world runs. The authored zones stay what the document
    /// said, so a zone refused in one level is judged again when its own level is the one running.</summary>
    private void Place()
        => Zones.Load(_level is { } level ? TriggerZonePlacement.Of(_authored, level, Rooms, Reports.Add) : _authored);

    /// <summary>One zone document with a single zone, for the cases that only need one. The room is the fixture's
    /// standard one; a case about the room writes its own text instead.</summary>
    internal void LoadOne(string id, string shape, string size, string position,
        string level = "31:A:0", string rotation = "0, 0, 0, 1", string who = "both", bool blocks = false, string? room = null)
        => Load("{\"schemaVersion\":1,\"zones\":[{\"id\":\"" + id + "\",\"level\":\"" + level + "\",\"room\":"
            + (room ?? RoomJson()) + ",\"shape\":\"" + shape + "\",\"size\":[" + size + "],\"position\":[" + position
            + "],\"rotation\":[" + rotation + "],\"who\":\"" + who + "\",\"blocksPlayers\":" + (blocks ? "true" : "false") + "}]}");

    /// <summary>The last fact one capability published, or null when it published none.</summary>
    internal RuntimeEvent? Last(string fact)
    {
        var binding = TriggerZoneContract.BindingOf(fact);
        RuntimeEvent? last = null;
        foreach (var published in Published) if (published.BindingId == binding) last = published;
        return last;
    }

    internal int Count(string fact)
    {
        var binding = TriggerZoneContract.BindingOf(fact);
        return Published.Count(published => published.BindingId == binding);
    }

    private EntityReference Add(string kind, string id, double x, double y, double z)
    {
        var reference = new EntityReference(kind + ":" + id, Kernel.WorldEpoch, 1);
        _readings[reference.Id] = new Reading(reference, new[] { x, y, z });
        _candidates[reference.Id] = reference;
        return reference;
    }

    /// <summary>The one position read: the fixture's own snapshot, or none when the reading was dropped — exactly
    /// the rule the kernel's own inspection applies.</summary>
    private RuntimeEntitySnapshot? Observe(EntityReference reference)
        => _readings.TryGetValue(reference.Id, out var reading) ? reading.Snapshot : null;

    private IReadOnlyList<EntityReference> Candidates(string kind)
        => Array.AsReadOnly(_candidates.Values.Where(reference => Kind(reference) == kind)
            .OrderBy(reference => reference.Id, StringComparer.Ordinal).ToArray());

    /// <summary>The enemy kind's own provider failing to answer, which the kernel reports as
    /// `entity-candidates-failed` rather than as an empty set.</summary>
    private IReadOnlyList<EntityReference> EnemyCandidates()
        => EnemiesEnumerate
            ? Candidates(TriggerZoneModule.EnemyKind)
            : throw new InvalidOperationException("the enemy table cannot be read");

    private static string AfterKind(EntityReference reference)
    {
        var split = reference.Id.IndexOf(':');
        return split > 0 ? reference.Id[(split + 1)..] : "";
    }

    private static string Kind(EntityReference reference)
    {
        var split = reference.Id.IndexOf(':');
        return split > 0 ? reference.Id[..split] : "";
    }

    public void Dispose()
    {
        ZoneModule.Dispose();
        MapObjects.Dispose();
        Kernel.StopRuntime();
    }
}

/// <summary>The one Map provider declaration these cases register: this provider's identity and its trigger-zone
/// rows, which is all the map-object half needs to declare the zone category. The production declaration carries
/// every other Map row as well; a suite that wants only the zone category declares only it, and the rows are the
/// contract's own so a case cannot register a shape the game could not have registered.</summary>
internal static class ZoneDefinition
{
    private const string ProviderId = TriggerZoneContract.ProviderId;

    internal static RuntimeModule Create() => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = ProviderId, kind = "native", version = "0.1.0", dependencies = Array.Empty<string>() } },
            capabilities = TriggerZoneContract.CapabilityRows(),
            bindings = TriggerZoneContract.BindingRows()
        }).GetRawText(),
        new Dictionary<string, CommandHandler>(StringComparer.Ordinal),
        TriggerZoneContract.Supports());
}

/// <summary>A category source for a suite that declares no doors: it answers for nothing, which is what the
/// map-object module needs to know that no door of this world is addressable.</summary>
internal sealed class NoDoorSource : IMapObjectSource
{
    public string Category => MapObjectCategories.Door;
    public MapObjectReference? TryAddress(object instance) => null;
    public MapObjectReference? Parse(string text) => null;
    public MapObjectObservation? Read(object instance) => null;
    public double[]? Position(object instance) => null;
    public bool IsCurrentAddress(object instance, MapObjectReference address) => false;
    public void Report(string message) { }
}

/// <summary>The terminal counterpart of <see cref="NoDoorSource"/>.</summary>
internal sealed class NoTerminalSource : IMapObjectSource
{
    public string Category => MapObjectCategories.Terminal;
    public MapObjectReference? TryAddress(object instance) => null;
    public MapObjectReference? Parse(string text) => null;
    public MapObjectObservation? Read(object instance) => null;
    public double[]? Position(object instance) => null;
    public bool IsCurrentAddress(object instance, MapObjectReference address) => false;
    public void Report(string message) { }
}
