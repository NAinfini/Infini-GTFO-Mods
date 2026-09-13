namespace Enemies
{
    public sealed class EnemyAgent
    {
        public ushort GlobalID = 7;
        public IntPtr Pointer = new(10);
        public bool Alive = true, IsSetup = true;
        public Dam_EnemyDamageBase Damage = null!;
        public (float x, float y, float z) Point = (1, 2, 3);
        public Action? OnPosition;
        public (float x, float y, float z) Position { get { OnPosition?.Invoke(); return Point; } }
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
    public IntPtr Pointer = new(110);
    public bool IsSetup = true;
    public float Health = 50, HealthMax = 100;
    public int Sends;
    public void SendSetHealth(float value) { Sends++; Health = value; }
}
namespace SNetwork
{
    public static class SNet { public static bool IsMaster = true; }
    public struct SFloat16
    {
        private float value;
        public void Set(float input, float maximum) => value = input;
        public float Get(float maximum) => value;
    }
}
