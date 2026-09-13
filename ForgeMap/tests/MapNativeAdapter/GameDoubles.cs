using System.Runtime.CompilerServices;

// Doubles mirror only the interop members the production Map native sources read. Names, namespaces and
// member kinds follow build 20403457 (dump.cs / interop); behaviour is synthetic and NOT game-verified.
namespace ForgeRuntime
{
    public enum RuntimeMode { Off, Play, Authoring }
    public static class Plugin
    {
        public static RuntimeMode ConfiguredMode = RuntimeMode.Play;
        public static ForgeRuntime.Framework.RuntimeKernel? Runtime;
    }
}

public static class Pointers
{
    private static long _next = 0x10000;
    public static IntPtr Next() => new(Interlocked.Increment(ref _next));
}

/// <summary>Unity equality: a destroyed object compares equal to null.</summary>
public abstract class UnityObjectDouble
{
    public IntPtr Pointer { get; set; } = Pointers.Next();
    public bool Destroyed;
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

namespace SNetwork
{
    public static class SNet { public static bool IsMaster { get; set; } = true; }
    public sealed class SNet_IPlayerAgent
    {
        public object? Target;
        public T? TryCast<T>() where T : class => Target as T;
    }
    public sealed class SNet_Player : UnityObjectDouble
    {
        public ulong Lookup;
        public bool IsBot;
        public SNet_IPlayerAgent? PlayerAgent;
    }
}

namespace Player
{
    public sealed class PlayerAgent : UnityObjectDouble
    {
        public SNetwork.SNet_Player? Owner;
    }
    public sealed class PlayerManager
    {
        private static readonly List<PlayerAgent> Agents = new();
        public static bool ThrowOnRead;
        public static List<PlayerAgent> PlayerAgentsInLevel
            => ThrowOnRead ? throw new InvalidOperationException("fixture native read failure") : Agents;
        public void OnPlayerSpawned() { }
        public void OnPlayerDespawned() { }
        public static void Reset() { Agents.Clear(); ThrowOnRead = false; }
    }
}
