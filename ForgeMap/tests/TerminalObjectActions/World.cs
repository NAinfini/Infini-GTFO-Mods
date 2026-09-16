using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;
using SNetwork;
using TerminalObjectAction = ForgeMap.Native.TerminalObjectActions;

namespace ForgeMapTests.TerminalActionFacts;

/// <summary>One case's world: a level of doubles with a floor, zones and terminals; a kernel with the one
/// `gtfo.map_object` kind registered exactly the way the Map provider registers it — a namespace resolver plus
/// the instance resolver that maps a live terminal back to its reference — and one production
/// `TerminalObjectActions` over both.
///
/// The action itself, the two readers it goes through (`TerminalActions`, `TerminalObservation`) and the zone
/// table are the production files compiled into this assembly, so a case observes the production read and the
/// production write call. Only the game's own members are doubles, and they record what they were asked, which
/// is what makes "the action asked the terminal's own parser and the manager's own entry" an assertion rather
/// than a claim.
///
/// The module is registered from the real `TerminalObjectContract` rows and bindings, so the handler's declared
/// port shape is resolved against the capability graph this slice really declares: a port renamed on one side
/// fails the fixture's own construction, not a later dispatch.</summary>
internal sealed class World : IDisposable
{
    internal const string Kind = "gtfo.map_object";
    internal const long WorldEpoch = 7;
    internal const int Dimension = 0;
    internal const int Layer = 0;
    internal const int Zone = 1;

    private readonly RuntimeModuleHandle _registration;
    private readonly Dictionary<LG_ComputerTerminal, EntityReference> _byInstance = new();
    private readonly Dictionary<EntityReference, LG_ComputerTerminal> _byReference = new();
    private readonly HashSet<EntityReference> _live = new();

    internal RuntimeKernel Kernel { get; }
    internal TerminalObjectAction Actions { get; }
    internal List<string> Reports { get; } = new();
    /// <summary>The floor every address in this case is read from. A zone taken out of it stops being an
    /// addressable coordinate, which is what a level that never built it looks like.</summary>
    internal LG_Floor Floor { get; }
    /// <summary>When clear, the kernel has no authoritative world, which is the phase the action's host gate
    /// refuses. Nothing else in these cases turns it off.</summary>
    internal bool Authoritative { get; set; } = true;
    /// <summary>When set, the terminal lookup throws instead of answering, which is what a provider whose own
    /// table is mid-teardown looks like from here.</summary>
    internal bool LookupThrows { get; set; }

    internal World()
    {
        LG_LevelBuilder.Current = new LG_LevelBuilder { m_currentFloor = new LG_Floor { allZones = new List<LG_Zone>() } };
        Floor = LG_LevelBuilder.Current.m_currentFloor!;
        LG_ComputerTerminalManager.Reset();
        LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager();
        ZoneIndex.Reset();
        Kernel = new RuntimeKernel(new("fixture.terminal.actions", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(WorldEpoch);
        Actions = new TerminalObjectAction(Kernel, () => Authoritative, Lookup, Reports.Add);
        _registration = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
            {
                [TerminalObjectContract.CommandHandlerName] = Actions.HandleCommand,
                [TerminalObjectContract.OutputHandlerName] = Actions.HandleOutput
            },
            TerminalObjectContract.Supports())
        {
            // Both directions of the one `gtfo.map_object` table, exactly as the Map provider registers them:
            // the namespace resolver answers whether a reference is still current, the instance resolver answers
            // which reference a native object is. The action checks its own lookup against both.
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [Kind] = reference => _live.Contains(reference)
            },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
            {
                [Kind] = instance => instance is LG_ComputerTerminal terminal
                    && _byInstance.TryGetValue(terminal, out var reference) ? reference : null
            },
            Shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
            {
                [TerminalObjectContract.CommandHandlerName] = TerminalObjectContract.CommandShape,
                [TerminalObjectContract.OutputHandlerName] = TerminalObjectContract.OutputShape
            }
        }, RuntimeLogLevel.Off);
    }

    /// <summary>The registry seed this fixture registers: the two real capability rows and the two real binding
    /// rows from the game-independent contract under the Map provider's own id, so the rows a case exercises are
    /// the rows the package declares.</summary>
    private static string Registry() => RuntimeJson.From(new
    {
        providers = new[]
        {
            new { id = ModuleDefinition.ProviderId, kind = "native", version = ModuleDefinition.Version,
                dependencies = Array.Empty<string>() }
        },
        capabilities = TerminalObjectContract.Rows(),
        bindings = TerminalObjectContract.Bindings()
    }).GetRawText();

    /// <summary>Starts the runtime and settles one authoritative tick. The kernel answers no entity reference
    /// before its world has an authority, which is exactly the state the action's own gate refuses.</summary>
    internal void Start()
    {
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, Authoritative);
    }

    /// <summary>Turns the world non-authoritative from the next tick on, the way a machine that lost the master
    /// role sees it.</summary>
    internal void LoseAuthority()
    {
        Authoritative = false;
        Kernel.Advance(Kernel.CurrentTick + 1, false);
    }

    /// <summary>The manager the command row's own entry belongs to, taken away: the game's manager is stood up
    /// while a level is built and gone with it, so a request that arrives with none is refused by name.</summary>
    internal void StandDownManager() => LG_ComputerTerminalManager.Current = null;

    /// <summary>One zone in this case's floor, with its own coordinates and data block.</summary>
    internal LG_Zone AddZone(int dimension = Dimension, LG_LayerType layer = LG_LayerType.MainLayer,
        eLocalZoneIndex local = eLocalZoneIndex.Zone_1)
    {
        var zone = new LG_Zone { m_layer = new LG_Layer { m_type = layer },
            m_dimensionIndex = (LevelGeneration.eDimensionIndex)dimension,
            LocalIndex = local, m_settings = new LG_ZoneSettings { m_zoneData = new GameData.ExpeditionZoneData() } };
        Floor.allZones!.Add(zone);
        ZoneIndex.Reset();
        return zone;
    }

    /// <summary>One terminal placed in a zone's own terminal list, with a live command interpreter that accepts
    /// the named commands. The reference is the one the production reader builds from the instance, so the
    /// fixture's own table and the kernel's instance lookup agree by construction.</summary>
    internal LG_ComputerTerminal Terminal(LG_Zone zone, params TERM_Command[] commands)
    {
        var terminal = new LG_ComputerTerminal
        {
            CurrentStateName = TERM_State.PlayerInteracting,
            SpawnNode = new AIGraph.AIG_CourseNode { m_zone = zone },
            m_command = new LG_ComputerTerminalCommandInterpreter()
        };
        foreach (var command in commands) terminal.m_command.Commands[command.ToString()] = command;
        return Place(zone, terminal);
    }

    /// <summary>The same placement without an interpreter: a terminal the level placed but whose own parser
    /// cannot be asked, which the command row refuses by name.</summary>
    internal LG_ComputerTerminal Bare(LG_Zone zone)
        => Place(zone, new LG_ComputerTerminal
        {
            CurrentStateName = TERM_State.PlayerInteracting,
            SpawnNode = new AIGraph.AIG_CourseNode { m_zone = zone },
            m_command = null
        });

    private LG_ComputerTerminal Place(LG_Zone zone, LG_ComputerTerminal terminal)
    {
        (zone.TerminalsSpawnedInZone ??= new List<LG_ComputerTerminal>()).Add(terminal);
        ZoneIndex.Reset();
        var address = TerminalObservation.Address(terminal) ?? throw new Exception("The terminal carried no address.");
        var reference = new EntityReference(Kind + ":" + address, WorldEpoch, 1);
        _byInstance[terminal] = reference;
        _byReference[reference] = terminal;
        _live.Add(reference);
        return terminal;
    }

    /// <summary>The address of a terminal this fixture placed, spelled the way the entity id spells it.</summary>
    internal EntityReference ReferenceOf(LG_ComputerTerminal terminal)
        => _byInstance.TryGetValue(terminal, out var reference)
            ? reference : throw new Exception("The terminal was not placed by this fixture.");

    /// <summary>A current reference of the map-object kind that names no terminal: a door address. The action
    /// answers for terminals only, so this is the "another category of my own kind" case.</summary>
    internal EntityReference DoorReference(string address = "door/0/0/1/security")
    {
        var reference = new EntityReference(Kind + ":" + address, WorldEpoch, 1);
        _live.Add(reference);
        return reference;
    }

    /// <summary>A current reference of a kind this action does not own at all.</summary>
    internal EntityReference ForeignReference(string id = "fixture.other:1")
    {
        var reference = new EntityReference(id, WorldEpoch, 1);
        _live.Add(reference);
        return reference;
    }

    /// <summary>A reference of the right shape in a world that has ended.</summary>
    internal EntityReference StaleWorldReference(LG_ComputerTerminal terminal)
        => ReferenceOf(terminal) with { WorldEpoch = WorldEpoch - 1 };

    /// <summary>Retires a terminal the way a level teardown would: the kernel's own resolver no longer knows
    /// it, so the reference stops being current.</summary>
    internal void Retire(LG_ComputerTerminal terminal)
    {
        if (_byInstance.Remove(terminal, out var reference)) { _byReference.Remove(reference); _live.Remove(reference); }
    }

    /// <summary>Makes an instance resolve to a reference that is not the one asked about, which is the state a
    /// provider's own table is in while it is being rebuilt. The answered reference is returned so a case can ask
    /// about the reference the instance *should* have answered for.</summary>
    internal EntityReference Alias(LG_ComputerTerminal terminal, EntityReference reference)
    {
        var previous = _byInstance[terminal];
        _byInstance[terminal] = reference;
        return previous;
    }

    private LG_ComputerTerminal? Lookup(EntityReference reference)
    {
        if (LookupThrows) throw new InvalidOperationException("fixture lookup failure");
        return _byReference.TryGetValue(reference, out var terminal) ? terminal : null;
    }

    /// <summary>One action request frame as the dispatcher would hand it to the handler. An input a case does not
    /// name is absent from the frame, which is what a plan that wired nothing into it produces.</summary>
    internal static JsonElement Request(params (string Port, object? Value)[] inputs)
    {
        var frame = new JsonObject();
        foreach (var (port, value) in inputs) frame[port] = JsonValue.Create(value);
        return RuntimeJson.Parse(frame.ToJsonString());
    }

    /// <summary>One node parameter bag, the way the plan's own structural choices arrive.</summary>
    internal static JsonElement Parameters(params (string Id, object? Value)[] parameters)
    {
        var bag = new JsonObject();
        foreach (var (id, value) in parameters) bag[id] = JsonValue.Create(value);
        return RuntimeJson.Parse(bag.ToJsonString());
    }

    /// <summary>A reference as a request frame carries one: the entity's own id, world and life.</summary>
    internal static object Entity(EntityReference reference)
        => new { id = reference.Id, worldEpoch = reference.WorldEpoch, lifeEpoch = reference.LifeEpoch };

    /// <summary>The handler invoked the way the kernel invokes it. The command context is the SDK's own type with
    /// an internal constructor, so it is created through reflection: the constructor is found by the argument
    /// types a fixture can supply, and a trailing flag a framework version adds is filled from its own parameter
    /// name. A framework that renames or retypes one of these arguments fails here, in the fixture, rather than
    /// being papered over.</summary>
    internal CommandResult Command(JsonElement inputs) => Actions.HandleCommand(Context(inputs, Parameters()));

    internal CommandResult Output(JsonElement inputs, JsonElement parameters) => Actions.HandleOutput(Context(inputs, parameters));

    private static CommandContext Context(JsonElement inputs, JsonElement parameters)
    {
        var origin = new RuntimeEvent("fixture.terminal.request", TerminalObjectContract.CommandBindingId, WorldEpoch, 0,
            "fixture.terminal.scope", RuntimeJson.EmptyObject);
        var arguments = new object?[]
        {
            origin, 0L, "fixture.command", "fixture.plan", "fixture.resource", "1", "fixture.node", parameters, inputs
        };
        var constructor = typeof(CommandContext)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(candidate => candidate.GetParameters().Length >= arguments.Length
                && candidate.GetParameters().Take(arguments.Length).Select(parameter => parameter.ParameterType)
                    .SequenceEqual(arguments.Select(argument => argument!.GetType())))
            ?? throw new Exception("No CommandContext constructor takes this fixture's arguments.");
        var supplied = arguments.Concat(constructor.GetParameters().Skip(arguments.Length)
            .Select(parameter => parameter.Name == "isHost" ? (object?)true : null)).ToArray();
        return (CommandContext)constructor.Invoke(supplied)!;
    }

    internal static JsonElement[] Rows(CommandResult result) => result.Outputs.GetProperty("results").EnumerateArray().ToArray();

    internal static JsonElement Row(CommandResult result) => Rows(result).Single();

    public void Dispose()
    {
        _registration.Dispose();
        LG_ComputerTerminalManager.Reset();
        ZoneIndex.Reset();
        LG_LevelBuilder.Current = null;
        SNet.IsMaster = true;
    }
}
