using System.Reflection;
using InfiniTweaks;
using LevelGeneration;
using Player;
using SNetwork;
using static LevelGeneration.LG_ComputerTerminalManager;

int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
object? Hook(string name, params object[] args) => typeof(TerminalFixes).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
TerminalFixes.Clear(); Clock.Time = 0;
var manager = new LG_ComputerTerminalManager(); Current = manager; SNet.IsMaster = true;
var terminal = new LG_ComputerTerminal { SyncID = 1 }; manager.m_terminals.Add(1, terminal);
terminal.OnChange = () => Hook("StateChanged", terminal);
terminal.CurrentStateName = TERM_State.PlayerInteracting;
Check(!(bool)Hook("ValidateState", manager, new pTerminalState { ID = 1, state = (byte)TERM_State.Sleeping })!, "Occupied terminal cannot sleep.");
Check((bool)Hook("ValidateState", manager, new pTerminalState { ID = 1, state = (byte)TERM_State.Awake })!, "Other native transitions remain available.");
terminal.m_localInteractionSource = new PlayerAgent();
terminal.Input.m_lastSyncString = "old"; terminal.m_currentLine = "QUERY CELL";
Hook("EndInput", terminal);
Check(SentStrings.SequenceEqual(new[] { "QUERY CELL" }) && terminal.Input.m_lastSyncString == "QUERY CELL", "Exit prefix flushes unsent input before native cleanup.");
Hook("EndInput", terminal); Check(SentStrings.Count == 1, "Unchanged input is not sent twice.");
Hook("StateChanged", terminal); terminal.CurrentStateName = TERM_State.Sleeping;
Clock.Time = 0.1f; TerminalFixes.Tick();
Check(terminal.CurrentStateName == TERM_State.PlayerInteracting, "Grace window recovers a spurious sleep.");
// ChangeState synchronously registered a new watch. The old snapshot must not delete it.
Clock.Time = 1; terminal.m_localInteractionSource = null; TerminalFixes.Tick();
Check(terminal.CurrentStateName == TERM_State.Awake, "Reentrant registration survives snapshot removal and later recovers an abandoned terminal.");
terminal.m_command.OnEndOfQueue = new object();
Check(!(bool)Hook("ValidateCommand", manager, new pTerminalCommand { ID = 1, Sequence = 1 })!, "Busy native interpreter queues a command.");
terminal.m_command.OnEndOfQueue = null;
Check(!(bool)Hook("ValidateCommand", manager, new pTerminalCommand { ID = 1, Sequence = 2 })!, "New arrival cannot overtake an older queued command.");
manager.Validate = packet => (bool)Hook("ValidateCommand", manager, packet)!;
Hook("DispatchCommand", terminal.m_command); Hook("DispatchCommand", terminal.m_command);
Check(manager.Executed.SequenceEqual(new[] { 1, 2 }), "Queued commands reenter native validation in original order.");
terminal.m_command.OnEndOfQueue = new object();
for (int i = 0; i < 34; i++) Hook("ValidateCommand", manager, new pTerminalCommand { ID = 1, Sequence = i });
Check(Plugin.PluginLog.Warnings == 1, "Overflow is bounded and warns once per backlog.");
terminal.m_command.OnEndOfQueue = null; int before = manager.Executed.Count;
for (int i = 0; i < 40; i++) Hook("DispatchCommand", terminal.m_command);
Check(manager.Executed.Count - before == 32, "At most 32 commands are retained.");
Hook("ValidateCommand", manager, new pTerminalCommand { ID = 9 });
TerminalFixes.Clear(); Hook("DispatchCommand", terminal.m_command);
Check(manager.Executed.Count - before == 32, "Level cleanup removes queued commands.");
Console.WriteLine($"PASS: {checks} production terminal-hook checks with managed doubles; not a multiplayer/IL2CPP test.");

namespace InfiniTweaks
{
    internal static class Clock { internal static float Time; }
    internal static class Plugin { internal static Logger PluginLog = new(); }
    internal sealed class Logger { internal int Warnings; internal void LogWarning(string _) => Warnings++; }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class HarmonyPatch : Attribute { public HarmonyPatch() { } public HarmonyPatch(Type _, string __) { } }
    internal sealed class HarmonyPrefix : Attribute { }
    internal sealed class HarmonyPostfix : Attribute { }
}
namespace UnityEngine
{
    public struct Vector3 { public float x; public float sqrMagnitude => x * x; public static Vector3 operator -(Vector3 a, Vector3 b) => new() { x = a.x - b.x }; }
    public sealed class Transform { public Vector3 position; }
}
namespace SNetwork { public static class SNet { public static bool IsMaster; } }
namespace Player
{
    public sealed class PlayerLocomotion { public enum PLOC_State { None, OnTerminal } public PLOC_State m_currentStateEnum; }
    public sealed class PlayerAgent
    {
        public PlayerLocomotion Locomotion = new(); public bool IsLocallyOwned = true;
        public UnityEngine.Transform transform = new(); public PlayerSync Sync = new();
    }
    public sealed class PlayerSync { public LocomotionData m_locomotionData = new(); }
    public sealed class LocomotionData { public UnityEngine.Vector3 Pos; }
}
namespace LevelGeneration
{
    public enum TERM_State { Sleeping, Awake, PlayerInteracting }
    public class State { public T? TryCast<T>() where T : class => this as T; }
    public sealed class LG_TERM_PlayerInteracting : State
    {
        public LG_ComputerTerminal? m_terminal; public float m_inputTimer; public string m_lastSyncString = ""; public void Enter() { }
    }
    public sealed class LG_TERM_Ping { public LG_ComputerTerminal? m_terminal; public void Ping() { } }
    public sealed class LG_ComputerTerminal
    {
        public uint SyncID; public TERM_State CurrentStateName; public PlayerAgent? m_localInteractionSource, m_syncedInteractionSource;
        public string m_currentLine = ""; public LG_TERM_PlayerInteracting Input = new(); public LG_ComputerTerminalCommandInterpreter m_command;
        public Action? OnChange;
        public LG_ComputerTerminal() { m_command = new() { m_terminal = this }; Input.m_terminal = this; }
        public State GetState(int _) => Input;
        public void ChangeState(TERM_State state) { CurrentStateName = state; OnChange?.Invoke(); }
        public void SyncChangeState() { } public void EnterFPSView() { } public void ExitFPSView() { }
    }
    public sealed class LG_ComputerTerminalCommandInterpreter
    { public object? OnEndOfQueue; public LG_ComputerTerminal? m_terminal; public void UpdateTerminalScreen() { } }
    public sealed class LG_ComputerTerminalManager
    {
        public struct pTerminalState { public uint ID; public byte state; }
        public struct pTerminalCommand { public uint ID; public int Sequence; }
        public static LG_ComputerTerminalManager Current = null!; public static List<string> SentStrings = new();
        public Dictionary<uint, LG_ComputerTerminal> m_terminals = new(); public List<int> Executed = new();
        public Func<pTerminalCommand, bool>? Validate;
        public static void WantToSendTerminalString(uint _, string value) => SentStrings.Add(value);
        public void DoChangeTerminalStateValidation() { }
        public void DoTerminalCommandValidation(pTerminalCommand value) { if (Validate?.Invoke(value) != false) Executed.Add(value.Sequence); }
    }
}
