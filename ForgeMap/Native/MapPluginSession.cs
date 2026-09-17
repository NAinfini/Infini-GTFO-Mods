using System;
using System.Collections.Generic;
using System.Linq;
using AIGraph;
using ForgeMap;
using ForgeRuntime.Framework;
using LevelGeneration;
using Player;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>Domain-owned lifetime for one Map registration; the host remains the only runtime, clock and
/// authority gate. One registration carries every Map surface — the player identity the player domain owns, the
/// map-object namespaces this session owns, the environment presentation rows, the player facts and values, and
/// the level objects — because the runtime accepts exactly one provider of an identity namespace and a second
/// registration would be a second provider of it.</summary>
internal sealed partial class MapPluginSession : IDisposable
{
    private readonly RuntimeKernel _kernel;
    private readonly Action<string> _report;
    private readonly Action _removeHooks;
    private bool _disposed, _faulted;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    internal static string? LastCleanupDiagnostic { get; private set; }
    internal PlayerIdentityModule Module { get; private set; } = null!;
    internal MapObjectModule? MapObjects { get; private set; }
    /// <summary>The level-object half: scans, generators, containers and the items standing in the level. It is
    /// the only publisher of `gtfo.level_object`, and it is declared on the one registration every other Map
    /// surface is declared on.</summary>
    internal LevelObjectModule? Levels { get; private set; }
    /// <summary>The level-event half: the eight session/objective/zone/portal observation rows and the three
    /// actions. It holds a lifecycle subscription, so it is released with the other halves rather than left to the
    /// registration's own disposal.</summary>
    internal LevelEventModule? LevelEvents { get; private set; }
    /// <summary>The trigger-zone half: the authored volumes this install declared, the judging tick that reads
    /// them and the blocking bodies of the level being played. It publishes through the map-object path the doors
    /// and terminals already use.</summary>
    internal TriggerZoneSession? TriggerZones { get; private set; }
    /// <summary>The one registration on the kernel's own clock this package takes. It is held only while there is
    /// work — an in-flight light fade, or a placed zone with a subscriber — so an idle session pays nothing per
    /// frame. Every half that can produce work announces it through <see cref="MapClock.Wake"/>.</summary>
    private MapClock? _clock;
    /// <summary>The alarm, scan and wave half's attachment: the five execute rows are static handlers, and this is
    /// the registration and kernel they mint and resolve their effect handles through.</summary>
    private AlarmWaveActions? _alarmWave;
    /// <summary>The door and terminal observation half. The two door execute rows and the door value row answer
    /// from the tables it fills, which is why the session owns it and not the hook list.</summary>
    private DoorTerminalFacts? _doorTerminal;
    /// <summary>The attribute-modifier half: the one adapter the two sourced-modifier rows write through. It holds
    /// a lifecycle subscription on the registration, so the session owns it and releases it with the other
    /// halves.</summary>
    private AgentModifierAdapter? _agentModifiers;
    /// <summary>The native player value source and the three halves the player hooks publish through. A hook
    /// reads the installed half rather than a session, so a session that failed before attaching them publishes
    /// nothing.</summary>
    private NativePlayerVitals? _vitals;
    internal PlayerStateFacts? State { get; private set; }
    internal PlayerEventFacts? Events { get; private set; }
    internal PlayerLifeFacts? Lives { get; private set; }
    /// <summary>The world the native address tables belong to. The kernel owns the epoch; the zone table reads
    /// it here so every native instance it holds is invalidated with the world instead of outliving it.</summary>
    internal long WorldEpoch => _kernel.WorldEpoch;
    /// <summary>The runtime this session is declared on, for the native readers that resolve an address outside
    /// this file.</summary>
    internal RuntimeKernel Kernel => _kernel;
    internal bool Faulted => _faulted;
    internal string? LastFault { get; private set; }
    internal string? LastReporterFailure { get; private set; }
    private MapPluginSession(RuntimeKernel kernel, Action<string> report, Action removeHooks)
    { _kernel = kernel; _report = report; _removeHooks = removeHooks; }

    internal static MapPluginSession Start(RuntimeKernel kernel, RuntimeLogLevel logLevel, Action<string> report, Action<string> log,
        Action installHooks, Action removeHooks)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(log); ArgumentNullException.ThrowIfNull(installHooks);
        ArgumentNullException.ThrowIfNull(removeHooks);
        if (!kernel.IsRegistrationOpen)
            throw new InvalidOperationException("Map must register during dependent plugin Load, before Runtime startup.");
        var session = new MapPluginSession(kernel, report, removeHooks);
        bool hooksAttempted = false;
        try
        {
            // The map-object module owns the one Map registration, because it declares every Map surface: the
            // map-object namespaces, the `map-object` and `level` attachment matchers, and the provider entries
            // every other half of this package answers through. Duplicate provider or entity namespace throws
            // atomically before any Harmony patch is attempted, and each half attaches to the handle only after
            // that registration exists. The level reader is the game-bound half of the level matcher: the module
            // compares, this session reads.
            session.MapObjects = new MapObjectModule(kernel, logLevel, new DoorSource(report), new TerminalSource(report),
                Resolve, instance => kernel.ResolveEntityInstance(PlayerIdentityModule.EntityKind, instance),
                () => SNet.IsMaster, report, log, () => LevelIdentity.Read(log), MapObjectHit.Instance,
                generators: new GeneratorSource(report))
            {
                // The generator category's own value row reads through the game-bound half that holds the
                // instances: the module owns the shape and the resource table, and this is the one reader that can
                // read a native generator into them.
                GeneratorStateReader = GeneratorObservation.ReadState
            };
            // The one registration is created before every half, because the handle is what a half publishes
            // through and one registration has to declare all of their rows at once. Each entry of the definition
            // reads the half that is current when the kernel asks, so the halves below are built afterwards, and
            // a half that was never built refuses by name instead of publishing through rows nothing declared.
            session.MapObjects.Register(session.Definition());
            session.Levels = new LevelObjectModule(kernel, session.MapObjects.Registration, report)
            {
                // The scan value row reads through the game-bound half: the module owns the shape and the table,
                // and this is the one reader that can read a native scan into them.
                ScanStateReader = LevelObjectObservation.ReadScanState
            };
            session.LevelEvents = new LevelEventModule(kernel, session.MapObjects.Registration, report);
            // The door and terminal half answers the definition's two door execute rows and its door value row
            // through the tables it fills, and the patch classes reach it through `DoorTerminalFacts.Current`.
            session._doorTerminal = new DoorTerminalFacts(kernel, session.MapObjects.Registration, () => !session._faulted,
                report, TerminalObservation.BySyncId, TerminalObservation.Address, DoorTerminalFacts.DoorOwner,
                session.WeakDoorPlacement, player => PlayerIdentityModule.Current?.ReferenceOf(player));
            DoorTerminalFacts.Current = session._doorTerminal;
            // The zones are handed to the map-object module before any tick judges them, and the tick patch
            // reaches this one session: the registration exists by now, which is what the judging module publishes
            // through. The room resolver is the one that reads the level the game generated — it names an authored
            // room by the prefab object its `Assets/` path loads, so a zone whose room this level does not hold
            // exactly once is refused by name and left unjudged instead of being placed at a guessed world pose.
            session.TriggerZones = TriggerZoneSession.Start(kernel, session.MapObjects, () => SNet.IsMaster,
                () => LevelIdentity.Read(log), room => TriggerZoneRoomResolver.Lookup(kernel.WorldEpoch, room), report, log);
            // The one clock. The zone half is asked for its own work every time the clock looks; the light table has
            // no way to be asked — a schedule is announced from inside the command handler that makes it, which runs
            // in a dispatch rather than in a stage that could call this half — so it announces itself through the
            // hook below. Both jobs share the one registration, which is taken by the first of them to have work.
            session._clock = new MapClock(session.MapObjects.Registration,
                () => session.TriggerZones?.Refresh(),
                () => session.TriggerZones?.Pending ?? false,
                () => session.TriggerZones?.Tick(),
                () => LightColorFades.Count > 0,
                seconds => LightColorFades.Tick(kernel.WorldEpoch, seconds),
                () => UnityEngine.Time.deltaTime);
            LightColorFades.WorkScheduled = session._clock.Wake;
            // The five alarm/scan/wave rows are static handlers that mint and resolve their handles through the
            // kernel, so this is the one line that hands them their registration; it runs on the thread the kernel
            // was built on, which is the thread every half of this session is created on.
            session._alarmWave = AlarmWaveActions.Attach(kernel, session.MapObjects.Registration);
            // The one adapter both sourced-modifier rows write through, and the row the movement preset writes
            // through beside them: one write path onto the native synced-modifier table, never two. It is created
            // after `RegisterModule` returned because its lifecycle subscription needs a registration that exists.
            session._agentModifiers = new AgentModifierAdapter(session.MapObjects.Registration, report);
            session.Module = new PlayerIdentityModule(session.MapObjects.Registration, kernel, () => !session._faulted, log, report);
            session._vitals = new NativePlayerVitals();
            session.State = PlayerStateFacts.Attach(session.MapObjects.Registration, kernel, session._vitals, log, report);
            session.Events = PlayerEventFacts.Attach(session.MapObjects.Registration, kernel, session._vitals, log, report);
            session.Lives = new PlayerLifeFacts(session.MapObjects.Registration, kernel, () => !session._faulted, report, log);
                // The one value source the query rows read through: the identity half says which life a reference is,
            // this source says what that life holds.
            PlayerValueReads.Attach(new NativePlayerValues(kernel));
            hooksAttempted = true; installHooks();
            return session;
        }
        catch (Exception original)
        {
            // Any residual registration is inert even if rollback itself fails.
            session._faulted = true;
            var errors = new List<Exception>();
            if (hooksAttempted) Cleanup(removeHooks, errors);
            session.DetachHalves(errors);
            if (session.MapObjects != null) Cleanup(session.MapObjects.Dispose, errors);
            if (session.Module != null) Cleanup(session.Module.Dispose, errors);
            session._disposed = true;
            if (errors.Count != 0) PreserveCleanupFailure(original, "ForgeMap.CleanupFailures", new AggregateException(errors), report);
            throw;
        }
    }

    /// <summary>Releases the halves that hold a static installation point or a per-world table of their own,
    /// before the registration they published on is disposed. A half that was never created is skipped, so a
    /// session that failed while starting releases exactly what it managed to build.</summary>
    private void DetachHalves(List<Exception> errors)
    {
        var doorTerminal = _doorTerminal;
        _doorTerminal = null;
        if (doorTerminal != null) Cleanup(() => DoorTerminalFacts.Current = null, errors);
        var state = State; State = null;
        if (state != null) Cleanup(() => PlayerStateFacts.Detach(state), errors);
        var events = Events; Events = null;
        if (events != null) Cleanup(() => PlayerEventFacts.Detach(events), errors);
        var lives = Lives; Lives = null;
        var levels = Levels; Levels = null;
        if (levels != null) Cleanup(levels.Dispose, errors);
        var levelEvents = LevelEvents; LevelEvents = null;
        if (levelEvents != null) Cleanup(levelEvents.Dispose, errors);
        // The zones go before the registration they publish through: the half releases this world's blocking
        // bodies and its lifecycle subscription, so a released registration is never judged or published to.
        var zones = TriggerZones; TriggerZones = null;
        if (zones != null) Cleanup(zones.Dispose, errors);
        // A released registration must never be minted through again: the static rows read this attachment, and a
        // session that can restart would otherwise hand the next registration's commands a dead one.
        var alarmWave = _alarmWave; _alarmWave = null;
        if (alarmWave != null) Cleanup(() => AlarmWaveActions.Detach(alarmWave), errors);
        if (PlayerValueReads.Installed is { } reads) Cleanup(() => PlayerValueReads.Detach(reads), errors);
        // The movement preset and the two attribute rows are one adapter, so it goes last of the halves that
        // write: releasing it revokes every modification this provider still holds an id for.
        var agentModifiers = _agentModifiers; _agentModifiers = null;
        if (agentModifiers != null) Cleanup(agentModifiers.Dispose, errors);
    }

    /// <summary>The one Map provider definition: the rows, the support lines and the shape table
    /// <see cref="ModuleRegistration"/> declares — the same registration the release export builds — completed
    /// with the bodies and the runtime surface of every half. This session supplies the bodies because it is the
    /// only place that knows the player identity, the map objects, the environment and the level together, and
    /// because the runtime accepts exactly one provider of one identity namespace. Each entry reads the half that
    /// is current when the kernel asks, so the definition can be built before the player half attaches to the
    /// registration it is declared on; nothing is dispatched before the host starts the runtime.
    ///
    /// Every row's declaration — its capability, its binding, its registration row and its port shape — is the
    /// game-independent contract's, and only the handler bodies are native, so the declaration and the
    /// implementation cannot drift into two descriptions of one binding.</summary>
    private RuntimeModule Definition()
    {
        var actions = new TerminalObjectActions(_kernel, () => !_faulted, TerminalFor, _report);
        var objectives = ObjectiveActionHandler.For(() => !_faulted, _report);
        var doors = new DoorTerminalActions(_kernel, () => !_faulted, DoorFor, _report);
        // The three door execute rows' own half: it resolves a recipient through the same address grammar the
        // observation half records doors by, so a door a plan names is the door that was observed.
        var doorActions = new DoorActionCommands(_kernel, () => !_faulted, DoorActionCommands.ResolveByAddress, _report);
        var environment = EnvironmentActions.For(() => !_faulted, _report);
        var presented = EnvironmentPresentation.For(_report);
        var hud = new HudActions(_report);
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [PlayerHealthContract.HandlerName] = PlayerHealthAction.Execute,
            [TerminalObjectContract.CommandHandlerName] = actions.HandleCommand,
            [TerminalObjectContract.VisibilityHandlerName] = actions.HandleVisibility,
            [TerminalObjectContract.OutputHandlerName] = actions.HandleOutput,
            [ObjectiveActionContract.StateHandlerName] = objectives.HandleState,
            [ObjectiveActionContract.PhaseHandlerName] = objectives.HandlePhase,
            [ObjectiveActionContract.ExtractionHandlerName] = objectives.HandleExtraction,
            [DoorTerminalActionContract.LockHandlerName] = doors.HandleLock,
            [DoorTerminalActionContract.UnlockHandlerName] = doors.HandleUnlock,
            // The three door rows and the two player rows: the contract's own handler names, so the declaration
            // and its body are one fact.
            [DoorActionContract.OpenHandlerName] = doorActions.HandleOpen,
            [DoorActionContract.CloseHandlerName] = doorActions.HandleClose,
            [DoorActionContract.AlarmHandlerName] = doorActions.HandleAlarm,
            [PlayerActionContract.TeleportHandlerName] = PlayerActions.Teleport,
            [PlayerActionContract.InfectionHandlerName] = PlayerActions.Infection,
            // The movement preset writes the one native modification table through the one adapter this session
            // created: a handler table is built before the half that owns the state, so it is a static facade over
            // that half. The two sourced-modifier rows answer through the same adapter, but their capability rows
            // belong to `forge.contract.combat` and that provider does not declare them yet, so this registration
            // has no binding for them and asks for no body here.
            [MovementProfileContract.HandlerName] = AgentModifierAdapter.ProfileHandler,
            [EnvironmentContract.LightingHandler] = environment.HandleLighting,
            [EnvironmentContract.LightColorHandler] = environment.HandleLightColor,
            [EnvironmentContract.FogHandler] = environment.HandleFog,
            [EnvironmentContract.FogCycleHandler] = environment.HandleFogCycle,
            [EnvironmentContract.NavMarkerHandler] = environment.HandleNavMarker,
            [EnvironmentContract.AnimationHandler] = environment.HandleAnimation,
            [EnvironmentContract.AudioHandler] = presented.HandleAudio,
            [EnvironmentContract.AudioStopHandler] = presented.HandleAudioStop,
            [EnvironmentContract.IntelHandler] = presented.HandleIntel,
            [EnvironmentContract.DialogueHandler] = presented.HandleDialogue,
            [EnvironmentContract.PlayerVoiceHandler] = presented.HandlePlayerVoice,
            [HudContract.ValueHandler] = hud.HandleValue,
            [PlayerCommandContract.DamageHandlerName] = PlayerCommandActions.Damage,
            [PlayerCommandContract.ReviveHandlerName] = PlayerCommandActions.Revive,
            [PlayerCommandContract.DownHandlerName] = PlayerCommandActions.Down,
            // The three scan/wave rows are static facades: each turns its request into the native
            // entry point its capability names, and the two start rows mint the handle through the
            // attachment this session took in Start.
            [AlarmWaveContract.ScanStartHandler] = AlarmWaveActions.ExecuteStartScan,
            [AlarmWaveContract.WaveStartHandler] = AlarmWaveActions.ExecuteStartWave,
            [AlarmWaveContract.WaveStopHandler] = AlarmWaveActions.ExecuteStopWave,
            // The three level-event actions, through the same static facade: each builds the
            // `WardenObjectiveEventData` the engine's own event manager executes.
            [LevelEventContract.ObjectiveTimerHandlerName] = LevelEventActions.Timer,
            [LevelEventContract.DimensionHandlerName] = LevelEventActions.Dimension,
            [LevelEventContract.ExpeditionEndHandlerName] = LevelEventActions.ExpeditionEnd
        };
        var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [PlayerSelectorContract.HandlerName] = PlayerSelector.Evaluate,
            // The zone row's evaluator is the game-independent contract's, built from the one read only this
            // half can make: the zone the anchor's own course node belongs to.
            [ZoneSelectorContract.HandlerName] =
                ZoneSelectorContract.Evaluators(new ZoneSelectorContract.ZoneReaders(ZoneOfPlayer))[ZoneSelectorContract.HandlerName],
            [DoorQueryContract.HandlerName] =
                DoorQueryContract.Evaluator(new DoorQueryContract.DoorReaders(DoorStateSample)),
            [EnvironmentContract.EnvironmentStateHandler] = EnvironmentQuery.Evaluate,
            // The `v-zone-lights` row: the same level the other environment reads stand in, resolving the
            // named zone through the one zone table and reading its own light list.
            [EnvironmentContract.ZoneLightsHandler] = EnvironmentQuery.ZoneLights,
            // The value rows answer through the module that holds the tables the facts fill, so a read of a
            // scan or a generator is a read of the same instance the fact named and not a second lookup of
            // it. Each half is read late, for the same reason every other entry here is.
            [LevelObjectContract.ScanStateHandler] = context => LevelObjects().ReadScanState(context),
            [GeneratorContract.GeneratorStateHandler] = context => MapObjectHalf().ReadGeneratorState(context),
            // The `v-obj` row: one objective layer's live state, read through the game-bound reader.
            [LevelObjectiveValueContract.HandlerName] = LevelObjectiveValueContract.Evaluator(
                new LevelObjectiveValueContract.LayerReader(LevelObjectiveValueReader.Read))
        }
            .Concat(PlayerValueReads.Evaluators())
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        // The rows, the support lines and the shape table are the one game-independent registration both this
        // session and the release export build; only the bodies above and the native tables below are this half's.
        return ModuleRegistration.Create(handlers, evaluators) with
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = reference => PlayerIdentityModule.Current is { } half && half.IsCurrent(reference),
                // A weak door is a `gtfo.map_object` door like any other, so the namespace answers for it from the
                // one table that recorded it, and the kernel refuses a reference of a world this session no
                // longer holds.
                [MapObjectModule.EntityKind] = IsCurrentMapObject
            },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = instance => PlayerIdentityModule.Current?.ResolveInstance(instance)
            },
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = reference => PlayerIdentityModule.Current?.Observe(reference)
            },
            // The player half is the only owner of `gtfo.player`, so it is also the only source of a player
            // candidate set. The selector reads the set through this one enumeration, which is why no second path
            // to the module exists: a step that needs every player asks the kernel for the kind.
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = PlayerCandidates
            },
            // Which zone a player of this provider stands in is the level's own answer, read from the player's
            // course node through the module's zone table. The trigger row that filters a candidate set by zone
            // reads it back through this one responder, and the same table answers the `zone` resource kind below.
            EntityZones = new Dictionary<string, Func<EntityReference, EntityReference?>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = ZoneOfPlayer
            },
            ResourceProviders = new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
            {
                [RuntimeZones.ResourceKind] = RuntimeResourceProvider.Of(ZoneResources, ResolveZoneResource),
                // The kinds the native world owns are read late, through the half that holds their tables: a
                // map-object half that was never created is refused by name instead of answering an empty world,
                // and the objective machine's three slots are the level's own answer.
                [GeneratorContract.GeneratorResourceKind] = RuntimeResourceProvider.Of(
                    () => MapObjectHalf().EnumerateGenerators(), id => MapObjectHalf().ResolveGenerator(id)),
                [LevelObjectiveValueContract.ObjectiveResourceKind] =
                    RuntimeResourceProvider.Of(ObjectiveLayerResources, ResolveObjectiveLayerResource),
                // The alarm/scan and wave kinds: the native tables the five execute rows act on, taken from the
                // same attachment the handlers read, so a plan naming one resolves the instance it will start.
                [AlarmWaveContract.ChainedPuzzleKind] = RuntimeResourceProvider.Of(
                    AlarmWaveActions.EnumerateChainedPuzzles, AlarmWaveActions.ResolveChainedPuzzle),
                [AlarmWaveContract.WaveKind] = RuntimeResourceProvider.Of(
                    AlarmWaveActions.EnumerateWaves, AlarmWaveActions.ResolveWave)
            },
            // The one player-session table every presentation row of this provider is routed by. The kernel hands
            // the step's own recipient references to this one function and addresses the sessions it answers, so
            // an environment row and a player row are routed by the same conversion: the entity a plan named, the
            // player it resolves to and that player's own session id. No second list of sessions exists anywhere
            // in this package.
            PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>(StringComparer.Ordinal)
            {
                [ModuleDefinition.ProviderId] = PlayerSessions.SessionsOf
            }
        };
    }

    /// <summary>The level-object half, refused by name when it was never created. Every entry of the definition
    /// that answers through it reads it here rather than at composition time, because the definition is composed
    /// before the registration exists and the half is created after it.</summary>
    private LevelObjectModule LevelObjects()
        => Levels ?? throw new RuntimeContractException("level-object-unavailable", "No Map level-object half is installed.");

    /// <summary>The map-object half, refused by name when it was never created. The generator category's resource
    /// kind and value row read the tables it holds, and the definition is composed before the half exists, so
    /// every entry that answers through it reads it here rather than at composition time.</summary>
    private MapObjectModule MapObjectHalf()
        => MapObjects ?? throw new RuntimeContractException("map-object-unavailable", "No Map map-object half is installed.");

    /// <summary>Whether one `gtfo.map_object` reference is still one this session holds: an entrance-gate door, a
    /// terminal, or a weak door the door-terminal half recorded for this world. The namespace is answered by one
    /// predicate rather than one per category, because a plan that pins any map object asks the same question
    /// about the same namespace.</summary>
    private bool IsCurrentMapObject(EntityReference reference)
    {
        if (_disposed || reference.WorldEpoch != _kernel.WorldEpoch) return false;
        if (_doorTerminal != null && _doorTerminal.IsCurrentWeakDoor(reference)) return true;
        if (MapObjectDoorAddress.TryParse(Id(reference)) is { } door)
            return DoorObservation.ByAddress(door) is { } entrance && !entrance.WasCollected;
        if (MapObjectTerminalAddress.TryParse(Id(reference)) is { } terminal)
            return TerminalObservation.ByAddress(terminal) is { } instance && !instance.WasCollected;
        return false;
    }

    /// <summary>The address text one `gtfo.map_object` reference carries, or null when it is not a reference of
    /// that namespace at all.</summary>
    private static string? Id(EntityReference reference)
    {
        string prefix = MapObjectModule.EntityKind + ":";
        return reference.Id != null && reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? reference.Id[prefix.Length..] : null;
    }

    /// <summary>The one door the two door execute rows act on: an entrance gate of this level. The address is
    /// read with the category's own grammar and resolved through the same table the observation half reads, so a
    /// door action and a door fact can never disagree about which door an address names. A weak door has no
    /// entrance to open or close — its two states are the fact rows' — so it resolves to no instance here.</summary>
    private static object? DoorFor(EntityReference reference)
    {
        if (MapObjectDoorAddress.TryParse(Id(reference)) is not { } address) return null;
        if (MapObjectDoorAddress.ParseWeakSerial(address.Key) is not null) return null;
        return DoorObservation.ByAddress(address);
    }

    /// <summary>One door read for the `forge.query.map.door_state` value row: the entrance door the reference
    /// names, its own last status, whether a lock holds it and the key it wants. A reference no door of this
    /// world answers for yields no sample, which the row refuses by name rather than answering with a status the
    /// door never had.</summary>
    private static DoorQueryContract.DoorSample? DoorStateSample(EntityReference reference)
    {
        if (MapObjectDoorAddress.TryParse(Id(reference)) is not { } address) return null;
        if (MapObjectDoorAddress.ParseWeakSerial(address.Key) is not null) return null;
        if (DoorObservation.ByAddress(address) is not { } door) return null;
        if (!DoorObservation.IsCurrentAddress(door, address)) return null;
        return DoorObservation.Read(door)?.Door is { } reading
            ? new DoorQueryContract.DoorSample(reading.Status, reading.Locked, reading.Key) : null;
    }

    /// <summary>The zone one weak door stands in: the level's own coordinates for it and the entity reference the
    /// row's `zone` port carries, read once. The gate the door was spawned on links the course node, and the node
    /// owns the zone; a door the level cannot walk to yields nothing rather than a guessed zone.</summary>
    private (ZoneCoordinates Coordinates, EntityReference Reference)? WeakDoorPlacement(LG_WeakDoor door)
    {
        if (door == null || door.WasCollected) return null;
        LG_Zone? zone;
        try
        {
            // The gate links navigation nodes, and only a course node stands in a zone: a node of another kind
            // carries no zone at all, so the walk takes the first node that really is one.
            var nodes = door.Gate?.m_nodes;
            zone = null;
            if (nodes != null)
                for (var index = 0; index != nodes.Count && zone == null; index++)
                    zone = nodes[index]?.TryCast<AIG_CourseNode>()?.m_zone;
        }
        catch (Exception) { return null; }
        if (zone == null || zone.WasCollected) return null;
        return ZoneTable() is { } table && table.TryCoordinates(zone, out var coordinates)
            ? (coordinates, RuntimeZones.Reference(_kernel.WorldEpoch, coordinates.Dimension, coordinates.Layer, coordinates.Zone))
            : null;
    }

    /// <summary>Whether a player slot the presentation rows name is one this process can address. The game's own
    /// slot index is the port the voice entry takes, so the check is the level's own player table rather than a
    /// second table this package would have to keep in step.</summary>
    private static bool SlotExists(int slot)
        => slot >= 0 && slot < PlayerManager.PlayerAgentsInLevel.Count;

    /// <summary>The zone table of the world being read, or null before a world exists to read it from. The table
    /// is keyed by this kernel's own world epoch, so a level of another world can never answer for this one; a
    /// table built from no level at all is not a table this provider can place anything in, which is what the
    /// null answer carries to every reader of it.</summary>
    private ZoneIndex? ZoneTable()
        => ZoneIndex.Current(_kernel.WorldEpoch) is { HoldsLevel: true } table ? table : null;

    /// <summary>The zone a player life stands in: the zone its own course node belongs to, read through the
    /// level's zone table so the answer is a zone this level really has. A null is not a placement — a course node
    /// whose zone the level's own table does not hold is a life this provider cannot place, and the kernel reports
    /// that as its own unknown rather than as an entity standing outside every zone — and every read this process
    /// cannot make is refused with its own name instead of being answered as that null: a world with no level zone
    /// table, a reference this session is not holding as a current life, and a life whose course node does not
    /// read are three different failures, and a caller that read any of them as "stands in no zone" would exclude
    /// a player it simply could not place.</summary>
    private EntityReference? ZoneOfPlayer(EntityReference reference)
    {
        if (ZoneTable() is not { } zones)
            throw new RuntimeContractException(ZoneSelectorContract.AnchorUnavailableCode,
                "This world holds no level zone table to read.");
        if (PlayerIdentityModule.Current is not { } players || players.CurrentAgent(reference) is not { } agent)
            throw new RuntimeContractException(ZoneSelectorContract.AnchorUntrackedCode,
                "This provider holds no current life for " + reference.Id + ".");
        LG_Zone? zone;
        try
        {
            var node = agent.CourseNode;
            if (node == null)
                throw new RuntimeContractException(ZoneSelectorContract.AnchorNodeMissingCode,
                    "The life has no course node to place it.");
            zone = node.m_zone;
        }
        catch (RuntimeContractException) { throw; }
        catch (Exception)
        {
            throw new RuntimeContractException(ZoneSelectorContract.AnchorNodeMissingCode,
                "The life's own course node did not read.");
        }
        return zones.TryCoordinates(zone, out var coordinates)
            ? RuntimeZones.Reference(_kernel.WorldEpoch, coordinates.Dimension, coordinates.Layer, coordinates.Zone)
            : null;
    }

    /// <summary>Every zone of the level being read, as the `zone` resource kind's whole answer. A world that
    /// holds no level holds no zone: the kind has an owner and the owner has nothing to name, which is an empty
    /// table rather than a refusal — the read itself succeeded.</summary>
    private IReadOnlyList<ResourceRef> ZoneResources()
    {
        if (ZoneTable() is not { } zones) return Array.Empty<ResourceRef>();
        var resources = new List<ResourceRef>(zones.Zones().Count);
        foreach (var zone in zones.Zones())
            if (zones.TryCoordinates(zone, out var coordinates)) resources.Add(ZoneReference(coordinates));
        return resources;
    }

    /// <summary>The zone one resource id names right now, or null when this level has no single zone at those
    /// coordinates. The id is the level's own three coordinates, so an id of another shape or of another level
    /// names nothing here instead of being reinterpreted.</summary>
    private ResourceRef? ResolveZoneResource(string resourceId)
        => ZoneCoordinatesOf(resourceId) is { } coordinates && ZoneTable()?.ZoneAt(coordinates) != null
            ? ZoneReference(coordinates) : null;

    /// <summary>The three coordinates a zone id spells, or null when the text is not that.</summary>
    private static ZoneCoordinates? ZoneCoordinatesOf(string resourceId)
    {
        if (resourceId == null || !resourceId.StartsWith(RuntimeZones.EntityKind + ":", StringComparison.Ordinal)) return null;
        var parts = resourceId[(RuntimeZones.EntityKind.Length + 1)..].Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var dimension) || !int.TryParse(parts[1], out var layer)
            || !int.TryParse(parts[2], out var zone)) return null;
        return new ZoneCoordinates(dimension, layer, zone);
    }

    /// <summary>One live zone as its resource reference: the level's own three coordinates and the world they
    /// belong to, built from the one table entry every zone reader goes through.</summary>
    private ResourceRef ZoneReference(ZoneCoordinates coordinates)
        => new(RuntimeZones.ResourceKind, RuntimeZones.Id(coordinates.Dimension, coordinates.Layer, coordinates.Zone));

    /// <summary>Every objective layer this level can be asked about, as the `objective` resource kind's whole
    /// answer: the objective machine's three fixed slots, each named by the same `layer:` reference the
    /// level-event rows publish an objective under, so a state read and the trigger that announced the layer name
    /// the same place with the same text (ruling 124.3). A slot the machine holds no objective data for is not a
    /// resource: it would otherwise resolve here and then refuse the very read it was named for.</summary>
    private static IReadOnlyList<ResourceRef> ObjectiveLayerResources()
    {
        var resources = new List<ResourceRef>(LevelObjectiveValueContract.Layers.Count);
        foreach (var layer in LevelObjectiveValueContract.Layers)
            if (ObjectiveLayer(layer) is not null) resources.Add(ObjectiveReference(layer));
        return resources;
    }

    /// <summary>The objective resource one id names right now, or null when the id is not a layer reference this
    /// package writes or the machine holds no objective for that layer. The id is the layer's own name, so
    /// `layer:main` names one slot of whatever level is being read and any other shape names nothing here.</summary>
    private static ResourceRef? ResolveObjectiveLayerResource(string resourceId)
        => LevelObjectiveValueContract.LayerOf(resourceId) is { } layer && ObjectiveLayer(layer) is not null
            ? ObjectiveReference(layer) : null;

    /// <summary>The machine's own layer member for one of the contract's three names, or null for a name the
    /// contract would have refused and for a layer this level has no objective data for. The data check is the
    /// machine's own, which is what keeps "this level has no objective here" from being guessed from a state that
    /// happens to read as untouched.</summary>
    private static LevelGeneration.LG_LayerType? ObjectiveLayer(string layer)
        => LevelObjectiveValueReader.LayerType(layer) is { } type && WardenObjectiveManager.HasWardenObjectiveDataForLayer(type)
            ? type : null;

    /// <summary>One layer as the objective resource the level-event rows publish it under.</summary>
    private static ResourceRef ObjectiveReference(string layer)
        => new(LevelObjectiveValueContract.ObjectiveResourceKind,
            LevelObjectiveValueContract.LayerReferencePrefix + layer);

    /// <summary>The Map player candidate set: the lives the player half still resolves right now, ordered by
    /// `id` ordinal so the answer does not depend on the module's dictionary layout. The half is read late,
    /// because this definition is built before it attaches to the registration it is declared on; a half that
    /// was never attached or has already been released is refused by name — the kernel reports
    /// `entity-candidates-failed` — never answered with an empty world. An empty list means the half is
    /// registered and tracks no live player, which is a real answer.</summary>
    private static IReadOnlyList<EntityReference> PlayerCandidates()
    {
        if (PlayerIdentityModule.Current is not { } players || !players.IsRegistered)
            throw new RuntimeContractException("player-module-unavailable", "No Map player identity is registered.");
        var current = players.CurrentPlayers();
        var ordered = new List<EntityReference>(current);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return ordered;
    }

    /// <summary>The native instance behind a map-object address, for the categories whose native owner can
    /// resolve one. Both owners can: a door is the entrance gate of the zone the address names, and a terminal
    /// is the terminal at that placement index in the zone the address names. A category with no such lookup
    /// answers nothing, and the read then simply does not happen.</summary>
    private static object? Resolve(string category, string address)
    {
        if (category == MapObjectCategories.Door)
            return MapObjectDoorAddress.TryParse(address) is { } door ? DoorObservation.ByAddress(door) : null;
        if (category == MapObjectCategories.Terminal)
            return MapObjectTerminalAddress.TryParse(address) is { } terminal
                ? TerminalObservation.ByAddress(terminal) : null;
        return null;
    }

    /// <summary>The terminal one action recipient names, for the terminal action rows. The address is read back
    /// with the terminal category's own grammar and resolved through the same level table the observation half
    /// reads, so a terminal action and a terminal fact can never disagree about which terminal an address names.
    /// The kernel's own instance check stays the caller's: this answers the instance, never an identity.</summary>
    private static LG_ComputerTerminal? TerminalFor(EntityReference reference)
        => TerminalObjectActions.Address(reference) is { } address ? TerminalObservation.ByAddress(address) : null;

    /// <summary>Runs one map-object readback on the owning thread. A native callback that fails disables the
    /// whole session, exactly as a player readback does: the failure reason is recorded once and no further
    /// callback work runs until restart.</summary>
    internal void GuardMapObjects(Action<MapObjectModule> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        var module = MapObjects;
        if (_disposed || _faulted || module == null) return;
        if (_kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { callback(module); }
        catch (Exception error)
        {
            _faulted = true;
            var errors = new List<Exception>();
            Cleanup(module.Dispose, errors);
            string detail = Describe(error);
            LastFault = detail;
            try { _report("Map map-object observation disabled until restart after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>Runs one level-object readback on the owning thread, with the same failure contract as every
    /// other native callback: one failure disables the session and is reported once.</summary>
    internal void GuardLevelObjects(Action<LevelObjectModule> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        var module = Levels;
        if (_disposed || _faulted || module == null) return;
        if (_kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { callback(module); }
        catch (Exception error)
        {
            _faulted = true;
            var errors = new List<Exception>();
            Cleanup(module.Dispose, errors);
            string detail = Describe(error);
            LastFault = detail;
            try { _report("Map level-object observation disabled until restart after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>Runs one level-event readback on the owning thread, with the same failure contract as every other
    /// native callback. The half is read late, so a callback that fires before the half exists publishes nothing
    /// rather than throwing through a Harmony patch.</summary>
    internal void GuardLevelEvents(Action<LevelEventModule> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        var module = LevelEvents;
        if (_disposed || _faulted || module == null) return;
        if (_kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { callback(module); _clock?.Wake(); }
        catch (Exception error)
        {
            _faulted = true;
            var errors = new List<Exception>();
            Cleanup(module.Dispose, errors);
            string detail = Describe(error);
            LastFault = detail;
            try { _report("Map level-event observation disabled until restart after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>The identity string the `level` attachment matcher compares, read once per world. The
    /// expedition-start row publishes the same string, so a plan mounted on a level and a plan waiting for that
    /// level's own start agree about which level they are in.</summary>
    internal string? LevelReference() => LevelIdentity.Read(_report)?.ToString();

    internal void Guard(Action<PlayerIdentityModule> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || _faulted || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { callback(Module); _clock?.Wake(); }
        catch (Exception error)
        {
            _faulted = true;
            var errors = new List<Exception>(); Cleanup(Module.ClearWorld, errors);
            string detail = Describe(error);
            LastFault = detail;
            try { _report("Map player identity disabled until restart after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>Runs one player-life transition on the owning thread. The half is read late and answers nothing
    /// while it is not attached, because a session that failed before it created the half must not publish a
    /// fact through a registration that never carried its bindings. A callback that fails disables the session
    /// exactly as every other native callback does.</summary>
    internal void GuardLifeFacts(Action<PlayerLifeFacts> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || _faulted || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        var facts = Lives;
        if (facts == null) return;
        try { callback(facts); }
        catch (Exception error)
        {
            _faulted = true;
            var errors = new List<Exception>(); Cleanup(Module.ClearWorld, errors);
            string detail = Describe(error);
            LastFault = detail;
            try { _report("Map player life observation disabled until restart after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>Runs one player-state transition on the owning thread. The half is read late and answers
    /// nothing while it is not attached, because a session that failed before it created the half must not
    /// publish a fact through a registration that never carried its bindings. A callback that fails disables the
    /// session exactly as every other native callback does.</summary>
    internal void GuardState(Action<PlayerStateFacts> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || _faulted || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        var facts = State;
        if (facts == null) return;
        try { callback(facts); }
        catch (Exception error)
        {
            _faulted = true;
            var errors = new List<Exception>();
            Cleanup(facts.Dispose, errors);
            string detail = Describe(error);
            LastFault = detail;
            try { _report("Map player state observation disabled until restart after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>Runs one player-event transition on the owning thread. The half is read late and answers nothing
    /// while it is not attached, because a session that failed before it created the half must not publish a
    /// fact through a registration that never carried its bindings. A callback that fails disables the session
    /// exactly as every other native callback does.</summary>
    internal void GuardEvents(Action<PlayerEventFacts> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || _faulted || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        var facts = Events;
        if (facts == null) return;
        try { callback(facts); }
        catch (Exception error)
        {
            _faulted = true;
            var errors = new List<Exception>();
            Cleanup(facts.Dispose, errors);
            string detail = Describe(error);
            LastFault = detail;
            try { _report("Map player event observation disabled until restart after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }
    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        // Unregister first: Runtime rejects dispatch/lifecycle re-entry or in-use providers
        // before native detours or session flags can be changed.
        var errors = new List<Exception>();
        DetachHalves(errors);
        // The clock is given back before the registration it hangs on, so no callback of this session's world is
        // left to run against a disposed provider. The light transitions this session scheduled are frames of this
        // session's world: a clock that somehow still ran for one frame after teardown must find nothing to write
        // into, and the table's own announcement must not reach a wake of the next session's clock.
        _clock?.Dispose();
        _clock = null;
        LightColorFades.WorkScheduled = null;
        LightColorFades.Clear();
        MapObjects?.Dispose();
        Module.Dispose();
        _faulted = true;
        Cleanup(_removeHooks, errors);
        _disposed = true;
        if (errors.Count != 0) throw new AggregateException("Map shutdown cleanup failed.", errors);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Map session requires its owning simulation thread.");
    }

    private static string Describe(Exception error)
    {
        string message;
        try { message = error.Message ?? ""; }
        catch (Exception unavailable) { message = "<message unavailable: " + unavailable.GetType().Name + ">"; }
        if (message.Length > 1900) message = message[..1900];
        var description = error.GetType().Name + ": " + message;
        return description.Length <= 2048 ? description : description[..2048];
    }

    internal static void PreserveCleanupFailure(Exception original, string key, Exception cleanup, Action<string> report)
    {
        // One bounded diagnostic. Hostile exception accessors or a broken logger must not replace the primary exception.
        string diagnostic = key + ": " + Describe(cleanup);
        try { original.Data[key] = cleanup; }
        catch (Exception attachment) { diagnostic += "; attachment=" + attachment.GetType().Name; }
        try { report(diagnostic); }
        catch (Exception reporter) { diagnostic += "; reporter=" + reporter.GetType().Name; }
        LastCleanupDiagnostic = diagnostic.Length <= 4096 ? diagnostic : diagnostic[..4096];
    }

    private static void Cleanup(Action action, List<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }
}
