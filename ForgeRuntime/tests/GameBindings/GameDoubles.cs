using System;

namespace ForgeRuntime
{
    internal enum RuntimeMode { Off, Authoring, Play }
    internal static class Plugin { internal static readonly BepInEx.Logging.ManualLogSource PluginLog = new(); }
}
namespace BepInEx { public static class Paths { public static string GameRootPath = ""; public static string BepInExRootPath = ""; } }
namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public readonly System.Collections.Generic.List<string> Messages = new();
        public void LogInfo(object value) { lock (Messages) Messages.Add("info:" + value); }
        public void LogWarning(object value) { lock (Messages) Messages.Add("warning:" + value); }
        public void LogError(object value) { lock (Messages) Messages.Add("error:" + value); }
    }
}
namespace UnityEngine { public class MonoBehaviour { public MonoBehaviour(IntPtr pointer) { } } }
public enum eGameStateName { Inactive, Startup, Offline, FakeLobby, NoLobby, Lobby, Generating, ReadyToStopElevatorRide, StopElevatorRide, ReadyToStartLevel, InLevel, AfterLevel, Slim, CaptureRecall, ExpeditionSuccess, ExpeditionFail, ExpeditionAbort }
public static class GameStateManager { public static eGameStateName CurrentStateName = eGameStateName.Lobby; }

namespace SNetwork
{
    public static class SNet { public static bool IsMaster = true; public static MasterState MasterManagement = new(); }
    public sealed class MasterState { public bool IsMigrating; }
    public struct SFloat16
    {
        public static Func<float, float, float> Preview = (value, maximum) => value;
        private float value;
        public void Set(float v, float maximum) => value = Preview(v, maximum);
        public float Get(float maximum) => value;
    }
}
namespace Enemies
{
    public sealed class EnemyAgent
    {
        public ushort GlobalID;
        public IntPtr Pointer;
        public bool Alive = true;
        public Dam_EnemyDamageBase Damage = null!;
    }
}
public sealed class Dam_EnemyDamageLimb
{
    public IntPtr Pointer = new IntPtr(210);
    public Dam_EnemyDamageBase m_base = null!;
    public int m_limbID;
    public bool Destroyed;
    public Action? OnDestroyedRead;
    public bool IsDestroyed { get { OnDestroyedRead?.Invoke(); return Destroyed; } set { Destroyed = value; } }
    public void DestroyLimb() { IsDestroyed = true; }
}
public sealed class Dam_EnemyDamageBase
{
    public Dam_EnemyDamageLimb[] DamageLimbs = System.Array.Empty<Dam_EnemyDamageLimb>();
    public Enemies.EnemyAgent Owner = null!;
    public IntPtr Pointer;
    public bool IsSetup = true;
    public float Health = 50, HealthMax = 100;
    public int Sends;
    public Action<float>? Commit;
    public void SendSetHealth(float health) { Sends++; if (Commit == null) Health = health; else Commit(health); }
}
