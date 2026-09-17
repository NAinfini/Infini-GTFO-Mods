using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>
/// The `gtfo.map_object` half of the Map provider: the one map-object entity namespace, its read-only
/// observer, the `map-object` attachment matcher, and the one publisher every door and terminal hook goes
/// through. The provider half that knows both domains composes the one registration and this half declares
/// its own surface on it.
///
/// Publication is deliberately narrow. A hook reports only the kind of fact its own native callback carried;
/// the module reads the instance back, refuses the report when the instance no longer matches the address the
/// reference was built from, and publishes only a value that read produced and that differs from the last one
/// it published for that address. A repeated native sync of one state is therefore not a second event. Every
/// binding here observes native state, so each is registered with role `observe`, carries no result row, and
/// is published by the host only.
/// </summary>
public sealed partial class MapObjectModule : IDisposable
{
    // The entity namespace this provider registers is declared in `MapObjectModule.Identity.cs`, apart from this
    // half, so a game-independent contract can name it without referencing the module. Everything else about the
    // namespace lives here.
    private const string Prefix = EntityKind + ":";
    private const string AddressTag = "map-object.address=";
    private const string CategoryTag = "map-object.category=";
    private const string Faction = "map-object";
    // A map object is addressed, not alive: the shared snapshot's life state has no other honest value for a
    // door or a terminal, and inventing `entity.despawned` facts is not this provider's business.
    private const string LifeState = "alive";
    private sealed record Published(string StateKey, long Transition);
    private readonly RuntimeKernel _kernel;
    private readonly RuntimeLogLevel _logLevel;
    private readonly IMapObjectSource _doors;
    private readonly IMapObjectSource _terminals;
    private readonly Func<string, string, object?> _resolve;
    private readonly Func<object, EntityReference?>? _players;
    private readonly Func<bool> _authority;
    private readonly Action<string> _report;
    private readonly Action<string> _log;
    /// <summary>The reader of the current level's own identity, or null when this half has none (a fixture with
    /// no game-bound side). A `level` mount is the one kind judged from the mount target alone, so its reader is
    /// the only world read this module makes outside an observed instance.</summary>
    private readonly Func<MapLevelReference?>? _currentLevel;
    /// <summary>The game-bound half's climb from a hit object to the map object it belongs to, or null when this
    /// half has no such reader. A hit reaches this kind as whatever native object the bullet was resolved
    /// against — a collider on a door blade, a terminal screen — and only the half that owns the native types
    /// can say which map object that is. The module still owns the decision: it classifies the instance this
    /// answers with and addresses it exactly as one handed over directly, so a climb that names nothing leaves
    /// the kind unresolved instead of inventing an address.</summary>
    private readonly Func<object, object?>? _hit;
    private RuntimeModuleHandle? _registration;
    private IReadOnlyDictionary<string, RuntimeSubscriptionGate>? _gates;
    private RuntimeLifecycleSubscription? _lifecycle;
    /// <summary>The provider's session half, composed on the one registration this module owns. A session fact is
    /// not a map object, so its publishing rules live in their own half; it is created here because this module
    /// is where the registration and the one native-callback path for this provider already exist.</summary>
    private ExpeditionModule? _expeditions;
    private readonly Dictionary<string, Published> _published = new(StringComparer.Ordinal);
    /// <summary>This world's level identity, read at most once per world: unreadable and absent are different
    /// answers, and both are worth remembering for the world they were read in.</summary>
    private MapLevelReference? _level;
    private bool _levelRead;
    // The accepted command of a terminal, keyed by address text: the native state change does not carry the
    // command that caused it, so the result fact reports the command this terminal's own entry last carried.
    private readonly Dictionary<string, string> _accepted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private long _publishedFacts;
    private bool _disposed;

    public MapObjectModule(RuntimeKernel kernel, RuntimeLogLevel logLevel, IMapObjectSource doors,
        IMapObjectSource terminals, Func<string, string, object?> resolve, Func<object, EntityReference?>? players,
        Func<bool> authority, Action<string> report, Action<string> log, Func<MapLevelReference?>? currentLevel = null,
        Func<object, object?>? hit = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _logLevel = logLevel;
        _doors = doors ?? throw new ArgumentNullException(nameof(doors));
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _players = players;
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _currentLevel = currentLevel;
        _hit = hit;
    }

    /// <summary>Registers this half on the one Map provider definition. The definition is composed by the
    /// caller that knows both halves — the game-bound session — because one registration has to declare every
    /// Map entity namespace and the runtime accepts exactly one provider of one; this half only adds its own
    /// category, its own instance lookup, its own observer and the `map-object` attachment matcher.</summary>
    public void Register(RuntimeModule definition)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(definition);
        if (_registration != null) throw new InvalidOperationException("Map objects are already registered.");
        // A subscription that cannot be taken leaves no registration behind: an unobservable module would keep
        // a world's table across a level change, which is worse than a package that failed to load.
        _registration = _kernel.RegisterModule(Declare(definition), _logLevel);
        try { _lifecycle = _registration.ObserveLifecycle(OnLifecycle); }
        catch { _registration.Dispose(); _registration = null; throw; }
        // The session half publishes through this registration and nothing else, so it is born exactly when the
        // registration is and dies with it: an event published after the provider is gone would be a report about
        // a world nothing is left to answer for.
        _expeditions = new ExpeditionModule(_registration, _kernel, _authority, _report);
        // The subscription gates of this provider's own bindings, taken here because this is the one moment the
        // registration exists and its bindings are already facts of the registry.
        _gates = _registration.SubscriptionGates();
    }

    /// <summary>Adds this half's own runtime surface to the caller's definition. The definition is a record
    /// whose dictionaries are shared, so each one is copied before this half's entry is added rather than the
    /// caller's definition being mutated.</summary>
    private RuntimeModule Declare(RuntimeModule definition) => definition with
    {
        EntityResolvers = Merge(definition.EntityResolvers, EntityKind, IsCurrent),
        EntityInstanceResolvers = Merge(definition.EntityInstanceResolvers, EntityKind, ResolveInstance),
        EntityObservers = Merge(definition.EntityObservers, EntityKind, Observe),
        AttachmentMatchers = Attachments(definition)
    };

    /// <summary>The mount kinds this half owns. A map object is matched against the event's own subject; a level
    /// is not an entity at all, so its kind is registered as a scope and judged from the reference alone — which
    /// is also what lets it judge the events that carry no subject. A half without a level reader declares no
    /// `level` matcher: a kind nothing can compare is better refused at load than matched loosely.
    private IReadOnlyDictionary<string, AttachmentMatcherRegistration> Attachments(RuntimeModule definition)
    {
        var owned = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
        {
            ["map-object"] = AttachmentMatcherRegistration.BySubject(MatchesAttachment)
        };
        if (_currentLevel != null) owned["level"] = AttachmentMatcherRegistration.ByScope(MatchesLevel);
        return Merge(definition.AttachmentMatchers, owned);
    }

    private static Dictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T>? declared, string key, T handler)
    {
        var merged = declared == null
            ? new Dictionary<string, T>(StringComparer.Ordinal)
            : new Dictionary<string, T>(declared, StringComparer.Ordinal);
        merged[key] = handler;
        return merged;
    }

    /// <summary>The caller's declarations with this half's own kinds added; an entry the caller already declared
    /// is replaced, because one kind has exactly one owner and this half is the one registering here.</summary>
    private static Dictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T>? declared, IReadOnlyDictionary<string, T> owned)
    {
        var merged = declared == null
            ? new Dictionary<string, T>(StringComparer.Ordinal)
            : new Dictionary<string, T>(declared, StringComparer.Ordinal);
        foreach (var entry in owned) merged[entry.Key] = entry.Value;
        return merged;
    }
    public bool IsRegistered => !_disposed && _registration is { IsRegistered: true };

    /// <summary>Whether a loaded plan or a suspended wait is listening on one of this provider's own bindings. The
    /// gates are taken at registration and the kernel refreshes each one in place wherever the subscription table
    /// changes, so this answers for the world as it is now rather than as it was when the caller looked last. A
    /// binding this provider does not publish answers false: a question about somebody else's trigger is not this
    /// module's to answer.</summary>
    internal bool HasSubscribers(string bindingId)
        => _gates != null && _gates.TryGetValue(bindingId, out var gate) && gate.HasSubscribers;

    /// <summary>The one Map registration. The package's other domains attach to it instead of registering a
    /// second provider of the same identity namespace.</summary>
    public RuntimeModuleHandle Registration
        => _registration ?? throw new InvalidOperationException("The map-object module is not registered yet.");

    /// <summary>The one session half of this provider. A native callback reports an expedition end here; the
    /// half answers whether the event reached the kernel and never dispatches anything itself.</summary>
    public ExpeditionModule Expeditions
        => _expeditions ?? throw new InvalidOperationException("The map-object module is not registered yet.");

    /// <summary>Events this module actually handed to the kernel (status `queued`). A client, an unreadable
    /// instance or a repeated state never contributes, which is what makes the count assertable.</summary>
    public long PublishedFacts => _publishedFacts;

    /// <summary>The world this provider publishes in: the kernel's own epoch, which every entity id, event scope
    /// and reference this module answers with carries. A game-bound reader that keeps its own table of native
    /// instances keys that table on this, so an address read in one level is never answered from the next.</summary>
    public long WorldEpoch => _kernel.WorldEpoch;

    /// <summary>Drops the module's own per-world tables at a world transition. The kernel's world epoch is what
    /// invalidates earlier ids; this only releases the memory of a level that no longer exists, and drops the
    /// level identity read in the world that just ended so the next dispatch reads the new level's own.</summary>
    public void BeginWorld()
    {
        CheckThread();
        _published.Clear();
        _accepted.Clear();
        _generators.Clear();
        _level = null;
        _levelRead = false;
    }

    /// <summary>One door status the door's own sync callback carried. The status is re-read from the instance
    /// after the callback returned, so the fact reports the door's state and not the callback's argument.</summary>
    public void DoorStateChanged(object door)
    {
        CheckThread();
        if (!TrySubject(_doors, door, out var subject) || !Read(subject, out var observation)) return;
        var snapshot = observation.Door!;
        JsonElement? phase = snapshot.Phase is { } value ? RuntimeJson.From(value) : null;
        Publish(subject, MapObjectContract.DoorStateFact, snapshot.StateKey, Payload(
            // A door status that is not an interaction transition has no phase, and the port is declared
            // optional: the key is left out instead of publishing a phase the door never reported.
            ("door", RuntimeJson.From(subject.Reference)),
            ("state", RuntimeJson.From(snapshot.State)),
            ("phase", phase)));
    }

    /// <summary>One door state the lock component reported. The lock callback carries the same door state as
    /// the door's own sync callback and both facts are read from the same instance, so a native lock callback
    /// republishing one status is refused by the lock fact's own state key.</summary>
    public void DoorLockChanged(object door)
    {
        CheckThread();
        if (!TrySubject(_doors, door, out var subject) || !Read(subject, out var observation)) return;
        var snapshot = observation.Door!;
        Publish(subject, MapObjectContract.LockStateFact,
            "lock:" + (snapshot.Locked ? "locked" : "unlocked") + ":" + snapshot.Key, Payload(
                ("door", RuntimeJson.From(subject.Reference)),
                ("locked", RuntimeJson.From(snapshot.Locked)),
                ("key", RuntimeJson.From(snapshot.Key))));
    }

    /// <summary>One terminal state the terminal's own state callback carried. Session and result are two facts
    /// read from that one state, so a state that is not an outcome still opens or closes a session. The
    /// command is the one the terminal's own request entry last carried.</summary>
    public void TerminalStateChanged(object terminal, object? actor)
    {
        CheckThread();
        if (!TrySubject(_terminals, terminal, out var subject) || !Read(subject, out var observation)) return;
        var snapshot = observation.Terminal!;
        if (snapshot.Outcome != null)
            Publish(subject, MapObjectContract.TerminalResultFact, "result:" + MapObjectOutcomes.Name(snapshot.Outcome.Value), Payload(
                ("terminal", RuntimeJson.From(subject.Reference)),
                ("command", RuntimeJson.From(Accepted(subject.Address))),
                ("outcome", RuntimeJson.From(snapshot.Outcome.Value))));
        Publish(subject, MapObjectContract.TerminalSessionFact,
            "session:" + (snapshot.SessionActive ? "active" : "inactive"), Payload(
                ("terminal", RuntimeJson.From(subject.Reference)),
                ("actor", Actor(actor) is { } player ? RuntimeJson.From(player) : null),
                ("active", RuntimeJson.From(snapshot.SessionActive))));
    }

    /// <summary>One terminal command the terminal accepted. The native entry carries the command itself, so the
    /// fact reports that value and never a reconstructed one.</summary>
    public void TerminalCommandAccepted(object terminal, int command, object? actor)
    {
        CheckThread();
        if (!TrySubject(_terminals, terminal, out var subject) || !Read(subject, out var observation)) return;
        string name = MapObjectTerminalCommand.Name(command);
        if (!Publish(subject, MapObjectContract.TerminalCommandFact, "command:" + name, Payload(
            ("terminal", RuntimeJson.From(subject.Reference)),
            // The native command entry carries no player, so the actor port stays absent rather than publishing
            // a null a step would have to read as a player that was never observed.
            ("actor", Actor(actor) is { } player ? RuntimeJson.From(player) : null),
            ("command", RuntimeJson.From(name))))) return;
        _accepted[subject.Address.ToString()] = name;
    }

    /// <summary>One published payload, built port by port. A port whose value the native side could not read is
    /// left out instead of being published as a JSON null: the declaration marks it optional, and an absent port
    /// is the framework's own way to say "not observable" — a null would claim the port was written.</summary>
    private static JsonElement Payload(params (string Id, JsonElement? Value)[] ports)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in ports) if (value is { } present) payload[id] = present;
        return RuntimeJson.From(payload);
    }

    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        if (value.Kind == RuntimeLifecycleKind.WorldChanged) BeginWorld();
    }

    /// <summary>Publishes one fact of the interaction family: the binding is the fact's own name under the
    /// map-object provider, which is what the door and terminal rows declare.</summary>
    private bool Publish(MapObjectSubject subject, string fact, string stateKey, JsonElement outputs)
        => Publish(subject, fact, MapObjectContract.BindingFor(fact), stateKey, outputs);

    /// <summary>Publishes one fact of one subject through an explicit binding. The instance was re-read after
    /// the native callback returned: an instance whose address no longer reads the same way publishes nothing,
    /// and neither does one whose members cannot be read in full. A category whose rows another contract declares
    /// — the generator family's — names the binding its row carries rather than a second copy of the resolver.</summary>
    private bool Publish(MapObjectSubject subject, string fact, string binding, string stateKey, JsonElement outputs)
    {
        if (_disposed || _registration is not { IsRegistered: true } registration) return false;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (!_authority())
        {
            ReportOnce("client:" + subject.Address,
                "map-object fact observed on a non-authoritative peer: the host publishes map-object state, a client does not.");
            return false;
        }
        string address = subject.Address.ToString();
        if (_published.TryGetValue(address, out var last) && last.StateKey == stateKey) return false;
        long transition = last == null ? 1 : last.Transition + 1;
        _published[address] = new Published(stateKey, transition);
        // Nothing is listening on this binding: the kernel would answer `no-consumer` for the very event this
        // call is about to build, so the event value is never built. The state above is still recorded, because a
        // fact observed while nobody listened is a fact this module has already reported.
        if (Unsubscribed(binding)) return false;
        var result = registration.Publish(new RuntimeEvent(
            FactId(subject, fact, transition), binding, _kernel.WorldEpoch,
            Math.Max(0, _kernel.CurrentTick), Scope(), outputs));
        if (result.Status == "queued") { _publishedFacts++; return true; }
        if (result.Status == "rejected")
            ReportOnce("publish:" + fact + ":" + result.Code, "map-object " + subject.Category + " fact rejected: " + result.Code);
        return false;
    }

    /// <summary>Whether no loaded plan is mounted on one of this half's own bindings. The kernel precomputes the
    /// answer and refreshes it where the subscription table changes, so a callback with nothing to say to anyone
    /// skips the event value instead of building one the kernel would refuse as `no-consumer`. A binding this
    /// registration does not declare is not this half's to skip, and the kernel still decides it.</summary>
    private bool Unsubscribed(string binding)
        => _gates is { } gates && gates.TryGetValue(binding, out var gate) && !gate.HasSubscribers;

    /// <summary>The event id is the fact's own identity: world, address, fact kind and the subject's own
    /// transition number. A repeated sync of one state produces neither a new transition nor a new id.</summary>
    private string FactId(MapObjectSubject subject, string fact, long transition)
        => EntityKind + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + subject.Address + ":" + transition.ToString(CultureInfo.InvariantCulture);

    private string Scope() => "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);

    private bool TrySubject(IMapObjectSource source, object instance, out MapObjectSubject subject)
    {
        subject = null!;
        if (instance == null) return false;
        if (source.TryAddress(instance) is not { } address) return false;
        subject = new MapObjectSubject(EntityId(address), address, source, instance);
        return true;
    }

    /// <summary>Re-reads the instance a hook reported, once, and refuses the report when the instance no longer
    /// reads as the address it was addressed with or no longer carries this fact's own members.</summary>
    private bool Read(MapObjectSubject subject, out MapObjectObservation observation)
    {
        observation = null!;
        var read = subject.Source.Read(subject.Instance);
        if (read != null && read.AddressMatches && subject.Source.IsCurrentAddress(subject.Instance, subject.Address)
            && ReadsAsCategory(read, subject))
        {
            observation = read;
            return true;
        }
        subject.Source.Report("map-object observation refused for " + subject.Address
            + ": the native instance no longer reads as it was addressed.");
        return false;
    }

    /// <summary>Whether one re-read instance carries the section its category promises: a door's own snapshot, a
    /// terminal's, the zone record, or — for the generator category — the generator's own reading or its group's,
    /// which the address's own key form tells apart. A read that answers with another category's section is not
    /// this fact's reading.</summary>
    private static bool ReadsAsCategory(MapObjectObservation read, MapObjectSubject subject)
    {
        bool generator = subject.Category == MapObjectGeneratorAddress.Category;
        bool group = generator && MapObjectGeneratorAddress.IsGroupKey(subject.Address.Key);
        return (read.Door != null) == (subject.Category == MapObjectCategories.Door)
            && (read.Terminal != null) == (subject.Category == MapObjectCategories.Terminal)
            && (read.Zone != null) == (subject.Category == TriggerZoneAddress.Category)
            && (read.Generator != null) == (generator && !group)
            && (read.Group != null) == group;
    }

    /// <summary>The entity id of one address in one world: the one map-object namespace, then the address
    /// text. The id is derived from the address alone, so it is stable across readings and reverses without a
    /// table, and a world change invalidates it through the epoch it carries.</summary>
    private EntityReference EntityId(MapObjectReference address)
        => new(Prefix + address, _kernel.WorldEpoch, 1);

    /// <summary>Whether a reference is still current. A category that can hand back the instance it names is
    /// checked against that instance; a category that cannot — this level's zone table is built while the level
    /// is being built, so a door and a terminal both have windows where nothing can be resolved back — is
    /// answered from the world the reference carries, which is what the entity validation itself already did.
    /// A rejection here is reserved for an instance that positively contradicts the reference.</summary>
    private bool IsCurrent(EntityReference reference)
    {
        if (reference.WorldEpoch != _kernel.WorldEpoch) return false;
        if (!Read(reference, out var source, out var address)) return false;
        return Resolve(source, address) is not { } instance || source.IsCurrentAddress(instance, address);
    }

    /// <summary>The native instance an address names right now, or null when the category cannot resolve one.
    /// A terminal is resolved through the terminal manager's own registry; a door is resolved through the
    /// category's instance resolver, which the kernel routes to the provider that registered it.</summary>
    private object? Resolve(IMapObjectSource source, MapObjectReference address)
        => _resolve(source.Category, address.ToString());

    /// <summary>The current reference of a native object this kind owns: the object's own address, read from its
    /// own members through the category that answers for its type. A hit reaches the same entry with the object
    /// the bullet was resolved against, which is one of the map object's own components rather than the map
    /// object itself, so the game-bound half first climbs to the map object that component belongs to — and a
    /// hit object with no map object above it names nothing and leaves the kind unresolved.</summary>
    private EntityReference? ResolveInstance(object instance)
    {
        if (_kernel.StartupState != RuntimeStartupState.Ready) return null;
        var mapObject = _hit?.Invoke(instance) ?? instance;
        return Source(mapObject) is { } source && source.TryAddress(mapObject) is { } address
            ? EntityId(address) : null;
    }

    private RuntimeEntitySnapshot? Observe(EntityReference reference)
    {
        if (!Read(reference, out var source, out var address)) return null;
        if (Resolve(source, address) is not { } instance) return null;
        if (!source.IsCurrentAddress(instance, address)) return null;
        var observation = source.Read(instance);
        if (observation == null || !observation.AddressMatches) return null;
        // The snapshot carries the object's own position and nothing invented: a position that did not read
        // leaves the whole observation unavailable, exactly as the framework's three-coordinate contract asks.
        if (source.Position(instance) is not { Length: 3 } position) return null;
        return new RuntimeEntitySnapshot(reference, EntityKind, Faction, LifeState,
            Tags(address, observation), Array.Empty<string>(), position);
    }

    /// <summary>The address an entity id was built from, with the category's own reader: the category is the
    /// address's first segment and the reader is looked up by it, so an id that names another category or
    /// another shape is not this provider's and is refused rather than reinterpreted.</summary>
    private bool Read(EntityReference reference, out IMapObjectSource source, out MapObjectReference address)
    {
        source = null!; address = null!;
        if (!reference.Id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string text = reference.Id[Prefix.Length..];
        int slash = text.IndexOf('/');
        if (slash <= 0 || Source(text[..slash]) is not { } found) return false;
        if (found.Parse(text) is not { } parsed) return false;
        source = found; address = parsed;
        return true;
    }

    /// <summary>The `map-object` attachment matcher: a plan attached to an address is dispatched only for the
    /// event's own subject, and only for the subject's own category. The subject's id is the address this
    /// provider read off the instance before it published, so an id that names the mounted address is a
    /// positive reading of that object; a category that can also resolve an address back through native state
    /// must additionally agree, which is what keeps a stale mount from firing for a replaced object.</summary>
    private bool MatchesAttachment(string? category, string reference, EntityReference subject)
    {
        if (category == null || reference.Length == 0) return false;
        if (Source(category) is not { } source || source.Parse(reference) is not { } address) return false;
        if (!Read(subject, out var subjectSource, out var subjectAddress)) return false;
        if (!ReferenceEquals(subjectSource, source) || address != subjectAddress) return false;
        return Resolve(source, address) is not { } instance || source.IsCurrentAddress(instance, address);
    }

    /// <summary>The `level` attachment matcher: a plan attached to one level's identity is dispatched only in
    /// that level. The reference is parsed strictly, and a reference that is not one is reported once and
    /// answers false — never "this must be my level then". A world with no readable expedition answers false the
    /// same way, and says so once, so a behaviour that never fired can be told from one that never matched.</summary>
    private bool MatchesLevel(string? category, string reference)
    {
        if (category != null || MapLevelReference.TryParse(reference) is not { } mounted)
        {
            ReportOnce("level-reference:" + reference, "level attachment reference is not `<rundown block id>:<tier A-E>:<tier index>` "
                + "in decimal without leading zeros: " + reference);
            return false;
        }
        if (Level() is not { } current)
        {
            ReportOnce("level-identity", "level attachment could not be compared: this world reports no readable "
                + "expedition, so no level mount matches in it.");
            return false;
        }
        return mounted == current;
    }

    /// <summary>This world's level identity, read once through the reader the game-bound half handed over and
    /// kept until the next world begins. The reader is the one that records what it read, where the game's own
    /// key for the same expedition is available.</summary>
    private MapLevelReference? Level()
    {
        if (_levelRead) return _level;
        _levelRead = true;
        _level = _currentLevel!();
        return _level;
    }

    /// <summary>The reader of one category. A native instance is classified by the source that answers for its
    /// type, so a door is never read through the terminal reader or the other way round. The trigger zones are the
    /// third category this provider addresses, answered by `TriggerZoneMapObject.cs`, and the power generators
    /// are the fourth, answered by `GeneratorMapObject.cs`.</summary>
    private IMapObjectSource? Source(string category)
        => _doors.Category == category ? _doors
            : _terminals.Category == category ? _terminals
            : _zoneSource.Category == category ? _zoneSource
            : _generatorSource.Category == category ? _generatorSource : null;

    private IMapObjectSource? Source(object instance)
        => _doors.TryAddress(instance) != null ? _doors
            : _terminals.TryAddress(instance) != null ? _terminals
            : _zoneSource.TryAddress(instance) != null ? _zoneSource
            : _generatorSource.TryAddress(instance) != null ? _generatorSource : null;

    /// <summary>The actor of an interaction fact, resolved through the player domain's own instance lookup so a
    /// map object never carries a player identity of its own. A caller that cannot name the actor publishes the
    /// port absent rather than inventing one.</summary>
    private EntityReference? Actor(object? instance)
        => instance == null || _players == null ? null : _players(instance);

    private string Accepted(MapObjectReference address)
        => _accepted.TryGetValue(address.ToString(), out var command) ? command : "";

    private IReadOnlyList<string> Tags(MapObjectReference address, MapObjectObservation observation)
    {
        var tags = new List<string>(4) { CategoryTag + address.Category, AddressTag + address.ToString() };
        if (observation.Door != null) tags.Add("map-object.door.status=" + observation.Door.State);
        if (observation.Terminal != null) tags.Add("map-object.terminal.status=" + observation.Terminal.State);
        if (observation.Generator != null) tags.Add("map-object.generator.state=" + (observation.Generator.Powered ? "powered" : "unpowered"));
        return tags;
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _report(message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Map objects require the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        _expeditions?.Dispose();
        _expeditions = null;
        _registration?.Dispose();
        _published.Clear();
        _accepted.Clear();
        _generators.Clear();
    }
}
