using System.Runtime.CompilerServices;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;

// The alarm/wave/scan doubles: they mirror only the interop members the two slice sources read. Names,
// namespaces and member kinds follow build 20403457 (dump.cs / interop); behaviour is synthetic and NOT
// game-verified. Every counter on a double exists so a test can tell "the native entry ran" from "nothing was
// written", which is the only claim the result rows are allowed to make.

namespace SNetwork
{
    /// <summary>The game's own master flag; the native action layer reads it beside the ABI's host fact.</summary>
    public static class SNet { public static bool IsMaster { get; set; } = true; }

    /// <summary>Stands in for the replication interface the Mastermind's spawn entry declares.</summary>
    public interface IReplicator { }
}

/// <summary>Unity object identity: a destroyed object compares equal to null and reports itself collected.</summary>
public abstract class UnityObjectDouble
{
    private static long _next = 0x20000;
    public IntPtr Pointer { get; set; } = new(Interlocked.Increment(ref _next));
    public bool Destroyed;
    public bool WasCollected => Destroyed;
    private static bool Alive(UnityObjectDouble? value) => value is not null && !value.Destroyed;
    public static bool operator ==(UnityObjectDouble? left, UnityObjectDouble? right)
    {
        bool l = Alive(left), r = Alive(right);
        return !l || !r ? l == r : ReferenceEquals(left, right);
    }
    public static bool operator !=(UnityObjectDouble? left, UnityObjectDouble? right) => !(left == right);
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

public struct Vector3
{
    public float x, y, z;
    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
}

namespace AIGraph
{
    /// <summary>One course node. The production start path takes the level's first node, so the double keeps the
    /// game's own static list and lets a test empty it.</summary>
    public sealed class AIG_CourseNode : UnityObjectDouble
    {
        public static List<AIG_CourseNode>? s_allNodes { get; set; } = new();
    }
}

namespace GameData
{
    /// <summary>The alarm/scan author block. Only the members the action layer reads are declared: the alarm name
    /// a chained-puzzle resource may be spelled with, and the alarm flag the game uses to tell an alarm from a
    /// scan.</summary>
    public class ChainedPuzzleDataBlock : UnityObjectDouble
    {
        public string? PublicAlarmName { get; set; }
        public bool TriggerAlarmOnActivate { get; set; }
    }

    /// <summary>The data block base the two wave blocks share. A generic double keeps one static table per
    /// closed type, exactly like the game's own generic base, so a settings block can never be answered as a
    /// population block. It derives from the interop object base because the real one does: a block that was
    /// collected has to read as gone.</summary>
    public class GameDataBlockBase<T> : UnityObjectDouble where T : GameDataBlockBase<T>
    {
        private static readonly Dictionary<uint, T> Blocks = new();
        public uint persistentID { get; set; }
        public static void Reset() => Blocks.Clear();
        public static void Register(T block) => Blocks[block.persistentID] = block;
        public static T? GetBlock(uint id) => Blocks.TryGetValue(id, out var block) ? block : null;
        public static IReadOnlyList<T> GetAllBlocks() => Blocks.Values.ToList();
    }

    public sealed class SurvivalWaveSettingsDataBlock : GameDataBlockBase<SurvivalWaveSettingsDataBlock> { }
    public sealed class SurvivalWavePopulationDataBlock : GameDataBlockBase<SurvivalWavePopulationDataBlock> { }
}

namespace ChainedPuzzles
{
    /// <summary>The three interactions the game's own entry takes.</summary>
    public enum eChainedPuzzleInteraction : byte { Activate, Solve, Deactivate }

    public enum eChainedPuzzleStatus : byte { Disabled, Active, Solved }

    /// <summary>One chained puzzle instance: the alarm and the scan are the same native kind, so one double
    /// serves both. `Interactions` records every call to the native entry, which is what proves the action
    /// submitted instead of only reporting that it had.</summary>
    public sealed class ChainedPuzzleInstance : UnityObjectDouble
    {
        public GameData.ChainedPuzzleDataBlock? data;
        public string? m_puzzleUID;
        public bool active;
        public bool solved;
        public int cores = 1;
        public readonly List<eChainedPuzzleInteraction> Interactions = new();
        public GameData.ChainedPuzzleDataBlock? Data => data;
        public string m_puzzleUIDValue => m_puzzleUID ?? "";
        public bool IsActive => active;
        public bool IsSolved => solved;
        public int NRofPuzzles() => cores;
        public void AttemptInteract(eChainedPuzzleInteraction interaction)
        {
            Interactions.Add(interaction);
            if (interaction == eChainedPuzzleInteraction.Activate) active = true;
            if (interaction == eChainedPuzzleInteraction.Deactivate) active = false;
        }
    }

    /// <summary>The level's own puzzle list. The game adds a puzzle to it while building the level, so an empty
    /// list is the state a level with no alarm and no scan is in.</summary>
    public sealed class ChainedPuzzleManager : UnityObjectDouble
    {
        public static ChainedPuzzleManager? Current { get; set; }
        public List<ChainedPuzzleInstance>? m_instances { get; set; } = new();
    }
}

/// <summary>The wave system's own manager and event base, with the identity field the start entry fills in. The
/// manager derives from the interop object base because the game's own does (`Mastermind : GlobalManager`). A
/// started wave is registered in the manager's own event list, which is what `TryGetEvent` reads back: the stop
/// side of the handle path resolves the event through it rather than searching for a wave by name.</summary>
public class Mastermind : UnityObjectDouble
{
    public class MastermindEvent : UnityObjectDouble
    {
        public ushort EventID { get; set; }
        public int Stops;
        public virtual void StopEvent() => Stops++;
    }

    /// <summary>One survival wave: the game's own event type for it, with the stop entry the handle path
    /// calls.</summary>
    public sealed class SurvivalWave : MastermindEvent { }

    public static Mastermind? Current { get; set; }
    /// <summary>What the native entry does: answers whether it accepted the wave, hands back the event id it
    /// registered, and records the arguments so a test can assert the two data block ids really travelled.</summary>
    public bool Accepts = true;
    public readonly List<(uint Settings, uint Population, string Node)> Triggers = new();
    private readonly Dictionary<ushort, MastermindEvent> _events = new();

    /// <summary>The events this mastermind holds, which is the game's own `TryGetEvent` table.</summary>
    public IReadOnlyDictionary<ushort, MastermindEvent> Events => _events;

    public bool TriggerSurvivalWave(AIGraph.AIG_CourseNode? refNode, uint settingsID, uint populationDataID, out ushort eventID)
    {
        eventID = 0;
        if (!Accepts) return false;
        eventID = (ushort)(Triggers.Count + 1);
        Triggers.Add((settingsID, populationDataID, refNode?.Pointer.ToString() ?? ""));
        _events[eventID] = new SurvivalWave { EventID = eventID };
        return true;
    }

    /// <summary>The game's own lookup: an event id this mastermind does not hold answers false and hands back
    /// null, which is what makes a registration the master accepted but does not hold unnameable.</summary>
    public bool TryGetEvent(ushort eventId, out MastermindEvent? masterMindEvent)
        => _events.TryGetValue(eventId, out masterMindEvent);
}

/// <summary>The fixture's resettable world: every static the two slice sources read is cleared here, so one test
/// can never inherit another's puzzle list, wave table or master flag.</summary>
internal static class Fixture
{
    private static RuntimeKernel? _kernel;
    private static RuntimeModuleHandle? _registration;

    /// <summary>The kernel of the registration the handle cases mint through.</summary>
    internal static RuntimeKernel Kernel => _kernel ?? throw new InvalidOperationException("Register() first.");

    internal static void Reset()
    {
        SNetwork.SNet.IsMaster = true;
        ChainedPuzzles.ChainedPuzzleManager.Current = null;
        Mastermind.Current = null;
        AIGraph.AIG_CourseNode.s_allNodes = new List<AIGraph.AIG_CourseNode>();
        GameData.GameDataBlockBase<GameData.SurvivalWaveSettingsDataBlock>.Reset();
        GameData.GameDataBlockBase<GameData.SurvivalWavePopulationDataBlock>.Reset();
    }

    /// <summary>One live registration of this slice's own three rows, and the native half attached to it. A handle
    /// is a pool slot of a registered provider, so the start rows can only publish one on a world that has this;
    /// the rows and the handler table are the contract's own, which is what makes the rows under test the rows
    /// the runtime would resolve.</summary>
    internal static AlarmWaveActions Attach()
    {
        _registration?.Dispose();
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.test.alarmwave", "0.1.0", RuntimeKernel.ApiVersion, "20403457"));
        kernel.BeginWorld(1);
        var capabilities = JsonDocument.Parse(AlarmWaveContract.CapabilitiesJson).RootElement;
        var registry = RuntimeJson.From(new
        {
            providers = new[] { new { id = AlarmWaveContract.ProviderId, kind = "native", version = "0.1.0", dependencies = Array.Empty<string>() } },
            capabilities,
            bindings = AlarmWaveContract.Bindings()
        });
        var module = new RuntimeModule(RuntimeKernel.ApiVersion, registry.GetRawText(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
            {
                [AlarmWaveContract.ScanStartHandler] = AlarmWaveActions.ExecuteStartScan,
                [AlarmWaveContract.WaveStartHandler] = AlarmWaveActions.ExecuteStartWave,
                [AlarmWaveContract.WaveStopHandler] = AlarmWaveActions.ExecuteStopWave
            },
            AlarmWaveContract.Support())
        { Shapes = AlarmWaveContract.Shapes() };
        _kernel = kernel;
        _registration = kernel.RegisterModule(module, RuntimeLogLevel.Off);
        return AlarmWaveActions.Attach(kernel, _registration);
    }

    /// <summary>Detaches the half the way a session's teardown does, so a case can assert the detached refusal.
    /// The registration is released with it: a handle cannot outlive the provider that minted it.</summary>
    internal static void Detach()
    {
        AlarmWaveActions.Detach(AlarmWaveActions.Attach(Kernel, _registration!));
        _registration?.Dispose();
    }

    /// <summary>One level with a puzzle manager, one course node and a master, which is the minimum a wave start
    /// needs before any wave-specific fixture is added. The slice's own registration is attached beside it: the
    /// two start rows mint their handles through it, so a case that expects a start to write the world is a case
    /// with a world to write in.</summary>
    internal static ChainedPuzzles.ChainedPuzzleManager Level()
    {
        Reset();
        var manager = new ChainedPuzzles.ChainedPuzzleManager();
        ChainedPuzzles.ChainedPuzzleManager.Current = manager;
        AIGraph.AIG_CourseNode.s_allNodes!.Add(new AIGraph.AIG_CourseNode());
        Mastermind.Current = new Mastermind();
        Attach();
        return manager;
    }

    internal static ChainedPuzzles.ChainedPuzzleInstance Puzzle(string uid, string alarmName = "",
        bool active = false, bool solved = false, int cores = 1)
        => new()
        {
            m_puzzleUID = uid,
            data = new GameData.ChainedPuzzleDataBlock { PublicAlarmName = alarmName },
            active = active,
            solved = solved,
            cores = cores
        };

    internal static (uint Settings, uint Population) WavePair(uint settingsId = 7, uint populationId = 9)
    {
        GameData.GameDataBlockBase<GameData.SurvivalWaveSettingsDataBlock>.Register(
            new GameData.SurvivalWaveSettingsDataBlock { persistentID = settingsId });
        GameData.GameDataBlockBase<GameData.SurvivalWavePopulationDataBlock>.Register(
            new GameData.SurvivalWavePopulationDataBlock { persistentID = populationId });
        return (settingsId, populationId);
    }

    /// <summary>Runs one handler the way the kernel does: the command context's own constructor is internal to
    /// the SDK, so it is reached through its non-public signature.</summary>
    internal static CommandResult Run(CommandHandler handler, object? inputs, object? parameters = null, bool isHost = true)
    {
        var origin = new RuntimeEvent("test.alarm.wave", "forge.module.gtfo.map.binding.test", 1, 0,
            "test.alarm.scope", RuntimeJson.EmptyObject);
        var context = (CommandContext)Activator.CreateInstance(typeof(CommandContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object?[]
            {
                origin, 0L, "test.alarm.command", "test.alarm.plan", "test.alarm.resource", "1", "test.alarm.node",
                RuntimeJson.From(parameters ?? new { }), RuntimeJson.From(inputs ?? new { }), isHost
            }, null)!;
        return handler(context);
    }

    internal static JsonElement Resource(string kind, string id) => RuntimeJson.From(new { resourceKind = kind, resourceId = id });

    /// <summary>The handle one start row published on its own port, as a detached value a later command can carry
    /// as an input. A row that published none answers null, which is the case a stop test asserts instead of
    /// passing an empty object that would be refused as a malformed handle.</summary>
    internal static JsonElement? Handle(CommandResult result, string port)
        => result.Outputs.ValueKind == JsonValueKind.Object && result.Outputs.TryGetProperty(port, out var value)
            && value.ValueKind == JsonValueKind.Object ? value.Clone() : null;

    internal static JsonElement Row(CommandResult result)
        => result.Outputs.GetProperty("results").EnumerateArray().Single();

    internal static string Field(CommandResult result, string name) => Row(result).GetProperty(name).GetString() ?? "";
}
