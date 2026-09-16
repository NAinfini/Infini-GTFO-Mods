using System.Runtime.CompilerServices;

// Doubles mirror only the interop members the production terminal action layer reads. Names, namespaces and
// member kinds follow build 20403457 (dump.cs / interop); behaviour is synthetic and NOT game-verified. The
// door half of the level is present only because the one zone table is built for both categories: `LG_SecurityDoor`
// and the gate it hangs on exist here so the production table compiles, and no case in this project uses them.

public static class Pointers
{
    private static long _next = 0x20000;
    public static IntPtr Next() => new(Interlocked.Increment(ref _next));
}

/// <summary>Unity equality: a destroyed object compares equal to null, and the interop base every game wrapper
/// rests on reports whether it was collected.</summary>
public abstract class UnityObjectDouble
{
    public IntPtr Pointer { get; set; } = Pointers.Next();
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

namespace SNetwork
{
    /// <summary>The session's own master flag. The production terminal action layer reads it as one half of the
    /// host gate, exactly as the game's command entry does.</summary>
    public static class SNet { public static bool IsMaster { get; set; } = true; }
}

namespace UnityEngine
{
    public abstract class Component : UnityObjectDouble { }
    public abstract class Behaviour : Component { }
    public abstract class MonoBehaviour : Behaviour { }
}

/// <summary>Unity's Transform, read for a position exactly like the interop property whose value is the struct.</summary>
public sealed class TransformDouble : UnityObjectDouble
{
    public Vector3 position;
}

namespace AIGraph
{
    /// <summary>The course node a terminal was spawned on; its `m_zone` is the terminal's own statement of which
    /// zone it stands in.</summary>
    public sealed class AIG_CourseNode : UnityObjectDouble
    {
        public LevelGeneration.LG_Zone? m_zone;
    }
}

namespace GameData
{
    /// <summary>One specific terminal spawn of a zone's data block; the production address reader counts them.</summary>
    public sealed class SpecificTerminalSpawnData : UnityObjectDouble
    {
        public int InstanceIndex;
    }

    /// <summary>The zone's data block entry, with the one member the address reader consults.</summary>
    public sealed class ExpeditionZoneData : UnityObjectDouble
    {
        public List<SpecificTerminalSpawnData>? SpecificTerminalSpawnDatas = new();
    }
}

namespace LevelGeneration
{
    public enum LG_LayerType { MainLayer = 0, SecondaryLayer = 1, ThirdLayer = 2 }
    public enum eLocalZoneIndex { Zone_0 = 0, Zone_1 = 1, Zone_2 = 2, Zone_3 = 3, Zone_4 = 4, Zone_5 = 5 }
    public enum eDimensionIndex { Reality = 0, Dimension_1 = 1 }

    public enum TERM_State
    {
        Sleeping = 0, Awake = 1, PlayerInteracting = 2, DataMining = 3, Hacked = 4, CodePuzzle = 5,
        InputTest = 6, ReactorError = 7, AskToPlayLogAudio = 8, DoPlayAudioFile = 9, AudioLoopError = 10,
        Ping = 11, PasswordProtected = 12, EnterPassword = 13
    }

    public enum TERM_Command : byte
    {
        None = 0, Help = 1, Commands = 2, Cls = 3, Exit = 4, Open = 5, Close = 6, Activate = 7, Deactivate = 8,
        EmptyLine = 9, InvalidCommand = 10, DownloadData = 11, ViewSecurityLog = 12, Override = 13,
        DisableAlarm = 14, Locate = 15, ActivateBeacon = 16, Find = 17, ShowList = 18, Query = 19, Ping = 20,
        ReactorStartup = 21, ReactorVerify = 22, ReactorShutdown = 23, WardenObjectiveSpecialCommand = 24,
        TerminalUplinkConnect = 25, TerminalUplinkVerify = 26, TerminalUplinkConfirm = 27, ListLogs = 28,
        ReadLog = 29, Start = 30, TryUnlockingTerminal = 31, WardenObjectiveGatherCommand = 32,
        TerminalCorruptedUplinkConnect = 33, TerminalCorruptedUplinkVerify = 34, TimedConnectionSend = 35,
        TimedConnectionVerify = 36, UsedCommand = 37, UniqueCommand1 = 38, UniqueCommand2 = 39,
        UniqueCommand3 = 40, UniqueCommand4 = 41, UniqueCommand5 = 42, Info = 43, MAX_COUNT = 44
    }

    /// <summary>The terminal's own line kinds: what a printed line's severity maps onto.</summary>
    public enum TerminalLineType
    {
        Normal = 0, Fail = 1, SpinningWaitDone = 2, SpinningWaitNoDone = 3, ProgressWait = 4, Warning = 5
    }

    /// <summary>The locked door the zone table has a slot for. The door action layer is not part of this slice;
    /// the type exists so the one shared zone table compiles unchanged.</summary>
    public interface iLG_Door_Core
    {
        bool WasCollected { get; }
        object? Target { get; }
        T? TryCast<T>() where T : class => Target as T;
    }

    public sealed class LG_SecurityDoor : UnityEngine.MonoBehaviour, iLG_Door_Core
    {
        public object? Target => this;
    }

    /// <summary>The gate a zone is entered through; only the door slot is read by the zone table.</summary>
    public sealed class LG_Gate : UnityObjectDouble
    {
        public iLG_Door_Core? SpawnedDoor { get; set; }
    }

    public sealed class LG_Layer : UnityObjectDouble
    {
        public LG_LayerType m_type { get; set; }
    }

    public sealed class LG_ZoneSettings : UnityObjectDouble
    {
        public GameData.ExpeditionZoneData? m_zoneData { get; set; }
    }

    public sealed class LG_Zone : UnityObjectDouble
    {
        public LG_Layer? m_layer { get; set; } = new();
        public eDimensionIndex m_dimensionIndex { get; set; }
        public eLocalZoneIndex LocalIndex { get; set; }
        public LG_ZoneSettings? m_settings { get; set; } = new();
        public LG_Gate? m_sourceGate { get; set; }
        public List<LG_ComputerTerminal>? TerminalsSpawnedInZone { get; set; } = new();
    }

    public sealed class LG_Floor : UnityObjectDouble
    {
        public List<LG_Zone>? allZones { get; set; } = new();
    }

    /// <summary>The level singleton the floor is reached through. Its `Current` is settable, so a level that was
    /// torn down leaves it null exactly as the game's own scene teardown does.</summary>
    public sealed class LG_LevelBuilder : UnityObjectDouble
    {
        public static LG_LevelBuilder? Current { get; set; }
        public LG_Floor? m_currentFloor { get; set; } = new();
    }

    /// <summary>The terminal, with the members the action layer reaches: its own address keys (through the spawn
    /// node and the zone's list), its interpreter, and the one printed-line entry. A case reads the appended line
    /// rather than the state payload, which is the point.</summary>
    public sealed class LG_ComputerTerminal : UnityEngine.MonoBehaviour
    {
        public uint SyncID { get; set; }
        public TERM_State CurrentStateName { get; set; }
        public AIGraph.AIG_CourseNode? SpawnNode { get; set; }
        public bool m_hasInteractingPlayer;
        public TransformDouble? transform { get; set; } = new();
        public LG_ComputerTerminalCommandInterpreter? m_command { get; set; }
        public List<TerminalLine> Lines { get; } = new();
        public void AddLine(TerminalLineType terminalLineType, string line, float time = 0)
            => Lines.Add(new TerminalLine(terminalLineType, line, time));
    }

    /// <summary>One line a printed request appended: the terminal's own line kind, the text and the native
    /// member's own time argument, exactly as the real member receives them.</summary>
    public sealed record TerminalLine(TerminalLineType Type, string Text, float Time);

    /// <summary>The terminal's own command interpreter. Only the members the command action reaches are mirrored:
    /// the parse the real interpreter performs is replaced by a table a case fills in, because the game's parse is
    /// not this fixture's subject — what a case proves is that the production action asks the terminal's own
    /// parser and sends what it answered.</summary>
    public sealed class LG_ComputerTerminalCommandInterpreter : UnityObjectDouble
    {
        public Dictionary<string, TERM_Command> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ParseCalls;
        public string? LastInput;
        public bool TryGetCommand(string inputString, out TERM_Command command, out string param1, out string param2)
        {
            ParseCalls++;
            LastInput = inputString;
            command = TERM_Command.None;
            param1 = ""; param2 = "";
            var parts = (inputString ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !Commands.TryGetValue(parts[0], out command)) return false;
            param1 = parts.Length > 1 ? parts[1] : "";
            param2 = parts.Length > 2 ? parts[2] : "";
            return true;
        }
    }

    /// <summary>The manager whose static entries the action layer reaches. `Current` can be stood up or taken
    /// away, because the command action refuses a request when no manager is alive.</summary>
    public sealed class LG_ComputerTerminalManager : UnityObjectDouble
    {
        public static LG_ComputerTerminalManager? Current { get; set; }
        public static readonly List<SentCommand> Sent = new();
        public static void WantToSendTerminalCommand(uint terminalID, TERM_Command command, string inputString,
            string param1, string param2)
            => Sent.Add(new SentCommand(terminalID, command, inputString, param1, param2));
        public static void Reset() { Current = null; Sent.Clear(); }
    }

    /// <summary>One command the manager's own entry was asked to send: the terminal's sync id, the parsed
    /// command and the three strings the entry carries.</summary>
    public sealed record SentCommand(uint TerminalId, TERM_Command Command, string Input, string Param1, string Param2);
}
